# -*- coding: utf-8 -*-
"""Разрезка абзаца на предложения — минимальная единица контекста.

Правила проверялись на Тихом Доне (test_bench.py). Логика такая: кандидат на
конец — знак конца ( . ! ? … ), за ним возможны закрывающие кавычки и скобки.
Точка НЕ заканчивает предложение, если:

* перед ней сокращение («т. е.», «г.») или инициал («М. А. Шолохов»);
* за ней строчная буква («и ушёл. утром» — так не пишут, а «т. е. так» — пишут);
* это одна точка, за которой идёт ещё одна буква-инициал.

Многоточие и тире в реплике разрывают её только когда после тире начинается новая
реплика словами-атрибутами («— сказал он»), а не ремарка внутри («— Ну… — протянул
он» — это одно предложение, потому что перед тире было многоточие).
"""
import re

# Сокращения, после которых точка не завершает предложение. Список закрытый и
# намеренно короткий: ложный разрыв хуже, чем склеенные предложения (контекст
# длиннее не вредит, а разорванная рамка «от … до …» вредит).
ABBREVIATIONS = {
    "т", "е", "гг", "в", "вв", "с", "см", "др", "пр", "им", "ул", "д", "кв",
    "стр", "рис", "напр", "ок", "гр", "р", "руб", "коп", "тыс", "млн",
}

_END_RE = re.compile(r"[.!?…]+[\"'»”)]*")
_LAST_WORD_RE = re.compile(r"([\w-]+)$", re.UNICODE)


def _is_abbreviation(prefix: str) -> bool:
    match = _LAST_WORD_RE.search(prefix.strip())
    if not match:
        return False
    word = match.group(1).lower()
    if word == "т":  # «т. е.» и «т. д.» — сокращения, а одной «т.» в книге нет
        return True
    return word in ABBREVIATIONS


def _is_initial(prefix: str) -> bool:
    """Последнее слово — инициал: одна заглавная буква («М.»)."""
    match = _LAST_WORD_RE.search(prefix.strip())
    return bool(match and len(match.group(1)) == 1 and match.group(1).isupper())


def _initials_before(tail: str) -> bool:
    """За точкой идёт инициал: «…М. А. Шолохов»."""
    return bool(re.match(r"\s*([А-ЯЁA-Z])[\s.]", tail))


def split_sentences(text: str):
    """Список предложений. Пробелы в начале/конце обрезаются, пустые не отдаются."""
    text = text.strip()
    if not text:
        return []
    result = []
    start = 0
    for match in _END_RE.finditer(text):
        end = match.end()
        tail = text[end:]
        stop = text[start:match.start()]
        sign = match.group(0)
        if _is_abbreviation(stop):
            continue
        # Одна точка перед строчной буквой или инициалом — не конец предложения.
        if sign == "." and (tail[:1].islower() or _initials_before(tail)):
            continue
        # Инициал перед точкой: «М. А. Шолохов» — точка принадлежит инициалу, а не
        # концу фразы. Проверяем последнее слово: одна заглавная буква.
        if sign == "." and _is_initial(stop):
            continue
        # Многоточие с тире и ремаркой («— Ну… — протянул он») — одна реплика, если
        # после тире не начинается новый абзац реплики (новый абзац приходит отдельно).
        if sign.startswith("…") and tail.lstrip().startswith("—"):
            continue
        sentence = text[start:end].strip()
        if sentence:
            result.append(sentence)
        start = end
    rest = text[start:].strip()
    if rest:
        result.append(rest)
    return result
