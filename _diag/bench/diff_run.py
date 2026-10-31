# -*- coding: utf-8 -*-
"""Что движки отвечают по-разному: без эталона, по словам.

Это не замер точности (его даёт bench.py с эталоном), а карта расхождений: где
движки спорят между собой, где один промолчал, где они согласны. Позволяет быстро
увидеть, сколько в книге слов каждого типа и в каком классе ситуаций они сидят.
"""
import argparse
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench import find_run_files, sentences_of  # noqa: E402
from fb2text import letters, words  # noqa: E402

WORDS_ONLY = len(" ")  # маркер: ниже используется как замена пустой строки


def flat_words(path):
    """Все слова книги по порядку: (слово, позиция ударения)."""
    result = []
    for _, _, text in sentences_of(path):
        result.extend(words(text))
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--limit", type=int, default=0, help="только первые N слов (0 — все)")
    args = parser.parse_args()

    files = find_run_files(args.root)
    print("файлы:", {name: os.path.basename(path) for name, path in files.items()})
    data = {name: flat_words(path) for name, path in files.items()}

    base = data["база"]
    if args.limit:
        data = {name: value[: args.limit] for name, value in data.items()}
        base = data["база"]

    for name, value in data.items():
        if len(value) != len(base):
            print(f"! {name}: слов {len(value)}, у базы {len(base)} — сравнение остановлено")
            return 1

    stats = Counter()
    examples = {}
    for index in range(len(base)):
        forms = {name: value[index] for name, value in data.items()}
        letters_set = {letters(form) for form, _ in forms.values()}
        if len(letters_set) != 1:
            stats["написание разъехалось"] += 1
            examples.setdefault("написание", (index, forms))
            continue
        stresses = {name: stress for name, (_, stress) in forms.items()}
        if stresses["база"] == stresses["ruaccent"] == stresses["silero"] == stresses["итог"]:
            stats["все согласны"] += 1
            continue
        key = f"база={stresses['база']} ru={stresses['ruaccent']} si={stresses['silero']} итог={stresses['итог']}"
        stats[key] += 1
        examples.setdefault(key, (index, forms))

    total = len(base)
    print(f"\nвсего слов: {total}")
    for key, count in stats.most_common(12):
        print(f"{count:7d}  {count / total * 100:5.1f}%  {key}")
        if key in examples:
            _, forms = examples[key]
            print("           пример:", {n: f for n, (f, _) in forms.items()})
    if "" in stats:
        print("  (в примере ниже ударение без знака = нет ударения)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
