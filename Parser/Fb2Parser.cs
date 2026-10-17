using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FB2AccentConverter.Parser;

/// <summary>
/// Парсер FB2-файлов. Stateless-реализация: результат Parse содержит всю информацию,
/// включая сноски и изображения. Не создаёт FlowDocument — это делает DocumentModelBuilder.
/// Не зависит от WPF — только чистые модели данных.
/// </summary>
public class Fb2Parser : IDocumentParser
{
    private static readonly Regex InvalidXmlCharsRegex = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);
    private static readonly Regex UnescapedAmpRegex = new(@"&(?!#\d+;|#x[0-9a-fA-F]+;|[a-zA-Z_][a-zA-Z0-9_]*;)", RegexOptions.Compiled);

    private readonly ILogger? _logger;

    public Fb2Parser(ILogger? logger = null)
    {
        _logger = logger;
    }

    public DocumentModel Parse(byte[] bytes)
    {
        return Parse(bytes, null, null);
    }

    public DocumentModel Parse(byte[] bytes, IProgress<double>? progress = null, System.Threading.CancellationToken? ct = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string xmlContent = DecodeAndCleanXml(bytes);

        XDocument xdoc;
        try
        {
            xdoc = XDocument.Parse(xmlContent);
        }
        catch (XmlException ex)
        {
            _logger?.LogError($"FB2: невалидный XML в файле", ex);
            throw new InvalidDataException($"Файл повреждён и не может быть открыт как FB2 (невалидный XML).\n{ex.Message}");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"FB2: ошибка парсинга XML", ex);
            throw new InvalidDataException($"Файл повреждён и не может быть открыт как FB2.\n{ex.Message}");
        }

        XNamespace ns = xdoc.Root?.Name.NamespaceName ?? "";
        XNamespace xlink = "http://www.w3.org/1999/xlink";

        var model = new DocumentModel();

        // Изображения - сохраняем base64-снимок содержимого, а не ссылку на XElement.
        // Ссылка держала весь XDocument DOM живым, пока жива DocumentModel (всю
        // сессию), — на книгах с обложками это сотни МБ и лишний давление на GC.
        // Должно быть ПЕРЕД title-info, так как coverpage может ссылаться на изображения
        if (xdoc.Root != null)
        {
            foreach (var bin in xdoc.Root.Descendants(ns + "binary"))
            {
                var id = bin.Attribute("id")?.Value;
                if (id != null)
                {
                    // Снимок строки + ленивое декодирование (см. ImageData.Base64Data)
                    model.Images.Add(new ImageData { Id = id, Base64Data = bin.Value });
                }
            }
        }

        // The reading model includes synthetic title-page text and title-info annotations.
        // Keep an explicit structural marker for the actual body so FB2 saving can map
        // edits to source paragraphs without relying on fragile text/count heuristics.
        // Сноски (body name="notes" и body name="comments"). Список body собираем
        // ОДИН раз: xdoc.Descendants(...) — это полный обход дерева, а он в файлах
        // вроде «Червь» (72 тысячи <p>) выполнялся у нас трижды подряд.
        var bodies = xdoc.Root == null
            ? new List<XElement>()
            : xdoc.Root.Descendants(ns + "body").ToList();

        XElement? titleInfo = null;
        XElement? mainBody = null;
        var noteBodies = new List<XElement>();

        titleInfo = xdoc.Root!.Elements(ns + "description")
            .SelectMany(d => d.Elements(ns + "title-info"))
            .FirstOrDefault()
            ?? xdoc.Root.Elements(ns + "title-info").FirstOrDefault();

        XElement? persistedJournal = xdoc.Descendants(ns + "custom-info")
            .FirstOrDefault(info => (string?)info.Attribute("info-type") == "DeepReader edit journal v1");
        if (persistedJournal != null)
            model.EditRecords.AddRange(ParseEditEntries(persistedJournal, ns));

        foreach (XElement body in bodies)
        {
            var name = (string?)body.Attribute("name");
            if (name == "notes" || name == "comments")
            {
                noteBodies.Add(body);
            }
            else if (mainBody == null)
            {
                mainBody = body;
            }
        }

        // Название книги и автор из title-info
        if (titleInfo != null)
        {
            var bookTitle = titleInfo.Element(ns + "book-title");
            if (bookTitle != null)
            {
                model.BookTitle = bookTitle.Value.Trim();
            }

            // Автор
            var authorEl = titleInfo.Element(ns + "author");
            string? authorName = null;
            if (authorEl != null)
            {
                var firstName = authorEl.Element(ns + "first-name")?.Value?.Trim() ?? "";
                var middleName = authorEl.Element(ns + "middle-name")?.Value?.Trim() ?? "";
                var lastName = authorEl.Element(ns + "last-name")?.Value?.Trim() ?? "";
                var parts = new[] { firstName, middleName, lastName }.Where(s => !string.IsNullOrEmpty(s));
                authorName = string.Join(" ", parts);
            }

            // Сохраняем автора в модель
            if (!string.IsNullOrEmpty(authorName))
            {
                model.BookAuthor = authorName;
            }

            // Создаём титульную страницу перед основным текстом
            var titleSection = new DocumentSection { IsSyntheticTitlePage = true };

            // Обложка (coverpage) - если есть
            // ВРЕМЕННО ОТКЛЮЧЕНО: обложка вызывает фризы при автоскроллинге у некоторых файлов
            // TODO: найти способ безопасно загружать обложки без блокировки UI
            /*
            var coverpage = titleInfo.Element(ns + "coverpage");
            if (coverpage != null)
            {
                var coverImage = coverpage.Element(ns + "image");
                if (coverImage != null)
                {
                    var coverHref = coverImage.Attribute(xlink + "href")?.Value;
                    if (!string.IsNullOrEmpty(coverHref) && coverHref.StartsWith("#"))
                    {
                        var coverId = coverHref.Substring(1);
                        var coverImgData = model.Images.FirstOrDefault(i => i.Id == coverId);
                        if (coverImgData != null)
                        {
                            // Сохраняем ссылку на ImageData вместо загрузки данных
                            titleSection.Elements.Add(new ImageElement { Data = null, ImageDataRef = coverImgData });
                        }
                    }
                }
            }
            */

            // Название книги — крупно, по центру
            if (!string.IsNullOrEmpty(model.BookTitle))
            {
                var titleEl = new TitleElement { IsLarge = true };
                titleEl.Runs.Add(new InlineRun { Text = model.BookTitle, Style = InlineStyle.Normal });
                titleSection.Elements.Add(titleEl);
            }

            // Автор — по центру, курсивом, с размером шрифта автора
            if (!string.IsNullOrEmpty(authorName))
            {
                var titleAuthor = new AuthorElement { TextAlignment = TextAlignment.Center };
                titleAuthor.Runs.Add(new InlineRun { Text = authorName, Style = InlineStyle.Italic });
                titleSection.Elements.Add(titleAuthor);
            }

            // Аннотация, если есть
            var annotation = titleInfo.Element(ns + "annotation");
            if (annotation != null)
            {
                titleSection.Elements.Add(new EmptyLineElement());
                foreach (var p in annotation.Elements(ns + "p"))
                {
                    var para = new ParagraphElement
                    {
                        TextAlignment = TextAlignment.Justify
                    };
                    foreach (var node in p.Nodes())
                        ParseInlineNodeToRun(node, para.Runs);
                    titleSection.Elements.Add(para);
                }
            }

            // Отступ перед основным текстом
            titleSection.Elements.Add(new EmptyLineElement());
            titleSection.Elements.Add(new EmptyLineElement());

            model.Sections.Add(titleSection);
        }

        // Сноски (body name="notes" и body name="comments") — сохраняем в DocumentModel.Footnotes
        // Используем Descendants для рекурсивного поиска всех section с id,
        // так как в некоторых FB2 секции сносок вложены в обёрточные section без id
        var noteSections = noteBodies
            .SelectMany(b => b.Descendants(ns + "section"))
            .ToList();

        foreach (var section in noteSections)
        {
            var sectId = (string?)section.Attribute("id");
            if (sectId == null) continue;

            var noteModel = new DocumentModel();
            var noteSection = new DocumentSection();
            foreach (var el in section.Elements())
            {
                if (el.Name.LocalName == "title") continue;
                ParseElementToModel(el, noteSection.Elements, model.Images, ns, xlink);
            }
            noteModel.Sections.Add(noteSection);
            model.Footnotes[sectId] = noteModel;
        }

        // Основное тело уже найдено при сборе bodies (первое не-notes/comments).

        // Извлекаем параграфы с id из основного тела как сноски (endnotes)
        // Некоторые FB2 файлы используют <p id="local_1"> внутри основного тела для примечаний,
        // на которые ссылаются через <a l:href="#local_1"> без указания type="note"
        if (mainBody != null)
        {
            foreach (var p in mainBody.Descendants(ns + "p"))
            {
                var pid = (string?)p.Attribute("id");
                if (pid == null) continue;

                var noteModel = new DocumentModel();
                var noteSection = new DocumentSection();
                foreach (var node in p.Nodes())
                {
                    var para = new ParagraphElement
                    {
                        TextAlignment = TextAlignment.Justify
                    };
                    ParseInlineNodeToRun(node, para.Runs);
                    noteSection.Elements.Add(para);
                }
                if (noteSection.Elements.Count > 0)
                {
                    noteModel.Sections.Add(noteSection);
                    model.Footnotes[pid] = noteModel;
                }
            }
        }

        var mainSection = model.MainBodySection;
        if (mainBody != null)
        {
            // Используем рекурсивный парсер с отслеживанием заголовков
            ParseBodyToModel(mainBody, mainSection.Elements, model.Images, ns, xlink);
        }
        model.Sections.Add(mainSection);

        return model;
    }

    private static IEnumerable<DocumentEditRecord> ParseEditEntries(XElement container, XNamespace ns)
    {
        foreach (XElement entry in container.Elements(ns + "entry"))
        {
            string original = (string?)entry.Attribute("original") ?? "";
            string replacement = (string?)entry.Attribute("replacement") ?? "";
            if (original.Length == 0 && replacement.Length == 0) continue;
            XElement? context = entry.Element(ns + "context");
            yield return new DocumentEditRecord
            {
                Id = (string?)entry.Attribute("id") ?? Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.TryParse((string?)entry.Attribute("timestamp"), out DateTime timestamp) ? timestamp : DateTime.Now,
                OriginalText = original,
                ReplacementText = replacement,
                OriginalParagraphText = context?.Value ?? "",
                TargetParagraphText = context?.Value ?? "",
                OriginalOffset = int.TryParse((string?)entry.Attribute("offset"), out int offset) ? offset : -1,
                IsTitleMetadataEdit = (string?)entry.Attribute("metadata") == "true",
            };
        }
    }

    // Chapter heading patterns for books where headings are plain paragraphs
    // (typical for HTML-table conversions). Deliberately strict: numbered
    // ("CHAPTER 45", "ГЛАВА 45", "ЧАСТЬ 2", "ГЛАВА IV"), one-word
    // ("Пролог", "Эпилог", "ОГЛАВЛЕНИЕ", "CONTENTS"), or ALL-CAPS
    // ("CHAPTER HOUSE", "ГЛАВА ПЕРВАЯ"). Narrative sentences do not match.
    private static readonly Regex ChapterKeywordRegex = new(
        @"^(?:CHAPTER|ГЛАВА|PART|ЧАСТЬ|SECTION|РАЗДЕЛ)[\s.\-–—]*[0-9IVXLCDM]+[\s.]*$|^(?:ПРОЛОГ|PROLOGUE|ЭПИЛОГ|EPILOGUE|ОГЛАВЛЕНИЕ|CONTENTS|ПРИЛОЖЕНИЕ|APPENDIX|ПРЕДИСЛОВИЕ|FOREWORD)[\s.\-–—]*(?:[0-9IVXLCDM]+[\s.]*)*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChapterAllCapsRegex = new(
        @"^(?:CHAPTER|ГЛАВА|PART|ЧАСТЬ)\s+[A-ZА-ЯЁ0-9](?:[A-ZА-ЯЁ0-9\s.\-–—]{0,38}[A-ZА-ЯЁ0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsChapterHeading(List<InlineRun> runs)
    {
        if (runs.Count == 0) return false;
        var sb = new StringBuilder(runs.Count * 16);
        foreach (var r in runs)
        {
            if (r.IsLineBreak) return false;
            sb.Append(r.Text);
        }
        var text = sb.ToString().Trim();
        if (text.Length == 0 || text.Length > 60) return false;
        return ChapterKeywordRegex.IsMatch(text) || ChapterAllCapsRegex.IsMatch(text);
    }

    /// <summary>
    /// Block-level tags (FB2 + HTML) that may appear inside unknown elements.
    /// Used by the default branch in ParseBodyToModel.
    /// </summary>
    private static readonly HashSet<string> BlockLevelTags = new(StringComparer.Ordinal)
    {
        "p", "section", "title", "subtitle", "poem", "cite", "epigraph",
        "table", "tbody", "thead", "tfoot", "tr", "td", "th",
        "ol", "ul", "li", "dl", "dt", "dd", "div", "blockquote", "output"
    };

    private void ParseBodyToModel(
        XElement parent,
        List<DocumentElement> elements,
        List<ImageData> images,
        XNamespace ns,
        XNamespace xlink)
    {
        bool lastWasHeading = false;

        foreach (var el in parent.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "title":
                case "subtitle":
                    if (el.Ancestors(ns + "section").Any(section => (string?)section.Attribute("id") == "deepreader-edit-list"))
                        break;
                    var titleEl = new TitleElement
                    {
                        IsLarge = el.Name.LocalName == "title"
                    };
                    ParseTitleContent(el, titleEl.Runs);
                    elements.Add(titleEl);
                    lastWasHeading = true;
                    break;

                case "section":
                    if ((string?)el.Attribute("id") == "deepreader-edit-list")
                    {
                        lastWasHeading = false;
                        break;
                    }
                    var section = new DocumentSection();
                    // Рекурсивно обрабатываем вложенный section
                    ParseBodyToModel(el, section.Elements, images, ns, xlink);
                    if (section.Elements.Count > 0)
                        elements.Add(new SectionElement { Section = section });
                    // После section не ставим флаг, так как внутри section может быть что угодно
                    lastWasHeading = false;
                    break;

                case "p":
                    if (el.Ancestors(ns + "section").Any(section => (string?)section.Attribute("id") == "deepreader-edit-list"))
                        break;
                    if (string.IsNullOrWhiteSpace(el.Value) && !el.HasElements) break;
                    var para = new ParagraphElement
                    {
                        TextAlignment = TextAlignment.Justify,
                        IsFirstParagraphAfterHeading = lastWasHeading
                    };
                    var alignAttr = el.Attribute("align")?.Value;
                    if (!string.IsNullOrEmpty(alignAttr))
                    {
                        para.TextAlignment = alignAttr.ToLower() switch
                        {
                            "center" => TextAlignment.Center,
                            "right" => TextAlignment.Right,
                            "left" => TextAlignment.Left,
                            _ => TextAlignment.Justify
                        };
                    }
                    foreach (var node in el.Nodes())
                        ParseInlineNodeToRun(node, para.Runs);
                    if (IsChapterHeading(para.Runs))
                    {
                        var heading = new TitleElement { IsLarge = true };
                        heading.Runs.AddRange(para.Runs);
                        elements.Add(heading);
                        lastWasHeading = true;
                    }
                    else
                    {
                        elements.Add(para);
                        lastWasHeading = false;
                    }
                    break;

                case "empty-line":
                    elements.Add(new EmptyLineElement());
                    lastWasHeading = false;
                    break;

                case "image":
                    var href = el.Attribute(xlink + "href")?.Value;
                    if (!string.IsNullOrEmpty(href) && href.StartsWith("#"))
                    {
                        var id = href.Substring(1);
                        var imgData = images.FirstOrDefault(i => i.Id == id);
                        elements.Add(new ImageElement { Data = imgData?.GetOrLoadData() });
                    }
                    lastWasHeading = false;
                    break;

                case "poem":
                    var poem = new PoemElement();
                    foreach (var stanza in el.Elements(ns + "stanza"))
                    {
                        var lines = new List<List<InlineRun>>();
                        foreach (var v in stanza.Elements(ns + "v"))
                        {
                            var runs = new List<InlineRun>();
                            foreach (var node in v.Nodes())
                                ParseInlineNodeToRun(node, runs);
                            lines.Add(runs);
                        }
                        poem.Stanzas.Add(lines);
                    }
                    elements.Add(poem);
                    lastWasHeading = false;
                    break;

                case "epigraph":
                case "cite":
                    var container = el.Name.LocalName == "epigraph"
                        ? (DocumentElement)new EpigraphElement()
                        : new CitationElement();
                    ParseContainerContent(el, container);
                    elements.Add(container);
                    lastWasHeading = false;
                    break;

                case "text-author":
                    var authorRuns = new List<InlineRun>();
                    foreach (var node in el.Nodes())
                        ParseInlineNodeToRun(node, authorRuns);
                    elements.Add(new AuthorElement { Runs = authorRuns });
                    lastWasHeading = false;
                    break;

                default:
                    // Unknown tag (e.g. an HTML table inside FB2 produced by a broken
                    // converter: <td>text</td>, or custom markup). If it has block-level
                    // children, recurse into them; otherwise its content becomes a
                    // paragraph. Previously text outside <p> was silently dropped: in
                    // books whose whole body sits inside <table><tr><td> the entire
                    // text was lost and the reader saw only a couple of paragraphs.
                    if (el.HasElements && el.Elements().Any(static c => BlockLevelTags.Contains(c.Name.LocalName)))
                    {
                        ParseBodyToModel(el, elements, images, ns, xlink);
                    }
                    else if (!string.IsNullOrWhiteSpace(el.Value))
                    {
                        var loosePara = new ParagraphElement
                        {
                            TextAlignment = TextAlignment.Justify,
                            IsFirstParagraphAfterHeading = lastWasHeading
                        };
                        foreach (var node in el.Nodes())
                            ParseInlineNodeToRun(node, loosePara.Runs);
                        if (loosePara.Runs.Count > 0)
                        {
                            if (IsChapterHeading(loosePara.Runs))
                            {
                                var looseHeading = new TitleElement { IsLarge = true };
                                looseHeading.Runs.AddRange(loosePara.Runs);
                                elements.Add(looseHeading);
                                lastWasHeading = true;
                            }
                            else
                            {
                                elements.Add(loosePara);
                                lastWasHeading = false;
                            }
                        }
                    }
                    break;
            }
        }
    }

    private string DecodeAndCleanXml(byte[] bytes)
    {
        // Сначала определяем кодировку
        Encoding enc = DetectEncoding(bytes);
        string raw;
        try
        {
            // Encoding.GetString НЕ отрезает BOM — U+FEFF остался бы первым
            // символом строки, и XDocument.Parse упал бы с
            // "Data at the root level is invalid. Line 1, position 1."
            int offset = bytes.Length >= enc.Preamble.Length &&
                bytes.AsSpan(0, enc.Preamble.Length).SequenceEqual(enc.Preamble)
                    ? enc.Preamble.Length
                    : 0;
            raw = enc.GetString(bytes, offset, bytes.Length - offset).TrimStart('\uFEFF');
            _logger?.LogDebug($"FB2: файл декодирован с кодировкой {enc.EncodingName}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"FB2: не удалось декодировать файл с определённой кодировкой {enc.EncodingName}, используем UTF-8", ex);
            // Если совсем не получилось – пробуем UTF-8
            raw = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }

        // Убираем недопустимые в XML символы и чиним частые косяки FB2
        raw = CleanInvalidXmlChars(raw);
        return raw;
    }

    private Encoding DetectEncoding(byte[] bytes)
    {
        // 0. BOM надёжнее XML-декларации: он однозначно задает и кодировку, и то,
        //    что преамбулу нужно отрезать при декодировании (см. DecodeAndCleanXml).
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            _logger?.LogDebug("FB2: кодировка определена по BOM: UTF-8");
            return Encoding.UTF8;
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                _logger?.LogDebug("FB2: кодировка определена по BOM: UTF-16 LE");
                return Encoding.Unicode;
            }

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                _logger?.LogDebug("FB2: кодировка определена по BOM: UTF-16 BE");
                return Encoding.BigEndianUnicode;
            }
        }

        // Читаем первые байты в ASCII
        string head;
        try
        {
            head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 300));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug($"FB2: не удалось прочитать заголовок файла в ASCII", ex);
            head = string.Empty;
        }

        // Ищем XML-декларацию и выдираем encoding
        string? encodingName = null;
        int start = head.IndexOf("<?xml", StringComparison.Ordinal);
        if (start >= 0)
        {
            int end = head.IndexOf("?>", start);
            if (end >= 0)
            {
                string decl = head.Substring(start, end - start + 2);
                Match m = Regex.Match(decl, "encoding\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
                if (m.Success)
                    encodingName = m.Groups[1].Value;
            }
        }

        if (!string.IsNullOrEmpty(encodingName))
        {
            try
            {
                var enc = Encoding.GetEncoding(encodingName);
                _logger?.LogDebug($"FB2: кодировка определена из XML-декларации: {encodingName}");
                return enc;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"FB2: кодировка '{encodingName}' из XML-декларации не поддерживается, используем UTF-8", ex);
            }
        }

        // Не удалось определить – возвращаем UTF-8
        _logger?.LogDebug("FB2: кодировка не определена, используем UTF-8 по умолчанию");
        return Encoding.UTF8;
    }

    private string CleanInvalidXmlChars(string xml)
    {
        if (string.IsNullOrEmpty(xml))
            return xml;

        // Используем StringBuilder с предварительным выделением памяти
        var sb = new StringBuilder(xml.Length);
        
        // Проход по символам с использованием Span для минимизации аллокаций
        ReadOnlySpan<char> span = xml.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            
            // Пропускаем недопустимые символы (0x00-0x08, 0x0B, 0x0C, 0x0E-0x1F, 0x7F)
            // Допустимы: 0x09 (tab), 0x0A (LF), 0x0D (CR)
            if (c < '\x09') continue;
            if (c == '\x0B' || c == '\x0C') continue;
            if (c >= '\x0E' && c <= '\x1F') continue;
            if (c == '\x7F') continue;
            
            // Символы ‹ и › — обычные текстовые угловые кавычки. Их нельзя
            // преобразовывать в '<' и '>': после такой замены фрагмент вроде
            // «‹…›» превращается в псевдо-тег <…>, и XML-парсер отвергает
            // полностью исправный FB2-файл.
            
            // Обработка амперсандов и HTML-сущностей
            if (c == '&')
            {
                if (i + 7 <= span.Length)
                {
                    ReadOnlySpan<char> remaining = span.Slice(i);
                    if (remaining.StartsWith("&laquo;", StringComparison.Ordinal)) 
                    { sb.Append('«'); i += 6; continue; }
                    if (remaining.StartsWith("&raquo;", StringComparison.Ordinal)) 
                    { sb.Append('»'); i += 6; continue; }
                    if (remaining.StartsWith("&nbsp;", StringComparison.Ordinal)) 
                    { sb.Append(' '); i += 6; continue; }
                    if (remaining.StartsWith("&mdash;", StringComparison.Ordinal)) 
                    { sb.Append('—'); i += 6; continue; }
                    if (remaining.StartsWith("&ndash;", StringComparison.Ordinal)) 
                    { sb.Append('–'); i += 6; continue; }
                }
                if (i + 8 <= span.Length)
                {
                    ReadOnlySpan<char> remaining = span.Slice(i);
                    if (remaining.StartsWith("&hellip;", StringComparison.Ordinal)) 
                    { sb.Append('…'); i += 7; continue; }
                }
                
                // Экранируем "голые" амперсанды
                if (!IsValidEntity(span, i))
                {
                    sb.Append("&amp;");
                    continue;
                }
            }
            
            sb.Append(c);
        }

        return sb.ToString();
    }
    
    private static bool IsValidEntity(ReadOnlySpan<char> span, int startPos)
    {
        // Проверяем, является ли & началом валидной сущности
        if (startPos + 1 >= span.Length) return false;
        
        // Проверка на числовую сущность &#123; или &#xABC;
        if (span[startPos + 1] == '#')
        {
            int end = span.Slice(startPos).IndexOf(';');
            if (end > 0)
            {
                var entity = span.Slice(startPos, end + 1);
                if (entity.StartsWith("&#x", StringComparison.OrdinalIgnoreCase) ||
                    entity.StartsWith("&#X", StringComparison.OrdinalIgnoreCase))
                {
                    // Проверяем шестнадцатеричную сущность
                    for (int i = 3; i < entity.Length - 1; i++)
                    {
                        char c = entity[i];
                        if (!char.IsDigit(c) && (c < 'A' || c > 'F') && (c < 'a' || c > 'f')) 
                            return false;
                    }
                    return true;
                }
                else if (entity.StartsWith("&#", StringComparison.Ordinal))
                {
                    // Проверяем десятичную сущность
                    for (int i = 2; i < entity.Length - 1; i++)
                    {
                        if (!char.IsDigit(entity[i])) return false;
                    }
                    return true;
                }
            }
            return false;
        }

        // Проверка на именованную сущность &name;
        int end2 = span.Slice(startPos).IndexOf(';');
        if (end2 > 1)
        {
            var entity = span.Slice(startPos + 1, end2 - 1);
            for (int i = 0; i < entity.Length; i++)
            {
                char c = entity[i];
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }
            return true;
        }
        
        return false;
    }

    private void ParseTitleContent(XElement el, List<InlineRun> runs)
    {
        // В FB2 каждый <p> внутри <title> — отдельная строка. Маркер переноса
        // обязателен между ними: без него плоская текстовая модель склеивает
        // части заголовка, а offset оглавления уже не совпадает с отображением.
        foreach (var p in el.Elements())
        {
            if (p.Name.LocalName != "p")
                continue;

            var allText = GetTitleText(p);
            if (string.IsNullOrWhiteSpace(allText))
                continue;

            if (runs.Count > 0)
                runs.Add(new InlineRun { IsLineBreak = true });

            runs.Add(new InlineRun { Text = allText, Style = InlineStyle.Normal });
        }
    }

    private string GetTitleText(XElement el)
    {
        var parts = new List<string>();
        CollectTitleText(el, parts);
        // Нормализуем пробелы: несколько пробелов → один, убираем начальные/конечные
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)))
            .Replace("  ", " ").Trim();
    }

    private void CollectTitleText(XNode node, List<string> parts)
    {
        if (node is XText txt)
        {
            // Убираем лишние пробелы и переносы строк из текстовых узлов.
            // Раньше здесь стоял Regex.Replace(@"\s+", " ") — на десятках тысяч
            // текстовых узлов заголовков это регулярка на каждый узел.
            string text = CollapseWhitespace(txt.Value);
            if (!string.IsNullOrEmpty(text))
                parts.Add(text);
        }
        else if (node is XElement el)
        {
            // Рекурсивно обрабатываем вложенные элементы (strong, emphasis, a и т.д.)
            foreach (var child in el.Nodes())
                CollectTitleText(child, parts);
        }
    }

    /// <summary>Схлопывает любой пробельный символ в один пробел и обрезает края.</summary>
    private static string CollapseWhitespace(string value)
    {
        int i = 0;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
        if (i == value.Length) return string.Empty;

        int end = value.Length;
        while (end > i && char.IsWhiteSpace(value[end - 1])) end--;

        var sb = new StringBuilder(end - i);
        bool prevWasSpace = false;
        for (int k = i; k < end; k++)
        {
            char c = value[k];
            if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace) sb.Append(' ');
                prevWasSpace = true;
            }
            else
            {
                sb.Append(c);
                prevWasSpace = false;
            }
        }

        return sb.ToString();
    }

    private void ParseContainerContent(XElement el, DocumentElement container)
    {
        if (container is EpigraphElement epig)
        {
            foreach (var child in el.Elements())
            {
                if (child.Name.LocalName == "author")
                {
                    epig.Author = new List<InlineRun>();
                    foreach (var node in child.Nodes())
                        ParseInlineNodeToRun(node, epig.Author);
                }
                else
                {
                    ParseElementToModel(child, epig.Elements, new List<ImageData>(), el.Name.Namespace, "http://www.w3.org/1999/xlink");
                }
            }
        }
        else if (container is CitationElement cit)
        {
            foreach (var child in el.Elements())
                ParseElementToModel(child, cit.Elements, new List<ImageData>(), el.Name.Namespace, "http://www.w3.org/1999/xlink");
        }
    }

    private void ParseElementToModel(
        XElement el,
        List<DocumentElement> elements,
        List<ImageData> images,
        XNamespace ns,
        XNamespace xlink)
    {
        switch (el.Name.LocalName)
        {
            case "section":
                var section = new DocumentSection();
                foreach (var c in el.Elements())
                    ParseElementToModel(c, section.Elements, images, ns, xlink);
                if (section.Elements.Count > 0)
                    elements.Add(new SectionElement { Section = section });
                break;

            case "title":
            case "subtitle":
                var titleEl = new TitleElement
                {
                    IsLarge = el.Name.LocalName == "title"
                };
                ParseTitleContent(el, titleEl.Runs);
                elements.Add(titleEl);
                break;

            case "epigraph":
            case "cite":
                var container = el.Name.LocalName == "epigraph"
                    ? (DocumentElement)new EpigraphElement()
                    : new CitationElement();
                ParseContainerContent(el, container);
                elements.Add(container);
                break;

            case "poem":
                var poem = new PoemElement();
                foreach (var stanza in el.Elements(ns + "stanza"))
                {
                    var lines = new List<List<InlineRun>>();
                    foreach (var v in stanza.Elements(ns + "v"))
                    {
                        var runs = new List<InlineRun>();
                        foreach (var node in v.Nodes())
                            ParseInlineNodeToRun(node, runs);
                        lines.Add(runs);
                    }
                    poem.Stanzas.Add(lines);
                }
                elements.Add(poem);
                break;

            case "p":
                if (string.IsNullOrWhiteSpace(el.Value) && !el.HasElements) break;
                var para = new ParagraphElement
                {
                    TextAlignment = TextAlignment.Justify
                };
                var alignAttr = el.Attribute("align")?.Value;
                if (!string.IsNullOrEmpty(alignAttr))
                {
                    para.TextAlignment = alignAttr.ToLower() switch
                    {
                        "center" => TextAlignment.Center,
                        "right" => TextAlignment.Right,
                        "left" => TextAlignment.Left,
                        _ => TextAlignment.Justify
                    };
                }
                // Нестандартные FB2 вкладывают <stanza>/<v>/<poem> прямо в <p>.
                // Такие узлы обрабатываются в ParseInlineNodeToRun — там добавляется
                // маркер переноса строки, чтобы абзац разбивался на строки, а не
                // превращался в один огромный блок (причина лагов прокрутки).
                foreach (var node in el.Nodes())
                    ParseInlineNodeToRun(node, para.Runs);
                elements.Add(para);
                break;

            case "empty-line":
                elements.Add(new EmptyLineElement());
                break;

            case "image":
                var href = el.Attribute(xlink + "href")?.Value;
                if (!string.IsNullOrEmpty(href) && href.StartsWith("#"))
                {
                    var id = href.Substring(1);
                    var imgData = images.FirstOrDefault(i => i.Id == id);
                    elements.Add(new ImageElement { Data = imgData?.GetOrLoadData() });
                }
                break;

            case "text-author":
                var authorRuns = new List<InlineRun>();
                foreach (var node in el.Nodes())
                    ParseInlineNodeToRun(node, authorRuns);
                elements.Add(new AuthorElement { Runs = authorRuns });
                break;

            default:
                foreach (var child in el.Elements())
                    ParseElementToModel(child, elements, images, ns, xlink);
                break;
        }
    }

    /// <summary>
    /// Текст вложенных ранов через один пробел. Раньше здесь стоял
    /// <c>string.Join(" ", runs.Select(r =&gt; r.Text))</c> — на файле с тысячами
    /// strong/emphasis это десятки тысяч лишних делегатов и итераторов.
    /// </summary>
    private static string JoinRunText(List<InlineRun> runs)
    {
        switch (runs.Count)
        {
            case 0:
                return string.Empty;
            case 1:
                return runs[0].Text ?? string.Empty;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < runs.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(runs[i].Text);
        }

        return sb.ToString();
    }

    private void ParseInlineNodeToRun(XNode node, List<InlineRun> runs)
    {
        if (node is XText txt)
        {
            string text = txt.Value;
            if (string.IsNullOrEmpty(text)) return;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) runs.Add(new InlineRun { Text = " " });
                if (!string.IsNullOrEmpty(lines[i]))
                    runs.Add(new InlineRun { Text = lines[i], Style = InlineStyle.Normal });
            }
        }
        else if (node is XElement el)
        {
            switch (el.Name.LocalName)
            {
                case "a":
                    var typeAttr = el.Attribute("type")?.Value;
                    var hrefAttr = el.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))?.Value;

                    // Fallback: если не нашли через xlink namespace, ищем атрибут href по локальному имени
                    // (в некоторых FB2 файлах префикс может быть "l:" вместо "xlink:")
                    if (string.IsNullOrEmpty(hrefAttr))
                    {
                        hrefAttr = el.Attributes()
                            .FirstOrDefault(a => a.Name.LocalName == "href" && a.Name.NamespaceName != "")?.Value;
                    }

                    // Fallback #2: ищем href вообще без namespace (на случай совсем битых файлов)
                    if (string.IsNullOrEmpty(hrefAttr))
                    {
                        hrefAttr = el.Attribute("href")?.Value;
                    }

                    // Собираем текст ссылки
                    var linkRuns = new List<InlineRun>();
                    foreach (var child in el.Nodes())
                        ParseInlineNodeToRun(child, linkRuns);

                    if (!string.IsNullOrEmpty(hrefAttr))
                    {
                        // Для внутренних ссылок (начинаются с #) — сохраняем ID без #
                        // Для внешних ссылок — сохраняем полный URL как есть
                        string hyperlinkTarget;
                        if (hrefAttr.StartsWith("#"))
                        {
                            hyperlinkTarget = hrefAttr.Substring(1);
                        }
                        else
                        {
                            hyperlinkTarget = hrefAttr;
                        }

                        runs.Add(new InlineRun
                        {
                            Text = JoinRunText(linkRuns),
                            HyperlinkTarget = hyperlinkTarget
                        });
                    }
                    else
                    {
                        // Нет href — вставляем текст ссылки как обычный текст
                        runs.AddRange(linkRuns);
                    }
                    break;

                case "strong":
                    var boldRuns = new List<InlineRun>();
                    foreach (var child in el.Nodes())
                        ParseInlineNodeToRun(child, boldRuns);
                    runs.Add(new InlineRun
                    {
                        Text = JoinRunText(boldRuns),
                        Style = InlineStyle.Bold
                    });
                    break;

                case "emphasis":
                    var italicRuns = new List<InlineRun>();
                    foreach (var child in el.Nodes())
                        ParseInlineNodeToRun(child, italicRuns);
                    runs.Add(new InlineRun
                    {
                        Text = JoinRunText(italicRuns),
                        Style = InlineStyle.Italic
                    });
                    break;

                case "sub":
                    var subRuns = new List<InlineRun>();
                    foreach (var child in el.Nodes())
                        ParseInlineNodeToRun(child, subRuns);
                    runs.Add(new InlineRun
                    {
                        Text = JoinRunText(subRuns),
                        Style = InlineStyle.Subscript
                    });
                    break;

                case "sup":
                    var supRuns = new List<InlineRun>();
                    foreach (var child in el.Nodes())
                        ParseInlineNodeToRun(child, supRuns);
                    runs.Add(new InlineRun
                    {
                        Text = JoinRunText(supRuns),
                        Style = InlineStyle.Superscript
                    });
                    break;

                case "empty-line":
                    runs.Add(new InlineRun { Text = " ", IsLineBreak = true });
                    break;

                case "stanza":
                case "v":
                    // Строка стиха/строфа внутри абзаца (нестандартная, но распространнёная
                    // структура FB2). Ставим маркер переноса строки, чтобы огромный
                    // параграф со стихами разбивался на отдельные визуальные строки,
                    // затем рекурсивно разбираем содержимое (текст стиха, emphasis и т.д.).
                    runs.Add(new InlineRun { IsLineBreak = true });
                    foreach (var child in el.Nodes())
                        ParseInlineNodeToRun(child, runs);
                    break;

                default:
                    foreach (var child in el.Nodes())
                        ParseInlineNodeToRun(child, runs);
                    break;
            }
        }
    }
}