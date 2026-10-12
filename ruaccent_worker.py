#!/usr/bin/env python3
"Persistent JSON-lines bridge to RUAccent; model loading happens only once."""
import argparse
import importlib.util
import json
import os
import pathlib
import re
import warnings
import contextlib
import io
# Keep third-party initialization warnings and native-library notices out of
# stdout: stdout is the machine-readable JSON/JSON-lines protocol for DSR.
import sys
import traceback
import time
# Allow an operator to tune CPU inference; cap defaults to avoid oversubscribing
# small machines. CUDA kernels remain managed by ONNX Runtime.
_CPU_THREADS = max(1, int(os.environ.get("DSR_CPU_THREADS", os.cpu_count() or 1)))
# Set before importing ONNX Runtime; provider/session options are fixed during load.
os.environ["OMP_NUM_THREADS"] = str(_CPU_THREADS)
os.environ["OMP_WAIT_POLICY"] = "ACTIVE"
os.environ["OMP_DYNAMIC"] = "FALSE"
os.environ["OMP_PROC_BIND"] = "TRUE"
os.environ["MKL_NUM_THREADS"] = str(_CPU_THREADS)
os.environ["OPENBLAS_NUM_THREADS"] = str(_CPU_THREADS)
# RUAccent's ONNX inference needs no optional torch import during initialization.
_DLL_DIRECTORIES = []
WORD_RE = re.compile(r"[\w\u0400-\u052f+\u0300-\u036f]+(?:[-'’][\w\u0400-\u052f+\u0300-\u036f]+)*", re.UNICODE)
ACCENT_RE = re.compile(r"[+\u0300-\u036f]")


def gpu_probe() -> dict:
    try:
        import subprocess
        try:
            result = subprocess.run(
                ["nvidia-smi", "--query-gpu=name", "--format=csv,noheader"],
                capture_output=True, text=True, timeout=10, check=True,
            )
            names = [line.strip() for line in result.stdout.splitlines() if line.strip()]
        except (OSError, subprocess.SubprocessError):
            names = []
        if not names:
            try:
                import torch
                if torch.cuda.is_available():
                    names = [torch.cuda.get_device_name(index) for index in range(torch.cuda.device_count())]
            except Exception:
                pass
        nvidia_gpus = names
        if not nvidia_gpus:
            return {"supported": False, "message": "Видеокарта NVIDIA не обнаружена (nvidia-smi недоступен)."}
        # Import RUAccent only after confirming an NVIDIA GPU is available.
        # CUDA support is validated against ONNX Runtime and the actual RUAccent models below.
        # Register NVIDIA package DLL directories before importing ONNX Runtime.
        try:
            import nvidia
            _DLL_DIRECTORIES.extend(os.add_dll_directory(str(folder)) for folder in pathlib.Path(nvidia.__path__[0]).glob("*/bin"))
        except (ImportError, AttributeError):
            pass
        # Current CUDA/cuDNN DLLs are installed into the DSR Python environment
        # by onnxruntime-gpu[cuda,cudnn]; add their directories before ORT starts.
        site_packages = pathlib.Path(sys.prefix) / "Lib" / "site-packages" / "nvidia"
        if site_packages.is_dir():
            _DLL_DIRECTORIES.extend(os.add_dll_directory(str(folder)) for folder in site_packages.glob("*/bin"))
        # ONNX Runtime preload emits CUDA compatibility warnings on stdout in some
        # builds; suppress those while probing so stdout stays valid JSON only.
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()), warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import onnxruntime as ort
            if hasattr(ort, "preload_dlls"):
                ort.preload_dlls()
        if "CUDAExecutionProvider" not in ort.get_available_providers():
            return {
                "supported": False,
                "can_install_gpu_runtime": True,
                "message": f"Обнаружена {nvidia_gpus[0]}, но CUDAExecutionProvider ещё не установлен.",
            }
        ruaccent_spec = importlib.util.find_spec("ruaccent")
        if ruaccent_spec is None or ruaccent_spec.origin is None:
            return {"supported": False, "message": "Пакет RUAccent не найден для CUDA-проверки."}
        accent_model_dir = pathlib.Path(ruaccent_spec.origin).resolve().parent / "nn" / "nn_accent"
        accent_model = accent_model_dir / "model.onnx"
        if not accent_model.is_file():
            return {"supported": False, "message": "CUDA provider найден, но файл модели RUAccent ещё не загружен для теста."}
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            session = ort.InferenceSession(str(accent_model), providers=["CUDAExecutionProvider", "CPUExecutionProvider"])
            verify_cuda_inference("accent_model", session)
        providers = session.get_providers()
        if not providers or providers[0] != "CUDAExecutionProvider":
            return {"supported": False, "message": f"CUDA provider не удалось активировать для RUAccent (активны: {', '.join(providers)}). Проверьте CUDA/cuDNN runtime."}
        return {"supported": True, "message": f"Обнаружена {nvidia_gpus[0]}; CUDA provider выполнил реальный инференс RUAccent."}
    except Exception as exc:
        return {
            "supported": False,
            "can_install_gpu_runtime": True,
            "message": f"Проверка CUDA завершилась ошибкой: {exc}",
        }


def verify_cuda_inference(model_name: str, session) -> None:
    import numpy as np
    type_map = {
        "tensor(int64)": np.int64,
        "tensor(int32)": np.int32,
        "tensor(float)": np.float32,
        "tensor(double)": np.float64,
        "tensor(bool)": np.bool_,
    }
    inputs = {}
    for item in session.get_inputs():
        dtype = type_map.get(item.type)
        if dtype is None:
            raise RuntimeError(f"{model_name}: неподдерживаемый тип входа ONNX {item.type}.")
        shape = tuple(dim if isinstance(dim, int) and dim > 0 else 1 for dim in item.shape)
        inputs[item.name] = np.zeros(shape, dtype=dtype)
    session.run(None, inputs)


def report_model_progress(model_name: str, file_name: str, downloaded: int, total: int) -> None:
    percent = int(downloaded * 100 / total) if total > 0 else 0
    size = lambda value: f"{value / 1024 / 1024:.1f} МБ"
    print(f"DSR_PROGRESS:Загрузка модели {model_name}: {file_name} — {size(downloaded)} / {size(total)} ({percent}%)",
          file=sys.stderr, flush=True)


def install_download_progress(model_name: str) -> None:
    from tqdm.auto import tqdm as BaseTqdm
    import ruaccent.ruaccent as ruaccent_module
    original_download = ruaccent_module.hf_hub_download
    def download_with_progress(*args, **kwargs):
        filename = kwargs.get("filename")
        if filename is None and len(args) > 1:
            filename = args[1]
        filename = str(filename or "файл модели")
        prefix = "nn/nn_omograph/"
        if prefix in filename:
            download_name = filename.split(prefix, 1)[1].split("/", 1)[0]
        else:
            download_name = "общие модели RUAccent"

        class DownloadProgress(BaseTqdm):
            def __init__(self, *bar_args, **bar_kwargs):
                self._last_report = 0.0
                super().__init__(*bar_args, **bar_kwargs)
                self._report(force=True)

            def _report(self, force: bool = False) -> None:
                now = time.monotonic()
                if force or now - self._last_report >= 0.5 or (self.total and self.n >= self.total):
                    report_model_progress(download_name, str(self.desc or filename), int(self.n), int(self.total or 0))
                    self._last_report = now
            def update(self, n=1):
                result = super().update(n)
                self._report()
                return result
        kwargs.setdefault("tqdm_class", DownloadProgress)
        return original_download(*args, **kwargs)

    ruaccent_module.hf_hub_download = download_with_progress


def configure_onnx_performance(accentizer) -> None:
    # Report loaded ORT sessions and their active execution providers.
    for model_name in ("omograph_model", "accent_model", "stress_usage_predictor", "yo_homograph_model"):
        model = getattr(accentizer, model_name, None)
        session = getattr(model, "session", None)
        if session is None:
            continue
        # Report actual providers and avoid silently changing session behavior;
        # thread pools are set via environment before ORT import.
        print(f"DSR_PERF:{model_name}:providers={','.join(session.get_providers())}:cpu_threads={_CPU_THREADS}", file=sys.stderr, flush=True)


def configure_onnx_tokenizer_inputs(accentizer) -> None:
    # Match the tokenizer's outputs to the actual required ONNX model inputs.
    # In particular CharTokenizer otherwise omits token_type_ids on newer exports.
    for_model_inputs = True
    if not for_model_inputs:
        return
    for model_name in ("omograph_model", "accent_model", "stress_usage_predictor", "yo_homograph_model"):
        model = getattr(accentizer, model_name, None)
        session = getattr(model, "session", None)
        tokenizer = getattr(model, "tokenizer", None)
        if session is None or tokenizer is None:
            continue
        tokenizer.model_input_names = [item.name for item in session.get_inputs()]

def words(text: str) -> list[str]:
    return [ACCENT_RE.sub("", match.group()).casefold().replace("ё", "е") for match in WORD_RE.finditer(text)]


def safe_process(accentizer, text: str) -> str:
    # Use RUAccent's contextual paragraph inference once (faster and better at
    # disambiguating homographs). Only recover word-by-word if token alignment fails.
    result = accentizer.process_all(text)
    if words(result) == words(text) and dialogue_initial_words_preserved(text, result):
        return result
    print("WARNING: RUAccent altered a dialogue prefix; retrying line by line.", file=sys.stderr, flush=True)
    pieces = re.split(r"(\r?\n)", text)
    recovered_lines = []
    for piece in pieces:
        if piece in ("\n", "\r\n"):
            recovered_lines.append(piece)
        else:
            recovered_lines.append(process_line(accentizer, piece))
    recovered_text = "".join(recovered_lines)
    if words(recovered_text) != words(text):
        raise RuntimeError("RUAccent не сохранил исходную последовательность слов; результат отклонён.")
    return recovered_text

def dialogue_initial_words_preserved(source: str, result: str) -> bool:
    source_lines = source.splitlines()
    result_lines = result.splitlines()
    if len(source_lines) != len(result_lines):
        return False
    for source_line, result_line in zip(source_lines, result_lines):
        match = re.match(r"^(\s*[—–―]\s*)([\w\u0400-\u052f]+)", source_line, re.UNICODE)
        if match:
            output_match = re.match(r"^(\s*[—–―]\s*)([\w\u0400-\u052f]+)", result_line, re.UNICODE)
            if not output_match or output_match.group(2).casefold() != match.group(2).casefold():
                return False
    return True

def accent_words_in_piece(accentizer, piece: str) -> str:
    # Accent every word of the fragment on its own. A word RUAccent cannot
    # reproduce is kept exactly as written, so the source text is never damaged.
    # RUAccent drops underscores and can rewrite non-Cyrillic tokens, which is
    # normal for FB2 metadata such as <genre>foreign_fantasy</genre>.
    parts = []
    last = 0
    for match in WORD_RE.finditer(piece):
        word = match.group()
        try:
            candidate = accentizer.process_all(word)
        except Exception:
            candidate = word
        if words(candidate) != words(word):
            candidate = word
        parts.append(piece[last:match.start()])
        parts.append(candidate)
        last = match.end()
    parts.append(piece[last:])
    return "".join(parts)

def process_line(accentizer, text: str) -> str:
    result = accentizer.process_all(text)
    if words(result) == words(text) and dialogue_initial_words_preserved(text, result):
        return result
    pieces = re.split(r"(\s+)", text)
    recovered = []
    for piece in pieces:
        if not piece or piece.isspace() or not WORD_RE.search(piece):
            recovered.append(piece)
            continue
        try:
            candidate = accentizer.process_all(piece)
            if words(candidate) == words(piece):
                recovered.append(candidate)
                continue
        except Exception:
            pass
        # If the surrounding context breaks RUAccent (a leading em dash is the
        # frequent case), accent the words of the fragment one by one. Words the
        # model cannot preserve stay unaccented instead of failing the file.
        recovered.append(accent_words_in_piece(accentizer, piece))
    recovered_text = "".join(recovered)
    if words(recovered_text) != words(text):
        raise RuntimeError("RUAccent не сохранил исходную последовательность слов; результат отклонён.")
    return recovered_text


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="turbo3.1")
    parser.add_argument("--device", choices=("CPU", "CUDA"), default="CPU")
    parser.add_argument("--probe-gpu", action="store_true")
    dictionary_group = parser.add_mutually_exclusive_group()
    dictionary_group.add_argument("--dictionary", dest="dictionary", action="store_true")
    dictionary_group.add_argument("--no-dictionary", dest="dictionary", action="store_false")
    parser.set_defaults(dictionary=True)
    args = parser.parse_args()

    if args.probe_gpu:
        # Third-party imports (notably Transformers/ONNX Runtime) can print warnings.
        # Keep the probe contract strict: exactly one UTF-8 JSON object on stdout.
        with contextlib.redirect_stdout(io.StringIO()):
            result = gpu_probe()
        sys.stdout.buffer.write((json.dumps(result, ensure_ascii=False) + "\n").encode("utf-8"))
        sys.stdout.buffer.flush()
        return 0
    if args.device == "CUDA":
        probe = gpu_probe()
        if not probe["supported"]:
            print("ERROR: " + probe["message"], flush=True)
            return 1
        print("GPU: " + probe["message"], file=sys.stderr, flush=True)

    try:
        from ruaccent import RUAccent
        model_path = pathlib.Path(importlib.util.find_spec("ruaccent").origin).resolve().parent / "nn" / "nn_omograph" / args.model
        model_bytes = sum(item.stat().st_size for item in model_path.rglob("*") if item.is_file()) if model_path.is_dir() else 0
        print(f"DSR_PROGRESS:Подготовка модели {args.model}; уже в кэше: {model_bytes / 1024 / 1024:.1f} МБ.", file=sys.stderr, flush=True)
        install_download_progress(args.model)
        # RUAccent's rule engine has an unavailable CRF asset in this release.
        # Its neural ONNX models still provide accents, омographs and ё handling.
        accentizer = RUAccent()
        accentizer.load(
            omograph_model_size=args.model,
            use_dictionary=args.dictionary,
            device=args.device,
            tiny_mode=True,
        )
        configure_onnx_tokenizer_inputs(accentizer)
        configure_onnx_performance(accentizer)
        if args.device == "CUDA":
            model_names = ("omograph_model", "accent_model", "stress_usage_predictor", "yo_homograph_model")
            active = []
            for model_name in model_names:
                model = getattr(accentizer, model_name, None)
                session = getattr(model, "session", None)
                providers = session.get_providers() if session is not None else []
                active.append((model_name, providers))
                if session is not None and providers and providers[0] == "CUDAExecutionProvider":
                    verify_cuda_inference(model_name, session)
            # Only check models that actually have an ONNX session.
            # In tiny_mode=True the stress_usage_predictor is intentionally skipped.
            loaded = [(name, prov) for name, prov in active if prov]
            not_using_cuda = [f"{name}: {prov}" for name, prov in loaded if prov[0] != "CUDAExecutionProvider"]
            if not loaded or not_using_cuda:
                details = not_using_cuda or ["нет загруженных ONNX-сессий"]
                raise RuntimeError("CUDA выбрана, но не все ONNX-модели RUAccent работают через CUDAExecutionProvider: " + "; ".join(details))
            print("Проверено: все ONNX-модели RUAccent используют CUDAExecutionProvider.", file=sys.stderr, flush=True)
        model_bytes = sum(item.stat().st_size for item in model_path.rglob("*") if item.is_file()) if model_path.is_dir() else 0
        print(f"DSR_PROGRESS:Модель {args.model} загружена; размер файлов модели: {model_bytes / 1024 / 1024:.1f} МБ ({args.device}).", file=sys.stderr, flush=True)
        print("READY", flush=True)
    except Exception as exc:
        print("ERROR: " + str(exc), flush=True)
        traceback.print_exc(file=sys.stderr)
        return 1
    for line in sys.stdin:
        try:
            text = json.loads(line)
            if not isinstance(text, str):
                raise ValueError("Ожидалась JSON-строка.")
            inference_started = time.perf_counter()
            result = safe_process(accentizer, text)
            inference_seconds = time.perf_counter() - inference_started
            print(f"DSR_INFERENCE:{inference_seconds:.6f}", file=sys.stderr, flush=True)
            print(json.dumps({"text": result}, ensure_ascii=False), flush=True)
        except Exception as exc:
            print(json.dumps({"error": str(exc)}, ensure_ascii=False), flush=True)

    return 0
if __name__ == "__main__":
    raise SystemExit(main())
