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
    private static readonly System.Text.RegularExpressions.Regex PunctuationTokens = new(
        @"[\p{L}\p{M}\p{N}_]+|[^\p{L}\p{M}\p{N}_]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Подробный журнал правок: помогает разобрать, почему конкретная книга не применилась.
    internal static string ReviewLogPath => Path.Combine(DataDirectory, "DSR-правила.log");

    internal static void LogReview(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            lock (LogLock)
                File.AppendAllText(ReviewLogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    private static readonly System.Text.RegularExpressions.Regex WordTokens = new(
        @"[\p{L}\p{M}\p{N}_]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex NotCyrillicWord = new(
        @"^[^а-яёА-ЯЁ]+$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex RussianVowel = new(
        "[аеёиоуыэюя]", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static string LogFilePath => Path.Combine(DataDirectory, "DSR-errors.txt");
    internal static string UserDictionaryPath => Path.Combine(DataDirectory, "пользовательские-ударения.tsv");
    internal static string TrainingLogPath => Path.Combine(DataDirectory, "накопленные-примеры.jsonl");
    internal static string CorrectionsFor(string reviewPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reviewPath))!, Path.GetFileNameWithoutExtension(reviewPath).Replace("_проверка-слов", "_исправления") + ".txt");
    internal static string ReviewPathFor(string inputPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath))!, Path.GetFileNameWithoutExtension(inputPath) + "_проверка-слов.txt");
    private static readonly Lazy<Dictionary<string, string>> UserDictionary = new(LoadUserDictionary);
    internal static string FormCorrectionsPathFor(string reviewPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reviewPath))!, Path.GetFileNameWithoutExtension(reviewPath).Replace("_проверка-слов", "_формы") + ".json");

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

    internal sealed class ConversionControl
    {
        private readonly ManualResetEventSlim _resume = new(true);
        private int _stopRequested;
        internal bool IsPaused => !_resume.IsSet && !IsStopped;
        internal bool IsStopped => Volatile.Read(ref _stopRequested) != 0;
        internal void Pause() => _resume.Reset();
        internal void Resume() => _resume.Set();
        internal void Stop() { Interlocked.Exchange(ref _stopRequested, 1); _resume.Set(); }
        internal void Checkpoint()
        {
            _resume.Wait();
            if (IsStopped) throw new OperationCanceledException("Обработка остановлена пользователем.");
        }
    }

    internal sealed class ConversionDiagnostics
    {
        internal string Input { get; init; } = "";
        internal string Output { get; init; } = "";
        internal string Format { get; init; } = "";
        internal string Model { get; init; } = "";
        internal string Device { get; init; } = "";
        internal long InputBytes { get; set; }
        internal long OutputBytes { get; set; }
        internal long CharacterCount { get; set; }
        internal int TotalUnits { get; set; }
        internal int CompletedUnits { get; set; }
        internal int FailedUnits { get; set; }
        internal int UnrecognizedWordCount { get; set; }
        internal long ImagePayloadCharacterCount { get; set; }
        internal double CpuPercent { get; set; }
        internal long PeakWorkingSetBytes { get; set; }
        internal TimeSpan InferenceElapsed { get; set; }
        internal long InferenceCalls { get; set; }
        internal List<string> PerformanceEvents { get; } = new();
        internal TimeSpan Elapsed { get; set; }
        internal string Status { get; set; } = "Выполняется";
        internal List<string> Events { get; } = new();
    }

    internal static void LogConversion(string input, long bytes, long characters, TimeSpan elapsed, TimeSpan? estimatedRemaining, string status)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {status}: {input} | {bytes / 1024d / 1024d:0.00} МБ | {characters:N0} символов | обработка {elapsed:hh\\:mm\\:ss} | осталось {(estimatedRemaining.HasValue ? estimatedRemaining.Value.ToString(@"hh\\:mm\\:ss") : "н/д")}{Environment.NewLine}";
            lock (LogLock) File.AppendAllText(Path.Combine(DataDirectory, "DSR-processing.log"), line, new UTF8Encoding(false));
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
                try
                {
                    var application = new System.Windows.Application();
                    application.Run(new MainWindow());
                    return 0;
                }
                catch (Exception ex)
                {
                    LogError("Ошибка запуска интерфейса", ex);
                    System.Windows.MessageBox.Show(ex.Message, "Ошибка запуска DSR", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return 1;
                }
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
                throw new FileNotFoundException($"В папке не найдено FB2- или TXT-файлов: {options.Input}");

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
                    {
                        var diagnostics = new ConversionDiagnostics
                        {
                            Input = input, Output = output, Format = ".TXT", Model = options.Model, Device = options.Device,
                            InputBytes = new FileInfo(input).Length, CharacterCount = DecodeText(File.ReadAllBytes(input)).Length
                        };
                        var startedAt = DateTimeOffset.Now;
                        var timer = Stopwatch.StartNew();
                        using var currentProcess = Process.GetCurrentProcess();
                        try { ConvertTextFile(input, output, options.Model, options.Device, Console.WriteLine, (_, _) => { }, new ConversionControl(), diagnostics); diagnostics.Status = "Завершено"; }
                        catch (Exception ex) { diagnostics.Status = ex is OperationCanceledException ? "Остановлено пользователем" : "Ошибка"; diagnostics.Events.Add(ex.ToString()); throw; }
                        finally { timer.Stop(); diagnostics.Elapsed = timer.Elapsed; diagnostics.OutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0; diagnostics.PeakWorkingSetBytes = currentProcess.PeakWorkingSet64; diagnostics.CpuPercent = timer.Elapsed.TotalMilliseconds > 0 ? currentProcess.TotalProcessorTime.TotalMilliseconds * 100 / timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount : 0; WriteConversionReport(output, diagnostics, startedAt); }
                    }
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
        bool includeIllustrations, Action<string> report, Action<int, int> reportProgress, ConversionControl control)
    {
        var bytes = new FileInfo(input).Length;
        var sourceBytes = File.ReadAllBytes(input);
        var sourceText = Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? DecodeText(sourceBytes)
            : DecodeXml(sourceBytes);
        var diagnostics = new ConversionDiagnostics
        {
            Input = Path.GetFullPath(input), Output = Path.GetFullPath(output),
            Format = Path.GetExtension(input).ToUpperInvariant(), Model = model, Device = device,
            InputBytes = bytes, CharacterCount = sourceText.Length
        };
        var conversionTimer = Stopwatch.StartNew();
        var startTimestamp = DateTimeOffset.Now;
        try
        {
            control.Checkpoint();
            report($"DIAGNOSTICS:{Path.GetFullPath(output)}");
            if (Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                ConvertTextFile(input, output, model, device, report, reportProgress, control, diagnostics);
            else
            {
                using var accentizer = new RuAccentWorker(model, true, device, report, diagnostics);
                accentizer.Start();
                control.Checkpoint();
                ConvertFile(input, output, accentizer, includeIllustrations, reportProgress, control, diagnostics);
                report($"Готово: {output}");
            }
            diagnostics.Status = "Завершено";
        }
        catch (OperationCanceledException)
        {
            diagnostics.Status = "Остановлено пользователем";
            diagnostics.OutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Status = "Ошибка";
            diagnostics.OutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            diagnostics.Events.Add("Критическая ошибка: " + ex);
            throw;
        }
        finally
        {
            conversionTimer.Stop();
            diagnostics.Elapsed = conversionTimer.Elapsed;
            using (var currentProcess = Process.GetCurrentProcess())
            {
                diagnostics.PeakWorkingSetBytes = Math.Max(diagnostics.PeakWorkingSetBytes, currentProcess.PeakWorkingSet64);
                diagnostics.CpuPercent = conversionTimer.Elapsed.TotalMilliseconds > 0
                    ? currentProcess.TotalProcessorTime.TotalMilliseconds * 100 / conversionTimer.Elapsed.TotalMilliseconds / Environment.ProcessorCount
                    : 0;
            }
            if (diagnostics.PerformanceEvents.Count == 0)
                diagnostics.Events.Add("Worker не сообщил данные о фактически загруженных ONNX execution providers.");
            WriteConversionReport(output, diagnostics, startTimestamp);
        }
    }

    private static void WriteConversionReport(string outputPath, ConversionDiagnostics diagnostics, DateTimeOffset startTimestamp)
    {
        var reportPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, Path.GetFileNameWithoutExtension(outputPath) + "_статистика.txt");
        var report = new StringBuilder();
        report.AppendLine("DSR — отчёт конвертации и диагностики");
        report.AppendLine($"Дата начала: {startTimestamp:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Дата завершения отчёта: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Статус: {diagnostics.Status}");
        report.AppendLine($"Вход: {diagnostics.Input}");
        report.AppendLine($"Выход: {diagnostics.Output}");
        report.AppendLine($"Формат: {diagnostics.Format}");
        report.AppendLine($"Модель: {diagnostics.Model}; устройство: {diagnostics.Device}");
        report.AppendLine($"Входной объём: {diagnostics.InputBytes} байт ({diagnostics.InputBytes / 1024d / 1024d:0.000} МБ)");
        report.AppendLine($"Выходной объём: {diagnostics.OutputBytes} байт ({diagnostics.OutputBytes / 1024d / 1024d:0.000} МБ)");
        report.AppendLine($"Символов текста для обработки: {diagnostics.CharacterCount:N0}");
        report.AppendLine($"Символов внутри FB2 binary/изображений (пропущено): {diagnostics.ImagePayloadCharacterCount:N0}");
        report.AppendLine($"Обработано фрагментов: {diagnostics.CompletedUnits:N0}/{diagnostics.TotalUnits:N0}");
        report.AppendLine($"Ошибок отдельных фрагментов: {diagnostics.FailedUnits:N0}");
        report.AppendLine($"Слов в отчёте проверки: {diagnostics.UnrecognizedWordCount:N0}");
        report.AppendLine($"Время обработки: {FormatDuration(diagnostics.Elapsed)}");
        report.AppendLine($"Скорость: {(diagnostics.Elapsed.TotalSeconds > 0 ? diagnostics.CharacterCount / diagnostics.Elapsed.TotalSeconds : 0):0.0} символов/с; {(diagnostics.Elapsed.TotalSeconds > 0 ? diagnostics.CompletedUnits / diagnostics.Elapsed.TotalSeconds : 0):0.00} фрагментов/с");
        report.AppendLine($"Пик RAM процесса: {diagnostics.PeakWorkingSetBytes / 1024d / 1024d:0.0} МБ");
        report.AppendLine($"Инференс ONNX: {FormatDuration(diagnostics.InferenceElapsed)} всего, {diagnostics.InferenceCalls:N0} вызов");
        report.AppendLine($"ONNX sessions: {string.Join("; ", diagnostics.PerformanceEvents)}");
        report.AppendLine($"Процесс: {Environment.ProcessPath}; ОС: {Environment.OSVersion}; .NET: {Environment.Version}");
        report.AppendLine($"Процессор: {Environment.ProcessorCount} логических ядер");
        report.AppendLine("События / исключения:");
        if (diagnostics.Events.Count == 0) report.AppendLine("Нет.");
        else foreach (var item in diagnostics.Events) report.AppendLine(item);
        try { File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { LogError($"Не удалось записать отчёт диагностики {reportPath}", ex); }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (long)duration.TotalHours;
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static void ConvertTextFile(string inputPath, string outputPath, string model, string device,
        Action<string> report, Action<int, int> reportProgress, ConversionControl control, ConversionDiagnostics diagnostics)
    {
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var lines = text.Split('\n');
        diagnostics.TotalUnits = lines.Length;
        diagnostics.CharacterCount = text.Length;
        diagnostics.InputBytes = sourceBytes.LongLength;
        var conversionTimer = Stopwatch.StartNew();
        var bytesIn = sourceBytes.LongLength;
        var wordReview = new List<string>();
        using var accentizer = new RuAccentWorker(model, true, device, report);
        accentizer.Start();
        accentizer.EnsureStarted();
        reportProgress(0, lines.Length);
        var result = new StringBuilder(text.Length + 64);
        var completed = 0;
        TimeSpan? estimatedRemaining = null;
        for (var i = 0; i < lines.Length; i++)
        {
            control.Checkpoint();
            var line = lines[i];
            var ending = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            var content = ending.Length == 0 ? line : line[..^1];
            if (!string.IsNullOrWhiteSpace(content))
            {
                try
                {
                    var modelOutput = RuAccentMarker.Replace(accentizer.Process(ApplyUserDictionary(content)), "\u0301");
                    var accented = EnsureDialogueInitialWordAccent(content, modelOutput, accentizer);
                    result.Append(EnsureOriginalPunctuation(content, accented));
                    var reviewCountBefore = wordReview.Count;
                    CollectUnrecognizedWords(content, accented, wordReview);
                    diagnostics.UnrecognizedWordCount += CountReviewWords(wordReview.Skip(reviewCountBefore));
                }
                catch (Exception ex)
                {
                    diagnostics.FailedUnits++;
                    diagnostics.Events.Add($"Строка {i + 1}: {ex}");
                    // Keep the original line untouched so a single problem line
                    // never aborts the whole conversion.
                    LogError($"Строка оставлена без изменений: {TruncateForLog(content)}", ex);
                    result.Append(content);
                }
            }
            else result.Append(content);
            result.Append(ending);
            if (i + 1 < lines.Length) result.Append('\n');
            completed = i + 1;
            diagnostics.CompletedUnits = completed;
            if (completed == lines.Length || completed % 10 == 0)
            {
                estimatedRemaining = completed > 0
                    ? TimeSpan.FromSeconds(conversionTimer.Elapsed.TotalSeconds * (lines.Length - completed) / completed)
                    : null;
                report($"Прогресс {completed}/{lines.Length}; прошло {conversionTimer.Elapsed:hh\\:mm\\:ss}; осталось {(estimatedRemaining.HasValue ? estimatedRemaining.Value.ToString(@"hh\\:mm\\:ss") : "расчёт…")}");
                LogConversion(inputPath, bytesIn, text.Length, conversionTimer.Elapsed, estimatedRemaining, $"Обработка {completed}/{lines.Length}");
                reportProgress(completed, lines.Length);
            }
        }
        control.Checkpoint();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, EncodeText(result.ToString(), sourceBytes));
        diagnostics.OutputBytes = new FileInfo(outputPath).Length;
        diagnostics.Elapsed = conversionTimer.Elapsed;
        diagnostics.UnrecognizedWordCount = CountReviewWords(wordReview);
        WriteWordReview(inputPath, wordReview);
        conversionTimer.Stop();
        LogConversion(inputPath, bytesIn, text.Length, conversionTimer.Elapsed, TimeSpan.Zero, "Завершено");
        report($"Готово: {outputPath}");
    }

    private static string EnsureOriginalPunctuation(string source, string converted)
    {
        var sourceTokens = PunctuationTokens.Matches(source).Select(match => match.Value).ToArray();
        var outputTokens = PunctuationTokens.Matches(converted).Select(match => match.Value).ToArray();
        var sourcePunctuation = sourceTokens.Where(token => !WordTokens.IsMatch(token, 0)).ToArray();
        var outputPunctuation = outputTokens.Where(token => !WordTokens.IsMatch(token, 0)).ToArray();
        var outputWordsForAlignment = outputTokens.Where(token => WordTokens.IsMatch(token, 0)).ToArray();
        if (outputWordsForAlignment.Length != sourceTokens.Count(token => WordTokens.IsMatch(token, 0))) return converted;
        if (sourcePunctuation.SequenceEqual(outputPunctuation, StringComparer.Ordinal))
        {
            // Restore positions too, not just the punctuation inventory (notably the
            // final periods of ellipses), while preserving each accented word.
            var restored = new StringBuilder(source.Length + 16);
            var wordIndex = 0;
            for (var i = 0; i < sourceTokens.Length; i++)
                restored.Append(WordTokens.IsMatch(sourceTokens[i], 0) ? outputWordsForAlignment[wordIndex++] : sourceTokens[i]);
            return restored.ToString();
        }

        // Preserve punctuation exactly while allowing the model to accent words:
        // align alternating word/punctuation tokens and use source punctuation.
        var sourceWords = sourceTokens.Where(token => WordTokens.IsMatch(token, 0)).ToArray();
        var outputWords = outputTokens.Where(token => WordTokens.IsMatch(token, 0)).ToArray();
        if (sourceWords.Length != outputWords.Length) return converted;
        var rebuilt = new StringBuilder(source.Length + 16);
        var sourceIndex = 0;
        var outputIndex = 0;
        var sourceWordIndex = 0;
        var outputWordIndex = 0;
        while (sourceIndex < sourceTokens.Length && outputIndex < outputTokens.Length)
        {
            if (WordTokens.IsMatch(sourceTokens[sourceIndex]) && WordTokens.IsMatch(outputTokens[outputIndex]))
            {
                if (!NormalizeWordLetters(sourceTokens[sourceIndex]).Equals(NormalizeWordLetters(outputTokens[outputIndex]), StringComparison.OrdinalIgnoreCase)) return converted;
                rebuilt.Append(outputTokens[outputIndex++]); sourceIndex++; sourceWordIndex++; outputWordIndex++;
                continue;
            }
            if (!WordTokens.IsMatch(sourceTokens[sourceIndex])) rebuilt.Append(sourceTokens[sourceIndex++]);
            if (outputIndex < outputTokens.Length && !WordTokens.IsMatch(outputTokens[outputIndex])) outputIndex++;
        }
        while (sourceIndex < sourceTokens.Length) rebuilt.Append(sourceTokens[sourceIndex++]);
        if (sourceWordIndex != sourceWords.Length || outputWordIndex != outputWords.Length) return converted;
        return rebuilt.ToString();
    }

    internal sealed record WordReviewEntry(string Word, string Context, string Proposed);
    internal sealed record DeclensionCorrection(string Lemma, string Form, string Case, string Context);
    internal sealed record WordReviewCorrection(string SourceWord, string Corrected, string Context);
    internal sealed record WordReviewCaseCorrection(string SourceWord, string Corrected, string Context, int ContextOccurrence);
    internal sealed record WordReviewPatternCorrection(string SourceWord, string Corrected, string? ExcludedContext, int ExcludedOccurrence);

    private static void CollectUnrecognizedWords(string source, string converted, List<string> report)
    {
        var sourceMatches = WordTokens.Matches(source).Cast<System.Text.RegularExpressions.Match>().ToArray();
        var outputMatches = WordTokens.Matches(converted).Cast<System.Text.RegularExpressions.Match>().ToArray();
        if (sourceMatches.Length != outputMatches.Length) return;
        var words = new List<string>();
        for (var i = 0; i < sourceMatches.Length; i++)
        {
            var original = sourceMatches[i].Value;
            var normalized = NormalizeWordLetters(original);
            if (NotCyrillicWord.IsMatch(original) || normalized.Length < 2) continue;
            var convertedWord = outputMatches[i].Value;
            var hasStress = convertedWord.Contains('\u0301');
            var hasYo = original.Contains('ё', StringComparison.OrdinalIgnoreCase);
            var vowelCount = RussianVowel.Matches(normalized).Count;
            // Ё is inherently stressed in Russian orthography. A word with one
            // vowel has no competing stress position, so neither belongs in review.
            if (!hasStress && !hasYo && vowelCount > 1) words.Add(original);
        }
        if (words.Count > 0) report.Add($"Фрагмент: {source}{Environment.NewLine}Не распознаны: {string.Join(", ", words.Distinct(StringComparer.OrdinalIgnoreCase))}{Environment.NewLine}");
    }

    internal static List<WordReviewEntry> ReadWordReview(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Contains("Правка [", StringComparison.Ordinal))
        {
            var structured = new List<WordReviewEntry>();
            string? currentContext = null;
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Фрагмент: ", StringComparison.Ordinal)) currentContext = line[10..];
                else if (line.StartsWith("Правка [", StringComparison.Ordinal) && currentContext != null)
                {
                    var close = line.IndexOf("]: ", StringComparison.Ordinal);
                    if (close > 8) structured.Add(new WordReviewEntry(line[8..close], currentContext, line[(close + 3)..]));
                }
            }
            return AddContextOccurrences(structured
                .GroupBy(entry => NormalizeWordLetters(entry.Word).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group)
                .ToList());
        }
        var entries = new List<WordReviewEntry>();
        string? context = null;
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith("Фрагмент: ", StringComparison.Ordinal)) context = line[10..];
            else if (line.StartsWith("Не распознаны: ", StringComparison.Ordinal) && context != null)
            {
                var contextText = context;
                foreach (var word in line[14..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    entries.Add(new WordReviewEntry(word, contextText, word));
                context = null;
            }
        }
        return AddContextOccurrences(entries
            .GroupBy(entry => NormalizeWordLetters(entry.Word).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group)
            .ToList());
    }

    internal static (string Context, int Occurrence) SplitReviewContext(string encodedContext)
    {
        var marker = encodedContext.LastIndexOf('\u0001');
        if (marker < 0 || !int.TryParse(encodedContext[(marker + 1)..], out var occurrence)) return (encodedContext, 0);
        return (encodedContext[..marker], occurrence);
    }

    private static List<WordReviewEntry> AddContextOccurrences(List<WordReviewEntry> entries)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        return entries.Select(entry =>
        {
            var visibleContext = SplitReviewContext(entry.Context).Context;
            occurrences.TryGetValue(visibleContext, out var occurrence);
            occurrences[visibleContext] = occurrence + 1;
            return entry with { Context = $"{visibleContext}\u0001{occurrence}" };
        }).ToList();
    }

    internal static string FormatWordReview(IEnumerable<WordReviewEntry> entries)
    {
        var list = entries.ToArray();
        var output = new StringBuilder("Слова без автоматически определённого ударения. Контекст приведён целым фрагментом. Исправьте вариант ниже вручную." + Environment.NewLine + Environment.NewLine);
        if (list.Length == 0) return "Нераспознанных слов не найдено." + Environment.NewLine;
        foreach (var group in list.GroupBy(item => SplitReviewContext(item.Context).Context))
        {
            output.AppendLine("Фрагмент: " + group.Key);
            output.AppendLine("Не распознаны: " + string.Join(", ", group.Select(item => item.Word)));
            foreach (var item in group)
            {
                var proposed = UserDictionary.Value.TryGetValue(NormalizeWordLetters(item.Word).ToLowerInvariant(), out var saved) ? saved : item.Word;
                output.AppendLine($"Правка [{item.Word}]: {proposed}");
            }
            output.AppendLine();
        }
        return output.ToString();
    }

    internal static bool IsValidCorrection(string sourceWord, string corrected)
    {
        return !string.IsNullOrWhiteSpace(corrected) && !corrected.Any(char.IsWhiteSpace) &&
            NormalizeWordLetters(sourceWord).Equals(NormalizeWordLetters(corrected), StringComparison.OrdinalIgnoreCase) &&
            IsValidStressedWord(corrected);
    }

    internal static bool IsValidDeclensionForm(string form) => IsValidStressedWord(form);

    internal static bool IsValidStressedWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Any(char.IsWhiteSpace) ||
            !word.Any(ch => "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ".Contains(ch))) return false;
        var accents = word.Count(ch => ch == '\u0301');
        if (accents > 1) return false;
        if (accents == 0) return word.Contains('ё', StringComparison.OrdinalIgnoreCase);
        var index = word.IndexOf('\u0301');
        if (index == 0 || !"аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(word[index - 1])) return false;
        // Ё is inherently stressed and does not take a separate acute mark.
        return char.ToLowerInvariant(word[index - 1]) != 'ё';
    }

    internal static List<DeclensionCorrection> ReadDeclensionForms(string reviewPath, string word)
    {
        var path = FormCorrectionsPathFor(reviewPath);
        if (!File.Exists(path)) return new List<DeclensionCorrection>();
        try
        {
            return (JsonSerializer.Deserialize<List<DeclensionCorrection>>(File.ReadAllText(path)) ?? new())
                .Where(item => item.Lemma.Equals(word, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (JsonException ex)
        {
            LogError($"Повреждён файл форм слов {path}", ex);
            return new List<DeclensionCorrection>();
        }
    }

    // Все сохранённые словоформы разом: нужны для пакетного применения правок.
    internal static List<DeclensionCorrection> ReadAllDeclensionForms(string? reviewPath)
    {
        if (string.IsNullOrWhiteSpace(reviewPath)) return new List<DeclensionCorrection>();
        var path = FormCorrectionsPathFor(reviewPath);
        if (!File.Exists(path)) return new List<DeclensionCorrection>();
        return JsonSerializer.Deserialize<List<DeclensionCorrection>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<DeclensionCorrection>();
    }

    internal static void SaveDeclensionForms(string reviewPath, WordReviewEntry entry, IEnumerable<DeclensionCorrection> forms)
    {
        var path = FormCorrectionsPathFor(reviewPath);
        var all = File.Exists(path)
            ? JsonSerializer.Deserialize<List<DeclensionCorrection>>(File.ReadAllText(path)) ?? new List<DeclensionCorrection>()
            : new List<DeclensionCorrection>();
        all.RemoveAll(item => item.Lemma.Equals(entry.Word, StringComparison.OrdinalIgnoreCase));
        var saved = forms.ToList();
        all.AddRange(saved);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
        foreach (var form in saved)
        {
            var key = NormalizeWordLetters(form.Form).ToLowerInvariant();
            UserDictionary.Value[key] = form.Form;
        }
        File.WriteAllLines(UserDictionaryPath, UserDictionary.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "\t" + pair.Value), new UTF8Encoding(true));
        var examples = string.Join(Environment.NewLine, saved.Select(form => JsonSerializer.Serialize(new { type = "declension", lemma = form.Lemma, form = form.Form, grammaticalCase = form.Case, context = form.Context, created = DateTimeOffset.Now })));
        if (examples.Length > 0) lock (LogLock) File.AppendAllText(TrainingLogPath, examples + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static void SaveUserCorrection(WordReviewEntry entry, string corrected)
    {
        if (!IsValidCorrection(entry.Word, corrected)) throw new ArgumentException("Некорректный вариант ударения.");
        Directory.CreateDirectory(DataDirectory);
        var key = NormalizeWordLetters(entry.Word).ToLowerInvariant();
        var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(UserDictionaryPath))
        {
            foreach (var line in File.ReadAllLines(UserDictionaryPath, Encoding.UTF8))
            {
                var columns = line.Split('\t', 2);
                if (columns.Length == 2 && columns[0].Length > 0) dict[columns[0]] = columns[1];
            }
        }
        dict[key] = corrected;
        File.WriteAllLines(UserDictionaryPath, dict.Select(item => item.Key + "\t" + item.Value), new UTF8Encoding(true));
        UserDictionary.Value[key] = corrected;
        var example = JsonSerializer.Serialize(new { source = entry.Word, corrected, context = entry.Context, normalized = key, created = DateTimeOffset.Now });
        lock (LogLock) File.AppendAllText(TrainingLogPath, example + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static string SaveDeclensionForm(WordReviewEntry entry, string form, string grammaticalCase)
    {
        if (!IsValidDeclensionForm(form)) throw new ArgumentException("Форма должна быть русским словом с одним ударением или буквой «ё».");
        var key = NormalizeWordLetters(form).ToLowerInvariant();
        Directory.CreateDirectory(DataDirectory);
        UserDictionary.Value[key] = form;
        var lines = UserDictionary.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "\t" + pair.Value);
        File.WriteAllLines(UserDictionaryPath, lines, new UTF8Encoding(true));
        var correction = new DeclensionCorrection(entry.Word, form, grammaticalCase, entry.Context);
        var example = JsonSerializer.Serialize(new { type = "declension", lemma = correction.Lemma, form = correction.Form, grammaticalCase = correction.Case, context = correction.Context, created = DateTimeOffset.Now });
        lock (LogLock) File.AppendAllText(TrainingLogPath, example + Environment.NewLine, new UTF8Encoding(false));
        return key;
    }

    internal static int ApplyWordCorrectionsToFb2(string inputPath, string outputPath, IEnumerable<DeclensionCorrection> corrections)
    {
        var correctionMap = corrections.GroupBy(correction => NormalizeWordLetters(correction.Form).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Form, StringComparer.OrdinalIgnoreCase);
        LogReview($"Применение сохранённых словоформ к {Path.GetFileName(inputPath)}: слов {correctionMap.Count}");
        var applied = ApplyWordReplacementMapToFb2(inputPath, outputPath, correctionMap);
        LogReview($"Словоформы: заменено вхождений {applied}");
        return applied;
    }

    internal static int ApplyReviewCorrectionsToFb2(string outputPath, IEnumerable<WordReviewCorrection> corrections)
    {
        var correctionMap = corrections
            .GroupBy(correction => NormalizeWordLetters(correction.SourceWord).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Corrected, StringComparer.OrdinalIgnoreCase);
        return ApplyWordReplacementMapToFb2(outputPath, outputPath, correctionMap);
    }

    internal static Dictionary<string, int> CountReviewWordOccurrences(string outputPath, IEnumerable<string> words)
    {
        var counts = words.Distinct(StringComparer.Ordinal).ToDictionary(word => word, _ => 0, StringComparer.Ordinal);
        if (!File.Exists(outputPath) || counts.Count == 0) return counts;
        var document = XDocument.Parse(DecodeXml(File.ReadAllBytes(outputPath)), LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root == null) return counts;
        var ns = root.Name.Namespace;
        var text = string.Join(" ", root.DescendantsAndSelf()
            .Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>())
            .Select(node => node.Value));
        foreach (System.Text.RegularExpressions.Match match in WordTokens.Matches(text))
        {
            var key = NormalizeWordLetters(match.Value);
            foreach (var word in counts.Keys.ToArray())
                if (NormalizeWordLetters(word).Equals(key, StringComparison.OrdinalIgnoreCase)) counts[word]++;
        }
        return counts;
    }

    internal static int ApplyReviewPatternCorrectionsToFb2(string outputPath, IEnumerable<WordReviewPatternCorrection> corrections) =>
        ApplyReviewPatternCorrectionsToFb2(outputPath, outputPath, corrections);

    internal static int ApplyReviewPatternCorrectionsToFb2(string inputPath, string outputPath, IEnumerable<WordReviewPatternCorrection> corrections)
    {
        var entries = corrections.ToArray();
        if (entries.Length == 0) return 0;
        var map = entries.GroupBy(item => NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var excluded = entries.Where(item => item.ExcludedContext != null)
            .Select(item => (Key: NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), Context: item.ExcludedContext!, item.ExcludedOccurrence))
            .ToHashSet();
        return ApplyPatternReplacementMapToFb2(inputPath, outputPath, map, excluded);
    }

    private static int ApplyPatternReplacementMapToFb2(string inputPath, string outputPath,
        Dictionary<string, WordReviewPatternCorrection> correctionMap,
        HashSet<(string Key, string Context, int ExcludedOccurrence)> excluded)
    {
        if (correctionMap.Count == 0) return 0;
        LogReview($"Применение правок: {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)}; групп слов: {correctionMap.Count}; исключённых контекстов: {excluded.Count}");
        LogReview("Правки по группам: " + string.Join("; ", correctionMap.Values.Select(item => $"{item.SourceWord} -> {item.Corrected}")));
        var bytes = File.ReadAllBytes(inputPath);
        _ = new Fb2Parser().Parse(bytes);
        var document = XDocument.Parse(DecodeXml(bytes), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В исходном FB2 отсутствует корневой элемент.");
        var ns = root.Name.Namespace;
        var originalTextNodes = root.DescendantNodes().OfType<XText>().ToArray();
        var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#')).Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var binaryNodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
        var replacements = 0;
        var excludedContexts = excluded.Where(item => item.Key.Length > 0).ToArray();
        foreach (var node in root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).Where(node => !binaryNodes.Contains(node)).Distinct())
        {
            var updated = WordTokens.Replace(node.Value, match =>
            {
                var normalized = NormalizeWordLetters(match.Value);
                var key = normalized.ToLowerInvariant();
                if (!correctionMap.TryGetValue(key, out var correction)) return match.Value;
                if (excludedContexts.Any(item => item.Key == key && item.ExcludedOccurrence >= 0 &&
                    string.Equals(item.Context, node.Value, StringComparison.Ordinal) && CountContextOccurrence(node.Value, match.Index, match.Value) == item.ExcludedOccurrence)) return match.Value;
                replacements++;
                return correction.Corrected;
            });
            if (!string.Equals(updated, node.Value, StringComparison.Ordinal)) node.Value = updated;
        }
        LogReview($"Заменено вхождений в XML: {replacements}");
        if (replacements == 0) return 0;
        if (!originalTextNodes.SequenceEqual(root.DescendantNodes().OfType<XText>()))
            throw new InvalidDataException("Изменился состав XML-текстовых узлов; безопасно сохранить исходную структуру FB2 невозможно.");
        SaveFb2PreservingSourceStructure(inputPath, outputPath, document);
        _ = new Fb2Parser().Parse(File.ReadAllBytes(outputPath));
        return replacements;
    }

    private static void SaveFb2PreservingSourceStructure(string inputPath, string outputPath, XDocument editedDocument)
    {
        var sourceBytes = File.ReadAllBytes(inputPath);
        var originalXml = DecodeXml(sourceBytes);
        var editedXml = editedDocument.ToString(SaveOptions.DisableFormatting);
        var editedDocumentForDiff = XDocument.Parse(editedXml, LoadOptions.PreserveWhitespace);
        var originalDocument = XDocument.Parse(originalXml, LoadOptions.PreserveWhitespace);
        var originalNodes = originalDocument.DescendantNodes().OfType<XText>().Where(node => node.Value.Length > 0).ToArray();
        var updatedNodes = editedDocumentForDiff.DescendantNodes().OfType<XText>().Where(node => node.Value.Length > 0).ToArray();
        if (updatedNodes.Length != originalNodes.Length)
            throw new InvalidDataException("Невозможно сохранить исправления без изменения структуры FB2: изменилось количество текстовых узлов.");

        // Не используем самописные индексы XML-текстовых узлов: XDocument может разделять
        // текст иначе (CDATA, комментарии, сущности). Вместо этого ищем уникальные исходные
        // фрагменты справа налево и применяем только буквальную замену внутри найденного текста.
        var resultXml = originalXml;
        var changed = new List<(string OldValue, string NewValue, bool IsCData)>();
        for (var i = 0; i < originalNodes.Length; i++)
            if (!string.Equals(originalNodes[i].Value, updatedNodes[i].Value, StringComparison.Ordinal))
                changed.Add((originalNodes[i].Value, updatedNodes[i].Value, originalNodes[i] is XCData));

        foreach (var change in changed.OrderByDescending(item => item.OldValue.Length))
        {
            var oldText = change.OldValue;
            var newText = change.NewValue;
            var encodedOld = EscapeXmlTextForLexicalMatch(oldText);
            var encodedNew = change.IsCData
                ? newText.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal)
                : EscapeXmlTextForLexicalMatch(newText);
            var searchFrom = resultXml.Length;
            var found = false;
            while (searchFrom >= 0)
            {
                var candidate = resultXml.LastIndexOf(encodedOld, searchFrom - 1, StringComparison.Ordinal);
                if (candidate < 0) break;
                // Ensure candidate lies in text, not in an element attribute/tag.
                if (IsXmlTextPosition(resultXml, candidate, out var cdata))
                {
                    var absoluteEnd = candidate + encodedOld.Length;
                    if (IsXmlTextPosition(resultXml, absoluteEnd - 1, out var endCData) && cdata == endCData)
                    {
                        resultXml = resultXml[..candidate] + encodedNew + resultXml[absoluteEnd..];
                        found = true;
                        break;
                    }
                }
                searchFrom = candidate;
            }
            if (!found)
            {
                var sample = oldText.Length > 120 ? oldText[..120] + "…" : oldText;
                LogReview($"Не удалось найти исходный текстовый фрагмент «{sample}» при сохранении XML; CDATA={change.IsCData}");
                throw new InvalidDataException("Не удалось сопоставить изменённый текст с исходным XML FB2; файл не был перезаписан.");
            }
        }
        LogReview($"Сопоставление завершено: изменённых текстовых фрагментов {changed.Count}");
        var resultBytes = EncodeXmlLikeSource(resultXml, sourceBytes);
        var tempPath = outputPath + ".review.tmp";
        var backupPath = outputPath + ".review.bak";
        try
        {
            File.WriteAllBytes(tempPath, resultBytes);
            _ = new Fb2Parser().Parse(File.ReadAllBytes(tempPath));
            if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(inputPath, backupPath, overwrite: true);
                try { File.Move(tempPath, outputPath, overwrite: true); }
                catch { File.Copy(backupPath, outputPath, overwrite: true); throw; }
            }
            else
            {
                File.Move(tempPath, outputPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    // True, если позиция принадлежит текстовому содержимому (а не тегу/атрибуту).
    // Отслеживает кавычки в атрибутах, комментарии, декларации и CDATA.
    private static bool IsXmlTextPosition(string xml, int position, out bool isCData)
    {
        isCData = false;
        if (position < 0 || position >= xml.Length) return false;
        var inTag = false;
        var quote = '\0';
        var inComment = false;
        var inCData = false;
        for (var i = 0; i <= position; i++)
        {
            if (inComment)
            {
                if (i + 2 < xml.Length && xml.AsSpan(i, 3).SequenceEqual("-->")) { i += 2; inComment = false; }
                continue;
            }
            if (inCData)
            {
                if (i + 2 < xml.Length && xml.AsSpan(i, 3).SequenceEqual("]]>") ) { i += 2; inCData = false; continue; }
                if (i == position) { isCData = true; return true; }
                continue;
            }
            if (!inTag && i + 3 < xml.Length && xml.AsSpan(i, 4).SequenceEqual("<!--")) { inComment = true; i += 3; continue; }
            if (!inTag && i + 8 < xml.Length && xml.AsSpan(i, 9).SequenceEqual("<![CDATA[")) { inCData = true; i += 8; continue; }
            var ch = xml[i];
            if (inTag)
            {
                if (quote != '\0') { if (ch == quote) quote = '\0'; }
                else if (ch is '\'' or '\"') quote = ch;
                else if (ch == '>') inTag = false;
            }
            else if (ch == '<') inTag = true;
            else if (i == position) return true;
        }
        return false;
    }

    private sealed class LexicalXmlScanner
    {
        private readonly string _xml;
        private readonly List<(int Start, int Length, bool IsCData)> _textNodes = new();
        private int _depth;
        private bool _inTag;
        private bool _inComment;
        private bool _inCData;
        private int _tagStart;
        private bool _tagIsSpecial;
        private bool _tagIsClosing;
        private int _textStart = -1;

        internal LexicalXmlScanner(string xml)
        {
            _xml = xml;
            Scan();
        }

        internal List<(int Start, int Length, bool IsCData)> TextNodes() => _textNodes;

        private void Scan()
        {
            for (var i = 0; i < _xml.Length; i++)
            {
                // Переходы вперёд оставляют i на последнем символе конструкции, а обычный
                // continue прибавляет ещё один, поэтому смещения здесь всегда на единицу меньше.
                if (_inComment)
                {
                    if (_xml.AsSpan(i, 2).SequenceEqual("--"))
                    {
                        i += 2;
                        _inComment = false;
                        _textStart = _depth > 0 ? i + 1 : -1;
                    }
                    continue;
                }
                if (_inCData)
                {
                    if (_xml.AsSpan(i, 2).SequenceEqual("]]"))
                    {
                        // Пустая секция CDATA тоже занимает место в дереве, поэтому
                        // диапазон добавляется всегда — иначе индексы разъедутся.
                        _textNodes.Add((_textStart, i - _textStart, true));
                        i += 2;
                        _inCData = false;
                        _textStart = _depth > 0 ? i + 1 : -1;
                    }
                    continue;
                }
                if (!_inTag && _xml.AsSpan(i).StartsWith("<!--"))
                {
                    FlushText(i);
                    _inComment = true;
                    i += 3;
                    continue;
                }
                if (!_inTag && _xml.AsSpan(i).StartsWith("<![CDATA["))
                {
                    FlushText(i);
                    _textStart = i + 9;
                    _inCData = true;
                    i += 8;
                    continue;
                }
                var ch = _xml[i];
                if (ch == '<' && !_inTag)
                {
                    // Текст перед закрывающим тегом корня дерево отбрасывает,
                    // поэтому хвостовой прогон в конце файла узлом не становится.
                    var closingRoot = _depth == 1 && i + 1 < _xml.Length && _xml[i + 1] == '/';
                    if (closingRoot) _textStart = -1;
                    else FlushText(i);
                    _tagStart = i;
                    _tagIsClosing = i + 1 < _xml.Length && _xml[i + 1] == '/';
                    _tagIsSpecial = i + 1 < _xml.Length && _xml[i + 1] is '!' or '?';
                    _inTag = true;
                }
                else if (ch == '>' && _inTag)
                {
                    if (!_tagIsSpecial)
                    {
                        if (_tagIsClosing) _depth = Math.Max(0, _depth - 1);
                        else
                        {
                            var beforeClose = i - 1;
                            while (beforeClose > _tagStart && char.IsWhiteSpace(_xml[beforeClose])) beforeClose--;
                            var isSelfClosing = _xml[beforeClose] == '/';
                            // Открытие корня поднимает глубину до 1; вложенные элементы — на 1.
                            if (_depth == 0) _depth = isSelfClosing ? 0 : 1;
                            else if (!isSelfClosing && _xml[_tagStart + 1] != '/') _depth++;
                        }
                    }
                    _inTag = false;
                    // Текст вне корневого элемента в дерево не входит (текст между
                    // объявлением <?xml?> и <FictionBook> узлом не является).
                    _textStart = _depth > 0 ? i + 1 : -1;
                }
            }
            // Хвостовой текст вне корневого элемента в дерево не попадает
            // (последний \r\n после </FictionBook> узлом не становится).
            if (_depth > 0 && _textStart >= 0 && _textStart < _xml.Length)
                _textNodes.Add((_textStart, _xml.Length - _textStart, false));
        }

        // Добавляет текстовый узел только если в нём есть содержимое: пустой XText
        // между элементами не создаётся, и сдвигать индекс нельзя.
        private void FlushText(int end)
        {
            if (_depth > 0 && _textStart >= 0 && end > _textStart) _textNodes.Add((_textStart, end - _textStart, false));
            _textStart = -1;
        }
    }

    private static string DecodeXmlTextFragment(string fragment)
    {
        try
        {
            var wrapped = XDocument.Parse("<r>" + fragment + "</r>", LoadOptions.PreserveWhitespace);
            return string.Concat(wrapped.Root!.Nodes().Select(node => node is XText text ? text.Value : node.ToString(SaveOptions.DisableFormatting)));
        }
        catch (System.Xml.XmlException)
        {
            // Серверы и словари иногда отдают FB2 с экранированными XML-сущностями внутри
            // текста (например, &amp;quot;). В этом случае фрагмент можно сопоставить только буквально.
            return null!;
        }
    }

    private static string EscapeXmlTextForLexicalMatch(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static byte[] EncodeXmlLikeSource(string xml, byte[] originalBytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        if (originalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) encoding = new UTF8Encoding(true);
        else if (originalBytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) encoding = Encoding.Unicode;
        else if (originalBytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) encoding = Encoding.BigEndianUnicode;
        else
        {
            var prefix = Encoding.ASCII.GetString(originalBytes, 0, Math.Min(originalBytes.Length, 512));
            var declaration = System.Text.RegularExpressions.Regex.Match(prefix, "<\\?xml[^>]*encoding\\s*=\\s*[\\\"'](?<enc>[^\\\"']+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            encoding = declaration.Success ? Encoding.GetEncoding(declaration.Groups["enc"].Value) : new UTF8Encoding(false);
        }
        return encoding.GetBytes(xml);
    }

    private static int CountContextOccurrence(string text, int characterIndex, string sourceWord)
    {
        var occurrence = 0;
        foreach (System.Text.RegularExpressions.Match match in WordTokens.Matches(text))
        {
            if (match.Index >= characterIndex) break;
            if (NormalizeWordLetters(match.Value).Equals(NormalizeWordLetters(sourceWord), StringComparison.OrdinalIgnoreCase)) occurrence++;
        }
        return occurrence;
    }

    internal static int ApplyReviewCaseCorrectionsToFb2(string outputPath, IEnumerable<WordReviewCaseCorrection> corrections)
    {
        var entries = corrections.ToArray();
        if (entries.Length == 0) return 0;
        var bytes = File.ReadAllBytes(outputPath);
        _ = new Fb2Parser().Parse(bytes);
        var document = XDocument.Parse(DecodeXml(bytes), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В итоговом FB2 отсутствует корневой элемент.");
        var ns = root.Name.Namespace;
        var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#')).Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var binaryNodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
        var originalTextNodes = root.DescendantNodes().OfType<XText>().ToArray();
        var nodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).Where(node => !binaryNodes.Contains(node)).Distinct().ToArray();
        var replacements = 0;
        foreach (var correction in entries)
        {
            var context = SplitReviewContext(correction.Context).Context;
            foreach (var node in nodes)
            {
                var contextStart = node.Value.IndexOf(context, StringComparison.Ordinal);
                if (contextStart < 0) continue;
                var span = node.Value.Substring(contextStart, context.Length);
                var updated = WordTokens.Replace(span, match =>
                    string.Equals(NormalizeWordLetters(match.Value), NormalizeWordLetters(correction.SourceWord), StringComparison.OrdinalIgnoreCase)
                        ? correction.Corrected : match.Value);
                if (updated == span) continue;
                node.Value = node.Value[..contextStart] + updated + node.Value[(contextStart + context.Length)..];
                replacements++;
                break;
            }
        }
        if (replacements > 0)
        {
            if (!originalTextNodes.SequenceEqual(root.DescendantNodes().OfType<XText>()))
                throw new InvalidDataException("Изменился состав XML-текстовых узлов; безопасно сохранить исходную структуру FB2 невозможно.");
            SaveFb2PreservingSourceStructure(outputPath, outputPath, document);
            _ = new Fb2Parser().Parse(File.ReadAllBytes(outputPath));
        }
        return replacements;
    }

    private static int ApplyWordReplacementMapToFb2(string inputPath, string outputPath, Dictionary<string, string> correctionMap)
    {
        if (correctionMap.Count == 0) return 0;
        var bytes = File.ReadAllBytes(inputPath);
        _ = new Fb2Parser().Parse(bytes);
        var document = XDocument.Parse(DecodeXml(bytes), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("В итоговом FB2 отсутствует корневой элемент.");
        var ns = root.Name.Namespace;
        var imageIds = root.Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "image")
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#')).Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var binaryNodes = root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && element.Name.LocalName == "binary" && imageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).ToHashSet();
        var replacements = 0;
        foreach (var node in root.DescendantsAndSelf().Where(element => element.Name.Namespace == ns && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>()).Where(node => !binaryNodes.Contains(node)).Distinct())
        {
            var updated = WordTokens.Replace(node.Value, match =>
            {
                var key = NormalizeWordLetters(match.Value).ToLowerInvariant();
                if (!correctionMap.TryGetValue(key, out var corrected)) return match.Value;
                replacements++;
                return corrected;
            });
            if (!string.Equals(updated, node.Value, StringComparison.Ordinal)) node.Value = updated;
        }
        if (replacements == 0) return 0;
        var temporary = outputPath + ".review.tmp";
        try
        {
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = document.Declaration == null, NewLineHandling = NewLineHandling.None }))
                document.Save(writer);
            _ = new Fb2Parser().Parse(File.ReadAllBytes(temporary));
            File.Move(temporary, outputPath, overwrite: true);
            return replacements;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<string, string> LoadUserDictionary()
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(UserDictionaryPath)) return replacements;
        foreach (var line in File.ReadAllLines(UserDictionaryPath, Encoding.UTF8))
        {
            var columns = line.Split('\t', 2);
            if (columns.Length == 2 && columns[0].Length > 0) replacements[columns[0]] = columns[1];
        }
        return replacements;
    }

    private static string ApplyUserDictionary(string text)
    {
        var replacements = UserDictionary.Value;
        if (replacements.Count == 0) return text;
        return WordTokens.Replace(text, match =>
        {
            var key = NormalizeWordLetters(match.Value).ToLowerInvariant();
            return replacements.TryGetValue(key, out var replacement) ? replacement : match.Value;
        });
    }

    private static int CountReviewWords(IEnumerable<string> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            var marker = entry.IndexOf("Не распознаны:", StringComparison.Ordinal);
            if (marker < 0) continue;
            count += entry[(marker + "Не распознаны:".Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }
        return count;
    }

    private static void WriteWordReview(string inputPath, List<string> review)
    {
        var reportPath = ReviewPathFor(inputPath);
        var entries = new List<WordReviewEntry>();
        foreach (var block in review)
        {
            var contextPrefix = "Фрагмент: ";
            var unknownPrefix = "Не распознаны: ";
            var contextStart = block.IndexOf(contextPrefix, StringComparison.Ordinal);
            var unknownStart = block.IndexOf(unknownPrefix, StringComparison.Ordinal);
            if (contextStart < 0 || unknownStart < 0) continue;
            var contextStartIndex = contextStart + contextPrefix.Length;
            var context = block[contextStartIndex..unknownStart].TrimEnd();
            var words = block[(unknownStart + unknownPrefix.Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            entries.AddRange(words.Select(word => new WordReviewEntry(word, context, word)));
        }
        File.WriteAllText(reportPath, FormatWordReview(entries), new UTF8Encoding(true));
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

    internal static string NormalizeWordLettersForReview(string word) => NormalizeWordLetters(word);

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
        bool includeIllustrations = true, Action<int, int>? reportProgress = null,
        ConversionControl? control = null, ConversionDiagnostics? diagnostics = null)
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
        var imageElements = root.Descendants()
            .Where(element => element.Name.Namespace == fictionBookNs && element.Name.LocalName == "image").ToArray();
        var embeddedImageIds = imageElements
            .Select(element => (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))
            .Where(href => !string.IsNullOrWhiteSpace(href) && href!.StartsWith('#'))
            .Select(href => href![1..]).ToHashSet(StringComparer.Ordinal);
        var embeddedText = root.DescendantsAndSelf()
            .Where(element => element.Name.Namespace == fictionBookNs && element.Name.LocalName == "binary"
                && embeddedImageIds.Contains((string?)element.Attribute("id") ?? ""))
            .SelectMany(element => element.DescendantNodes().OfType<XText>())
            .Where(node => !string.IsNullOrWhiteSpace(node.Value)).ToArray();
        var embeddedTextSet = embeddedText.ToHashSet();
        var eligibleText = root.DescendantsAndSelf()
            .Where(element => element.Name.Namespace == fictionBookNs && TextElements.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantNodes().OfType<XText>())
            .Where(node => !embeddedTextSet.Contains(node))
            .Distinct()
            .ToArray();
        var processableText = eligibleText.Where(textNode => !string.IsNullOrWhiteSpace(textNode.Value)).ToArray();
        var completedText = 0;
        var conversionTimer = Stopwatch.StartNew();
        var byteCount = input.LongLength;
        var characterCount = processableText.Sum(node => (long)node.Value.Length);
        var wordReview = new List<string>();
        if (diagnostics != null)
        {
            diagnostics.TotalUnits = processableText.Length;
            diagnostics.CharacterCount = characterCount;
            diagnostics.InputBytes = byteCount;
        }
        reportProgress?.Invoke(0, processableText.Length);

        // Binary image payloads are excluded from inference regardless of whether
        // they are preserved in the output. Keep their original XML nodes untouched.

        foreach (var textNode in processableText)
        {
            control?.Checkpoint();
            if (string.IsNullOrWhiteSpace(textNode.Value)) continue;
            var sourceText = textNode.Value;
            // Модель загружается только при первом реальном запросе — тогда результат
            // заведомо появится, а первая загрузка не завершит процесс раньше времени.
            accentizer.EnsureStarted();
            try
            {
                var modelOutput = RuAccentMarker.Replace(accentizer.Process(sourceText), "\u0301");
                var accented = EnsureOriginalPunctuation(sourceText, EnsureDialogueInitialWordAccent(sourceText, modelOutput, accentizer));
                var reviewCountBefore = wordReview.Count;
                CollectUnrecognizedWords(sourceText, accented, wordReview);
                if (diagnostics != null) diagnostics.UnrecognizedWordCount += CountReviewWords(wordReview.Skip(reviewCountBefore));
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
                if (diagnostics != null)
                {
                    diagnostics.FailedUnits++;
                    diagnostics.Events.Add($"Фрагмент {completedText + 1}: {ex}");
                }
                // Keep the original fragment untouched so a single problem fragment
                // never aborts the whole conversion.
                LogError($"Фрагмент оставлен без изменений: {TruncateForLog(sourceText)}", ex);
            }
            completedText++;
            if (diagnostics != null) diagnostics.CompletedUnits = completedText;
            if (completedText == processableText.Length || completedText % 10 == 0)
            {
                var remaining = completedText > 0
                    ? TimeSpan.FromSeconds(conversionTimer.Elapsed.TotalSeconds * (processableText.Length - completedText) / completedText)
                    : (TimeSpan?)null;
                LogConversion(inputPath, byteCount, characterCount, conversionTimer.Elapsed, remaining, $"Обработка {completedText}/{processableText.Length}");
                reportProgress?.Invoke(completedText, processableText.Length);
            }
        }

        if (document.Declaration != null)
            document.Declaration.Encoding = "utf-8";

        control?.Checkpoint();
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
            control?.Checkpoint();
            File.Move(temporary, outputPath, overwrite: true);
            if (diagnostics != null)
            {
                diagnostics.OutputBytes = new FileInfo(outputPath).Length;
                diagnostics.ImagePayloadCharacterCount = embeddedText.Sum(node => (long)node.Value.Length);
                conversionTimer.Stop();
                diagnostics.Elapsed = conversionTimer.Elapsed;
            }
            WriteWordReview(inputPath, wordReview);
            conversionTimer.Stop();
            LogConversion(inputPath, byteCount, characterCount, conversionTimer.Elapsed, TimeSpan.Zero, "Завершено");
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
            if (outputSpecified && (output.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase) || output.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                if (Directory.Exists(input)) throw new ArgumentException("Для папки -o должен задавать папку, не имя файла.");
                var sourceExtension = Path.GetExtension(input);
                if (!sourceExtension.Equals(Path.GetExtension(output), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Расширение выходного файла должно совпадать с форматом исходного файла.");
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
        private readonly ConversionDiagnostics? _diagnostics;
        private TimeSpan _workerInferenceTime;
        private long _workerCallCount;

        public RuAccentWorker(string model, bool dictionary, string device, Action<string>? report = null, ConversionDiagnostics? diagnostics = null)
        {
            _diagnostics = diagnostics;
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
            var cpuThreads = Math.Max(1, Environment.ProcessorCount);
            start.Environment["DSR_CPU_THREADS"] = cpuThreads.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                else if (e.Data.StartsWith("DSR_PERF:", StringComparison.Ordinal))
                {
                    _diagnostics?.PerformanceEvents.Add(e.Data["DSR_PERF:".Length..]);
                    _report?.Invoke(e.Data);
                }
                else if (e.Data.StartsWith("DSR_INFERENCE:", StringComparison.Ordinal))
                {
                    if (double.TryParse(e.Data.AsSpan("DSR_INFERENCE:".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        _workerInferenceTime += TimeSpan.FromSeconds(seconds);
                        _workerCallCount++;
                        if (_diagnostics != null)
                        {
                            _diagnostics.InferenceElapsed = _workerInferenceTime;
                            _diagnostics.InferenceCalls = _workerCallCount;
                        }
                    }
                    _report?.Invoke(e.Data);
                }
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
