using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FB2AccentConverter;

/// <summary>
/// Доработка после конвертации: подсказки ИИ и ручная доводка ударений и опечаток.
/// Раньше это было отдельное окно; теперь — вкладки 2 и 3 главного окна MainWindow.
/// </summary>
public sealed partial class MainWindow
{
    private string? _reviewPath;
    private string? _outputPath;
    private readonly List<MutableReviewEntry> _entries = new();
    private readonly List<TextBox> _declensions = new();
    private Dictionary<string, int> _wordCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _llmSuggestions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Program.LlmRejected> _llmRejected = new(StringComparer.Ordinal);
    private string _llmStatus = string.Empty;
    private Program.LlmEngine _llmEngine = Program.CurrentLlmEngine();
    private bool _updatingLlmEngine;
    private int _selectedGroupAnchor = -1;
    private int _selectedSampleIndex = -1;
    private bool _rebuildingSamples;
    private bool _updatingCorrection;
    // Текущее написание слова без знака ударения. Именно к нему привязываются кнопки
    // ударных гласных, поэтому исправление опечатки и расстановка ударения работают вместе.
    private string _workingWord = string.Empty;
    // Большой редактор фрагмента: проверяемое слово — жирный Run внутри текста.
    private Run? _wordRun;
    private bool _updatingFragment;
    private bool _wordEditFromEditor;
    private string _fragmentBefore = string.Empty;
    private string _fragmentAfter = string.Empty;
    private double _reviewFontSize = 16;

    /// <summary>
    /// Настраивает элементы вкладок 2–3. Вызывается один раз из конструктора MainWindow
    /// после InitializeComponent. Пути можно передать сразу (режим --review) или позже.
    /// </summary>
    internal void InitializeReview(string? reviewPath = null, string? outputPath = null)
    {
        _reviewPath = reviewPath;
        _outputPath = outputPath;
        SortModeComboBox.ItemsSource = new[] { "По алфавиту", "По частоте в тексте" };
        SortModeComboBox.SelectedIndex = 1;
        GrammaticalCaseComboBox.ItemsSource = new[] { "Именительный", "Родительный", "Дательный", "Винительный", "Творительный", "Предложный", "Другая форма / контекст" };
        GrammaticalCaseComboBox.SelectedIndex = 0;
        ReviewFileTextBox.Text = _reviewPath ?? string.Empty;
        Fb2FileTextBox.Text = _outputPath ?? string.Empty;
        SearchTextBox.TextChanged += SearchTextBox_TextChanged;
        SortModeComboBox.SelectionChanged += SortModeComboBox_SelectionChanged;
        WordGroupsListBox.SelectionChanged += WordGroupsListBox_SelectionChanged;
        ContextsListBox.SelectionChanged += ContextsListBox_SelectionChanged;
        CorrectionTextBox.TextChanged += Correction_TextChanged;
        AddTextEditingContextMenu(CorrectionTextBox);
        AddTextEditingContextMenu(ReviewFileTextBox);
        AddTextEditingContextMenu(Fb2FileTextBox);
        ContextsListBox.Loaded += (_, _) => AttachContextMenusToSamples();
        ContextsListBox.ItemContainerGenerator.StatusChanged += (_, _) => AttachContextMenusToSamples();
        ChooseReviewButton.Click += ChooseReview_Click;
        ChooseFb2Button.Click += ChooseFb2_Click;
        LoadFilesButton.Click += LoadFiles_Click;
        OpenCorrectionsButton.Click += OpenCorrections_Click;
        ApplyAllButton.Click += ApplyAll_Click;
        PreviousButton.Click += Previous_Click;
        NextButton.Click += Next_Click;
        AddFormButton.Click += AddForm_Click;
        SaveCorrectionButton.Click += SaveCorrection_Click;
        ConfirmStressButton.Click += ConfirmStress_Click;
        ResetWordButton.Click += ResetToOriginalWord_Click;
        // Большой редактор: слово правится прямо в тексте, кнопки ударения — крупные.
        FragmentEditor.TextChanged += FragmentEditor_TextChanged;
        FontLargerButton.Click += (_, _) => ChangeReviewFontSize(2);
        FontSmallerButton.Click += (_, _) => ChangeReviewFontSize(-2);
        LlmCheckButton.Click += LlmCheck_Click;
        UnrecognizedWordsListBox.MouseDoubleClick += (_, _) => JumpToManualReview();
        InitializeLlmEngines();
        PreviewKeyDown += HandleReviewNavigationKey;
        WordGroupsListBox.PreviewKeyDown += HandleReviewNavigationKey;
        ContextsListBox.PreviewKeyDown += HandleReviewNavigationKey;
        if (_reviewPath != null && File.Exists(_reviewPath)) LoadReviewFiles();
        else UpdateEditorEnabled();
    }

    /// <summary>Подставляет файлы отчёта и результата и загружает их в редактор.</summary>
    internal void SetReviewPaths(string? reviewPath, string? outputPath)
    {
        if (!string.IsNullOrWhiteSpace(reviewPath)) { _reviewPath = reviewPath; ReviewFileTextBox.Text = reviewPath!; }
        if (!string.IsNullOrWhiteSpace(outputPath)) { _outputPath = outputPath; Fb2FileTextBox.Text = outputPath!; }
        if (_reviewPath != null && File.Exists(_reviewPath)) LoadReviewFiles();
        else { RefreshWordCounts(); ApplyReviewItems(); UpdateEditorEnabled(); }
    }

    /// <summary>Обновляет оба списка слов: полный (вкладка 3) и краткий (вкладка 2).</summary>
    private void ApplyReviewItems()
    {
        WordGroupsListBox.ItemsSource = BuildReviewItems();
        UnrecognizedWordsListBox.ItemsSource = BuildUnrecognizedItems();
    }

    // Краткий список нераспознанных слов для вкладки ИИ: без поиска, всегда по частоте.
    private List<ReviewItem> BuildUnrecognizedItems() =>
        _entries.Select((entry, index) => (entry, index))
            .GroupBy(pair => ExactWordKey(pair.entry.Word), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var count = _wordCounts.TryGetValue(first.entry.Word, out var found) ? found : 0;
                var hint = _llmSuggestions.ContainsKey(ExactWordKey(first.entry.Word)) ? "  ·  есть подсказка ИИ" : "";
                return new ReviewItem(first.index, $"{first.entry.Word} — {count:N0} вхождений{hint}", first.entry.Word, count);
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // Двойной щелчок по слову в кратком списке открывает его на вкладке ручной доводки.
    private void JumpToManualReview()
    {
        if (UnrecognizedWordsListBox.SelectedItem is not ReviewItem item) return;
        ReviewTabs.SelectedIndex = 2;
        var found = WordGroupsListBox.Items.Cast<ReviewItem>()
            .FirstOrDefault(candidate => ExactWordKey(candidate.Word) == ExactWordKey(item.Word));
        if (found != null) { WordGroupsListBox.SelectedItem = found; WordGroupsListBox.ScrollIntoView(found); }
    }

    private void RefreshWordCounts()
    {
        _wordCounts = !string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath)
            ? Program.CountReviewWordOccurrences(_outputPath, _entries.Select(entry => entry.Word))
            : new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private List<ReviewItem> BuildReviewItems()
    {
        var filter = SearchTextBox.Text.Trim();
        var groups = _entries.Select((entry, index) => (entry, index))
            .GroupBy(pair => ExactWordKey(pair.entry.Word), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var count = _wordCounts.TryGetValue(first.entry.Word, out var found) ? found : 0;
                var key = ExactWordKey(first.entry.Word);
                var hint = _llmSuggestions.TryGetValue(key, out var suggestion)
                    ? $"  ·  ИИ: {suggestion}"
                    : _llmRejected.TryGetValue(key, out var rejected)
                        ? string.IsNullOrEmpty(rejected.Raw) ? "  ·  ИИ: нет ответа" : $"  ·  ИИ ответил: {rejected.Raw} (проверьте)"
                        : string.Empty;
                return new ReviewItem(first.index, $"{first.entry.Word} — {count:N0} вхождений{hint}", first.entry.Word, count);
            })
            .Where(item => string.IsNullOrEmpty(filter) || _entries.Any(entry => ExactWordKey(entry.Word) == ExactWordKey(item.Word) &&
                (entry.Word.Contains(filter, StringComparison.OrdinalIgnoreCase) || entry.Context.Contains(filter, StringComparison.OrdinalIgnoreCase))));
        return (SortModeComboBox.SelectedIndex == 0
            ? groups.OrderBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)
            : groups.OrderByDescending(item => _wordCounts.TryGetValue(item.Word, out var count) ? count : 0).ThenBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    private void RebuildGroupList()
    {
        var selectedWord = (WordGroupsListBox.SelectedItem as ReviewItem)?.Word;
        ApplyReviewItems();
        if (selectedWord != null)
        {
            var found = WordGroupsListBox.Items.Cast<ReviewItem>().FirstOrDefault(item => ExactWordKey(item.Word) == ExactWordKey(selectedWord));
            if (found != null) WordGroupsListBox.SelectedItem = found;
        }
        if (WordGroupsListBox.SelectedItem == null && WordGroupsListBox.Items.Count > 0) WordGroupsListBox.SelectedIndex = 0;
        UpdateNavigationState();
    }

    private void ChooseReview_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите файл нераспознанных слов", Filter = "Отчёт слов (*.txt)|*.txt|Все файлы (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _reviewPath = dialog.FileName;
        ReviewFileTextBox.Text = _reviewPath;
        LoadReviewFiles();
    }

    private void ChooseFb2_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите FB2 или TXT для ручной коррекции", Filter = "Книги и тексты (*.fb2;*.txt)|*.fb2;*.txt|Книга FictionBook (*.fb2)|*.fb2|Текстовый файл (*.txt)|*.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _outputPath = dialog.FileName;
        Fb2FileTextBox.Text = _outputPath;
        RefreshWordCounts();
        ApplyReviewItems();
        UpdateEditorEnabled();
    }

    private void OpenCorrections_Click(object sender, RoutedEventArgs e) => OpenDeclensionFile();
    private void AddForm_Click(object sender, RoutedEventArgs e) => AddDeclensionRow();
    private void ApplyAll_Click(object sender, RoutedEventArgs e) => ApplyAllCorrections();
    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateGroup(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateGroup(1);
    private void SaveCorrection_Click(object sender, RoutedEventArgs e) => ApplyGroupCorrection();
    private void LoadFiles_Click(object sender, RoutedEventArgs e) => LoadReviewFiles();
    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildGroupList();
    private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildGroupList();

    private async void LlmCheck_Click(object sender, RoutedEventArgs e) => await RunLlmCheckAsync();

    // --- Движок подсказок: своя модель на диске, свой сервер Ollama или внешний API ----------
    // Настройки сохраняются сразу при изменении: их читают и окно, и командная строка.
    private static readonly (string Title, string Value)[] LlmProviders =
    {
        ("Своя модель на диске (ONNX)", Program.LlmEngine.Local),
        ("Ollama — свой сервер", Program.LlmEngine.Ollama),
        ("Внешний API (OpenAI-совместимый)", Program.LlmEngine.OpenAi),
    };

    private void InitializeLlmEngines()
    {
        var settings = Program.LoadLlmSettings();
        _updatingLlmEngine = true;
        LlmProviderComboBox.ItemsSource = LlmProviders.Select(item => item.Title).ToArray();
        var providerIndex = Array.FindIndex(LlmProviders, item => item.Value == _llmEngine.Provider);
        LlmProviderComboBox.SelectedIndex = providerIndex < 0 ? 0 : providerIndex;
        LlmOllamaUrlTextBox.Text = string.IsNullOrWhiteSpace(settings.OllamaUrl) ? "http://127.0.0.1:11434" : settings.OllamaUrl;
        LlmApiUrlTextBox.Text = string.IsNullOrWhiteSpace(settings.ApiUrl) ? "https://api.openai.com/v1" : settings.ApiUrl;
        LlmApiKeyTextBox.Text = settings.ApiKey;
        LlmOllamaModelComboBox.Text = settings.OllamaModel;
        LlmApiModelComboBox.Text = settings.ApiModel;
        LlmReuseCheckBox.IsChecked = settings.ReuseOtherEngines;
        RefreshLocalModels();
        _updatingLlmEngine = false;
        UpdateLlmPanels();
        ShowBenchmarkResult();

        LlmProviderComboBox.SelectionChanged += (_, _) => { UpdateLlmPanels(); SaveLlmEngine(); };
        LlmModelComboBox.SelectionChanged += (_, _) => SaveLlmEngine();
        LlmLocalRefreshButton.Click += (_, _) => RefreshLocalModels();
        LlmOllamaRefreshButton.Click += (_, _) => RefreshRemoteModels(Program.LlmEngine.Ollama, LlmOllamaModelComboBox, LlmOllamaRefreshButton);
        LlmApiRefreshButton.Click += (_, _) => RefreshRemoteModels(Program.LlmEngine.OpenAi, LlmApiModelComboBox, LlmApiRefreshButton);
        LlmOllamaUrlTextBox.LostFocus += (_, _) => SaveLlmEngine();
        LlmOllamaModelComboBox.LostFocus += (_, _) => SaveLlmEngine();
        LlmApiUrlTextBox.LostFocus += (_, _) => SaveLlmEngine();
        LlmApiKeyTextBox.LostFocus += (_, _) => SaveLlmEngine();
        LlmApiModelComboBox.LostFocus += (_, _) => SaveLlmEngine();
        LlmReuseCheckBox.Checked += (_, _) => SaveLlmEngine();
        LlmReuseCheckBox.Unchecked += (_, _) => SaveLlmEngine();
        LlmBenchmarkButton.Click += LlmBenchmark_Click;
    }

    private string CurrentProvider() =>
        LlmProviderComboBox.SelectedIndex >= 0 && LlmProviderComboBox.SelectedIndex < LlmProviders.Length
            ? LlmProviders[LlmProviderComboBox.SelectedIndex].Value
            : Program.LlmEngine.Local;

    private void UpdateLlmPanels()
    {
        var provider = CurrentProvider();
        LlmLocalPanel.Visibility = provider == Program.LlmEngine.Local ? Visibility.Visible : Visibility.Collapsed;
        LlmOllamaPanel.Visibility = provider == Program.LlmEngine.Ollama ? Visibility.Visible : Visibility.Collapsed;
        LlmApiPanel.Visibility = provider == Program.LlmEngine.OpenAi ? Visibility.Visible : Visibility.Collapsed;
        RefreshLlmEngineText();
    }

    private void RefreshLlmEngineText() =>
        LlmEngineTextBlock.Text = "Сейчас выбран: " + _llmEngine.Title +
            ". Подсказки считаются отдельно для каждого движка и не смешиваются.";

    // Настройки сохраняются по любым изменениям: следующее открытие окна и командная строка
    // увидят то же самое.
    private void SaveLlmEngine()
    {
        if (_updatingLlmEngine) return;
        var provider = CurrentProvider();
        var local = LlmModelComboBox.SelectedItem as string ?? _llmEngine.LocalModel;
        Program.SaveLlmSettings(provider, local, LlmOllamaUrlTextBox.Text.Trim(), LlmOllamaModelComboBox.Text.Trim(),
            LlmApiUrlTextBox.Text.Trim(), LlmApiModelComboBox.Text.Trim(), LlmApiKeyTextBox.Text.Trim(),
            LlmReuseCheckBox.IsChecked == true);
        var previous = _llmEngine;
        _llmEngine = Program.CurrentLlmEngine();
        RefreshLlmEngineText();
        if (previous.Provider == _llmEngine.Provider && previous.Id == _llmEngine.Id) return;
        // Уже найденные подсказки оставляем: они верны независимо от движка. Новый движок
        // берётся только за слова, для которых подсказки ещё нет.
        FileStatusTextBlock.Text =
            $"Движок подсказок переключён: {previous.Title} → {_llmEngine.Title}." + Environment.NewLine +
            "Нажмите «Подсказки ИИ по контексту»: уже найденные подсказки останутся, а новый движок" +
            " займётся только оставшимися словами (у каждого движка свой список ответов).";
    }

    // Локальные модели: всё, что лежит в ресурсы\модели\llm и готово к запуску.
    private void RefreshLocalModels()
    {
        var models = Program.DiscoverLlmModels();
        var current = _llmEngine.LocalModel;
        if (!models.Any(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase)))
            models.Insert(0, current);
        var wasUpdating = _updatingLlmEngine;
        _updatingLlmEngine = true;
        LlmModelComboBox.ItemsSource = models;
        LlmModelComboBox.SelectedItem =
            models.FirstOrDefault(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
        _updatingLlmEngine = wasUpdating;
    }

    // Список моделей у внешнего сервиса: Ollama отдаёт загруженные модели, API — доступные.
    private async void RefreshRemoteModels(string provider, ComboBox target, Button button)
    {
        target.IsEnabled = false;
        button.IsEnabled = false;
        button.Content = "Спрашиваю…";
        var engine = BuildEngine(provider);
        var error = "";
        try
        {
            var models = await Task.Run(() =>
            {
                var list = Program.ListRemoteLlmModels(engine, out var problem);
                error = problem;
                return list;
            });
            if (error.Length > 0)
            {
                FileStatusTextBlock.Text = "Не удалось получить список моделей: " + error;
                return;
            }
            var current = target.Text;
            target.ItemsSource = models;
            target.Text = models.Contains(current, StringComparer.OrdinalIgnoreCase)
                ? current
                : models.FirstOrDefault() ?? current;
            FileStatusTextBlock.Text = models.Count == 0
                ? "Сервис не сообщил ни одной модели. Проверьте адрес и наличие загруженных моделей."
                : $"Сервис сообщил моделей: {models.Count}. Выберите нужную в списке «Модель».";
            SaveLlmEngine();
        }
        catch (Exception problem)
        {
            Program.LogError("Список моделей внешнего сервиса", problem);
            FileStatusTextBlock.Text = "Список моделей не получен: " + problem.Message;
        }
        finally
        {
            target.IsEnabled = true;
            button.IsEnabled = true;
            button.Content = "Список моделей";
        }
    }

    // Движок по текущему состоянию полей — для проверки списка моделей и теста производительности,
    // когда настройки ещё не успели сохраниться.
    private Program.LlmEngine BuildEngine(string provider) => provider switch
    {
        Program.LlmEngine.Ollama => new Program.LlmEngine(provider, _llmEngine.LocalModel,
            LlmOllamaModelComboBox.Text.Trim(), LlmOllamaUrlTextBox.Text.Trim(), ""),
        Program.LlmEngine.OpenAi => new Program.LlmEngine(provider, _llmEngine.LocalModel,
            LlmApiModelComboBox.Text.Trim(), LlmApiUrlTextBox.Text.Trim(), LlmApiKeyTextBox.Text.Trim()),
        _ => new Program.LlmEngine(provider,
            LlmModelComboBox.SelectedItem as string ?? _llmEngine.LocalModel, "", "", ""),
    };

    private async void LlmBenchmark_Click(object sender, RoutedEventArgs e)
    {
        SaveLlmEngine();
        var engine = BuildEngine(CurrentProvider());
        LlmBenchmarkButton.IsEnabled = false;
        var progress = new LlmProgressWindow(
            "Проверяю движок на контрольных словах с известным ударением. Это занимает столько же времени, сколько несколько десятков слов.");
        progress.Owner = this;
        progress.Show();
        try
        {
            var result = await Task.Run(() => Program.RunLlmBenchmark(engine,
                message => Dispatcher.Invoke(() => { if (progress.IsLoaded) progress.SetStatus(message); }), progress.Token));
            if (!result.Ok)
            {
                if (progress.Token.IsCancellationRequested) return;
                MessageBox.Show(this, result.Message, "Проверка движка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Program.SaveLlmBenchmark(result);
            Program.LogReview($"Тест движка: {result.Title} — {result.Correct} из {result.Total} верных ударений за {result.Seconds:0} с.");
            ShowBenchmarkResult();
            var text = new StringBuilder();
            text.AppendLine($"Движок: {result.Title}.");
            text.AppendLine($"Верных ударений: {result.Correct} из {result.Total}; время: {result.Seconds:0} с " +
                            $"({result.Seconds / Math.Max(1, result.Total):0.0} с на слово).");
            text.AppendLine();
            foreach (var line in result.Details)
            {
                var parts = line.Split('\t');
                text.AppendLine(parts[0] == "верно"
                    ? $"   ✓ {parts[1]} — {parts[2]}"
                    : $"   ✗ {parts[1]} — верно {parts[2]}, получено {parts[3]}");
            }
            text.AppendLine();
            text.AppendLine(Program.LlmBenchmarkAdvice());
            MessageBox.Show(this, text.ToString(), "Проверка движка: скорость и точность",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception problem)
        {
            Program.LogError("Тест движка подсказок", problem);
            MessageBox.Show(this, "Тест движка не удался: " + problem.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progress.Close();
            LlmBenchmarkButton.IsEnabled = true;
        }
    }

    private void ShowBenchmarkResult()
    {
        var advice = Program.LlmBenchmarkAdvice();
        LlmBenchmarkTextBlock.Text = advice ?? "Тест покажет, сколько ударений движок угадает и сколько времени это займёт.";
    }

    // Локальный ИИ-проход по контексту. Языковая модель на этом же компьютере (или внешний сервис)
    // подбирает ударение для слов, которых нет ни в словаре RUAccent, ни в пользовательских
    // ударениях. Результат — только подсказки: каждая принимается отдельно кнопкой «Да».
    private async Task RunLlmCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath) || !File.Exists(_reviewPath))
        {
            MessageBox.Show(this, "Сначала выберите файл отчёта со словами для проверки.", "Нужен отчёт", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, "В отчёте нет слов для проверки.", "Нечего проверять", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groups = _entries
            .GroupBy(entry => ExactWordKey(entry.Word), StringComparer.Ordinal)
            .Select(group => group.ToArray())
            .ToList();
        var book = !string.IsNullOrWhiteSpace(_outputPath) ? _outputPath! : _reviewPath!;
        var cache = Program.LoadLlmSuggestions();
        var failures = Program.LoadLlmFailures();
        var reuse = LlmReuseCheckBox.IsChecked == true;
        var pending = new List<Program.LlmStressItem>();
        var reused = 0;
        var skipped = 0;
        foreach (var group in groups)
        {
            var word = group[0].Word;
            var key = ExactWordKey(word);
            // Свой ответ — в этом проходе или из кэша — повторно не нужен: модель работает
            // без случайности и вернула бы то же самое.
            if (_llmSuggestions.ContainsKey(key)) { reused++; continue; }
            if (cache.TryGetValue(Program.LlmCacheKey(_llmEngine.Id, book, word), out var ownAnswer) && Program.IsValidCorrection(word, ownAnswer))
            {
                _llmSuggestions[key] = ownAnswer;
                reused++;
                continue;
            }
            // Подсказка другого движка годится, пока не снята галочка «не переспрашивать»:
            // тогда выбранный движок проверяет такие слова заново — ради второго мнения.
            if (reuse && Program.FindAnyLlmSuggestion(cache, book, word) is { } fromOtherEngine && Program.IsValidCorrection(word, fromOtherEngine))
            {
                _llmSuggestions[key] = fromOtherEngine;
                reused++;
                continue;
            }
            // Слова, по которым этот движок уже не смог ответить, повторно не отправляются:
            // генерация без случайности, повтор дал бы тот же результат, только снова минуты.
            if (failures.TryGetValue(Program.LlmCacheKey(_llmEngine.Id, book, word), out var failed))
            {
                _llmRejected[key] = new Program.LlmRejected(failed.Raw, failed.Reason);
                skipped++;
                continue;
            }
            var contexts = group.Select(item => item.Context).Where(context => !string.IsNullOrWhiteSpace(context)).Distinct().Take(2).ToArray();
            pending.Add(new Program.LlmStressItem(word, contexts));
        }

        if (pending.Count > 0)
        {
            LlmCheckButton.IsEnabled = false;
            var progress = new LlmProgressWindow();
            progress.Owner = this;
            progress.Show();
            try
            {
                var items = pending;
                var outcome = await Task.Run(() => Program.RunLlmStressCheck(items, book,
                    message => Dispatcher.Invoke(() => { if (progress.IsLoaded) progress.SetStatus(message); }), progress.Token));
                if (!outcome.Ok)
                {
                    if (progress.Token.IsCancellationRequested) _llmStatus = "ИИ-проход прерван пользователем.";
                    else
                    {
                        Program.LogReview("ИИ-проход не выполнен: " + outcome.Message);
                        MessageBox.Show(this, outcome.Message, "ИИ-проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    foreach (var pair in outcome.Suggestions)
                    {
                        var suggestionKey = ExactWordKey(pair.Key);
                        _llmSuggestions[suggestionKey] = pair.Value;
                        // Если раньше по слову ответа не было, а теперь он есть — старая пометка не нужна.
                        _llmRejected.Remove(suggestionKey);
                        cache[Program.LlmCacheKey(_llmEngine.Id, book, pair.Key)] = pair.Value;
                    }
                    foreach (var pair in outcome.Rejected)
                    {
                        _llmRejected[ExactWordKey(pair.Key)] = pair.Value;
                        failures[Program.LlmCacheKey(_llmEngine.Id, book, pair.Key)] =
                            new Program.LlmFailure(pair.Key, pair.Value.Raw, pair.Value.Reason);
                    }
                    Program.SaveLlmSuggestions(cache);
                    Program.SaveLlmFailures(failures);
                    _llmStatus = $"ИИ: подсказок {outcome.Answered} из {outcome.Items}; движок: {outcome.Device}; {outcome.Seconds:0} с.";
                    Program.LogReview(_llmStatus + " Журнал: " + Program.LlmLogPath);
                    ShowLlmSummary(outcome, skipped);
                }
            }
            catch (Exception error)
            {
                Program.LogError("ИИ-проверка остатка", error);
                MessageBox.Show(this, "ИИ-проверка не удалась: " + error.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progress.Close();
                LlmCheckButton.IsEnabled = true;
            }
        }

        RebuildGroupList();
        // Сразу показываем первую группу с готовой подсказкой: иначе результат ИИ легко не заметить.
        var listed = WordGroupsListBox.Items.Cast<ReviewItem>().ToArray();
        var firstAnswered = listed.FirstOrDefault(item => _llmSuggestions.ContainsKey(ExactWordKey(item.Word)))
            ?? listed.FirstOrDefault(item => _llmRejected.ContainsKey(ExactWordKey(item.Word)));
        if (firstAnswered != null) WordGroupsListBox.SelectedItem = firstAnswered;
        LlmCheckButton.Content = _llmSuggestions.Count > 0
            ? $"Подсказки ИИ ({_llmSuggestions.Count})"
            : "Подсказки ИИ по контексту";
        Program.LogReview($"ИИ-подсказки в окне: принято {_llmSuggestions.Count}, без ответа {_llmRejected.Count}, " +
            $"пропущено {skipped}, групп в списке {listed.Length}. Журнал: {Program.LlmLogPath}");
        var summary = $"Подсказок ИИ: {_llmSuggestions.Count} (групп слов {groups.Count}"
            + (reused > 0 ? $", из кэша {reused}" : string.Empty)
            + (skipped > 0 ? $", без ответа ранее {skipped}" : string.Empty) + $"; движок {_llmEngine.Title}).";
        FileStatusTextBlock.Text = _llmStatus.Length > 0
            ? summary + Environment.NewLine + _llmStatus
            : summary + Environment.NewLine + "Подсказка видна сверху над полем «Исправленный вариант»; подтверждайте по одной кнопкой «Да».";
    }

    // Короткий отчёт после ИИ-прохода: сколько слов получило подсказку, почему по остальным
    // применить нечего и где лежит подробный журнал по каждому слову.
    private void ShowLlmSummary(Program.LlmStressOutcome outcome, int skipped)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Движок: {_llmEngine.Title}.");
        text.AppendLine($"Подсказка получена по {outcome.Answered} словам из {outcome.Items}.");
        text.AppendLine();
        if (outcome.Answered > 0)
            text.AppendLine("Слова с подсказкой помечены в списке слов как «· ИИ: …», вариант уже стоит");
        if (outcome.Answered > 0)
            text.AppendLine("в поле «Исправленный вариант» — проверяйте и нажимайте «Да ✓».");
        var reasons = outcome.Rejected.GroupBy(pair => ReasonLabel(pair.Value.Reason))
            .OrderByDescending(group => group.Count()).ToArray();
        if (reasons.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("По остальным словам ответа нет:");
            foreach (var group in reasons) text.AppendLine($"  • {group.Key}: {group.Count()}");
        }
        if (skipped > 0)
        {
            text.AppendLine();
            text.AppendLine($"{skipped} слов уже проверялись ранее и остались без ответа — повторно они не отправлялись.");
            text.AppendLine("Чтобы попробовать их снова, удалите файл " + Program.LlmFailuresPath);
        }
        text.AppendLine();
        text.AppendLine("Подтверждайте подсказки по одной кнопкой «Да ✓», затем «Применить всё и сохранить».");
        text.AppendLine("Подробный журнал: " + Program.LlmLogPath);
        MessageBox.Show(this, text.ToString(), "ИИ-проверка ударений", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string ReasonLabel(string reason) => reason switch
    {
        "form_mismatch" => "модель ответила другой формой слова",
        "other_word" => "модель назвала другое слово",
        "none" or "" => "модель не ответила",
        _ => reason,
    };

    // Небольшое немодальное окно: показывает, что делает модель, позволяет прервать проход
    // и раз в секунду выводит нагрузку на процессор, память и видеокарту.
    private sealed class LlmProgressWindow : Window
    {
        private readonly TextBlock _status;
        private readonly TextBlock _load;
        private readonly DispatcherTimer _timer;
        private readonly LoadMonitor _monitor = new();
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken Token => _cancellation.Token;

        public LlmProgressWindow(string headline = "Выбранный движок подбирает ударение для слов, которых не было в словаре. Это занимает несколько минут и нагружает видеокарту.")
        {
            Title = "ИИ-проверка ударений";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xED, 0xDD));
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = headline,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 14, 0, 8) });
            _status = new TextBlock { Text = "Запускаю модель…", TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_status);
            _load = new TextBlock
            {
                Text = "Нагрузка: считаю…",
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x5A, 0x45)),
            };
            panel.Children.Add(_load);
            var cancel = new Button
            {
                Content = "Прервать",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(14, 4, 14, 4),
            };
            cancel.Click += (_, _) => { _cancellation.Cancel(); _status.Text = "Прерываю…"; };
            panel.Children.Add(cancel);
            Content = panel;

            // Мониторинг нагрузки: обновляем раз в секунду, пока идёт проход.
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                if (!IsLoaded) return;
                try { _load.Text = "Нагрузка: " + _monitor.Describe(); }
                catch { /* мониторинг не критичен для прохода */ }
            };
            _timer.Start();
        }

        public void SetStatus(string message) => _status.Text = message;

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _monitor.Dispose();
            _cancellation.Cancel();
            base.OnClosed(e);
        }
    }

    private void WordGroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WordGroupsListBox.SelectedItem is ReviewItem selected) SelectGroup(selected.Index);
        UpdateNavigationState();
        UpdateEditorEnabled();
    }

    private void ContextsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rebuildingSamples && ContextsListBox.SelectedItem is SampleItem sample) SelectSample(sample.Index);
    }

    private void Correction_TextChanged(object sender, TextChangedEventArgs e) => CorrectionChanged();

    private void SampleInclude_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SampleItem sample }) SetSampleIncluded(sample.Index, !sample.Included);
    }

    private void OpenDeclensionFile()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return;
        var path = Program.FormCorrectionsPathFor(_reviewPath);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Файл словоформ пока не создан. Он появится после применения исправления к группе:\n{path}", "Файл словоформ ещё не создан", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateEditorEnabled();
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть файл словоформ: " + ex.Message, "Ошибка открытия", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LoadReviewFiles()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath) || !File.Exists(_reviewPath))
        {
            MessageBox.Show(this, "Выберите существующий файл отчёта нераспознанных слов.", "Нет отчёта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_outputPath) && !File.Exists(_outputPath))
        {
            MessageBox.Show(this, "Выбранный FB2-файл не найден.", "Нет FB2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _entries.Clear();
            _entries.AddRange(Program.ReadWordReview(_reviewPath).Select(entry => new MutableReviewEntry(entry)));
            RefreshWordCounts();
            _selectedGroupAnchor = -1;
            _selectedSampleIndex = -1;
            ApplyReviewItems();
            FileStatusTextBlock.Text = _entries.Count == 0 ? "Отчёт пуст или не содержит слов для проверки." : $"Загружено случаев: {_entries.Count}; точных групп слов: {WordGroupsListBox.Items.Count}. Сортировка: {SortModeComboBox.SelectedItem}.";
            WordGroupsListBox.Focusable = true;
            WordGroupsListBox.IsTabStop = true;
            ContextTextBlock.Text = "Выберите группу слова слева: будет отредактирована вся группа.";
            if (WordGroupsListBox.Items.Count > 0) WordGroupsListBox.SelectedIndex = 0;
            UpdateEditorEnabled();
            UpdateNavigationState();
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть отчёт: " + ex.Message, "Ошибка чтения", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CorrectionChanged()
    {
        if (_updatingCorrection || WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return;
        var corrected = CorrectionTextBox.Text;
        foreach (var entry in GetSelectedGroup()) entry.Proposed = corrected;
        // Если изменились буквы (исправили опечатку), кнопки ударных гласных должны
        // отражать новое написание, иначе ударение ставилось бы на исходное слово и стирало правку.
        var letters = PlainLetters(corrected);
        var lettersChanged = !string.Equals(letters, _workingWord, StringComparison.OrdinalIgnoreCase);
        _workingWord = letters;
        if (lettersChanged)
        {
            _updatingCorrection = true;
            RebuildStressChoices();
            _updatingCorrection = false;
            ContextTextBlock.Text = "Написание изменено (исправление опечатки). Теперь выберите ударную гласную — ударение встанет на исправленное слово.";
        }
        else
        {
            ContextTextBlock.Text = "Правка применяется ко всей выбранной группе. Снимите галочку у образца, чтобы оставить его без изменений.";
        }
        // Отражение правки в большом редакторе. Если текст ввели прямо там, обновлять не нужно.
        if (!_wordEditFromEditor) UpdateEditorWord();
    }

    private static string PlainLetters(string word) => word.Replace("\u0301", string.Empty);

    // Сравнение букв без учёта ударения и замены е↔ё: только реальная правка опечатки
    // считается изменением написания и вызывает вопрос «сохранить?».
    private static bool SameLettersApartFromYo(string left, string right) =>
        FoldLetters(left).Equals(FoldLetters(right), StringComparison.Ordinal);

    private static string FoldLetters(string value) =>
        value.Replace("\u0301", string.Empty).Replace('ё', 'е').Replace('Ё', 'Е').ToLowerInvariant();

    // Текст большого редактора: «до» + проверяемое слово (жирным, с ударением) + «после».
    private void RenderFragment(string before, string word, string after)
    {
        _fragmentBefore = before;
        _fragmentAfter = after;
        _updatingFragment = true;
        try
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            paragraph.Inlines.Add(new Run(before));
            _wordRun = new Run(word)
            {
                // Проверяемое слово должно сразу бросаться в глаза: крупнее, жирнее и с подсветкой.
                FontWeight = FontWeights.Black,
                FontSize = ReviewWordFontSize,
                Foreground = TryFindResource("Accent") as Brush ?? Brushes.SaddleBrown,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xD6)),
            };
            paragraph.Inlines.Add(_wordRun);
            paragraph.Inlines.Add(new Run(after));
            FragmentEditor.Document.Blocks.Clear();
            FragmentEditor.Document.Blocks.Add(paragraph);
            FragmentEditor.FontSize = _reviewFontSize;
        }
        finally
        {
            _updatingFragment = false;
        }
    }

    // Слово — это жирный фрагмент текста. Если пользователь стёр его целиком, ищем любой жирный Run.
    private Run? CurrentWordRun()
    {
        if (_wordRun is { Parent: not null }) return _wordRun;
        var found = FragmentEditor.Document.Blocks.OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
            .FirstOrDefault(run => run.FontWeight == FontWeights.Bold);
        if (found != null) _wordRun = found;
        return found;
    }

    private void UpdateEditorWord()
    {
        var wordRun = CurrentWordRun();
        if (wordRun == null) return;
        if (string.Equals(wordRun.Text, CorrectionTextBox.Text, StringComparison.Ordinal)) return;
        _updatingFragment = true;
        try { wordRun.Text = CorrectionTextBox.Text; }
        finally { _updatingFragment = false; }
    }

    private string DocumentText() =>
        new TextRange(FragmentEditor.Document.ContentStart, FragmentEditor.Document.ContentEnd).Text.TrimEnd('\r', '\n');

    // Слово — это текст между сохранёнными частями «до» и «после». Если контекст стёрли
    // (например, выделили всё и напечатали заново), словом считается весь оставшийся
    // текст, если это одно слово без пробелов. Перерисовки нет, чтобы не сбивать ввод.
    private string ExtractWordFromDocument(string doc)
    {
        if (doc.Length >= _fragmentBefore.Length + _fragmentAfter.Length
            && doc.StartsWith(_fragmentBefore, StringComparison.Ordinal)
            && doc.EndsWith(_fragmentAfter, StringComparison.Ordinal))
            return doc.Substring(_fragmentBefore.Length, doc.Length - _fragmentBefore.Length - _fragmentAfter.Length);
        var trimmed = doc.Trim();
        return trimmed.Length > 0 && !trimmed.Any(char.IsWhiteSpace) ? trimmed : string.Empty;
    }

    private void FragmentEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingFragment) return;
        var text = ExtractWordFromDocument(DocumentText());
        if (string.IsNullOrEmpty(text)) return;
        if (string.Equals(text, CorrectionTextBox.Text, StringComparison.Ordinal)) return;
        // Написание ввели прямо в тексте — переносим его в рабочее поле (источник правки).
        // Редактор при этом не трогаем, чтобы не сбивать позицию курсора.
        _wordEditFromEditor = true;
        try { CorrectionTextBox.Text = text; }
        finally { _wordEditFromEditor = false; }
    }

    // Полный фрагмент выбранного образца переносится в редактор целиком.
    private void RenderSelectedSample()
    {
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index < 0 || selected.Index >= _entries.Count) return;
        var wordKey = ExactWordKey(_entries[selected.Index].Word);
        var index = _selectedSampleIndex;
        if (index < 0 || index >= _entries.Count || ExactWordKey(_entries[index].Word) != wordKey)
            index = _entries.FindIndex(entry => ExactWordKey(entry.Word) == wordKey);
        if (index < 0) return;
        var entry = _entries[index];
        var sample = SampleItem.For(index, entry.Context, entry.Word, entry.Included);
        var word = CorrectionTextBox.Text.Length > 0 ? CorrectionTextBox.Text : DefaultInitialStress(entry.Word);
        RenderFragment(sample.Before, word, sample.After);
    }

    private void ChangeReviewFontSize(double delta)
    {
        _reviewFontSize = Math.Clamp(_reviewFontSize + delta, 11, 34);
        FragmentEditor.FontSize = _reviewFontSize;
        if (_wordRun != null) _wordRun.FontSize = ReviewWordFontSize;
        WordGroupsListBox.FontSize = Math.Max(11, _reviewFontSize - 2);
        ContextsListBox.FontSize = Math.Max(11, _reviewFontSize - 2);
    }

    /// <summary>Проверяемое слово в тексте всегда крупнее окружающего текста.</summary>
    private double ReviewWordFontSize => Math.Round(_reviewFontSize * 1.3, 1);

    private IEnumerable<MutableReviewEntry> GetSelectedGroup()
    {
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return Enumerable.Empty<MutableReviewEntry>();
        return _entries.Where(entry => ExactWordKey(entry.Word) == ExactWordKey(_entries[selected.Index].Word));
    }

    // Группы, для которых пользователь ввёл правку (по одной на каждое уникальное слово).
    private List<(MutableReviewEntry Entry, string Correction, MutableReviewEntry[] Group)> CollectEditedGroups()
    {
        return _entries.Select((entry, index) => (entry, index))
            .GroupBy(pair => ExactWordKey(pair.entry.Word), StringComparer.Ordinal)
            .Select<IEnumerable<(MutableReviewEntry entry, int index)>, (MutableReviewEntry Entry, string Correction, MutableReviewEntry[] Group)>(group =>
            {
                var pairs = group.ToArray();
                var correction = CorrectionTextFor(pairs[0].entry);
                return (pairs[0].entry, correction, pairs.Select(pair => pair.entry).ToArray());
            })
            .Where(item => item.Correction.Length > 0 && (Program.IsValidCorrection(item.Entry.Word, item.Correction) || Program.IsValidTypoCorrection(item.Entry.Word, item.Correction)))
            .ToList();
    }

    private static string CorrectionTextFor(MutableReviewEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Proposed)) return string.Empty;
        var proposed = entry.Proposed.Trim().Replace("ё\u0301", "ё", StringComparison.OrdinalIgnoreCase);
        // Вариант с ударением или ё — осознанная правка; также считаем правкой вариант с изменёнными буквами (опечатка).
        return proposed.Contains('\u0301') || proposed.Contains('ё', StringComparison.OrdinalIgnoreCase)
            || !Program.NormalizeWordLettersForReview(proposed).Equals(Program.NormalizeWordLettersForReview(entry.Word), StringComparison.OrdinalIgnoreCase)
            ? proposed : string.Empty;
    }

    private void ApplyGroupCorrection()
    {
        var edited = CollectEditedGroups();
        var savedForms = ReadSavedForms();
        if (edited.Count == 0 && savedForms.Count == 0)
        {
            MessageBox.Show(this, "Введите хотя бы одно исправление (ударение или «ё») либо сохраните словоформу.", "Нет исправлений", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_reviewPath) || string.IsNullOrWhiteSpace(_outputPath) || !File.Exists(_outputPath))
        {
            MessageBox.Show(this, "Выберите файл отчёта и редактируемый FB2 или TXT.", "Нужны файлы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var sourcePath = _outputPath;
        var correctedPath = GetCorrectedOutputPath(sourcePath);
        var isText = Path.GetExtension(sourcePath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        try
        {
            ConfirmOutputPath(sourcePath, correctedPath, "Перезапись corrected-файла");
            if (!string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(correctedPath) &&
                MessageBox.Show(this, $"Файл результата уже существует:\n{correctedPath}\n\nПересоздать его из выбранного исходного файла?", "Пересоздание corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var pattern = new List<Program.WordReviewPatternCorrection>();
            foreach (var group in edited)
            {
                var excluded = group.Group.Where(item => !item.Included).ToArray();
                if (excluded.Length == 0) pattern.Add(new Program.WordReviewPatternCorrection(group.Entry.Word, group.Correction, null, -1));
                else pattern.AddRange(excluded.Select(item => new Program.WordReviewPatternCorrection(group.Entry.Word, group.Correction, item.Context, item.ContextOccurrence)));
            }
            var replacements = pattern.Count == 0 ? 0 : (isText ? Program.ApplyReviewPatternCorrectionsToTextFile(sourcePath, correctedPath, pattern) : Program.ApplyReviewPatternCorrectionsToFb2(sourcePath, correctedPath, pattern));
            if (pattern.Count > 0)
            {
                _outputPath = correctedPath;
                Fb2FileTextBox.Text = correctedPath;
            }
            if (savedForms.Count > 0)
            {
                if (pattern.Count == 0 && !string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, correctedPath, overwrite: true);
                    _outputPath = correctedPath;
                    Fb2FileTextBox.Text = correctedPath;
                }
                replacements += (isText ? Program.ApplyWordCorrectionsToTextFile(_outputPath, _outputPath, savedForms) : Program.ApplyWordCorrectionsToFb2(_outputPath, _outputPath, savedForms));
            }
            _wordCounts = Program.CountReviewWordOccurrences(_outputPath, _entries.Select(item => item.Word));
            foreach (var group in edited)
            {
                group.Entry.Proposed = string.Empty;
                Program.SaveUserCorrection(group.Entry.ToEntry(), group.Correction);
                Program.SaveDeclensionForms(_reviewPath!, group.Entry.ToEntry(),
                    group.Group.Select(item => new Program.DeclensionCorrection(item.Word, group.Correction, "Исходное слово", item.Context)));
                _entries.RemoveAll(entry => ExactWordKey(entry.Word) == ExactWordKey(group.Entry.Word) && entry.Included);
            }
            foreach (var word in edited.Take(edited.Count - 1).Select(group => group.Entry.Word)) SaveRemainingReviewEntries(word);
            SaveRemainingReviewEntries(string.Empty);
            if (edited.Count > 0) ReloadReviewEntries();
            UpdateEditorEnabled();
            FileStatusTextBlock.Text = $"Пакетная правка применена.\nФайл: {_outputPath}\nГрупп слов: {edited.Count}; сохранённых словоформ к применению: {savedForms.Count}; заменено вхождений: {replacements}.";
            OfferToOpenResultFolders("Правки применены.",
                _outputPath,
                Program.FormCorrectionsPathFor(_reviewPath!),
                StatisticsPath,
                InputTextBox.Text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Подробности пишем в DSR-правки.log и DSR-errors.txt, чтобы было что разбирать.
            Program.LogError("Применение пакета правок", ex);
            Program.LogReview("Пакет правок не применён: " + ex.Message);
            ShowApplyError(ex, edited);
        }
    }

    /// <summary>Отчёт со статистикой: путь известен сразу после выбора результата конвертации.</summary>
    private string? StatisticsPath => !string.IsNullOrWhiteSpace(OutputTextBox.Text)
        ? Program.StatisticsPathFor(OutputTextBox.Text)
        : _diagnosticsPath;

    /// <summary>
    /// После применения правок предлагаем открыть папки с результатами. Если все созданные
    /// и обработанные файлы (и исходник) лежат в одной папке — открываем её один раз;
    /// иначе открываем каждую из папок.
    /// </summary>
    private void OfferToOpenResultFolders(string title, params string?[] files)
    {
        var folders = new List<string>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            string? directory;
            try { directory = Path.GetDirectoryName(Path.GetFullPath(file)); }
            catch { continue; }
            if (!string.IsNullOrEmpty(directory) && !folders.Any(existing => string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase)))
                folders.Add(directory);
        }
        if (folders.Count == 0) return;
        if (folders.Count == 1)
        {
            if (MessageBox.Show(this, title + "\n\nОткрыть папку с результатом?\n" + folders[0],
                    "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenFolder(folders[0]);
            return;
        }
        var list = string.Join("\n", folders.Select((folder, index) => "  " + (index + 1) + ". " + folder));
        if (MessageBox.Show(this,
                title + "\n\nСозданные и обработанные файлы оказались в разных папках:\n\n" + list + "\n\nОткрыть все эти папки?",
                "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        foreach (var folder in folders) OpenFolder(folder);
    }

    private static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); }
        catch (Exception error) { Program.LogError("Открытие папки " + folder, error); }
    }

    private void ShowApplyError(Exception ex, List<(MutableReviewEntry Entry, string Correction, MutableReviewEntry[] Group)> edited)
    {
        MessageBox.Show(this,
            "Не удалось применить исправление: " + ex.Message +
            $"\n\nПодробности записаны в журнал:\n{Program.ReviewLogPath}\n{Program.LogFilePath}" +
            $"\n\nПравок в пакете: {edited.Count}.",
            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        FileStatusTextBlock.Text = "Ошибка применения правок — подробности в журнале: " + Program.ReviewLogPath;
    }

    // Все сохранённые словоформы из файла правок — они применяются в том же пакете.
    private List<Program.DeclensionCorrection> ReadSavedForms()
    {
        try { return Program.ReadAllDeclensionForms(_reviewPath); }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось прочитать файл словоформ: " + ex.Message, "Ошибка чтения", MessageBoxButton.OK, MessageBoxImage.Error);
            return new List<Program.DeclensionCorrection>();
        }
    }

    private void ReloadReviewEntries()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath) || !File.Exists(_reviewPath)) return;
        var selectedWord = (WordGroupsListBox.SelectedItem as ReviewItem)?.Word;
        _entries.Clear();
        _entries.AddRange(Program.ReadWordReview(_reviewPath!).Select(entry => new MutableReviewEntry(entry)));
        RefreshWordCounts();
        ApplyReviewItems();
        var restored = selectedWord == null ? null : WordGroupsListBox.Items.Cast<ReviewItem>().FirstOrDefault(item => ExactWordKey(item.Word) == ExactWordKey(selectedWord));
        if (restored != null) WordGroupsListBox.SelectedItem = restored;
        else if (WordGroupsListBox.Items.Count > 0) WordGroupsListBox.SelectedIndex = 0;
        else
        {
            ContextsListBox.ItemsSource = null;
            StressChoicesPanel.Children.Clear();
            DeclensionRowsPanel.Children.Clear();
            _declensions.Clear();
            CorrectionTextBox.Text = string.Empty;
            ContextTextBlock.Text = "Все группы обработаны — список слов пуст.";
        }
        UpdateNavigationState();
    }

    private void ApplyAllEditedGroups()
    {
        ApplyGroupCorrection();
    }

    private void SaveRemainingReviewEntries(string correctedWord)
    {
        if (_reviewPath == null) return;
        Program.LogReview($"Обновление отчёта {Path.GetFileName(_reviewPath)}: группы слов закреплены");
        var correctedKey = ExactWordKey(correctedWord);
        var remaining = _entries.Where(entry => ExactWordKey(entry.Word) != correctedKey || !entry.Included).Select(entry => entry.ToEntry());
        File.WriteAllText(_reviewPath, Program.FormatWordReview(remaining), new UTF8Encoding(true));
    }

    private void CopyGroupText(IEnumerable<MutableReviewEntry> entries)
    {
        var list = entries.ToArray();
        var text = string.Join(Environment.NewLine, list.Select(entry => $"{entry.Word} — {entry.Context}"));
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); FileStatusTextBlock.Text = list.Length == 1 ? "Слово и контекст скопированы." : "Слова и контексты группы скопированы."; }
        catch (Exception ex) { FileStatusTextBlock.Text = "Не удалось скопировать текст: " + ex.Message; }
    }

    private void AddGoogleContextMenu(Control control, string word, string context)
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Копировать слово и контекст" };
        copy.Click += (_, _) =>
        {
            var entry = _entries.FirstOrDefault(item => item.Word == word && item.Context == context);
            if (entry != null) CopyGroupText(new[] { entry });
        };
        var google = new MenuItem { Header = "Открыть предложение в Google: куда поставить ударение?" };
        google.Click += (_, _) => OpenGoogleStressSearch(word, context);
        menu.Items.Add(copy);
        menu.Items.Add(google);
        control.ContextMenu = menu;
    }

    private void AttachContextMenusToSamples()
    {
        if (ContextsListBox.Items.Count == 0) return;
        foreach (var sample in ContextsListBox.Items.Cast<SampleItem>())
            if (ContextsListBox.ItemContainerGenerator.ContainerFromItem(sample) is ListBoxItem container)
                AddGoogleContextMenu(container, _entries[sample.Index].Word, sample.Context);
    }

    private static void AddTextEditingContextMenu(TextBox textBox)
    {
        var menu = new ContextMenu();
        foreach (var (label, command) in new[]
        {
            ("Отменить", ApplicationCommands.Undo), ("Вырезать", ApplicationCommands.Cut),
            ("Копировать", ApplicationCommands.Copy), ("Вставить", ApplicationCommands.Paste),
            ("Выделить всё", ApplicationCommands.SelectAll)
        }) menu.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = textBox });
        textBox.ContextMenu = menu;
    }

    private void SelectGroup(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count) return;
        var wordKey = ExactWordKey(_entries[entryIndex].Word);
        var group = _entries.Select((entry, index) => (entry, index)).Where(pair => ExactWordKey(pair.entry.Word) == wordKey).ToArray();
        if (group.Length == 0) return;
        _selectedGroupAnchor = group[0].index;
        if (!group.Any(pair => pair.index == _selectedSampleIndex)) _selectedSampleIndex = group.FirstOrDefault(pair => pair.entry.Included).index;
        var samples = group.Select(pair => SampleItem.For(pair.index, pair.entry.Context, pair.entry.Word, pair.entry.Included)).ToList();
        _rebuildingSamples = true;
        ContextsListBox.ItemsSource = samples;
        ContextsListBox.SelectedItem = samples.FirstOrDefault(sample => sample.Index == _selectedSampleIndex);
        _rebuildingSamples = false;
        AttachContextMenusToSamples();
        var selectedEntry = group[0].entry;
        var count = _wordCounts.TryGetValue(selectedEntry.Word, out var occurrences) ? occurrences : 0;
        ContextTextBlock.Text = $"Выбрана группа «{selectedEntry.Word}»: совпадений в тексте {count:N0}; контекстов {group.Length}.";
        SelectionCountTextBlock.Text = $"Включено образцов: {group.Count(pair => pair.entry.Included)} из {group.Length}";
        _updatingCorrection = true;
        var proposed = group.Select(pair => pair.entry.Proposed).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? selectedEntry.Word;
        // Подсказка ИИ подставляется только вместо «ударения по умолчанию»: если пользователь
        // уже ввёл свою правку, она важнее. Принимает подсказку всё равно пользователь кнопкой «Да».
        var suggestion = _llmSuggestions.TryGetValue(wordKey, out var llmSuggestion) ? llmSuggestion : null;
        if (suggestion != null && !Program.IsValidCorrection(selectedEntry.Word, suggestion)) suggestion = null;
        // Ответ модели другим словом (опечатка, старая форма, имя) принять как ударение нельзя,
        // но если он близок по буквам, его можно предложить как исправление написания — тоже вручную.
        var typoFix = suggestion != null || !_llmRejected.TryGetValue(wordKey, out var rejectedAnswer)
            || string.IsNullOrEmpty(rejectedAnswer.Raw) || !Program.IsValidTypoCorrection(selectedEntry.Word, rejectedAnswer.Raw)
            ? null : rejectedAnswer.Raw;
        var prefilled = false;
        if (!proposed.Contains('\u0301') && !proposed.Contains('ё', StringComparison.OrdinalIgnoreCase))
        {
            if (suggestion != null) { proposed = suggestion; prefilled = true; }
            else if (typoFix != null) { proposed = typoFix; prefilled = true; }
        }
        CorrectionTextBox.Text = proposed.Contains('\u0301') || proposed.Contains('ё', StringComparison.OrdinalIgnoreCase) ? proposed : DefaultInitialStress(proposed);
        _updatingCorrection = false;
        var hint = suggestion != null
            ? prefilled
                ? $"ИИ предлагает «{suggestion}» — вариант уже подставлен в поле «Исправленный вариант». Проверьте и нажмите «Да ✓»."
                : $"ИИ предлагает «{suggestion}» — в поле «Исправленный вариант» стоит ваша прежняя правка, ИИ её не перебивает."
            : typoFix != null
                ? $"ИИ предлагает другое написание: «{typoFix}» — буквы отличаются от исходного слова. Нажмите «Да», если форма верная, или исправьте вручную."
                : _llmRejected.TryGetValue(wordKey, out var otherAnswer)
                    ? string.IsNullOrEmpty(otherAnswer.Raw)
                        ? "ИИ не подобрал ударение для этого слова — выберите ударную гласную вручную."
                        : $"ИИ ответил иначе: «{otherAnswer.Raw}» — буквы не совпали с исходным словом, поэтому автоматически не подставляется."
                    : string.Empty;
        LlmHintTextBlock.Text = hint;
        LlmHintPanel.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StressChoicesPanel.Children.Clear();
        DeclensionRowsPanel.Children.Clear();
        _declensions.Clear();
        _workingWord = PlainLetters(CorrectionTextBox.Text);
        RebuildStressChoices();
        OriginalWordTextBlock.Text = $"Исходное слово: {selectedEntry.Word}";
        LoadSavedForms(selectedEntry);
        if (_declensions.Count == 0) AddDeclensionRow(selectedEntry.Word);
        RenderSelectedSample();
        UpdateNavigationState();
        UpdateEditorEnabled();
    }

    private void NavigateGroup(int direction)
    {
        if (WordGroupsListBox.Items.Count == 0) return;
        var next = Math.Clamp(WordGroupsListBox.SelectedIndex + direction, 0, WordGroupsListBox.Items.Count - 1);
        WordGroupsListBox.SelectedIndex = next;
        WordGroupsListBox.ScrollIntoView(WordGroupsListBox.SelectedItem);
    }

    private void HandleReviewNavigationKey(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter — то же, что кнопка «Да»: подтвердить текущее ударение и идти дальше.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ConfirmCurrentGroup();
            e.Handled = true;
            return;
        }
        if (CorrectionTextBox.IsKeyboardFocusWithin || FragmentEditor.IsKeyboardFocusWithin || DeclensionRowsPanel.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Left) { NavigateGroup(-1); e.Handled = true; return; }
        if (e.Key == Key.Right) { NavigateGroup(1); e.Handled = true; return; }
        if (e.Key is not (Key.Up or Key.Down) || !ContextsListBox.IsKeyboardFocusWithin || ContextsListBox.Items.Count == 0) return;
        ContextsListBox.SelectedIndex = Math.Clamp(ContextsListBox.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, ContextsListBox.Items.Count - 1);
        e.Handled = true;
    }

    private void SelectSample(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count) return;
        _selectedSampleIndex = entryIndex;
        var entry = _entries[entryIndex];
        ContextTextBlock.Text = $"Выбрано предложение: {entry.Word} — {entry.Context}";
        var group = _entries.Where(item => ExactWordKey(item.Word) == ExactWordKey(entry.Word)).ToArray();
        SelectionCountTextBlock.Text = $"Включено образцов: {group.Count(item => item.Included)} из {group.Length}";
        var selected = ContextsListBox.Items.Cast<SampleItem>().FirstOrDefault(sample => sample.Index == entryIndex);
        if (selected != null && !ReferenceEquals(ContextsListBox.SelectedItem, selected)) ContextsListBox.SelectedItem = selected;
        RenderSelectedSample();
    }

    private void SetSampleIncluded(int index, bool included)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries[index].Included = included;
        var group = _entries.Where(entry => ExactWordKey(entry.Word) == ExactWordKey(_entries[index].Word)).ToArray();
        SelectionCountTextBlock.Text = $"Включено образцов: {group.Count(entry => entry.Included)} из {group.Length}";
        if (!included && _selectedSampleIndex == index)
        {
            var replacementIndex = _entries.Select((entry, entryIndex) => (entry, entryIndex))
                .Where(pair => ExactWordKey(pair.entry.Word) == ExactWordKey(_entries[index].Word) && pair.entry.Included)
                .Select(pair => pair.entryIndex).DefaultIfEmpty(index).First();
            _selectedSampleIndex = replacementIndex;
        }
        var samples = ContextsListBox.Items.Cast<SampleItem>().Select(item => item.Index == index ? SampleItem.For(item.Index, item.Context, _entries[index].Word, included) : item).ToList();
        _rebuildingSamples = true;
        ContextsListBox.ItemsSource = samples;
        ContextsListBox.SelectedItem = samples.FirstOrDefault(item => item.Index == _selectedSampleIndex);
        _rebuildingSamples = false;
        AttachContextMenusToSamples();
        SelectSample(_selectedSampleIndex >= 0 ? _selectedSampleIndex : index);
    }

    private void UpdateEditorEnabled()
    {
        var hasReport = !string.IsNullOrWhiteSpace(_reviewPath) && File.Exists(_reviewPath);
        LoadFilesButton.IsEnabled = hasReport;
        OpenCorrectionsButton.IsEnabled = hasReport && File.Exists(Program.FormCorrectionsPathFor(_reviewPath!));
        ApplyAllButton.IsEnabled = hasReport && !string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath);
        SaveCorrectionButton.IsEnabled = ApplyAllButton.IsEnabled && WordGroupsListBox.SelectedItem != null;
    }

    private void UpdateNavigationState()
    {
        PreviousButton.IsEnabled = WordGroupsListBox.SelectedIndex > 0;
        NextButton.IsEnabled = WordGroupsListBox.SelectedIndex >= 0 && WordGroupsListBox.SelectedIndex < WordGroupsListBox.Items.Count - 1;
    }

    private static string DefaultInitialStress(string word)
    {
        if (word.Contains('\u0301') || word.Contains('ё', StringComparison.OrdinalIgnoreCase)) return word;
        var vowels = word.Select((ch, index) => (ch, index)).Where(item => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(item.ch)).ToArray();
        if (vowels.Length == 0) return word;
        // Ударение по умолчанию обязано быть допустимой правкой и для слов с одной
        // гласной, и для слов, начинающихся с гласной: иначе кнопка "Да" не сработает.
        var position = vowels[0].index <= 1 ? vowels[0].index : vowels.Length > 1 ? vowels[1].index : vowels[0].index;
        return word.Insert(position + 1, "\u0301");
    }

    private const string Vowels = "аеёиоуыэюя";

    // Всё слово крупными стилизованными буквами; кликабельны только гласные.
    private void RebuildVowelChoices()
    {
        StressChoicesPanel.Children.Clear();
        var word = _workingWord;
        if (string.IsNullOrEmpty(word))
        {
            StressChoicesPanel.Children.Add(new TextBlock
            {
                Text = "Выберите слово в списке слева.",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("MutedInk") as Brush ?? Brushes.Gray
            });
            return;
        }
        var accentPosition = CorrectionTextBox.Text.IndexOf('\u0301') - 1;
        foreach (var (letter, position) in word.Select((ch, index) => (ch, index)))
        {
            var lower = char.ToLowerInvariant(letter);
            if (!Vowels.Contains(lower))
            {
                StressChoicesPanel.Children.Add(WordLetter(letter.ToString(), null, isVowel: false, isStressed: false, null, null));
                continue;
            }
            var captured = position;
            if (lower == 'ё')
            {
                // «ё» всегда ударная: клик по ней возвращает «е», чтобы можно было
                // поставить обычное ударение (см. ApplyVowelClick).
                StressChoicesPanel.Children.Add(WordLetter(letter.ToString(), null, isVowel: true, isStressed: true,
                    "Ё всегда ударная. Клик вернёт «е», чтобы можно было поставить обычное ударение.",
                    () => ApplyVowelClick(captured)));
                continue;
            }
            StressChoicesPanel.Children.Add(WordLetter(letter.ToString(), "\u00b4", isVowel: true, isStressed: accentPosition == position,
                lower == 'е'
                    ? "Клик — ударение на эту «е». Ещё клик — «ё». Третий клик — «е» без ударения."
                    : "Клик — поставить ударение на эту гласную. Ещё клик — снять ударение.",
                () => ApplyVowelClick(captured)));
        }
        UpdateVowelPanelHint(word, accentPosition);
    }

    // Ударение может стоять не на гласной (см. «Уникальное ударение»). В верхней панели
    // его тогда не видно — предупреждаем, чтобы это не выглядело как ошибка.
    private void UpdateVowelPanelHint(string word, int accentPosition)
    {
        var onVowel = accentPosition >= 0 && accentPosition < word.Length &&
                      Vowels.Contains(char.ToLowerInvariant(word[accentPosition]));
        VowelPanelHint.Visibility = accentPosition < 0 || onVowel ? Visibility.Collapsed : Visibility.Visible;
        // Показываем слово целиком: в маленьком шрифте «ѣ» легко спутать с «ь».
        VowelPanelHint.Text = accentPosition >= 0 && accentPosition < word.Length
            ? "Редкий случай: ударение стоит на «" + word[..(accentPosition + 1)] + "\u0301" + word[(accentPosition + 1)..] +
              "» — на букве, которой нет среди гласных. Так и задумано."
            : string.Empty;
    }

    /// <summary>
    /// Редкий случай: ударение не на гласной, а на ѣ, і, ѧ или другой букве древней
    /// орфографии («свѣ́тлостію», «мѣ́дъ»). Кликабельна любая буква слова.
    /// </summary>
    private void RebuildUniqueStressChoices()
    {
        UniqueStressPanel.Children.Clear();
        var word = _workingWord;
        if (string.IsNullOrEmpty(word))
        {
            UniqueStressPanel.Children.Add(new TextBlock
            {
                Text = "Выберите слово в списке слева.",
                Foreground = TryFindResource("MutedInk") as Brush ?? Brushes.Gray,
            });
            return;
        }
        var accentPosition = CorrectionTextBox.Text.IndexOf('\u0301') - 1;
        foreach (var (letter, position) in word.Select((ch, index) => (ch, index)))
        {
            var captured = position;
            var isYo = char.ToLowerInvariant(letter) == 'ё';
            UniqueStressPanel.Children.Add(WordLetter(letter.ToString(), null, isVowel: true, isStressed: accentPosition == position,
                isYo ? "«ё» всегда ударная — отдельный знак ударения не нужен." : "Поставить ударение на эту букву.",
                isYo ? null : () => SetStressOnAnyLetter(captured)));
        }
    }

    private void SetStressOnAnyLetter(int position)
    {
        if (_updatingCorrection) return;
        var word = _workingWord;
        if (position < 0 || position >= word.Length) return;
        if (char.ToLowerInvariant(word[position]) == 'ё')
        {
            SetWorkingCorrection(word, "«ё» всегда ударная — отдельный знак ударения не нужен.");
            return;
        }
        SetWorkingCorrection(word.Insert(position + 1, "\u0301"), string.Empty);
    }

    // Обе панели ударения показывают одно и то же слово, поэтому перестраиваются вместе.
    private void RebuildStressChoices()
    {
        RebuildVowelChoices();
        RebuildUniqueStressChoices();
    }

    private UIElement WordLetter(string glyph, string? mark, bool isVowel, bool isStressed, string? tooltip, Action? click)
    {
        var accent = TryFindResource("Accent") as Brush ?? Brushes.SaddleBrown;
        var muted = TryFindResource("MutedInk") as Brush ?? Brushes.Gray;
        var ink = TryFindResource("Ink") as Brush ?? Brushes.Black;
        var box = new StackPanel { Margin = new Thickness(1, 0, 1, 0) };
        // Знак ударения над ударной гласной: видно, где оно стоит, не читая слово целиком.
        box.Children.Add(new TextBlock
        {
            Text = mark != null && isStressed ? mark : " ",
            FontSize = 13,
            Height = 15,
            TextAlignment = TextAlignment.Center,
            Foreground = isStressed ? accent : Brushes.Transparent,
        });
        box.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = 32,
            // Шрифт тот же, что и в окне редактирования фрагмента.
            FontWeight = isStressed ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isStressed ? accent : isVowel ? ink : muted,
        });
        if (click == null)
        {
            box.ToolTip = tooltip;
            return box;
        }
        var button = new Button { Content = box, ToolTip = tooltip, Style = TryFindResource("LetterButton") as Style };
        if (isStressed)
        {
            button.BorderBrush = accent;
            button.BorderThickness = new Thickness(1.5);
        }
        button.Click += (_, _) => click();
        return button;
    }

    // Клик по гласной: «е» ходит по кругу «ударная → ё → без ударения», остальные гласные
    // просто ставят и снимают ударение. Клик по другой гласной переносит ударение туда.
    private void ApplyVowelClick(int position)
    {
        if (_updatingCorrection) return;
        var word = _workingWord;
        if (position < 0 || position >= word.Length) return;
        var lower = char.ToLowerInvariant(word[position]);
        if (!Vowels.Contains(lower)) return;
        var accentPosition = CorrectionTextBox.Text.IndexOf('\u0301') - 1;

        if (lower == 'ё')
        {
            // Возврат «е»: без «ё» можно поставить обычное ударение на любую гласную.
            var yoLetters = word.ToCharArray();
            yoLetters[position] = char.IsUpper(yoLetters[position]) ? 'Е' : 'е';
            SetWorkingCorrection(new string(yoLetters), "«ё» заменена на «е» — теперь можно поставить обычное ударение.");
            return;
        }
        if (accentPosition < 0 && word.Any(ch => char.ToLowerInvariant(ch) == 'ё'))
        {
            FileStatusTextBlock.Text = "В слове есть «ё» — оно и есть ударная гласная. Кликните по «ё», чтобы вернуть «е» и поставить обычное ударение.";
            return;
        }

        string next;
        if (accentPosition != position) next = word.Insert(position + 1, "\u0301"); // поставили ударение
        else if (lower == 'е') // «е́» → «ё»
        {
            var yoLetters = word.ToCharArray();
            yoLetters[position] = char.IsUpper(yoLetters[position]) ? 'Ё' : 'ё';
            next = new string(yoLetters);
        }
        else next = word; // сняли ударение
        SetWorkingCorrection(next, string.Empty);
    }

    private void SetWorkingCorrection(string value, string note)
    {
        CorrectionTextBox.Text = value;
        // Знак ударения буквы не меняет, поэтому панели перестраиваем всегда.
        _workingWord = PlainLetters(value);
        RebuildStressChoices();
        if (note.Length > 0) FileStatusTextBlock.Text = note;
    }

    // Кнопка «Да» у поля правки: текущий вариант принимается как есть (даже если это
    // ударение по умолчанию), и открывается следующая группа без применения к файлу.
    private void ConfirmStress_Click(object sender, RoutedEventArgs e) => ConfirmCurrentGroup();

    private void ConfirmCurrentGroup()
    {
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count)
        {
            FileStatusTextBlock.Text = "Выберите группу слова в списке слева.";
            return;
        }
        var entry = _entries[selected.Index];
        var proposed = CorrectionTextBox.Text.Trim().Replace("ё\u0301", "ё", StringComparison.OrdinalIgnoreCase);
        // Пустое поле тоже принимается: тогда используется ударение, показанное подсказкой.
        if (proposed.Length == 0) proposed = DefaultInitialStress(entry.Word);
        // Правка букв (исправление опечатки) — предлагаем сохранить её или нет.
        if (!SameLettersApartFromYo(proposed, entry.Word))
        {
            var answer = MessageBox.Show(this,
                $"Написание слова изменено.\n\nбыло: {entry.Word}\nстало: {PlainLetters(proposed)}\n\nСохранить исправленное написание?",
                "Исправление написания", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                FileStatusTextBlock.Text = "Исправленное написание не сохранено: в правке осталось исходное слово.";
                return;
            }
        }
        if (!Program.IsValidCorrection(entry.Word, proposed) && !Program.IsValidTypoCorrection(entry.Word, proposed))
        {
            MessageBox.Show(this, "Не удалось подтвердить правку: в поле должно стоять то же слово с одним знаком ударения, слово с «ё» либо исправленная опечатка (не более 3 отличий от исходного слова).", "Проверьте правку", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var item in GetSelectedGroup()) item.Proposed = proposed;
        var confirmed = _entries.Count(item => ExactWordKey(item.Word) == ExactWordKey(entry.Word));
        var editedGroups = CollectEditedGroups().Count;
        FileStatusTextBlock.Text = $"Принято: «{proposed}» (группа «{entry.Word}», образцов {confirmed}). Подтверждено групп: {editedGroups}. В конце нажмите «Применить всё и сохранить».";
        NavigateGroup(1);
    }

    // Возврат к исходному слову из книги: снимает исправление написания (опечатки),
    // которое пользователь мог внести вручную.
    private void ResetToOriginalWord_Click(object sender, RoutedEventArgs e)
    {
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return;
        var entry = _entries[selected.Index];
        var restored = DefaultInitialStress(entry.Word);
        _updatingCorrection = true;
        CorrectionTextBox.Text = restored;
        _workingWord = PlainLetters(entry.Word);
        RebuildStressChoices();
        _updatingCorrection = false;
        foreach (var item in GetSelectedGroup()) item.Proposed = restored;
        OriginalWordTextBlock.Text = $"Исходное слово: {entry.Word}";
        ContextTextBlock.Text = "Написание возвращено к исходному слову. Выберите ударную гласную или снова исправьте буквы.";
        UpdateEditorWord();
    }

    private void LoadSavedForms(MutableReviewEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return;
        foreach (var form in Program.ReadDeclensionForms(_reviewPath, entry.Word).Where(form => !form.Case.Equals("Исходное слово", StringComparison.Ordinal)))
            AddDeclensionRow(form.Form, form.Case);
    }

    private void AddDeclensionRow(string? initialForm = null, string? grammaticalCase = null)
    {
        var formBox = new TextBox { Text = initialForm ?? string.Empty, MinWidth = 230 };
        AddTextEditingContextMenu(formBox);
        var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var remove = new Button { Content = "Убрать", Padding = new Thickness(5, 2, 5, 2), ToolTip = "Удалить эту форму из списка сохраняемых словоформ" };
        remove.Click += (_, _) => { if (_declensions.Remove(formBox)) DeclensionRowsPanel.Children.Remove(panel); };
        DockPanel.SetDock(remove, Dock.Right);
        panel.Children.Add(remove);
        var caseBox = new ComboBox { Width = 170, ItemsSource = GrammaticalCaseComboBox.ItemsSource, SelectedItem = grammaticalCase ?? GrammaticalCaseComboBox.SelectedItem, Margin = new Thickness(0, 0, 6, 0) };
        var stressPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void RebuildFormVowels()
        {
            stressPanel.Children.Clear();
            var radios = new List<RadioButton>();
            var form = formBox.Text;
            var plain = form.Replace("\u0301", "");
            foreach (var (vowel, position) in plain.Select((ch, index) => (ch, index)).Where(item => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(item.ch)))
            {
                if (char.ToLowerInvariant(vowel) == 'ё')
                {
                    stressPanel.Children.Add(new TextBlock { Text = "ё (ударная)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), Foreground = Brushes.DarkGreen });
                    continue;
                }
                var radio = new RadioButton { Content = vowel.ToString(), GroupName = "form-" + formBox.GetHashCode(), Tag = position, Margin = new Thickness(3), ToolTip = "Поставить ударение на эту гласную" };
                radio.Checked += (_, _) => { if (position < plain.Length) formBox.Text = plain.Insert(position + 1, "\u0301"); };
                radios.Add(radio);
                stressPanel.Children.Add(radio);
                if (char.ToLowerInvariant(vowel) == 'е')
                {
                    var yo = new Button { Content = "ё", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 1, 4, 1), ToolTip = "Заменить эту е на ё" };
                    yo.Click += (_, _) => { var chars = plain.ToCharArray(); chars[position] = char.IsUpper(chars[position]) ? 'Ё' : 'ё'; formBox.Text = new string(chars); };
                    stressPanel.Children.Add(yo);
                }
            }
            var stressed = form.IndexOf('\u0301') - 1;
            var selected = radios.FirstOrDefault(radio => radio.Tag is int index && index == stressed);
            if (selected != null) selected.IsChecked = true;
        }
        formBox.TextChanged += (_, _) => RebuildFormVowels();
        panel.Children.Add(caseBox);
        panel.Children.Add(formBox);
        panel.Children.Add(stressPanel);
        DeclensionRowsPanel.Children.Add(panel);
        _declensions.Add(formBox);
        panel.Tag = caseBox;
        if (!string.IsNullOrWhiteSpace(formBox.Text)) RebuildFormVowels();
    }

    private void ApplyAllCorrections()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath) || string.IsNullOrWhiteSpace(_outputPath) || !File.Exists(_outputPath)) return;
        var correctionsPath = Program.FormCorrectionsPathFor(_reviewPath);
        List<Program.DeclensionCorrection> saved;
        try { saved = File.Exists(correctionsPath) ? JsonSerializer.Deserialize<List<Program.DeclensionCorrection>>(File.ReadAllText(correctionsPath)) ?? new() : new(); }
        catch (JsonException ex) { MessageBox.Show(this, "Не удалось прочитать файл форм слов: " + ex.Message, "Ошибка чтения", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (saved.Count == 0) { MessageBox.Show(this, "В файле пока нет сохранённых исправлений.", "Нет исправлений", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var sourcePath = _outputPath;
        var correctedPath = GetCorrectedOutputPath(sourcePath);
        try
        {
            ConfirmOutputPath(sourcePath, correctedPath, "Перезапись corrected-файла");
            if (!string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(correctedPath) &&
                MessageBox.Show(this, $"Файл уже существует:\n{correctedPath}\n\nПересоздать его из выбранного FB2?", "Пересоздание corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var corrections = saved.GroupBy(item => Program.NormalizeWordLettersForReview(item.Form), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            if (!string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase)) File.Copy(sourcePath, correctedPath, overwrite: true);
            var isText = Path.GetExtension(sourcePath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
            var count = isText ? Program.ApplyWordCorrectionsToTextFile(sourcePath, correctedPath, corrections) : Program.ApplyWordCorrectionsToFb2(sourcePath, correctedPath, corrections);
            _outputPath = correctedPath;
            Fb2FileTextBox.Text = correctedPath;
            RefreshWordCounts();
            ApplyReviewItems();
            UpdateEditorEnabled();
            FileStatusTextBlock.Text = count == 0 ? $"Вхождений по сохранённым исправлениям не найдено. Файл не изменён: {correctedPath}" : $"Создан/обновлён файл: {correctedPath}\nСохранённые исправления применены: заменено вхождений {count}.";
            OfferToOpenResultFolders("Исправления применены.",
                correctedPath,
                Program.FormCorrectionsPathFor(_reviewPath!),
                StatisticsPath,
                InputTextBox.Text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось применить исправления: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ConfirmOutputPath(string sourcePath, string correctedPath, string title)
    {
        if (string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, $"Исходный файл уже имеет суффикс _corrected и будет перезаписан:\n{sourcePath}\n\nПродолжить?", title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            throw new OperationCanceledException();
    }

    private static string GetCorrectedOutputPath(string sourcePath)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        var directory = Path.GetDirectoryName(fullSource)!;
        var baseName = Path.GetFileNameWithoutExtension(fullSource);
        return baseName.EndsWith("_corrected", StringComparison.OrdinalIgnoreCase) ? fullSource : Path.Combine(directory, baseName + "_corrected" + Path.GetExtension(sourcePath));
    }

    private static void OpenGoogleStressSearch(string word, string context)
    {
        var index = context.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        var highlighted = index < 0 ? $"{context} Проверяемое слово: **{word}**" : context[..index] + "**" + context.Substring(index, word.Length) + "**" + context[(index + word.Length)..];
        Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString($"куда ставить ударение в предложении {highlighted}")) { UseShellExecute = true });
    }

    private static string ExactWordKey(string word) => word.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private sealed class MutableReviewEntry
    {
        private readonly (string Context, int Occurrence) _splitContext;
        public MutableReviewEntry(Program.WordReviewEntry entry) { Word = entry.Word; Proposed = entry.Proposed; _splitContext = Program.SplitReviewContext(entry.Context); }
        public string Word { get; }
        public string Context => _splitContext.Context;
        public int ContextOccurrence => _splitContext.Occurrence;
        public string Proposed { get; set; }
        public bool Included { get; set; } = true;
        public Program.WordReviewEntry ToEntry() => new(Word, $"{Context}\u0001{ContextOccurrence}", Proposed);
    }

    private sealed record ReviewItem(int Index, string Display, string Word, int Count);

    private sealed record SampleItem(int Index, string Context, bool Included, string Before, string Exact, string After)
    {
        public static SampleItem For(int index, string context, string word, bool included)
        {
            var plain = context.Replace("\u0301", "");
            var start = word.Length == 0 ? -1 : plain.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return new SampleItem(index, context, included, context + " ", word, string.Empty);
            return new SampleItem(index, context, included, plain[..start], plain.Substring(start, word.Length), plain[(start + word.Length)..]);
        }
    }
}

