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
        if self._stresser is None:
            return word
        try:
            result = self._stresser.stress_text(word)
            return result if result else word
        except Exception:
            return word

    def accent_text(self, text: str) -> str:
        if self._stresser is None or not text or not text.strip():
            return text
        try:
            return self._stresser.stress_text(text)
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
