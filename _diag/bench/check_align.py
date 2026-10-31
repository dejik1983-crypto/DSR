# -*- coding: utf-8 -*-
"""Сверка раскладки: четыре файла одного прогона должны давать одинаковые слова.

Если разойдётся хоть одно слово, бенчмарк перестаёт быть замером точности и
превращается в замер «кто где сдвинулся». Скрипт говорит, где именно.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fb2text import letters, read_segments, words

ROOT = r"D:\Выгрузки\Шолохов Михаил Александрович\Тихий Дон\1\DSR_Тихий Дон"
FILES = {
    "база": os.path.join(ROOT, "add-stress", "Тихий Дон_база.fb2"),
    "ruaccent": os.path.join(ROOT, "RUAccent", "Тихий Дон_ruaccent_по_базе.fb2"),
    "silero": os.path.join(ROOT, "Silero", "Тихий Дон_silero_по_базе.fb2"),
    "итог": os.path.join(ROOT, "Тихий Дон_итог.fb2"),
}

loaded = {}
for name, path in FILES.items():
    segments = read_segments(path)
    flat = [letters(word) for segment in segments for word, _ in words(segment.text)]
    loaded[name] = flat
    print(f"{name:9s} сегментов={len(segments):5d}  слов={len(flat):7d}")

base = loaded["база"]
# Расхождение в написании — это ё/е («студёны» → «студены»): его поставили/сняли
# движки, это тоже результат, а не сдвиг. Сдвиг — когда буквы разошлись сверх ё/е
# или число слов не совпало: тогда позиции несравнимы и замер врёт.
for name, flat in loaded.items():
    if name == "база":
        continue
    if len(flat) != len(base):
        print(f"{name}: РАЗНОЕ ЧИСЛО СЛОВ ({len(flat)} против {len(base)}) — замер невозможен")
        continue
    yo = sum(1 for i in range(len(base)) if base[i] != flat[i] and base[i].replace("ё", "е") == flat[i].replace("ё", "е"))
    shift = [(i, base[i], flat[i]) for i in range(len(base))
             if base[i].replace("ё", "е") != flat[i].replace("ё", "е")]
    print(f"{name}: разница ё/е {yo}, реальных сдвигов {len(shift)}")
    for i, a, b in shift[:5]:
        print(f"   #{i}: {a} -> {b}")
    if shift:
        raise SystemExit(1)
