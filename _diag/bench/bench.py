# -*- coding: utf-8 -*-
"""Этап 1: замер точности движков DSR на реальной книге.

Что делает
----------
1. Читает FB2 так же, как конвертер (fb2text.read_segments), режет текст на
   ПРЕДЛОЖЕНИЯ (sentences.py) — это будущая единица работы конвейера.
2. Строит эталон (gold.jsonl) из самого надёжного источника: интерактивно, по
   тексту, где ударений ещё нет, человек ставит их вручную. Автоматически эталон
   не создаётся: любая модель — это то, что мы проверяем.
3. Сравнивает каждый файл прогона (база / RUAccent / Silero / итог) с эталоном и
   делит ошибки на классы: падеж/согласование, идиома, омограф, ё, неизвестное
   слово, служебное.

Почему по предложениям, а не по словам
--------------------------------------
Ошибка «от ма́ла до вели́ка» по отдельным словам не видна: «ма́ла» и «вели́ка»
разобраны как правильные именительные. Видна она только в паре — поэтому эталон
хранит предложение целиком, а идиома проверяется на паре соседних слов.

Запуск
------
    python _diag/bench/bench.py --list "D:\\...\\DSR_Тихий Дон"   # какие файлы есть
    python _diag/bench/bench.py --gold duet-1 --make                # разметить эталон
    python _diag/bench/bench.py --gold duet-1 --score                # замерить

Эталон кладётся в _diag/bench/gold/<имя>.jsonl (по предложению в строке).
"""
import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fb2text import ACUTE, letters, read_segments, words  # noqa: E402
from sentences import split_sentences  # noqa: E402

# Слово для разметки: та же разбивка, что в fb2text.words.
WORD_ITER = re.compile(r"[^\W\d_][\w\u0300-\u036f]*", re.UNICODE)

HERE = os.path.dirname(os.path.abspath(__file__))
GOLD_DIR = os.path.join(HERE, "gold")
REPORT_DIR = os.path.join(HERE, "reports")

# Файлы одного прогона. Имена — как в DSR_<книга>.
RUN_FILES = (
    ("база", ("add-stress", "*_база.fb2")),
    ("ruaccent", ("RUAccent", "*_ruaccent_по_базе.fb2")),
    ("silero", ("Silero", "*_silero_по_базе.fb2")),
    ("итог", ("", "*_итог.fb2")),
)

# Идиомы, где ударение диктует фраза (Этап 6 плана). Служат меткой класса ошибки.
# Пары слов, где ударение диктует фраза. Служат только меткой класса ошибки.
IDIOMS = (
    ("мала", "велика"),
    ("малу", "велику"),
)


def find_run_files(root):
    """Находит четыре файла прогона в папке DSR_<книга>."""
    import glob
    found = {}
    for name, (subdir, pattern) in RUN_FILES:
        matches = glob.glob(os.path.join(root, subdir, pattern))
        if matches:
            found[name] = max(matches, key=os.path.getmtime)
    return found


def sentences_of(path):
    """Все предложения книги в порядке текста: список (path, номер, текст)."""
    result = []
    for segment in read_segments(path):
        for number, sentence in enumerate(split_sentences(segment.text)):
            result.append((segment.path, number, sentence))
    return result


# ---------------- эталон ----------------

def gold_path(name):
    return os.path.join(GOLD_DIR, name + ".jsonl")


def make_gold(name, source_path, limit_sentences, report=print):
    """Интерактивная разметка эталона.

    Формат строки: {"id": n, "text": "предложение с акутами"}.
    Ввод: печатается предложение без акутов и голые слова вроде
    «слово=2 слово=1» — номер гласной (с 1) для нужных слов. Слово без номера
    остаётся без ударения. Пустая строка — оставить предложение как есть.
    """
    os.makedirs(GOLD_DIR, exist_ok=True)
    source = [item for item in sentences_of(source_path)
              if len(words(item[2])) >= 2]
    if limit_sentences:
        source = source[:limit_sentences]
    rows = []
    total = len(source)
    for index, (_, _, text) in enumerate(source):
        report("")
        report(f"[{index + 1}/{total}] {text}")
        # Слова по номерам — разметчику проще, чем повторять каждое слово.
        report("  слова:")
        chunks = []
        for number, (word, _) in enumerate(words(text), start=1):
            chunks.append(f"{number}:{letters(word)}")
            if number % 8 == 0:
                report("    " + "  ".join(chunks))
                chunks = []
        if chunks:
            report("    " + "  ".join(chunks))
        answer = input("  ударения (номер=гласная; пусто — как есть; s — пропустить): ").strip()
        if answer.lower() == "s":
            continue
        accented = apply_answers(text, answer)
        rows.append({"id": len(rows), "text": accented})
    with open(gold_path(name), "w", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + "\n")
    report(f"эталон: {gold_path(name)} ({len(rows)} предложений)")
    return rows


def apply_answers(text, answer):
    """Ставит знаки по ответу разметчика: «слово=2» — ударение на 2-ю гласную."""
    # Ответ — по порядку слов: «мала=2 велика=2» ставит ударения на первое и второе
    # слово. Набор «слово=номер» не годился: одно написание повторяется во многих
    # местах, и «мала=2» переписало бы и «мала» из другого места предложения.
    marks = {}
    for chunk in answer.split():
        if "=" not in chunk:
            continue
        number, _, vowel = chunk.partition("=")
        if number.isdigit() and vowel.isdigit():
            marks[int(number)] = int(vowel) - 1
    pieces = []
    last = 0
    for number, match in enumerate(WORD_ITER.finditer(text), start=1):
        pieces.append(text[last:match.start()])
        raw = match.group(0)
        index = marks.get(number)
        pieces.append(insert_acute(raw, index) if index is not None else raw)
        last = match.end()
    pieces.append(text[last:])
    return "".join(pieces)


def insert_acute(word, vowel_index):
    """Ставит U+0301 после гласной с номером vowel_index (0-based)."""
    if vowel_index < 0:
        return word
    vowels = "аеёиоуыэюяАЕЁИОУЫЭЮЯ"
    seen = -1
    for position, char in enumerate(word):
        if char in vowels:
            seen += 1
            if seen == vowel_index:
                return word[:position + 1] + ACUTE + word[position + 1:]
    return word


def load_gold(name):
    path = gold_path(name)
    if not os.path.exists(path):
        raise SystemExit(f"нет эталона: {path} — сначала --make")
    with open(path, encoding="utf-8") as stream:
        return [json.loads(line) for line in stream if line.strip()]


# ---------------- сравнение ----------------

def classify(gold_text, got_text):
    """Класс ошибки для пары предложений: список (номер слова, класс, эталон, ответ)."""
    gold = words(gold_text)
    got = words(got_text)
    if len(gold) != len(got):
        return [(-1, "сдвиг", f"слов {len(gold)}", f"слов {len(got)}")]
    errors = []
    for index, ((gold_word, gold_stress), (got_word, got_stress)) in enumerate(zip(gold, got)):
        # «Сдвиг» — только когда текст действительно разъехался (лишнее слово,
        # другой корень). Разница ё/е — это класс «ё», а не сдвиг.
        if letters(gold_word).replace("ё", "е") != letters(got_word).replace("ё", "е"):
            errors.append((index, "сдвиг", gold_word, got_word))
            continue
        gold_has_yo = "ё" in gold_word.lower()
        got_has_yo = "ё" in got_word.lower()
        if gold_stress == got_stress and gold_has_yo == got_has_yo:
            continue
        # Ударение «сдвинулось» именно из-за буквы ё — это класс «ё»; всё остальное,
        # включая потерю ударения, — «ударение».
        if gold_has_yo != got_has_yo:
            errors.append((index, "ё", gold_word, got_word))
        else:
            errors.append((index, "ударение", gold_word, got_word))
    # Ошибки внутри фразеологических пар поднимаются в свой класс «идиома»: именно
    # их мы разбирали в 1.9.0, и в общем «ударении» они теряются. Пару ищем в
    # окне из трёх слов: во фразе между ними стоит предлог («мала до велика»).
    for left, right in IDIOMS:
        for start in range(len(gold) - 2):
            if (letters(gold[start][0]), letters(gold[start + 2][0])) != (left, right):
                continue
            inside = {start, start + 2}
            if not any(position in inside for position, kind, _, _ in errors
                       if kind in ("ударение", "ё")):
                continue
            errors = [(i, "идиома", g, o) if i in inside and kind in ("ударение", "ё")
                      else (i, kind, g, o) for (i, kind, g, o) in errors]
    return errors


def find_sentence(sentences, gold_text):
    """Номер предложения файла, совпадающего с эталоном по буквам.

    Эталон хранит знаки ударения, файл — свои, поэтому сравниваем без знаков и без
    различий ё/е: ударение — то, что мы и замеряем, оно не должно мешать поиску.
    Для идиомы «мала…велика» знаки в файле были, но буквы те же — найдётся.
    """
    wanted = normalize_for_search(gold_text)
    for index, text in enumerate(sentences):
        if normalize_for_search(text) == wanted:
            return index
    return None


def normalize_for_search(text):
    return " ".join(letters(word) for word, _ in words(text)).replace("ё", "е")


def score(gold_rows, got_sentences, label):
    """Сравнить ответ движка с эталоном; вернуть статистику и примеры ошибок."""
    stats = Counter()
    classes = Counter()
    examples = defaultdict(list)
    total_words = 0
    for row in gold_rows:
        # Предложение ищем по тексту, а не по номеру: любая правка разрезки или
        # вставка слова сдвигает нумерацию, и замер сравнивал бы разные места книги.
        position = find_sentence(got_sentences, row["text"])
        if position is None:
            stats["нет предложения"] += 1
            continue
        got_text = got_sentences[position]
        total_words += len(words(row["text"]))
        errors = classify(row["text"], got_text)
        if not errors:
            stats["верно"] += 1
            continue
        stats["с ошибками"] += 1
        for _position, kind, gold_word, got_word in errors:
            classes[kind] += 1
            if len(examples[kind]) < 5:
                examples[kind].append((row["text"], gold_word, got_word))
    return {
        "label": label,
        "sentences": len(gold_rows),
        "words": total_words,
        "exact": stats["верно"],
        "classes": dict(classes),
        "examples": {kind: value for kind, value in examples.items()},
    }


def format_report(results, gold_name):
    lines = [f"# Замер точности DSR — эталон «{gold_name}»", ""]
    header = "| движок | предложений | слов | точно | ошибок на 100 слов | " + \
             "ударение | ё | идиома | сдвиг |"
    lines.append(header)
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for result in results:
        words_count = result["words"] or 1
        error_words = sum(result["classes"].values())
        lines.append(
            f"| {result['label']} | {result['sentences']} | {result['words']} | "
            f"{result['exact']} | {error_words / words_count * 100:.1f} | "
            f"{result['classes'].get('ударение', 0)} | {result['classes'].get('ё', 0)} | "
            f"{result['classes'].get('идиома', 0)} | {result['classes'].get('сдвиг', 0)} |")
    lines.append("")
    for result in results:
        lines.append(f"## {result['label']}")
        if not result["examples"]:
            lines.append("Ошибок нет.")
        for kind, items in result["examples"].items():
            lines.append(f"### {kind}")
            for sentence, gold_word, got_word in items:
                lines.append(f"- `{sentence}`")
                lines.append(f"  - эталон: **{gold_word}** → получено: **{got_word}**")
        lines.append("")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", help="папка DSR_<книга> с файлами прогона")
    parser.add_argument("--output", default=os.path.join(REPORT_DIR, "report.md"),
                        help="куда записать отчёт (по умолчанию reports/report.md)")
    parser.add_argument("--source", help="файл для разметки эталона (--make)")
    parser.add_argument("--gold", help="имя эталона в _diag/bench/gold")
    parser.add_argument("--make", action="store_true", help="разметить эталон")
    parser.add_argument("--score", action="store_true", help="замерить файлы прогона")
    parser.add_argument("--limit", type=int, default=40, help="сколько предложений разметить")
    args = parser.parse_args()

    if args.make:
        if not args.gold or not args.source:
            raise SystemExit("--make требует --gold и --source")
        make_gold(args.gold, args.source, args.limit)
        return 0

    if args.score:
        if not args.gold or not args.root:
            raise SystemExit("--score требует --gold и --root")
        files = find_run_files(args.root)
        if not files:
            raise SystemExit(f"не найдены файлы прогона в {args.root}")
        gold_rows = load_gold(args.gold)
        results = []
        for name, path in files.items():
            # Сопоставляем по ТЕКСТУ, а не по номеру предложения: номер уехал бы от
            # любой правки разрезки, и замер сравнивал бы разные места книги.
            # Функция «поиск эталона в файле» отдаёт номер каждого предложения
            # в списке предложений файла (или -1).
            got = [text for _, _, text in sentences_of(path)]
            results.append(score(gold_rows, got, name))
        text = format_report(results, args.gold)
        os.makedirs(REPORT_DIR, exist_ok=True)
        report_path = args.output
        with open(report_path, "w", encoding="utf-8") as stream:
            stream.write(text)
        print(text)
        print(f"\nотчёт: {report_path}")
        return 0

    parser.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())
