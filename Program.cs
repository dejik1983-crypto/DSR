using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FB2AccentConverter.Parser;

namespace FB2AccentConverter;

internal static class Program
{
    // Windows-1251 нужна для чтения и записи TXT без BOM, но в .NET она доступна
    // только через провайдер кодовых страниц. Без регистрации Encoding.GetEncoding(1251)
    // бросает «No data is available for encoding 1251». Статический конструктор
    // выполняется раньше Main, поэтому провайдер доступен и в CLI, и в окне.
    static Program() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly System.Text.RegularExpressions.Regex RuAccentMarker = new(
        "\\+(?=[а-яёА-ЯЁ])", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly object LogLock = new();
    private static readonly System.Text.RegularExpressions.Regex PunctuationTokens = new(
        @"[\p{L}\p{M}\p{N}_]+|[^\p{L}\p{M}\p{N}_]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Safety net (C# side of the ruaccent worker protection). Even if the Python
    // worker leaves a stray combining acute mark, these rules guarantee that:
    //  • a combining accent never stays adjacent to the letter ё (ё is always stressed);
    //  • a short function word (preposition/conjunction/particle) never carries stress.
    // Keep this list identical to _UNSTRESSED_WORDS in ruaccent_worker.py: the two
    // layers must agree, otherwise one of them silently stops protecting a word.
    private static readonly HashSet<string> UnstressedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Prepositions
        "о", "об", "обо", "в", "во", "с", "со", "к", "ко", "у", "от", "до", "из",
        "без", "для", "про", "над", "под", "при", "на", "за",
        // Conjunctions and particles
        "и", "а", "но", "да", "ли", "бы", "же", "ль", "то",
    };
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
    internal static string YoDictionaryPath => Path.Combine(ApplicationDirectory, "dictionary", "ё.txt");
    internal static string TrainingLogPath => Path.Combine(DataDirectory, "накопленные-примеры.jsonl");
    internal static string CorrectionsFor(string reviewPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reviewPath))!, Path.GetFileNameWithoutExtension(reviewPath).Replace("_проверка-слов", "_исправления") + ".txt");
    internal static string ReviewPathFor(string inputPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath))!, Path.GetFileNameWithoutExtension(inputPath) + "_проверка-слов.txt");
    // ConcurrentDictionary: the user dictionary is read on the background conversion
    // thread and written from the UI thread when corrections are saved, so a plain
    // Dictionary could be corrupted by a concurrent read and write.
    private static readonly Lazy<ConcurrentDictionary<string, string>> UserDictionary = new(LoadUserDictionary);
    private static readonly Lazy<Dictionary<string, string>> YoDictionary = new(LoadYoDictionary);
    internal static string FormCorrectionsPathFor(string reviewPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reviewPath))!, Path.GetFileNameWithoutExtension(reviewPath).Replace("_проверка-слов", "_формы") + ".json");

    /// <summary>Отчёт со статистикой конвертации, который появляется рядом с результатом.</summary>
    internal static string StatisticsPathFor(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, Path.GetFileNameWithoutExtension(outputPath) + "_статистика.txt");

    // The actual accenting runs in the child python.exe worker; the window process
    // itself mostly waits on it and uses almost no CPU. The load indicators therefore
    // sum the CPU and memory of the whole process tree (the window plus every child,
    // including the Python worker), otherwise they would show the near-idle window as 0%.
    private const uint Th32CsSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // CPU времени процесса накапливается как сумма приростов по каждому PID, а не как
    // абсолютная сумма дерева: при снимке дерева короткоживущий дочерний процесс
    // (python-воркер) мог завершиться между двумя замерами, и тогда его вклад
    // полностью пропадал из показаний индикатора.
    private static readonly Dictionary<int, double> ProcessCpuBaseline = new();
    private static readonly object ProcessCpuLock = new();
    private static double ProcessCpuAccumulatedMilliseconds;

    internal static (double CpuMilliseconds, long WorkingSetBytes) CurrentProcessTreeUsage()
    {
        var samples = new List<(int Id, double CpuMilliseconds, long WorkingSetBytes)>();
        using (var self = Process.GetCurrentProcess())
        {
            AddProcessUsage(self, samples);
            var children = ReadChildProcessIds();
            var rootId = self.Id;
            var pending = new Queue<int>();
            var visited = new HashSet<int> { rootId };
            pending.Enqueue(rootId);
            while (pending.Count > 0)
            {
                if (!children.TryGetValue(pending.Dequeue(), out var childIds)) continue;
                foreach (var childId in childIds)
                {
                    if (!visited.Add(childId)) continue;
                    pending.Enqueue(childId);
                    try
                    {
                        using var child = Process.GetProcessById(childId);
                        AddProcessUsage(child, samples);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
        long workingSetBytes = 0;
        lock (ProcessCpuLock)
        {
            var seen = new HashSet<int>();
            foreach (var sample in samples)
            {
                seen.Add(sample.Id);
                var delta = ProcessCpuBaseline.TryGetValue(sample.Id, out var last)
                    ? sample.CpuMilliseconds - last
                    : sample.CpuMilliseconds;
                if (delta > 0) ProcessCpuAccumulatedMilliseconds += delta;
                ProcessCpuBaseline[sample.Id] = sample.CpuMilliseconds;
                workingSetBytes += sample.WorkingSetBytes;
            }
            foreach (var staleId in ProcessCpuBaseline.Keys.Where(id => !seen.Contains(id)).ToList())
                ProcessCpuBaseline.Remove(staleId);
        }
        return (ProcessCpuAccumulatedMilliseconds, workingSetBytes);
    }

    private static Dictionary<int, List<int>> ReadChildProcessIds()
    {
        var children = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return children;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return children;
            do
            {
                var parentId = (int)entry.th32ParentProcessID;
                if (!children.TryGetValue(parentId, out var list))
                    children[parentId] = list = new List<int>();
                list.Add((int)entry.th32ProcessID);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return children;
    }

    private static void AddProcessUsage(Process process, List<(int Id, double CpuMilliseconds, long WorkingSetBytes)> samples)
    {
        try
        {
            samples.Add((process.Id, process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64));
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (ArgumentException) { }
    }

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
        internal int DictionaryStressCount { get; set; }
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
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {status}: {input} | {bytes / 1024d / 1024d:0.00} МБ | {characters:N0} символов | обработка {elapsed:hh\\:mm\\:ss} | осталось {(estimatedRemaining.HasValue ? estimatedRemaining.Value.ToString("hh\\:mm\\:ss") : "н/д")}{Environment.NewLine}";
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

            if (args.Length >= 2 && args[0] == "--review")
            {
                // Прямой запуск доработки: удобно, когда книга уже сконвертирована.
                // Открывается вкладка ИИ-доработки с подставленными файлами.
                try
                {
                    var book = args.Length >= 3 ? args[2] : null;
                    var application = new System.Windows.Application();
                    application.Run(new MainWindow(args[1], book));
                    return 0;
                }
                catch (Exception ex)
                {
                    LogError("Ошибка запуска окна проверки", ex);
                    System.Windows.MessageBox.Show(ex.Message, "Ошибка запуска окна проверки",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return 1;
                }
            }

            if (args.Length == 1 && args[0] == "--probe-gpu")
            {
                var gpu = ProbeGpu();
                Console.WriteLine(gpu.Message);
                return gpu.Supported ? 0 : 1;
            }

            if (args.Length >= 2 && args[0] == "--llm-stress")
                return RunLlmStressCommand(args[1], ParseLlmEngineArgs(args, 2));

            if (args.Length >= 1 && args[0] == "--llm-benchmark")
                return RunLlmBenchmarkCommand(ParseLlmEngineArgs(args, 1));

            if (args.Length >= 1 && args[0] == "--llm-remote-models")
            {
                var engine = ParseLlmEngineArgs(args, 1);
                var remote = ListRemoteLlmModels(engine, out var remoteError);
                if (remoteError.Length > 0) Console.Error.WriteLine(remoteError);
                Console.WriteLine("Сервис: " + engine.Title);
                foreach (var name in remote) Console.WriteLine("  " + name);
                return remote.Count == 0 ? 1 : 0;
            }

            if (args.Length == 1 && args[0] == "--llm-models")
            {
                var available = DiscoverLlmModels();
                var engine = CurrentLlmEngine();
                Console.WriteLine("Движок сейчас: " + engine.Title + " (" + engine.Id + ")");
                Console.WriteLine("Модель по умолчанию: " + DefaultLlmModelFolder);
                Console.WriteLine("Выбрана сейчас: " + CurrentLlmModel());
                Console.WriteLine("Найдено локальных моделей: " + available.Count);
                foreach (var name in available) Console.WriteLine("  " + name + "  (" + LlmModelDirectory(name) + ")");
                Console.WriteLine("Папка моделей: " + LlmModelsDirectory);
                Console.WriteLine("Настройки: " + LlmSettingsPath);
                return 0;
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
    internal static Action<string>? SetupProgress { get; set; }
    private static void ReportSetupOutput(string line)
    {
        if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Collecting ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Installing collected packages", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Building wheel", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Preparing metadata", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Retrying", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Starting new HTTPS connection", StringComparison.OrdinalIgnoreCase))
            SetupProgress?.Invoke(line.Trim());
    }
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

    private static readonly object PythonEnvironmentLock = new();

    /// <summary>Версия сборки: после обновления DSR окружение готовится заново.</summary>
    internal static string AppVersion
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() ?? "0" : informational;
        }
    }

    /// <summary>Версия для показа пользователю: без служебного суффикса сборки.</summary>
    internal static string DisplayVersion
    {
        get
        {
            var version = AppVersion;
            var suffix = version.IndexOfAny(new[] { '+', '-' });
            return suffix > 0 ? version[..suffix] : version;
        }
    }

    /// <summary>Python из локального окружения рядом с приложением.</summary>
    internal static string EnvironmentPythonPath => Path.Combine(DataDirectory, "python", "Scripts", "python.exe");

    /// <summary>
    /// Быстрая проверка окружения при запуске: импортируются ли пакеты. Нужна, чтобы
    /// не пропустить перенос на другой компьютер, где окружение Python нерабочее.
    /// </summary>
    internal static bool VerifyEnvironmentQuick()
    {
        try
        {
            var python = EnvironmentPythonPath;
            if (!File.Exists(python)) return false;
            return RunCommand(python, "-c", "import ruaccent, onnxruntime; print(onnxruntime.__version__)").ExitCode == 0;
        }
        catch (Exception error)
        {
            LogError("Быстрая проверка окружения", error);
            return false;
        }
    }

    internal static string EnsurePythonRuntime()
    {
        // Подготовка, проверка GPU и установка CUDA пишут в одно локальное окружение Python.
        // Не даём им работать одновременно: параллельные pip-процессы блокируют файлы
        // друг друга, и один из них падает с WinError 32 («файл занят другим процессом»).
        lock (PythonEnvironmentLock)
            return EnsurePythonRuntimeCore();
    }

    private static string EnsurePythonRuntimeCore()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ModelCacheDirectory);
        SetupProgress?.Invoke("Проверяю локальное окружение Python…");
        var script = ExtractEmbeddedFile("DSR.ruaccent_worker.py", "ruaccent_worker.py");
        var requirements = ExtractEmbeddedFile("DSR.requirements.txt", "requirements.txt");
        var pythonEnvironment = Path.Combine(DataDirectory, "python");
        var python = Path.Combine(pythonEnvironment, "Scripts", "python.exe");
        var systemPython = Environment.GetEnvironmentVariable("PYTHON") ?? "python";

        if (!File.Exists(python))
        {
            SetupProgress?.Invoke("Создаю локальное окружение Python…");
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
            SetupProgress?.Invoke("Устанавливаю RUAccent и ONNX Runtime; идёт скачивание зависимостей…");
            // Repair only required inference packages, without optional torch/crfsuite.
            const string accentSpec = "ruaccent @ git+https://github.com/Den4ikAI/ruaccent.git@3ac0ad5f6508f6c1a4a604220042232c70a7baf9";
            var install = InstallPipPackages(python, pythonEnvironment, "Установка RuAccent", accentSpec, "onnxruntime");
            if (install.ExitCode != 0)
            {
                LogError("Не удалось установить RUAccent и ONNX Runtime", install.Output);
                throw new InvalidOperationException(IsTransientFileLock(install.Output)
                    ? "Не удалось установить RUAccent и ONNX Runtime: файлы окружения заняты другим процессом. Закройте другие окна DSR и повторите «Подготовка к работе». Журнал: " + LogFilePath
                    : "Не удалось установить RUAccent и ONNX Runtime. Проверьте интернет и место на диске. Подробности в журнале: " + LogFilePath);
            }
            check = RunCommand(python, "-c", "import ruaccent, onnxruntime; print('onnxruntime', onnxruntime.__version__)");
            if (check.ExitCode != 0)
                throw new InvalidOperationException("Проверка RUAccent и ONNX Runtime не пройдена. " + check.Output);
        }

        return python;
    }

    // Windows на короткое время может блокировать файл окружения (антивирус, индексатор
    // или уходящий процесс python). Такие ошибки pip обычно проходят при повторе.
    private static bool IsTransientFileLock(string output) =>
        output.Contains("WinError 32", StringComparison.OrdinalIgnoreCase)
        || output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
        || output.Contains("занят другим процессом", StringComparison.OrdinalIgnoreCase);

    // Останавливает только те процессы python, которые запущены из нашего локального
    // окружения (ресурсы\python), чтобы не трогать посторонние установки Python.
    private static void StopLocalEnvironmentPython(string pythonEnvironment)
    {
        string root;
        try { root = Path.GetFullPath(pythonEnvironment); }
        catch { return; }
        foreach (var process in Process.GetProcessesByName("python"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    process.Kill(entireProcessTree: true);
            }
            catch { /* процесс недоступен или уже завершился */ }
            finally { process.Dispose(); }
        }
    }

    // Установка пакетов pip с повтором: Windows иногда на короткое время блокирует файлы
    // окружения (антивирус, индексатор или уходящий процесс python), и первая попытка падает.
    private static (int ExitCode, string Output) InstallPipPackages(string python, string pythonEnvironment, string label, params string[] packages)
    {
        var install = (ExitCode: 1, Output: string.Empty);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Оставшийся от прерванной подготовки Python держит файлы в site-packages.
            // Останавливаем только процессы из нашего локального окружения.
            StopLocalEnvironmentPython(pythonEnvironment);
            var arguments = new List<string> { "-m", "pip", "install", "--upgrade" };
            arguments.AddRange(packages);
            install = RunCommand(python, arguments.ToArray());
            if (install.ExitCode == 0) break;
            if (attempt < 3 && IsTransientFileLock(install.Output))
            {
                SetupProgress?.Invoke($"{label}: файлы окружения заняты, повтор ({attempt + 1}/3)…");
                Thread.Sleep(TimeSpan.FromSeconds(3 * attempt));
                continue;
            }
            break;
        }
        return install;
    }

    // Локальная модель ИИ работает через onnxruntime-genai. Пакет тяжёлый и не входит в базовую
    // подготовку, поэтому ставим его при первом использовании локального движка подсказок.
    internal static string? EnsureLlmRuntimeOrError(string python)
    {
        lock (PythonEnvironmentLock)
        {
            var check = RunCommand(python, "-c", "import onnxruntime_genai; print('ok')");
            if (check.ExitCode == 0) return null;
            SetupProgress?.Invoke("Устанавливаю onnxruntime-genai для локальной модели ИИ…");
            var install = InstallPipPackages(python, Path.Combine(DataDirectory, "python"),
                "Установка onnxruntime-genai", "onnxruntime-genai");
            if (install.ExitCode != 0)
            {
                LogError("Не удалось установить onnxruntime-genai", install.Output);
                return "Не удалось установить пакет onnxruntime-genai для локальной модели ИИ. "
                    + (IsTransientFileLock(install.Output)
                        ? "Файлы окружения заняты другим процессом — закройте другие окна DSR и повторите."
                        : "Проверьте интернет и место на диске. Журнал: " + LogFilePath);
            }
            check = RunCommand(python, "-c", "import onnxruntime_genai; print('ok')");
            return check.ExitCode == 0 ? null : "Пакет onnxruntime-genai установлен, но не загружается. " + check.Output;
        }
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
        return RunCommand(start);
    }

    private static (int ExitCode, string Output) RunCommand(ProcessStartInfo start)
    {
        Directory.CreateDirectory(DataDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Не удалось запустить {start.FileName}.");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdout.AppendLine(e.Data);
            ReportSetupOutput(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stderr.AppendLine(e.Data);
            ReportSetupOutput(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return (process.ExitCode, (stdout.ToString() + Environment.NewLine + stderr.ToString()).Trim());
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
        report.AppendLine($"Нераспознанных слов в отчёте: {diagnostics.UnrecognizedWordCount:N0}");
        report.AppendLine($"Ударений подставлено из словаря RUAccent: {diagnostics.DictionaryStressCount:N0}");
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
        // Polysyllabic words the model left unmarked, de-duplicated by dictionary key.
        var residueWords = new HashSet<string>(StringComparer.Ordinal);
        using var accentizer = new RuAccentWorker(model, true, device, report);
        accentizer.Start();
        accentizer.EnsureStarted();
        reportProgress(0, lines.Length);
        var result = new StringBuilder(text.Length + 64);
        var completed = 0;
        TimeSpan? estimatedRemaining = null;
        var contents = new string[lines.Length];
        var endings = new string[lines.Length];
        var accentedLines = new string?[lines.Length];
        // Lines that were accented successfully; failed lines are left untouched and
        // must never receive a dictionary stress mark or reappear in the review.
        var reviewable = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ending = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            contents[i] = ending.Length == 0 ? line : line[..^1];
            endings[i] = ending;
        }
        // Batch independent lines so the worker can accent them concurrently
        // instead of one blocking request/response round-trip per line.
        const int inferenceBatchSize = 128;
        for (var batchStart = 0; batchStart < lines.Length; batchStart += inferenceBatchSize)
        {
            control.Checkpoint();
            var batchEnd = Math.Min(batchStart + inferenceBatchSize, lines.Length);
            var indices = new List<int>();
            var inputs = new List<string>();
            for (var i = batchStart; i < batchEnd; i++)
            {
                if (string.IsNullOrWhiteSpace(contents[i])) continue;
                indices.Add(i);
                inputs.Add(ApplyUserDictionary(contents[i]));
            }
            (string? Text, string? Error)[] outputs = inputs.Count > 0
                ? accentizer.ProcessBatch(inputs)
                : Array.Empty<(string?, string?)>();
            for (var k = 0; k < indices.Count; k++)
            {
                var i = indices[k];
                var content = contents[i];
                var inference = outputs[k];
                if (inference.Error != null)
                {
                    diagnostics.FailedUnits++;
                    diagnostics.Events.Add($"Строка {i + 1}: {inference.Error}");
                    LogError($"Строка оставлена без изменений: {TruncateForLog(content)}", new InvalidOperationException(inference.Error));
                    accentedLines[i] = content;
                    continue;
                }
                try
                {
                    var modelOutput = RuAccentMarker.Replace(inference.Text ?? content, "\u0301");
                    var modelProcessed = ProtectUnstressedWords(EnsureDialogueInitialWordAccent(content, modelOutput, accentizer));
                    var modelProcessedWithPunctuation = EnsureOriginalPunctuation(content, modelProcessed);
                    accentedLines[i] = ApplyYoDictionary(modelProcessedWithPunctuation);
                    var reviewCountBefore = wordReview.Count;
                    foreach (var word in CollectUnrecognizedWords(content, accentedLines[i]!, wordReview))
                        residueWords.Add(DictionaryKey(word));
                    diagnostics.UnrecognizedWordCount += CountReviewWords(wordReview.Skip(reviewCountBefore));
                    reviewable[i] = true;
                }
                catch (Exception ex)
                {
                    diagnostics.FailedUnits++;
                    diagnostics.Events.Add($"Строка {i + 1}: {ex}");
                    // Keep the original line untouched so a single problem line
                    // never aborts the whole conversion.
                    LogError($"Строка оставлена без изменений: {TruncateForLog(content)}", ex);
                    accentedLines[i] = content;
                }
            }
            completed = batchEnd;
            diagnostics.CompletedUnits = completed;
            estimatedRemaining = completed > 0
                ? TimeSpan.FromSeconds(conversionTimer.Elapsed.TotalSeconds * (lines.Length - completed) / completed)
                : null;
            report($"Прогресс {completed}/{lines.Length}; прошло {conversionTimer.Elapsed:hh\\:mm\\:ss}; осталось {(estimatedRemaining.HasValue ? estimatedRemaining.Value.ToString("hh\\:mm\\:ss") : "расчёт…")}");
            LogConversion(inputPath, bytesIn, text.Length, conversionTimer.Elapsed, estimatedRemaining, $"Обработка {completed}/{lines.Length}");
            reportProgress(completed, lines.Length);
        }
        // The neural model leaves some polysyllabic words unmarked (rare words,
        // compounds, names). RUAccent ships a much larger stress dictionary that is
        // skipped in tiny_mode to save RAM: resolve those words once for the whole
        // file, add the marks it knows, then rebuild the review so only genuinely
        // unknown words are left for the manual pass.
        var dictionaryStress = accentizer.ResolveWords(residueWords.ToArray());
        if (dictionaryStress.Count > 0)
        {
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    accentedLines[i] = ApplyDictionaryStress(accentedLines[i]!, dictionaryStress);
            wordReview.Clear();
            for (var i = 0; i < accentedLines.Length; i++)
                if (reviewable[i] && accentedLines[i] != null)
                    CollectUnrecognizedWords(contents[i], accentedLines[i]!, wordReview);
            diagnostics.DictionaryStressCount = dictionaryStress.Count;
            report($"Из словаря RUAccent подставлено ударений: {dictionaryStress.Count}.");
        }
        for (var i = 0; i < lines.Length; i++)
        {
            result.Append(accentedLines[i] ?? contents[i]);
            result.Append(endings[i]);
            if (i + 1 < lines.Length) result.Append('\n');
        }
        control.Checkpoint();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, EncodeText(result.ToString(), sourceBytes));
        diagnostics.OutputBytes = new FileInfo(outputPath).Length;
        diagnostics.Elapsed = conversionTimer.Elapsed;
        diagnostics.UnrecognizedWordCount = CountReviewWords(wordReview);
        WriteWordReview(outputPath, wordReview);
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
    internal sealed record WordReviewPatternCorrection(string SourceWord, string Corrected, string? ExcludedContext, int ExcludedOccurrence);

    private static List<string> CollectUnrecognizedWords(string source, string converted, List<string> report)
    {
        var sourceMatches = WordTokens.Matches(source).Cast<System.Text.RegularExpressions.Match>().ToArray();
        var outputMatches = WordTokens.Matches(converted).Cast<System.Text.RegularExpressions.Match>().ToArray();
        if (sourceMatches.Length != outputMatches.Length) return new List<string>();

        var suspiciousWords = new List<string>();
        for (var i = 0; i < sourceMatches.Length; i++)
        {
            var original = sourceMatches[i].Value;
            var normalized = NormalizeWordLetters(original);
            if (NotCyrillicWord.IsMatch(original) || normalized.Length < 2) continue;
            // RUAccent never marks stress inside a monosyllabic word: its position is
            // unambiguous, so the absence of a mark there is not a defect. Only words
            // with two or more vowels can genuinely be missing a required stress mark.
            if (RussianVowel.Matches(normalized).Count < 2) continue;
            // Short prepositions/conjunctions/particles are deliberately left unstressed
            // by ProtectUnstressedWords, so they must not be reported either.
            if (UnstressedWords.Contains(normalized)) continue;
            var convertedWord = outputMatches[i].Value;
            // Only a combining acute explicitly emitted by the model counts as
            // stress; ë is self-stressed in Russian and excludes the word from review.
            var hasExplicitStress = convertedWord.Contains('\u0301');
            var hasYo = convertedWord.Contains('ё', StringComparison.OrdinalIgnoreCase);
            if (!hasExplicitStress && !hasYo)
                suspiciousWords.Add(original);
        }

        if (suspiciousWords.Count > 0)
            report.Add($"Фрагмент: {source}{Environment.NewLine}Не распознаны: {string.Join(", ", suspiciousWords.Distinct(StringComparer.OrdinalIgnoreCase))}{Environment.NewLine}");
        return suspiciousWords;
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
                else if (line.StartsWith("Не распознаны: ", StringComparison.Ordinal) && currentContext != null)
                {
                    var contextText = currentContext;
                    foreach (var word in line[14..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        structured.Add(new WordReviewEntry(word, contextText, word));
                }
                else if (line.StartsWith("Правка [", StringComparison.Ordinal) && currentContext != null)
                {
                    var close = line.IndexOf("]: ", StringComparison.Ordinal);
                    if (close > 8) structured.Add(new WordReviewEntry(line[8..close], currentContext, line[(close + 3)..]));
                }
            }
            // Both the "Не распознаны" and the "Правка [...]" lines describe the same
            // word in the same fragment, so keep a single entry per (word, fragment):
            // prefer the "Правка" one, which may carry an already saved correction.
            return AddContextOccurrences(structured
                .GroupBy(entry => NormalizeWordLetters(entry.Word).ToLowerInvariant() + "\u0001" + entry.Context, StringComparer.Ordinal)
                .Select(group => group.FirstOrDefault(entry => !string.Equals(entry.Proposed, entry.Word, StringComparison.Ordinal)) ?? group.First())
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
        var output = new StringBuilder("Слова для ручной проверки: многосложные слова (два и более слогов) с русскими гласными, в которых после обработки нет явной отметки ударения и нет буквы ё. Односложные слова программа намеренно оставляет без ударения: положение ударения в них однозначно. Такие слова могут быть корректными безударными в записи, неизвестными модели или опечатками; список не утверждает, что каждое слово ошибочно." + Environment.NewLine + Environment.NewLine);
        if (list.Length == 0) return "Многосложных слов без явного ударения и без буквы ё не найдено." + Environment.NewLine;
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

    // Typo correction: the corrected word may differ in letters from the source
    // (unlike a stress-only correction), but must remain a valid Russian word
    // and be close enough to the original to avoid accidental text corruption.
    internal static bool IsValidTypoCorrection(string sourceWord, string corrected)
    {
        if (string.IsNullOrWhiteSpace(corrected) || corrected.Any(char.IsWhiteSpace)) return false;
        if (!IsValidStressedWord(corrected)) return false;
        var sourceNorm = NormalizeWordLetters(sourceWord).ToLowerInvariant();
        var targetNorm = NormalizeWordLetters(corrected).ToLowerInvariant();
        if (sourceNorm == targetNorm) return false; // accent-only fix — handled by IsValidCorrection
        var distance = LevenshteinDistance(sourceNorm, targetNorm);
        var maxEdit = Math.Max(1, Math.Min(3, sourceNorm.Length / 2));
        return distance <= maxEdit;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[n, m];
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
        if (index == 0) return false;
        var stressed = word[index - 1];
        // Обычно ударение падает на гласную. Но в древнерусской и церковнославянской
        // орфографии ударной может быть и ѣ, ѧ, і и другие буквы вне современного алфавита
        // («свѣ́тлостію», «мѣ́дъ»), поэтому разрешаем знак ударения после любой кириллической буквы.
        if (!IsCyrillicLetter(stressed)) return false;
        // Ё is inherently stressed and does not take a separate acute mark.
        return char.ToLowerInvariant(stressed) != 'ё';
    }

    private static bool IsCyrillicLetter(char ch) =>
        ch is >= 'Ѐ' and <= 'ӿ' or >= 'а' and <= 'я' or >= 'А' and <= 'Я';

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
        if (!IsValidCorrection(entry.Word, corrected) && !IsValidTypoCorrection(entry.Word, corrected))
            throw new ArgumentException("Некорректный вариант исправления.");
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

    internal static Dictionary<string, int> CountReviewWordOccurrences(string outputPath, IEnumerable<string> words)
    {
        if (Path.GetExtension(outputPath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return CountReviewWordOccurrencesInText(outputPath, words);
        return CountReviewWordOccurrencesInFb2(outputPath, words);
    }

    private static Dictionary<string, int> CountReviewWordOccurrencesInFb2(string outputPath, IEnumerable<string> words)
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

    private static Dictionary<string, int> CountReviewWordOccurrencesInText(string path, IEnumerable<string> words)
    {
        var counts = words.Distinct(StringComparer.Ordinal).ToDictionary(word => word, _ => 0, StringComparer.Ordinal);
        if (!File.Exists(path) || counts.Count == 0) return counts;
        var text = DecodeText(File.ReadAllBytes(path));
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

    // TXT equivalents of the FB2 pattern and word-form apply functions.
    // They preserve the original encoding, BOM and line endings of the source file.
    internal static int ApplyReviewPatternCorrectionsToTextFile(string inputPath, string outputPath, IEnumerable<WordReviewPatternCorrection> corrections)
    {
        var entries = corrections.ToArray();
        if (entries.Length == 0) return 0;
        var map = entries.GroupBy(item => NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var excluded = entries.Where(item => item.ExcludedContext != null)
            .Select(item => (Key: NormalizeWordLetters(item.SourceWord).ToLowerInvariant(), Context: item.ExcludedContext!, item.ExcludedOccurrence))
            .ToHashSet();
        return ApplyPatternReplacementMapToText(inputPath, outputPath, map, excluded);
    }

    internal static int ApplyWordCorrectionsToTextFile(string inputPath, string outputPath, IEnumerable<DeclensionCorrection> corrections)
    {
        var correctionMap = corrections.GroupBy(correction => NormalizeWordLetters(correction.Form).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Form, StringComparer.OrdinalIgnoreCase);
        return ApplyWordReplacementMapToText(inputPath, outputPath, correctionMap);
    }

    private static int ApplyPatternReplacementMapToText(string inputPath, string outputPath,
        Dictionary<string, WordReviewPatternCorrection> correctionMap,
        HashSet<(string Key, string Context, int ExcludedOccurrence)> excluded)
    {
        if (correctionMap.Count == 0) return 0;
        LogReview($"Применение правок TXT: {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)}; групп слов: {correctionMap.Count}; исключённых контекстов: {excluded.Count}");
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var lines = text.Split('\n');
        var replacements = 0;
        var excludedContexts = excluded.Where(item => item.Key.Length > 0).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ending = line.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            var content = ending.Length == 0 ? line : line[..^1];
            var updated = WordTokens.Replace(content, match =>
            {
                var normalized = NormalizeWordLetters(match.Value);
                var key = normalized.ToLowerInvariant();
                if (!correctionMap.TryGetValue(key, out var correction)) return match.Value;
                if (excludedContexts.Any(item => item.Key == key && item.ExcludedOccurrence >= 0 &&
                    string.Equals(item.Context, content, StringComparison.Ordinal) && CountContextOccurrence(content, match.Index, match.Value) == item.ExcludedOccurrence)) return match.Value;
                replacements++;
                return correction.Corrected;
            });
            lines[i] = updated + ending;
        }
        LogReview($"Заменено вхождений в TXT: {replacements}");
        if (replacements == 0) return 0;
        File.WriteAllBytes(outputPath, EncodeText(string.Join("\n", lines), sourceBytes));
        return replacements;
    }

    private static int ApplyWordReplacementMapToText(string inputPath, string outputPath, Dictionary<string, string> correctionMap)
    {
        if (correctionMap.Count == 0) return 0;
        var sourceBytes = File.ReadAllBytes(inputPath);
        var text = DecodeText(sourceBytes);
        var replacements = 0;
        var updated = WordTokens.Replace(text, match =>
        {
            var key = NormalizeWordLetters(match.Value).ToLowerInvariant();
            if (!correctionMap.TryGetValue(key, out var corrected)) return match.Value;
            replacements++;
            return corrected;
        });
        if (replacements == 0) return 0;
        File.WriteAllBytes(outputPath, EncodeText(updated, sourceBytes));
        return replacements;
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
        var originalTextNodes = root.DescendantNodes().OfType<XText>().ToArray();
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
        // Reuse the structure-preserving writer used by the review path so applying
        // saved forms never drops comments, CDATA or the original FB2 formatting.
        if (!originalTextNodes.SequenceEqual(root.DescendantNodes().OfType<XText>()))
            throw new InvalidDataException("Изменился состав XML-текстовых узлов; безопасно сохранить исходную структуру FB2 невозможно.");
        SaveFb2PreservingSourceStructure(inputPath, outputPath, document);
        _ = new Fb2Parser().Parse(File.ReadAllBytes(outputPath));
        return replacements;
    }

    private static Dictionary<string, string> LoadYoDictionary()
    {
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dictionaryDirectory = Path.Combine(ApplicationDirectory, "dictionary");
        var dictionaryFiles = Directory.Exists(dictionaryDirectory)
            ? Directory.EnumerateFiles(dictionaryDirectory, "*.txt")
            : Enumerable.Empty<string>();
        if (!dictionaryFiles.Any())
        {
            var projectDirectory = Path.Combine(Directory.GetCurrentDirectory(), "dictionary");
            dictionaryFiles = Directory.Exists(projectDirectory)
                ? Directory.EnumerateFiles(projectDirectory, "*.txt")
                : Enumerable.Empty<string>();
        }
        foreach (var dictionaryFile in dictionaryFiles)
        foreach (var line in File.ReadLines(dictionaryFile, Encoding.UTF8))
        {
            var word = line.Trim().TrimStart('\uFEFF');
            if (word.Length == 0 || word.StartsWith('#') || !word.Contains('ё', StringComparison.OrdinalIgnoreCase)) continue;
            var normalized = NormalizeWordLetters(word).ToLowerInvariant();
            if (!words.ContainsKey(normalized)) words[normalized] = word;
        }
        return words;
    }

    private static string ApplyYoDictionary(string text)
    {
        var words = YoDictionary.Value;
        if (words.Count == 0) return text;
        return WordTokens.Replace(text, match =>
        {
            var sourceWord = match.Value;
            var normalized = NormalizeWordLetters(sourceWord).ToLowerInvariant();
            if (!words.TryGetValue(normalized, out var dictionaryWord)) return sourceWord;
            var sourceLetters = sourceWord.Replace("\u0301", "", StringComparison.Ordinal);
            var dictionaryLetters = dictionaryWord.Replace("ё", "е", StringComparison.OrdinalIgnoreCase);
            if (!sourceLetters.Equals(dictionaryLetters, StringComparison.OrdinalIgnoreCase)) return sourceWord;
            if (sourceWord.Any(ch => char.IsDigit(ch) || ch == '_')) return sourceWord;
            var stressIndex = sourceWord.IndexOf('\u0301');
            if (stressIndex >= 0 && (stressIndex == 0 || char.ToLowerInvariant(sourceWord[stressIndex - 1]) != 'е'))
                return sourceWord;
            // Preserve the source token's capitalization while restoring ё.
            var replacement = dictionaryWord;
            if (sourceWord.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)))
                replacement = replacement.ToUpperInvariant();
            else if (char.IsUpper(sourceWord[0]))
                replacement = char.ToUpperInvariant(replacement[0]) + replacement[1..];
            return replacement;
        });
    }

    private static ConcurrentDictionary<string, string> LoadUserDictionary()
    {
        var replacements = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

    // Like NormalizeWordLetters, but keeps the ё/е distinction: RUAccent's stress
    // dictionary stores ё spellings verbatim, so a е/ё mismatch must not match.
    private static string DictionaryKey(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var ch in word)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    // Adds a stress mark to words the neural model left unmarked, using RUAccent's
    // full stress dictionary (accents.json.gz). Every dictionary entry is the same
    // word with a single '+' before the stressed vowel, so applying a match only
    // ever adds a mark — never changes letters. Capitalisation is restored from the
    // source token, and ё words are skipped because ё already carries the stress.
    private static string ApplyDictionaryStress(string text, IReadOnlyDictionary<string, string> dictionary)
    {
        if (dictionary.Count == 0 || string.IsNullOrEmpty(text)) return text;
        return WordTokens.Replace(text, match =>
        {
            var word = match.Value;
            if (word.IndexOf('\u0301') >= 0 || word.IndexOf('ё') >= 0 || word.IndexOf('Ё') >= 0) return word;
            if (!dictionary.TryGetValue(DictionaryKey(word), out var stressed)) return word;
            var replacement = stressed.Replace("+", "\u0301");
            if (word.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)))
                return replacement.ToUpperInvariant();
            if (char.IsUpper(word[0])) return UppercaseFirstLetter(replacement);
            return replacement;
        });
    }

    // Uppercases the first letter, skipping a leading stress mark (U+0301), so a
    // dictionary form like "+яхонтовым" becomes "Я́хонтовым" rather than "я́хонтовым".
    private static string UppercaseFirstLetter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i])) continue;
            return text[..i] + char.ToUpperInvariant(text[i]) + text[(i + 1)..];
        }
        return text;
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

    // --- Локальный ИИ-проход по контексту -------------------------------------------------
    // Слова, которые нейромодель RUAccent оставила без ударения (редкие, диалектные,
    // устаревшие, имена), уточняются локальной языковой моделью на этом же компьютере.
    // Проход идёт отдельным процессом Python: onnxruntime-genai тянет своё CUDA-окружение
    // и не должен конфликтовать с сессиями RUAccent в основном процессе.
    // Результат — только подсказки: они показываются в окне проверки и применяются
    // пользователем вручную.

    internal const int LlmBatchSize = 8;
    internal const string DefaultLlmModelFolder = "Qwen2.5-3B-Instruct-onnx";
    internal const string LlmGenaiConfigName = "genai_config.json";

    // Контрольные слова для теста движка: ударение каждого сверено с полным словарём ударений
    // RUAccent (accents.json.gz). Тест отвечает на вопрос «какой движок стоит выбрать на этом
    // компьютере»: он показывает и скорость, и число верных ударений. Контекст — обычное
    // предложение со словом: в книге слово тоже приходит с контекстом, иначе задача не та.
    internal static readonly (string Word, int Stress, string Context)[] LlmBenchmarkWords =
    {
        ("договор", 5, "Мы подписали договор вчера вечером."),
        ("звонит", 4, "Он звонит мне каждый вечер."),
        ("каталог", 5, "Библиотекарь открыл каталог и нашёл нужную книгу."),
        ("квартал", 5, "Мы переехали в новый квартал."),
        ("красивее", 4, "Этот сад красивее прежнего."),
        ("банты", 1, "Она завязала банты и вышла на улицу."),
        ("торты", 1, "Мама испекла торты к празднику."),
        ("жалюзи", 5, "Он опустил жалюзи и включил свет."),
        ("щавель", 3, "Бабушка принесла щавель с огорода."),
        ("баловать", 5, "Не надо баловать детей сладостями."),
        ("ходатайство", 3, "Адвокат подал ходатайство в суд."),
        ("обеспечение", 5, "Отец отвечает за обеспечение семьи."),
        ("украинский", 4, "Он выучил украинский язык за год."),
        ("бухгалтер", 4, "Бухгалтер посчитал расходы за месяц."),
        ("мизерный", 1, "Он получил мизерный оклад."),
        ("вероисповедание", 9, "В анкете спрашивают вероисповедание."),
        ("нефтепровод", 9, "Через степь проложили нефтепровод."),
        ("туфля", 1, "Одна туфля куда-то пропала."),
        ("средство", 2, "Это средство хорошо помогает от кашля."),
        ("диспансер", 7, "Он пошёл в диспансер на проверку."),
        ("феномен", 3, "Этот феномен изучают уже сто лет."),
        ("гастрономия", 7, "Она любит французскую гастрономию."),
        ("цемент", 3, "Рабочие привезли цемент и песок."),
        // И простые слова: так видно и базовую работу модели, и провалы на редких словах.
        ("молоко", 5, "Он выпил молоко утром."),
        ("собака", 3, "Собака громко лаяла во дворе."),
        ("человек", 5, "Этот человек пришёл первым."),
        ("работа", 3, "Работа заняла весь день."),
        ("деревня", 3, "Деревня стоит у самой реки."),
        ("окно", 3, "Он открыл окно и впустил свежий воздух."),
        ("книга", 2, "Книга лежала на столе."),
        ("ворона", 3, "Ворона села на забор."),
    };

    // Знак ударения ставится после ударной гласной — так же, как в подсказках ИИ и в окне проверки.
    internal static string StressMarked(string word, int vowelIndex) =>
        word[..(vowelIndex + 1)] + "\u0301" + word[(vowelIndex + 1)..];

    internal static string LlmModelsDirectory => Path.Combine(ModelCacheDirectory, "llm");
    internal static string LlmSettingsPath => Path.Combine(DataDirectory, "llm-настройки.json");
    internal static string LlmSuggestionsPath => Path.Combine(DataDirectory, "llm-подсказки.json");

    // Папка выбранной модели. В настройках хранится либо имя папки внутри ресурсы\модели\llm,
    // либо полный путь — тогда модель можно держать где угодно.
    internal static string LlmModelDirectory(string model) =>
        Path.IsPathRooted(model) ? model : Path.Combine(LlmModelsDirectory, model);

    // --- Способы получить подсказки: своя модель на диске, сервер Ollama или внешний API ---
    // Сервис с OpenAI-совместимым интерфейсом (OpenAI, OpenRouter, LM Studio, llama.cpp, vLLM)
    // подключается одинаково: адрес, ключ, имя модели. Модели без цензуры тоже подходят — это
    // просто другая модель у того же сервиса, особых настроек для них не требуется.
    internal sealed record LlmEngine(string Provider, string LocalModel, string RemoteModel, string BaseUrl, string ApiKey)
    {
        public const string Local = "local";
        public const string Ollama = "ollama";
        public const string OpenAi = "openai";

        public bool IsLocal => Provider is not (Ollama or OpenAi);

        // Имя движка для кэша подсказок и для журнала. Ключ внешнего сервиса сюда не попадает.
        public string Id => Provider switch
        {
            Ollama => "ollama#" + RemoteModel,
            OpenAi => "api#" + HostOf(BaseUrl) + "#" + RemoteModel,
            _ => LocalModel,
        };

        public string Title => Provider switch
        {
            Ollama => string.IsNullOrWhiteSpace(RemoteModel) ? $"Ollama ({BaseUrl})" : $"Ollama ({RemoteModel})",
            OpenAi => string.IsNullOrWhiteSpace(RemoteModel)
                ? $"внешний API ({BaseUrl})"
                : $"внешний API ({RemoteModel}, {BaseUrl})",
            _ => LocalModel,
        };

        public static LlmEngine FromSettings(LlmSettings settings, string localModel)
        {
            var provider = (settings.Provider ?? Local).ToLowerInvariant();
            return new LlmEngine(
                provider,
                localModel,
                provider == Ollama ? settings.OllamaModel : settings.ApiModel,
                provider == Ollama ? settings.OllamaUrl : settings.ApiUrl,
                provider == OpenAi ? settings.ApiKey : "");
        }

        private static string HostOf(string url)
        {
            try
            {
                var parsed = new Uri(url);
                return parsed.Host + parsed.AbsolutePath.TrimEnd('/');
            }
            catch (UriFormatException)
            {
                return url;
            }
        }
    }

    // Настройки ИИ сохраняются в ресурсы\llm-настройки.json. Ключ внешнего сервиса лежит там же
    // в открытом виде: файл не стоит никому передавать, а вместо него можно задать переменную
    // окружения DSR_LLM_API_KEY.
    internal sealed record LlmSettings
    {
        public string Provider { get; init; } = LlmEngine.Local;
        public string Model { get; init; } = "";
        public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";
        public string OllamaModel { get; init; } = "";
        public string ApiUrl { get; init; } = "https://api.openai.com/v1";
        public string ApiModel { get; init; } = "";
        public string ApiKey { get; init; } = "";
        // Искать ли подсказку у других движков, прежде чем спрашивать выбранный.
        public bool ReuseOtherEngines { get; init; } = true;
        public Dictionary<string, LlmBenchmark>? Benchmarks { get; init; }
    }

    internal sealed record LlmBenchmark(string Engine, string Title, int Correct, int Total, double Seconds, string When);

    private static readonly JsonSerializerOptions LlmJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Готовыми моделями считаются все папки рядом с программой, в которых есть genai_config.json:
    // чтобы добавить новую, достаточно положить её в ресурсы\модели\llm — она сразу появится в списке.
    internal static List<string> DiscoverLlmModels()
    {
        var models = new List<string>();
        try
        {
            if (Directory.Exists(LlmModelsDirectory))
                foreach (var directory in Directory.EnumerateDirectories(LlmModelsDirectory))
                    if (File.Exists(Path.Combine(directory, LlmGenaiConfigName)))
                        models.Add(Path.GetFileName(directory));
        }
        catch (Exception error)
        {
            LogError("Поиск моделей ИИ", error);
        }
        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    internal static LlmSettings LoadLlmSettings()
    {
        try
        {
            if (File.Exists(LlmSettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<LlmSettings>(File.ReadAllText(LlmSettingsPath, Encoding.UTF8));
                if (loaded != null)
                    return loaded.Benchmarks == null
                        ? loaded with { Benchmarks = new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal) }
                        : loaded;
            }
        }
        catch (Exception error)
        {
            LogError("Чтение настроек ИИ", error);
        }
        return new LlmSettings
        {
            Model = DefaultLlmModelFolder,
            Benchmarks = new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal),
        };
    }

    internal static void SaveLlmSettings(LlmSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LlmSettingsPath,
                JsonSerializer.Serialize(settings, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись настроек ИИ", error);
        }
    }

    // Движок из настроек: его используют и окно проверки, и командная строка.
    internal static LlmEngine CurrentLlmEngine() => LlmEngine.FromSettings(LoadLlmSettings(), CurrentLlmModel());

    // Выбранные в окне настройки: удобно собрать их одной строкой и сохранить.
    internal static void SaveLlmSettings(string provider, string localModel, string ollamaUrl, string ollamaModel,
        string apiUrl, string apiModel, string apiKey, bool reuseOtherEngines)
    {
        var benchmarks = LoadLlmSettings().Benchmarks ?? new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal);
        SaveLlmSettings(new LlmSettings
        {
            Provider = provider,
            Model = localModel,
            OllamaUrl = ollamaUrl,
            OllamaModel = ollamaModel,
            ApiUrl = apiUrl,
            ApiModel = apiModel,
            ApiKey = apiKey,
            ReuseOtherEngines = reuseOtherEngines,
            Benchmarks = benchmarks,
        });
    }

    internal static void SaveLlmBenchmark(LlmBenchmarkResult result)
    {
        if (!result.Ok) return;
        var settings = LoadLlmSettings();
        var benchmarks = new Dictionary<string, LlmBenchmark>(
            settings.Benchmarks ?? new Dictionary<string, LlmBenchmark>(StringComparer.Ordinal), StringComparer.Ordinal)
        {
            [result.EngineId] = new LlmBenchmark(result.EngineId, result.Title, result.Correct, result.Total,
                result.Seconds, DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
        };
        SaveLlmSettings(settings with { Benchmarks = benchmarks });
    }

    // Выбранная модель: сохранённый выбор, если такая папка на месте, иначе модель по умолчанию,
    // иначе первая найденная.
    internal static string CurrentLlmModel()
    {
        var saved = LoadLlmSettings().Model;
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(Path.Combine(LlmModelDirectory(saved), LlmGenaiConfigName)))
            return saved;
        var available = DiscoverLlmModels();
        var preferred = available.FirstOrDefault(name =>
            string.Equals(name, DefaultLlmModelFolder, StringComparison.OrdinalIgnoreCase));
        return preferred ?? available.FirstOrDefault() ?? DefaultLlmModelFolder;
    }

    internal sealed record LlmStressItem(string Word, IReadOnlyList<string> Contexts);

    // Ответ модели, который не удалось принять как правку: сырой ответ и причина отказа.
    internal sealed record LlmRejected(string Raw, string Reason);

    internal sealed record LlmStressOutcome(
        bool Ok, string Message, string Device, double Seconds, int Answered, int Items,
        Dictionary<string, string> Suggestions, Dictionary<string, LlmRejected> Rejected)
    {
        public static LlmStressOutcome Fail(string message, int items) =>
            new(false, message, "", 0, 0, items,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlmRejected>(StringComparer.Ordinal));
    }

    // Кэш подсказок: ключ — модель, имя книги и слово, значение — слово со знаком ударения.
    // Кэш экономит самый дорогой шаг (загрузка модели и генерация) при повторных прогонах
    // той же книги и хранится рядом с пользовательским словарём. Модель входит в ключ, чтобы
    // ответы одной модели не подменяли ответы другой при переключении.
    internal static Dictionary<string, string> LoadLlmSuggestions()
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(LlmSuggestionsPath)) return empty;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(LlmSuggestionsPath, Encoding.UTF8));
            if (loaded == null) return empty;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in loaded) result[MigrateLlmKey(pair.Key)] = pair.Value;
            return result;
        }
        catch (Exception error)
        {
            LogError("Чтение кэша ИИ-подсказок", error);
            return empty;
        }
    }

    internal static void SaveLlmSuggestions(Dictionary<string, string> suggestions)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LlmSuggestionsPath,
                JsonSerializer.Serialize(suggestions, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись кэша ИИ-подсказок", error);
        }
    }

    internal static string LlmCacheKey(string model, string book, string word) =>
        (model + "|" + LlmBookName(book) + "|" + word).ToLowerInvariant();

    // Ключи прежних версий не содержали модель: их ответы получены моделью по умолчанию,
    // поэтому при чтении старых файлов дописываем её имя, чтобы кэш не терялся.
    private static string MigrateLlmKey(string key) =>
        key.Count(character => character == '|') == 1
            ? DefaultLlmModelFolder.ToLowerInvariant() + "|" + key
            : key;

    // Готовая подсказка по этому слову, найденная у любой модели. Нужна при переключении
    // моделей: слова, которые уже разобрала прежняя модель, повторно не отправляются —
    // иначе каждая смена модели стоила бы полного прогона по всей книге.
    internal static string? FindAnyLlmSuggestion(Dictionary<string, string> suggestions, string book, string word)
    {
        var suffix = "|" + LlmBookName(book).ToLowerInvariant() + "|" + word.ToLowerInvariant();
        foreach (var pair in suggestions)
            if (pair.Key.EndsWith(suffix, StringComparison.Ordinal))
                return pair.Value;
        return null;
    }

    // Имя книги одинаково для файла-книги и для отчёта «…_проверка-слов.txt», чтобы кэш
    // подсказок совпадал при запуске из окна и из командной строки.
    internal static string LlmBookName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string reviewSuffix = "_проверка-слов";
        return name.EndsWith(reviewSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^reviewSuffix.Length]
            : name;
    }

    internal static LlmStressOutcome RunLlmStressCheck(IReadOnlyList<LlmStressItem> items, string book,
        Action<string> report, CancellationToken token, LlmEngine? engine = null, bool benchmark = false)
    {
        if (items.Count == 0)
            return new LlmStressOutcome(true, "Нет слов для проверки.", "", 0, 0, 0,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlmRejected>(StringComparer.Ordinal));
        var chosen = engine ?? CurrentLlmEngine();
        var modelDirectory = LlmModelDirectory(chosen.LocalModel);
        if (chosen.IsLocal && (string.IsNullOrWhiteSpace(chosen.LocalModel)
                || !File.Exists(Path.Combine(modelDirectory, LlmGenaiConfigName))))
            return LlmStressOutcome.Fail(
                "Не найдена выбранная модель ИИ: " + (string.IsNullOrWhiteSpace(chosen.LocalModel) ? "(не выбрана)" : chosen.LocalModel) +
                Environment.NewLine + "Ожидается папка:" + Environment.NewLine + modelDirectory +
                Environment.NewLine + Environment.NewLine +
                "Модель скачивается один раз командой:" + Environment.NewLine +
                "python -c \"from huggingface_hub import snapshot_download; " +
                "snapshot_download('elbruno/Qwen2.5-3B-Instruct-onnx', local_dir=r'" + modelDirectory + "')\"",
                items.Count);
        if (!chosen.IsLocal && (string.IsNullOrWhiteSpace(chosen.BaseUrl) || string.IsNullOrWhiteSpace(chosen.RemoteModel)))
            return LlmStressOutcome.Fail(
                chosen.Provider == LlmEngine.Ollama
                    ? "Не выбран сервер Ollama или модель. Укажите адрес (обычно http://127.0.0.1:11434) " +
                      "и имя загруженной модели, например qwen2.5:7b."
                    : "Не указан адрес сервиса или имя модели. Адрес нужен вместе с версией, " +
                      "например https://api.openai.com/v1.",
                items.Count);

        string script;
        string python;
        try
        {
            script = ExtractEmbeddedFile("DSR.llm_stress_worker.py", "llm_stress_worker.py");
            python = EnsurePythonRuntime();
            // Локальная модель ONNX требует пакет onnxruntime-genai — доустанавливаем его здесь.
            if (chosen.IsLocal)
            {
                var runtimeError = EnsureLlmRuntimeOrError(python);
                if (runtimeError != null)
                    return LlmStressOutcome.Fail(runtimeError, items.Count);
            }
        }
        catch (Exception error)
        {
            LogError("Подготовка ИИ-прохода", error);
            return LlmStressOutcome.Fail(error.Message, items.Count);
        }

        var work = Path.Combine(DataDirectory, "временные-файлы");
        Directory.CreateDirectory(work);
        var requestPath = Path.Combine(work, "llm-запрос.json");
        var responsePath = Path.Combine(work, "llm-ответ.json");
        try
        {
            if (File.Exists(responsePath)) File.Delete(responsePath);
            var payload = new
            {
                book = new { title = Path.GetFileNameWithoutExtension(book), author = "" },
                words = items.Select(item => new { word = item.Word, contexts = item.Contexts.ToArray() }).ToArray(),
            };
            File.WriteAllText(requestPath, JsonSerializer.Serialize(payload, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись запроса для ИИ", error);
            return LlmStressOutcome.Fail("Не удалось подготовить запрос к ИИ: " + error.Message, items.Count);
        }

        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.Environment["PYTHONUTF8"] = "1";
        ConfigureResourceEnvironment(start);
        var arguments = new List<string>
        {
            "-u", script,
            "--in", requestPath,
            "--out", responsePath,
            "--batch", LlmBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (chosen.IsLocal)
        {
            arguments.Add("--model");
            arguments.Add(modelDirectory);
        }
        else
        {
            arguments.Add("--provider");
            arguments.Add(chosen.Provider);
            arguments.Add("--base-url");
            arguments.Add(chosen.BaseUrl);
            arguments.Add("--remote-model");
            arguments.Add(chosen.RemoteModel);
            if (!string.IsNullOrWhiteSpace(chosen.ApiKey))
                start.Environment["DSR_LLM_API_KEY"] = chosen.ApiKey;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var errors = new StringBuilder();
        Process process;
        try
        {
            process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить процесс ИИ.");
        }
        catch (Exception error)
        {
            LogError("Запуск ИИ-прохода", error);
            return LlmStressOutcome.Fail("Не удалось запустить ИИ-проход: " + error.Message, items.Count);
        }

        void Handle(string? line)
        {
            if (line == null) return;
            var data = TrimProtocolLine(line);
            if (!data.StartsWith("DSR_LLM:", StringComparison.Ordinal))
            {
                if (data.Length > 0) lock (errors) errors.AppendLine(data);
                return;
            }
            foreach (var message in TranslateLlmProgress(data["DSR_LLM:".Length..]))
                report(message);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (process)
        {
            try
            {
                while (!process.WaitForExit(300))
                {
                    if (!token.IsCancellationRequested) continue;
                    try { process.Kill(true); } catch { /* процесс уже завершился */ }
                    return LlmStressOutcome.Fail("ИИ-проход отменён.", items.Count);
                }
                process.WaitForExit();
            }
            catch (Exception error)
            {
                LogError("Ожидание ИИ-прохода", error);
                return LlmStressOutcome.Fail("Ошибка ИИ-прохода: " + error.Message, items.Count);
            }
        }

        if (!File.Exists(responsePath))
            return LlmStressOutcome.Fail(
                "ИИ-проход завершился без ответа." + Environment.NewLine + errors.ToString().Trim(), items.Count);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(responsePath, Encoding.UTF8));
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                return LlmStressOutcome.Fail(errorElement.GetString() ?? "Ошибка ИИ-прохода", items.Count);

            var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
            var rejected = new Dictionary<string, LlmRejected>(StringComparer.Ordinal);
            var rows = new StringBuilder();
            var answered = 0;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var word = item.TryGetProperty("word", out var wordElement) && wordElement.ValueKind == JsonValueKind.String
                        ? wordElement.GetString() : null;
                    if (string.IsNullOrEmpty(word)) continue;
                    var suggestion = item.TryGetProperty("suggestion", out var suggestionElement) && suggestionElement.ValueKind == JsonValueKind.String
                        ? suggestionElement.GetString() : null;
                    var raw = item.TryGetProperty("raw", out var rawElement) && rawElement.ValueKind == JsonValueKind.String
                        ? rawElement.GetString() : null;
                    var reason = item.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
                        ? sourceElement.GetString() ?? "" : "";
                    // Ответ принимается только если он проходит ту же проверку, что и правка,
                    // введённая человеком: те же буквы и ровно один знак ударения.
                    if (!string.IsNullOrEmpty(suggestion) && IsValidCorrection(word, suggestion))
                    {
                        suggestions[word] = suggestion;
                        answered++;
                        rows.AppendLine($"принято\t{word}\t{suggestion}\t{(string.IsNullOrEmpty(reason) ? "model" : reason)}");
                    }
                    else
                    {
                        // Сюда попадают все слова без готового ответа — и те, где модель ответила
                        // не тем словом, и те, где ответа не было вовсе: их надо показать честно.
                        if (!string.IsNullOrEmpty(suggestion))
                            rejected[word] = new LlmRejected(suggestion, "ответ не похож на исходное слово");
                        else
                            rejected[word] = new LlmRejected(raw ?? "", string.IsNullOrEmpty(reason) ? "none" : reason);
                        rows.AppendLine($"отклонено\t{word}\t{raw ?? ""}\t{(string.IsNullOrEmpty(reason) ? "нет ответа" : reason)}\t{suggestion ?? ""}");
                    }
                }
            }

            var device = "";
            var seconds = 0d;
            if (root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                if (stats.TryGetProperty("device", out var deviceElement) && deviceElement.ValueKind == JsonValueKind.String)
                    device = deviceElement.GetString() ?? "";
                if (stats.TryGetProperty("seconds", out var secondsElement) && secondsElement.ValueKind == JsonValueKind.Number)
                    seconds = secondsElement.GetDouble();
            }
            var outcome = new LlmStressOutcome(true, "", device, seconds, answered, items.Count, suggestions, rejected);
            if (!benchmark) WriteLlmLog(items, outcome, rows, errors, chosen);
            return outcome;
        }
        catch (Exception error)
        {
            LogError("Чтение ответа ИИ", error);
            return LlmStressOutcome.Fail("Не удалось разобрать ответ ИИ: " + error.Message, items.Count);
        }
    }

    internal static string LlmLogPath => Path.Combine(DataDirectory, "DSR-ии-проверка.log");

    // Слова, по которым модель уже не смогла дать ответ. Генерация идёт без случайности
    // (greedy) и повторный проход по одному слову даёт тот же результат, поэтому такие
    // слова больше не отправляются — иначе каждая проверка снова занимала бы минуты.
    internal static string LlmFailuresPath => Path.Combine(DataDirectory, "llm-без-ответа.json");

    internal sealed record LlmFailure(string Word, string Raw, string Reason);

    internal static Dictionary<string, LlmFailure> LoadLlmFailures()
    {
        var empty = new Dictionary<string, LlmFailure>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(LlmFailuresPath)) return empty;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LlmFailure>>(
                File.ReadAllText(LlmFailuresPath, Encoding.UTF8));
            if (loaded == null) return empty;
            var result = new Dictionary<string, LlmFailure>(StringComparer.Ordinal);
            foreach (var pair in loaded) result[MigrateLlmKey(pair.Key)] = pair.Value;
            return result;
        }
        catch (Exception error)
        {
            LogError("Чтение списка слов без ответа ИИ", error);
            return empty;
        }
    }

    internal static void SaveLlmFailures(Dictionary<string, LlmFailure> failures)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LlmFailuresPath,
                JsonSerializer.Serialize(failures, LlmJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись списка слов без ответа ИИ", error);
        }
    }

    // Подробный журнал последнего ИИ-прохода: что спросили, что ответила модель, что принято.
    // Нужен, когда модель ответила, но подсказок не появилось.
    private static void WriteLlmLog(IReadOnlyList<LlmStressItem> items, LlmStressOutcome outcome,
        StringBuilder rows, StringBuilder errors, LlmEngine engine)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var logged = new HashSet<string>(
                rows.ToString().Split('\n').Select(line => line.Split('\t')).Where(parts => parts.Length > 1)
                    .Select(parts => parts[1]), StringComparer.Ordinal);
            var text = new StringBuilder();
            text.AppendLine("ИИ-проверка ударений, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("Движок: " + engine.Title + (engine.IsLocal ? " (" + LlmModelDirectory(engine.LocalModel) + ")" : ""));
            text.AppendLine($"Отправлено слов: {items.Count}; принято: {outcome.Answered}; отклонено: {outcome.Rejected.Count}.");
            text.AppendLine($"Вычисления: {outcome.Device}; время: {outcome.Seconds:0} с.");
            text.AppendLine("Строки: итог, слово, что ответила модель, причина.");
            text.Append(rows);
            foreach (var item in items)
            {
                if (logged.Contains(item.Word)) continue;
                text.AppendLine($"без ответа\t{item.Word}\t\tнет строки в ответе модели");
            }
            var errorText = errors.ToString().Trim();
            if (errorText.Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Вывод воркера:");
                foreach (var line in errorText.Split('\n').TakeLast(20)) text.AppendLine(line.TrimEnd());
            }
            File.WriteAllText(LlmLogPath, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            LogError("Запись журнала ИИ-проверки", error);
        }
    }

    // Режим командной строки: прогон ИИ-подсказок по готовому отчёту «…_проверка-слов.txt»
    // без запуска интерфейса. Удобен для длинных книг и для проверки самого механизма.
    // Движок берётся из настроек, если не указан явно ключами --llm-provider/--llm-model/--llm-url.
    private static int RunLlmStressCommand(string reviewPath, LlmEngine engine)
    {
        if (!File.Exists(reviewPath))
        {
            Console.Error.WriteLine("Файл отчёта не найден: " + reviewPath);
            return 1;
        }

        Console.WriteLine("Движок: " + engine.Title);

        List<WordReviewEntry> entries;
        try
        {
            entries = ReadWordReview(reviewPath);
        }
        catch (Exception error)
        {
            LogError("Чтение отчёта для ИИ-проверки", error);
            Console.Error.WriteLine("Не удалось прочитать отчёт: " + error.Message);
            return 1;
        }

        if (entries.Count == 0)
        {
            Console.WriteLine("В отчёте нет слов для проверки.");
            return 0;
        }

        var groups = entries
            .GroupBy(entry => entry.Word.Normalize(NormalizationForm.FormC).ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        var items = groups
            .Select(group => new LlmStressItem(group.First().Word,
                group.Select(entry => entry.Context).Where(context => !string.IsNullOrWhiteSpace(context))
                    .Distinct().Take(2).ToArray()))
            .ToList();

        var settings = LoadLlmSettings();
        var failures = LoadLlmFailures();
        var cached = LoadLlmSuggestions();
        var pending = items.Where(item =>
        {
            var key = LlmCacheKey(engine.Id, reviewPath, item.Word);
            if (failures.ContainsKey(key)) return false;
            // Свой прежний ответ повторно не спрашиваем: ответ того же движка не изменится.
            if (cached.ContainsKey(key)) return false;
            // Чужой ответ принимаем, пока включена галочка «не переспрашивать».
            return !settings.ReuseOtherEngines || FindAnyLlmSuggestion(cached, reviewPath, item.Word) == null;
        }).ToList();
        if (pending.Count < items.Count)
            Console.WriteLine($"Уже проверялись ранее (повторно не отправляются): {items.Count - pending.Count}.");

        Console.WriteLine($"Слов для ИИ-проверки: {pending.Count} (групп слов {groups.Count}).");
        var outcome = RunLlmStressCheck(pending, reviewPath, message => Console.WriteLine(message),
            CancellationToken.None, engine);
        if (!outcome.Ok)
        {
            Console.Error.WriteLine(outcome.Message);
            return 1;
        }

        Console.WriteLine($"Подсказок принято: {outcome.Answered} из {outcome.Items}; отклонено: {outcome.Rejected.Count}; движок: {outcome.Device}; {outcome.Seconds:0} с.");
        foreach (var item in items)
        {
            var itemKey = LlmCacheKey(engine.Id, reviewPath, item.Word);
            if (outcome.Suggestions.TryGetValue(item.Word, out var suggestion))
            {
                Console.WriteLine($"  {item.Word} → {suggestion}");
                continue;
            }
            if (cached.TryGetValue(itemKey, out var knownGood))
            {
                Console.WriteLine($"  {item.Word} → {knownGood} (из кэша)");
                continue;
            }
            if (outcome.Rejected.TryGetValue(item.Word, out var rejected) && !string.IsNullOrEmpty(rejected.Raw))
            {
                Console.WriteLine($"  {item.Word} → модель ответила «{rejected.Raw}» ({rejected.Reason})");
                continue;
            }
            if (failures.TryGetValue(itemKey, out var known) && !string.IsNullOrEmpty(known.Raw))
            {
                Console.WriteLine($"  {item.Word} → модель отвечала «{known.Raw}» ({known.Reason})");
                continue;
            }
            Console.WriteLine($"  {item.Word} → (нет ответа)");
        }

        foreach (var pair in outcome.Suggestions) cached[LlmCacheKey(engine.Id, reviewPath, pair.Key)] = pair.Value;
        SaveLlmSuggestions(cached);
        foreach (var pair in outcome.Rejected)
            failures[LlmCacheKey(engine.Id, reviewPath, pair.Key)] = new LlmFailure(pair.Key, pair.Value.Raw, pair.Value.Reason);
        SaveLlmFailures(failures);
        Console.WriteLine("Подсказки сохранены в " + LlmSuggestionsPath);
        Console.WriteLine("Подробности по каждому слову: " + LlmLogPath);
        return 0;
    }

    // Тест движка на контрольных словах: скорость и число верных ударений.
    internal static LlmBenchmarkResult RunLlmBenchmark(LlmEngine engine, Action<string> report, CancellationToken token)
    {
        var items = LlmBenchmarkWords
            .Select(pair => new LlmStressItem(pair.Word, new[] { pair.Context }))
            .ToList();
        var outcome = RunLlmStressCheck(items, "контрольные-слова", report, token, engine, benchmark: true);
        if (!outcome.Ok)
            return new LlmBenchmarkResult(false, outcome.Message, engine.Id, engine.Title, 0, items.Count, 0,
                Array.Empty<string>());

        var correct = 0;
        var details = new List<string>();
        foreach (var (word, stress, _) in LlmBenchmarkWords)
        {
            var expected = StressMarked(word, stress);
            var got = outcome.Suggestions.TryGetValue(word, out var value) ? value : null;
            if (string.Equals(got, expected, StringComparison.Ordinal))
            {
                correct++;
                details.Add($"верно\t{word}\t{expected}");
            }
            else
            {
                details.Add($"неверно\t{word}\t{expected}\t{got ?? "(нет ответа)"}");
            }
        }
        return new LlmBenchmarkResult(true, "", engine.Id, engine.Title, correct, items.Count, outcome.Seconds, details);
    }

    internal sealed record LlmBenchmarkResult(bool Ok, string Message, string EngineId, string Title,
        int Correct, int Total, double Seconds, IReadOnlyList<string> Details);

    // Рекомендация по движкам: сначала больше верных ударений, затем меньше времени.
    internal static string? LlmBenchmarkAdvice()
    {
        var benchmarks = LoadLlmSettings().Benchmarks;
        if (benchmarks == null || benchmarks.Count == 0) return null;
        var best = benchmarks.Values
            .OrderByDescending(item => item.Total == 0 ? 0 : (double)item.Correct / item.Total)
            .ThenBy(item => item.Seconds)
            .First();
        return $"Лучший результат: {best.Title} — {best.Correct} из {best.Total} верных ударений, " +
               $"{best.Seconds:0} с ({best.Seconds / Math.Max(1, best.Total):0.0} с на слово).";
    }

    private static int RunLlmBenchmarkCommand(LlmEngine engine)
    {
        Console.WriteLine("Движок: " + engine.Title);
        var result = RunLlmBenchmark(engine, message => Console.WriteLine(message), CancellationToken.None);
        if (!result.Ok)
        {
            Console.Error.WriteLine(result.Message);
            return 1;
        }
        Console.WriteLine($"Верных ударений: {result.Correct} из {result.Total}; время: {result.Seconds:0} с " +
                          $"({result.Seconds / Math.Max(1, result.Total):0.0} с на слово).");
        foreach (var line in result.Details) Console.WriteLine("  " + line.Replace('\t', ' '));
        SaveLlmBenchmark(result);
        var advice = LlmBenchmarkAdvice();
        if (advice != null) Console.WriteLine(advice);
        return 0;
    }

    // Список моделей у внешнего сервиса: Ollama отдаёт его на /api/tags, OpenAI-совместимые — на /models.
    internal static List<string> ListRemoteLlmModels(LlmEngine engine, out string error)
    {
        error = "";
        var models = new List<string>();
        string script;
        string python;
        try
        {
            script = ExtractEmbeddedFile("DSR.llm_stress_worker.py", "llm_stress_worker.py");
            python = EnsurePythonRuntime();
        }
        catch (Exception problem)
        {
            error = problem.Message;
            return models;
        }

        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.Environment["PYTHONUTF8"] = "1";
        foreach (var argument in new[]
                 {
                     "-u", script, "--list-models", "--provider", engine.Provider,
                     "--base-url", engine.BaseUrl, "--remote-model", engine.RemoteModel,
                 })
            start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(engine.ApiKey)) start.Environment["DSR_LLM_API_KEY"] = engine.ApiKey;

        try
        {
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить список моделей.");
            var output = process.StandardOutput.ReadToEnd();
            var errorText = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(true); } catch { /* процесс уже завершился */ }
                error = "Сервис не ответил за 30 с. Проверьте адрес: " + engine.BaseUrl;
                return models;
            }
            foreach (var line in output.Split('\n'))
            {
                var data = TrimProtocolLine(line.TrimEnd('\r'));
                if (data.StartsWith("DSR_LLM:available:", StringComparison.Ordinal))
                    models.Add(data["DSR_LLM:available:".Length..]);
                else if (data.StartsWith("DSR_LLM:error:", StringComparison.Ordinal))
                    error = data["DSR_LLM:error:".Length..];
            }
            if (models.Count == 0 && error.Length == 0)
                error = errorText.Trim().Length > 0
                    ? errorText.Trim().Split('\n')[0]
                    : "Сервис не сообщил ни одной модели.";
        }
        catch (Exception problem)
        {
            LogError("Список моделей внешнего сервиса", problem);
            error = problem.Message;
        }
        return models;
    }

    // Ключи выбора движка для командной строки: --llm-provider local|ollama|openai,
    // --llm-model <имя|путь>, --llm-url <адрес>, --llm-key <ключ>.
    private static LlmEngine ParseLlmEngineArgs(string[] args, int start)
    {
        string? provider = null;
        string? model = null;
        string? url = null;
        string? key = null;
        for (var index = start; index + 1 < args.Length; index++)
            switch (args[index])
            {
                case "--llm-provider": provider = args[index + 1].Trim().ToLowerInvariant(); break;
                case "--llm-model": model = args[index + 1]; break;
                case "--llm-url": url = args[index + 1]; break;
                case "--llm-key": key = args[index + 1]; break;
            }

        var settings = LoadLlmSettings();
        var chosen = provider ?? (settings.Provider ?? LlmEngine.Local).ToLowerInvariant();
        if (chosen is not (LlmEngine.Local or LlmEngine.Ollama or LlmEngine.OpenAi))
            throw new ArgumentException("--llm-provider принимает только local, ollama или openai.");
        var local = CurrentLlmModel();
        if (chosen == LlmEngine.Local) return new LlmEngine(chosen, model ?? local, "", "", "");
        return new LlmEngine(
            chosen,
            local,
            model ?? (chosen == LlmEngine.Ollama ? settings.OllamaModel : settings.ApiModel) ?? "",
            url ?? (chosen == LlmEngine.Ollama ? settings.OllamaUrl : settings.ApiUrl) ?? "",
            chosen == LlmEngine.OpenAi ? key ?? settings.ApiKey ?? Environment.GetEnvironmentVariable("DSR_LLM_API_KEY") ?? "" : "");
    }

    private static string TrimProtocolLine(string line)
    {
        var start = 0;
        while (start < line.Length && (line[start] == '\0' || line[start] == '\uFEFF' || line[start] == '\u200B'))
            start++;
        return start == 0 ? line : line[start..];
    }

    // Превращает технические строки воркера в короткие понятные сообщения о ходе работы.
    private static IEnumerable<string> TranslateLlmProgress(string payload)
    {
        // Путь к модели содержит «:», поэтому тег отделяем только по первому двоеточию.
        var colon = payload.IndexOf(':');
        var tag = colon < 0 ? payload : payload[..colon];
        var rest = colon < 0 ? string.Empty : payload[(colon + 1)..];
        switch (tag)
        {
            case "model":
                yield return "ИИ: модель " + Path.GetFileName(rest);
                break;
            case "words":
                yield return $"ИИ: слов для проверки — {rest}";
                break;
            case "device":
                yield return $"ИИ: вычисления на {rest}";
                break;
            case "loaded":
                yield return $"ИИ: модель загружена за {rest} с";
                break;
            case "warning":
                yield return "ИИ: " + rest;
                break;
            case "batch_error":
                yield return "ИИ: ошибка запроса — " + rest;
                break;
            case "retry_start":
                yield return $"ИИ: уточняю {rest} слов по одному…";
                break;
            case "progress":
                // progress:<проход>:<готово>/<всего>:<секунды>
                var fields = rest.Split(':');
                if (fields.Length >= 3)
                {
                    var stage = fields[0] == "2" ? "уточнение" : "первый проход";
                    yield return $"ИИ ({stage}): {fields[1]}, {fields[2]}";
                }
                break;
            case "done":
                yield return "ИИ: подсказок получено " + rest;
                break;
            default:
                yield return "ИИ: " + payload;
                break;
        }
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

    // Applies the ё and function-word stress protections to an accented text run.
    // Works token by token so only whole words are matched; the combining acute
    // mark (U+0301) is dropped when it touches ё or when the word is a short
    // preposition/conjunction/particle that must stay unstressed.
    internal static string ProtectUnstressedWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return WordTokens.Replace(text, match => ProtectWord(match.Value));
    }

    private static string ProtectWord(string word)
    {
        if (word.IndexOf('\u0301') < 0) return word;
        // 1) A combining accent may never sit next to ё — ё is always stressed.
        var builder = new StringBuilder(word.Length);
        for (var i = 0; i < word.Length; i++)
        {
            var ch = word[i];
            if (ch == '\u0301')
            {
                var prev = builder.Length > 0 ? builder[builder.Length - 1] : '\0';
                var next = i + 1 < word.Length ? word[i + 1] : '\0';
                if (char.ToLowerInvariant(prev) == 'ё' || char.ToLowerInvariant(next) == 'ё')
                    continue; // drop the accent that touches ё
            }
            builder.Append(ch);
        }
        var cleaned = builder.ToString();
        // 2) A short function word must stay unstressed.
        if (UnstressedWords.Contains(NormalizeWordLetters(cleaned)))
            cleaned = cleaned.Replace("\u0301", "");
        return cleaned;
    }

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
        // Polysyllabic words the model left unmarked, the original text of every node
        // (kept so the review can be rebuilt after the dictionary pass), and which
        // nodes were accented successfully — failed nodes stay untouched and must not
        // receive a dictionary stress mark or reappear in the review.
        var residueWords = new HashSet<string>(StringComparer.Ordinal);
        var sourceTexts = processableText.Select(node => node.Value).ToArray();
        var reviewable = new bool[processableText.Length];
        if (diagnostics != null)
        {
            diagnostics.TotalUnits = processableText.Length;
            diagnostics.CharacterCount = characterCount;
            diagnostics.InputBytes = byteCount;
        }
        reportProgress?.Invoke(0, processableText.Length);

        // Binary image payloads are excluded from inference regardless of whether
        // they are preserved in the output. Keep their original XML nodes untouched.

        // Send independent text nodes to the worker in batches. The worker accents
        // them concurrently, hiding the per-node ONNX latency that left the CPU and
        // GPU idle in the previous one-request-at-a-time loop.
        const int inferenceBatchSize = 64;
        accentizer.EnsureStarted();
        for (var batchStart = 0; batchStart < processableText.Length; batchStart += inferenceBatchSize)
        {
            control?.Checkpoint();
            var batchCount = Math.Min(inferenceBatchSize, processableText.Length - batchStart);
            var batchInputs = new string[batchCount];
            for (var offset = 0; offset < batchCount; offset++)
                batchInputs[offset] = ApplyUserDictionary(processableText[batchStart + offset].Value);
            var batchOutputs = accentizer.ProcessBatch(batchInputs);
            for (var offset = 0; offset < batchCount; offset++)
            {
                control?.Checkpoint();
                var textNode = processableText[batchStart + offset];
                var sourceText = sourceTexts[batchStart + offset];
                var inference = batchOutputs[offset];
                completedText++;
                if (inference.Error != null)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.FailedUnits++;
                        diagnostics.Events.Add($"Фрагмент {completedText}: {inference.Error}");
                    }
                    LogError($"Фрагмент оставлен без изменений: {TruncateForLog(sourceText)}", new InvalidOperationException(inference.Error));
                }
                else
                {
                    try
                    {
                        var modelOutput = RuAccentMarker.Replace(inference.Text ?? sourceText, "\u0301");
                        var modelProcessed = ProtectUnstressedWords(EnsureDialogueInitialWordAccent(sourceText, modelOutput, accentizer));
                        var modelProcessedWithPunctuation = EnsureOriginalPunctuation(sourceText, modelProcessed);
                        var accented = ApplyYoDictionary(modelProcessedWithPunctuation);
                        var reviewCountBefore = wordReview.Count;
                        foreach (var word in CollectUnrecognizedWords(sourceText, accented, wordReview))
                            residueWords.Add(DictionaryKey(word));
                        if (diagnostics != null) diagnostics.UnrecognizedWordCount += CountReviewWords(wordReview.Skip(reviewCountBefore));
                        reviewable[batchStart + offset] = true;
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
                            diagnostics.Events.Add($"Фрагмент {completedText}: {ex}");
                        }
                        // Keep the original fragment untouched so a single problem fragment
                        // never aborts the whole conversion.
                        LogError($"Фрагмент оставлен без изменений: {TruncateForLog(sourceText)}", ex);
                    }
                }
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
        }

        // The model leaves some polysyllabic words unmarked; RUAccent's bundled full
        // stress dictionary usually knows them. Resolve once for the whole file, add
        // the marks it knows, then rebuild the review so only truly unknown words
        // remain for the manual pass (see ConvertTextFile).
        var dictionaryStress = accentizer.ResolveWords(residueWords.ToArray());
        if (dictionaryStress.Count > 0)
        {
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    processableText[index].Value = ApplyDictionaryStress(processableText[index].Value, dictionaryStress);
            wordReview.Clear();
            for (var index = 0; index < processableText.Length; index++)
                if (reviewable[index])
                    CollectUnrecognizedWords(sourceTexts[index], processableText[index].Value, wordReview);
            changed = 0;
            for (var index = 0; index < processableText.Length; index++)
                if (!string.Equals(processableText[index].Value, sourceTexts[index], StringComparison.Ordinal)) changed++;
            if (diagnostics != null) diagnostics.DictionaryStressCount = dictionaryStress.Count;
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
            WriteWordReview(outputPath, wordReview);
            conversionTimer.Stop();
            LogConversion(inputPath, byteCount, characterCount, conversionTimer.Elapsed, TimeSpan.Zero, "Завершено");
            Console.WriteLine($"  Узлов текста с изменениями: {changed}; исправлено пробелов на границах: {normalized}; подставлено из словаря: {dictionaryStress.Count}");
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
        Console.WriteLine("\nПроверка остатка слов моделью ИИ (без интерфейса):");
        Console.WriteLine("  DSR.exe --llm-stress \"<книга>_ударения_проверка-слов.txt\" [--llm-provider local|ollama|openai]");
        Console.WriteLine("      [--llm-model <модель>] [--llm-url <адрес>] [--llm-key <ключ>]");
        Console.WriteLine("      Подбирает ударения для слов, которых нет в словаре, и сохраняет подсказки");
        Console.WriteLine("      в кэш. Ничего не применяется автоматически. Слова, по которым ответа не");
        Console.WriteLine("      было, повторно не отправляются (список: ресурсы\\llm-без-ответа.json).");
        Console.WriteLine("      --llm-provider local — своя модель из ресурсы\\модели\\llm (по умолчанию),");
        Console.WriteLine("      ollama — свой сервер Ollama, openai — внешний сервис с OpenAI-совместимым API.");
        Console.WriteLine("      --llm-model: для local имя папки или полный путь, для сервисов — имя модели.");
        Console.WriteLine("      Ключ внешнего сервиса можно задать переменной окружения DSR_LLM_API_KEY.\n");
        Console.WriteLine("Тест движка на контрольных словах (скорость и точность, с рекомендацией):");
        Console.WriteLine("  DSR.exe --llm-benchmark [--llm-provider ... --llm-model ... --llm-url ... --llm-key ...]\n");
        Console.WriteLine("Список моделей ИИ, лежащих рядом с программой:");
        Console.WriteLine("  DSR.exe --llm-models");
        Console.WriteLine("Список моделей у внешнего сервиса (например, загруженных в Ollama):");
        Console.WriteLine("  DSR.exe --llm-remote-models --llm-provider ollama [--llm-url http://127.0.0.1:11434]");
        Console.WriteLine("\nОкно проверки слов отдельно (когда книга уже сконвертирована):");
        Console.WriteLine("  DSR.exe --review \"<книга>_ударения_проверка-слов.txt\" [\"<книга>_ударения.fb2\"]");
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

        // Trims leading control characters (notably a stray NUL emitted by onnxruntime on the
        // shared stderr handle) so protocol prefixes are still recognised, while keeping the
        // rest of the line — including meaningful Cyrillic text — untouched.
        private static string TrimProtocolPrefix(string line)
        {
            var start = 0;
            while (start < line.Length && (line[start] == '\0' || line[start] == '\uFEFF' || line[start] == '\u200B'))
                start++;
            return start == 0 ? line : line[start..];
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
                // onnxruntime writes to the shared stderr fd directly and can leave a stray
                // NUL/control char in front of the first line our worker prints afterwards
                // (observed: "\0DSR_PERF:omograph_model:…"). Without this trim the line was
                // silently dropped from the report, making it look like the omograph model
                // never loaded and hiding real model switches.
                var data = TrimProtocolPrefix(e.Data);
                if (data.StartsWith("DSR_PROGRESS:", StringComparison.Ordinal))
                    _report?.Invoke(data["DSR_PROGRESS:".Length..]);
                else if (data.StartsWith("DSR_PERF:", StringComparison.Ordinal))
                {
                    _diagnostics?.PerformanceEvents.Add(data["DSR_PERF:".Length..]);
                    _report?.Invoke(data);
                }
                else if (data.StartsWith("DSR_INFERENCE:", StringComparison.Ordinal))
                {
                    if (double.TryParse(data.AsSpan("DSR_INFERENCE:".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        _workerInferenceTime += TimeSpan.FromSeconds(seconds);
                        _workerCallCount++;
                        if (_diagnostics != null)
                        {
                            _diagnostics.InferenceElapsed = _workerInferenceTime;
                            _diagnostics.InferenceCalls = _workerCallCount;
                        }
                    }
                    _report?.Invoke(data);
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

        // Sends a batch of independent text nodes in one request. The worker
        // processes them concurrently, which overlaps the many tiny ONNX
        // sessions that made the sequential request/response loop latency-bound.
        // Results are returned in input order; a failed node carries its error.
        public (string? Text, string? Error)[] ProcessBatch(IReadOnlyList<string> texts)
        {
            if (texts.Count == 0) return Array.Empty<(string?, string?)>();
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("ruaccent не запущен.");
                var json = JsonSerializer.Serialize(texts);
                process.StandardInput.WriteLine(json);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс ruaccent завершился без ответа.");
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("ruaccent вернул неожиданный ответ на пакет.");
                var results = new (string? Text, string? Error)[texts.Count];
                var index = 0;
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (index >= results.Length) break;
                    if (element.TryGetProperty("error", out var error))
                        results[index] = (null, error.GetString() ?? "Ошибка ruaccent");
                    else if (element.TryGetProperty("text", out var value))
                        results[index] = (value.GetString(), null);
                    index++;
                }
                if (index != texts.Count)
                    throw new InvalidOperationException("ruaccent вернул неполный ответ на пакет.");
                return results;
            }
        }

        // Asks the worker to look words up in RUAccent's bundled full stress
        // dictionary (accents.json.gz). The worker streams the file on first use and
        // caches the answers, so repeated conversions are cheap and its memory stays
        // proportional to the requested words, never to the 3.2M-entry dictionary.
        public IReadOnlyDictionary<string, string> ResolveWords(IReadOnlyList<string> words)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (words.Count == 0) return map;
            lock (_sync)
            {
                var process = _process ?? throw new InvalidOperationException("ruaccent не запущен.");
                var json = JsonSerializer.Serialize(new { op = "resolve", words });
                process.StandardInput.WriteLine(json);
                process.StandardInput.Flush();
                var response = process.StandardOutput.ReadLine();
                if (response == null)
                    throw new InvalidOperationException("Процесс ruaccent завершился без ответа.");
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetString() ?? "Ошибка ruaccent");
                if (document.RootElement.TryGetProperty("resolved", out var resolved) && resolved.ValueKind == JsonValueKind.Object)
                    foreach (var property in resolved.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.String)
                            map[property.Name] = property.Value.GetString()!;
                return map;
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
