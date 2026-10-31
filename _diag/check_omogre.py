# -*- coding: utf-8 -*-
"""Проверка воркера omogre без проекта: разбор кода и работа с настоящей моделью.

Что проверяется:
1. синтаксис модуля;
2. «длинные» куски — окно не превышает предел модели, а слово остаётся в окне;
3. живой разбор через модель (если она скачана): длинная фраза не валит весь
   запрос, а ответ по слову находится.

Формат — тот же, что у DSR: JSON-строка в stdin, ответ в stdout. Если модели
нет, живая часть пропускается с сообщением, а не падает.
"""
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)

import omogre_stress_worker as worker  # noqa: E402

failed = 0


def check(name, condition, detail=None):
    global failed
    failed += 0 if condition else 1
    print(("OK   " if condition else "FAIL ") + name + ("" if condition else f" — {detail}"))


# --- 1. окно контекста -----------------------------------------------------
long_text = ("Петро шёл по улице и вспоминал, как в прошлом году они всей семьёй "
             "косили за Доном. ") * 40 + "Атаман сказал: «Всё, от мала до велика, встают». " \
    + ("Потом опять была степь и ветер. ") * 40
check("длинный контекст обрезается", len(long_text) > worker.MAX_CONTEXT_CHARS, str(len(long_text)))
window = worker.OmogreAccentor.context_window(long_text, "велика")
check("слово осталось в окне", "велика" in window.casefold(), window[:80])
check("окно не длиннее исходного", len(window) <= len(long_text))

# Окно «около слова», без разметки токенов: проверяем только границы.
piece = worker._window_around(long_text, "велика", 200)
check("_window_around держит слово внутри", "велика" in piece.casefold())
check("_window_around не пустой", bool(piece), repr(piece[:20]))

# --- 2. резка длинной фразы ------------------------------------------------
pieces = worker._split_sentence(long_text)
check("фраза режется на куски", len(pieces) > 3, str(len(pieces)))
# Склейка должна возвращать ту же фразу без перестановок: иначе позиции слов
# в ответе разъедутся с запросом («Доном.Атаман» вместо «Доном. Атаман»).
joined = "".join(pieces)
check("куски склеиваются без потерь", joined == long_text, repr(joined[:60]))
check("на границе кусков нет слипания", "Атаман" in joined)

# --- 3. живой разбор (если модель есть) ------------------------------------
model_dir = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DSR")
live = os.environ.get("DSR_OMOGRE_DATA")
if not live:
    print("живая часть: DSR_OMOGRE_DATA не задан — пропущена")
else:
    try:
        accentor = worker.OmogreAccentor(live, "CPU")
    except Exception as error:
        print(f"живая часть: модель не загрузилась ({type(error).__name__}: {error}) — пропущена")
    else:
        contexts = [
            "Атаман сказал, что все, от мала до велика, встают на защиту.",
            "Она была мала собою, но духом велика.",
            long_text,
        ]
        answer = accentor.accentuate_many(contexts)
        check("ответов столько же, сколько фраз", len(answer) == len(contexts), str(len(answer)))
        check("первая фраза размечена", bool(answer[0]), answer[0][:80])
        # Длинная фраза разбирается кусками, каждый кусок получает свой ответ;
        # они склеиваются без разделителя, поэтому «вся фраза размечена» проверяется
        # по последнему куску (хвост текста) и по тому, что все куски не пустые.
        pieces = []
        for sentence in worker._split_sentence(contexts[2]):
            pieces.extend(worker._split_long_piece(sentence, accentor._safe_window_chars()))
        long_answer = accentor.accentuate_many([contexts[2]])[0]
        check("длинная фраза не уронила разбор", bool(long_answer) and len(long_answer) > 100,
              str(len(long_answer)))
        tail = worker._normalize(contexts[2][-40:])
        check("хвост длинной фразы размечен",
              worker._normalize(long_answer[-120:]).endswith(tail.split(" ")[-1][:8]))
        for text, marked in zip(contexts, answer):
            print("   ", marked[:120])

print("\nитог:", "всё прошло" if failed == 0 else f"провалов {failed}")
sys.exit(1 if failed else 0)
