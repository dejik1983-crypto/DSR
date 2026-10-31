# -*- coding: utf-8 -*-
"""Черновик эталона (для проверки человеком) из «итога» прогона.

Почему так, а не «вручную с нуля»: ставить десятки ударений в предложении руками
быстро становится источником ошибок — легче проверить чужое, чем набрать своё.
«Итог» — это то, что уже поступило в производство, то есть самый вероятный
кандидат в норму. Но эталон, сделанный из проверяемого, проверяет сам себя, поэтому:

- берём сумму в той же «сделке», где была наша заведомая ошибка (три идиомы),
  и маленький контрольный набор обычных предложений впереди;
- значения по-настоящему утверждает человек: скрипт печатает предложения по
  одному и спрашивает подтверждение (`Enter`) или правку (`2=2`), или `s` —
  выкинуть из эталона. Только после этого файл считается эталоном.
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench import GOLD_DIR, apply_answers, sentences_of, words  # noqa: E402


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", required=True, help="файл FB2, откуда брать текст")
    parser.add_argument("--name", required=True, help="имя эталона")
    parser.add_argument("--from", dest="first", type=int, default=0, help="с какого предложения")
    parser.add_argument("--count", type=int, default=30)
    parser.add_argument("--noninteractive", action="store_true",
                        help="сразу записать как есть (только для проверки инструмента!)")
    args = parser.parse_args()

    os.makedirs(GOLD_DIR, exist_ok=True)
    items = sentences_of(args.src)
    chosen = list(range(args.first, min(args.first + args.count, len(items))))
    rows = []
    for number in chosen:
        text = items[number][2]
        if args.noninteractive:
            rows.append({"id": len(rows), "text": text})
            continue
        print()
        print(f"[{len(rows) + 1}] {text}")
        print("   слова:", "  ".join(f"{i}:{word}" for i, (word, _) in enumerate(words(text), 1)))
        answer = input("   Enter — верно; 2=2 ... — правка; s — выкинуть: ").strip()
        if answer.lower() == "s":
            continue
        rows.append({"id": len(rows), "text": apply_answers(text, answer) if answer else text})
    path = os.path.join(GOLD_DIR, args.name + ".jsonl")
    with open(path, "w", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + "\n")
    print(f"\nэталон: {path} ({len(rows)} предложений)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
