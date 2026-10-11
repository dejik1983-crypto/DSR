using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DeepReader.Services;

namespace FB2AccentConverter;

internal static class Program
{
    private static readonly System.Text.RegularExpressions.Regex RuAccentMarker = new(
        "\\+(?=[а-яёА-ЯЁ])", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly HashSet<string> TextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "v", "subtitle", "text-author", "date", "book-title", "first-name", "middle-name", "last-name",
        "nickname", "home-page", "email", "genre", "sequence", "annotation", "poem", "stanza", "cite",
        "epigraph", "empty-line", "thesis", "src-title", "match"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // Some Windows shells have redirected output handles that reject encoding changes.
        }
        try
        {
            if (args.Length == 0)
            {
                var application = new System.Windows.Application();
                application.Run(new MainWindow());
                return 0;
            }

            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            using var accentizer = new RuAccentWorker(options.Model, options.Dictionary, options.Device);
            accentizer.Start();

            var files = Directory.Exists(options.Input)
                ? Directory.EnumerateFiles(options.Input, "*.fb2", options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToArray()
                : new[] { options.Input };
            if (files.Length == 0)
                throw new FileNotFoundException($"В папке не найдено FB2-файлов: {options.Input}");

            if (options.OutputWasSpecified && options.SingleOutputFile == null)
                Directory.CreateDirectory(options.Output);
            var successes = 0;
            foreach (var input in files)
            {
                try
                {
                    var output = GetOutputPath(input, options);
                    ConvertFile(input, output, accentizer);
                    successes++;
                    Console.WriteLine($"Готово: {input} -> {output}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Ошибка [{input}]: {ex.Message}");
                }
            }

            Console.WriteLine($"Обработано: {successes}/{files.Length}");
            return successes == files.Length ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal sealed record ChapterInfo(int Id, string Title, int Level);

    internal static IReadOnlyList<ChapterInfo> ReadChapters(string path)
    {
        var data = File.ReadAllBytes(path);
        _ = new Fb2Parser().Parse(data);
        var document = XDocument.Parse(DecodeXml(data), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой XML-элемент.");
        var ns = root.Name.Namespace;
        var chapters = new List<ChapterInfo>();
        var mainBody = GetMainBody(root, ns);
        if (mainBody == null) return chapters;

        foreach (var (section, title, level) in EnumerateChapterSections(mainBody, ns))
            chapters.Add(new ChapterInfo(chapters.Count, title, level));
        return chapters;
    }

    private static IEnumerable<(XElement Section, string Title, int Level)> EnumerateChapterSections(XElement parent, XNamespace ns, int level = 0)
    {
        foreach (var section in parent.Elements().Where(e => e.Name.Namespace == ns && e.Name.LocalName == "section"))
        {
            var titleElement = section.Elements().FirstOrDefault(e => e.Name.Namespace == ns && e.Name.LocalName == "title");
            var title = titleElement == null ? null : string.Join(" ", titleElement.Elements()
                .Where(e => e.Name.Namespace == ns && e.Name.LocalName == "p")
                .Select(p => CollapseWhitespace(string.Concat(p.DescendantNodes().OfType<XText>().Select(t => t.Value))))
                .Where(text => text.Length > 0));
            if (IsValidChapterTitle(title)) yield return (section, title!, level);
            foreach (var nested in EnumerateChapterSections(section, ns, level + 1)) yield return nested;
        }
    }

    private static bool IsValidChapterTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length >= 100) return false;
        return title.Any(char.IsLetterOrDigit);
    }

    internal static void ConvertInteractive(string input, string output, string model, string device,
        IReadOnlyCollection<int>? selectedChapterIds, bool includeIllustrations, Action<string> report,
        Action<int, int> reportProgress)
    {
        using var accentizer = new RuAccentWorker(model, true, device, report);
        accentizer.Start();
        ConvertFile(input, output, accentizer, selectedChapterIds, includeIllustrations, reportProgress);
        report($"Готово: {output}");
    }

    private static string GetOutputPath(string input, Options options)
    {
        if (options.SingleOutputFile != null)
            return Path.GetFullPath(options.SingleOutputFile);
        var folder = options.OutputWasSpecified ? options.Output : Path.GetDirectoryName(Path.GetFullPath(input))!;
        if (options.OutputWasSpecified && options.Recursive && Directory.Exists(options.Input))
        {
            var relativeFolder = Path.GetDirectoryName(Path.GetRelativePath(options.Input, input));
            if (!string.IsNullOrEmpty(relativeFolder) && relativeFolder != ".")
                folder = Path.Combine(folder, relativeFolder);
        }
        var name = Path.GetFileNameWithoutExtension(input) + options.Suffix + ".fb2";
        var candidate = Path.Combine(folder, name);
        var source = Path.GetFullPath(input);
        if (string.Equals(Path.GetFullPath(candidate), source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь выходного файла совпадает с исходным. Укажите другой суффикс или папку результата.");
        return candidate;
    }

    private static void ConvertFile(string inputPath, string outputPath, RuAccentWorker accentizer,
        IReadOnlyCollection<int>? selectedChapterIds = null, bool includeIllustrations = true,
        Action<int, int>? reportProgress = null)
    {
        var input = File.ReadAllBytes(inputPath);

        // Задействуем именно FB2-парсер DeepReader, чтобы проверять тот же формат,
        // который впоследствии будет отображать читатель. Дерево XML ниже нужно
        // для точечного изменения текста с сохранением разметки и ресурсов FB2.
        _ = new Fb2Parser().Parse(input);

        var xml = DecodeXml(input);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В FB2 отсутствует корневой XML-элемент.");
        var fictionBookNs = root.Name.Namespace;
        var changed = 0;
        var normalized = 0;
        IEnumerable<XElement> textScope = root.DescendantsAndSelf();
        if (selectedChapterIds != null)
        {
            var chapterSections = new List<XElement>();
            var mainBody = GetMainBody(root, fictionBookNs);
            if (mainBody != null)
            {
                var chapterId = 0;
                foreach (var (section, _, _) in EnumerateChapterSections(mainBody, fictionBookNs))
                {
                    if (selectedChapterIds.Contains(chapterId)) chapterSections.Add(section);
                    chapterId++;
                }
            }
            textScope = chapterSections.SelectMany(section => section.DescendantsAndSelf()).Distinct();
        }
        var eligibleText = textScope
            .Where(element => element.Name.Namespace == fictionBookNs && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>())
            .Distinct()
            .ToArray();
        var processableText = eligibleText.Where(textNode => !string.IsNullOrWhiteSpace(textNode.Value)).ToArray();
        var completedText = 0;
        reportProgress?.Invoke(0, processableText.Length);

        if (!includeIllustrations)
        {
            root.Descendants().Where(e => e.Name.Namespace == fictionBookNs && e.Name.LocalName == "image").Remove();
            root.Descendants().Where(e => e.Name.Namespace == fictionBookNs && e.Name.LocalName == "binary").Remove();
        }

        foreach (var textNode in eligibleText)
        {
            if (string.IsNullOrWhiteSpace(textNode.Value)) continue;
            var sourceText = textNode.Value;
            var accented = RuAccentMarker.Replace(accentizer.Process(sourceText), "\u0301");
            if (!string.Equals(accented, sourceText, StringComparison.Ordinal))
            {
                textNode.Value = accented;
                changed++;
            }
            var preserveWhitespace = sourceText.Length - sourceText.TrimStart().Length;
            if (preserveWhitespace > 0 && accented.Length >= preserveWhitespace && !accented.AsSpan(0, preserveWhitespace).SequenceEqual(sourceText.AsSpan(0, preserveWhitespace)))
            {
                textNode.Value = string.Concat(sourceText.AsSpan(0, preserveWhitespace), accented.AsSpan(preserveWhitespace));
                normalized++;
            }
            completedText++;
            if (completedText == processableText.Length || completedText % 10 == 0)
                reportProgress?.Invoke(completedText, processableText.Length);
        }

        if (document.Declaration != null)
            document.Declaration.Encoding = "utf-8";

        var temporary = outputPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        try
        {
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                OmitXmlDeclaration = document.Declaration == null,
                NewLineHandling = NewLineHandling.None
            }))
                document.Save(writer);

            // Проверяем, что результат по-прежнему читается парсером DeepReader.
            _ = new Fb2Parser().Parse(File.ReadAllBytes(temporary));
            File.Move(temporary, outputPath, overwrite: true);
            Console.WriteLine($"  Узлов текста с изменениями: {changed}; исправлено пробелов на границах: {normalized}");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static XElement? GetMainBody(XElement root, XNamespace ns) =>
        root.Descendants().Where(e => e.Name.Namespace == ns && e.Name.LocalName == "body")
            .FirstOrDefault(body => (string?)body.Attribute("name") is not "notes" and not "comments");

    private static string CollapseWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace) result.Append(' ');
            result.Append(character);
            pendingSpace = false;
        }
        return result.ToString();
    }

    private static string DecodeXml(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        var declaration = System.Text.RegularExpressions.Regex.Match(prefix, "<\\?xml[^>]*encoding\\s*=\\s*[\"'](?<enc>[^\"']+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) encoding = Encoding.UTF8;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) encoding = Encoding.Unicode;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) encoding = Encoding.BigEndianUnicode;
        else if (declaration.Success) encoding = Encoding.GetEncoding(declaration.Groups["enc"].Value);
        else encoding = new UTF8Encoding(false, true);

        var preamble = encoding.GetPreamble();
        var offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset).TrimStart('\uFEFF');
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Конвертер FB2: расстановка ударений через ruaccent с проверкой FB2-парсером DeepReader.");
        Console.WriteLine("Использование:");
        Console.WriteLine("  FB2AccentConverter.exe <файл.fb2 | папка> [-o <папка | файл.fb2>] [--recursive]");
        Console.WriteLine("      [--suffix _ударения] [--model turbo3.1] [--no-dictionary] [--device CPU|CUDA]\n");
        Console.WriteLine("По умолчанию создаётся копия рядом с источником с суффиксом _ударения.");
        Console.WriteLine("При передаче папки в -o файлы помещаются в неё; структура подпапок при --recursive сохраняется.");
        Console.WriteLine("Для ruaccent нужен Python с установленными зависимостями из requirements.txt.");
    }

    private sealed class Options
    {
        public required string Input { get; init; }
        public string Output { get; init; } = "";
        public bool OutputWasSpecified { get; init; }
        public string? SingleOutputFile { get; init; }
        public string Suffix { get; init; } = "_ударения";
        public string Model { get; init; } = "turbo3.1";
        public bool Dictionary { get; init; } = true;
        public string Device { get; init; } = "CPU";
        public bool Recursive { get; init; }
        public bool ShowHelp { get; init; }

        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?"))
                return new Options { Input = "", ShowHelp = true };

            string? input = null;
            string output = "";
            var outputSpecified = false;
            string suffix = "_ударения";
            string model = "turbo3.1";
            var dictionary = true;
            var device = "CPU";
            var recursive = false;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Value()
                {
                    if (++i >= args.Length) throw new ArgumentException($"Для параметра {arg} не задано значение.");
                    return args[i];
                }
                switch (arg.ToLowerInvariant())
                {
                    case "-o": case "--output": output = Value(); outputSpecified = true; break;
                    case "--suffix": suffix = Value(); break;
                    case "--model": model = Value(); break;
                    case "--device": device = Value().ToUpperInvariant(); break;
                    case "--no-dictionary": dictionary = false; break;
                    case "--recursive": recursive = true; break;
                    default:
                        if (arg.StartsWith('-')) throw new ArgumentException($"Неизвестный параметр: {arg}");
                        if (input != null) throw new ArgumentException("Укажите только один исходный файл или папку.");
                        input = arg;
                        break;
                }
            }
            if (input == null) throw new ArgumentException("Не указан исходный файл или папка.");
            if (device is not ("CPU" or "CUDA")) throw new ArgumentException("--device должен быть CPU или CUDA.");

            string? singleOutput = null;
            if (outputSpecified && output.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(input)) throw new ArgumentException("Для папки -o должен задавать папку, не имя файла.");
                if (File.Exists(input) && string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Выходной файл не должен совпадать с исходным.");
                singleOutput = output;
            }
            else if (outputSpecified)
                Directory.CreateDirectory(output);

            return new Options { Input = input, Output = output, OutputWasSpecified = outputSpecified,
                SingleOutputFile = singleOutput, Suffix = suffix, Model = model, Dictionary = dictionary,
                Device = device, Recursive = recursive };
        }
    }

    private sealed class RuAccentWorker : IDisposable
    {
        private readonly string _model;
        private readonly bool _dictionary;
        private readonly string _device;
        private readonly Action<string>? _report;
        private Process? _process;
        private readonly object _sync = new();

        public RuAccentWorker(string model, bool dictionary, string device, Action<string>? report = null)
        {
            _model = model;
            _dictionary = dictionary;
            _device = device;
            _report = report;
        }

        public void Start()
        {
            var script = Path.Combine(AppContext.BaseDirectory, "ruaccent_worker.py");
            var python = Environment.GetEnvironmentVariable("PYTHON") ?? "python";
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.Environment["PYTHONUTF8"] = "1";
            var cacheRoot = Path.Combine(AppContext.BaseDirectory, "model-cache");
            Directory.CreateDirectory(cacheRoot);
            start.Environment["HF_HOME"] = Path.Combine(cacheRoot, "huggingface");
            start.Environment["TORCH_HOME"] = Path.Combine(cacheRoot, "torch");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(cacheRoot, "xdg");
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--model"); start.ArgumentList.Add(_model);
            start.ArgumentList.Add("--device"); start.ArgumentList.Add(_device);
            start.ArgumentList.Add(_dictionary ? "--dictionary" : "--no-dictionary");
            _process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Python для ruaccent.");
            var errors = new StringBuilder();
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            _process.BeginErrorReadLine();
            var ready = _process.StandardOutput.ReadLine();
            if (ready != "READY")
            {
                var detail = errors.ToString();
                throw new InvalidOperationException(ready == null
                    ? $"Не удалось запустить ruaccent через '{python}'. Установите зависимости из requirements.txt. {detail}"
                    : $"ruaccent не загрузился: {ready}. {detail}");
            }
            _report?.Invoke($"Модель {_model} загружена ({_device}). Начинаю обработку…");
            Console.WriteLine($"ruaccent запущен (модель {_model}, {_device}).");
        }

        public string Process(string text)
        {
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("ruaccent не запущен.");
                var json = JsonSerializer.Serialize(text);
                process.StandardInput.WriteLine(json);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс ruaccent завершился без ответа.");
                using var result = JsonDocument.Parse(response);
                if (result.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetString() ?? "Ошибка ruaccent");
                return result.RootElement.GetProperty("text").GetString() ?? text;
            }
        }

        public void Dispose()
        {
            if (_process == null) return;
            try { _process.StandardInput.Close(); } catch { }
            try { if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }
    }
}
