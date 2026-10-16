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
import gzip
import io
from concurrent.futures import ThreadPoolExecutor
# Keep third-party initialization warnings and native-library notices out of
# stdout: stdout is the machine-readable JSON/JSON-lines protocol for DSR.
import sys
import traceback
import time
# Allow an operator to tune CPU inference; cap defaults to avoid oversubscribing
# small machines. CUDA kernels remain managed by ONNX Runtime.
_CPU_THREADS = max(1, int(os.environ.get("DSR_CPU_THREADS", os.cpu_count() or 1)))
# Independent text nodes are latency-bound: each one triggers many tiny ONNX
# sessions, so a strictly sequential loop leaves cores and GPU streams idle.
# Process several nodes concurrently to overlap tokenization and inference.
_PIPELINE_WORKERS = max(1, int(os.environ.get("DSR_PIPELINE_WORKERS") or min(12, max(2, os.cpu_count() or 4))))
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


def _emit(line: str, stream=None) -> None:
    # Write a whole diagnostic line atomically: pipeline workers run concurrently,
    # and print() splits text and newline into separate writes that can interleave.
    stream = stream or sys.stderr
    stream.write(line + "\n")
    stream.flush()

# Words that must never carry a stress mark — short prepositions, conjunctions
# and particles. ruaccent sometimes marks them erroneously; this list prevents
# that. All comparisons are case-insensitive and ё/e are normalised.
_UNSTRESSED_WORDS: frozenset[str] = frozenset((
    # Prepositions
    "о", "об", "обо", "в", "во", "с", "со", "к", "ко", "у", "от", "до", "из",
    "без", "для", "про", "над", "под", "при", "на", "за",
    # Conjunctions / particles
    "и", "а", "но", "да", "ли", "бы", "же", "ль", "то",
))


def _normalize(text: str) -> str:
    """Lower-case and normalise ё→e, remove accent markers."""
    return ACCENT_RE.sub("", text).casefold().replace("ё", "e").replace("Ё", "e")


def _remove_stress_from_unstressed(text: str) -> str:
    """Remove stress markers (+) fromЁ and from short function words."""
    # 1) Ё is inherently stressed — remove any + before or after it.
    #    ruaccent can produce "по+ёшь" (stress on ё) — strip the + so the
    #    combining mark is never placed on the preceding letter.
    #    Also remove "ё+" (ruaccent sometimes places + after the vowel).
    result = re.sub(r"\+(?=[ёЁ])", "", text)   # + before ё
    result = re.sub(r"([ёЁ])\+", r"\1", result)  # + after ё

    # 2) Short function words (prepositions, conjunctions, particles).
    #    Strip + from these words wherever they appear as whole tokens.
    def _strip_word(match: re.Match) -> str:
        word = match.group(0)
        if _normalize(word) in _UNSTRESSED_WORDS:
            return ACCENT_RE.sub("", word)  # remove all accent markers
        return word
    return WORD_RE.sub(_strip_word, result)


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


def configure_onnx_performance(accentizer, omograph_model_size: str = "") -> None:
    # Report loaded ORT sessions and their active execution providers, so the report shows
    # exactly which models (and which omograph model) are actually in use this run.
    for model_name in ("omograph_model", "accent_model", "stress_usage_predictor", "yo_homograph_model"):
        model = getattr(accentizer, model_name, None)
        session = getattr(model, "session", None)
        label = f"{model_name}(model={omograph_model_size})" if model_name == "omograph_model" and omograph_model_size else model_name
        if session is None:
            # Legitimate only for stress_usage_predictor in tiny_mode; anything else means
            # the model silently did not load, which the report must not hide.
            print(f"DSR_PERF:{label}:MISSING", file=sys.stderr, flush=True)
            continue
        # Report actual providers and avoid silently changing session behavior;
        # thread pools are set via environment before ORT import.
        print(f"DSR_PERF:{label}:providers={','.join(session.get_providers())}:cpu_threads={_CPU_THREADS}", file=sys.stderr, flush=True)


def verify_required_models(accentizer) -> None:
    # omograph_model is what the model picker actually switches; if it is absent the run
    # would silently fall back to accent-only output (the "identical result" symptom), so
    # fail loudly instead.
    missing = [
        name for name in ("omograph_model", "accent_model", "yo_homograph_model")
        if getattr(getattr(accentizer, name, None), "session", None) is None
    ]
    if missing:
        raise RuntimeError("RUAccent не загрузил обязательные ONNX-модели: " + ", ".join(missing))


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


# Per-word accent inference is deterministic and depends only on the word itself,
# while a book repeats the same words many times. Caching the (single-word) result
# removes the redundant ONNX runs and tokenizations without changing any output —
# important on GPU, where every extra run adds launch overhead and keeps the card idle.
_ACCENT_CACHE_ENABLED = os.environ.get("DSR_ACCENT_CACHE", "1") != "0"
# A book repeats words but has a bounded vocabulary; the cap keeps a pathological
# document from growing the cache without limit (past it we simply stop adding).
_ACCENT_CACHE_LIMIT = max(0, int(os.environ.get("DSR_ACCENT_CACHE_LIMIT", "200000")))
_ACCENT_CACHE = {}
_ACCENT_CACHE_HITS = [0]

# RUAccent ships a second, far larger stress dictionary (accents.json.gz, ~3.2M
# forms) that is skipped in tiny_mode to keep RAM low. When DSR finds polysyllabic
# words the neural model left unmarked, it streams that file once and keeps only the
# entries that were asked for, so the worker's memory stays proportional to the
# review list instead of the dictionary.
_AUTOSTRESS_DICT_ENABLED = os.environ.get("DSR_AUTOSTRESS_DICT", "1") != "0"
_FULL_DICT_KNOWN = {}


def _full_dictionary_path() -> pathlib.Path:
    spec = importlib.util.find_spec("ruaccent")
    if spec is None or spec.origin is None:
        return pathlib.Path()
    return pathlib.Path(spec.origin).resolve().parent / "dictionary" / "accents.json.gz"


def _dict_key(word: str) -> str:
    # Mirror RUAccent's own lookup: lower-case and drop accent markers, but keep ё
    # as-is, because accents.json.gz stores ё spellings verbatim (unlike _normalize,
    # which folds ё to е for the function-word list).
    return ACCENT_RE.sub("", word).lower()


def resolve_full_dictionary(words):
    """Return {word: stressed-form-with-plus} for words present in accents.json.gz.

    Only the requested entries are kept, so the 3.2M-entry dictionary never
    inflates the worker's RSS. Results (including misses) are cached across calls.
    """
    result = {}
    if not _AUTOSTRESS_DICT_ENABLED:
        return result
    missing = []
    for word in words:
        key = _dict_key(str(word))
        if key and key not in _FULL_DICT_KNOWN:
            _FULL_DICT_KNOWN[key] = None
            missing.append(key)
    if missing:
        wanted = set(missing)
        try:
            with gzip.open(_full_dictionary_path(), "rt", encoding="utf-8") as stream:
                for line in stream:
                    q = line.find('": "')
                    if q < 0:
                        continue
                    key = line[2:q]
                    if key not in wanted:
                        continue
                    value = line[q + 3:].rstrip()
                    if value.endswith(","):
                        value = value[:-1]
                    if value.startswith('"') and value.endswith('"'):
                        value = value[1:-1]
                    _FULL_DICT_KNOWN[key] = value
                    wanted.discard(key)
                    if not wanted:
                        break
        except OSError as exc:
            _emit(f"DSR_WARN:полный словарь ударений недоступен: {exc}")
    for word in words:
        value = _FULL_DICT_KNOWN.get(_dict_key(str(word)))
        if value:
            result[str(word)] = value
    return result


def _cache_store(word: str, value: str) -> None:
    if _ACCENT_CACHE_LIMIT == 0 or len(_ACCENT_CACHE) < _ACCENT_CACHE_LIMIT:
        _ACCENT_CACHE[word] = value


def install_accent_cache(accentizer) -> None:
    if not _ACCENT_CACHE_ENABLED:
        return
    model = getattr(accentizer, "accent_model", None)
    if model is None:
        return
    original = model.put_accent

    def put_accent_cached(word):
        cached = _ACCENT_CACHE.get(word)
        if cached is not None:
            _ACCENT_CACHE_HITS[0] += 1
            return cached
        result = original(word)
        _cache_store(word, result)
        return result

    model.put_accent = put_accent_cached


# RUAccent accents words one at a time, which means thousands of tiny ONNX calls
# per book: the GPU sits idle between launches while the CPU pays the launch and
# tokenizer overhead. This batches every candidate word of a paragraph into a few
# calls per word length (identical lengths need no padding). Words whose decision is
# close to the model's thresholds are re-checked individually, so the produced text
# is identical to the plain one-word-at-a-time result.
_ACCENT_BATCH_STATS = {"batches": 0, "words": 0, "rechecks": 0, "fallbacks": 0}

# The batched path mirrors a few RUAccent internals (the [CLS]/[SEP] slice width,
# argmax over logits, the 0.55 threshold). A start-up self-test runs the batched path
# against the library's own one-word path; if they disagree for even one word,
# batching is switched off and the slower-but-certain path is used.
_ACCENT_SELFTEST_WORDS = (
    "теперь новое зубах топливо горстями шагах длинным платок жилам пламенем плечо вела "
    "одной мама окно дерево человек она они его сегодня вчера работа работает красивая "
    "быстро медленно хорошо плохо книга книги читаю читаем замок коса начала округа сторожи "
    "развитая пережитое роженица пристава ожила создало проклята коротки удало волоки "
    "вывозите выходите железы перепела балчуга вертела воронью бутиках сторожу приставу "
    "электричество здравствуйте государство путешествие"
).split()
_EXPECTED_RUACCENT_VERSION = os.environ.get("DSR_RUACCENT_VERSION", "1.5.8.3")


def install_batched_pipeline(accentizer) -> None:
    if os.environ.get("DSR_PIPELINE_BATCH", "1") == "0":
        return
    model = getattr(accentizer, "accent_model", None)
    session = getattr(model, "session", None)
    tokenizer = getattr(model, "tokenizer", None)
    id2label = getattr(model, "id2label", None)
    if session is None or tokenizer is None or id2label is None:
        return
    try:
        import numpy as _np
        from ruaccent.text_preprocessor import TextPreprocessor as _TextPreprocessor
        from ruaccent.accent_model import softmax as _softmax
    except Exception:
        return

    original_internal = accentizer.process_all_internal
    batch_size = max(1, int(os.environ.get("DSR_ACCENT_BATCH", "64")))
    threshold = 0.55
    borderline = 0.10
    logits_index = {output.name: i for i, output in enumerate(session.get_outputs())}.get("logits", 0)

    def _put_accent_batch(group_words):
        results = [None] * len(group_words)
        # Sorting by length keeps every chunk nearly uniform, so a single padded run
        # per chunk wastes almost nothing while cutting the number of ONNX launches
        # (the main reason the GPU stayed idle on thousands of one-word calls).
        order = sorted(range(len(group_words)), key=lambda i: len(group_words[i]))
        for start in range(0, len(order), batch_size):
            group = order[start:start + batch_size]
            words_in_group = [group_words[i] for i in group]
            lowers = [w.lower() for w in words_in_group]
            encoded = tokenizer(lowers, return_tensors="np", padding=True)
            inputs = {k: v.astype(_np.int64) for k, v in encoded.items()}
            logits = session.run(None, inputs)[logits_index]
            probabilities = _softmax(logits)
            _ACCENT_BATCH_STATS["batches"] += 1
            _ACCENT_BATCH_STATS["words"] += len(group)
            for offset, index in enumerate(group):
                length = len(lowers[offset]) + 2
                token_probs = probabilities[offset, :length, :]
                token_logits = logits[offset, :length, :]
                scores = _np.max(token_probs, axis=-1)
                labels = _np.argmax(token_logits, axis=-1)
                part = _np.partition(token_probs, -2, axis=-1)
                margins = part[:, -1] - part[:, -2]
                if bool(_np.min(_np.abs(scores - threshold)) < borderline) or bool(_np.min(margins) < borderline):
                    _ACCENT_BATCH_STATS["rechecks"] += 1
                    results[index] = model.put_accent(words_in_group[offset])
                    continue
                pred = [{"label": id2label[str(int(l))], "score": float(s)} for l, s in zip(labels, scores)]
                results[index] = model.render_stress(words_in_group[offset], pred)
        return results

    try:
        import ruaccent as _ruaccent_package
        installed_version = getattr(_ruaccent_package, "__version__", "")
    except Exception:
        installed_version = ""
    forced = os.environ.get("DSR_PIPELINE_BATCH", "") == "1"
    if installed_version and installed_version != _EXPECTED_RUACCENT_VERSION and not forced:
        _emit(
            "DSR_PERF:accent_batch:disabled (ruaccent {0} != tested {1}; set DSR_PIPELINE_BATCH=1 to force)".format(
                installed_version, _EXPECTED_RUACCENT_VERSION
            )
        )
        return
    try:
        expected = [model.put_accent(word) for word in _ACCENT_SELFTEST_WORDS]
        produced = _put_accent_batch(list(_ACCENT_SELFTEST_WORDS))
        mismatches = [(w, e, p) for w, e, p in zip(_ACCENT_SELFTEST_WORDS, expected, produced) if e != p]
    except Exception as exc:
        _emit(f"DSR_PERF:accent_batch:disabled (self-test error: {type(exc).__name__}: {exc})")
        return
    if mismatches:
        word, want, got = mismatches[0]
        _emit(
            "DSR_PERF:accent_batch:disabled (self-test mismatch {0} of {1}: {2!r} -> {3!r} != {4!r})".format(
                len(mismatches), len(_ACCENT_SELFTEST_WORDS), word, got, want
            )
        )
        return
    _emit(
        "DSR_PERF:accent_batch:enabled (ruaccent {0}, self-test ok, {1} words)".format(
            installed_version or "unknown", len(_ACCENT_SELFTEST_WORDS)
        )
    )

    def batched_process_all_internal(text):
        text = re.sub(accentizer.normalize, "", text)
        sentences = _TextPreprocessor.split_by_sentences(text)
        pieces = []
        jobs = []
        for sentence in sentences:
            sentence_words, remaining_text = _TextPreprocessor.split_by_words(sentence)
            if len(sentence_words) == 0:
                pieces.append(("".join(remaining_text), None))
                continue
            if getattr(accentizer, "tiny_mode", False):
                stress_usages = ["STRESS"] * len(text)
            else:
                stress_usages = accentizer.extract_entities(accentizer.stress_usage_predictor.predict_stress_usage(sentence))
            processed_words = accentizer._process_yo(sentence_words, sentence)
            processed_words = accentizer._process_omographs(processed_words)
            for i, word in enumerate(processed_words):
                if "+" in word:
                    continue
                if stress_usages[i] != "STRESS":
                    continue
                lower_word = word.lower()
                stressed_word = accentizer.accents.get(lower_word, lower_word)
                if stressed_word == lower_word and not accentizer.has_punctuation(lower_word) and accentizer.count_vowels(lower_word) > 1:
                    jobs.append((processed_words, i, word))
                else:
                    match = re.finditer(r"\+", stressed_word)
                    word_fixed = list(word)
                    for j, e in enumerate(list(match)):
                        word_fixed = word_fixed[:e.start() + j] + ["+"] + list(word)[e.end() - 1:]
                    processed_words[i] = "".join(word_fixed)
            pieces.append((remaining_text, processed_words))
        pending_words = []
        pending_jobs = []
        for job in jobs:
            container, index, word = job
            cached = _ACCENT_CACHE.get(word)
            if cached is not None:
                _ACCENT_CACHE_HITS[0] += 1
                container[index] = cached
            else:
                pending_words.append(word)
                pending_jobs.append(job)
        if pending_words:
            produced = _put_accent_batch(pending_words)
            for word, produced_word, job in zip(pending_words, produced, pending_jobs):
                container, index, _ = job
                container[index] = produced_word
                _cache_store(word, produced_word)
        assembled = []
        for remaining_text, processed_words in pieces:
            if processed_words is None:
                assembled.append(remaining_text)
                continue
            processed_sentence = "".join([l + r for l, r in zip(remaining_text, processed_words)] + [remaining_text[-1]])
            assembled.append(accentizer.delete_spaces_before_punc(processed_sentence))
        return "".join(assembled)

    def safe_process_all_internal(text):
        try:
            return batched_process_all_internal(text)
        except Exception as exc:
            _ACCENT_BATCH_STATS["fallbacks"] += 1
            if _ACCENT_BATCH_STATS["fallbacks"] == 1:
                _emit(f"WARNING: accent batching failed, using per-word path: {type(exc).__name__}: {exc}")
            return original_internal(text)

    accentizer.process_all_internal = safe_process_all_internal


def words(text: str) -> list[str]:
    return [ACCENT_RE.sub("", match.group()).casefold().replace("ё", "е") for match in WORD_RE.finditer(text)]


def safe_process(accentizer, text: str) -> str:
    # Use RUAccent's contextual paragraph inference once (faster and better at
    # disambiguating homographs). Only recover word-by-word if token alignment fails.
    result = accentizer.process_all(text)
    if words(result) == words(text) and dialogue_initial_words_preserved(text, result):
        return _remove_stress_from_unstressed(result)
    _emit("WARNING: RUAccent altered a dialogue prefix; retrying line by line.")
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
    return _remove_stress_from_unstressed(recovered_text)

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


def process_payload(accentizer, executor, payload):
    """Accent one text node or a batch of them; returns one result dict or a list."""
    if isinstance(payload, list):
        return list(executor.map(lambda item: process_item(accentizer, item), payload))
    return process_item(accentizer, payload)


def process_item(accentizer, item):
    try:
        if not isinstance(item, str):
            raise ValueError("Ожидалась JSON-строка.")
        inference_started = time.perf_counter()
        result = safe_process(accentizer, item)
        _emit(f"DSR_INFERENCE:{time.perf_counter() - inference_started:.6f}")
        return {"text": result}
    except Exception as exc:
        return {"error": str(exc)}


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
        configure_onnx_performance(accentizer, args.model)
        verify_required_models(accentizer)
        install_accent_cache(accentizer)
        install_batched_pipeline(accentizer)
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
        _emit(f"DSR_PERF:pipeline:workers={_PIPELINE_WORKERS}")
        print("READY", flush=True)
    except Exception as exc:
        print("ERROR: " + str(exc), flush=True)
        traceback.print_exc(file=sys.stderr)
        return 1
    executor = ThreadPoolExecutor(max_workers=_PIPELINE_WORKERS)
    try:
        for line in sys.stdin:
            try:
                payload = json.loads(line)
            except Exception as exc:
                print(json.dumps({"error": str(exc)}, ensure_ascii=False), flush=True)
                continue
            if isinstance(payload, dict):
                # Control request, not text: resolve the words DSR flagged for
                # review against RUAccent's bundled full stress dictionary.
                if payload.get("op") == "resolve":
                    words = payload.get("words")
                    if not isinstance(words, list):
                        words = []
                    response = {
                        "resolved": resolve_full_dictionary([str(word) for word in words]),
                        "enabled": _AUTOSTRESS_DICT_ENABLED,
                    }
                else:
                    response = {"error": f"неизвестная операция: {payload.get('op')!r}"}
            else:
                response = process_payload(accentizer, executor, payload)
            print(json.dumps(response, ensure_ascii=False), flush=True)
    finally:
        executor.shutdown(wait=False)
    if _ACCENT_CACHE_ENABLED:
        _emit(f"DSR_PERF:accent_cache:entries={len(_ACCENT_CACHE)}:hits={_ACCENT_CACHE_HITS[0]}")
    _emit(
        "DSR_PERF:accent_batch:batches={0}:words={1}:rechecks={2}:fallbacks={3}".format(
            _ACCENT_BATCH_STATS["batches"],
            _ACCENT_BATCH_STATS["words"],
            _ACCENT_BATCH_STATS["rechecks"],
            _ACCENT_BATCH_STATS["fallbacks"],
        )
    )
    if _AUTOSTRESS_DICT_ENABLED:
        # "requested" counts every distinct word we looked up (hits and misses); "found" is how
        # many the dictionary actually knew. The old "entries" label counted both and read as if
        # the dictionary had filled that many words.
        found = sum(1 for value in _FULL_DICT_KNOWN.values() if value)
        _emit(f"DSR_PERF:dictionary_stress:requested={len(_FULL_DICT_KNOWN)}:found={found}")

    return 0
if __name__ == "__main__":
    raise SystemExit(main())
