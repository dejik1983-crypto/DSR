#!/usr/bin/env python3
"""Persistent JSON-lines bridge to Natasha + accent dictionary.

Used as a "anchor" (high-precision reference) for cross-checking RUAccent/Silero.
For each word it tries to resolve stress via:
  1. morphology (natasha) -> part of speech / case / number
  2. accent dictionary lookup (word + POS) -> stressed form

If no dictionary entry is found, the word is returned unchanged (no guess).
This makes the worker conservative: it only answers when it is confident.
"""
import json
import os
import re
import sys
import traceback

ACCENT = "\u0301"
WORD_RE = re.compile(r"[\w\u0400-\u052f\u0300-\u036f]+(?:[-'’][\w\u0400-\u052f\u0300-\u036f]+)*", re.UNICODE)


def _emit(line: str) -> None:
    sys.stderr.write(line + "\n")
    sys.stderr.flush()


def _load_dictionary(path: str) -> dict:
    """Load simple TSV/word list from a file or directory."""
    result = {}
    if not path or not os.path.exists(path):
        return result
    files = []
    if os.path.isdir(path):
        for name in os.listdir(path):
            if name.lower().endswith((".txt", ".tsv")):
                files.append(os.path.join(path, name))
    else:
        files = [path]
    for filepath in files:
        with open(filepath, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split("\t")
                if len(parts) == 1:
                    word = parts[0]
                    if "ё" in word.lower():
                        result[word.replace("ё", "е").replace("Ё", "Е").casefold()] = word
                elif len(parts) >= 2:
                    key = parts[0].replace("ё", "е").replace("Ё", "Е").casefold()
                    result[key] = parts[1]
    return result


def _normalize(text: str) -> str:
    return text.replace(ACCENT, "").casefold().replace("ё", "е")


class NatashaAccentor:
    def __init__(self, dictionary_path: str):
        self._dict = _load_dictionary(dictionary_path)
        self._morph = None
        try:
            from natasha import MorphVocab, Segmenter
            self._segmenter = Segmenter()
            self._morph_vocab = MorphVocab()
            self._has_natasha = True
        except Exception as error:
            _emit(f"natasha недоступна: {error}")
            self._has_natasha = False
            self._segmenter = None
            self._morph_vocab = None

    def _pos_for(self, token_text: str) -> str:
        if not self._has_natasha:
            return ""
        try:
            from natasha import Doc
            doc = Doc(token_text)
            doc.segment(self._segmenter)
            doc.tag_morph(self._morph_vocab)
            for t in doc.tokens:
                if t.text.lower() == token_text.lower():
                    return getattr(t, "pos", "") or ""
        except Exception:
            pass
        return ""

    def accent_word(self, word: str) -> str:
        """Return stressed form if found in dictionary, else original word."""
        key = _normalize(word)
        # Direct dictionary hit (ignoring POS) — conservative but useful.
        if key in self._dict:
            stressed = self._dict[key]
            # Preserve capitalization of source.
            if word and word[0].isupper() and stressed and stressed[0].islower():
                stressed = stressed[0].upper() + stressed[1:]
            return stressed
        return word

    def accent_text(self, text: str) -> str:
        if not text or not text.strip():
            return text
        # Replace each word token using dictionary; keep punctuation.
        def _repl(m: re.Match) -> str:
            w = m.group(0)
            return self.accent_word(w)
        return WORD_RE.sub(_repl, text)


def _handle_request(accentor: NatashaAccentor, request: dict) -> dict:
    op = request.get("op", "text")
    if op == "text":
        text = request.get("text", "")
        return {"text": accentor.accent_text(text)}
    if op == "words":
        items = request.get("items", [])
        results = []
        for item in items:
            word = item.get("word", "")
            results.append({"word": word, "accented": accentor.accent_word(word)})
        return {"results": results}
    return {"error": f"unknown op: {op}"}


def main() -> None:
    dictionary_path = os.environ.get("DSR_NATASHA_DICTIONARY", "")
    # Also accept --dictionary
    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--dictionary" and i + 1 < len(args):
            dictionary_path = args[i + 1]
            i += 2
            continue
        i += 1
    accentor = NatashaAccentor(dictionary_path)
    _emit(f"natasha-stress: готово, словарь {len(accentor._dict)} записей")
    # Ready signal
    print(json.dumps({"ready": True, "dictionary_size": len(accentor._dict)}), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
            response = _handle_request(accentor, request)
        except Exception as error:
            response = {"error": f"{type(error).__name__}: {error}", "traceback": traceback.format_exc()}
        print(json.dumps(response, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
