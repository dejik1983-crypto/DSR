#!/usr/bin/env python3
"""Persistent JSON-lines bridge to silero-stress (MIT, https://github.com/snakers4/silero-stress).

Second accent engine for DSR, used in two ways:

* as an independent second opinion for the words RUAccent and the bundled stress
  dictionary left unresolved (`{"op": "words", ...}`) — the answers are suggestions
  for the human, exactly like the language-model pass;
* as the accent engine of the conversion itself (text nodes in, accented text out),
  so a book can be processed by silero-stress instead of RUAccent.

Differences from RUAccent that the rest of DSR depends on:

* the library marks a stressed vowel with '+' before it ('Мен+я'); DSR keeps the
  combining acute U+0301 after the vowel, so the marks are moved here;
* ё is written by the letter itself, never with an extra acute — DSR treats such a
  word as accented already;
* single-vowel words are left unaccented on purpose: DSR deliberately does not
  annotate them, the position of the stress in them is unambiguous;
* the library wants a sentence with a little context, so a context window around the
  word is cut out of the fragment instead of feeding the whole paragraph.
"""
import argparse
import contextlib
import io
import json
import re
import sys
import time
import traceback

WORD_RE = re.compile(r"[\wЀ-ӿЁ-џ̀-ͯ]+(?:[-''][\wЀ-ӿЁ-џ̀-ͯ]+)*", re.UNICODE)
STRESS_RE = re.compile(r"\+")
ACCENT = "\u0301"

# Слова, которым ударение не ставится никогда: короткие предлоги, союзы, частицы.
# Список продублирован в Program.cs — при изменении поправить оба места.
UNSTRESSED_WORDS = (
    "в", "во", "к", "ко", "на", "надо", "не", "ни", "но", "о", "об", "обо", "от", "ото", "по", "под", "подо",
    "при", "про", "с", "со", "у", "из", "изо", "за", "и", "а", "но", "же", "бы", "ли", "быть",
)

# Контекст вокруг слова: silero-stress рассчитан на предложение плюс небольшой
# контекст, а не на целую главу, поэтому окно ограничено.
CONTEXT_RADIUS = 320


def _emit(line: str) -> None:
    sys.stderr.write(line + "\n")
    sys.stderr.flush()


def _normalize(text: str) -> str:
    """Регистр и е/ё не должны влиять на сравнение букв."""
    return text.replace(ACCENT, "").replace("+", "").casefold().replace("ё", "е")


def _letters(word: str) -> str:
    return _normalize(word)


def to_dsr_marks(text: str) -> str:
    """Переносит знак '+' перед гласной в combining acute после неё."""
    out = []
    plus_pending = False
    for ch in text:
        if ch == "+":
            plus_pending = True
            continue
        if plus_pending and ch.isalpha():
            out.append(ch)
            # Знак ставится только после гласной, и никогда после ё: буква ё сама ударение несёт,
            # а DSR такой знак отвергает при проверке правки.
            if ch.lower() in "аеиоуыэюя":
                out.append(ACCENT)
            plus_pending = False
            continue
        plus_pending = False
        out.append(ch)
    return "".join(out)


class SileroAccentor:
    """Обёртка над silero_stress: модель грузится один раз, ответы кэшируются."""

    def __init__(self, device: str):
        from silero_stress import load_accentor

        # Библиотека ждёт идентификатор torch: «cpu» или «cuda:0», а не «CPU».
        wanted = "cuda:0" if device.upper().startswith("CUDA") else "cpu"
        self._device = wanted
        started = time.perf_counter()
        self._accentor = load_accentor()
        try:
            self._accentor.to(device=wanted)
        except Exception as error:  # CPU-режим должен работать всегда
            _emit(f"silero-stress: устройство {wanted} недоступно ({error}), работаю на cpu")
            self._accentor.to(device="cpu")
            self._device = "cpu"
        self._loaded = time.perf_counter() - started
        self._cache = {}
        self._hits = 0

    @property
    def device(self) -> str:
        return self._device

    def accent_text(self, text: str) -> str:
        """Расставляет знаки в готовом тексте узла (режим конвертации)."""
        if not text or not text.strip():
            return text
        result = self._accentor(text, put_stress=True, put_yo=True,
                                put_stress_homo=True, put_yo_homo=True,
                                stress_single_vowel=False,
                                words_to_ignore=list(UNSTRESSED_WORDS))
        return self._drop_stress_from_ignored(to_dsr_marks(result))

    @staticmethod
    def _drop_stress_from_ignored(text: str) -> str:
        """Снимает знаки со слов, которым ударение не ставится никогда.

        words_to_ignore у silero сравнивает слово буква в букву, поэтому «Вы» оно
        пропускает. Список коротких слов закрывается тут, без всякой зависимости
        от того, как именно библиотека сравнивает регистр.
        """
        ignored = {_letters(item) for item in UNSTRESSED_WORDS}
        return WORD_RE.sub(
            lambda match: match.group(0).replace(ACCENT, "") if _letters(match.group(0)) in ignored else match.group(0),
            text)

    def suggest(self, word: str, contexts) -> str:
        """Предлагает написание с ударением по контексту; '' — если ответа нет."""
        cached = self._cache.get(word)
        if cached is not None:
            self._hits += 1
            return cached
        answer = self._suggest_uncached(word, contexts)
        self._cache[word] = answer
        return answer

    def _suggest_uncached(self, word: str, contexts) -> str:
        if not word:
            return ""
        target = _letters(word)
        if _letters(word) in {_letters(item) for item in UNSTRESSED_WORDS}:
            return ""
        variants = []
        for context in contexts or []:
            if not isinstance(context, str) or not context.strip():
                continue
            window = self._context_window(context, word)
            if not window:
                continue
            try:
                result = self._accentor(window, put_stress=True, put_yo=True,
                                        put_stress_homo=True, put_yo_homo=True,
                                        stress_single_vowel=False,
                                        words_to_ignore=list(UNSTRESSED_WORDS))
                result = self._drop_stress_from_ignored(to_dsr_marks(result))
            except Exception as error:
                _emit(f"silero-stress: ошибка на «{word}»: {error}")
                continue
            found = self._pick_variant(to_dsr_marks(result), word, target)
            if found:
                variants.append(found)
            if len(variants) >= 2:
                break
        if not variants:
            return ""
        if len(set(variants)) == 1:
            return variants[0]
        # Контексты противоречат друг другу — молчим, решение принимает человек.
        return ""

    @staticmethod
    def _context_window(context: str, word: str):
        """Вырезает предложение вокруг вхождения слова."""
        low = context.casefold()
        position = low.find(word.casefold())
        if position < 0:
            # Слово может стоять с отличающейся буквой (о/а, ё) — ищем по началу.
            stem = word.casefold()[:max(3, len(word) - 2)]
            position = low.find(stem)
            if position < 0:
                return None
        start = max(0, position - CONTEXT_RADIUS)
        end = min(len(context), position + len(word) + CONTEXT_RADIUS)
        window = context[start:end].strip()
        return window or None

    @staticmethod
    def _pick_variant(marked: str, word: str, target: str):
        """Ищет в размеченном тексте то же слово и возвращает его написание."""
        for match in WORD_RE.finditer(marked):
            candidate = match.group(0)
            if _letters(candidate) != target:
                continue
            if "+" in candidate or ACCENT in candidate:
                return candidate.replace("+", "")
            if "ё" in candidate.casefold():
                return candidate.replace("+", "")
            return ""
        return ""


def resolve_words(accentor, payload):
    """Второе мнение по словам: подсказки и список без ответа."""
    items = payload.get("items") or []
    started = time.perf_counter()
    suggestions = {}
    rejected = []
    done = 0
    for item in items:
        word = str((item or {}).get("word", "")).strip()
        if not word:
            continue
        contexts = (item or {}).get("contexts") or []
        done += 1
        # Ход работы приложение показывает само: оно знает, сколько слов осталось
        # во всей книге, а не только в этой пачке.
        answer = accentor.suggest(word, contexts)
        if answer:
            suggestions[word] = answer
        else:
            rejected.append({"word": word, "raw": "", "reason": "silero не дал однозначного ответа"})
    _emit(f"DSR_PERF:silero:words={done}:suggestions={len(suggestions)}:seconds={time.perf_counter() - started:.3f}:cache={len(accentor._cache)}:hits={accentor._hits}")
    return {"suggestions": suggestions, "rejected": rejected}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", choices=("CPU", "CUDA"), default="CPU")
    args = parser.parse_args()

    try:
        # Импорт torch печатает предупреждения — stdout занят протоколом DSR.
        with contextlib.redirect_stdout(io.StringIO()):
            accentor = SileroAccentor(args.device)
        _emit(f"DSR_DEVICE:{accentor.device}")
        _emit(f"silero-stress загружен за {accentor._loaded:.1f} с; устройство {accentor.device}")
        print("READY", flush=True)
    except Exception as exc:
        print("ERROR: " + str(exc), flush=True)
        traceback.print_exc(file=sys.stderr)
        return 1

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            payload = json.loads(line)
        except Exception as exc:
            print(json.dumps({"error": str(exc)}, ensure_ascii=False), flush=True)
            continue
        try:
            if isinstance(payload, dict):
                if payload.get("op") == "words":
                    response = resolve_words(accentor, payload)
                else:
                    response = {"error": f"неизвестная операция: {payload.get('op')!r}"}
            else:
                text = "\n".join(str(item) for item in payload if isinstance(item, str))
                response = {"text": accentor.accent_text(text)}
        except Exception as exc:
            traceback.print_exc(file=sys.stderr)
            response = {"error": str(exc)}
        print(json.dumps(response, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())