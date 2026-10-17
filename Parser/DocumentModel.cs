using System.Collections.Generic;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace FB2AccentConverter.Parser;

public class DocumentModel
{
    public string? SourceFilePath { get; set; }
    public string BookTitle { get; set; } = "";
    public string BookAuthor { get; set; } = "";
    public List<DocumentSection> Sections { get; } = new();
        /// <summary>Содержимое реального основного FB2 body, без синтетической титульной страницы.</summary>
    public DocumentSection MainBodySection { get; } = new();

    /// <summary>
    /// Сноски книги. Ключ — id сноски из FB2, значение — DocumentModel с содержимым сноски.
    /// Хранится внутри DocumentModel для stateless-парсинга.
    /// </summary>
    public Dictionary<string, DocumentModel> Footnotes { get; } = new();
    /// <summary>
    /// Изображения книги.
    /// </summary>
    public List<ImageData> Images { get; } = new();
    public List<DocumentEditRecord> EditRecords { get; } = new();
}

public sealed class DocumentEditRecord
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string OriginalText { get; init; } = "";
    public string ReplacementText { get; set; } = "";
    public string TargetParagraphText { get; init; } = "";
    public int TargetOffset { get; init; } = -1;
    /// <summary>Абзац в тексте оригинала, сохранённый при первой правке этого абзаца.</summary>
    public string OriginalParagraphText { get; init; } = "";
    /// <summary>Абсолютная позиция изменённого фрагмента в тексте оригинала.</summary>
    public int OriginalOffset { get; init; } = -1;
    public bool IsTitleMetadataEdit { get; init; }
    /// <summary>Уникальный идентификатор записи для надёжной сериализации журнала.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}

public class DocumentSection
{
    public List<DocumentElement> Elements { get; } = new();

    /// <summary>
    /// Раздел построен парсером из <c>title-info</c> (название, автор, аннотация),
    /// а не взят из тела FB2. Правки в таком разделе должны уходить в метаданные,
    /// а не в текст книги.
    /// </summary>
    public bool IsSyntheticTitlePage { get; set; }
}

public abstract class DocumentElement
{
    public abstract ElementType Type { get; }
}

public enum ElementType
{
    Paragraph,
    Title,
    Subtitle,
    Poem,
    Image,
    EmptyLine,
    Citation,
    Epigraph
}

public class ParagraphElement : DocumentElement
{
    public override ElementType Type => ElementType.Paragraph;
    public List<InlineRun> Runs { get; } = new();
    public TextAlignment TextAlignment { get; set; } = TextAlignment.Justify;
    public bool IsFirstParagraphAfterHeading { get; set; }
}

public class TitleElement : DocumentElement
{
    public override ElementType Type => ElementType.Title;
    public List<InlineRun> Runs { get; } = new();
    public bool IsLarge { get; set; } = true;
}

public class SubtitleElement : DocumentElement
{
    public override ElementType Type => ElementType.Subtitle;
    public List<InlineRun> Runs { get; } = new();
}

public class SectionElement : DocumentElement
{
    public override ElementType Type => ElementType.Paragraph; // placeholder
    public DocumentSection Section { get; set; } = null!;
}

public class AuthorElement : DocumentElement
{
    public override ElementType Type => ElementType.Paragraph; // placeholder
    public List<InlineRun> Runs { get; set; } = new();
    public TextAlignment TextAlignment { get; set; } = TextAlignment.Right;
}

public class PoemElement : DocumentElement
{
    public override ElementType Type => ElementType.Poem;
    public List<List<List<InlineRun>>> Stanzas { get; } = new();
}

public class ImageElement : DocumentElement
{
    public override ElementType Type => ElementType.Image;
    public byte[]? Data { get; set; }

    /// <summary>
    /// Ссылка на ImageData для ленивой загрузки (используется для обложки).
    /// Если задана, приоритетнее чем Data.
    /// </summary>
    public ImageData? ImageDataRef { get; set; }

    /// <summary>
    /// Возвращает ключ для кэша изображений (MD5 для скорости).
    /// </summary>
    public string GetCacheKey()
    {
        // Если есть ссылка на ImageData, получаем данные из неё
        if (ImageDataRef != null)
        {
            var data = ImageDataRef.GetOrLoadData();
            if (data != null && data.Length > 0)
                return Convert.ToHexString(MD5.HashData(data));
        }
        if (Data != null && Data.Length > 0)
            return Convert.ToHexString(MD5.HashData(Data));
        return "";
    }
}

public class EmptyLineElement : DocumentElement
{
    public override ElementType Type => ElementType.EmptyLine;
}

public sealed class EditListElement : DocumentElement
{
    public override ElementType Type => ElementType.Title;
    public List<DocumentEditRecord> Records { get; init; } = new();
}

public class CitationElement : DocumentElement
{
    public override ElementType Type => ElementType.Citation;
    public List<DocumentElement> Elements { get; } = new();
}

public class EpigraphElement : DocumentElement
{
    public override ElementType Type => ElementType.Epigraph;
    public List<DocumentElement> Elements { get; } = new();
    public List<InlineRun>? Author { get; set; }
}

public class InlineRun
{
    public string Text { get; set; } = "";
    public InlineStyle Style { get; set; } = InlineStyle.Normal;
    public string? HyperlinkTarget { get; set; }
    public bool IsLineBreak { get; set; }
}

public enum InlineStyle
{
    Normal,
    Bold,
    Italic,
    Subscript,
    Superscript
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
    Justify
}

public class ImageData
{
    public string Id { get; set; } = "";
    public byte[]? Data { get; set; }

    /// <summary>
    /// Устаревшее: ссылка на узел XML. Хранить её опасно — она держит весь
    /// XDocument DOM в памяти, пока жива модель книги. Парсер больше не
    /// заполняет; оставлено для совместимости.
    /// </summary>
    public XElement? Element { get; set; }

    /// <summary>
    /// Base64-снимок содержимого &lt;binary&gt;, сделанный при парсинге. Декодируется
    /// лениво и сбрасывается после первого обращения — DOM исходника при этом
    /// освобождается сразу после Parse (раньше оставался живым на всю сессию).
    /// </summary>
    public string? Base64Data { get; set; }

    /// <summary>
    /// Лениво загружает данные изображения из XElement.
    /// </summary>
    public byte[] GetOrLoadData()
    {
        if (Data != null) return Data;
        if (Element != null)
        {
            try
            {
                Data = Convert.FromBase64String(Element.Value.Trim());
                Element = null; // освобождаем память после загрузки
                return Data;
            }
            catch
            {
                Data = Array.Empty<byte>();
                Element = null;
                return Data;
            }
        }
        if (Base64Data != null)
        {
            try
            {
                Data = Convert.FromBase64String(Base64Data.Trim());
            }
            catch
            {
                Data = Array.Empty<byte>();
            }
            Base64Data = null; // освобождаем память после загрузки
            return Data;
        }
        return Array.Empty<byte>();
    }
}

/// <summary>Применяет замену в уплощённом тексте источника, сохраняя структуру элементов и иллюстрации.</summary>
public static class DocumentTextEditor
{
    public static bool Replace(DocumentModel document, int start, int length, string replacement)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (start < 0 || length < 0) return false;
        var cursor = 0;
        return ReplaceInSections(document.Sections, ref cursor, start, length, replacement);
    }

    /// <summary>
    /// Найденный абзац модели вместе с координатами правки внутри него.
    /// Все позиции — в нормализованных координатах абзаца (пробельные
    /// последовательности схлопнуты в один пробел), то есть ровно в тех,
    /// которые понимает <see cref="Services.EditedFb2Writer"/>.
    /// </summary>
    public readonly struct ParagraphEdit
    {
        /// <summary>Индекс совпадения в нормализованном абзаце.</summary>
        public int MatchOffset { get; }
        public bool IncludesStressMark { get; }

        /// <summary>Список <see cref="InlineRun"/> абзаца для модификации.</summary>
        public IReadOnlyList<InlineRun> Runs { get; }

        /// <summary>Нормализованный текст абзаца (без пробельных последовательностей и ударений).</summary>
        public string ParagraphText { get; }

        /// <summary>Оригинал без пробелов и ударений — то, что искал пользователь.</summary>
        public string OriginalText { get; }

        /// <summary>Смещение <see cref="OriginalText"/> в <see cref="ParagraphText"/> (нормализованное).</summary>
        public int OriginalOffset { get; }

        /// <summary>Смещение первого заменяемого символа в нормализованном тексте абзаца.</summary>
        public int ReplacementStart { get; }

        /// <summary>Длина заменяемого фрагмента в нормализованном тексте.</summary>
        public int ReplacementLength { get; }

        /// <summary>Флаг: параграф построен из <c>title-info</c> (титульная страница).</summary>
        public bool IsTitlePage { get; }

        public ParagraphEdit(IReadOnlyList<InlineRun> runs, string paragraphText, string originalText, int originalOffset, bool isTitlePage, int matchOffset = -1, bool includesStressMark = false)
        {
            MatchOffset = matchOffset >= 0 ? matchOffset : originalOffset;
            IncludesStressMark = includesStressMark;
            Runs = runs;
            ParagraphText = paragraphText;
            OriginalText = originalText;
            OriginalOffset = originalOffset;
            ReplacementStart = originalOffset;
            ReplacementLength = originalText.Length;
            IsTitlePage = isTitlePage;
        }
    }

    /// <summary>
    /// Ищет абзац модели, соответствующий тексту блока виртуализированного
    /// движка, и положение в нём строки <paramref name="original"/>.
    /// <para>
    /// Плоский буфер нового движка (<c>BookModel.Text</c>) НЕ содержит переводов
    /// строк между абзацами, а его пробелы схлопнуты
    /// <c>TextNormalizer</c>-ом. Сырой текст модели устроен иначе, поэтому
    /// абсолютные смещения между двумя представлениями не совпадают и искать
    /// правку «по индексу» нельзя. Поиск идёт по тексту абзаца — он остаётся
    /// корректным при любом расхождении в разделителях.
    /// </para>
    /// </summary>
    /// <param name="paragraphHint">Текст блока книги, окружающий правку. Сужает
    /// поиск до одного абзаца и не даёт подменить другое вхождение того же слова.</param>
    /// <summary>
    /// Переводит сырое смещение в текстовом блоке в смещение нормализованного
    /// абзаца: схлопывает пробелы и игнорирует знаки ударения так же, как поиск.
    /// </summary>
    public static int GetNormalizedOffset(string text, int rawOffset)
    {
        ArgumentNullException.ThrowIfNull(text);
        rawOffset = Math.Clamp(rawOffset, 0, text.Length);
        int normalizedOffset = 0;
        bool pendingSpace = false;
        for (int i = 0; i < rawOffset; i++)
        {
            char character = text[i];
            if (IsInvisibleForMatching(character)) continue;
            if (char.IsWhiteSpace(character))
            {
                if (normalizedOffset > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                normalizedOffset++;
                pendingSpace = false;
            }
            normalizedOffset++;
        }

        // The target offset is the boundary before the next source character, so a
        // whitespace run immediately before it becomes one normalized separator.
        if (pendingSpace) normalizedOffset++;

        return normalizedOffset;
    }

    public static bool TryFindParagraph(
        DocumentModel document, string paragraphHint, string original, out ParagraphEdit found, int minimumOffset = 0, bool includeStressMark = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        found = default;
        if (string.IsNullOrEmpty(original)) return false;

        string wanted = includeStressMark ? original : Collapse(original);
        if (wanted.Length == 0) return false;
        minimumOffset = Math.Max(0, minimumOffset);

        // Сначала ищем только внутри подсказанного абзаца: так правка не уедет в
        // другое предложение с таким же словом.
        foreach (bool requireHint in new[] { true, false })
        {
            foreach (Candidate candidate in EnumerateCandidates(document))
            {
                string normalized = NormalizeRuns(candidate.Runs, out List<int> rawPositions, includeStressMark);
                if (normalized.Length == 0) continue;
                if (requireHint)
                {
                    if (string.IsNullOrEmpty(paragraphHint)) continue;
                    string hintKey = includeStressMark ? Collapse(paragraphHint, includeStressMark: true) : Collapse(paragraphHint);
                    if (!SameParagraph(normalized, hintKey)) continue;
                }

                // minimumOffset originates in the reader's flattened buffer. Its
                // coordinates can exceed this normalized model paragraph (for
                // example, when stress marks/whitespace differ between the two
                // representations). IndexOf(startIndex) throws when startIndex is
                // greater than Length; if no complete match can fit at that offset,
                // retry from the beginning of the already-matched paragraph.
                int lastPossibleStart = normalized.Length - wanted.Length;
                int searchStart = Math.Min(minimumOffset, normalized.Length);
                int position = normalized.IndexOf(wanted, searchStart, StringComparison.Ordinal);
                if (position < 0 && requireHint && minimumOffset > lastPossibleStart)
                    position = normalized.IndexOf(wanted, StringComparison.Ordinal);
                if (position < 0) continue;

                int basePosition = includeStressMark
                    ? NormalizeRuns(candidate.Runs, out _, includeStressMark: false).IndexOf(Collapse(original), StringComparison.Ordinal)
                    : position;
                found = new ParagraphEdit(candidate.Runs, normalized, wanted, basePosition < 0 ? position : basePosition, candidate.IsTitlePage, position, includeStressMark);
                return true;
            }

            if (!requireHint) break;
        }

        return false;
    }

    /// <summary>
    /// Выполняет замену в абзаце, найденном <see cref="TryFindParagraph"/>.
    /// </summary>
    public static bool ApplyEdit(DocumentModel document, ParagraphEdit edit, string replacement)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (edit.Runs is not List<InlineRun> runs) return false;
        if (edit.OriginalText.Length == 0) return false;

        string normalized = NormalizeRuns(runs, out List<int> rawPositions, edit.IncludesStressMark);
        int position = edit.MatchOffset;
        if (position < 0 || position + edit.OriginalText.Length > normalized.Length
            || !normalized.AsSpan(position, edit.OriginalText.Length).SequenceEqual(edit.OriginalText.AsSpan()))
            position = normalized.IndexOf(edit.OriginalText, StringComparison.Ordinal);
        if (position < 0) return false;

        int rawStart = rawPositions[position];
        int rawEnd = rawPositions[position + edit.OriginalText.Length - 1] + 1;
        // Stress marks are ignored while matching. Include marks attached just
        // outside the matched letters (notably a mark after the final vowel), so
        // replacing a word also replaces its old stress instead of leaving it
        // beside the newly inserted one.
        while (rawStart > 0 && IsInvisibleForMatching(RawCharacterAt(runs, rawStart - 1))) rawStart--;
        while (rawEnd < GetRawLength(runs) && IsInvisibleForMatching(RawCharacterAt(runs, rawEnd))) rawEnd++;
        int cursor = 0;
        return ReplaceRuns(runs, ref cursor, rawStart, rawEnd - rawStart, replacement);
    }

    private static int GetRawLength(IReadOnlyList<InlineRun> runs)
    {
        int length = 0;
        foreach (InlineRun run in runs)
            length += run.IsLineBreak ? 1 : run.Text.Length;
        return length;
    }

    private static char RawCharacterAt(IReadOnlyList<InlineRun> runs, int index)
    {
        foreach (InlineRun run in runs)
        {
            int runLength = run.IsLineBreak ? 1 : run.Text.Length;
            if (index < runLength) return run.IsLineBreak ? '\n' : run.Text[index];
            index -= runLength;
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>
    /// Приводит текст абзаца модели к виду, в котором его видит движок: любая
    /// последовательность пробельных символов становится одним пробелом, края
    /// обрезаются. Одновременно строится карта «позиция в нормализованном тексте
    /// → позиция в конкатенации runs», по которой правка возвращается в сырые
    /// координаты.
    /// <para>
    /// Знаки ударения из сравнения исключаются. В исходном FB2 они стоят перед
    /// ударной гласной, а редактор показывает их после неё, поэтому текстовое
    /// сравнение с их участием разбегалось, и правка ударения либо не находила
    /// фрагмент, либо подставляла слово в другое место книги.
    /// </para>
    /// </summary>
    private static string NormalizeRuns(IReadOnlyList<InlineRun> runs, out List<int> rawPositions, bool includeStressMark = false)
    {
        rawPositions = new List<int>();
        var result = new System.Text.StringBuilder();
        int cursor = 0;
        bool pendingSpace = false;
        int pendingAt = -1;

        foreach (InlineRun run in runs)
        {
            if (run.IsLineBreak)
            {
                if (result.Length > 0) { pendingSpace = true; pendingAt = cursor; }
                cursor++;
                continue;
            }

            string text = run.Text;
            for (int i = 0; i < text.Length; i++, cursor++)
            {
                char c = text[i];
                if (IsInvisibleForMatching(c) && !includeStressMark)
                    continue;

                if (char.IsWhiteSpace(c))
                {
                    if (result.Length > 0) { pendingSpace = true; pendingAt = cursor; }
                    continue;
                }

                if (pendingSpace)
                {
                    result.Append(' ');
                    rawPositions.Add(pendingAt);
                    pendingSpace = false;
                }

                result.Append(c);
                rawPositions.Add(cursor);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Символы, не участвующие в сопоставлении текстов: комбинирующие знаки
    /// ударения. Их порядок в источнике и в окне правки различен, поэтому они не
    /// должны влиять ни на поиск абзаца, ни на поиск заменяемого фрагмента.
    /// </summary>
    private static bool IsInvisibleForMatching(char c) =>
        c is '\u0301' or '\u0341' or '\u00B4' or '\u0300';

    /// <summary>Схлопывает пробелы без карты позиций — для сравнения текстов.</summary>
    private static string Collapse(string value, bool includeStressMark = false) => NormalizeRuns(
        new List<InlineRun> { new InlineRun { Text = value } }, out _, includeStressMark);

    /// <summary>
    /// Считает абзацы совпадающими, если их нормализованные тексты равны, либо
    /// один является началом/концом другого: подсказка берётся из фрагмента и
    /// может быть короче всего абзаца.
    /// </summary>
    private static bool SameParagraph(string normalizedParagraph, string paragraphHint)
    {
        string hint = Collapse(paragraphHint);
        if (hint.Length == 0) return false;
        return normalizedParagraph.Contains(hint, StringComparison.Ordinal)
            || hint.Contains(normalizedParagraph, StringComparison.Ordinal);
    }

    private readonly struct Candidate
    {
        public Candidate(List<InlineRun> runs, bool isTitlePage)
        {
            Runs = runs;
            IsTitlePage = isTitlePage;
        }

        public List<InlineRun> Runs { get; }
        public bool IsTitlePage { get; }
    }

    /// <summary>
    /// Перечисляет все текстовые абзацы модели. Разделы, построенные парсером из
    /// title-info, помечаются как титульная страница — правки в них уходят в
    /// метаданные книги.
    /// </summary>
    private static IEnumerable<Candidate> EnumerateCandidates(DocumentModel document)
    {
        foreach (DocumentSection section in document.Sections)
            foreach (DocumentElement element in section.Elements)
                foreach (Candidate candidate in FlattenCandidates(element, section.IsSyntheticTitlePage))
                    yield return candidate;
    }

    private static IEnumerable<Candidate> FlattenCandidates(DocumentElement element, bool isTitlePage)
    {
        switch (element)
        {
            case ParagraphElement paragraph: yield return new Candidate(paragraph.Runs, isTitlePage); break;
            case TitleElement title: yield return new Candidate(title.Runs, isTitlePage); break;
            case SubtitleElement subtitle: yield return new Candidate(subtitle.Runs, isTitlePage); break;
            case AuthorElement author: yield return new Candidate(author.Runs, isTitlePage); break;
            case PoemElement poem:
                foreach (var stanza in poem.Stanzas)
                    foreach (var line in stanza)
                        yield return new Candidate(line, isTitlePage);
                break;
            case SectionElement section:
                foreach (DocumentElement child in section.Section.Elements)
                    foreach (Candidate inner in FlattenCandidates(child, isTitlePage))
                        yield return inner;
                break;
            case CitationElement citation:
                foreach (DocumentElement child in citation.Elements)
                    foreach (Candidate inner in FlattenCandidates(child, isTitlePage))
                        yield return inner;
                break;
            case EpigraphElement epigraph:
                foreach (DocumentElement child in epigraph.Elements)
                    foreach (Candidate inner in FlattenCandidates(child, isTitlePage))
                        yield return inner;
                if (epigraph.Author != null) yield return new Candidate(epigraph.Author, isTitlePage);
                break;
        }
    }

    public static string Flatten(DocumentModel document)
    {
        var result = new System.Text.StringBuilder();
        AppendSections(result, document.Sections);
        return result.ToString();
    }

    private static void AppendSections(System.Text.StringBuilder result, IEnumerable<DocumentSection> sections)
    {
        bool prior = false;
        foreach (var section in sections)
            foreach (var element in section.Elements)
            {
                if (prior && result.Length > 0) result.Append('\n');
                AppendElement(result, element);
                prior = element is not EmptyLineElement;
            }
    }

    private static void AppendElement(System.Text.StringBuilder result, DocumentElement element)
    {
        switch (element)
        {
            case ParagraphElement paragraph: AppendRuns(result, paragraph.Runs); break;
            case TitleElement title: AppendRuns(result, title.Runs); break;
            case SubtitleElement subtitle: AppendRuns(result, subtitle.Runs); break;
            case AuthorElement author: AppendRuns(result, author.Runs); break;
            // Edit records are kept as metadata and are not part of book text.
            case EditListElement: break;
            case PoemElement poem:
                foreach (var stanza in poem.Stanzas)
                    foreach (var line in stanza) { AppendRuns(result, line); result.Append('\n'); }
                break;
            case SectionElement section: AppendSections(result, new[] { section.Section }); break;
            case CitationElement citation:
                foreach (var child in citation.Elements) AppendElement(result, child);
                break;
            case EpigraphElement epigraph:
                foreach (var child in epigraph.Elements) AppendElement(result, child);
                if (epigraph.Author != null) AppendRuns(result, epigraph.Author);
                break;
        }
    }

    private static void AppendRuns(System.Text.StringBuilder result, IEnumerable<InlineRun> runs)
    {
        foreach (var run in runs)
            result.Append(run.IsLineBreak ? "\n" : run.Text);
    }

    private static bool ReplaceInSections(IEnumerable<DocumentSection> sections, ref int cursor, int start, int length, string replacement)
    {
        bool prior = false;
        foreach (var section in sections)
            foreach (var element in section.Elements)
            {
                if (prior && element is not EmptyLineElement) cursor++;
                if (ReplaceInElement(element, ref cursor, start, length, replacement)) return true;
                prior = element is not EmptyLineElement;
            }
        return false;
    }

    private static bool ReplaceInElement(DocumentElement element, ref int cursor, int start, int length, string replacement)
    {
        switch (element)
        {
            case ParagraphElement paragraph: return ReplaceRuns(paragraph.Runs, ref cursor, start, length, replacement);
            case TitleElement title: return ReplaceRuns(title.Runs, ref cursor, start, length, replacement);
            case SubtitleElement subtitle: return ReplaceRuns(subtitle.Runs, ref cursor, start, length, replacement);
            case AuthorElement author: return ReplaceRuns(author.Runs, ref cursor, start, length, replacement);
            case PoemElement poem:
                foreach (var stanza in poem.Stanzas)
                    foreach (var line in stanza)
                        if (ReplaceRuns(line, ref cursor, start, length, replacement)) return true;
                break;
            case EditListElement:
                break;
            case SectionElement section: return ReplaceInSections(new[] { section.Section }, ref cursor, start, length, replacement);
            case CitationElement citation:
                foreach (var child in citation.Elements) if (ReplaceInElement(child, ref cursor, start, length, replacement)) return true;
                break;
            case EpigraphElement epigraph:
                foreach (var child in epigraph.Elements) if (ReplaceInElement(child, ref cursor, start, length, replacement)) return true;
                if (epigraph.Author != null) return ReplaceRuns(epigraph.Author, ref cursor, start, length, replacement);
                break;
            case ImageElement: break;
            case EmptyLineElement: cursor++; break;
        }
        return false;
    }

    private static bool ReplaceTextValue(string value, ref int cursor, int start, int length, string replacement, out string updated)
    {
        int overlapStart = Math.Max(start, cursor);
        int overlapEnd = Math.Min(start + length, cursor + value.Length);
        if (overlapStart >= overlapEnd)
        {
            cursor += value.Length;
            updated = value;
            return false;
        }
        int localStart = overlapStart - cursor;
        int localEnd = overlapEnd - cursor;
        updated = value[..localStart] + replacement + value[localEnd..];
        return true;
    }

    private static bool ReplaceRuns(List<InlineRun> runs, ref int cursor, int start, int length, string replacement)
    {
        if (length == 0)
        {
            int position = cursor;
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                if (!run.IsLineBreak && start >= position && start <= position + run.Text.Length)
                {
                    int local = Math.Clamp(start - position, 0, run.Text.Length);
                    run.Text = run.Text.Insert(local, replacement);
                    return replacement.Length > 0;
                }
                position += run.IsLineBreak ? 1 : run.Text.Length;
            }
        }
        int rangeEnd = start + length;
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            string text = run.IsLineBreak ? "\n" : run.Text;
            int overlapStart = Math.Max(start, cursor);
            int overlapEnd = Math.Min(rangeEnd, cursor + text.Length);
            if (overlapStart < overlapEnd)
            {
                int localStart = overlapStart - cursor;
                int localEnd = overlapEnd - cursor;
                if (run.IsLineBreak) return false;
                int consumedEnd = overlapEnd;
                int removeAcrossRuns = Math.Max(0, rangeEnd - consumedEnd);
                string updated = text[..localStart] + replacement + text[localEnd..];
                runs[i] = new InlineRun { Text = updated, Style = run.Style, HyperlinkTarget = run.HyperlinkTarget, IsLineBreak = false };
                cursor += text.Length;
                int remaining = removeAcrossRuns;
                while (remaining > 0 && i + 1 < runs.Count)
                {
                    var next = runs[i + 1];
                    int nextLength = next.IsLineBreak ? 1 : next.Text.Length;
                    if (next.IsLineBreak) return false;
                    int removeLength = Math.Min(remaining, nextLength);
                    next.Text = next.Text[removeLength..];
                    remaining -= removeLength;
                    if (next.Text.Length == 0) runs.RemoveAt(i + 1);
                }
                if (remaining > 0) return false;
                return true;
            }
            cursor += text.Length;
        }
        return false;
    }
}