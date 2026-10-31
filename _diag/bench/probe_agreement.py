# -*- coding: utf-8 -*-
"""Сколько слов в книге вообще получили ударение — по каждому файлу прогона.

Это первая цифра, которая нужна: если 84 % слов «все согласны», надо понимать,
сколько из них согласны В ОТСУТСТВИИ знака (движки просто промолчали), а сколько —
с одинаковым ударением.
"""
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench import find_run_files, sentences_of  # noqa: E402
from fb2text import words  # noqa: E402

ROOT = r"D:\Выгрузки\Шолохов Михаил Александрович\Тихий Дон\1\DSR_Тихий Дон"


def flats(path):
    return [item for _, _, text in sentences_of(path) for item in words(text)]


files = find_run_files(ROOT)
data = {name: flats(path) for name, path in files.items()}
total = len(data["база"])
print("слов:", total)
for name, value in data.items():
    stressed = sum(1 for _, stress in value if stress >= 0)
    print(f"{name:9s} с ударением: {stressed:7d} ({stressed / total * 100:5.1f}%)")

none = 0
same = 0
for index in range(total):
    marks = [data[name][index][1] for name in data]
    if all(mark < 0 for mark in marks):
        none += 1
    elif len(set(marks)) == 1:
        same += 1
print(f"без ударения у всех: {none} ({none / total * 100:.1f}%)")
print(f"одинаково и есть знак: {same} ({same / total * 100:.1f}%)")
