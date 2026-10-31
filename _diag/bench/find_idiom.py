# -*- coding: utf-8 -*-
"""Где в предложениях книги стоят наши фразеологизмы — и кто в них ошибся."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench import IDIOMS, find_run_files, sentences_of  # noqa: E402
from fb2text import letters, words  # noqa: E402

ROOT = r"D:\Выгрузки\Шолохов Михаил Александрович\Тихий Дон\1\DSR_Тихий Дон"
files = find_run_files(ROOT)
data = {name: sentences_of(path) for name, path in files.items()}

# Ищем пары НЕ рядом: во фразе между ними стоит предлог «до» — это тоже слово.
# Поэтому берём окно из трёх слов и смотрим на первое и третье.
for index, (_, _, base_text) in enumerate(data["база"]):
    tokens = [letters(word) for word, _ in words(base_text)]
    hit = None
    for start in range(len(tokens) - 2):
        if (tokens[start], tokens[start + 2]) in IDIOMS:
            hit = (start, start + 2)
            break
    if hit is None:
        continue
    start, end = hit
    print(f"#{index}: {' '.join(tokens[max(0, start - 3):end + 3])}")
    for name in files:
        _, _, text = data[name][index]
        forms = [form for form, _ in words(text)]
        # Предложение «базы» и предложение движка могут разойтись по числу слов
        # (так было до правок раскидки): тогда показываем, что есть.
        shown = " / ".join(forms[start:end + 1]) if len(forms) > end else f"(слов {len(forms)})"
        print(f"   {name:9s} {shown}")
