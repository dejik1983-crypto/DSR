using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _previousIdle;
    private long _previousKernel;
    private long _previousUser;
    private bool _hasCpuSample;
    private bool _resourceSampleRunning;
    private string? _loadedFile;

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
        if (!CanWriteToApplicationDirectory())
        {
            _clearCacheButton.IsEnabled = false;
            _prepareButton.IsEnabled = false;
            _convert.IsEnabled = false;
            _status.Text = "Папка приложения недоступна для записи. Переместите DSR.exe в папку с правами записи (например, не в Program Files).";
        }
        UpdateSelectedModelStatus();
        _status.Text = "Нажмите «Подготовка к работе»: DSR настроит Python, загрузит и проверит модели.";
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
        _resourceTimer.Tick += (_, _) => UpdateResourceSizeStatus();
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
        UpdateResourceSizeStatus();
    }

    private void UpdateResourceSizeStatus()
    {
        try
        {
            var root = Program.DataDirectory;
            if (!Directory.Exists(root))
            {
                _resourceSizeStatus.Text = $"Ресурсы будут размещены: {root}";
                return;
            }
            var areas = new[] { ("Python", Path.Combine(root, "python")), ("Модели и кэш", Path.Combine(root, "модели")), ("Кэш загрузок", Path.Combine(root, "кэш")), ("Временные файлы", Path.Combine(root, "временные-файлы")) };
            var totals = areas.Select(area => (area.Item1, Size: Directory.Exists(area.Item2)
                ? Directory.EnumerateFiles(area.Item2, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : 0L)).ToArray();
            var total = totals.Sum(item => item.Size);
            _resourceSizeStatus.Text = $"Ресурсы: {root} · всего {FormatSize(total)}\n" + string.Join(" · ", totals.Select(item => $"{item.Item1}: {FormatSize(item.Size)}"));
        }
        catch (Exception ex)
        {
            _resourceSizeStatus.Text = $"Папка ресурсов: {Program.DataDirectory} (не удалось посчитать размер: {ex.Message})";
        }
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
            var idleDelta = sample.Idle - _previousIdle;
            var kernelDelta = sample.Kernel - _previousKernel;
            var userDelta = sample.User - _previousUser;
            if (_hasCpuSample && kernelDelta + userDelta > 0)
            {
                var cpu = Math.Clamp(100.0 * (kernelDelta + userDelta - idleDelta) / (kernelDelta + userDelta), 0, 100);
                _cpuUsage.Value = cpu;
                _cpuLabel.Text = $"CPU: {cpu:0}%";
            }
            _previousIdle = sample.Idle;
            _previousKernel = sample.Kernel;
            _previousUser = sample.User;
            _hasCpuSample = true;
            _memoryUsage.Value = sample.MemoryPercent;
            _memoryLabel.Text = $"ОЗУ: {sample.MemoryPercent:0}% ({sample.MemoryUsedGb:0.0}/{sample.MemoryTotalGb:0.0} ГБ)";
            _gpuUsage.Value = sample.GpuHasUtilization ? sample.GpuPercent : sample.GpuMemoryPercent;
            _gpuLabel.Text = sample.GpuAvailable
                ? $"GPU: {(sample.GpuHasUtilization ? $"{sample.GpuPercent:0}%" : "нагрузка н/д")} · VRAM {sample.GpuMemoryUsedMb:N0}/{sample.GpuMemoryTotalMb:N0} МБ"
                : "GPU: NVIDIA недоступна";
        }
        catch (Exception)
        {
            _gpuLabel.Text = "GPU: данные недоступны";
        }
        finally { _resourceSampleRunning = false; }
    }

    private static ResourceUsage ReadResourceUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new InvalidOperationException("Не удалось получить загрузку CPU.");
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new InvalidOperationException("Не удалось получить использование ОЗУ.");
        var gpuPercent = 0.0;
        var gpuMemoryUsed = 0.0;
        var gpuMemoryTotal = 0.0;
        var gpuHasUtilization = false;
        var gpuAvailable = false;
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
                        gpuHasUtilization = double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuPercent);
                        var hasMemoryUsed = double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuMemoryUsed);
                        var hasMemoryTotal = double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuMemoryTotal);
                        gpuAvailable = gpuHasUtilization || (hasMemoryUsed && hasMemoryTotal && gpuMemoryTotal > 0);
                    }
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { }

        var totalMemory = memory.TotalPhysical;
        var usedMemory = totalMemory - memory.AvailablePhysical;
        return new ResourceUsage(FileTimeValue(idle), FileTimeValue(kernel), FileTimeValue(user),
            totalMemory == 0 ? 0 : usedMemory * 100.0 / totalMemory,
            usedMemory / 1024.0 / 1024 / 1024, totalMemory / 1024.0 / 1024 / 1024,
            gpuAvailable ? Math.Clamp(gpuPercent, 0, 100) : 0, gpuMemoryUsed, gpuMemoryTotal,
            gpuMemoryTotal > 0 ? Math.Clamp(gpuMemoryUsed * 100.0 / gpuMemoryTotal, 0, 100) : 0,
            gpuHasUtilization, gpuAvailable);
    }

    private static long FileTimeValue(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        ((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

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

    private sealed record ResourceUsage(long Idle, long Kernel, long User, double MemoryPercent,
        double MemoryUsedGb, double MemoryTotalGb, double GpuPercent, double GpuMemoryUsedMb,
        double GpuMemoryTotalMb, double GpuMemoryPercent, bool GpuHasUtilization, bool GpuAvailable);

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
                        finally { SetBusy(false); }
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
                    if (message.StartsWith("Загрузка модели ", StringComparison.Ordinal)
                        || message.StartsWith("Подготовка модели ", StringComparison.Ordinal)
                        || message.StartsWith("Модель ", StringComparison.Ordinal))
                    {
                        _modelStatus.Text = message;
                        var percentStart = message.LastIndexOf(')');
                        var percentEnd = message.LastIndexOf('%');
                        if (message.StartsWith("Загрузка модели ", StringComparison.Ordinal)
                            && percentStart >= 0 && percentEnd > percentStart
                            && double.TryParse(message.AsSpan(percentStart + 1, percentEnd - percentStart - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var downloadedPercent))
                        {
                            _progress.IsIndeterminate = false;
                            _progress.Value = Math.Clamp(downloadedPercent, 0, 100);
                        }
                        else if (message.StartsWith("Модель ", StringComparison.Ordinal) && message.Contains("загружена", StringComparison.Ordinal))
                        {
                            _progress.IsIndeterminate = false;
                            _progress.Value = 100;
                        }
                    }
                    else
                        _status.Text = message;
                    if (message.StartsWith("Модель ", StringComparison.Ordinal) && message.Contains("загружена", StringComparison.Ordinal))
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = 100;
                    }
                    else if (message.StartsWith("Готово:", StringComparison.Ordinal))
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = 100;
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
            UpdateReviewButton();
            if (Path.GetExtension(output).Equals(".fb2", StringComparison.OrdinalIgnoreCase) && _reviewWords.IsEnabled)
            {
                var reviewPath = Program.ReviewPathFor(output);
                new WordReviewWindow(reviewPath, output) { Owner = this }.ShowDialog();
                UpdateReviewButton();
            }
            UpdateResourceSizeStatus();
            if (MessageBox.Show(this, "Книга готова. Открыть папку с результатом?", "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            conversionWatch.Stop();
            _status.Text = "Обработка остановлена. Отчёт диагностики сохранён рядом с выходным файлом.";
            _conversionStats.Text = $"Остановлено через {FormatElapsed(conversionWatch.Elapsed)} · отчёт: {_diagnosticsPath}";
        }
        catch (Exception ex)
        {
            Program.LogError($"Ошибка конвертации файла: {input} -> {output}", ex);
            _status.Text = "Ошибка: " + ex.GetBaseException().Message + $"\nПодробности: {Program.LogFilePath}\nОтчёт: {_diagnosticsPath}";
        }
        finally
        {
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
        var hasOutput = !string.IsNullOrWhiteSpace(outputPath) && Path.GetExtension(outputPath).Equals(".fb2", StringComparison.OrdinalIgnoreCase);
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
        SetBusy(true);
        _status.Text = "Настраиваю Python и зависимости, загружаю выбранную модель, выполняю тест обработки…";
        try
        {
            var selectedDevice = _device.SelectedItem?.ToString() ?? "CPU";
            var selectedModel = _model.SelectedItem?.ToString() ?? "turbo3.1";
            await Task.Run(() => Program.PrepareInteractive(selectedModel, selectedDevice,
                message => Dispatcher.Invoke(() => _status.Text = message)));
            _prepared = true;
            UpdateResourceSizeStatus();
            var gpu = await Task.Run(Program.ProbeGpu);
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
            _status.Text = "Приложение готово к работе.";
            UpdateSelectedModelStatus();
            MessageBox.Show(this, "Приложение готово к работе.\n\nДоступно:\n• FB2 → FB2 с сохранением структуры и иллюстраций;\n• TXT → TXT с сохранением строк и форматирования текста;\n• CPU или доступная CUDA-видеокарта;\n• исходный файл не изменяется.", "DSR готов", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _prepared = false;
            var details = $"Модель: {_model.SelectedItem}; устройство: {_device.SelectedItem}; Python: {Path.Combine(Program.DataDirectory, "python", "Scripts", "python.exe")}";
            Program.LogError("Подготовка к работе не завершена", new InvalidOperationException(details, ex));
            _status.Text = "Подготовка не завершена: " + ex.GetBaseException().Message;
            MessageBox.Show(this, _status.Text + $"\n\nПодробный лог: {Program.LogFilePath}", "Ошибка подготовки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
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
        _prepareButton.IsEnabled = !busy;
        _convert.IsEnabled = !busy;
        _clearCacheButton.IsEnabled = !busy;
        _gpuTest.IsEnabled = !busy;
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
            UpdateResourceSizeStatus();
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
