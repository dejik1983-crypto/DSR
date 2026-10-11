using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private static readonly object LogLock = new();
    internal static string LogFilePath => Path.Combine(DataDirectory, "DSR-errors.txt");

    internal static void LogError(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            lock (LogLock)
                File.AppendAllText(LogFilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}",
                    new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    internal static void LogError(string context, string details)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            lock (LogLock)
                File.AppendAllText(LogFilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{details}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}",
                    new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

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

            if (args.Length == 1 && args[0] == "--probe-gpu")
            {
                var gpu = ProbeGpu();
                Console.WriteLine(gpu.Message);
                return gpu.Supported ? 0 : 1;
            }

            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            // Delay environment setup until an actual conversion is requested. CLI users
            // get the same self-healing Python install as the GUI on first conversion.
            var pythonSetup = EnsurePythonRuntime();
            Console.WriteLine($"Python-компоненты готовы: {pythonSetup}");
            var files = Directory.Exists(options.Input)
                ? Directory.EnumerateFiles(options.Input, "*.*", options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(file => Path.GetExtension(file).Equals(".fb2", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase)).ToArray()
                : new[] { options.Input };
            if (files.Length == 0)
                throw new FileNotFoundException($"В папке не найдено FB2-файлов: {options.Input}");

            using var accentizer = new RuAccentWorker(options.Model, options.Dictionary, options.Device);
            if (files.Any(file => Path.GetExtension(file).Equals(".fb2", StringComparison.OrdinalIgnoreCase)))
                accentizer.Start();
            if (options.OutputWasSpecified && options.SingleOutputFile == null)
                Directory.CreateDirectory(options.Output);
            var successes = 0;
            foreach (var input in files)
            {
                try
                {
                    var output = GetOutputPath(input, options);
                    if (Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                        ConvertTextFile(input, output, options.Model, options.Device, Console.WriteLine, (_, _) => { });
                    else
                        ConvertFile(input, output, accentizer);
                    successes++;
                    Console.WriteLine($"Готово: {input} -> {output}");
                }
                catch (Exception ex)
                {
                    LogError($"Ошибка обработки входного файла: {input}", ex);
                    Console.Error.WriteLine($"Ошибка [{input}]: {ex.Message}");
                }
            }

            Console.WriteLine($"Обработано: {successes}/{files.Length}");
            return successes == files.Length ? 0 : 1;
        }
        catch (Exception ex)
        {
            LogError("Необработанная ошибка приложения", ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal sealed record GpuProbeResult(bool Supported, string Message, bool CanInstallGpuRuntime = false);

    internal static string ApplicationDirectory => AppContext.BaseDirectory;
    internal static string DataDirectory => Path.Combine(ApplicationDirectory, "ресурсы");
    internal static string ModelCacheDirectory => Path.Combine(DataDirectory, "модели");

    private static string ExtractEmbeddedFile(string resourceName, string fileName)
    {
        var destination = Path.Combine(DataDirectory, fileName);
        Directory.CreateDirectory(DataDirectory);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"В приложении не найден встроенный файл {fileName}.");
        using var output = File.Create(destination);
        resource.CopyTo(output);
        return destination;
    }

    internal static string EnsurePythonRuntime()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ModelCacheDirectory);
        var script = ExtractEmbeddedFile("DSR.ruaccent_worker.py", "ruaccent_worker.py");
        var requirements = ExtractEmbeddedFile("DSR.requirements.txt", "requirements.txt");
        var pythonEnvironment = Path.Combine(DataDirectory, "python");
        var python = Path.Combine(pythonEnvironment, "Scripts", "python.exe");
        var systemPython = Environment.GetEnvironmentVariable("PYTHON") ?? "python";

        if (!File.Exists(python))
        {
            var version = RunCommand(systemPython, "-c", "import sys; print(sys.version_info[0] * 100 + sys.version_info[1]); raise SystemExit(0 if sys.version_info >= (3, 10) else 1)");
            if (version.ExitCode != 0)
                throw new InvalidOperationException($"Для первого запуска нужен Python 3.10+ и доступ к интернету. {version.Output}");
            var createEnvironment = RunCommand(systemPython, "-m", "venv", pythonEnvironment);
            if (createEnvironment.ExitCode != 0)
                throw new InvalidOperationException("Не удалось создать локальное Python-окружение. " + createEnvironment.Output);
        }

        var check = RunCommand(python, "-c", "import ruaccent, onnxruntime; print('ok')");
        if (check.ExitCode != 0)
        {
            // Repair only required inference packages, without optional torch/crfsuite.
            var install = RunCommand(python, "-m", "pip", "install", "--upgrade", "ruaccent @ git+https://github.com/Den4ikAI/ruaccent.git@3ac0ad5f6508f6c1a4a604220042232c70a7baf9", "onnxruntime");
            if (install.ExitCode != 0)
                throw new InvalidOperationException("Не удалось установить/исправить RUAccent и ONNX Runtime. Проверьте интернет и место на диске. " + install.Output);
            check = RunCommand(python, "-c", "import ruaccent, onnxruntime; print('onnxruntime', onnxruntime.__version__)");
            if (check.ExitCode != 0)
                throw new InvalidOperationException("Проверка RUAccent и ONNX Runtime не пройдена. " + check.Output);
        }

        return python;
    }

    internal static string InstallGpuRuntime()
    {
        var python = EnsurePythonRuntime();
        var remove = RunCommand(python, "-m", "pip", "uninstall", "-y", "onnxruntime", "onnxruntime-gpu");
        if (remove.ExitCode != 0)
            throw new InvalidOperationException("Не удалось подготовить Python-окружение к установке CUDA-пакета. " + remove.Output);

        // The official Windows wheel extra installs the matching CUDA/cuDNN runtime DLLs.
        var install = RunCommand(python, "-m", "pip", "install", "--upgrade", "onnxruntime-gpu[cuda,cudnn]");
        if (install.ExitCode != 0)
        {
            var restoreCpu = RunCommand(python, "-m", "pip", "install", "onnxruntime");
            throw new InvalidOperationException("Не удалось установить ONNX Runtime для GPU. Проверьте подключение к интернету. "
                + install.Output + (restoreCpu.ExitCode == 0 ? "\nВосстановлена CPU-версия ONNX Runtime." : "\nНе удалось восстановить CPU-версию: " + restoreCpu.Output));
        }
        return install.Output;
    }

    private static void ConfigureResourceEnvironment(ProcessStartInfo start)
    {
        var modelRoot = ModelCacheDirectory;
        var pipCache = Path.Combine(DataDirectory, "кэш", "pip");
        var temporary = Path.Combine(DataDirectory, "временные-файлы");
        var nativeTemporary = Path.Combine(Path.GetTempPath(), "DSR-temp");
        foreach (var folder in new[]
        {
            modelRoot,
            Path.Combine(modelRoot, "huggingface", "hub"),
            Path.Combine(modelRoot, "huggingface", "transformers"),
            Path.Combine(modelRoot, "torch"),
            pipCache,
            Path.Combine(DataDirectory, "кэш", "xdg"),
            temporary,
            nativeTemporary
        }) Directory.CreateDirectory(folder);
        start.Environment["PIP_CACHE_DIR"] = pipCache;
        start.Environment["HF_HOME"] = Path.Combine(modelRoot, "huggingface");
        start.Environment["HUGGINGFACE_HUB_CACHE"] = Path.Combine(modelRoot, "huggingface", "hub");
        start.Environment["TRANSFORMERS_CACHE"] = Path.Combine(modelRoot, "huggingface", "transformers");
        start.Environment["TORCH_HOME"] = Path.Combine(modelRoot, "torch");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(DataDirectory, "кэш", "xdg");
        // python-crfsuite's Windows C extension cannot open model paths containing
        // non-ASCII characters. Keep native temp staging ASCII-only; application
        // resources and model cache themselves remain alongside DSR.exe.
        start.Environment["TEMP"] = nativeTemporary;
        start.Environment["TMP"] = nativeTemporary;
    }

    private static (int ExitCode, string Output) RunCommand(string executable, params string[] arguments)
    {
        Directory.CreateDirectory(DataDirectory);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        ConfigureResourceEnvironment(start);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Не удалось запустить {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, (stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult()).Trim());
    }

    internal static GpuProbeResult ProbeGpu()
    {
        var script = ExtractEmbeddedFile("DSR.ruaccent_worker.py", "ruaccent_worker.py");
        var python = EnsurePythonRuntime();
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        ConfigureResourceEnvironment(start);
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--probe-gpu");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Python для проверки GPU.");
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        var error = errorTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            return new GpuProbeResult(false, string.IsNullOrWhiteSpace(error) ? "Проверка GPU завершилась ошибкой." : error.Trim());
        // Ignore any native/Python ANSI warning accidentally written before or after
        // the protocol response. Parse the JSON object containing our expected fields.
        var jsonStart = output.IndexOf('{');
        while (jsonStart >= 0)
        {
            var jsonEnd = output.LastIndexOf('}');
            if (jsonEnd > jsonStart)
            {
                try
                {
                    using var result = JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
                    if (result.RootElement.TryGetProperty("supported", out var supportedValue)
                        && result.RootElement.TryGetProperty("message", out var messageValue))
                    {
                        var supported = supportedValue.GetBoolean();
                        var message = messageValue.GetString() ?? "Статус GPU не получен.";
                        var canInstall = result.RootElement.TryGetProperty("can_install_gpu_runtime", out var installValue)
                            && installValue.GetBoolean();
                        return new GpuProbeResult(supported, message, canInstall);
                    }
                }
                catch (JsonException)
                {
                    // Try the next opening brace in case a warning contained braces.
                }
            }
            jsonStart = output.IndexOf('{', jsonStart + 1);
        }
        var preview = output.Length > 200 ? output[..200] : output;
        return new GpuProbeResult(false, $"Проверка GPU вернула некорректный ответ. {preview}");
    }

    internal static void PrepareInteractive(string model, string device, Action<string> report)
    {
        _ = EnsurePythonRuntime();
        report("Python и зависимости проверены.");
        using var accentizer = new RuAccentWorker(model, true, device, report);
        accentizer.Start();
        accentizer.EnsureStarted();
        _ = accentizer.Process("Проверка готовности.");
        report($"Подготовка завершена: модель {model}, устройство {device} проверены.");
    }

    internal static void ConvertInteractive(string input, string output, string model, string device,
        bool includeIllustrations, Action<string> report,
        Action<int, int> reportProgress)
    {
        if (Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            ConvertTextFile(input, output, model, device, report, reportProgress);
            return;
        }
        using var accentizer = new RuAccentWorker(model, true, device, report);
        accentizer.Start();
        ConvertFile(input, output, accentizer, includeIllustrations, reportProgress);
        report($"Готово: {output}");
    }

    private static void ConvertTextFile(string inputPath, string outputPath, string model, string device,
        Action<string> report, Action<int, int> reportProgress)
    {
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var lines = text.Split('\n');
        using var accentizer = new RuAccentWorker(model, true, device, report);
        accentizer.Start();
        accentizer.EnsureStarted();
        reportProgress(0, lines.Length);
        var result = new StringBuilder(text.Length + 64);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ending = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            var content = ending.Length == 0 ? line : line[..^1];
            if (!string.IsNullOrWhiteSpace(content))
            {
                try
                {
                    var modelOutput = RuAccentMarker.Replace(accentizer.Process(content), "\u0301");
                    result.Append(EnsureDialogueInitialWordAccent(content, modelOutput, accentizer));
                }
                catch (Exception ex)
                {
                    // Keep the original line untouched so a single problem line
                    // never aborts the whole conversion.
                    LogError($"Строка оставлена без изменений: {TruncateForLog(content)}", ex);
                    result.Append(content);
                }
            }
            else result.Append(content);
            result.Append(ending);
            if (i + 1 < lines.Length) result.Append('\n');
            if (i == lines.Length - 1 || (i + 1) % 10 == 0) reportProgress(i + 1, lines.Length);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, EncodeText(result.ToString(), sourceBytes));
        report($"Готово: {outputPath}");
    }

    private static string EnsureDialogueInitialWordAccent(string source, string modelOutput, RuAccentWorker accentizer)
    {
        var start = 0;
        while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
        if (start >= source.Length || source[start] is not ('—' or '–' or '―')) return modelOutput;
        start++;
        while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
        var end = start;
        // The source text may already contain a combining acute accent (\u0301),
        // so the scan must treat it as a word character too, otherwise the first
        // spoken word is truncated at the accent (e.g. «Воро́ны» -> «Воро»).
        while (end < source.Length && IsWordCharacter(source[end])) end++;
        if (end == start) return modelOutput;

        var originalWord = source[start..end];
        var expected = NormalizeWordLetters(originalWord);
        var outputStart = SkipDialoguePrefix(modelOutput, 0);
        var actualEnd = outputStart;
        while (actualEnd < modelOutput.Length && IsWordCharacter(modelOutput[actualEnd])) actualEnd++;
        var outputWord = modelOutput[outputStart..actualEnd];
        if (NormalizeWordLetters(outputWord).Equals(expected, StringComparison.OrdinalIgnoreCase))
            return modelOutput;

        // Retry the whole dialogue via the worker's lossless word-alignment fallback.
        // That fallback accentuates all words and never drops letters.
        var retriedOutput = RuAccentMarker.Replace(accentizer.Process(source), "\u0301");
        var retriedStart = SkipDialoguePrefix(retriedOutput, 0);
        var retriedEnd = retriedStart;
        while (retriedEnd < retriedOutput.Length && IsWordCharacter(retriedOutput[retriedEnd])) retriedEnd++;
        var retriedWord = retriedOutput[retriedStart..retriedEnd];
        if (NormalizeWordLetters(retriedWord).Equals(expected, StringComparison.OrdinalIgnoreCase))
            return retriedOutput;

        // Last resort: keep the first spoken word exactly as written in the source
        // so the conversion never loses letters because of a single word.
        if (actualEnd > outputStart)
            return modelOutput[..outputStart] + originalWord + modelOutput[actualEnd..];
        return modelOutput;
    }

    // The dialogue dash (—, –, ―) may be followed by the first spoken word.
    // Skip the leading whitespace and the dash to reach that word.
    private static int SkipDialoguePrefix(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (start < text.Length && text[start] is '—' or '–' or '―')
        {
            start++;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        }
        return start;
    }

    // A word may contain the combining acute accent (\u0301) placed right before
    // the stressed vowel, so it must not stop the scan: it is a word character too.
    private static bool IsWordCharacter(char ch) =>
        char.IsLetter(ch) ||
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark;

    // Compare words ignoring the accent marks and the ё/е distinction.
    private static string NormalizeWordLetters(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var ch in word)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(ch is 'ё' or 'Ё' ? 'е' : ch);
        }
        return builder.ToString();
    }

    // Shorten a text fragment for the error log so long paragraphs stay readable.
    private static string TruncateForLog(string text)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 120 ? singleLine : singleLine[..120] + "…";
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1251).GetString(bytes); }
    }

    private static byte[] EncodeText(string text, byte[] original)
    {
        if (original.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        if (original.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();
        if (original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        try
        {
            var encoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            _ = encoding.GetBytes(text);
            return encoding.GetBytes(text);
        }
        catch (EncoderFallbackException) { return new UTF8Encoding(false).GetBytes(text); }
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
        var name = Path.GetFileNameWithoutExtension(input) + options.Suffix + Path.GetExtension(input);
        var candidate = Path.Combine(folder, name);
        var source = Path.GetFullPath(input);
        if (string.Equals(Path.GetFullPath(candidate), source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь выходного файла совпадает с исходным. Укажите другой суффикс или папку результата.");
        return candidate;
    }

    private static void ConvertFile(string inputPath, string outputPath, RuAccentWorker accentizer,
        bool includeIllustrations = true, Action<int, int>? reportProgress = null)
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
        var eligibleText = root.DescendantsAndSelf()
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
            // Модель загружается только при первом реальном запросе — тогда результат
            // заведомо появится, а первая загрузка не завершит процесс раньше времени.
            accentizer.EnsureStarted();
            try
            {
                var modelOutput = RuAccentMarker.Replace(accentizer.Process(sourceText), "\u0301");
                var accented = EnsureDialogueInitialWordAccent(sourceText, modelOutput, accentizer);
                if (!string.Equals(accented, sourceText, StringComparison.Ordinal))
                {
                    textNode.Value = accented;
                    changed++;
                }
                if (!string.Equals(accented, modelOutput, StringComparison.Ordinal))
                    normalized++;
            }
            catch (Exception ex)
            {
                // Keep the original fragment untouched so a single problem fragment
                // never aborts the whole conversion.
                LogError($"Фрагмент оставлен без изменений: {TruncateForLog(sourceText)}", ex);
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
        Console.WriteLine("Конвертер FB2/TXT: расстановка ударений через ruaccent; FB2 проверяется парсером DeepReader.");
        Console.WriteLine("Использование:");
        Console.WriteLine("  DSR.exe <файл.fb2 | файл.txt | папка> [-o <папка | файл.fb2 | файл.txt>] [--recursive]");
        Console.WriteLine("      [--suffix _ударения] [--model turbo3.1] [--no-dictionary] [--device CPU|CUDA]\n");
        Console.WriteLine("По умолчанию создаётся копия рядом с источником с суффиксом _ударения.");
        Console.WriteLine("При передаче папки в -o файлы помещаются в неё; структура подпапок при --recursive сохраняется.");
        Console.WriteLine($"Ресурсы DSR хранятся рядом с приложением: {DataDirectory}");
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

        public void EnsureStarted()
        {
            if (_process != null) return;
            Start();
        }

        public void Start()
        {
            if (_process != null) return;
            var script = ExtractEmbeddedFile("DSR.ruaccent_worker.py", "ruaccent_worker.py");
            var python = EnsurePythonRuntime();
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
            start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            start.Environment["USE_TORCH"] = "1";
            ConfigureResourceEnvironment(start);
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--model"); start.ArgumentList.Add(_model);
            start.ArgumentList.Add("--device"); start.ArgumentList.Add(_device);
            start.ArgumentList.Add(_dictionary ? "--dictionary" : "--no-dictionary");
            _process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Python для ruaccent.");
            var errors = new StringBuilder();
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("DSR_PROGRESS:", StringComparison.Ordinal))
                    _report?.Invoke(e.Data["DSR_PROGRESS:".Length..]);
                else lock (errors) errors.AppendLine(e.Data);
            };
            _process.BeginErrorReadLine();
            var ready = _process.StandardOutput.ReadLine();
            if (ready != "READY")
            {
                var detail = errors.ToString();
                var failure = new InvalidOperationException(ready == null
                    ? $"Не удалось запустить ruaccent через '{python}'. Установите зависимости из requirements.txt. {detail}"
                    : $"ruaccent не загрузился: {ready}. {detail}");
                LogError("Загрузка worker RUAccent завершилась ошибкой", failure);
                if (_process.HasExited && _process.ExitCode != 0)
                    LogError("RUAccent worker завершился с ошибкой", $"Exit code: {_process.ExitCode}{Environment.NewLine}{detail}");
                throw failure;
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
