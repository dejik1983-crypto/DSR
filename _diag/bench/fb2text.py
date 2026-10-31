# -*- coding: utf-8 -*-
"""Чтение текста FB2 в том же порядке, в каком его видит конвертер DSR.

Зачем: бенчмарк сравнивает четыре файла одного прогона (база, RUAccent, Silero,
итог). Если раскладка разойдётся, сравнивать будет нечего: словарь уедет на одну
позицию и «ошибкой» станет всё подряд. Поэтому текст берём только из текстовых
узлов, которые DSR и обрабатывает (список — TextElements в Program.cs), а служебную
разметку (картинки, ссылки, сноски) выкидываем.

Модуль намеренно без зависимостей: элемент Expat из стандартной библиотеки.
"""
import re
import xml.etree.ElementTree as ET

# Те же элементы, что Program.TextElements: текст из них получает ударения.
TEXT_ELEMENTS = {
    "p", "v", "subtitle", "text-author", "date", "book-title", "first-name",
    "middle-name", "last-name", "nickname", "home-page", "email", "genre",
    "sequence", "annotation", "poem", "stanza", "cite", "epigraph", "empty-line",
    "thesis", "src-title", "match",
}
# Внутри этих элементов текст — это только текст (сноски, картинки и т.п. пропускаем).
SKIP_ELEMENTS = {"binary", "image", "stylesheet", "script"}
ACUTE = "\u0301"


def _local(tag: str) -> str:
    """XML-тег без пространства имён: '{http://...}p' -> 'p'."""
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


class Segment:
    """Непрерывный кусок текста: узел FB2 и вложенность (например, эпиграф → стих)."""

    __slots__ = ("path", "text")

    def __init__(self, path, text):
        self.path = path
        self.text = text

    def __repr__(self):
        return f"Segment({self.path!r}, {self.text[:40]!r})"


def read_segments(path):
    """Возвращает список Segment: по одному на текстовый узел FB2.

    Текст узла склеивается из всех его частей (элементы могут быть перемешаны с
    <a>, <emphasis>): "слово <a>1</a> ещё" даёт "слово ещё", как в конвертере.
    """
    tree = ET.parse(path)
    root = tree.getroot()
    segments = []

    def walk(element, path_parts):
        name = _local(element.tag)
        if name in SKIP_ELEMENTS:
            return
        in_text = name in TEXT_ELEMENTS
        own = []
        if element.text:
            own.append(element.text)
        for child in element:
            child_name = _local(child.tag)
            if child_name in SKIP_ELEMENTS:
                # Ссылка/картинка/бинарь: текст атомарный, внутрь не лезем.
                if child.tail:
                    own.append(child.tail)
                continue
            if in_text and child_name not in TEXT_ELEMENTS:
                # Атомарный элемент с вложенной разметкой (<emphasis>, <strong>,
                # <a>, <image>): разметку выкидываем, текст склеиваем. Иначе из-за
                # ссылки на сноску «ендовы <a>1</a>, камышистая» распалось бы на
                # несколько сегментов, а номера сносок попали бы в текст.
                own.append("".join(child.itertext()))
                if child.tail:
                    own.append(child.tail)
                continue
            # Текстовый элемент-обёртка (стих, эпиграф, цитата…): разрезаем на
            # сегменты, чтобы сохранить путь и порядок, как у конвертера.
            if own:
                segments.append(Segment("/".join(path_parts), "".join(own).strip()))
                own = []
            walk(child, path_parts + [child_name])
            if child.tail:
                own.append(child.tail)
        if own:
            segments.append(Segment("/".join(path_parts), "".join(own).strip()))

    bodies = root.findall("{http://www.gribuser.ru/xml/fictionbook/2.0}body")
    for body in bodies:
        walk(body, [])
    return [segment for segment in segments if segment.text]


# ---------------- слова и знаки ударения ----------------

# Слово — ровно как Program.WordTokens в DSR: буквы, знак ударения (комбинирующий)
# и цифры. Дефис словом не считается: «и́з-под» в DSR — два слова.
#
# Но совпадение должно начинаться с БУКВЫ: без этого правила знак ударения в начале
# слога сам становится началом слова, и «а́» с предыдущим пробелом уезжает в слово.
VOWEL = "аеёиоуыэюяАЕЁИОУЫЭЮЯ"
# В шаблоне есть \u0300-\u036f (комбинирующие знаки), и Python считает их началом
# слова: «мала́» расчленилось бы на «мала» и «́». Поэтому первым стоит *буква*,
# а знаки и цифры — только продолжение. Множество знаков при этом то же, что у
# Program.WordTokens в DSR, а дефис словом не считается.
WORD_RE = re.compile(r"[^\W\d_][\w\u0300-\u036f]*", re.UNICODE)


def words(text):
    """Список (слово, позиция гласной с ударением или -1) в порядке текста.

    Делим по тем же словам, что DSR (Program.WordTokens: буквы, комбинирующие знаки
    и цифры в одном слове), поэтому номера слов совпадают с номерами конвертера.

    Цифры тоже слово («1914»), просто без гласных.
    """
    return [(match.group(0), stress_index(match.group(0))) for match in WORD_RE.finditer(text)]


def stress_index(word):
    """Номер гласной (0-based) со знаком U+0301, иначе -1.

    Знак стоит ПОСЛЕ гласной: «ию́ньским» = и, ю, U+0301. Поэтому ищем запись
    «гласная + U+0301» и запоминаем номер этой гласной.

    Слово должно начинаться с буквы; если разбор начался со знака ударения
    (бывает, если текст уже частично размечен), считаем слово безударным.
    """
    if not word or word[0] == ACUTE:
        return -1
    vowel_index = -1
    for char in word:
        if char in VOWEL:
            vowel_index += 1
        elif char == ACUTE:
            return vowel_index
    return -1


def letters(word):
    """Слово без знака ударения и в нижнем регистре — ключ для сравнения написаний."""
    return word.replace(ACUTE, "").lower()


def yo_index(word):
    """Номер гласной, на которой стоит ё, иначе -1 (ё всегда ударная)."""
    vowel_index = -1
    for char in letters(word):
        if char in "аеёиоуыэюя":
            vowel_index += 1
        if char == "ё":
            return vowel_index
    return -1
