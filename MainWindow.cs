using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FB2AccentConverter;

public sealed partial class MainWindow : Window
{
    private TextBox _input => InputTextBox;
    private TextBox _output => OutputTextBox;
    private Button _prepareButton => PrepareButton;
    private bool _prepared;
    private CheckBox _illustrations => IllustrationsCheckBox;
    private ComboBox _model => ModelComboBox;
    private ComboBox _device => DeviceComboBox;
    private Button _gpuTest => GpuTestButton;
    private TextBlock _gpuStatus => GpuStatusText;
    private Button _convert => ConvertButton;
    private Button _pauseConversion => PauseConversionButton;
    private Button _stopConversion => StopConversionButton;
    private Button _reviewWords => ReviewWordsButton;
    private Program.ConversionControl? _conversionControl;
    private string? _diagnosticsPath;
    private Button _clearCacheButton => ClearCacheButton;
    private TextBlock _status => StatusText;
    private TextBlock _conversionStats => ConversionStatsText;
    private TextBlock _modelStatus => ModelStatusText;
    private TextBlock _resourceSizeStatus => ResourceSizeStatusText;
    private ProgressBar _progress => ConversionProgressBar;
    private ProgressBar _cpuUsage => CpuUsageBar;
    private ProgressBar _memoryUsage => MemoryUsageBar;
    private ProgressBar _gpuUsage => GpuUsageBar;
    private TextBlock _cpuLabel => CpuLabel;
    private TextBlock _memoryLabel => MemoryLabel;
    private TextBlock _gpuLabel => GpuLabel;
    private ProgressBar _networkUsage => NetworkUsageBar;
    private TextBlock _networkLabel => NetworkLabel;
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _hasCpuSample;
    private bool _resourceSampleRunning;
    private DateTime _previousNetworkSampleTime;
    private long _previousNetworkBytes;
    private bool _hasNetworkSample;
    private DateTime _previousCpuSampleTime;
    private double _previousProcessCpuMs;
    private DateTime _activityStartedAt = DateTime.MinValue;
    private string _activityStage = "Ожидание";
    private bool _activityWasDownloading;
    private string? _loadedFile;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var model in new[] { "turbo3.1", "turbo3", "turbo2", "big_poetry", "tiny" }) _model.Items.Add(model);
        _model.SelectedIndex = 0;
        _model.SelectionChanged += (_, _) => UpdateSelectedModelStatus();
        _device.Items.Add("CPU");
        _device.SelectedIndex = 0;
        BrowseInputButton.Click += (_, _) => SelectInput();
        BrowseOutputButton.Click += (_, _) => SelectOutput();
        _clearCacheButton.Click += (_, _) => ClearCache();
        var canWrite = CanWriteToApplicationDirectory();
        _clearCacheButton.IsEnabled = canWrite;
        _prepareButton.IsEnabled = canWrite;
        _convert.IsEnabled = canWrite;
        UpdateSelectedModelStatus();
        _status.Text = canWrite
            ? "Нажмите «Подготовка к работе»: DSR настроит Python, загрузит и проверит модели."
            : "Папка приложения недоступна для записи. Переместите DSR.exe в папку с правами записи (например, не в Program Files).";
        _convert.Click += async (_, _) => await ConvertAsync();
        _pauseConversion.Click += (_, _) => ToggleConversionPause();
        _stopConversion.Click += (_, _) => StopConversion();
        _reviewWords.Click += (_, _) => OpenWordReview();
        UpdateReviewButton();
        _prepareButton.Click += async (_, _) => await PrepareAsync();
        _gpuTest.Click += async (_, _) => await CheckGpuAsync();
        _resourceTimer.Tick += async (_, _) => await UpdateResourceUsageAsync();
        _resourceTimer.Start();
        Closed += (_, _) => _resourceTimer.Stop();
        var lastSizeUpdate = DateTime.MinValue;
        _resourceTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - lastSizeUpdate < TimeSpan.FromSeconds(30)) return;
            lastSizeUpdate = DateTime.UtcNow;
            _ = UpdateResourceSizeStatusAsync();
        };
        _ = UpdateResourceUsageAsync();
    }

    private void UpdateSelectedModelStatus()
    {
        var model = _model.SelectedItem?.ToString() ?? "turbo3.1";
        var path = Path.Combine(Program.DataDirectory, "python", "Lib", "site-packages", "ruaccent", "nn", "nn_omograph", model);
        long bytes = 0;
        try
        {
            if (Directory.Exists(path))
                bytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        }
        catch (IOException) { }
        _modelStatus.Text = bytes > 0
            ? $"Выбрана модель омографов: {model}; её файлы (ONNX и токенизатор) занимают {bytes / 1024.0 / 1024:0.0} МБ."
            : $"Выбрана модель омографов: {model}; файлы появятся при загрузке.";
        _ = UpdateResourceSizeStatusAsync();
    }

    private async Task UpdateResourceSizeStatusAsync()
    {
        var text = await Task.Run(() =>
        {
            try
            {
                var root = Program.DataDirectory;
                if (!Directory.Exists(root)) return $"Ресурсы будут размещены: {root}";
                var areas = new[] { ("Python", Path.Combine(root, "python")),
                    ("Модели и кэш", Path.Combine(root, "модели")), ("Кэш загрузок", Path.Combine(root, "кэш")) };
                var totals = areas.Select(area => (area.Item1, Size: Directory.Exists(area.Item2)
                    ? Directory.EnumerateFiles(area.Item2, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                    : 0L)).ToArray();
                return $"Ресурсы: {root} · всего {FormatSize(totals.Sum(item => item.Size))}\n"
                    + string.Join(" · ", totals.Select(item => $"{item.Item1}: {FormatSize(item.Size)}"));
            }
            catch (Exception ex) { return $"Не удалось посчитать размер ресурсов: {ex.Message}"; }
        });
        if (IsLoaded) _resourceSizeStatus.Text = text;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private async Task UpdateResourceUsageAsync()
    {
        if (_resourceSampleRunning) return;
        _resourceSampleRunning = true;
        try
        {
            var sample = await Task.Run(ReadResourceUsage);
            // Дерево процессов: окно DSR почти не тратит CPU — всю работу выполняет
            // дочерний python.exe (воркер), поэтому без него индикатор показывал ~0%.
            var (processCpu, processWorkingSet) = Program.CurrentProcessTreeUsage();
            try
            {
                var nowCpu = DateTime.UtcNow;
                if (_hasCpuSample)
                {
                    var elapsedMilliseconds = Math.Max(1, (nowCpu - _previousCpuSampleTime).TotalMilliseconds);
                    var cpuPercent = Math.Clamp((processCpu - _previousProcessCpuMs) / elapsedMilliseconds * 100.0 / Math.Max(1, Environment.ProcessorCount), 0, 100);
                    _cpuUsage.Value = cpuPercent;
                    _cpuLabel.Text = $"CPU программы: {cpuPercent:0}%";
                }
                _previousProcessCpuMs = processCpu;
                _previousCpuSampleTime = nowCpu;
                _hasCpuSample = true;
            }
            catch (InvalidOperationException) { }
            var processMemoryMb = processWorkingSet / 1024.0 / 1024;
            var processMemoryPercent = sample.MemoryTotalGb > 0 ? processMemoryMb / 1024 / sample.MemoryTotalGb * 100 : 0;
            _memoryUsage.Value = Math.Clamp(processMemoryPercent, 0, 100);
            _memoryLabel.Text = $"ОЗУ программы: {processMemoryMb:0} МБ ({processMemoryPercent:0.0}% общей)";
            _gpuUsage.Value = sample.GpuHasUtilization ? sample.GpuPercent : sample.GpuMemoryPercent;
            _gpuLabel.Text = sample.GpuAvailable
                ? $"GPU: {(sample.GpuHasUtilization ? $"{sample.GpuPercent:0}% (всего на карте)" : "нагрузка н/д")} · VRAM {sample.GpuMemoryUsedMb:N0}/{sample.GpuMemoryTotalMb:N0} МБ"
                : "GPU: NVIDIA недоступна";
            var now = DateTime.UtcNow;
            var elapsed = _hasNetworkSample ? (now - _previousNetworkSampleTime).TotalSeconds : 0;
            var receiveRate = elapsed > 0 ? (sample.NetworkBytes - _previousNetworkBytes) / elapsed : 0;
            receiveRate = Math.Max(0, receiveRate);
            _networkUsage.Value = Math.Clamp(receiveRate / (1024 * 1024) * 10, 0, 100);
            _networkLabel.Text = receiveRate > 1024
                ? $"Сеть: ↓ {FormatSize((long)receiveRate)}/с"
                : "Сеть: ожидание";
            _previousNetworkBytes = sample.NetworkBytes;
            _previousNetworkSampleTime = now;
            _hasNetworkSample = true;
            UpdateActivityElapsed();
        }
        catch (Exception)
        {
            _gpuLabel.Text = "GPU: данные недоступны";
            _cpuLabel.Text = "CPU программы: данные недоступны";
            _memoryLabel.Text = "ОЗУ программы: данные недоступны";
            _networkLabel.Text = "Сеть: данные недоступны";
        }
        finally { _resourceSampleRunning = false; }
    }

    private static ResourceUsage ReadResourceUsage()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new InvalidOperationException("Не удалось получить использование ОЗУ.");
        var gpu = ReadGpuSample();

        var totalMemory = memory.TotalPhysical;
        var usedMemory = totalMemory - memory.AvailablePhysical;
        long networkBytes;
        try
        {
            networkBytes = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(adapter =>
                {
                    try
                    {
                        var stats = adapter.GetIPv4Statistics();
                        return stats.BytesReceived + stats.BytesSent;
                    }
                    catch (NetworkInformationException) { return 0L; }
                    catch (InvalidOperationException) { return 0L; }
                })
                .Sum();
        }
        catch (NetworkInformationException) { networkBytes = 0; }
        return new ResourceUsage(totalMemory == 0 ? 0 : usedMemory * 100.0 / totalMemory,
            usedMemory / 1024.0 / 1024 / 1024, totalMemory / 1024.0 / 1024 / 1024,
            gpu.Available ? Math.Clamp(gpu.Percent, 0, 100) : 0, gpu.MemoryUsedMb, gpu.MemoryTotalMb,
            gpu.MemoryTotalMb > 0 ? Math.Clamp(gpu.MemoryUsedMb * 100.0 / gpu.MemoryTotalMb, 0, 100) : 0,
            gpu.HasUtilization, gpu.Available, networkBytes);
    }

    private readonly record struct GpuSample(bool Available, bool HasUtilization, double Percent, double MemoryUsedMb, double MemoryTotalMb);

    private static readonly TimeSpan GpuSampleInterval = TimeSpan.FromSeconds(5);
    private static readonly object GpuSampleLock = new();
    private static DateTime _gpuSampledAt = DateTime.MinValue;
    private static GpuSample _gpuSample = new(false, false, 0, 0, 0);

    // Spawning nvidia-smi is comparatively expensive, so its reading is cached: the
    // one-second UI timer refreshes CPU/RAM/network on every tick but reuses the last
    // GPU value until GpuSampleInterval elapses.
    private static GpuSample ReadGpuSample()
    {
        lock (GpuSampleLock)
        {
            if (DateTime.UtcNow - _gpuSampledAt < GpuSampleInterval) return _gpuSample;
            _gpuSample = QueryGpuSample();
            _gpuSampledAt = DateTime.UtcNow;
            return _gpuSample;
        }
    }

    private static GpuSample QueryGpuSample()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--query-gpu=utilization.gpu,memory.used,memory.total");
            start.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(start);
            if (process != null)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                if (process.WaitForExit(800))
                {
                    var values = outputTask.GetAwaiter().GetResult().Split(',', StringSplitOptions.TrimEntries);
                    if (process.ExitCode == 0 && values.Length >= 3)
                    {
                        var hasUtilization = double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent);
                        var hasMemoryUsed = double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var memoryUsed);
                        var hasMemoryTotal = double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var memoryTotal);
                        var available = hasUtilization || (hasMemoryUsed && hasMemoryTotal && memoryTotal > 0);
                        return new GpuSample(available, hasUtilization, percent, memoryUsed, memoryTotal);
                    }
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { }
        return new GpuSample(false, false, 0, 0, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed record ResourceUsage(double MemoryPercent, double MemoryUsedGb, double MemoryTotalGb,
        double GpuPercent, double GpuMemoryUsedMb, double GpuMemoryTotalMb, double GpuMemoryPercent,
        bool GpuHasUtilization, bool GpuAvailable, long NetworkBytes);

    private void StartActivity(string stage)
    {
        _activityStartedAt = DateTime.UtcNow;
        _activityStage = stage;
        _activityWasDownloading = false;
        ConversionStatsText.Text = $"Этап: {stage} · загрузка сети отслеживается выше";
    }

    private void SetActivityStage(string stage)
    {
        _activityStage = stage;
        UpdateActivityElapsed();
    }

    private void SetActivityDownloading(bool downloading)
    {
        _activityWasDownloading = downloading;
        UpdateActivityElapsed();
    }

    private void UpdateActivityElapsed()
    {
        if (_activityStartedAt == DateTime.MinValue) return;
        var elapsed = FormatElapsed(DateTime.UtcNow - _activityStartedAt);
        var downloadState = _activityWasDownloading ? " · скачивание/загрузка модели" : "";
        ConversionStatsText.Text = $"Этап: {_activityStage}{downloadState} · идёт {elapsed}";
    }

    private void StopActivity(string result)
    {
        if (_activityStartedAt == DateTime.MinValue) return;
        var elapsed = FormatElapsed(DateTime.UtcNow - _activityStartedAt);
        ConversionStatsText.Text = $"Этап: {result} · заняло {elapsed}";
        _activityStartedAt = DateTime.MinValue;
        _activityWasDownloading = false;
    }

    private void SelectInput()
    {
        var dialog = new OpenFileDialog { Filter = "Книги и тексты (*.fb2;*.txt)|*.fb2;*.txt|FictionBook (*.fb2)|*.fb2|Текстовые файлы (*.txt)|*.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _loadedFile = dialog.FileName;
        _input.Text = _loadedFile;
        var extension = Path.GetExtension(_loadedFile);
        if (!extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Поддерживаются только файлы FB2 и TXT.", "Неподдерживаемый формат", MessageBoxButton.OK, MessageBoxImage.Warning);
            _loadedFile = null;
            _input.Clear();
            return;
        }
        _output.Text = Path.Combine(Path.GetDirectoryName(_loadedFile)!, Path.GetFileNameWithoutExtension(_loadedFile) + "_ударения" + extension);
        UpdateReviewButton();
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "TXT открыт. Строки и структура файла будут сохранены.";
            _illustrations.IsEnabled = false;
            return;
        }
        _status.Text = "FB2-файл выбран. Можно запускать обработку всей книги.";
        _illustrations.IsEnabled = true;
    }

    private void SelectOutput()
    {
        var extension = Path.GetExtension(_loadedFile ?? ".fb2");
        var filter = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ? "Текстовый файл (*.txt)|*.txt" : "Книга FictionBook (*.fb2)|*.fb2";
        var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, AddExtension = true, FileName = Path.GetFileName(_output.Text) };
        if (!string.IsNullOrWhiteSpace(_output.Text)) dialog.InitialDirectory = Path.GetDirectoryName(_output.Text);
        if (dialog.ShowDialog(this) == true)
        {
            _output.Text = dialog.FileName;
            UpdateReviewButton();
        }
    }

    // Гарантирует, что окружение Python готово до начала конвертации, и показывает
    // прогресс первого запуска, пока окружение и модель скачиваются.

    private async Task CheckGpuAsync()
    {
        _gpuTest.IsEnabled = false;
        _gpuStatus.Text = "Готовлю Python-компоненты и проверяю NVIDIA RTX. Первый запуск скачивает зависимости из интернета…";
        try
        {
            var result = await Task.Run(Program.ProbeGpu);
            if (result.Supported)
            {
                if (!_device.Items.Contains("CUDA")) _device.Items.Add("CUDA");
                _device.SelectedItem = "CUDA";
                _gpuStatus.Text = result.Message + " Для обработки выбрана CUDA.";
                _gpuStatus.Foreground = Brushes.DarkGreen;
            }
            else
            {
                _device.SelectedItem = "CPU";
                if (_device.Items.Contains("CUDA")) _device.Items.Remove("CUDA");
                if (result.CanInstallGpuRuntime)
                {
                    var answer = MessageBox.Show(this,
                        result.Message + "\n\nУстановить CUDA-сборку ONNX Runtime и необходимые CUDA/cuDNN runtime-библиотеки в локальную папку DSR? После установки проверка GPU запустится повторно.",
                        "Установить поддержку NVIDIA GPU?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes)
                    {
                        StartActivity("Установка CUDA Runtime");
                        Program.SetupProgress = message => Dispatcher.Invoke(() =>
                        {
                            _gpuStatus.Text = message;
                            SetActivityStage(message);
                            SetActivityDownloading(message.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                                || message.Contains("Retrying", StringComparison.OrdinalIgnoreCase));
                        });
                        SetBusy(true);
                        try
                        {
                            _gpuStatus.Text = "Удаляю CPU-пакет и устанавливаю CUDA/cuDNN + ONNX Runtime. Загрузка может быть большой и занять несколько минут…";
                            await Task.Run(Program.InstallGpuRuntime);
                            _gpuStatus.Text = "GPU-компоненты установлены. Повторная проверка…";
                            var retry = await Task.Run(Program.ProbeGpu);
                            if (retry.Supported)
                            {
                                if (!_device.Items.Contains("CUDA")) _device.Items.Add("CUDA");
                                _device.SelectedItem = "CUDA";
                                _gpuStatus.Text = retry.Message + " Для обработки выбрана CUDA.";
                                _gpuStatus.Foreground = Brushes.DarkGreen;
                            }
                            else
                            {
                                _gpuStatus.Text = retry.Message + " Будет использован CPU.";
                                _gpuStatus.Foreground = Brushes.DarkOrange;
                            }
                        }
                        finally
                        {
                            Program.SetupProgress = null;
                            if (_activityStartedAt != DateTime.MinValue) StopActivity("Установка завершена");
                            SetBusy(false);
                        }
                    }
                    else
                    {
                        _gpuStatus.Text = result.Message + " Будет использован CPU. Можно установить GPU позже кнопкой «Проверить GPU».";
                        _gpuStatus.Foreground = Brushes.DarkOrange;
                    }
                }
                else
                {
                    _gpuStatus.Text = result.Message + " Будет использован CPU.";
                    _gpuStatus.Foreground = Brushes.DarkOrange;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError("Ошибка проверки/установки GPU", ex);
            _device.SelectedItem = "CPU";
            _gpuStatus.Text = "Не удалось проверить или установить GPU: " + ex.GetBaseException().Message + " Будет использован CPU.";
            _gpuStatus.Foreground = Brushes.DarkOrange;
        }
        finally { _gpuTest.IsEnabled = true; }
    }

    private async Task ConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(_loadedFile) || !File.Exists(_loadedFile)) { _status.Text = "Сначала выберите существующий FB2- или TXT-файл."; return; }
        if (string.IsNullOrWhiteSpace(_output.Text)) { _status.Text = "Укажите путь сохранения результата."; return; }
        if (string.Equals(Path.GetFullPath(_loadedFile), Path.GetFullPath(_output.Text), StringComparison.OrdinalIgnoreCase)) { _status.Text = "Результат не должен совпадать с исходным файлом."; return; }
        var sourceExtension = Path.GetExtension(_loadedFile);
        if (!sourceExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !sourceExtension.Equals(".fb2", StringComparison.OrdinalIgnoreCase)) { _status.Text = "Поддерживаются только форматы FB2 и TXT."; return; }
        var expectedExtension = sourceExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ? ".txt" : ".fb2";
        if (!Path.GetExtension(_output.Text).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase)) { _status.Text = $"Для входного {expectedExtension.ToUpperInvariant()} укажите выходной файл с расширением {expectedExtension}."; return; }
        if (!_prepared || !Directory.Exists(Program.DataDirectory))
        {
            _status.Text = "Сначала нажмите «Подготовка к работе» и дождитесь сообщения о готовности.";
            return;
        }
        var input = _loadedFile!;
        var output = _output.Text;
        var model = _model.SelectedItem?.ToString() ?? "turbo3.1";
        var device = _device.SelectedItem?.ToString() ?? "CPU";
        var includeImages = _illustrations.IsChecked == true;
        StartActivity("Подготовка конвертации");
        SetBusy(true);
        _conversionControl = new Program.ConversionControl();
        _pauseConversion.IsEnabled = true;
        _stopConversion.IsEnabled = true;
        _pauseConversion.Content = "Пауза";
        _diagnosticsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + "_статистика.txt");
        var conversionWatch = Stopwatch.StartNew();
        var totalUnits = 0;
        var sourceBytes = new FileInfo(input).Length;
        var sourceCharacters = new FileInfo(input).Length;
        if (Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            try { sourceCharacters = File.ReadAllText(input).Length; } catch (IOException) { }
        }
        _conversionStats.Text = $"Файл: {sourceBytes / 1024d / 1024d:0.00} МБ · около {sourceCharacters:N0} символов · прошло 00:00:00 · осталось —";
        _modelStatus.Text = $"Загружается модель омографов {model}; размер будет показан во время скачивания…";
        _status.Text = $"Готовлю Python-компоненты; модель омографов: {model} ({device}).";
        _progress.Value = 0;
        try
        {
            await Task.Run(() => Program.ConvertInteractive(input, output, model, device, includeImages,
                message => Dispatcher.Invoke(() =>
                {
                    if (message.StartsWith("DIAGNOSTICS:", StringComparison.Ordinal))
                    {
                        _diagnosticsPath = Path.Combine(Path.GetDirectoryName(message["DIAGNOSTICS:".Length..])!, Path.GetFileNameWithoutExtension(message["DIAGNOSTICS:".Length..]) + "_статистика.txt");
                        return;
                    }
                    if (message.StartsWith("DSR_PERF:", StringComparison.Ordinal))
                    {
                        _conversionStats.Text = message;
                        return;
                    }
                    var isModelLoading = message.StartsWith("Загрузка модели ", StringComparison.Ordinal);
                    var isModelReady = message.StartsWith("Подготовка модели ", StringComparison.Ordinal)
                        || message.StartsWith("Модель ", StringComparison.Ordinal);
                    if (isModelLoading || isModelReady)
                    {
                        SetActivityDownloading(isModelLoading);
                        _modelStatus.Text = message;
                        SetActivityStage(message);
                        var percentStart = message.LastIndexOf(')');
                        var percentEnd = message.LastIndexOf('%');
                        if (isModelLoading && percentStart >= 0 && percentEnd > percentStart
                            && double.TryParse(message.AsSpan(percentStart + 1, percentEnd - percentStart - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var downloadedPercent))
                        {
                            _progress.IsIndeterminate = false;
                            _progress.Value = Math.Clamp(downloadedPercent, 0, 100);
                        }
                        else if (isModelReady && message.Contains("загружена", StringComparison.Ordinal))
                        {
                            _progress.IsIndeterminate = false;
                            _progress.Value = 100;
                        }
                    }
                    else
                    {
                        _status.Text = message;
                        SetActivityStage(message);
                        if (message.StartsWith("Готово:", StringComparison.Ordinal))
                        {
                            _progress.IsIndeterminate = false;
                            _progress.Value = 100;
                        }
                    }
                }),
                (completed, total) => Dispatcher.Invoke(() =>
                {
                    totalUnits = total;
                    _progress.IsIndeterminate = false;
                    _progress.Value = total == 0 ? 100 : completed * 100.0 / total;
                    var remaining = completed > 0 ? TimeSpan.FromSeconds(conversionWatch.Elapsed.TotalSeconds * Math.Max(0, total - completed) / completed) : (TimeSpan?)null;
                    _status.Text = $"Обработка текста: {completed:N0} из {total:N0} фрагментов ({_progress.Value:0}%).";
                    _conversionStats.Text = $"Файл: {sourceBytes / 1024d / 1024d:0.00} МБ · около {sourceCharacters:N0} символов · прошло {FormatElapsed(conversionWatch.Elapsed)} · осталось {(remaining.HasValue ? FormatElapsed(remaining.Value) : "расчёт…")}";
                }), _conversionControl!));
            conversionWatch.Stop();
            _conversionStats.Text = $"Файл: {sourceBytes / 1024d / 1024d:0.00} МБ · около {sourceCharacters:N0} символов · обработано {totalUnits:N0} фрагментов за {FormatElapsed(conversionWatch.Elapsed)} · отчёт слов рядом с исходником";
            _status.Text = "Конвертация завершена: " + output;
            StopActivity("Конвертация завершена");
            UpdateReviewButton();
            var outputExtension = Path.GetExtension(output);
            if ((outputExtension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) || outputExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) && _reviewWords.IsEnabled)
            {
                var reviewPath = Program.ReviewPathFor(output);
                new WordReviewWindow(reviewPath, output) { Owner = this }.ShowDialog();
                UpdateReviewButton();
            }
            _ = UpdateResourceSizeStatusAsync();
            if (MessageBox.Show(this, "Книга готова. Открыть папку с результатом?", "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            conversionWatch.Stop();
            StopActivity("Обработка остановлена");
            _status.Text = "Обработка остановлена. Отчёт диагностики сохранён рядом с выходным файлом.";
            _conversionStats.Text = $"Остановлено через {FormatElapsed(conversionWatch.Elapsed)} · отчёт: {_diagnosticsPath}";
        }
        catch (Exception ex)
        {
            StopActivity("Ошибка конвертации");
            Program.LogError($"Ошибка конвертации файла: {input} -> {output}", ex);
            _status.Text = "Ошибка: " + ex.GetBaseException().Message + $"\nПодробности: {Program.LogFilePath}\nОтчёт: {_diagnosticsPath}";
        }
        finally
        {
            if (_activityStartedAt != DateTime.MinValue) StopActivity("Обработка завершена");
            _conversionControl = null;
            _pauseConversion.IsEnabled = false;
            _stopConversion.IsEnabled = false;
            _pauseConversion.Content = "Пауза";
            SetBusy(false);
        }
    }

    private void ToggleConversionPause()
    {
        if (_conversionControl == null) return;
        if (_conversionControl.IsPaused)
        {
            _conversionControl.Resume();
            _pauseConversion.Content = "Пауза";
            _status.Text = "Обработка продолжается…";
        }
        else
        {
            _conversionControl.Pause();
            _pauseConversion.Content = "Продолжить";
            _status.Text = "Обработка приостановлена. Нажмите «Продолжить» или «Стоп».";
        }
    }

    private void UpdateReviewButton()
    {
        var outputPath = _output.Text;
        var extension = string.IsNullOrWhiteSpace(outputPath) ? "" : Path.GetExtension(outputPath);
        var hasOutput = !string.IsNullOrWhiteSpace(outputPath) &&
            (extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase));
        var reviewPath = hasOutput ? Program.ReviewPathFor(outputPath) : null;
        _reviewWords.Tag = hasOutput ? (reviewPath!, outputPath) : null;
        // Окно ручной правки само позволяет выбрать оба файла, поэтому кнопку
        // можно открыть и до конвертации/наличия отчёта.
        _reviewWords.IsEnabled = true;
    }

    private void OpenWordReview()
    {
        var window = new WordReviewWindow(_reviewWords.Tag is ValueTuple<string, string> paths ? paths.Item1 : null,
            _reviewWords.Tag is ValueTuple<string, string> outputPaths ? outputPaths.Item2 : null) { Owner = this };
        window.ShowDialog();
        UpdateReviewButton();
    }

    private void StopConversion()
    {
        _conversionControl?.Stop();
        _status.Text = "Останавливаю после текущего фрагмента…";
    }

    private async Task PrepareAsync()
    {
        if (_prepared && Directory.Exists(Program.DataDirectory))
        {
            MessageBox.Show(this, "Приложение готово к работе.\n\nДоступно: обработка FB2 с сохранением структуры и изображений, обработка TXT с сохранением строк, настройка CPU/CUDA.", "DSR готов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartActivity("Подготовка Python и моделей");
        SetBusy(true);
        var selectedDevice = _device.SelectedItem?.ToString() ?? "CPU";
        var selectedModel = _model.SelectedItem?.ToString() ?? "turbo3.1";
        _status.Text = "Настраиваю Python и зависимости, загружаю выбранную модель, выполняю тест обработки…";
        try
        {
            Program.SetupProgress = message => Dispatcher.Invoke(() =>
            {
                _status.Text = message;
                SetActivityStage(message);
                SetActivityDownloading(message.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Retrying", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Загрузка", StringComparison.OrdinalIgnoreCase));
            });
            try
            {
                await Task.Run(() => Program.PrepareInteractive(selectedModel, selectedDevice,
                    message => Dispatcher.Invoke(() =>
                    {
                        _status.Text = message;
                        SetActivityStage(message);
                        SetActivityDownloading(message.Contains("скач", StringComparison.OrdinalIgnoreCase)
                            || message.Contains("загружа", StringComparison.OrdinalIgnoreCase));
                    })));
            }
            finally { Program.SetupProgress = null; }
            _ = UpdateResourceSizeStatusAsync();
            Program.GpuProbeResult gpu;
            try { gpu = await Task.Run(Program.ProbeGpu); }
            catch (Exception ex)
            {
                gpu = new Program.GpuProbeResult(false, "Проверка GPU недоступна: " + ex.GetBaseException().Message);
            }
            if (gpu.Supported)
            {
                if (!_device.Items.Contains("CUDA")) _device.Items.Add("CUDA");
                _device.SelectedItem = "CUDA";
                _gpuStatus.Text = gpu.Message + " Для обработки выбрана CUDA.";
                _gpuStatus.Foreground = Brushes.DarkGreen;
            }
            else
            {
                _device.SelectedItem = "CPU";
                _gpuStatus.Text = gpu.Message + " Будет использован CPU.";
                _gpuStatus.Foreground = Brushes.DarkOrange;
            }
            _prepared = true;
            _status.Text = "Приложение готово к работе.";
            UpdateSelectedModelStatus();
            MessageBox.Show(this, "Приложение готово к работе.\n\nДоступно:\n• FB2 → FB2 с сохранением структуры и иллюстраций;\n• TXT → TXT с сохранением строк и форматирования текста;\n• CPU или доступная CUDA-видеокарта;\n• исходный файл не изменяется.", "DSR готов", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (Program.SetupProgress != null) Program.SetupProgress = null;
            StopActivity("Ошибка подготовки");
            _prepared = false;
            var details = $"Модель: {_model.SelectedItem}; устройство: {_device.SelectedItem}; Python: {Path.Combine(Program.DataDirectory, "python", "Scripts", "python.exe")}";
            Program.LogError("Подготовка к работе не завершена", new InvalidOperationException(details, ex));
            _status.Text = "Подготовка не завершена: " + ex.GetBaseException().Message;
            MessageBox.Show(this, _status.Text + $"\n\nПодробный лог: {Program.LogFilePath}", "Ошибка подготовки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_activityStartedAt != DateTime.MinValue) StopActivity("Подготовка завершена");
            SetBusy(false);
            if (!CanWriteToApplicationDirectory())
            {
                _prepareButton.IsEnabled = false;
                _convert.IsEnabled = false;
                _clearCacheButton.IsEnabled = false;
            }
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var totalHours = (long)elapsed.TotalHours;
        return $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        var canWrite = CanWriteToApplicationDirectory();
        _prepareButton.IsEnabled = !busy && canWrite;
        _convert.IsEnabled = !busy && canWrite;
        _clearCacheButton.IsEnabled = !busy && canWrite;
        _gpuTest.IsEnabled = !busy;
        BrowseInputButton.IsEnabled = !busy;
        BrowseOutputButton.IsEnabled = !busy;
        _status.Opacity = busy ? 0.8 : 1;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy;
    }

    private void ClearCache()
    {
        var cache = Program.DataDirectory;
        var answer = MessageBox.Show(this,
            $"Удалить все ресурсы DSR ({cache})? Будут удалены Python, модели и кэш; при следующей подготовке они загрузятся повторно. Приложение и ваши книги не затрагиваются.",
            "Очистка данных программы", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            if (Directory.Exists(cache))
            {
                var normalizedRoot = Path.GetFullPath(Program.ApplicationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedCache = Path.GetFullPath(cache).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(normalizedCache), normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(normalizedCache), "ресурсы", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Путь ресурсов не прошёл проверку безопасности: {normalizedCache}");
                Directory.Delete(normalizedCache, recursive: true);
            }
            _prepared = false;
            _clearCacheButton.IsEnabled = CanWriteToApplicationDirectory();
            _prepareButton.IsEnabled = _clearCacheButton.IsEnabled;
            _convert.IsEnabled = _clearCacheButton.IsEnabled;
            _ = UpdateResourceSizeStatusAsync();
            _status.Text = "Ресурсы удалены. Нажмите «Подготовка к работе», чтобы снова установить компоненты и модели.";
        }
        catch (Exception ex)
        {
            Program.LogError("Не удалось удалить папку ресурсов", ex);
            _status.Text = "Не удалось удалить папку ресурсов: " + ex.Message + $" Лог: {Program.LogFilePath}";
        }
    }

    private static bool CanWriteToApplicationDirectory()
    {
        try
        {
            var directory = Program.DataDirectory;
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

}
