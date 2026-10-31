#!/usr/bin/env python3
"""Persistent JSON-lines bridge to omogre (https://github.com/omogr/omogre).

Третий движок ударений DSR. В отличие от RUAccent и silero-stress, omogre —
контекстная модель омографов: она выбирает ударение по предложению, а не по
слову в отрыве от него, поэтому именно ей отдаётся разбор спорных случаев
(там, где RUAccent и silero разошлись).

Что важно для остальных частей DSR:

* библиотека ставит знак '+' *перед* ударной гласной («гр+уди»); DSR держит
  combining acute U+0301 *после* гласной, поэтому знак переносится здесь;
* буква ё сама несёт ударение и никогда не получает лишний знак — DSR отвергает
  правку с таким знаком при проверке;
* фраза длиннее окна модели (~510 токенов) роняет разбор: библиотека не берёт её
  целиком и на своём же пределе падает — «assert False» или NameError, у которого
  пустое сообщение; поэтому на вход идёт предложение вокруг вхождения, а если оно
  не помещается — кусок фразы со словом посередине (см. context_window);
* библиотека умеет отвечать по каждому вхождению отдельно (op «cases»): так
  омограф разбирается не словом целиком, а по предложению — «в этих случаях
  зам+ок, а в этих з+амок». Именно этот режим использует страница «Контроль ИИ»;
* op «words» (один ответ на слово) оставлен для командной строки и теста движка:
  там, где контексты расходятся, берётся вариант, встретившийся чаще;
* кроме контекстной модели у omogre есть вторая модель — «неизвестных слов»: она
  ставит ударение в именах и словах, которых нет в словаре. Библиотека зовёт её
  по ОДНОМУ слову за раз, без контекста, и на книге это тысячи мелких прогонов
  сети (именно они и держали процессор, пока видеокарта спала). Здесь она
  считается пачками слов и с кэшем: ответ зависит только от слова, поэтому
  результат ровно тот же, а прогонов сети — единицы.
"""
import argparse
import contextlib
import io
import json
import re
import sys
import time
import traceback

# То же слово, но с '+' внутри: знак стоит перед ударной гласной.
WORD_RE = re.compile(r"[\wЀ-ӿЁ-џ̀-ͯ+]+(?:[-'’][\wЀ-ӿЁ-џ̀-ͯ+]+)*", re.UNICODE)
ACCENT = "\u0301"

# Окно вокруг вхождения слова: у модели лимит ~510 токенов на строку, а на вход
# идёт одна фраза. Больше 2000 символов для русского — это уже за пределом.
CONTEXT_RADIUS = 600
MAX_CONTEXT_CHARS = 2000

# Предел окна модели (omogre.accentuator.reader.MAX_SENTENCE_LEN) и запас под
# разбор на токены: каждое слово — не меньше двух токенов (начало и конец), знаки
# препинания — ещё токены, а [CLS]/[SEP] библиотека прибавляет сама.
MAX_MODEL_TOKENS = 510
TOKEN_MARGIN = 32
SENTENCE_END = ".!?…"
PUNCTUATION = set(SENTENCE_END + ";:\n\r()«»\"—–-,")
# Проверка «поместится ли фраза» однажды уже опоздала: окно в 2000 символов
# проходило проверку по буквам, а библиотека падала на границе батча, потому что
# в ней считается по-другому (лимит × число фраз минус один токен). Поэтому
# около лимита остаётся запас не в один токен, а в несколько фраз.
REQUEST_MARGIN = 6

# Сколько токенов слов отдавать модели неизвестных слов за один прогон. У неё
# свой предел — 6000 токенов на батч (unk_reader.MAX_BATCH_TOKEN_NUM), берём с
# запасом: слова короткие, и один прогон на сотни слов работает на порядок
# быстрее, чем прогон на каждое слово.
UNK_BATCH_TOKENS = 4000


def _emit(line: str) -> None:
    sys.stderr.write(line + "\n")
    sys.stderr.flush()


def _normalize(text: str) -> str:
    """Регистр и е/ё не должны влиять на сравнение букв."""
    return text.replace(ACCENT, "").replace("+", "").casefold().replace("ё", "е")


def to_dsr_marks(token: str) -> str:
    """Переносит знак '+' перед гласной в combining acute после неё."""
    out = []
    plus_pending = False
    for ch in token:
        if ch == "+":
            plus_pending = True
            continue
        if plus_pending and ch.isalpha():
            out.append(ch)
            # Знак ставится только после гласной и никогда после ё: буква ё сама
            # несёт ударение, а DSR такой знак отвергает при проверке правки.
            if ch.lower() in "аеиоуыэюя":
                out.append(ACCENT)
            plus_pending = False
            continue
        plus_pending = False
        out.append(ch)
    return "".join(out)


def _cuda_unavailable_reason() -> str:
    """Почему видеокарту использовать нельзя. Пустая строка — можно."""
    try:
        import torch
    except Exception as error:  # сломанная сборка PyTorch
        return f"PyTorch не загрузился: {error}"
    try:
        if not torch.version.cuda:
            return "сборка PyTorch поставлена без поддержки CUDA"
        if not torch.cuda.is_available():
            return "видеокарта NVIDIA не отвечает: драйвер, разрядность или занятая карта"
        return ""
    except Exception as error:
        return str(error)


def _describe(error: BaseException) -> str:
    """Причина обычными словами: у assert и NameError в самой библиотеке сообщения нет.

    Пустое сообщение и было настоящей поломкой: пользователь видел «omogre не смог
    разобрать контекст: » и не понимал, что случилось. Теперь причина называется сама.
    """
    text = str(error).strip()
    name = type(error).__name__
    if not text:
        if name == "NameError":
            return "в библиотеке omogre не разобрано слишком длинное предложение"
        if name == "AssertionError":
            return "фраза не поместилась в пакет библиотеки omogre (слишком длинное предложение)"
        return f"неизвестная ошибка библиотеки {name} без описания"
    return f"{name}: {text}"


def _token_count(accentor, text: str) -> int:
    """Сколько токенов модель получит за эту фразу.

    Считает сам omogre: у него буквы и знаки разбираются в токены, а [CLS] и
    [SEP] тоже занимают место. Если разбор не удался, считаем по худшему
    случаю — два токена на букву и токен на каждый знак, плюс два служебных:
    такая оценка сужает окно сильнее, но длинное не пропускает.
    """
    try:
        ids, _, _ = accentor._accentuator.accentuator.tokenizer.get_inf_tokens(text)
        return len(ids)
    except Exception:
        return 2 * sum(1 for char in text if char.isalpha()) + sum(
            1 for char in text if char in PUNCTUATION) + 2


def _fits(accentor, text: str) -> bool:
    return _token_count(accentor, text) + REQUEST_MARGIN < MAX_MODEL_TOKENS


def _split_sentence(text: str):
    """Делит фразу на предложения — так, как их видит правило длины в самой библиотеке.

    Библиотека режет фразу на «мягкие» (всё размечается словарём) и «модельные»
    (есть слова с вариантами ударения). Модельных за один вызов должно быть
    меньше десятка — иначе она падает на границе пакета, а если среди них есть
    длинное предложение — с пустой ошибкой. Поэтому фраза разрезается заранее:
    по концу предложения, по длинной паузе из нескольких пробелов и по пределу
    длины куска.
    """
    pieces = []
    current = ""
    index = 0
    while index < len(text):
        char = text[index]
        current += char
        # Конец предложения: знак, за которым пробел или начало следующего.
        if char in SENTENCE_END:
            after = index + 1
            while after < len(text) and text[after] in "\"»)..":
                current += text[after]
                after += 1
            # Пробел ПОСЛЕ точки — часть той же фразы: иначе каждый следующий
            # кусок начинается с пробела, и « ".join(...)» ломает порядок слов
            # в запросе — позиции в ответе разъезжаются с позициями запроса.
            while after < len(text) and text[after] in " \t":
                current += text[after]
                after += 1
            pieces.append(current)
            current = ""
            index = after
            continue
        # Длинная пауза из нескольких пробелов тоже отделяет один кусок от другого.
        if char in " \t" and len(current) > 200:
            run = index
            while run < len(text) and text[run] in " \t":
                run += 1
            if run - index >= 2:
                pieces.append(current)
                current = ""
                index = run
                continue
        index += 1
    if current:
        pieces.append(current)
    return [piece for piece in pieces if piece.strip()]


def _split_long_piece(piece: str, limit: int):
    """Делит слишком длинный кусок по словам, чтобы каждый был не длиннее limit.

    Склейка обратно — тем же разделителем « », поэтому каждый кусок заканчивается
    пробелом: иначе при склейке слова соседних кусков слиплись бы («Доном.Атаман»),
    и позиции слов в ответе разъехались бы со словами запроса.
    """
    parts = []
    current = ""
    for word in piece.split(" "):
        candidate = word if not current else current + " " + word
        if current and len(candidate) > limit:
            parts.append(current)
            current = word
        else:
            current = candidate
    if current:
        parts.append(current)
    if not parts:
        return [piece]
    # Каждый кусок, кроме последнего, заканчивается пробелом: так склейка
    # " ".join(...) ничего не теряет и не слипает слова на границе.
    return [part.rstrip() + " " for part in parts[:-1]] + [parts[-1]]


def _window_around(context: str, word: str, size: int) -> str:
    """Кусок фразы размером около size символов, но со словом не на самом краю.

    Так слово остаётся в середине, и модель видит слева и справа примерно
    одинаковый контекст, даже если предложение в книге оказалось длиннее окна.
    """
    low = context.casefold()
    position = low.find(word.casefold())
    if position < 0:
        stem = word.casefold()[:max(3, len(word) - 2)]
        position = low.find(stem) if stem else -1
    if position < 0:
        return context[:size]
    start = max(0, position - size // 3)
    end = min(len(context), start + size)
    # Не режем по полуслову, если рядом есть пробел или знак препинания.
    if start > 0:
        while start < position and context[start] not in " \n\r\t" and not context[start] in PUNCTUATION:
            start += 1
    if end < len(context):
        while end > position + len(word) and context[end - 1] not in " \n\r\t" and context[end - 1] not in PUNCTUATION:
            end -= 1
    return context[start:end]


def _pick_variant(marked: str, word: str) -> str:
    """Ищет в размеченном предложении то же слово и возвращает его с ударением."""
    target = _normalize(word)
    for match in WORD_RE.finditer(marked):
        candidate = match.group(0)
        if _normalize(candidate) != target:
            # Составное слово через дефис: «К+аменка-Струм+илово» для «струмилово».
            # Ударение внутри нужной части — годится, берём именно её.
            part = next((piece for piece in re.split("[-'’]", candidate) if _normalize(piece) == target), None)
            if part is None:
                continue
            candidate = part
        if "+" in candidate or ACCENT in candidate or "ё" in candidate.casefold():
            result = to_dsr_marks(candidate.replace(ACCENT, ""))
            accents = result.count(ACCENT)
            # Годится форма ровно с одним знаком (или «ё» без знака). Разметка с
            # несколькими '+' или с '+' перед согласной — не ответ: честно
            # возвращаем «нет ответа», а не слово без ударения.
            if accents == 1 or (accents == 0 and "ё" in result.casefold()):
                return result
            return ""
        # Слово найдено, но знака у него нет (односложное или короткое) — ответа нет.
        return ""
    return ""


class OmogreAccentor:
    """Обёртка над omogre: модель грузится один раз на весь сеанс."""

    def __init__(self, data_path: str, device: str):
        from omogre import Accentuator

        wanted = "cuda" if device.upper().startswith("CUDA") else "cpu"
        started = time.perf_counter()
        if wanted == "cuda":
            # Видеокарта нужна, но её может не быть: сборка PyTorch бывает без CUDA, драйвер
            # устареть, а карта — быть занятой. Тогда сразу считаем на процессоре и говорим
            # об этом в строке хода работы: молчаливый откат выглядел бы как «CUDA не помогла».
            reason = _cuda_unavailable_reason()
            if reason:
                _emit(f"DSR_PROGRESS:omogre: видеокарта недоступна ({reason}), считаю на процессоре")
                wanted = "cpu"
        try:
            self._accentuator = Accentuator(data_path=data_path, download=True, device_name=wanted)
            self._device = wanted
        except Exception as error:  # CPU-режим должен работать всегда
            _emit(f"DSR_PROGRESS:omogre: устройство {wanted} не подошло ({error}), считаю на процессоре")
            self._accentuator = Accentuator(data_path=data_path, download=True, device_name="cpu")
            self._device = "cpu"
        self._loaded = time.perf_counter() - started
        # Ответы модели неизвестных слов на весь сеанс: слово → позиция ударения.
        self._unk_cache = {}
        self._unk_cached_words = 0
        self._unk_cached_runs = 0
        self._install_unknown_word_cache()
        # Размер куска фразы, который точно укладывается в окно модели (считается
        # один раз, при первом длинном контексте).
        self._safe_chars = None

    @property
    def device(self) -> str:
        return self._device

    @staticmethod
    def context_window(context: str, word: str, accentor=None):
        """Вырезает предложение вокруг вхождения слова, если контекст длинный.

        Главное — уложиться в окно модели (~510 токенов): фразу длиннее этого
        библиотека целиком не берёт, а на своём пределе падает с пустой ошибкой
        («omogre не смог разобрать контекст: »). Поэтому окно проверяется по
        токенам (счёт берётся у самого omogre), а не по числу символов, и режется
        до тех пор, пока кусок не помещается.
        """
        if len(context) <= MAX_CONTEXT_CHARS:
            return context
        low = context.casefold()
        position = low.find(word.casefold())
        if position < 0:
            stem = word.casefold()[:max(3, len(word) - 2)]
            position = low.find(stem)
        if position < 0:
            window = context[:MAX_CONTEXT_CHARS]
        else:
            start = max(0, position - CONTEXT_RADIUS)
            end = min(len(context), position + len(word) + CONTEXT_RADIUS)
            window = context[start:end]
        if accentor is None:
            return window
        # Предел по токенам: счёт ведётся у самого omogre — только по укороченному
        # куску, потому что разбор на токены всей фразы в 6 000 символов стоит
        # больше, чем сам ответ модели.
        safe = accentor._safe_window_chars()
        if len(window) > safe:
            window = _window_around(window, word, safe)
        for _ in range(24):
            if _fits(accentor, window):
                return window
            window = _window_around(window, word, max(len(word) + 40, int(len(window) * 0.8)))
            if len(window) <= len(word) + 40:
                break
        # Совсем короткий кусок: одна фраза-абзац без точек (например, длинная
        # цитата без знаков) — оставляем модель без контекста, но с самим словом.
        return _window_around(window, word, max(len(word), 80))

    def _safe_window_chars(self) -> int:
        """Сколько символов фразы заведомо укладывается в окно модели.

        Считается один раз на сеанс, на обычной русской фразе (длинные слова:
        в длинном слове токенов больше всего), с двойным запасом: точный счёт
        всё равно делается по настоящему окну перед разбором.
        """
        if self._safe_chars is None:
            safe = 120
            low, high = 120, 2000
            while low <= high:
                middle = (low + high) // 2
                if _fits(self, "становление " * (middle // 12)):
                    safe = middle
                    low = middle + 1
                else:
                    high = middle - 1
            self._safe_chars = max(80, safe // 2)
        return self._safe_chars

    def accentuate_many(self, contexts):
        """Размечает список фраз и склеивает ответ обратно по фразам.

        Каждая фраза режется на предложения и разбирается отдельно, поэтому
        длинная или слишком пёстрая фраза не отменяет весь разбор (раньше одна
        такая фраза давала «omogre не смог разобрать контекст: » на всю книгу).
        """
        if not contexts:
            return []
        self.precompute_unknown_words(list(contexts))
        limit = self._safe_window_chars()
        marked = []
        for text in contexts:
            pieces = []
            for sentence in _split_sentence(text):
                pieces.extend(_split_long_piece(sentence, limit))
            marked.append("".join(self._accentuate_pieces(pieces)))
        return marked

    def _accentuate_pieces(self, pieces):
        """Разметка кусков: одной пачкой, а если библиотека не справилась — по одному."""
        if not pieces:
            return []
        try:
            out = self._accentuator.accentuate(list(pieces))
        except Exception as error:
            # Причину называем по-человечески: у assert False и NameError в самой
            # библиотеке сообщение пустое, и пользователь видел «…: » без слов.
            _emit(f"DSR_PROGRESS:omogre: пачка фраз не разобрана ({_describe(error)}), разбираю по одной")
            return [self._accentuate_piece(piece, error) for piece in pieces]
        if isinstance(out, str):
            return [out]
        return list(out)

    def _accentuate_piece(self, text, batch_error):
        """Ответ на один кусок: если и он не подошёл — пустая разметка и причина."""
        try:
            result = self._accentuator.accentuate([text])
            if isinstance(result, str):
                return result
            return result[0] if result else ""
        except Exception as error:
            _emit(f"omogre: кусок пропущен ({_describe(error)}; в пачке — {_describe(batch_error)}): {text[:80]!r}")
            return ""

    def _install_unknown_word_cache(self):
        """Запоминает ответы модели неизвестных слов (слово → позиция ударения).

        Слово в этой модели идёт без контекста, значит один и тот же ответ не
        нужно считать второй раз: имена собственные и редкие слова повторяются в
        книге десятки раз, а каждый прогон сети по одному слову стоит столько же,
        сколько прогон по сотне слов.
        """
        unk = self._accentuator.accentuator.unk_model
        original = unk.get_acc_pos
        cache = self._unk_cache

        def cached_get_acc_pos(unk_word):
            key = unk_word if isinstance(unk_word, str) else str(unk_word)
            if key in cache:
                return cache[key]
            answer = original(key)
            cache[key] = answer
            return answer

        unk.get_acc_pos = cached_get_acc_pos

    def precompute_unknown_words(self, contexts):
        """Считает пачками слова, которых нет в словаре ударений.

        Библиотека сама этого не делает: она зовёт модель неизвестных слов на
        каждое слово отдельно, и на книге это тысячи прогонов сети по одному
        слову — время уходит на запуск сети, а не на счёт. Здесь слова собираются
        заранее (тем же разбором на слова, что и внутри omogre) и отправляются
        одним прогоном на несколько сотен слов.
        """
        try:
            words = self._unknown_words_of(contexts)
        except Exception as error:  # не удалось — работает обычный путь omogre
            _emit(f"omogre: общий батч слов пропущен ({error})")
            return
        words = [word for word in words if word not in self._unk_cache]
        if not words:
            return
        started = time.perf_counter()
        answers, runs = self._run_unknown_batches(words)
        self._unk_cached_words += answers
        self._unk_cached_runs += runs
        if runs:
            # Та же строка, что и отчёт по случаям: она видна в журнале прогона,
            # поэтому по ней легко проверить, что пачки слов действительно идут.
            _emit("DSR_PERF:omogre:unk_words=%d:unk_runs=%d:unk_seconds=%.3f"
                  % (answers, runs, time.perf_counter() - started))

    def _unknown_words_of(self, contexts):
        """Слова фраз, которые omogre отдаст модели неизвестных слов.

        Разбор на слова берётся у самого omogre (AccentDocument): так границы слов
        совпадают с внутренними, и лишнего в ответе не появляется — непонятые
        слова просто пройдут обычным путём библиотеки.
        """
        from omogre.accentuator.model import check_ee_comu, count_vowels, norm_word
        from omogre.accentuator.reader import AccentDocument

        model = self._accentuator.accentuator
        try:
            document = AccentDocument(model.tokenizer, list(contexts))
        except Exception:
            # Длинная фраза роняет и этот разбор (библиотека зовёт свою версию
            # get_batches с ошибкой), а ответ модели про неизвестные слова нужен
            # не любой ценой: без пачки слов библиотека просто спросит их по одной.
            document = None
        if document is None:
            return []
        vocabulary = model.tokenizer.accent_vocab.vocab
        variants = model.tokenizer.accent_vocab.vocab_index

        entries = list(document.easy_sentences)
        for sentence_spans, _, _ in document.model_batches:
            entries.extend(sentence_spans)

        words = []
        seen = set()
        for doc_pos, all_spans in entries:
            sentence = document.all_sentences[doc_pos]
            for (first, last), _ in all_spans:
                word = sentence[first:last]
                # Слова с ё и односложные (без гласных) решаются без модели.
                if check_ee_comu(word) >= 0 or count_vowels(word) < 1:
                    continue
                # Если у слова есть варианты ударения, отвечает словарь или
                # контекстная модель — модель неизвестных слов его не увидит.
                entry = vocabulary.get(norm_word(word))
                if entry is not None and len(variants[entry]) > 0:
                    continue
                key = word.casefold()
                if key in seen or key in self._unk_cache:
                    continue
                seen.add(key)
                words.append(key)
        return words

    def _run_unknown_batches(self, words):
        """Раскладывает слова по прогонам сети и возвращает (слов, прогонов)."""
        unk = self._accentuator.accentuator.unk_model
        todo = []
        for word in words:
            try:
                position = unk.unk_vocab.get_acc_pos(word)
            except Exception:
                position = -1
            if position >= 0:  # словарь отвечает сам, сеть не нужна
                self._unk_cache[word] = position
            else:
                todo.append(word)

        runs = 0
        pending = todo
        # Вторая попытка — страховка: если пачка почему-то не вместила все слова,
        # остаток всё равно считается пачкой, а не по одному слову.
        for _ in range(2):
            if not pending:
                break
            for chunk in self._split_unknown_chunks(pending):
                self._run_unknown_batch(chunk)
                runs += 1
            pending = [word for word in pending if word not in self._unk_cache]
        return len(words) - len(pending), runs

    @staticmethod
    def _split_unknown_chunks(words):
        """Делит слова на пачки по тому же счёту, что и в самом unk_reader.

        unk_reader берёт из пачки только то, что укладывается в её предел
        (длина самого длинного слова × число слов ≤ 6000), а остальное молча
        теряет. Поэтому предел считается здесь по этому же правилу, с запасом.
        """
        chunk = []
        longest = 1
        for word in words:
            size = len(word) + 2  # как в get_tokens: знак начала и знак конца
            new_longest = max(longest, size)
            if chunk and new_longest * (1 + len(chunk)) > UNK_BATCH_TOKENS:
                yield chunk
                chunk = []
                new_longest = size
            chunk.append(word)
            longest = new_longest
        if chunk:
            yield chunk

    def _run_unknown_batch(self, words):
        """Один прогон сети на пачку слов; ответы кладутся в кэш."""
        from omogre.accentuator.unk_reader import InfBatchFromSentenceList

        unk = self._accentuator.accentuator.unk_model
        try:
            reader = InfBatchFromSentenceList(list(words))
            batch = reader.get_next_batch()
        except Exception as error:
            _emit(f"omogre: пачка слов не собралась ({error})")
            return
        if batch is None:
            return
        result = []
        unk.process_model_batch(batch[0], batch[1], batch[2], result)
        for entry in result:
            if not isinstance(entry, tuple):
                continue  # слишком короткое слово — библиотека отвечает текстом
            word, position = entry
            if word not in self._unk_cache:
                self._unk_cache[word] = position


def resolve_words(accentor: OmogreAccentor, payload):
    """Разбор спорных слов: по одному ответу на слово и список без ответа."""
    items = payload.get("items") or []
    started = time.perf_counter()
    # Все уникальные контексты размечаются одним вызовом: так модель считает
    # контекст один раз, даже если он общий у нескольких слов.
    unique = []
    index = {}
    for item in items:
        for context in (item or {}).get("contexts") or []:
            if not isinstance(context, str) or not context.strip():
                continue
            window = OmogreAccentor.context_window(context, str((item or {}).get("word", "")), accentor)
            if window not in index:
                index[window] = len(unique)
                unique.append(window)
    try:
        marked = accentor.accentuate_many(unique)
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        return {"error": f"omogre не смог разобрать контекст: {_describe(error)}"}

    suggestions = {}
    rejected = []
    done = 0
    for item in items:
        word = str((item or {}).get("word", "")).strip()
        if not word:
            continue
        done += 1
        variants = []
        for context in (item or {}).get("contexts") or []:
            if not isinstance(context, str) or not context.strip():
                continue
            window = OmogreAccentor.context_window(context, word, accentor)
            position = index.get(window)
            if position is None or position >= len(marked):
                continue
            found = _pick_variant(marked[position], word)
            if found:
                variants.append(found)
        if not variants:
            rejected.append({"word": word, "raw": "", "reason": "omogre не дал однозначного ответа"})
            continue
        # Расхождение контекстов решается большинством; при равенстве берём первый.
        best = max(dict.fromkeys(variants), key=variants.count)
        suggestions[word] = best
    _emit("DSR_PERF:omogre:words=%d:suggestions=%d:seconds=%.3f" %
          (done, len(suggestions), time.perf_counter() - started))
    return {"suggestions": suggestions, "rejected": rejected}


def resolve_cases(accentor: OmogreAccentor, payload):
    """Разбор по каждому вхождению отдельно: один ответ на случай, а не на слово.

    Именно это и есть работа «по контексту»: одна и та же словоформа может
    прозвучать по-разному в разных предложениях, поэтому ответ хранится за
    конкретным случаем (word + occurrence), и страница контроля раскладывает
    случаи по двум вариантам ударения.
    """
    items = payload.get("items") or []
    started = time.perf_counter()
    unique = []
    index = {}
    for item in items:
        context = str((item or {}).get("context", ""))
        if not context.strip():
            continue
        window = OmogreAccentor.context_window(context, str((item or {}).get("word", "")), accentor)
        if window not in index:
            index[window] = len(unique)
            unique.append(window)
    try:
        marked = accentor.accentuate_many(unique)
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        return {"error": f"omogre не смог разобрать контекст: {_describe(error)}"}

    cases = []
    rejected = []
    done = 0
    for item in items:
        word = str((item or {}).get("word", "")).strip()
        if not word:
            continue
        done += 1
        try:
            occurrence = int((item or {}).get("occurrence", 0) or 0)
        except (TypeError, ValueError):
            occurrence = 0
        context = str((item or {}).get("context", ""))
        window = OmogreAccentor.context_window(context, word, accentor)
        position = index.get(window)
        found = ""
        if position is not None and position < len(marked):
            found = _pick_variant(marked[position], word)
        if found:
            cases.append({"id": (item or {}).get("id"), "word": word, "occurrence": occurrence, "form": found})
        else:
            rejected.append({"id": (item or {}).get("id"), "word": word, "occurrence": occurrence,
                             "reason": "omogre не дал ответа для этого случая"})
    _emit("DSR_PERF:omogre:cases=%d:answers=%d:seconds=%.3f" %
          (done, len(cases), time.perf_counter() - started))
    return {"cases": cases, "rejected": rejected}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-path", required=True)
    parser.add_argument("--device", choices=("CPU", "CUDA"), default="CPU")
    args = parser.parse_args()

    try:
        # Импорт torch печатает предупреждения — stdout занят протоколом DSR.
        with contextlib.redirect_stdout(io.StringIO()):
            accentor = OmogreAccentor(args.data_path, args.device)
        _emit(f"DSR_DEVICE:{accentor.device}")
        _emit(f"omogre загружен за {accentor._loaded:.1f} с; устройство {accentor.device}")
        # Устройство идёт в самой строке готовности: так оно не зависит от того, успел ли
        # приёмник разобрать строку DSR_DEVICE в stderr к моменту чтения READY.
        print("READY:" + accentor.device, flush=True)
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
            if isinstance(payload, dict) and payload.get("op") == "words":
                response = resolve_words(accentor, payload)
            elif isinstance(payload, dict) and payload.get("op") == "cases":
                response = resolve_cases(accentor, payload)
            else:
                response = {"error": f"неизвестная операция: {payload.get('op')!r}"}
        except Exception as exc:
            traceback.print_exc(file=sys.stderr)
            response = {"error": str(exc)}
        print(json.dumps(response, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
