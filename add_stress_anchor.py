#!/usr/bin/env python3
"""Persistent JSON-lines bridge to add-stress-to-epub's RussianTextStresser.

Uses spaCy + Wiktionary SQLite dictionary for morphologically aware stress.

The third-party library prints diagnostics straight to stdout (for example
"Apparently wrong POS detected" in russian_dictionary.py, and stressrn warning
messages in text_stresser.py). If those land on the protocol channel they
desynchronise the JSON-lines stream and eventually fill the pipe buffer, which
deadlocks both processes. To avoid that, the real stdout pipe is kept in a
private reference and Python's ``sys.stdout`` is redirected to stderr; every
diagnostic therefore goes to the stderr channel, which the host always drains.
"""

import json
import os
import re
import sys
import traceback

# Keep a private handle to the real protocol channel before anybody (including
# imported libraries) can write stray text to it.
_REAL_STDOUT = sys.stdout

try:
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

# Route every stray print()/write to sys.stdout into the diagnostic channel.
try:
    sys.stdout = sys.stderr
except Exception:
    pass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

ACCENT = "\u0301"

# Фразеологизмы: ударение в них закреплено за фразой, а не за отдельным словом.
# add-stress разбирает текст пословно, поэтому в «от мала до велика» он ставит уда-
# рение на именительный «ма́ла» и на «вели́ка», хотя здесь родительный: «мала́»,
# «велика́». Набор тот же, что у встроенного словаря литературных ударений
# (dictionary/литературные-ударения.txt) и у IDIOMS в Program.cs: три списка должны
# совпадать, иначе запасной ход в C# перестанет защищать те же фразы.
IDIOMS = (
    ("мала", "велика"),
    ("малу", "велику"),
)
# Слово фразы: буквы и комбинирующие знаки (сам знак ударения U+0301 тоже не буква,
# и без него в совпадение попадает только «ма»). Дефис входит в слово, поэтому
# «мала́-вели́ка» не распадётся на знак минуса.
# Разбор через finditer, а не re.split: split режет на вхождениях шаблона и теряет
# сами слова (re.split(r"\\w+", "a b") даёт ["", " ", ""]).
_TOKEN = re.compile(r"[\w\u0300-\u036f\u2019'-]+")
# Разделитель внутри фразы: пробел (в том числе неразрывный) или тире.
_SEPARATORS = {" ", "\u00a0", "-", "\u2013", "\u2014"}


def _plain(word: str) -> str:
    """Буквы слова без знака ударения и дефиса."""
    stripped = "".join(ch for ch in word.lower()
                       if ch != ACCENT and not ch.isdecimal())
    return "".join(ch for ch in stripped if ch.isalpha())


def _split(text: str):
    """Чередование «слово, разделитель, слово…»: чётные индексы — слова.

    re.split на вхождениях шаблона возвращает сами разделители, а слова теряет:
    re.split(r"\\w+", "a b") — это ["", " ", ""]. Поэтому слова берём из
    finditer и возвращаем всё, что между ними, на свои места.
    """
    pieces = []
    last = 0
    for match in _TOKEN.finditer(text):
        pieces.append(text[last:match.start()])
        pieces.append(match.group(0))
        last = match.end()
    pieces.append(text[last:])
    return pieces


def _word_indices(pieces):
    """Номера токенов-слов: в _split они стоят на нечётных индексах."""
    return [(index, piece) for index, piece in enumerate(pieces) if index % 2]


def _idiom_span(pieces, index: int):
    """Номера слов фразеологизма в pieces, если начинается он с index, иначе None.

    pieces — чередование: слово, разделитель, слово… Чётные индексы — слова.
    Фраза может быть записана «мала велика» и «мала до велика»: между её словами
    стоят только разделители, а предлог «до» к самой фразе не относится.
    """
    first = _plain(pieces[index])
    for left, right in IDIOMS:
        if first != left:
            continue
        for step in (2, 4):
            tail = index + step
            if tail >= len(pieces) or _plain(pieces[tail]) != right:
                continue
            if step == 4 and _plain(pieces[index + 2]) != "до":
                continue
            # Между словами фразы — только разделители: перенос строки, запятая или
            # точка значат, что это уже не фраза.
            separators = pieces[index + 1:tail:2]
            if all(token in _SEPARATORS for token in separators):
                return (index, tail)
    return None


def protect_idioms(text: str) -> str:
    """Оставляет фразеологизмы в «от мала до велика» без ударения.

    add-stress ставит ударение пословно, и в этой фразе оно падает не туда: «ма́ла»
    и «вели́ка» — именительный падеж, а во фразе родительный: «мала́», «велика́».
    Библиотека к тому же разносит ошибку по всей книге: слово, полученное с ударением,
    дальше идёт готовым вариантом во все вхождения, а слово без ударения сверяется по
    каждому фрагменту (op «cases» в omogre). Поэтому знаки снимаются уже здесь, внутри
    моста, а не только на выходе конвертера.
    """

    pieces = _split(text)
    changed = False
    for index, _ in _word_indices(pieces):
        span = _idiom_span(pieces, index)
        if span is None:
            continue
        for position in span:
            cleaned = pieces[position].replace(ACCENT, "")
            changed = changed or cleaned != pieces[position]
            pieces[position] = cleaned
    return "".join(pieces) if changed else text


def _idiom_override(word: str) -> Optional[str]:
    """Ответ для слова, которое в книге бывает частью фразеологизма, иначе None.

    Хосту слово возвращается уже готовым вариантом, и он идёт во все вхождения,
    без контекста. Поэтому для такого слова ответ — **без ударения**: дальше его
    разбирает omogre по предложению (op «cases»), и внутри фразы оно получает
    мала́/велика́, а в именительном — обычное ма́ла/вели́ка.
    """
    if not word:
        return None
    plain = _plain(word)
    if plain in {form for pair in IDIOMS for form in pair}:
        return "".join(ch for ch in word if ch != ACCENT)
    return None

ACCENT = "\u0301"


def _respond(payload) -> None:
    """Write a single protocol line to the host over the real stdout pipe."""
    _REAL_STDOUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    _REAL_STDOUT.flush()


def _log(message) -> None:
    """Write a diagnostic line to stderr (always drained by the host)."""
    try:
        sys.stderr.write(str(message) + "\n")
        sys.stderr.flush()
    except Exception:
        pass


class StressAnchor:
    def __init__(self, base_dir: str):
        self._stresser = None
        self._error = None
        try:
            from russian_text_stresser.text_stresser import RussianTextStresser

            db_path = os.path.join(base_dir, "russian_dict.db")
            simple_cases = os.path.join(base_dir, "simple_cases.pkl")
            if not os.path.exists(simple_cases):
                simple_cases = None
            self._stresser = RussianTextStresser(
                db_file=db_path,
                simple_cases_file=simple_cases,
                use_large_model=False,
            )
            _log("add-stress-anchor: готово")
        except Exception as error:
            self._error = f"{type(error).__name__}: {error}"
            _log(f"add-stress-anchor: ошибка инициализации: {self._error}\n{traceback.format_exc()}")

    def accent_word(self, word: str) -> str:
        self.last_error = None
        override = _idiom_override(word)
        if override is not None:
            return override
        if self._stresser is None:
            return word
        try:
            # Слово проверяется одно, без фразы вокруг: здесь защищает только то, что
            # _idiom_override выше уже отдал ответ для слова из фразеологизма.
            return self._stresser.stress_text(word) or word
        except Exception:
            return word

    def accent_text(self, text: str) -> str:
        self.last_error = None
        if not text or not text.strip():
            return text
        if self._stresser is None:
            return protect_idioms(text)
        try:
            # Снимать знаки с фразы надо и здесь: слово, полученное с ударением, уходит
            # во все вхождения (см. protect_idioms).
            return protect_idioms(self._stresser.stress_text(text))
        except Exception as error:
            _log(f"add-stress-anchor: ошибка обработки: {error}")
            return text


def _handle_request(anchor: StressAnchor, request: dict) -> dict:
    op = request.get("op", "text")
    if op == "text":
        return {"text": anchor.accent_text(request.get("text", ""))}
    if op == "words":
        items = request.get("items", [])
        results = []
        for item in items:
            word = item.get("word", "")
            results.append({"word": word, "accented": anchor.accent_word(word)})
        return {"results": results}
    return {"error": f"unknown op: {op}"}


def _parse_base_dir() -> str:
    base_dir = os.environ.get("DSR_STRESS_ANCHOR_DIR", os.path.dirname(os.path.abspath(__file__)))
    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--base-dir" and i + 1 < len(args):
            base_dir = args[i + 1]
            i += 2
            continue
        i += 1
    return base_dir


def main() -> None:
    base_dir = _parse_base_dir()

    # Make the stresser module importable and report a clean error if it is not.
    sys.path.insert(0, base_dir)
    for candidate in (os.path.dirname(base_dir), os.getcwd()):
        if candidate not in sys.path:
            sys.path.insert(0, candidate)

    anchor = StressAnchor(base_dir)
    if anchor._error is None:
        _respond({"ready": True, "error": None})
    else:
        _respond({"ready": False, "error": anchor._error})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
            response = _handle_request(anchor, request)
        except Exception as error:
            response = {
                "error": f"{type(error).__name__}: {error}",
                "traceback": traceback.format_exc(),
            }
        _respond(response)


if __name__ == "__main__":
    main()
