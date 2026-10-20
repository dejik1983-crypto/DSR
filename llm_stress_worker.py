# -*- coding: utf-8 -*-
"""Локальный ИИ-проход: выбрать ударную гласную для слов, которые нейромодель
RUAccent оставила без ударения (редкие, диалектные слова, имена, опечатки).

Скрипт запускается отдельным процессом (onnxruntime-genai, модель в ONNX),
читает запрос JSON и пишет ответ JSON. Сеть не используется.

Запрос:
    {"book": {"title": "...", "author": "..."},
     "words": [{"word": "землюшка", "contexts": ["...предложение...", ...]}, ...]}
Ответ:
    {"results": [{"word": "землюшка", "vowel": 4, "suggestion": "земл\u0301юшка",
                  "raw": "...", "source": "model|retry|none|form_mismatch|...", "pass": 1}],
     "stats": {...}}

Знак ударения (U+0301) в ответе стоит ПОСЛЕ ударной гласной — как и во всех
остальных файлах, которые создаёт конвертер (RUAccent, Silero, окно ручной правки).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import unicodedata
from pathlib import Path

MARK = "\u0301"
VOWELS = "аеёиоуыэюя"
MARK_OR_COMBINING = re.compile(r"[\u0300-\u036f\u0483-\u0489]")
NOISE = "«»\"'`.,!?;:()[]{}<>…—–-\\/|"

SYSTEM_PROMPT = (
    "Ты — корректор русского литературного ударения. Ты уверенно знаешь нормы русского языка, "
    "в том числе устаревшие, диалектные, просторечные и разговорные слова, фамилии, прозвища и топонимы.\n"
    "Задача: для каждого пронумерованного слова поставить знак ударения ´ (U+0301) сразу после ударной гласной "
    "и вернуть это слово в исходной форме.\n"
    "Правила:\n"
    "1. Буквы, их порядок, регистр и окончание должны точно совпадать с исходным словом. "
    "Склонять, спрягать и подставлять похожие слова запрещено.\n"
    "2. Знак ударения ровно один. Если в слове есть «ё», знак не нужен.\n"
    "3. Всегда отвечай наиболее вероятным вариантом для русского литературного произношения. "
    "null допустим только если буквосочетание вообще не является русским словом.\n"
    "4. Ответ на каждый пункт — это обязательно слово из списка, а не пример оформления.\n"
    "5. Слова пиши только кириллицей. Знак ударения — отдельный комбинируемый символ U+0301, "
    "он идёт сразу после ударной гласной. Не используй латинские буквы и не заменяй гласные "
    "на готовые á, ó, é, ý, à.\n"
    "Формат ответа — только JSON, как в образце."
)

RETRY_SYSTEM_PROMPT = (
    "Ты — корректор русского литературного ударения. Ты знаешь нормы русского языка, включая устаревшие, "
    "диалектные и просторечные слова, фамилии, прозвища и топонимы.\n"
    "Поставь знак ударения ´ (U+0301) сразу после ударной гласной в заданном слове.\n"
    "Правила:\n"
    "1. Буквы, их порядок, регистр и окончание совпадают с исходным словом.\n"
    "2. Знак ровно один. Если есть «ё», знак не нужен.\n"
    "3. Отвечай всегда наиболее вероятным вариантом; ответ без знака ударения считается отказом.\n"
    "Формат ответа — только JSON: {\"results\":[{\"i\":1,\"s\":\"соба\u0301ка\"}]}"
)


def log(message: str) -> None:
    sys.stdout.write(message + "\n")
    sys.stdout.flush()


def strip_marks(text: str) -> str:
    return MARK_OR_COMBINING.sub("", text)


def strip_noise(text: str) -> str:
    return text.strip().strip(NOISE).strip()


def clean_candidate(text: str) -> str:
    """Убирает markdown-разметку, которой модель иногда оборачивает слово."""
    return re.sub(r"[*_`~]", "", text)


# Слабые и бесплатные модели часто подменяют кириллические буквы похожими латинскими
# (например «катaлoг» с латинскими a и o) или ставят вместо комбинируемого знака U+0301
# разделительный ´ либо заранее склеенную латинскую «ó». Приводим такие ответы к обычному
# виду «кириллица + U+0301 после ударной гласной», иначе верный ответ отбраковывается.
_LATIN_HOMOGLYPHS = {
    "a": "а", "c": "с", "e": "е", "o": "о", "p": "р", "x": "х", "y": "у",
    "s": "с", "k": "к", "m": "м", "t": "т",
    "A": "А", "C": "С", "E": "Е", "O": "О", "P": "Р", "X": "Х", "Y": "У",
    "S": "С", "K": "К", "M": "М", "T": "Т",
}
_SPACING_ACUTE = {"\u00b4", "\u02b9", "\u02bc", "\u2019", "\u2032"}


def normalize_marks(text: str) -> str:
    """Убирает разнобой в записи ударения: разделительный ´ и латинские буквы → кириллица."""
    out: list[str] = []
    for ch in text:
        if ch in _SPACING_ACUTE:
            out.append(MARK)
            continue
        if ord(ch) > 0x00A0 and "LATIN" in unicodedata.name(ch, ""):
            for part in unicodedata.normalize("NFKD", ch):
                out.append(_LATIN_HOMOGLYPHS.get(part, part))
            continue
        out.append(ch)
    return "".join(out)


def resolve_answer(word: str, answer: object) -> tuple[int | None, str]:
    """Возвращает (индекс ударной гласной в исходном слове, причина отказа)."""
    if answer is None:
        return None, "none"
    if not isinstance(answer, str):
        return None, "badtype"
    candidate = clean_candidate(strip_noise(normalize_marks(answer)))
    if not candidate:
        return None, "empty"
    saw_word = False
    for token in re.findall(r"[А-Яа-яЁё\u0301]+", candidate):
        token = strip_noise(token)
        if not token:
            continue
        if strip_marks(token).lower() != word.lower():
            continue
        saw_word = True
        positions = [i for i, ch in enumerate(token) if ch == MARK]
        if len(positions) > 1:
            return None, "many_marks"
        if not positions:
            continue
        position = positions[0]
        # Конвенция файла: знак стоит перед ударной гласной.
        if position + 1 < len(token) and token[position + 1].lower() in VOWELS:
            return len(strip_marks(token[:position])), "model"
        # Принимаем и «знак после гласной» и нормализуем к конвенции файла.
        if position > 0 and token[position - 1].lower() in VOWELS:
            return len(strip_marks(token[:position - 1])), "model_after"
    # Слово без знака, но с ё: ударение уже обозначено.
    for token in re.findall(r"[А-Яа-яЁё]+", candidate):
        if strip_noise(token).lower() == word.lower() and "ё" in token.lower():
            return token.lower().index("ё"), "yo"
    return None, "form_mismatch" if saw_word else "other_word"


def build_suggestion(word: str, vowel_index: int) -> str:
    """Знак ударения ставится ПОСЛЕ ударной гласной — в той же конвенции, что
    использует окно ручной проверки и файл пользовательских ударений."""
    return word[:vowel_index + 1] + MARK + word[vowel_index + 1:]


def parse_payload(text: str) -> list[dict]:
    cleaned = text.strip()
    if cleaned.startswith("```"):
        cleaned = re.sub(r"^```[a-zA-Z]*\s*", "", cleaned)
        cleaned = re.sub(r"```\s*$", "", cleaned).strip()
    start = cleaned.find("{")
    end = cleaned.rfind("}")
    if start < 0 or end < start:
        return []
    try:
        payload = json.loads(cleaned[start:end + 1])
    except (ValueError, TypeError):
        return []
    if isinstance(payload, dict):
        items = payload.get("results")
        if isinstance(items, list):
            return [item for item in items if isinstance(item, dict)]
    return []


def book_line(book: dict) -> str:
    title = (book or {}).get("title") or ""
    author = (book or {}).get("author") or ""
    if not title and not author:
        return ""
    return "Книга: %s%s" % (title, (", " + author) if author else "")


def build_user_prompt(book: dict, batch: list[dict], single: bool) -> str:
    lines: list[str] = []
    header = book_line(book)
    if header:
        lines.append(header)
    if not single:
        lines.append("Образец оформления ответа (эти слова не из списка, отвечать ими нельзя):")
        lines.append('1) молоко -> {"i":1,"s":"молоко\u0301"}')
        lines.append('2) собака -> {"i":2,"s":"соба\u0301ка"}')
    lines.append("Слова из книги (для каждого нужен знак ударения):")
    for number, item in enumerate(batch, start=1):
        lines.append("%d) %s" % (number, item["word"]))
        for context in (item.get("contexts") or [])[:2 if not single else 1]:
            context = context.strip()
            if context:
                lines.append("   контекст: %s" % context)
    lines.append('Ответь JSON: {"results":[{"i":1,"s":"<ответ>"}]} для слов 1..%d.' % len(batch))
    return "\n".join(lines)


def prepare_dll_paths() -> None:
    """Windows: делает CUDA/cuDNN из пакетов nvidia-*-cu12 видимыми для onnxruntime-genai.

    onnxruntime-genai грузит свою CUDA-библиотеку обычным LoadLibrary, который ищет
    зависимости по PATH, поэтому одних каталогов из os.add_dll_directory мало.
    """
    if not sys.platform.startswith("win"):
        return
    extra: list[str] = []
    try:
        import importlib.util

        spec = importlib.util.find_spec("onnxruntime_genai")
        if spec and spec.submodule_search_locations:
            extra.append(spec.submodule_search_locations[0])
    except Exception:
        pass
    try:
        import onnxruntime

        ort_dir = os.path.dirname(os.path.abspath(onnxruntime.__file__))
        extra.append(os.path.join(ort_dir, "capi"))
        nvidia_dir = os.path.join(os.path.dirname(ort_dir), "nvidia")
        if os.path.isdir(nvidia_dir):
            for name in os.listdir(nvidia_dir):
                candidate = os.path.join(nvidia_dir, name, "bin")
                if os.path.isdir(candidate):
                    extra.append(candidate)
    except Exception:
        pass
    existing = os.environ.get("PATH", "")
    for directory in extra:
        if not os.path.isdir(directory):
            continue
        try:
            os.add_dll_directory(directory)
        except Exception:
            pass
        existing = directory + os.pathsep + existing
    os.environ["PATH"] = existing


def load_model(model_dir: str, device: str):
    """Загружает модель. Возвращает (og, model, tokenizer, подпись устройства, предупреждение)."""
    warning = None
    prepare_dll_paths()
    try:
        import onnxruntime

        # Загружает CUDA/cuDNN DLL из пакетов nvidia-*-cu12 в текущий процесс.
        try:
            onnxruntime.preload_dlls()
        except Exception:
            pass
    except Exception:
        pass
    import onnxruntime_genai as og

    if device != "cpu":
        try:
            config = og.Config(model_dir)
            config.clear_providers()
            config.append_provider("cuda")
            model = og.Model(config)
            tokenizer = og.Tokenizer(model)
            return og, model, tokenizer, "CUDA", None
        except Exception as error:  # noqa: BLE001
            warning = "CUDA недоступна, считаю на CPU: %s" % str(error).strip().splitlines()[0]
    model = og.Model(model_dir)
    tokenizer = og.Tokenizer(model)
    return og, model, tokenizer, getattr(model, "device_type", "CPU"), warning


def render_chat(tokenizer, system_prompt: str, user_prompt: str) -> str:
    messages = json.dumps(
        [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ],
        ensure_ascii=False,
    )
    try:
        return tokenizer.apply_chat_template(messages, add_generation_prompt=True)
    except Exception:
        return (
            "<|im_start|>system\n" + system_prompt + "<|im_end|>\n"
            "<|im_start|>user\n" + user_prompt + "<|im_end|>\n"
            "<|im_start|>assistant\n"
        )


def generate(og, model, tokenizer, prompt: str, max_new_tokens: int) -> str:
    params = og.GeneratorParams(model)
    tokens = tokenizer.encode(prompt)
    params.set_search_options(
        max_length=len(tokens) + max_new_tokens,
        do_sample=False,
        top_k=1,
        num_beams=1,
        temperature=1.0,
        repetition_penalty=1.0,
    )
    generator = og.Generator(model, params)
    generator.append_tokens(tokens)
    while not generator.is_done():
        generator.generate_next_token()
    produced = generator.get_sequence(0)
    if hasattr(produced, "tolist"):
        produced = produced.tolist()
    produced = list(produced)[len(tokens):]
    return tokenizer.decode(produced)


# --- Способы получить ответ модели: локальный ONNX GenAI или внешний сервер по HTTP ---
# Подсказки, разбор и проверка ответа одинаковы для всех, поэтому подменяется только
# сам шаг генерации. Это позволяет подключать любой сервис, включая модели без цензуры.

class LocalBackend:
    """Модель ONNX GenAI, лежащая рядом с программой."""

    kind = "local"

    def __init__(self, model_dir: str, device: str):
        self.og, self.model, self.tokenizer, self.label, self.warning = load_model(model_dir, device)

    def chat(self, system_prompt: str, user_prompt: str, max_new_tokens: int) -> str:
        prompt = render_chat(self.tokenizer, system_prompt, user_prompt)
        return generate(self.og, self.model, self.tokenizer, prompt, max_new_tokens)

    def models(self) -> list[str]:
        return []


# Коды, при которых имеет смысл повторить запрос: бесплатные модели внешних сервисов
# часто отвечают «занято» (429) или отдают временный сбой шлюза.
_RETRYABLE_HTTP = {429, 500, 502, 503, 504}

# Признаки того, что повтор не поможет: исчерпан дневной лимит бесплатных моделей,
# закончились средства или закрыт сам ключ. Такие ответы показываем сразу и понятно.
_HARD_QUOTA_MARKERS = ("free-models-per-day", "free_tier_daily", "key_limit", "Key limit exceeded")


def _explain_http_error(code: int, detail: str) -> str:
    """Человеческое объяснение отказа сервиса: что именно случилось и что делать."""
    message = ""
    try:
        payload = json.loads(detail) if detail.strip() else {}
        error = payload.get("error") if isinstance(payload, dict) else None
        if isinstance(error, dict):
            message = str(error.get("message") or "").strip()
        elif isinstance(error, str):
            message = error.strip()
    except (ValueError, TypeError):
        message = ""
    if not message:
        message = (detail or "").strip().splitlines()[0] if detail.strip() else ""
    hint = {
        401: "Проверьте ключ API.",
        402: "На счету внешнего сервиса нет средств.",
        403: "Лимит ключа исчерпан или эта модель закрыта для вашего ключа.",
        429: "Исчерпан дневной лимит бесплатных моделей — дождитесь сброса или пополните счёт сервиса.",
    }.get(code, "")
    parts = ["HTTP %d" % code]
    if message:
        parts.append(message)
    if hint:
        parts.append(hint)
    return "; ".join(parts)


def http_json(url: str, payload, api_key: str | None, timeout: float, method: str = "POST", attempts: int = 4):
    """Запрос к внешнему серверу. Только стандартная библиотека: ничего доставлять не нужно.
    Повторяет запрос при «сервис занят» с нарастающей паузой, чтобы бесплатный лимит не срывал проход,
    но при жёстком лимите (день/ключ) сразу сообщает причину, не тратя время на повторы."""
    import urllib.error
    import urllib.request

    body = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
    delay = 5.0
    for attempt in range(1, attempts + 1):
        request = urllib.request.Request(url, data=body, method=method)
        request.add_header("Content-Type", "application/json")
        if api_key:
            request.add_header("Authorization", "Bearer " + api_key)
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                text = response.read().decode("utf-8", "replace")
            return json.loads(text) if text.strip() else {}
        except urllib.error.HTTPError as error:
            try:
                detail = error.read().decode("utf-8", "replace")
            except Exception:  # noqa: BLE001
                detail = ""
            hard = error.code in (401, 402, 403) or (error.code == 429 and
                                                     any(marker in detail for marker in _HARD_QUOTA_MARKERS))
            if hard:
                raise RuntimeError(_explain_http_error(error.code, detail)) from None
            if error.code not in _RETRYABLE_HTTP or attempt >= attempts:
                raise
            header = (error.headers.get("Retry-After") if error.headers else "") or ""
            try:
                wait = max(delay, float(header))
            except ValueError:
                wait = delay
            log("DSR_LLM:warning:сервис занят, код %s; повтор через %d с (попытка %d из %d)"
                % (error.code, int(wait), attempt, attempts))
            time.sleep(wait)
            delay = min(delay * 2, 60)
        except urllib.error.URLError:
            if attempt >= attempts:
                raise
            time.sleep(delay)
            delay = min(delay * 2, 60)
    raise RuntimeError("сервис не ответил после нескольких попыток")


class OllamaBackend:
    """Свой сервер Ollama. Годится и для моделей без цензуры: достаточно загрузить такую
    модель командой `ollama pull <модель>` и выбрать её здесь — ограничений на неё нет."""

    kind = "ollama"

    def __init__(self, base_url: str, model: str, timeout: float):
        self.base = (base_url or "http://127.0.0.1:11434").rstrip("/")
        self.model = model
        self.timeout = timeout
        self.label = "Ollama (%s)" % (model or "модель не выбрана")
        self.warning = None

    def chat(self, system_prompt: str, user_prompt: str, max_new_tokens: int) -> str:
        payload = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "stream": False,
            "options": {"temperature": 0, "top_k": 1, "num_predict": max(64, max_new_tokens)},
        }
        data = http_json(self.base + "/api/chat", payload, None, self.timeout)
        return ((data.get("message") or {}).get("content") or "").strip()

    def models(self) -> list[str]:
        data = http_json(self.base + "/api/tags", None, None, 20, method="GET")
        return sorted({(item.get("name") or "") for item in (data.get("models") or []) if item.get("name")})


class OpenAiBackend:
    """Внешний сервис с OpenAI-совместимым интерфейсом: OpenAI, OpenRouter, LM Studio,
    llama.cpp, vLLM и прочие. Адрес указывается до версии, например https://api.openai.com/v1."""

    kind = "openai"

    def __init__(self, base_url: str, model: str, api_key: str, timeout: float):
        base = (base_url or "").strip().rstrip("/")
        if base and "/" not in base.split("://", 1)[-1]:
            base += "/v1"  # указан только адрес сервера — дописываем стандартный путь
        self.base = base
        self.model = model
        self.api_key = api_key
        self.timeout = timeout
        self.label = "API (%s)" % (model or "модель не выбрана")
        self.warning = None

    def chat(self, system_prompt: str, user_prompt: str, max_new_tokens: int) -> str:
        payload = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "temperature": 0,
            "max_tokens": max(64, max_new_tokens),
            "stream": False,
        }
        data = http_json(self.base + "/chat/completions", payload, self.api_key, self.timeout)
        choices = data.get("choices") or []
        if not choices:
            return ""
        content = (choices[0].get("message") or {}).get("content")
        if isinstance(content, list):  # некоторые сервисы отдают ответ частями
            content = "".join(part.get("text", "") for part in content if isinstance(part, dict))
        return (content or "").strip()

    def models(self) -> list[str]:
        data = http_json(self.base + "/models", None, self.api_key, 20, method="GET")
        return sorted({(item.get("id") or "") for item in (data.get("data") or []) if item.get("id")})


def create_backend(args):
    """Собирает выбранный способ получения ответов модели."""
    timeout = float(os.environ.get("DSR_LLM_HTTP_TIMEOUT") or 900)
    provider = (args.provider or "local").lower()
    if provider == "ollama":
        return OllamaBackend(args.base_url, args.remote_model or args.model, timeout)
    if provider == "openai":
        return OpenAiBackend(args.base_url, args.remote_model or args.model, args.api_key, timeout)
    return LocalBackend(args.model, args.device)


def process_pass(backend, book, items, system_prompt, batch_size, max_new_tokens,
                 single, label, started, raw_dumps):
    """Один проход по списку слов. Возвращает словарь word -> (vowel, reason, raw)."""
    strict = getattr(backend, "kind", "local") != "local"
    outcomes: dict[str, tuple[int | None, str, str | None]] = {}
    for offset in range(0, len(items), batch_size):
        batch = items[offset:offset + batch_size]
        # Сообщаем интерфейсу, какие слова уходят в модель прямо сейчас: пары
        # ask/answer дают живую строку «слово — думаю / результат», по которой видно,
        # что проход не завис, даже когда партия слов считается десятки секунд.
        for item in batch:
            log("DSR_LLM:ask:%s:%s" % (label, item["word"]))
        user_prompt = build_user_prompt(book, batch, single)
        cap = max_new_tokens or max(160, 40 * len(batch))
        answer = ""
        try:
            answer = backend.chat(system_prompt, user_prompt, cap)
        except Exception as failure:  # noqa: BLE001
            # У внешнего сервиса ошибка означает негодные адрес/ключ/модель: незачем
            # повторять её для каждой партии слов — сообщаем сразу.
            if strict:
                raise
            log("DSR_LLM:batch_error:%s" % str(failure).splitlines()[0])
        by_index = {}
        for item in parse_payload(answer):
            try:
                by_index[int(item.get("i"))] = item.get("s")
            except (TypeError, ValueError):
                continue
        if os.environ.get("DSR_LLM_DEBUG"):
            raw_dumps.append({"pass": label, "batch": offset // batch_size + 1, "answer": answer})
        for number, item in enumerate(batch, start=1):
            vowel, reason = resolve_answer(item["word"], by_index.get(number))
            outcomes[item["word"]] = (vowel, reason, by_index.get(number) if isinstance(by_index.get(number), str) else None)
            suggestion = build_suggestion(item["word"], vowel) if vowel is not None else ""
            log("DSR_LLM:answer:%s:%s:%s:%s" % (label, item["word"], "ok" if vowel is not None else "none", suggestion))
        done = min(offset + batch_size, len(items))
        log("DSR_LLM:progress:%s:%d/%d:%.1fs" % (label, done, len(items), time.time() - started))
    return outcomes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="", help="папка локальной модели ONNX GenAI")
    parser.add_argument("--in", dest="request_path", default="")
    parser.add_argument("--out", dest="response_path", default="")
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--batch", type=int, default=8)
    parser.add_argument("--max-new-tokens", type=int, default=0)
    parser.add_argument("--no-retry", dest="retry", action="store_false")
    parser.add_argument("--provider", default="local", choices=["local", "ollama", "openai"])
    parser.add_argument("--base-url", dest="base_url", default="")
    parser.add_argument("--remote-model", dest="remote_model", default="")
    parser.add_argument("--api-key", dest="api_key", default="")
    parser.add_argument("--list-models", dest="list_models", action="store_true")
    parser.set_defaults(retry=True)
    args = parser.parse_args()
    if not args.api_key:
        # Ключ удобнее держать в переменной окружения: тогда он не виден в списке процессов.
        args.api_key = os.environ.get("DSR_LLM_API_KEY", "")

    if args.list_models:
        try:
            names = create_backend(args).models()
        except Exception as error:  # noqa: BLE001
            print("DSR_LLM:error:%s" % str(error).splitlines()[0], flush=True)
            return 2
        for name in names:
            print("DSR_LLM:available:%s" % name, flush=True)
        print("DSR_LLM:available_done", flush=True)
        return 0

    if not args.request_path or not args.response_path:
        print("Не заданы --in и --out.", file=sys.stderr, flush=True)
        return 1

    started = time.time()
    request = json.loads(Path(args.request_path).read_text(encoding="utf-8"))
    book = request.get("book") or {}
    words = [item for item in (request.get("words") or []) if item.get("word")]

    def fail(message: str) -> int:
        Path(args.response_path).write_text(
            json.dumps({"error": message, "results": []}, ensure_ascii=False), encoding="utf-8")
        print(message, file=sys.stderr, flush=True)
        return 2

    if args.provider == "local" and not args.model:
        return fail("Не указана папка локальной модели.")

    try:
        backend = create_backend(args)
    except Exception as error:  # noqa: BLE001
        return fail("Не удалось подготовить модель: %s" % error)

    log("DSR_LLM:model:%s" % backend.label)
    if getattr(backend, "warning", None):
        log("DSR_LLM:warning:%s" % backend.warning)
    log("DSR_LLM:words:%d" % len(words))
    load_seconds = time.time() - started
    log("DSR_LLM:loaded:%.1f" % load_seconds)

    raw_dumps: list[dict] = []
    try:
        outcomes = process_pass(backend, book, words, SYSTEM_PROMPT, max(1, args.batch),
                                args.max_new_tokens, False, "1", started, raw_dumps)
    except Exception as error:  # noqa: BLE001
        return fail("Нет ответа от «%s»: %s" % (backend.label, str(error).splitlines()[0]))

    first_pass_ok = sum(1 for vowel, _, _ in outcomes.values() if vowel is not None)

    retried = 0
    retry_words: set[str] = set()
    unresolved = [item for item in words if outcomes.get(item["word"], (None, "", None))[0] is None]
    if args.retry and unresolved:
        log("DSR_LLM:retry_start:%d" % len(unresolved))
        try:
            retry_outcomes = process_pass(backend, book, unresolved, RETRY_SYSTEM_PROMPT, 1,
                                          args.max_new_tokens or 96, True, "2", started, raw_dumps)
        except Exception as error:  # noqa: BLE001
            log("DSR_LLM:batch_error:%s" % str(error).splitlines()[0])
            retry_outcomes = {}
        for word, (vowel, reason, raw) in retry_outcomes.items():
            if vowel is not None:
                retried += 1
                retry_words.add(word)
                outcomes[word] = (vowel, reason, raw)

    results = []
    for item in words:
        word = item["word"]
        vowel, reason, raw = outcomes.get(word, (None, "none", None))
        suggestion = build_suggestion(word, vowel) if vowel is not None else None
        results.append({
            "word": word,
            "vowel": vowel,
            "suggestion": suggestion,
            "raw": raw,
            "source": "model" if suggestion else reason,
            "pass": (2 if word in retry_words else 1) if suggestion else 0,
        })

    response = {
        "results": results,
        "stats": {
            "items": len(results),
            "answered": sum(1 for item in results if item["suggestion"]),
            "answered_first_pass": first_pass_ok,
            "answered_retry": retried,
            "device": backend.label,
            "load_seconds": round(load_seconds, 2),
            "seconds": round(time.time() - started, 2),
        },
    }
    Path(args.response_path).write_text(json.dumps(response, ensure_ascii=False, indent=1), encoding="utf-8")
    if raw_dumps:
        Path(args.response_path + ".raw.txt").write_text(
            "\n\n".join("=== pass %s batch %s ===\n%s" % (item["pass"], item["batch"], item["answer"]) for item in raw_dumps),
            encoding="utf-8",
        )
    log("DSR_LLM:done:%d/%d" % (response["stats"]["answered"], len(results)))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001
        print("Необработанная ошибка: %s" % error, file=sys.stderr, flush=True)
        sys.exit(3)
