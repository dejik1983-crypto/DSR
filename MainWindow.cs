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
    private Button _prepareButton => PrepareButton;
    private bool _prepared;
    // Всплывающие подсказки: их в приложении много, поэтому галочка в шапке убирает все сразу,
    // а выбор сохраняется между запусками (см. WindowSettings в Program). Поле статическое:
    // перехват открытия подсказки идёт на уровне классов элементов (см. статический
    // конструктор ниже), и обработчик не привязан к конкретному окну.
    private static bool _hideToolTips;

    static MainWindow()
    {
        // Событие открытия подсказки зарегистрировано как «прямое» (RoutingStrategy.Direct) и
        // поднимается на самом элементе, поэтому обработчик, повешенный на окно, его не получит.
        // Перехватываем по классам: тогда подходит любой элемент — в том числе внутри шаблонов
        // и в списках, которые создаются позже.
        EventManager.RegisterClassHandler(typeof(UIElement), ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(OnAnyToolTipOpening));
        EventManager.RegisterClassHandler(typeof(ContentElement), ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(OnAnyToolTipOpening));
    }

    // Handled = true не даёт подсказке открыться (см. PopupControlService.ShowToolTip).
    private static void OnAnyToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (_hideToolTips) e.Handled = true;
    }
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
    private Button _checkResourcesButton => CheckResourcesButton;
    private Button _reloadResourcesButton => ReloadResourcesButton;
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
    private Program.BookWorkspace? _workspace;
    private bool _busy;
    private bool _preparing;

    public MainWindow() : this(null, null) { }

    public MainWindow(string? reviewPath, string? fb2Path)
    {
        InitializeComponent();
        // Версия видна в подзаголовке и в заголовке окна — чтобы её не приходилось искать.
        VersionText.Text = "версия " + Program.DisplayVersion;
        Title = $"DSR {Program.DisplayVersion} — RUAccent + Silero и доработка книг";
        // Окно не должно быть выше рабочей области экрана: при крупном масштабе
        // заявленные 1280×920 превращаются в пиксели больше экрана, шапка уезжает
        // вверх, и содержимое вкладки наползает на заголовок с вкладками.
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
            Width = Math.Min(Width, workArea.Width);
            Height = Math.Min(Height, workArea.Height);
        }
        foreach (var model in new[] { "big_poetry" }) _model.Items.Add(model);
        _model.SelectedIndex = 0;
        _model.SelectionChanged += (_, _) => UpdateSelectedModelStatus();
        _device.Items.Add("CPU");
        _device.SelectedIndex = 0;
        BrowseInputButton.Click += (_, _) => SelectInput();
        OpenWorkspaceButton.Click += (_, _) => OpenWorkspaceFolder();
        _clearCacheButton.Click += (_, _) => ClearCache();
        _checkResourcesButton.Click += async (_, _) => await CheckResourcesAsync();
        _reloadResourcesButton.Click += async (_, _) => await ReloadResourcesAsync();
        HelpButton.Click += (_, _) => ShowProcessHelp();
        HideToolTipsCheckBox.Checked += (_, _) => ApplyHideToolTips(true);
        HideToolTipsCheckBox.Unchecked += (_, _) => ApplyHideToolTips(false);
        _hideToolTips = Program.LoadHideToolTips();
        HideToolTipsCheckBox.IsChecked = _hideToolTips;
        var canWrite = CanWriteToApplicationDirectory();
        _clearCacheButton.IsEnabled = canWrite;
        _checkResourcesButton.IsEnabled = canWrite;
        _reloadResourcesButton.IsEnabled = canWrite;
        _prepareButton.IsEnabled = canWrite;
        _convert.IsEnabled = canWrite;
        UpdateSelectedModelStatus();
        _status.Text = canWrite
            ? "Выберите книгу и нажмите «Обработать книгу»: DSR прогонит её через RUAccent и Silero и соберёт расхождения."
            : "Папка приложения недоступна для записи. Переместите DSR.exe в папку с правами записи (например, не в Program Files).";
        _convert.Click += async (_, _) => await ConvertAsync();
        ConvertStressAnchorButton.Click += async (_, _) => await ConvertWithStressAnchorAsync();
        ConvertStagedButton.Click += async (_, _) => await ConvertStagedAsync();
        _pauseConversion.Click += (_, _) => ToggleConversionPause();
        _stopConversion.Click += (_, _) => StopConversion();
        _reviewWords.Click += (_, _) => OpenWordReview();
        ContinueWorkButton.Click += ContinueWork_Click;
        UpdateReviewButton();
        _prepareButton.Click += async (_, _) => { await PrepareAsync(automatic: false); };
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
        // Подготовка окружения не требует нажатий: при первом запуске, в новом месте,
        // на новом компьютере или после смены модели DSR готовится сам.
        Loaded += (_, _) => _ = AutoPrepareIfNeededAsync();
        // Высоты вкладок и редактора считаются от видимой области окна — один раз после
        // первой отрисовки, когда размеры элементов уже известны.
        ContentRendered += (_, _) => FitWindowLayoutSoon();
        // Рабочая папка, результат RUAccent и производные пути (итог, словоформы,
        // статистика) заполняются сами по выбранной книге.
        InputTextBox.TextChanged += (_, _) => UpdateWorkspacePaths(suggestOutput: true);
        OutputTextBox.TextChanged += (_, _) => UpdateDerivedFilePaths();
        ReviewFileTextBox.TextChanged += (_, _) => UpdateDerivedFilePaths();
        Fb2FileTextBox.TextChanged += (_, _) => UpdateDerivedFilePaths();
        InitializeReview(reviewPath, fb2Path);
        if (reviewPath != null || fb2Path != null) ReviewTabs.SelectedIndex = 1;
        else RestoreLastSession();
    }

    /// <summary>
    /// Считает рабочую папку по выбранной книге и подставляет путь результата RUAccent.
    /// Всё, что создаёт DSR, лежит рядом с исходником в папке «DSR_имя книги»: отдельно
    /// прогон RUAccent, отдельно Silero, затем расхождения, словоформы и итог. Исходник
    /// при этом не изменяется.
    /// </summary>
    private void UpdateWorkspacePaths(bool suggestOutput)
    {
        var source = _loadedFile;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            _workspace = null;
            SetTextBoxValue(WorkspaceTextBox, string.Empty);
            SetTextBoxValue(OutputTextBox, string.Empty);
            OpenWorkspaceButton.IsEnabled = false;
            UpdateDerivedFilePaths();
            UpdateReviewButton();
            return;
        }
        _workspace = Program.WorkspaceFor(source);
        SetTextBoxValue(WorkspaceTextBox, _workspace.Root);
        OpenWorkspaceButton.IsEnabled = true;
        if (suggestOutput || string.IsNullOrWhiteSpace(OutputTextBox.Text))
            SetTextBoxValue(OutputTextBox, _workspace.RuAccentBook);
        UpdateDerivedFilePaths();
        UpdateReviewButton();
    }

    private static void SetTextBoxValue(TextBox box, string value)
    {
        if (!string.Equals(box.Text, value, StringComparison.Ordinal)) box.Text = value;
    }

    private void OpenWorkspaceFolder()
    {
        var folder = _workspace?.Root;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _status.Text = "Рабочая папка появится после выбора книги.";
            return;
        }
        OpenFolder(folder);
    }

    /// <summary>
    /// Показывает пути, которые приложение назначает само: итоговый файл после правок,
    /// файл сохранённых словоформ и отчёты со статистикой. Пусто, если книга не выбрана.
    /// </summary>
    private void UpdateDerivedFilePaths()
    {
        var workspace = _workspace;
        SetDerivedPath(CorrectedPathLabel, CorrectedFileTextBox,
            workspace != null && File.Exists(workspace.RuAccentBook) ? Program.CorrectedPathFor(workspace.RuAccentBook) : string.Empty);
        SetDerivedPath(FormsPathLabel, FormsFileTextBox,
            !string.IsNullOrWhiteSpace(ReviewFileTextBox.Text) ? Program.FormCorrectionsPathFor(ReviewFileTextBox.Text) : string.Empty);
        SetDerivedPath(StatsPathLabel, StatsFileTextBox, workspace != null
            ? string.Join("   ·   ", new[]
            {
                Path.GetFileName(workspace.RuAccentStatistics),
                Path.GetFileName(workspace.SileroStatistics),
                Path.GetFileName(workspace.ComparisonStatistics)
            })
            : string.Empty);
    }

    // Пустую строку показывать незачем: без выбранных файлов группа «Файлы» остаётся компактной.
    private static void SetDerivedPath(TextBlock label, TextBox box, string value)
    {
        var visible = !string.IsNullOrEmpty(value);
        label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        box.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        box.Text = value;
    }

    // Галочка «Выключить всплывающие подсказки»: сама подсказка не показывается обработчиком
    // OnAnyToolTipOpening, здесь остаётся только запомнить выбор.
    private void ApplyHideToolTips(bool hide)
    {
        if (_hideToolTips == hide) return;
        _hideToolTips = hide;
        Program.SaveHideToolTips(hide);
    }

    private void ShowProcessHelp()
    {
        MessageBox.Show(this,
            "Порядок работы\n\n" +
            "Исходная книга не изменяется. Всё, что создаёт DSR, складывается рядом с ней в папку «DSR_имя книги».\n\n" +
            "1. Обработка книги\n" +
            "• Выберите книгу FB2/TXT. Модель RUAccent и устройство (CPU или CUDA) можно не трогать — они подбираются сами.\n" +
            "• Нажмите «Обработать книгу». DSR сначала прогонит её через RUAccent, затем через Silero и сравнит оба результата.\n" +
            "• Или нажмите «Обработать по этапам (add-stress → RUAccent + Silero)» — рекомендуемый режим высокой точности: add-stress создаёт базу (его метки точные), затем RUAccent и Silero заполняют только слова без ударения в базе, а сравнение делит результат на «спорно», «сомнительно» и «неизвестно/опечатка». Совпавшие ударения принимаются автоматически.\n" +
            "• Галочка «Второе мнение» включает прогон Silero. Выключите её, если Silero не установлен или нужен только RUAccent.\n" +
            "• Нажимать «Подготовка к работе» и «Проверить GPU / CUDA» обычно не нужно: DSR готовит окружение сам при первом запуске, в новом месте, на новом компьютере и после смены модели. При первом запуске программа предупреждает, что скачает, и показывает шкалу прогресса.\n" +
            "• Всё, что DSR скачивает (Python, пакеты, модели), лежит в папке «ресурсы» рядом с DSR.exe. Словари уже встроены в exe. Папку «ресурсы» можно удалить целиком — программа и ваши книги не пострадают.\n" +
            "• Кнопки в разделе «Ресурсы»: «Проверить ресурсы» (глубокая проверка без скачивания), «Загрузить ресурсы заново» (удалить и скачать заново), «Очистить всё» (просто удалить папку ресурсов).\n" +
            "• Результаты: в папке «DSR_имя книги» появляются прогон RUAccent, прогон Silero со своей статистикой и отчёт расхождений.\n\n" +
            "2. Расхождения и ИИ (можно пропустить)\n" +
            "• В списке — только слова, где RUAccent и Silero поставили ударение на разные гласные.\n" +
            "• Выберите движок подсказок — свою модель на диске, Ollama или внешний API — и нажмите «Подсказки ИИ по расхождениям».\n" +
            "• Найденное — только подсказки: где ИИ не уверен, слово просто уходит в ручную доводку без варианта.\n" +
            "• Движок omogre разбирает не слово целиком, а каждый случай расхождения по предложению: омографы вроде «за́мок/замо́к» раскладываются по контексту.\n\n" +
            "3. Контроль ИИ результатов (если запускали omogre)\n" +
            "• Сверху — слово, ниже два окна: слева вариант 1 (общая форма группы), справа вариант 2 (случаи с другим ударением).\n" +
            "• Один щелчок по случаю раскрывает фрагмент целиком — проверяемое слово подсвечено, как в ручной доводке.\n" +
            "• Двойной щелчок переносит случай в другое окно; так же работают перетаскивание мышкой и кнопки ← → ; правая кнопка — меню: скопировать слово с контекстом, открыть предложение в Google, перенести случай.\n\n" +
            "4. Ручная доводка\n" +
            "• Здесь только опечатки и нераспознанные слова: то, что ИИ разобрала по случаям, принимают на вкладке 3 и сюда не повторяется.\n" +
            "• Выберите слово в списке и конкретный контекст.\n" +
            "• Поставьте ударение (или исправьте опечатку прямо в поле «Фрагмент текста») и нажмите «Да ✓».\n" +
            "• «Сохранить» запоминает прогресс правок; «Завершить работу» собирает итог — файл «имя_итог» в рабочей папке. Исходная книга и прогоны движков не изменяются.\n\n" +
            "Подсказки интерфейса\n" +
            "• Всплывающих подсказок в программе много: у каждой кнопки и поля есть пояснение. Если они мешают, поставьте галочку «Выключить всплывающие подсказки» в шапке окна — она убирает их все сразу, и выбор сохраняется при следующем запуске.",
            "Как это работает", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateSelectedModelStatus()
    {
        var model = _model.SelectedItem?.ToString() ?? "big_poetry";
        var path = Path.Combine(Program.DataDirectory, "python", "Lib", "site-packages", "ruaccent", "nn", "nn_omograph", model);
        long bytes = 0;
        try
        {
            if (Directory.Exists(path))
                bytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        }
        catch (IOException) { }
        var baseText = bytes > 0
            ? $"Выбрана модель омографов: {model}; её файлы (ONNX и токенизатор) занимают {bytes / 1024.0 / 1024:0.0} МБ."
            : $"Выбрана модель омографов: {model}; файлы появятся при загрузке.";
        // Окружение не совпадает с отметкой (новая версия, место, машина или модель) —
        // DSR подготовит его сам при следующем запуске.
        var pending = PreparationStamp.TryRead() is { } stamp && !stamp.IsValidFor(model);
        _modelStatus.Text = pending
            ? baseText + " Окружение для этой модели ещё не подготовлено — DSR подготовит его сам (или по кнопке «Подготовка к работе»)."
            : baseText;
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
                    ("Модели", Path.Combine(root, "модели")), ("Словари и движки", Path.Combine(root, "dictionary")),
                    ("add-stress", Path.Combine(root, "add-stress")), ("Кэш загрузок", Path.Combine(root, "кэш")) };
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
        var extension = Path.GetExtension(dialog.FileName);
        if (!extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Поддерживаются только файлы FB2 и TXT.", "Неподдерживаемый формат", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _loadedFile = dialog.FileName;
        _input.Text = _loadedFile; // запускает UpdateWorkspacePaths: рабочая папка и результат RUAccent
        _illustrations.IsEnabled = extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase);
        // Смена книги начинается с чистого листа: старые список слов и пути другой книги
        // не должны оставаться на экране.
        ClearReviewStateForNewBook();
        // Если этот документ уже обрабатывали, готовые материалы подставляются и список
        // слов загружается сразу — повторно прогонять движки не нужно.
        if (LoadExistingWorkspaceMaterials())
        {
            _status.Text = "Найдена рабочая папка этой книги: отчёт и файл правок загружены. Можно продолжить доработку или запустить обработку заново.";
        }
        else
        {
            UpdateDerivedFilePaths();
            UpdateReviewButton();
            _status.Text = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? "TXT выбран. Строки и структура файла сохранятся; исходник не изменяется."
                : "FB2 выбран. Иллюстрации можно сохранить; исходник не изменяется.";
        }
    }

    /// <summary>
    /// Если у выбранной книги уже есть проработанная рабочая папка, подставляет её файлы
    /// и сразу загружает список слов в редактор — не дожидаясь повторного прогона движков.
    /// Приоритет у материалов этапного конвейера: отчёт расхождений и «база + совпадения».
    /// </summary>
    private bool LoadExistingWorkspaceMaterials()
    {
        var workspace = _workspace;
        if (workspace == null) return false;
        var review = File.Exists(workspace.ComparisonReport) ? workspace.ComparisonReport
            : File.Exists(workspace.RuAccentReview) ? workspace.RuAccentReview : null;
        var book = File.Exists(workspace.StagedAppliedBook) ? workspace.StagedAppliedBook
            : File.Exists(workspace.BaseBook) ? workspace.BaseBook
            : File.Exists(workspace.RuAccentBook) ? workspace.RuAccentBook : null;
        if (review == null && book == null) return false;
        if (book != null) SetTextBoxValue(OutputTextBox, book);
        SetReviewPaths(review, book);
        UpdateDerivedFilePaths();
        UpdateReviewButton();
        RememberSession();
        ReviewTabs.SelectedIndex = 1;
        return true;
    }

    /// <summary>Сбрасывает список слов и пути правок при выборе другой книги.</summary>
    private void ClearReviewStateForNewBook()
    {
        _reviewPath = null;
        _outputPath = null;
        SetTextBoxValue(ReviewFileTextBox, string.Empty);
        SetTextBoxValue(Fb2FileTextBox, string.Empty);
        _entries.Clear();
        _llmSuggestions.Clear();
        _llmRejected.Clear();
        _controlVariants.Clear();
        _controlGroups = new();
        _controlGroupIndex = -1;
        _hasModelDisagreements = false;
        ApplyReviewItems();
        UpdateEditorEnabled();
        UpdateNavigationState();
    }

    // Гарантирует, что окружение Python готово до начала конвертации, и показывает
    // прогресс первого запуска, пока окружение и модель скачиваются.

    private async Task CheckGpuAsync()
    {
        // Проверка GPU пользуется тем же окружением Python, что и подготовка, поэтому
        // на время проверки блокируем подготовку (и наоборот) — иначе два pip-процесса
        // одновременно переписывают файлы окружения и один падает с WinError 32.
        SetBusy(true);
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
        finally { SetBusy(false); }
    }

    private async Task ConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(_loadedFile) || !File.Exists(_loadedFile)) { _status.Text = "Сначала выберите существующий FB2- или TXT-файл."; return; }
        var sourceExtension = Path.GetExtension(_loadedFile);
        if (!sourceExtension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) && !sourceExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) { _status.Text = "Поддерживаются только форматы FB2 и TXT."; return; }
        if (!_prepared || !Directory.Exists(Program.DataDirectory))
        {
            _status.Text = "Окружение ещё готовится. Дождитесь сообщения «Приложение готово к работе» и нажмите «Обработать книгу» снова.";
            return;
        }
        var input = _loadedFile!;
        _workspace = Program.WorkspaceFor(input);
        var model = _model.SelectedItem?.ToString() ?? "big_poetry";
        var device = _device.SelectedItem?.ToString() ?? "CPU";
        var includeImages = _illustrations.IsChecked == true;
        var runSilero = SileroCompareCheckBox.IsChecked == true;
        StartActivity("Подготовка обработки");
        SetBusy(true);
        _conversionControl = new Program.ConversionControl();
        _pauseConversion.IsEnabled = true;
        _stopConversion.IsEnabled = true;
        _pauseConversion.Content = "Пауза";
        _diagnosticsPath = _workspace.RuAccentStatistics;
        var conversionWatch = Stopwatch.StartNew();
        var totalUnits = 0;
        var sourceBytes = new FileInfo(input).Length;
        var sourceCharacters = sourceBytes;
        if (sourceExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            try { sourceCharacters = File.ReadAllText(input).Length; } catch (IOException) { }
        }
        _conversionStats.Text = $"Файл: {sourceBytes / 1024d / 1024d:0.00} МБ · около {sourceCharacters:N0} символов · прошло 00:00:00 · осталось —";
        _modelStatus.Text = $"Загружается модель омографов {model}; размер будет показан во время скачивания…";
        _status.Text = runSilero
            ? $"Обрабатываю книгу: сначала RUAccent, затем Silero и сравнение (модель {model}, {device})."
            : $"Обрабатываю книгу только движком RUAccent (модель {model}, {device}); Silero выключен.";
        _progress.Value = 0;
        try
        {
            var workspace = await Task.Run(() => Program.ConvertBookInteractive(input, model, device, includeImages, "CPU", runSilero,
                // Сообщения обоих движков идут через этот обработчик: загрузка модели,
                // этапы, ошибки — всё попадает в строку состояния, статистику и прогресс.
                message => Dispatcher.Invoke(() =>
                {
                    if (message.StartsWith("DIAGNOSTICS:", StringComparison.Ordinal))
                    {
                        _diagnosticsPath = Program.StatisticsPathFor(message["DIAGNOSTICS:".Length..]);
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
                    _status.Text = $"RUAccent расставляет ударения: {completed:N0} из {total:N0} фрагментов ({_progress.Value:0}%).";
                    _conversionStats.Text = $"Файл: {sourceBytes / 1024d / 1024d:0.00} МБ · около {sourceCharacters:N0} символов · прошло {FormatElapsed(conversionWatch.Elapsed)} · осталось {(remaining.HasValue ? FormatElapsed(remaining.Value) : "расчёт…")}";
                }),
                stage => Dispatcher.Invoke(() => { SetActivityStage(stage); }),
                _conversionControl!));
            conversionWatch.Stop();
            _workspace = workspace;
            // Пути результата и отчёта — прямо из рабочей папки: вкладки 2, 3 и 4 сразу
            // готовы к работе, ничего искать вручную не нужно.
            SetTextBoxValue(WorkspaceTextBox, workspace.Root);
            SetTextBoxValue(OutputTextBox, workspace.RuAccentBook);
            var review = runSilero && File.Exists(workspace.ComparisonReport) ? workspace.ComparisonReport
                : File.Exists(workspace.RuAccentReview) ? workspace.RuAccentReview
                : File.Exists(workspace.ComparisonReport) ? workspace.ComparisonReport : null;
            var book = File.Exists(workspace.RuAccentBook) ? workspace.RuAccentBook : null;
            SetTextBoxValue(ReviewFileTextBox, review ?? string.Empty);
            SetTextBoxValue(Fb2FileTextBox, book ?? string.Empty);
            SetReviewPaths(review, book);
            UpdateDerivedFilePaths();
            UpdateReviewButton();
            RememberSession();
            // С Silero дальше идём на вкладку расхождений; без него — сразу к ручной доводке.
            ReviewTabs.SelectedIndex = runSilero ? 1 : 3;
            _conversionStats.Text = $"Файл: {sourceBytes / 1024d / 1024d:0.00} МБ · около {sourceCharacters:N0} символов · обработано {totalUnits:N0} фрагментов за {FormatElapsed(conversionWatch.Elapsed)} · всё в папке {workspace.Root}";
            _status.Text = (runSilero
                ? "Обработка завершена: RUAccent, Silero и сравнение готовы. "
                : "Обработка завершена: результат RUAccent готов. ") + workspace.Root;
            StopActivity("Обработка завершена");
            var disagreements = _entries.Count(HasModelDisagreement);
            // Третья категория сравнения: слова без ударения у обеих моделей.
            var withoutStress = _entries.Count(entry => !HasModelDisagreement(entry));
            if (!runSilero)
                FileStatusTextBlock.Text = "Silero был выключен: для ручной доводки подготовлен список слов, которые RUAccent оставил без ударения.";
            else if (disagreements > 0)
                FileStatusTextBlock.Text = $"Расхождений RUAccent и Silero: {disagreements:N0} случаев" +
                    (withoutStress > 0 ? $"; без ударения у обеих моделей: {withoutStress:N0}" : string.Empty) +
                    ". Вкладка «2. Расхождения и ИИ» — разбор по контексту (omogre) на вкладке «3. Контроль ИИ результатов», затем ручная доводка.";
            else if (withoutStress > 0)
                FileStatusTextBlock.Text = $"Расхождений нет: модели совпали. Без ударения у обеих моделей: {withoutStress:N0} — задайте ударение на вкладке «4. Ручная доводка».";
            else
                FileStatusTextBlock.Text = "RUAccent и Silero совпали: расхождений нет, ручная доводка не требуется.";
            _ = UpdateResourceSizeStatusAsync();
            if (MessageBox.Show(this, "Обработка книги завершена. Открыть рабочую папку с результатами?", "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenFolder(workspace.Root);
        }
        catch (OperationCanceledException)
        {
            conversionWatch.Stop();
            StopActivity("Обработка остановлена");
            _status.Text = "Обработка остановлена. Уже готовые прогоны и отчёты остались в рабочей папке.";
            _conversionStats.Text = $"Остановлено через {FormatElapsed(conversionWatch.Elapsed)} · отчёт: {_diagnosticsPath}";
        }
        catch (Exception ex)
        {
            StopActivity("Ошибка обработки");
            Program.LogError($"Ошибка обработки книги: {input}", ex);
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

    // Кнопка активна, когда есть готовый отчёт: расхождения (или список неударенных
    // слов RUAccent, если Silero выключен). Отчёт и книга для правок берутся из полей
    // вкладки 1 — они заполняются автоматически после обработки.
     private async Task ConvertWithStressAnchorAsync()
     {
         if (string.IsNullOrWhiteSpace(_loadedFile) || !File.Exists(_loadedFile)) { _status.Text = "Сначала выберите существующий FB2- или TXT-файл."; return; }
         var sourceExtension = Path.GetExtension(_loadedFile);
         if (!sourceExtension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) && !sourceExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) { _status.Text = "Поддерживаются только форматы FB2 и TXT."; return; }
         var input = _loadedFile!;
         _workspace = Program.WorkspaceFor(input);
         StartActivity("Подготовка обработки");
         SetBusy(true);
         _conversionControl = new Program.ConversionControl();
         _pauseConversion.IsEnabled = false;
         _stopConversion.IsEnabled = true;
         _status.Text = "Обрабатываю книгу через add-stress-to-epub (spaCy + Wiktionary)…";
         _progress.IsIndeterminate = true;
         var output = Path.Combine(_workspace.Root, Path.GetFileNameWithoutExtension(input) + "_add-stress.fb2");
         if (sourceExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) output = Path.Combine(_workspace.Root, Path.GetFileNameWithoutExtension(input) + "_add-stress.txt");
         try
         {
             var progress = new Progress<(int Completed, int Total, string Message)>(u => Dispatcher.Invoke(() =>
             {
                 _status.Text = u.Message;
                 if (u.Total > 0) { _progress.IsIndeterminate = false; _progress.Value = u.Completed * 100.0 / u.Total; }
             }));
             await Task.Run(() => Program.ConvertWithStressAnchorInteractive(input, output, "CPU", progress, _conversionControl!));
             SetTextBoxValue(OutputTextBox, output);
             SetTextBoxValue(WorkspaceTextBox, _workspace.Root);
             _status.Text = "Готово: " + output;
             _progress.Value = 100;
         }
         catch (Exception ex)
         {
             _status.Text = "Ошибка: " + ex.Message;
         }
         finally
         {
             SetBusy(false);
             _stopConversion.IsEnabled = false;
         }
     }

     /// <summary>
     /// Этапный конвейер: add-stress создаёт базу, RUAccent и Silero заполняют только
     /// оставленные базой слова без ударения, затем сравнение делит расхождения на
     /// «спорно», «сомнительно» и «неизвестно/опечатка». Редактируемый файл — база.
     /// </summary>
     private async Task ConvertStagedAsync()
     {
         if (string.IsNullOrWhiteSpace(_loadedFile) || !File.Exists(_loadedFile)) { _status.Text = "Сначала выберите существующий FB2- или TXT-файл."; return; }
         var sourceExtension = Path.GetExtension(_loadedFile);
         if (!sourceExtension.Equals(".fb2", StringComparison.OrdinalIgnoreCase) && !sourceExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) { _status.Text = "Поддерживаются только форматы FB2 и TXT."; return; }
         var input = _loadedFile!;
         var model = _model.SelectedItem?.ToString() ?? "big_poetry";
         var device = _device.SelectedItem?.ToString() ?? "CPU";
         StartActivity("Подготовка этапного прогона");
         SetBusy(true);
         _conversionControl = new Program.ConversionControl();
         _pauseConversion.IsEnabled = true;
         _stopConversion.IsEnabled = true;
         _pauseConversion.Content = "Пауза";
         _status.Text = "Этапный прогон: база (add-stress) → RUAccent + Silero → сравнение…";
         _progress.IsIndeterminate = true;
         var conversionWatch = Stopwatch.StartNew();
         try
         {
             var workspace = await Task.Run(() => Program.ConvertStagedBookInteractive(
                 input, model, device, device,
                 text => Dispatcher.Invoke(() => _status.Text = text),
                 (completed, total) => Dispatcher.Invoke(() =>
                 {
                     _progress.IsIndeterminate = false;
                     _progress.Value = total == 0 ? 100 : completed * 100.0 / total;
                 }),
                 stage => Dispatcher.Invoke(() => SetActivityStage(stage)),
                 _conversionControl!));
             conversionWatch.Stop();
             _workspace = workspace;
             // Редактируемый файл — «база + совпадения»: именно он становится итогом после правок.
             SetTextBoxValue(WorkspaceTextBox, workspace.Root);
             var applied = File.Exists(workspace.StagedAppliedBook) ? workspace.StagedAppliedBook : workspace.BaseBook;
             SetTextBoxValue(OutputTextBox, applied);
             var review = File.Exists(workspace.ComparisonReport) ? workspace.ComparisonReport : null;
             var book = File.Exists(applied) ? applied : null;
             SetTextBoxValue(ReviewFileTextBox, review ?? string.Empty);
             SetTextBoxValue(Fb2FileTextBox, book ?? string.Empty);
             SetReviewPaths(review, book);
             UpdateDerivedFilePaths();
             UpdateReviewButton();
             RememberSession();
             ReviewTabs.SelectedIndex = 1;
             _progress.IsIndeterminate = false;
             _progress.Value = 100;
             _conversionStats.Text = $"Этапный прогон за {FormatElapsed(conversionWatch.Elapsed)} · всё в папке {workspace.Root}";
             _status.Text = "Этапный прогон завершён: база, RUAccent, Silero и сравнение готовы. " + workspace.Root;
             StopActivity("Этапный прогон завершён");
             FileStatusTextBlock.Text = "Редактируемый файл — «база + совпадение»: база add-stress плюс ударения, в которых RUAccent и Silero совпали. " +
                 "Вкладка «2. Расхождения и ИИ»: «спорно», «сомнительно» и «неизвестно/опечатка». Кнопка «Завершить работу» соберёт итоговый файл.";
         }
         catch (OperationCanceledException)
         {
             conversionWatch.Stop();
             StopActivity("Этапный прогон остановлен");
             _status.Text = "Этапный прогон остановлен. Уже готовые этапы остались в рабочей папке.";
         }
         catch (Exception ex)
         {
             StopActivity("Ошибка этапного прогона");
             Program.LogError($"Ошибка этапного прогона: {input}", ex);
             _status.Text = "Ошибка: " + ex.GetBaseException().Message + $"\nПодробности: {Program.LogFilePath}";
         }
         finally
         {
             if (_activityStartedAt != DateTime.MinValue) StopActivity("Этапный прогон завершён");
             _conversionControl = null;
             _pauseConversion.IsEnabled = false;
             _stopConversion.IsEnabled = false;
             _pauseConversion.Content = "Пауза";
             SetBusy(false);
         }
     }

    private void UpdateReviewButton()
    {
        var reviewPath = !string.IsNullOrWhiteSpace(ReviewFileTextBox.Text) && File.Exists(ReviewFileTextBox.Text)
            ? ReviewFileTextBox.Text : null;
        var bookPath = !string.IsNullOrWhiteSpace(Fb2FileTextBox.Text) && File.Exists(Fb2FileTextBox.Text)
            ? Fb2FileTextBox.Text : null;
        _reviewWords.Tag = reviewPath != null ? (reviewPath, bookPath ?? reviewPath) : null;
        _reviewWords.IsEnabled = reviewPath != null;
    }

    private void OpenWordReview()
    {
        var reviewPath = _reviewWords.Tag is ValueTuple<string, string> paths ? paths.Item1 : null;
        var bookPath = _reviewWords.Tag is ValueTuple<string, string> known ? known.Item2 : null;
        if (reviewPath != null) SetReviewPaths(reviewPath, bookPath);
        ReviewTabs.SelectedIndex = 1;
        UpdateReviewButton();
    }

    /// <summary>
    /// Подставляет файлы последней работы, чтобы к уже прогнанному документу можно было
    /// вернуться без поиска отчёта по папке. Список слов при этом не теряется: проверенные
    /// слова остаются в отчёте и просто помечены галочкой.
    /// </summary>
    private void RestoreLastSession()
    {
        var session = Program.LoadLastSession();
        if (session is null || !session.IsUsable)
        {
            RefreshContinueWorkButton();
            return;
        }
        if (!string.IsNullOrWhiteSpace(session.Input) && File.Exists(session.Input)) InputTextBox.Text = session.Input;
        SetReviewPaths(session.Review, string.IsNullOrWhiteSpace(session.File) || !File.Exists(session.File) ? null : session.File);
        UpdateDerivedFilePaths();
        UpdateReviewButton();
        RefreshContinueWorkButton();
    }

    private void RefreshContinueWorkButton()
    {
        var session = Program.LoadLastSession();
        if (session is null || !session.IsUsable)
        {
            ContinueWorkButton.IsEnabled = false;
            ContinueWorkButton.Content = "Продолжить работу";
            ContinueWorkButton.ToolTip = "Пока нечего продолжать: отчёт слов ещё не создавался. Запустите конвертацию или откройте готовый отчёт кнопкой «Отчёт…».";
            return;
        }
        var title = LastSessionTitle();
        ContinueWorkButton.IsEnabled = true;
        ContinueWorkButton.Content = $"Продолжить: {title}";
        ContinueWorkButton.ToolTip = $"Вернуться к документу «{title}». Отчёт: {session.Review}" +
            (File.Exists(session.File) ? $"\nПравки в: {session.File}" : "\nОтредактированный файл не найден — укажите его кнопкой «Файл…».");
    }

    private string LastSessionTitle()
    {
        var session = Program.LoadLastSession();
        if (session is null) return string.Empty;
        var path = !string.IsNullOrWhiteSpace(session.File) && File.Exists(session.File) ? session.File : session.Review;
        var title = Path.GetFileNameWithoutExtension(path);
        // Технические хвосты в названии кнопки только занимают место.
        foreach (var suffix in new[] { "_итог", "_расхождения", "_ruaccent", "_corrected", "_проверка-слов", "_ударения" })
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length];
                break;
            }
        title = title.Trim();
        return title.Length <= 34 ? title : title[..33] + "…";
    }

    private void ContinueWork_Click(object sender, RoutedEventArgs e)
    {
        var session = Program.LoadLastSession();
        if (session is null || !session.IsUsable)
        {
            RestoreLastSession();
            return;
        }
        var existingFile = !string.IsNullOrWhiteSpace(session.File) && File.Exists(session.File) ? session.File : null;
        _reviewPath = session.Review;
        _outputPath = existingFile;
        ReviewFileTextBox.Text = _reviewPath;
        Fb2FileTextBox.Text = existingFile ?? string.Empty;
        SetReviewPaths(_reviewPath, existingFile);
        UpdateDerivedFilePaths();
        UpdateReviewButton();
        ReviewTabs.SelectedIndex = 1;
    }

    private void StopConversion()
    {
        _conversionControl?.Stop();
        _status.Text = "Останавливаю после текущего фрагмента…";
    }

    /// <summary>
    /// Подготовка окружения. При automatic = true запускается сама при первом запуске,
    /// в новом месте, на новом компьютере или после смены модели — без кнопок и с
    /// предупреждением о том, что будет скачано. force = true готовит заново даже при
    /// наличии отметки (кнопка «Загрузить ресурсы заново»).
    /// </summary>
    private async Task<bool> PrepareAsync(bool automatic, bool force = false, SetupWindow? progress = null)
    {
        if (_preparing) return false;
        // Ранний выход только если подготовлено именно то, что выбрано сейчас:
        // после смены модели отметка не подходит и модель нужно скачать.
        var currentModel = _model.SelectedItem?.ToString() ?? "big_poetry";
        if (!force && _prepared && Directory.Exists(Program.DataDirectory)
            && PreparationStamp.TryRead() is { } readyStamp && readyStamp.IsValidFor(currentModel))
        {
            if (!automatic)
                MessageBox.Show(this, "Приложение готово к работе.\n\nДоступно: обработка FB2 с сохранением структуры и изображений, обработка TXT с сохранением строк, настройка CPU/CUDA.", "DSR готов", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        _preparing = true;
        StartActivity("Подготовка Python и моделей");
        SetBusy(true);
        var selectedDevice = _device.SelectedItem?.ToString() ?? "CPU";
        var selectedModel = _model.SelectedItem?.ToString() ?? "big_poetry";
        var statusText = automatic
            ? "Первый запуск в этом месте: настраиваю Python и зависимости, загружаю выбранную модель, выполняю тест обработки…"
            : "Настраиваю Python и зависимости, загружаю выбранную модель, выполняю тест обработки…";
        _status.Text = statusText;

        void Report(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _status.Text = message;
                SetActivityStage(message);
                SetActivityDownloading(message.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Retrying", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Загрузка", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Скачиваю", StringComparison.OrdinalIgnoreCase));
            }));
            progress?.Report(message);
        }

        try
        {
            Report(statusText);
            Program.SetupProgress = Report;
            try
            {
                await Task.Run(() => Program.PrepareInteractive(selectedModel, selectedDevice, Report));
            }
            finally { Program.SetupProgress = null; }
            _ = UpdateResourceSizeStatusAsync();
            Program.GpuProbeResult gpu;
            Report("Проверяю видеокарту…");
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
            // Отметка о подготовке: в следующий раз всё это сделается само.
            PreparationStamp.Create(selectedModel, _device.SelectedItem?.ToString() ?? selectedDevice, _gpuStatus.Text).Save();
            progress?.Report("Подготовка завершена. Ресурсы готовы.", 100);
            if (!automatic && progress == null)
                MessageBox.Show(this, "Приложение готово к работе.\n\nДоступно:\n• FB2 → FB2 с сохранением структуры и иллюстраций;\n• TXT → TXT с сохранением строк и форматирования текста;\n• CPU или доступная CUDA-видеокарта;\n• исходный файл не изменяется.\n\nДальше готовить вручную не нужно: при следующих запусках DSR не проверяет всё заново.", "DSR готов", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            if (Program.SetupProgress != null) Program.SetupProgress = null;
            StopActivity("Ошибка подготовки");
            _prepared = false;
            PreparationStamp.Clear();
            var details = $"Модель: {_model.SelectedItem}; устройство: {_device.SelectedItem}; Python: {Path.Combine(Program.DataDirectory, "python", "Scripts", "python.exe")}";
            Program.LogError("Подготовка к работе не завершена", new InvalidOperationException(details, ex));
            var message = ex.GetBaseException().Message;
            _status.Text = "Подготовка не завершена: " + message;
            if (progress != null) progress.Fail(message);
            else MessageBox.Show(this, _status.Text + $"\n\nПодробный лог: {Program.LogFilePath}", "Ошибка подготовки", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _preparing = false;
            if (_activityStartedAt != DateTime.MinValue) StopActivity("Подготовка завершена");
            SetBusy(false);
            if (!CanWriteToApplicationDirectory())
            {
                _prepareButton.IsEnabled = false;
                _convert.IsEnabled = false;
                _clearCacheButton.IsEnabled = false;
                _checkResourcesButton.IsEnabled = false;
                _reloadResourcesButton.IsEnabled = false;
            }
        }
    }

    /// <summary>
    /// Готовим окружение сами, когда это действительно нужно: первый запуск, новое место,
    /// другой компьютер, новая версия DSR или другая модель обработки. Если отметка о
    /// подготовке совпадает и файлы модели на месте, проверок через Python не делаем;
    /// глубокая проверка доступна кнопкой «Проверить ресурсы».
    /// </summary>
    private async Task AutoPrepareIfNeededAsync()
    {
        try { await AutoPrepareCoreAsync(); }
        catch (Exception error)
        {
            Program.LogError("Автоподготовка окружения", error);
            _status.Text = "Автоматическая подготовка не удалась: " + error.Message + " Нажмите «Подготовка к работе».";
        }
    }

    private async Task AutoPrepareCoreAsync()
    {
        if (!CanWriteToApplicationDirectory())
        {
            Program.LogReview("Автоподготовка: папка приложения недоступна для записи.");
            return;
        }
        var model = _model.SelectedItem?.ToString() ?? "big_poetry";
        Program.LogReview("Автоподготовка: проверяю отметку о подготовке (модель " + model + ").");
        if (PreparationStamp.TryRead() is { } stamp && stamp.IsValidFor(model) && Program.IsModelPrepared(model))
        {
            // Отметка совпала: никаких проверок через Python при каждом запуске.
            // Глубокая проверка доступна отдельной кнопкой «Проверить ресурсы».
            _prepared = true;
            ApplyPreparationStamp(stamp, model);
            Program.LogReview("Автоподготовка: отметка подходит, окружение готово.");
            return;
        }

        Program.LogReview("Автоподготовка: отметки нет или она не подходит, предлагаю подготовку.");
        // Даём окну дорисоваться, чтобы диалог появился поверх готового интерфейса.
        await Task.Delay(250);
        var answer = MessageBox.Show(this,
            "DSR нужно подготовить ресурсы для работы.\n\n"
            + "Сейчас программа:\n"
            + "• проверит Python и пакеты;\n"
            + "• скачает RUAccent, ONNX Runtime и модель ударений;\n"
            + "• при необходимости скачает переносимый Python.\n\n"
            + "Понадобится интернет и около 1–2 ГБ свободного места. Всё скачанное будет "
            + "лежать только в папке «ресурсы» рядом с DSR.exe. Сам DSR.exe и ваши книги "
            + "не изменяются; папку «ресурсы» можно удалить и загрузить заново.\n\n"
            + "Продолжить подготовку сейчас?",
            "Первый запуск DSR", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            _status.Text = "Подготовка отложена. Когда будет интернет, нажмите «Подготовка к работе» "
                + "или «Загрузить ресурсы заново» — программа скачает всё сама.";
            return;
        }

        var wizard = new SetupWindow(automatic: true) { Owner = this };
        var completed = false;
        wizard.Loaded += async (_, _) =>
        {
            completed = await PrepareAsync(automatic: true, force: true, progress: wizard);
            if (completed) wizard.Complete();
        };
        wizard.ShowDialog();
        if (!completed)
            _status.Text = "Подготовка не завершена. Исправьте причину и нажмите «Загрузить ресурсы заново» — уже скачанное сохранится.";
    }

    private void ApplyPreparationStamp(PreparationStamp stamp, string model)
    {
        if (stamp.Device == "CUDA" && !_device.Items.Contains("CUDA")) _device.Items.Add("CUDA");
        if (_device.Items.Contains(stamp.Device)) _device.SelectedItem = stamp.Device;
        if (!string.IsNullOrWhiteSpace(stamp.Gpu))
        {
            _gpuStatus.Text = stamp.Gpu + " Проверено при подготовке " + stamp.PreparedAt + ".";
            _gpuStatus.Foreground = Brushes.DarkGreen;
        }
        _status.Text = "Готово: окружение уже подготовлено (модель омографов: " + model + "). Кнопки подготовки и проверки GPU нажимать не нужно.";
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
        _checkResourcesButton.IsEnabled = !busy && canWrite;
        _reloadResourcesButton.IsEnabled = !busy && canWrite;
        _gpuTest.IsEnabled = !busy;
        BrowseInputButton.IsEnabled = !busy;
        _status.Opacity = busy ? 0.8 : 1;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy;
    }

    private async Task CheckResourcesAsync()
    {
        if (_busy || _preparing) return;
        var model = _model.SelectedItem?.ToString() ?? "big_poetry";
        StartActivity("Проверка ресурсов");
        SetBusy(true);
        _status.Text = "Проверяю ресурсы: словари, Python, пакеты и модель…";
        try
        {
            var report = await Task.Run(() => Program.VerifyResources(model));
            var healthy = report.StartsWith("Ресурсы в порядке", StringComparison.Ordinal);
            MessageBox.Show(this, report, healthy ? "Ресурсы в порядке" : "Проверка ресурсов",
                MessageBoxButton.OK, healthy ? MessageBoxImage.Information : MessageBoxImage.Warning);
            _status.Text = healthy
                ? "Проверка ресурсов пройдена: всё готово к работе."
                : "Проверка ресурсов нашла проблемы. Нажмите «Загрузить ресурсы заново».";
            if (!healthy)
            {
                if (MessageBox.Show(this, "Загрузить ресурсы заново сейчас? Уже скачанные файлы будут заменены.", "Загрузить ресурсы заново",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await ReloadResourcesAsync();
            }
        }
        catch (Exception error)
        {
            Program.LogError("Проверка ресурсов", error);
            MessageBox.Show(this, "Не удалось проверить ресурсы: " + error.GetBaseException().Message
                + $"\n\nПодробности: {Program.LogFilePath}", "Проверка ресурсов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopActivity("Проверка ресурсов завершена");
            SetBusy(false);
        }
    }

    private async Task ReloadResourcesAsync()
    {
        if (_busy || _preparing) return;
        var answer = MessageBox.Show(this,
            "Удалить текущие ресурсы и скачать всё заново?\n\n"
            + "Будут удалены Python, модели, кэш, встроенные словари и служебные настройки DSR. "
            + "Приложение DSR.exe и ваши книги не затрагиваются — всё нужное скачается снова "
            + "в папку «ресурсы» рядом с программой.",
            "Загрузить ресурсы заново", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _prepared = false;
        PreparationStamp.Clear();
        try
        {
            Program.CloseResourceProcesses();
            DeleteResourcesFolder();
            _ = UpdateResourceSizeStatusAsync();
        }
        catch (Exception error)
        {
            Program.LogError("Не удалось очистить ресурсы перед повторной загрузкой", error);
            MessageBox.Show(this, "Не удалось удалить папку ресурсов: " + error.GetBaseException().Message
                + "\n\nЗакройте все окна DSR и повторите. Подробности: " + Program.LogFilePath,
                "Загрузить ресурсы заново", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var wizard = new SetupWindow(automatic: false) { Owner = this };
        var completed = false;
        wizard.Loaded += async (_, _) =>
        {
            completed = await PrepareAsync(automatic: false, force: true, progress: wizard);
            if (completed) wizard.Complete();
        };
        wizard.ShowDialog();
        if (!completed)
            _status.Text = "Загрузка ресурсов не завершена. Повторите «Загрузить ресурсы заново» или проверьте интернет.";
    }

    private void ClearCache()
    {
        var cache = Program.DataDirectory;
        var answer = MessageBox.Show(this,
            $"Удалить все ресурсы DSR ({cache})?\n\nБудут удалены Python, модели, кэш, встроенные словари и служебные настройки программы. "
            + "Приложение DSR.exe и ваши книги не затрагиваются; при следующем запуске всё нужное загрузится заново.",
            "Очистка ресурсов DSR", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Program.CloseResourceProcesses();
            DeleteResourcesFolder();
            _prepared = false;
            PreparationStamp.Clear();
            _clearCacheButton.IsEnabled = CanWriteToApplicationDirectory();
            _checkResourcesButton.IsEnabled = _clearCacheButton.IsEnabled;
            _reloadResourcesButton.IsEnabled = _clearCacheButton.IsEnabled;
            _prepareButton.IsEnabled = _clearCacheButton.IsEnabled;
            _convert.IsEnabled = _clearCacheButton.IsEnabled;
            _ = UpdateResourceSizeStatusAsync();
            _status.Text = "Ресурсы удалены. При следующем запуске DSR подготовит окружение и модели автоматически.";
        }
        catch (Exception ex)
        {
            Program.LogError("Не удалось удалить папку ресурсов", ex);
            _status.Text = "Не удалось удалить папку ресурсов: " + ex.Message + $" Лог: {Program.LogFilePath}";
        }
    }

    /// <summary>Удаляет только «ресурсы» рядом с exe, с проверкой пути.</summary>
    private static void DeleteResourcesFolder()
    {
        var cache = Program.DataDirectory;
        if (!Directory.Exists(cache)) return;
        var normalizedRoot = Path.GetFullPath(Program.ApplicationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCache = Path.GetFullPath(cache).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(normalizedCache), normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(normalizedCache), "ресурсы", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Путь ресурсов не прошёл проверку безопасности: {normalizedCache}");
        Directory.Delete(normalizedCache, recursive: true);
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
