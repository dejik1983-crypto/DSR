# -*- coding: utf-8 -*-
"""Проверка фразеологизмов в мосте add-stress: от мала до велика -> без ударения.

Строки заданы escape-последовательностями: в самом файле теста не должно быть ни
знака ударения литералом, ни лишних пробелов, которые может съесть редактор.
"""
import sys
sys.path.insert(0, r"F:\DeepReader\DeepReader DSR\DSR")
from add_stress_anchor import protect_idioms, _idiom_override

A = "\u0301"
MALA = "\u043c\u0430" + A + "\u043b\u0430"                # ма косая ла
VELIKA = "\u0432\u0435\u043b\u0438" + A + "\u043a\u0430"  # вел косая ка
SP = " "
cases = [
    # (вход, ожидание)
    ("\u043ce, " + "\u043e\u0442" + SP + MALA + SP + "\u0434\u043e" + SP + VELIKA + ".",
     "\u043ce, " + "\u043e\u0442" + SP + "\u043c\u0430\u043b\u0430" + SP + "\u0434\u043e" + SP + "\u0432\u0435\u043b\u0438\u043a\u0430" + "."),
    ("\u043e\u0442" + SP + MALA + SP + "\u0434\u043e" + SP + VELIKA,
     "\u043e\u0442" + SP + "\u043c\u0430\u043b\u0430" + SP + "\u0434\u043e" + SP + "\u0432\u0435\u043b\u0438\u043a\u0430"),
    # Слово вне фразы не трогаем.
    ("\u043c\u0430\u043b\u0430\u044f" + SP + "\u0438" + SP + "\u0431\u043e\u043b\u044c\u0448\u0430" + A + "\u044f",
     "\u043c\u0430\u043b\u0430\u044f" + SP + "\u0438" + SP + "\u0431\u043e\u043b\u044c\u0448\u0430" + A + "\u044f"),
]
failed = 0
for source, expected in cases:
    actual = protect_idioms(source)
    ok = actual == expected
    failed += 0 if ok else 1
    print(("OK   " if ok else "FAIL ") + source.encode("unicode_escape").decode()
          + " -> " + actual.encode("unicode_escape").decode())

# Слово из фразы хосту отдаётся без ударения: иначе ошибка разойдётся по всей книге.
for word, expected in [(MALA, "\u043c\u0430\u043b\u0430"),
                       ("\u041c\u0430" + A + "\u043b\u0430", "\u041c\u0430\u043b\u0430"),
                       (VELIKA, "\u0432\u0435\u043b\u0438\u043a\u0430"),
                       ("\u043c\u0430" + A + "\u043b\u044b\u043c", None)]:
    actual = _idiom_override(word)
    ok = actual == expected
    failed += 0 if ok else 1
    print(("OK   " if ok else "FAIL ") + "override " + word.encode("unicode_escape").decode()
          + " -> " + str(actual))

print("\u0438\u0442\u043e\u0433:",
      "\u0432\u0441\u0451 \u043f\u0440\u043e\u0448\u043b\u043e" if failed == 0 else str(failed) + " \u043f\u0440\u043e\u0432\u0430\u043b\u043e\u0432")
sys.exit(1 if failed else 0)
