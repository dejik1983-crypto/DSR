using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
/// Раньше это было отдельное окно; теперь — вкладки 2, 3 и 4 главного окна MainWindow
/// («2. Расхождения и ИИ», «3. Контроль ИИ результатов», «4. Ручная доводка»).
/// </summary>
public sealed partial class MainWindow
{
    private string? _reviewPath;
    private string? _outputPath;
private int _manualCorrectionCount;
    private readonly List<MutableReviewEntry> _entries = new();
    private readonly List<TextBox> _declensions = new();
    private Dictionary<string, int> _wordCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _llmSuggestions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Program.LlmRejected> _llmRejected = new(StringComparer.Ordinal);
    private bool _hasModelDisagreements;
    private string _llmStatus = string.Empty;
    private Program.LlmEngine _llmEngine = Program.CurrentLlmEngine();
    private bool _updatingLlmEngine;
    // Выбор устройства для движков на PyTorch (silero-stress, omogre) и последнее
    // известное состояние сборки PyTorch — из него складывается подпись под кнопками.
    private bool _updatingLlmDevice;
    private bool _torchProbeRunning;
    private Program.TorchCudaStatus? _torchStatus;
    private int _selectedGroupAnchor = -1;
    private int _selectedSampleIndex = -1;
    private bool _rebuildingSamples;
    private bool _updatingCorrection;
    private bool _updatingCheckedFilter;
    // Текущее написание слова без знака ударения. Именно к нему привязываются кнопки
    // ударных гласных, поэтому исправление опечатки и расстановка ударения работают вместе.
    private string _workingWord = string.Empty;
    // Большой редактор фрагмента: проверяемое слово — жирный Run внутри текста.
    private Run? _wordRun;
    private bool _updatingFragment;
    private bool _wordEditFromEditor;
    private string _fragmentBefore = string.Empty;
    private string _fragmentAfter = string.Empty;
    private string _fragmentBaseline = string.Empty;
    // Свободные правки целого фрагмента: текст, который человек набрал в большом
    // редакторе. Ключ — видимый фрагмент из отчёта; применяется к файлу позже.
    private readonly Dictionary<string, string> _fragmentDrafts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _fragmentDraftSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _fragmentWordEditing;
    private int _fragmentTargetTokenIndex = -1;
    private double _reviewFontSize = 16;
    // Размер шрифта примеров на странице контроля ИИ: у больших фрагментов мелкий
    // текст читать неудобно, поэтому его можно увеличить кнопками A−/A+.
    private double _controlFontSize = 14;
    // Страница «Контроль ИИ результатов»: слово, два варианта ударения и случаи,
    // разложенные по окнам. Варианты хранятся на сессию, чтобы перенос случаев в обе
    // стороны работал, даже если окно временно опустело.
    private List<ControlGroupData> _controlGroups = new();
    // Ручная доводка (вкладка 4) наполняется только тем, что пользователь перетащил
    // туда из списков вкладок 2–3. Пустой набор — пустая вкладка.
    private readonly HashSet<string> _manualReviewKeys = new(StringComparer.Ordinal);
    // Конкретные вхождения, перенесённые в ручную доводку. В отличие от набора слов выше,
    // позволяют убрать из контроля один случай, не трогая остальные случаи того же слова.
    private readonly HashSet<string> _manualReviewCaseKeys = new(StringComparer.Ordinal);
    private Point _wordDragStart;
    private ReviewItem? _wordDragItem;
    private Point _unrecognizedDragStart;
    private ReviewItem? _unrecognizedDragItem;
    private const string ManualReviewDragFormat = "DSR.ManualReviewWord";
    private readonly Dictionary<string, (string Left, string Right)> _controlVariants = new(StringComparer.Ordinal);
    private int _controlGroupIndex = -1;
    // Перетаскивание случая мышкой из одного окна в другое: точка нажатия, случай под ней
    // и признак того, что перетаскивание состоялось (тогда отпускание кнопки — не щелчок).
    private const string ControlCaseDragFormat = "DSR.ControlCase";
    private Point _controlDragStart;
    private ControlCaseItem? _controlDragItem;
    private bool _controlDragHappened;
    // Раскрыт всегда один случай: у прежнего фрагмент сворачивается, а сам он помечается
    // просмотренным (полоска слева и спокойный фон). Так после десятка щелчков видно,
    // что уже прочитано; пометки живут до загрузки другого отчёта.
    private int _controlExpandedIndex = -1;
    private readonly HashSet<int> _controlViewedIndices = new();
    // Отмена переносов: стек шагов, в каждом — состояние случая до переноса.
    private readonly List<ControlMoveStep> _controlMoveHistory = new();
    // Журнал правок: каждый перенос и каждая отмена. Его показывает кнопка «Журнал правок»,
    // он же сохраняется в txt-документ, поэтому запись хранит и фрагмент текста.
    private readonly List<ControlEditEntry> _controlJournal = new();
    private Window? _controlJournalWindow;
    private Action? _refreshControlJournalWindow;
    // Примеры для обучения: проверенный случай пишется один раз за сессию (ключ — номер
    // случая и оставленное ударение), чтобы повторное сохранение не плодило дубли.
    private readonly HashSet<string> _trainingLogged = new(StringComparer.Ordinal);

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
        // Вкладка 4 — для того, где ИИ не справилась: без ударения, без разбора ИИ или с опечаткой.
        ManualFilterComboBox.ItemsSource = new[] { "Всё для ручной доводки", "ИИ не разобрала", "Без ударения" };
        ManualFilterComboBox.SelectedIndex = 0;
        GrammaticalCaseComboBox.ItemsSource = new[] { "Именительный", "Родительный", "Дательный", "Винительный", "Творительный", "Предложный", "Другая форма / контекст" };
        GrammaticalCaseComboBox.SelectedIndex = 0;
        ReviewFileTextBox.Text = _reviewPath ?? string.Empty;
        Fb2FileTextBox.Text = _outputPath ?? string.Empty;
        SearchTextBox.TextChanged += SearchTextBox_TextChanged;
        SortModeComboBox.SelectionChanged += SortModeComboBox_SelectionChanged;
        ManualFilterComboBox.SelectionChanged += SortModeComboBox_SelectionChanged;
        ShowCheckedCheckBox.Checked += ShowCheckedCheckBox_Changed;
        ShowCheckedCheckBox.Unchecked += ShowCheckedCheckBox_Changed;
        ShowCheckedManualCheckBox.Checked += ShowCheckedCheckBox_Changed;
        ShowCheckedManualCheckBox.Unchecked += ShowCheckedCheckBox_Changed;
        WordGroupsListBox.SelectionChanged += WordGroupsListBox_SelectionChanged;
        ContextsListBox.SelectionChanged += ContextsListBox_SelectionChanged;
        CorrectionTextBox.TextChanged += Correction_TextChanged;
        AddTextEditingContextMenu(CorrectionTextBox);
        AddTextEditingContextMenu(ReviewFileTextBox);
        AddTextEditingContextMenu(Fb2FileTextBox);
        ContextsListBox.Loaded += (_, _) => QueueAttachContextMenusToSamples();
        ContextsListBox.ItemContainerGenerator.StatusChanged += ContextsListBox_ContainerStatusChanged;
        ChooseReviewButton.Click += ChooseReview_Click;
        ChooseFb2Button.Click += ChooseFb2_Click;
        LoadFilesButton.Click += LoadFiles_Click;
        OpenCorrectionsButton.Click += OpenCorrections_Click;
        ApplyAllButton.Click += ApplyAll_Click;
        PreviousButton.Click += Previous_Click;
        NextButton.Click += Next_Click;
        PreviousCaseButton.Click += (_, _) => NavigateCase(-1);
        NextCaseButton.Click += (_, _) => NavigateCase(1);
        AddFormButton.Click += AddForm_Click;
        SaveCorrectionButton.Click += SaveCorrection_Click;
        SaveProgressButton.Click += (_, _) => SaveProgress_Click();
        FinishWorkButton.Click += (_, _) => FinishWork_Click();
        ConfirmStressButton.Click += ConfirmStress_Click;
        ResetWordButton.Click += ResetToOriginalWord_Click;
        // Большой редактор: слово правится прямо в тексте, кнопки ударения — крупные.
        FragmentEditor.TextChanged += FragmentEditor_TextChanged;
        FragmentEditor.SelectionChanged += FragmentEditor_SelectionChanged;
        _fragmentDraftSaveTimer.Tick += (_, _) => { _fragmentDraftSaveTimer.Stop(); SaveFragmentDrafts(); };
        FontLargerButton.Click += (_, _) => ChangeReviewFontSize(2);
        FontSmallerButton.Click += (_, _) => ChangeReviewFontSize(-2);
        LlmCheckButton.Click += LlmCheck_Click;
        LlmRecheckButton.Click += LlmRecheck_Click;
        // Страница «Контроль ИИ результатов»: перенос случаев между вариантами ударения,
        // раскрытие фрагмента щелчком и меню правой кнопки — как в окне ручной доводки.
        ControlMoveRightButton.Click += (_, _) => MoveControlCase(ControlLeftCasesListBox, ControlBucket.Right);
        ControlMoveLeftButton.Click += (_, _) => MoveControlCase(ControlRightCasesListBox, ControlBucket.Left);
        // Перетаскивание фрагментов в ручную доводку и обратно идёт через заголовки
        // вкладок: вкладка при этом не переключается, пока по ней не щёлкнут.
        WordGroupsListBox.PreviewMouseLeftButtonDown += ManualReviewWord_MouseDown;
        WordGroupsListBox.PreviewMouseMove += ManualReviewWord_MouseMove;
        UnrecognizedWordsListBox.PreviewMouseLeftButtonDown += Unrecognized_MouseDown;
        UnrecognizedWordsListBox.PreviewMouseMove += Unrecognized_MouseMove;

        // Третье окно: случаи-энклиномены, у которых ударение уходит на предлог («на́ пол»).
        ControlMoveToPrepositionButton.Click += (_, _) => MoveSelectedControlCase(ControlBucket.Enclinomen);
        ControlMoveFromPrepositionButton.Click += (_, _) => MoveControlCase(ControlEnclinomenCasesListBox, ControlBucket.Left);
        foreach (var cases in ControlCaseLists())
        {
            cases.PreviewMouseLeftButtonUp += ControlCasesMouseLeftButtonUp;
            // Перенос мышкой: случай перетаскивается из одного окна в другое.
            cases.PreviewMouseLeftButtonDown += ControlCasesDragMouseDown;
            cases.PreviewMouseMove += ControlCasesDragMouseMove;
            cases.DragOver += ControlCasesDragOver;
            cases.Drop += ControlCasesDrop;
            cases.SelectionChanged += (_, _) => UpdateControlNavigationState();
            // Меню наполняется при каждом открытии, но пустое меню должно существовать: событие
            // поднимается только у того элемента, которому меню уже назначено.
            cases.ContextMenu = new ContextMenu();
            cases.ContextMenuOpening += ControlCasesContextMenuOpening;
        }
        ControlPreviousWordButton.Click += (_, _) => NavigateControlGroup(-1);
        ControlNextWordButton.Click += (_, _) => NavigateControlGroup(1);
        // Размер шрифта примеров: крупнее/мельче и запоминается между запусками.
        ControlFontSmallerButton.Click += (_, _) => ChangeControlFontSize(-1);
        ControlFontLargerButton.Click += (_, _) => ChangeControlFontSize(1);
        _controlFontSize = Program.LoadControlFontSize();
        ApplyControlFontSize();
        ControlSaveButton.Click += (_, _) => SaveControlResults();
        ControlUndoButton.Click += (_, _) => UndoControlMove();
        ControlJournalButton.Click += (_, _) => ShowControlJournal();
        ControlResetViewedButton.Click += (_, _) => ResetControlViewed();
        ControlAccuracyButton.Click += (_, _) => SaveControlAccuracyReport(openAfterSave: true);
        ControlToManualButton.Click += (_, _) => SendControlGroupToManualReview();
        ManualReviewStepButton.Click += (_, _) => SendAllDisagreementsToManualReview();
        ModelChoicesPanel.SizeChanged += (_, _) => FitWindowLayoutSoon();
        UniqueStressToggle.Checked += UniqueStressToggle_Changed;
        UniqueStressToggle.Unchecked += UniqueStressToggle_Changed;
        UnrecognizedWordsListBox.MouseDoubleClick += (_, _) => JumpToManualReview();
        InitializeLlmEngines();
        PreviewKeyDown += HandleReviewNavigationKey;
        SizeChanged += (_, _) => FitWindowLayoutSoon();
        ReviewTabs.SelectionChanged += (_, _) => FitWindowLayout();
        // Заголовки вкладок — приёмники перетаскивания: на вкладку 4 фрагмент переносится
        // в ручную доводку, на вкладку 3 — возвращается обратно. Вкладка не переключается.
        ReviewTabs.AllowDrop = true;
        ReviewTabs.DragOver += ReviewTabs_DragOver;
        ReviewTabs.Drop += ReviewTabs_Drop;
        WordGroupsListBox.PreviewKeyDown += HandleReviewNavigationKey;
        ContextsListBox.PreviewKeyDown += HandleReviewNavigationKey;
        if (_reviewPath != null && File.Exists(_reviewPath)) LoadReviewFiles();
        else UpdateEditorEnabled();
    }

    /// <summary>Подставляет файлы отчёта и результата и загружает их в редактор.</summary>
    internal void SetReviewPaths(string? reviewPath, string? outputPath)
    {
        var changed = (!string.IsNullOrWhiteSpace(reviewPath) && !string.Equals(_reviewPath, reviewPath, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(outputPath) && !string.Equals(_outputPath, outputPath, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(reviewPath)) { _reviewPath = reviewPath; ReviewFileTextBox.Text = reviewPath!; }
        if (!string.IsNullOrWhiteSpace(outputPath)) { _outputPath = outputPath; Fb2FileTextBox.Text = outputPath!; }
        if (changed && _reviewPath != null && File.Exists(_reviewPath)) LoadReviewFiles();
        else { RefreshWordCounts(); ApplyReviewItems(); UpdateEditorEnabled(); }
    }

    /// <summary>Обновляет оба списка слов: полный (вкладка 4) и краткий (вкладка 2).</summary>
    private void ApplyReviewItems()
    {
        WordGroupsListBox.ItemsSource = BuildReviewItems();
        UnrecognizedWordsListBox.ItemsSource = BuildUnrecognizedItems();
        var hasDisagreement = _entries.Any(HasModelDisagreement);
        LlmCheckButton.IsEnabled = hasDisagreement;
        LlmRecheckButton.IsEnabled = hasDisagreement;
        ManualReviewStepButton.IsEnabled = _entries.Count > 0;
        UpdateDisagreementSummary();
        RefreshControlPage();
    }

    /// <summary>У слова есть расхождение моделей: RUAccent и Silero предложили разные варианты.</summary>
    private static bool HasModelDisagreement(MutableReviewEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.RuAccent) || !string.IsNullOrWhiteSpace(entry.Silero);

    // Сводка для вкладки 2: сколько расхождений, по скольким ИИ дал вариант, по скольким не
    // уверен, и сколько слов вообще осталось без ударения (третья категория сравнения).
    private void UpdateDisagreementSummary()
    {
        if (DisagreementSummaryTextBlock == null) return;
        if (_entries.Count == 0)
        {
            DisagreementSummaryTextBlock.Text = "Отчёт расхождений появится после обработки книги на вкладке 1.";
            return;
        }
        var keys = _entries.Where(HasModelDisagreement)
            .Select(entry => ExactWordKey(entry.Word))
            .ToHashSet(StringComparer.Ordinal);
        // Третья категория: слова, ударение в которых не поставила ни одна модель — ни
        // RUAccent, ни Silero. Они попадают в ручную доводку без готового варианта.
        var withoutStress = _entries.Where(entry => !HasModelDisagreement(entry))
            .Select(entry => ExactWordKey(entry.Word))
            .ToHashSet(StringComparer.Ordinal);
        var text = new StringBuilder();
        if (keys.Count == 0)
        {
            text.Append("Расхождений RUAccent и Silero нет: модели совпали.");
        }
        else
        {
            var answered = keys.Count(key => _llmSuggestions.ContainsKey(key));
            var unsure = keys.Count - answered;
            var moved = keys.Count(key => _entries.Any(entry => ExactWordKey(entry.Word) == key && IsManualReviewCase(entry)));
            text.Append($"Расхождений: слов {keys.Count:N0} (случаев {_entries.Count(HasModelDisagreement):N0}).");
            if (moved > 0)
                text.Append($" Перенесено в ручную доводку: {moved:N0}; осталось в списке: {keys.Count - moved:N0}.");
            if (answered > 0 || unsure < keys.Count)
                text.Append($" ИИ предложил вариант: {answered:N0}; без варианта ИИ: {unsure:N0}.");
            if (unsure > 0 && answered == 0)
                text.Append(" Запустите «Подсказки ИИ по расхождениям» или сразу переходите к ручной доводке.");
        }
        if (withoutStress.Count > 0)
            text.Append($" Без ударения у обеих моделей: слов {withoutStress.Count:N0}.");
        // Ручная доводка наполняется перетаскиванием: показываем, сколько слов туда перенесено.
        if (_entries.Count > 0)
        {
            var manualCases = _entries.Count(IsManualReviewCase);
            text.Append($" В ручной доводке: случаев {manualCases:N0} — добавляются кнопкой «Перейти к ручной доводке» или перетаскиванием на вкладку 4.");
        }
        DisagreementSummaryTextBlock.Text = text.ToString();
    }

    // Краткий список расхождений моделей: слова, для которых отчёт хранит оба варианта.
    private List<ReviewItem> BuildUnrecognizedItems() =>
        _entries.Select((entry, index) => (entry, index))
            .Where(pair => HasModelDisagreement(pair.entry))
            .GroupBy(pair => ExactWordKey(pair.entry.Word), StringComparer.Ordinal)
            // Если перенесены все случаи слова, оно не должно оставаться в списке
            // расхождений. Когда перенесён только один случай, слово остаётся: остальные
            // его вхождения ещё ждут решения на этой вкладке.
            .Where(group => group.Any(pair => !IsManualReviewCase(pair.entry)))
            .Select(group =>
            {
                var first = group.First();
                var count = _wordCounts.TryGetValue(first.entry.Word, out var found) ? found : 0;
                var key = ExactWordKey(first.entry.Word);
                var variants = new[]
                {
                    group.Select(pair => pair.entry.RuAccent).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    group.Select(pair => pair.entry.Silero).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    _llmSuggestions.TryGetValue(key, out var suggestion) ? suggestion : null
                }.Where(value => !string.IsNullOrWhiteSpace(value));
                var hint = string.Join(" / ", variants);
                // Слова, по которым ИИ не дал варианта, помечаем: их видно в списке, но
                // они не отделяются в другой список — все расхождения идут в ручную доводку.
                if (AiRan && !_llmSuggestions.ContainsKey(key)) hint = hint.Length > 0 ? "⚠ " + hint : "⚠ ИИ не уверен";
                var done = group.Any(pair => pair.entry.Checked);
                return new ReviewItem(first.index, $"{(done ? "✓ " : "")}{first.entry.Word} — {count:N0} вхождений{(hint.Length > 0 ? "  ·  " + hint : "")}", first.entry.Word, count, done);
            })
            .Where(item => ShowCheckedCheckBox.IsChecked == true || !item.Done)
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Проход ИИ уже запускался: есть подсказки или зафиксированные отказы.</summary>
    private bool AiRan => _llmSuggestions.Count > 0 || _llmRejected.Count > 0;

    // Двойной щелчок по слову в кратком списке открывает его на вкладке ручной доводки.
    private void JumpToManualReview()
    {
        if (UnrecognizedWordsListBox.SelectedItem is not ReviewItem item) return;
        // Двойной щелчок — это тоже осознанный перенос в ручную доводку: слово добавляется
        // туда, потому что сама вкладка наполняется только перенесёнными словами.
        AddWordToManualReview(ExactWordKey(item.Word));
        ApplyReviewItems();
        ReviewTabs.SelectedIndex = 3;
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
        // Отбор по фильтру считается один раз: раньше для каждой группы заново
        // перебирались все записи отчёта, и на большой книге (десятки тысяч слов)
        // ввод в поле поиска надолго замораживал окно.
        var filterKeys = string.IsNullOrEmpty(filter)
            ? null
            : new HashSet<string>(_entries
                .Where(entry => entry.Word.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || entry.Context.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(entry => ExactWordKey(entry.Word)), StringComparer.Ordinal);
        var groups = _entries.Select((entry, index) => (entry, index))
            .GroupBy(pair => ExactWordKey(pair.entry.Word), StringComparer.Ordinal)
            // В ручную доводку слово попадает только после перетаскивания сюда из списков
            // вкладок 2–3. Перенесённым считается конкретный случай; если у слова
            // перенесены не все вхождения, в списке видно, сколько случаев уже здесь.
            .Where(group => group.Any(pair => IsManualReviewCase(pair.entry)))
            .Select(group =>
            {
                var all = group.ToArray();
                var manual = all.Where(pair => IsManualReviewCase(pair.entry)).ToArray();
                if (manual.Length == 0) manual = all;
                var first = manual[0];
                var count = _wordCounts.TryGetValue(first.entry.Word, out var found) ? found : 0;
                var key = ExactWordKey(first.entry.Word);
                var ru = all.Select(pair => pair.entry.RuAccent).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var silero = all.Select(pair => pair.entry.Silero).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var hasSuggestion = _llmSuggestions.TryGetValue(key, out var suggestion);
                var hint = string.Join(" / ", new[] { ru == null ? null : "R: " + ru, silero == null ? null : "S: " + silero,
                    hasSuggestion ? "ИИ: " + suggestion : AiRan ? "⚠ ИИ не уверен" : null }.Where(value => value != null));
                var done = manual.Any(pair => pair.entry.Checked);
                var detail = hint.Length > 0 ? "  ·  " + hint : string.Empty;
                var scope = manual.Length < all.Length ? $"  ·  перенесено случаев: {manual.Length}" : string.Empty;
                return new ReviewItem(first.index, $"{(done ? "✓ " : "")}{first.entry.Word} — {count:N0} вхождений{scope}{detail}", first.entry.Word, count, done);
            })
            .Where(item => ShowCheckedCheckBox.IsChecked == true || !item.Done)
            .Where(item => filterKeys == null || filterKeys.Contains(ExactWordKey(item.Word)))
            .Where(item => ManualFilterAllows(item.Word));
        return (SortModeComboBox.SelectedIndex == 0
            ? groups.OrderBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)
            : groups.OrderByDescending(item => _wordCounts.TryGetValue(item.Word, out var count) ? count : 0).ThenBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    // Фильтр вкладки 4: все расхождения, только слова с вариантом ИИ или только без варианта.
    private bool ManualFilterAllows(string word)
    {
        var action = ManualFilterComboBox.SelectedIndex;
        if (action <= 0) return true;
        var key = ExactWordKey(word);
        if (action == 1) return !_llmSuggestions.ContainsKey(key) && _entries.Any(entry => ExactWordKey(entry.Word) == key && entry.NeedsManual);
        return _entries.Any(entry => ExactWordKey(entry.Word) == key && !HasModelDisagreement(entry));
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

    // Размер меняется раньше, чем станут известны новые размеры элементов, поэтому пересчёт
    // повторяется после раскладки — иначе остались бы прежние высоты.
    private void FitWindowLayoutSoon()
    {
        FitWindowLayout();
        Dispatcher.BeginInvoke(new Action(FitWindowLayout));
    }

    // Шапке и строке статуса задаётся точная высота: вкладка при нехватке места
    // поджимает строки-авто, и её содержимое наезжает на шапку. Желаемая высота
    // (DesiredSize) не зависит от текущей раскладки, поэтому пересчёт безопасен.
    private void FitWindowLayout()
    {
        var headerHeight = Math.Ceiling(HeaderPanel.DesiredSize.Height);
        if (headerHeight > 0 && Math.Abs(HeaderRow.Height.Value - headerHeight) > 0.5)
            HeaderRow.Height = new GridLength(headerHeight);
        var statusHeight = Math.Ceiling(StatusPanel.DesiredSize.Height);
        if (statusHeight > 0 && Math.Abs(StatusRow.Height.Value - statusHeight) > 0.5)
            StatusRow.Height = new GridLength(statusHeight);
        // Редактор фрагмента занимает всю оставшуюся высоту вкладки: панели ударения
        // не должны вытеснять текст, который идёт править. 360 — это панель ударения,
        // заголовок и подвал редактора, блок словоформ и строка с кнопкой «Да ✓».
        var tabsHeight = ReviewTabs.ActualHeight;
        if (tabsHeight <= 0) return;
        var editorHeight = Math.Clamp(tabsHeight - 360, 140, 900);
        if (Math.Abs(FragmentEditor.Height - editorHeight) > 4) FragmentEditor.Height = editorHeight;
    }

    // Запоминает пару «отчёт + редактируемый файл»: к документу можно вернуться
    // при следующем запуске, в том числе чтобы изменить уже принятые решения.
    private void RememberSession()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return;
        Program.SaveLastSession(_reviewPath, _outputPath, InputTextBox.Text);
        RefreshContinueWorkButton();
    }

    private void ChooseReview_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите файл нераспознанных слов", Filter = "Отчёт слов (*.txt)|*.txt|Все файлы (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _reviewPath = dialog.FileName;
        ReviewFileTextBox.Text = _reviewPath;
        // Если рядом уже есть отредактированная копия, продолжаем править её:
        // иначе новые правки легко применить к исходнику и потерять старые.
        var corrected = CorrectedSiblingPath(_reviewPath);
        if (corrected != null)
        {
            _outputPath = corrected;
            Fb2FileTextBox.Text = corrected;
            FileStatusTextBlock.Text = "Найден уже отредактированный файл, правки пойдут в него:\n" + corrected;
        }
        LoadReviewFiles();
        RememberSession();
    }

    // Ищем в рабочей папке уже готовый итог «имя_итог.fb2/.txt», чтобы продолжать
    // править именно его, а не создавать заново.
    private static string? CorrectedSiblingPath(string reviewPath)
    {
        try
        {
            if (Program.WorkspaceRootOf(reviewPath) is not { } root || !Directory.Exists(root)) return null;
            foreach (var extension in new[] { ".fb2", ".txt" })
            {
                var candidate = Directory.EnumerateFiles(root, "*_итог" + extension, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (candidate != null) return candidate;
            }
        }
        catch (Exception ex) { Program.LogError("Поиск итогового файла в рабочей папке", ex); }
        return null;
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
        RememberSession();
    }

    private void OpenCorrections_Click(object sender, RoutedEventArgs e) => OpenDeclensionFile();
    private void AddForm_Click(object sender, RoutedEventArgs e) => AddDeclensionRow();
    private void ApplyAll_Click(object sender, RoutedEventArgs e) => ApplyAllCorrections();

    // «Сохранить» — прогресс без сборки итога: отчёт с пометками «Проверено» и уже
    // применённые к файлу правки. Книга и прогоны движков при этом не изменяются.
    private void SaveProgress_Click()
    {
        SaveFragmentDrafts();
        SaveReviewState();
        RememberSession();
        FileStatusTextBlock.Text = _reviewPath != null && File.Exists(_reviewPath)
            ? $"Прогресс сохранён.\nОтчёт: {_reviewPath}\nРедактируемый файл: {_outputPath ?? "—"}"
            : "Пока нечего сохранять: сначала обработайте книгу или откройте отчёт кнопкой «Открыть отчёт и файл».";
    }

    // «Завершить работу» — собрать итог из базы add-stress и всех правок.
    private void FinishWork_Click()
{
    ApplyAllCorrections();
    if (!string.IsNullOrWhiteSpace(_outputPath))
    {
        var dir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
            catch { }
        }
    }
}
    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateGroup(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateGroup(1);
    private void SaveCorrection_Click(object sender, RoutedEventArgs e) => ApplyGroupCorrection();
    private void LoadFiles_Click(object sender, RoutedEventArgs e) => LoadReviewFiles();
    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildGroupList();
    private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildGroupList();

    // Галочка одна на обе вкладки: показывать ли слова с уже применённой правкой.
    // Значение берём у нажатой галочки, иначе вторая копия тут же отменяет первую.
    private void ShowCheckedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingCheckedFilter) return;
        var value = sender is CheckBox box ? box.IsChecked : ShowCheckedCheckBox.IsChecked;
        _updatingCheckedFilter = true;
        ShowCheckedCheckBox.IsChecked = value;
        ShowCheckedManualCheckBox.IsChecked = value;
        _updatingCheckedFilter = false;
        ApplyReviewItems();
        UpdateNavigationState();
        if (value == true) return;
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || !selected.Done) return;
        // Выбранное слово только что скрылось — переходим на первое неразобранное.
        if (WordGroupsListBox.Items.Count > 0) WordGroupsListBox.SelectedIndex = 0;
    }

    private async void LlmCheck_Click(object sender, RoutedEventArgs e)
    {
        // omogre разбирает каждый случай по контексту: результат показывается не списком
        // подсказок, а страницей «3. Контроль ИИ результатов».
        if (_llmEngine.IsOmogre) { await RunOmogreCaseCheckAsync(recheck: false); return; }
        await RunLlmCheckAsync();
        // Появились ответы ИИ по словам: то, что уже разобрано, уходит со вкладки 4.
        RefreshNeedsManualFlags();
        RebuildGroupList();
    }

    // Кнопка УНИК открывает панель ударения по любой букве. Нужна она редко — для слов
    // древней орфографии, где ударная не гласная, — поэтому по умолчанию скрыта.
    private void UniqueStressToggle_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = UniqueStressToggle.IsChecked == true;
        UniqueStressPanelBox.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            UniqueStressPanel.Children.Clear();
            return;
        }
        RebuildUniqueStressChoices();
        FileStatusTextBlock.Text = "Режим «уникальное ударение»: кликните по любой букве слова. Кнопка УНИК снова выключит панель.";
    }

    // Повторный запрос по уже проверенной книге: модель могла обновиться или смениться,
    // а прежние ответы остались бы в кэше и повтор их не тронул бы.
    private async void LlmRecheck_Click(object sender, RoutedEventArgs e)
    {
        await RerunLlmCheckAsync();
        // Новые ответы заменяют прежние: раскладка вкладки 4 пересчитывается по ним.
        RefreshNeedsManualFlags();
        RebuildGroupList();
    }

    private async Task RerunLlmCheckAsync()
    {
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, "Откройте отчёт со словами — перепроверять пока нечего.", "Нечего проверять", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var words = _entries.Select(entry => entry.Word).Distinct(StringComparer.Ordinal).Count();
        var checkedWords = _entries.Count(entry => entry.Checked);
        // Перепроверка идёт по всему отчёту: разбор по случаям касается и тех слов,
        // которые после первой проверки ушли со вкладки 4 на страницу контроля ИИ.
        var omogre = _llmEngine.IsOmogre;
        if (MessageBox.Show(this,
                $"Перепроверить заново {words:N0} слов движком «{_llmEngine.Title}»?\n\n" +
                (checkedWords > 0
                    ? $"Из них {checkedWords:N0} уже проверены — их решения никуда не денутся, но новые ответы ИИ могут оказаться другими.\n"
                    : string.Empty) +
                (omogre
                    ? "omogre заново разберёт каждый случай расхождения по контексту и разложит омографы по двум вариантам ударения на вкладке «3. Контроль ИИ результатов».\n\n"
                    : "Прежние ответы ИИ по этим словам будут заменены. На текст ничего не применяется: каждое слово подтверждается отдельно кнопкой «Да ✓».\n\n") +
                "Проход может занять несколько минут.",
                "Перепроверка с помощью ИИ", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (omogre) await RunOmogreCaseCheckAsync(recheck: true);
        else await RunLlmCheckAsync(recheck: true);
    }

    // --- Движок подсказок: своя модель на диске, свой сервер Ollama или внешний API ----------
    // Настройки сохраняются сразу при изменении: их читают и окно, и командная строка.
    // omogre стоит первым: это локальная контекстная модель омографов, лучший выбор для
    // спорных случаев (RUAccent и silero разошлись) — ей и отдаётся разбор по контексту.
    private static readonly (string Title, string Value)[] LlmProviders =
    {
        ("omogre — контекстные ударения (локально)", Program.LlmEngine.Omogre),
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
        InitializeLlmDevice();
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
        LlmOmogrePanel.Visibility = provider == Program.LlmEngine.Omogre ? Visibility.Visible : Visibility.Collapsed;
        RefreshLlmEngineText();
    }

    private void RefreshLlmEngineText() =>
        LlmEngineTextBlock.Text = "Сейчас выбран: " + _llmEngine.Title +
            ". Подсказки считаются отдельно для каждого движка и не смешиваются.";

    // --- Видеокарта для движков подсказок на PyTorch -------------------------------------------
    // RUAccent считает через ONNX Runtime: у него своя кнопка «Проверить GPU / CUDA» в первой
    // вкладке. silero-stress и omogre работают через PyTorch, поэтому у них отдельный выбор.
    // Сборка PyTorch ставится сразу для процессора, а сборка с CUDA — по кнопке: она весит
    // около трёх гигабайт. Выбор устройства сохраняется, поэтому restart ничего не сбрасывает.
    private const string LlmDeviceCuda = "CUDA";

    private void InitializeLlmDevice()
    {
        _updatingLlmDevice = true;
        LlmDeviceComboBox.ItemsSource = new[] { "Процессор (CPU)", "Видеокарта (CUDA)" };
        LlmDeviceComboBox.SelectedIndex = Program.LoadLlmDevice() == LlmDeviceCuda ? 1 : 0;
        _updatingLlmDevice = false;
        LlmDeviceComboBox.SelectionChanged += (_, _) => OnLlmDeviceChanged();
        LlmCudaProbeButton.Click += async (_, _) => await ProbeTorchCudaAsync(silent: false);
        LlmCudaInstallButton.Click += async (_, _) => await InstallTorchCudaAsync();
        RefreshLlmDeviceStatus();
        // Состояние сборки показываем сами при открытии окна: без этого непонятно, что стоит,
        // и кнопку установки пришлось бы нажимать вслепую.
        Loaded += (_, _) => _ = ProbeTorchCudaAsync(silent: true);
    }

    private string SelectedLlmDevice() =>
        LlmDeviceComboBox.SelectedIndex == 1 ? LlmDeviceCuda : "CPU";

    private void OnLlmDeviceChanged()
    {
        if (_updatingLlmDevice) return;
        Program.SaveLlmDevice(SelectedLlmDevice());
        RefreshLlmDeviceStatus();
    }

    private void SetLlmDevice(string device)
    {
        _updatingLlmDevice = true;
        LlmDeviceComboBox.SelectedIndex = device == LlmDeviceCuda ? 1 : 0;
        _updatingLlmDevice = false;
        Program.SaveLlmDevice(device);
        RefreshLlmDeviceStatus();
    }

    // Подпись под кнопками: что выбрано и что стоит на самом деле.
    private void RefreshLlmDeviceStatus()
    {
        if (LlmDeviceStatusTextBlock == null) return;
        var cuda = SelectedLlmDevice() == LlmDeviceCuda;
        var text = cuda ? "Сейчас выбран счёт на видеокарте." : "Сейчас выбран счёт на процессоре.";
        var status = _torchStatus;
        if (status == null)
        {
            text += " Нажмите «Проверить», чтобы узнать, какая сборка PyTorch стоит и видит ли она видеокарту.";
            LlmCudaInstallButton.Content = "Поставить сборку с CUDA";
        }
        else
        {
            text += " " + status.Message;
            var cudaReady = status.Available && status.CudaBuild.Length > 0;
            LlmCudaInstallButton.Content = cudaReady ? "Вернуть сборку для процессора" : "Поставить сборку с CUDA";
            if (cuda && !cudaReady)
                text += " Пока CUDA недоступна, движки считают на процессоре — поставить сборку можно этой кнопкой.";
        }
        LlmDeviceStatusTextBlock.Text = text;
    }

    private async Task ProbeTorchCudaAsync(bool silent)
    {
        if (_torchProbeRunning) return;
        _torchProbeRunning = true;
        LlmCudaProbeButton.IsEnabled = false;
        var failure = "";
        try
        {
            if (!silent) LlmDeviceStatusTextBlock.Text = "Смотрю, какая сборка PyTorch стоит и видит ли она видеокарту…";
            _torchStatus = await Task.Run(Program.ProbeTorchCuda);
        }
        catch (Exception error)
        {
            failure = "Проверить сборку PyTorch не удалось: " + error.GetBaseException().Message;
        }
        finally
        {
            _torchProbeRunning = false;
            LlmCudaProbeButton.IsEnabled = true;
        }
        RefreshLlmDeviceStatus();
        if (failure.Length > 0) LlmDeviceStatusTextBlock.Text = failure;
    }

    // Установка занимает минуты и весит гигабайты, поэтому спрашиваем подтверждение, а ход
    // работы показываем той же строкой этапа, что и подготовка окружения.
    private async Task InstallTorchCudaAsync()
    {
        var status = _torchStatus;
        var install = status == null || !(status.Available && status.CudaBuild.Length > 0);
        var question = install
            ? "Поставить сборку PyTorch с поддержкой CUDA?\n\n"
                + "Придётся скачать около трёх гигабайт, это займёт несколько минут. Сборка заменит сборку "
                + "для процессора и займёт примерно на три гигабайта больше места.\n\n"
                + "Считать на видеокарте будут локальные движки подсказок: silero-stress и omogre. "
                + "Ответы при этом не изменятся — меняется только то, где выполняется счёт.\n\n"
                + "Вернуть сборку для процессора можно той же кнопкой."
            : "Вернуть сборку PyTorch для процессора?\n\n"
                + "Счёт снова пойдёт на процессоре, а место освободится примерно на три гигабайта.";
        if (MessageBox.Show(this, question, install ? "Сборка PyTorch с CUDA" : "Сборка для процессора",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        LlmCudaInstallButton.IsEnabled = false;
        LlmCudaProbeButton.IsEnabled = false;
        StartActivity(install ? "Установка сборки PyTorch с CUDA" : "Возврат сборки PyTorch для процессора");
        Program.SetupProgress = message => Dispatcher.Invoke(() => SetActivityStage(message));
        try
        {
            var message = await Task.Run(() => install ? Program.InstallTorchCudaRuntime() : Program.InstallTorchCpuRuntime());
            _torchStatus = Program.ProbeTorchCuda();
            StopActivity(install ? "Сборка с CUDA готова" : "Сборка для процессора возвращена");
            SetLlmDevice(install ? LlmDeviceCuda : "CPU");
            LlmDeviceStatusTextBlock.Text = message;
        }
        catch (Exception error)
        {
            StopActivity("Установка не удалась");
            _torchStatus = null;
            RefreshLlmDeviceStatus();
            var detail = error.GetBaseException().Message;
            LlmDeviceStatusTextBlock.Text = detail;
            MessageBox.Show(this, detail, "Сборка PyTorch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Program.SetupProgress = null;
            LlmCudaInstallButton.IsEnabled = true;
            LlmCudaProbeButton.IsEnabled = true;
        }
    }

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
    private async Task RunLlmCheckAsync(bool recheck = false)
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
            .Where(entry => !string.IsNullOrWhiteSpace(entry.RuAccent) || !string.IsNullOrWhiteSpace(entry.Silero))
            .GroupBy(entry => ExactWordKey(entry.Word), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => !string.IsNullOrWhiteSpace(item.RuAccent) || !string.IsNullOrWhiteSpace(item.Silero)).ToArray())
            .ToList();
        if (groups.Count == 0)
        {
            FileStatusTextBlock.Text = "В отчёте нет расхождений RUAccent/Silero для ИИ-проверки.";
            return;
        }
        var book = !string.IsNullOrWhiteSpace(_outputPath) ? _outputPath! : _reviewPath!;
        var cache = Program.LoadLlmSuggestions();
        var failures = Program.LoadLlmFailures();
        var reuse = LlmReuseCheckBox.IsChecked == true && !recheck;
        // Перепроверка — осознанный повторный запрос по всем словам: модель могла обновиться
        // или смениться, а прежний ответ мог быть сделан другой версией. Старые ответы,
        // в том числе из кэша, выбрасываются — иначе повтор вернул бы то же самое.
        if (recheck)
        {
            foreach (var group in groups)
            {
                var word = group[0].Word;
                var key = ExactWordKey(word);
                _llmSuggestions.Remove(key);
                _llmRejected.Remove(key);
                cache.Remove(Program.LlmCacheKey(_llmEngine.Id, book, word));
                failures.Remove(Program.LlmCacheKey(_llmEngine.Id, book, word));
            }
            Program.LogReview($"Перепроверка ИИ по {_llmEngine.Title}: слов {groups.Count}, книга {Path.GetFileName(book)}");
        }
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
            LlmRecheckButton.IsEnabled = false;
            // silero-stress и omogre отвечают пачками и не присылают по-словный ход,
            // поэтому для них оставляем неопределённый прогресс без списка слов.
            var progress = new LlmProgressWindow(total: _llmEngine.IsSilero || _llmEngine.IsOmogre ? 0 : pending.Count);
            progress.Owner = this;
            progress.Show();
            try
            {
                var items = pending;
                var outcome = await Task.Run(() => Program.RunLlmStressCheck(items, book,
                    message => Dispatcher.Invoke(() => { if (progress.IsLoaded) progress.SetStatus(message); }),
                    progress.Token,
                    wordProgress: word => Dispatcher.Invoke(() => { if (progress.IsLoaded) progress.ReportWord(word); })));
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
                LlmRecheckButton.IsEnabled = true;
            }
        }

        RebuildGroupList();
        // Сразу показываем первую группу с готовой подсказкой: иначе результат ИИ легко не заметить.
        var listed = WordGroupsListBox.Items.Cast<ReviewItem>().ToArray();
        if (listed.Length > 0 && WordGroupsListBox.SelectedItem == null) WordGroupsListBox.SelectedIndex = 0;
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
        // Ответы по уже проверенным словам не видны в списке: подсказываем, где их искать.
        var checkedWithSuggestion = _entries.Count(entry => entry.Checked && _llmSuggestions.ContainsKey(ExactWordKey(entry.Word)));
        if (checkedWithSuggestion > 0 && ShowCheckedCheckBox.IsChecked != true)
        {
            ShowCheckedCheckBox.IsChecked = true;
            summary += Environment.NewLine +
                $"Показаны и уже проверенные слова: в {checkedWithSuggestion} из них ИИ предложил другой вариант.";
        }
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

    // Немодальное окно ИИ-прохода. Показывает, что делает модель, живой список
    // «слово — думаю / результат», общий прогресс, нагрузку на процессор, память и
    // видеокарту, и позволяет прервать проход.
    private sealed class LlmProgressWindow : Window
    {
        private static readonly Brush ThinkingBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x7A, 0x63));
        private static readonly Brush AnsweredBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0x3C));
        private static readonly Brush NoAnswerBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0x53, 0x2C));

        private readonly TextBlock _status;
        private readonly TextBlock _load;
        private readonly TextBlock _counters;
        private readonly ProgressBar _bar;
        private readonly ScrollViewer? _scroll;
        private readonly StackPanel? _list;
        private readonly Dictionary<string, (TextBlock Row, bool Answered)> _rows = new(StringComparer.Ordinal);
        private readonly DispatcherTimer _timer;
        private readonly LoadMonitor _monitor = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly int _total;

        public CancellationToken Token => _cancellation.Token;

        public LlmProgressWindow(string headline = "Выбранный движок подбирает ударение для слов, которых не было в словаре. Это занимает несколько минут и нагружает видеокарту.", int total = 0)
        {
            _total = total;
            Title = "ИИ-проверка ударений";
            Width = 640;
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
            _bar = new ProgressBar
            {
                Height = 6,
                Margin = new Thickness(0, 14, 0, 4),
                Minimum = 0,
                Maximum = total > 0 ? total : 100,
                IsIndeterminate = total <= 0,
                Value = 0,
            };
            panel.Children.Add(_bar);
            _counters = new TextBlock
            {
                Text = total > 0 ? $"0 из {total}" : string.Empty,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = ThinkingBrush,
            };
            panel.Children.Add(_counters);
            _status = new TextBlock { Text = "Запускаю модель…", TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_status);
            if (total > 0)
            {
                _list = new StackPanel();
                _scroll = new ScrollViewer
                {
                    Height = 240,
                    Margin = new Thickness(0, 10, 0, 0),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xF6, 0xEC)),
                    Padding = new Thickness(8, 6, 8, 6),
                    Content = _list,
                };
                panel.Children.Add(_scroll);
            }
            _load = new TextBlock
            {
                Text = "Нагрузка: считаю…",
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThinkingBrush,
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

        // Живая строка списка: слово сразу помечается «думаю», а после ответа модели —
        // готовым написанием (или пометкой, что ответа нет). Так видно, что проход идёт.
        public void ReportWord(Program.LlmWordProgress progress)
        {
            if (_list == null || _scroll == null) return;
            var fresh = !_rows.TryGetValue(progress.Word, out var entry);
            if (fresh)
            {
                var row = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), FontSize = 12 };
                entry = (row, false);
                _rows[progress.Word] = entry;
                _list.Children.Add(row);
            }
            switch (progress.Phase)
            {
                case Program.LlmWordPhase.Thinking:
                    entry.Row.Text = (fresh ? "…  " : "…  (повтор)  ") + progress.Word;
                    entry.Row.Foreground = ThinkingBrush;
                    entry.Answered = false;
                    break;
                case Program.LlmWordPhase.Answered:
                    entry.Row.Text = "✓  " + progress.Word + "  →  " + progress.Suggestion;
                    entry.Row.Foreground = AnsweredBrush;
                    entry.Answered = true;
                    break;
                default:
                    entry.Row.Text = "·  " + progress.Word + "  —  нет ответа";
                    entry.Row.Foreground = NoAnswerBrush;
                    entry.Answered = false;
                    break;
            }
            _rows[progress.Word] = entry;
            UpdateCounters();
            _scroll.ScrollToEnd();
        }

        private void UpdateCounters()
        {
            var answered = 0;
            foreach (var row in _rows.Values)
                if (row.Answered) answered++;
            var resolved = _rows.Count;
            if (_bar.Maximum > 0) _bar.Value = Math.Min(resolved, _bar.Maximum);
            _counters.Text = _total > 0
                ? $"{resolved} из {_total} · с ответом {answered} · без ответа {resolved - answered}"
                : string.Empty;
        }

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
        else AiVerdictPanel.Visibility = Visibility.Collapsed;
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
            LoadEntriesFromReport();
            RefreshWordCounts();
            _selectedGroupAnchor = -1;
            _selectedSampleIndex = -1;
            ApplyReviewItems();
            var pending = _entries.Count(entry => !entry.Checked);
            FileStatusTextBlock.Text = _entries.Count == 0 ? "Отчёт пуст или не содержит слов для проверки."
                : $"Загружено случаев: {_entries.Count}; ждут решения: {pending}. Сортировка: {SortModeComboBox.SelectedItem}. Вкладка «Ручная доводка» пуста: перетащите на неё нужные фрагменты из списков вкладок 2–3." +
                  (pending == 0 ? " Все слова этого документа уже проверены — снимите галочку «Показывать проверенные», чтобы вернуться к любому из них и изменить решение." : string.Empty);
            WordGroupsListBox.Focusable = true;
            WordGroupsListBox.IsTabStop = true;
            ContextTextBlock.Text = "Выберите группу слова слева: будет отредактирована вся группа.";
            if (WordGroupsListBox.Items.Count > 0) WordGroupsListBox.SelectedIndex = 0;
            else RebuildStressChoices(); // пустые панели должны подсказывать, что делать дальше
            UpdateEditorEnabled();
            UpdateNavigationState();
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть отчёт: " + ex.Message, "Ошибка чтения", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // Читает отчёт целиком и отмечает слова, к которым правка уже применена.
    private void LoadEntriesFromReport()
    {
        _entries.Clear();
        _llmSuggestions.Clear();
        _llmRejected.Clear();
        _controlVariants.Clear();
        _controlGroups = new();
        _controlGroupIndex = -1;
        // Вкладка ручной доводки наполняется вручную: при открытии другого отчёта список пуст.
        // Пометки о переносе читаются из отчёта ниже: прогресс по конкретным случаям
        // сохраняется между запусками, а не теряется при закрытии окна.
        _manualReviewKeys.Clear();
        _manualReviewCaseKeys.Clear();
        // Пометки «прочитано» и стек отмены относятся к прежнему отчёту: начинаем заново.
        // Журнал правок тоже ведётся по открытому сейчас отчёту.
        _controlViewedIndices.Clear();
        _controlMoveHistory.Clear();
        _controlJournal.Clear();
        _refreshControlJournalWindow?.Invoke();
        _hasModelDisagreements = false;
        var checkedWords = Program.ReadCheckedWords(_reviewPath!);
        // Кэш подсказок читается один раз: раньше он разбирался заново на каждую
        // строку отчёта, и на больших книгах (десятки тысяч слов) это надолго
        // замораживало окно сразу после прогона Silero.
        var llmCache = Program.LoadLlmSuggestions();
        // Ответы ИИ по конкретным вхождениям: по ним считается, где ИИ была права.
        var caseAnswers = Program.LoadCaseAnswers(_reviewPath);
        _trainingLogged.Clear();
        // Ответы ИИ по вхождениям решают, что идёт в ручную доводку: таблица заполняется один раз.
        Program.RefreshManualReviewCandidates(_reviewPath);
        var book = _outputPath ?? _reviewPath!;
        foreach (var entry in Program.ReadWordReview(_reviewPath!))
        {
            // Слова с жёстко заданным ударением не должны попадать в списки проверки:
            // их ударение уже зафиксировано словарём, и предлагать варианты для них бессмысленно.
            if (Program.HasFixedStress(entry.Word)) continue;
            var savedSuggestion = Program.FindAnyLlmSuggestion(llmCache, book, entry.Word);
            var loaded = savedSuggestion == null || (!string.IsNullOrWhiteSpace(entry.RuAccent) && !string.IsNullOrWhiteSpace(entry.Silero))
                ? entry : entry with { Proposed = savedSuggestion };
            var mutable = new MutableReviewEntry(loaded)
            {
                Checked = checkedWords.Contains(Program.ReviewWordKey(entry.Word)),
                Manual = entry.Manual,
            };
            if (entry.Manual) _manualReviewCaseKeys.Add(ManualCaseKey(mutable));
            if (caseAnswers.TryGetValue(Program.LlmCaseKey(mutable.Word, mutable.Context, mutable.ContextOccurrence), out var aiForm))
                mutable.AiForm = aiForm;
            // На вкладке 4 остаются только опечатки и нераспознанные: случай, который
            // разобрала ИИ, попадает на страницу контроля (3), а не в ручную доводку.
            mutable.NeedsManual = Program.IsManualReviewCandidate(entry);
            _entries.Add(mutable);
            _hasModelDisagreements |= !string.IsNullOrWhiteSpace(entry.RuAccent) || !string.IsNullOrWhiteSpace(entry.Silero);
        }
        LlmCheckButton.IsEnabled = _hasModelDisagreements;
        LlmRecheckButton.IsEnabled = _hasModelDisagreements;
        ManualReviewStepButton.IsEnabled = _entries.Count > 0;
        _fragmentDrafts.Clear();
        if (!string.IsNullOrWhiteSpace(_reviewPath))
            foreach (var edit in Program.ReadFragmentEdits(_reviewPath))
                _fragmentDrafts[edit.Context] = edit.Edited;
    }

    private void CorrectionChanged()
    {
        if (_updatingCorrection || WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return;
        var corrected = CorrectionTextBox.Text;
        foreach (var entry in GetCorrectionTargets()) entry.Proposed = corrected;
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
            var onIndividualCase = _selectedSampleIndex >= 0 && _selectedSampleIndex < _entries.Count && !_entries[_selectedSampleIndex].Included;
            ContextTextBlock.Text = onIndividualCase
                ? "Отдельное ударение этого случая (флажок снят): оно применится только к этому вхождению."
                : "Правка идёт по отмеченным образцам (общая форма группы). Снимите галочку у нужного случая, чтобы задать ему отдельное ударение.";
        }
        UpdateSelectedSampleDetail();
        // Отражение правки в большом редакторе. Если текст ввели прямо там, обновлять не нужно.
        if (!_wordEditFromEditor) UpdateEditorWord();
        if (!_fragmentWordEditing)
        {
            _fragmentBaseline = _fragmentBefore + CorrectionTextBox.Text + _fragmentAfter;
            // Случай, перенесённый отдельно, не должен менять общую форму всего слова.
            // Поэтому его полный фрагмент сохраняется как отдельная правка.
            if (IsCaseLevelCurrentGroup())
            {
                var index = _selectedSampleIndex >= 0 && _selectedSampleIndex < _entries.Count ? _selectedSampleIndex : (WordGroupsListBox.SelectedItem as ReviewItem)?.Index ?? -1;
                if (index >= 0 && index < _entries.Count)
                {
                    var visible = Program.SplitReviewContext(_entries[index].Context).Context;
                    _fragmentDrafts[visible] = DocumentText();
                    ScheduleFragmentDraftSave();
                }
            }
        }
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
            // Номер проверяемого слова среди слов фрагмента: он нужен, чтобы после
            // свободной правки текста снова подсветить именно это слово.
            var fullText = before + word + after;
            var matches = Regex.Matches(fullText, @"[\p{L}\p{M}]+");
            _fragmentTargetTokenIndex = -1;
            for (var i = 0; i < matches.Count; i++)
                if (matches[i].Index == before.Length) { _fragmentTargetTokenIndex = i; break; }
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

    private (string Before, string Word, string After) SplitFragmentForTarget(string fullText, string targetWord, int targetOccurrence)
    {
        var matches = Regex.Matches(fullText, @"[\p{L}\p{M}]+");
        var index = -1;
        var seen = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            if (Program.ReviewWordKey(matches[i].Value) == Program.ReviewWordKey(targetWord))
            {
                if (seen == targetOccurrence) { index = i; break; }
                seen++;
            }
        }
        if (index < 0 && _fragmentTargetTokenIndex >= 0 && _fragmentTargetTokenIndex < matches.Count)
            index = _fragmentTargetTokenIndex;
        if (index < 0 || index >= matches.Count)
            return (fullText, targetWord, string.Empty);
        var match = matches[index];
        _fragmentTargetTokenIndex = index;
        return (fullText[..match.Index], match.Value, fullText[(match.Index + match.Length)..]);
    }

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
        var doc = DocumentText();
        SaveCurrentFragmentDraft(doc);
        // Если пользователь правил именно проверяемое слово (жирный фрагмент), переносим
        // его написание в рабочее поле, чтобы кнопки ударения работали с новым словом.
        var run = CurrentWordRun();
        if (run != null && !string.IsNullOrWhiteSpace(run.Text) && !run.Text.Any(char.IsWhiteSpace)
            && !string.Equals(run.Text, CorrectionTextBox.Text, StringComparison.Ordinal))
        {
            _wordEditFromEditor = true;
            try { CorrectionTextBox.Text = run.Text; }
            finally { _wordEditFromEditor = false; }
        }
    }

    private void SaveCurrentFragmentDraft() => SaveCurrentFragmentDraft(DocumentText());

    private void SaveCurrentFragmentDraft(string doc)
    {
        if (_updatingFragment) return;
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index < 0 || selected.Index >= _entries.Count) return;
        var index = _selectedSampleIndex >= 0 && _selectedSampleIndex < _entries.Count ? _selectedSampleIndex : selected.Index;
        var entry = _entries[index];
        var visible = Program.SplitReviewContext(entry.Context).Context;
        var baseline = _fragmentBaseline;
        if (string.IsNullOrWhiteSpace(doc) || string.Equals(doc, baseline, StringComparison.Ordinal))
            _fragmentDrafts.Remove(visible);
        else
            _fragmentDrafts[visible] = doc;
        ScheduleFragmentDraftSave();
    }

    private void ScheduleFragmentDraftSave()
    {
        _fragmentDraftSaveTimer.Stop();
        _fragmentDraftSaveTimer.Start();
    }

    private void FragmentEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingFragment) return;
        var selectedText = FragmentEditor.Selection.Text.Trim();
        if (selectedText.Length > 0 && !selectedText.Any(char.IsWhiteSpace) && selectedText.Any(char.IsLetter))
        {
            _fragmentWordEditing = true;
            _workingWord = PlainLetters(selectedText);
            RebuildStressChoices();
        }
        else if (_fragmentWordEditing)
        {
            _fragmentWordEditing = false;
            _workingWord = PlainLetters(CorrectionTextBox.Text);
            RebuildStressChoices();
        }
    }

    private string ActiveWordText => _fragmentWordEditing ? FragmentEditor.Selection.Text.Trim() : CorrectionTextBox.Text;

    private void SaveFragmentDrafts()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return;
        try
        {
            var edits = _fragmentDrafts.Select(pair =>
            {
                var entry = _entries.FirstOrDefault(item => Program.SplitReviewContext(item.Context).Context == pair.Key);
                return new Program.WordReviewFragmentEdit(pair.Key, pair.Value, entry?.Word ?? string.Empty, entry?.ContextOccurrence ?? 0);
            }).ToList();
            Program.SaveFragmentEdits(_reviewPath!, edits);
        }
        catch (Exception ex) { Program.LogError("Сохранение правок фрагментов", ex); }
    }

    // Полный фрагмент выбранного образца переносится в редактор целиком.
    private void RenderSelectedSample()
    {
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index < 0 || selected.Index >= _entries.Count) return;
        var wordKey = ExactWordKey(_entries[selected.Index].Word);
        var index = _selectedSampleIndex;
        if (index < 0 || index >= _entries.Count || ExactWordKey(_entries[index].Word) != wordKey)
            index = _entries.FindIndex(entry => ExactWordKey(entry.Word) == wordKey && IsManualReviewCase(entry));
        if (index < 0) index = _entries.FindIndex(entry => ExactWordKey(entry.Word) == wordKey);
        if (index < 0) return;
        var entry = _entries[index];
        var sample = SampleItem.For(index, entry.Context, entry.Word, entry.Included, SampleDetailFor(entry));
        var word = CorrectionTextBox.Text.Length > 0 ? CorrectionTextBox.Text : DefaultInitialStress(entry.Word);
        var visible = Program.SplitReviewContext(entry.Context).Context;
        if (_fragmentDrafts.TryGetValue(visible, out var draft))
        {
            var split = SplitFragmentForTarget(draft, word, entry.ContextOccurrence);
            RenderFragment(split.Before, split.Word, split.After);
        }
        else
        {
            RenderFragment(sample.Before, word, sample.After);
        }
        _fragmentBaseline = sample.Before + word + sample.After;
        if (_fragmentDrafts.TryGetValue(visible, out var edited)) _fragmentBaseline = edited.Length > 0 ? edited : _fragmentBaseline;
        // Базовой считается исходная форма без правок пользователя; черновик хранится отдельно.
        _fragmentBaseline = sample.Before + word + sample.After;
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

    // Шрифт примеров на странице контроля: применяется ко всем трём окнам сразу
    // и запоминается, чтобы при следующем запуске не настраивать заново.
    private void ChangeControlFontSize(double delta)
    {
        _controlFontSize = Math.Clamp(_controlFontSize + delta, 10, 30);
        ApplyControlFontSize();
        Program.SaveControlFontSize(_controlFontSize);
    }

    private void ApplyControlFontSize()
    {
        _controlFontSize = Math.Clamp(_controlFontSize, 10, 30);
        foreach (var list in ControlCaseLists()) list.FontSize = _controlFontSize;
        // Слово в шапке остаётся заметно крупнее примеров.
        ControlWordTextBlock.FontSize = Math.Clamp(_controlFontSize * 2, 24, 40);
        // Заголовки окон чуть мельче примеров, но растут вместе с ними.
        var headerSize = Math.Max(11, _controlFontSize - 2);
        ControlLeftHeaderTextBlock.FontSize = headerSize;
        ControlRightHeaderTextBlock.FontSize = headerSize;
        ControlEnclinomenHeaderTextBlock.FontSize = headerSize;
    }

    // Вкладка 4 показывает только перенесённые сюда случаи. Это важно и для редактора:
    // правка одного случая не должна неявно менять остальные вхождения того же слова.
    private (MutableReviewEntry entry, int index)[] ManualGroupPairs(string wordKey) =>
        _entries.Select((entry, index) => (entry, index))
            .Where(pair => ExactWordKey(pair.entry.Word) == wordKey && IsManualReviewCase(pair.entry))
            .ToArray();

    private IEnumerable<MutableReviewEntry> GetSelectedGroup()
    {
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return Enumerable.Empty<MutableReviewEntry>();
        return ManualGroupPairs(ExactWordKey(_entries[selected.Index].Word)).Select(pair => pair.entry);
    }

    // Кому адресована текущая правка. Отмеченные флажком образцы получают общую форму группы,
    // а снятый образец — только свою: так одно написание можно разнести по разным ударениям.
    private IEnumerable<MutableReviewEntry> GetCorrectionTargets()
    {
        var group = GetSelectedGroup().ToArray();
        if (group.Length == 0) return Array.Empty<MutableReviewEntry>();
        var selected = _selectedSampleIndex >= 0 && _selectedSampleIndex < _entries.Count ? _entries[_selectedSampleIndex] : null;
        if (selected == null || !group.Contains(selected)) selected = group[0];
        return selected.Included ? group.Where(entry => entry.Included).ToArray() : new[] { selected };
    }

    // Общая форма группы: значение отмеченного образца; если отмеченных нет — первого непустого.
    private static string CommonCorrectionFor(IEnumerable<MutableReviewEntry> group)
    {
        var entries = group.ToArray();
        var fromIncluded = entries.Where(entry => entry.Included).Select(CorrectionTextFor).FirstOrDefault(value => value.Length > 0);
        if (fromIncluded != null) return fromIncluded;
        return entries.Select(CorrectionTextFor).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
    }

    // Группы, в которых есть хотя бы одна правка: у группы общая форма, а снятые образцы
    // могут нести каждое своё ударение (например «за́мок» и «замо́к» в одном слове).
    private List<Program.WordReviewFragmentEdit> CollectFragmentEdits() =>
        _fragmentDrafts.Select(pair =>
        {
            var entry = _entries.FirstOrDefault(item => Program.SplitReviewContext(item.Context).Context == pair.Key);
            return new Program.WordReviewFragmentEdit(pair.Key, pair.Value, entry?.Word ?? string.Empty, entry?.ContextOccurrence ?? 0);
        }).ToList();

    private List<(MutableReviewEntry Entry, string Correction, MutableReviewEntry[] Group)> CollectEditedGroups()
    {
        var result = new List<(MutableReviewEntry Entry, string Correction, MutableReviewEntry[] Group)>();
        var fragmentContexts = _fragmentDrafts.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var group in _entries.GroupBy(entry => ExactWordKey(entry.Word), StringComparer.Ordinal))
        {
            var entries = group.Where(entry => !fragmentContexts.Contains(Program.SplitReviewContext(entry.Context).Context)).ToArray();
            if (entries.Length == 0) continue;
            var common = CommonCorrectionFor(entries);
            // Группа без ударения, но с переносами на предлог — тоже правка: слово остаётся
            // без знака, а знак получает предлог перед ним. Пустую общую форму считаем
            // недопустимой: JsonException на «» ронял весь пакет правок.
            var valid = (common.Length > 0 && (Program.IsValidCorrection(entries[0].Word, common) || Program.IsValidTypoCorrection(entries[0].Word, common)))
                || entries.Any(entry => entry.Enclinomen)
                || entries.Any(entry =>
                {
                    var own = CorrectionTextFor(entry);
                    return own.Length > 0 && (Program.IsValidCorrection(entry.Word, own) || Program.IsValidTypoCorrection(entry.Word, own));
                });
            if (valid) result.Add((entries[0], common, entries));
        }
        return result;
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
        var fragmentEdits = CollectFragmentEdits();
        _manualCorrectionCount = edited.Count(g => g.Correction.Length > 0
            && (Program.IsValidCorrection(g.Entry.Word, g.Correction) || Program.IsValidTypoCorrection(g.Entry.Word, g.Correction)));
        if (edited.Count > 0 && (string.IsNullOrWhiteSpace(_reviewPath) || string.IsNullOrWhiteSpace(_outputPath) || !File.Exists(_outputPath)))
        {
            MessageBox.Show(this, "Выберите файл отчёта и редактируемый FB2 или TXT.", "Нужны файлы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Решения сначала записываются в файл правок и только потом читаются из него.
        // Иначе сохранённые ранее словоформы применялись бы после свежих правок
        // и возвращали бы к старому виду то, что человек только что изменил.
        foreach (var group in edited)
        {
            if (group.Correction.Length > 0 && (Program.IsValidCorrection(group.Entry.Word, group.Correction) || Program.IsValidTypoCorrection(group.Entry.Word, group.Correction)))
                Program.SaveUserCorrection(group.Entry.ToEntry(), group.Correction);
            // Перенесённый на предлог случай сюда не входит: у него своё решение в тексте
            // (слово без знака, знак на предлоге), и общая форма группы не должна
            // затирать его в словаре словоформ.
            if (group.Correction.Length > 0)
                Program.SaveDeclensionForms(_reviewPath!, group.Entry.ToEntry(),
                    group.Group.Where(item => !item.Enclinomen)
                        .Select(item => new Program.DeclensionCorrection(item.Word, group.Correction, "Исходное слово", item.Context)));
        }
        // Общая правка берётся из отчёта (pattern/individual ниже), а не из файла словоформ:
        // иначе сплошная словоформа затирала бы отдельные ударения снятых флажком вхождений.
        // Словоформы снятых флажком вхождений (случаи с переносом на предлог) сюда не входят:
        // иначе следующая запись пакета перезаписала бы их общей формой группы.
        var savedForms = ReadSavedForms().Where(form => !form.Case.Equals("Исходное слово", StringComparison.Ordinal))
            .Where(form => !edited.Any(item => item.Group.Any(entry => entry.Enclinomen
                && Program.ReviewWordKey(form.Lemma).Equals(Program.ReviewWordKey(entry.Word), StringComparison.Ordinal))))
            .ToList();
        if (edited.Count == 0 && savedForms.Count == 0 && fragmentEdits.Count == 0)
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
        var correctedPath = Program.CorrectedPathFor(sourcePath);
        var isText = Path.GetExtension(sourcePath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        try
        {
            ConfirmOutputPath(sourcePath, correctedPath, "Перезапись corrected-файла");
            if (!string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(correctedPath) &&
                MessageBox.Show(this, $"Файл результата уже существует:\n{correctedPath}\n\nПересоздать его из выбранного исходного файла?", "Пересоздание corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var pattern = new List<Program.WordReviewPatternCorrection>();
            var individual = new List<Program.WordReviewContextCorrection>();
            var prepositions = new List<Program.WordReviewPrepositionCorrection>();
            foreach (var group in edited)
            {
                // Вхождения-энклиномены: ударение уходит на предлог, слово остаётся без знака.
                // Общая форма группы их не касается — решение у каждого своё.
                var enclinomen = group.Group.Where(item => item.Enclinomen).ToArray();
                // Снятые флажком вхождения: у каждого может быть своё ударение. Из общей
                // формы исключаем ТОЛЬКО те, у кого своя форма реально отличается —
                // иначе снятое, но не заполненное вхождение осталось бы без ударения вовсе.
                var individuals = group.Group.Where(item => !item.Included && !item.Enclinomen)
                    .Select(item => (Item: item, Own: CorrectionTextFor(item)))
                    .Where(pair => pair.Own.Length > 0 && !string.Equals(pair.Own, group.Correction, StringComparison.Ordinal)
                        && (Program.IsValidCorrection(pair.Item.Word, pair.Own) || Program.IsValidTypoCorrection(pair.Item.Word, pair.Own)))
                    .ToArray();
                if (group.Correction.Length > 0)
                {
                    if (individuals.Length == 0 && enclinomen.Length == 0)
                        pattern.Add(new Program.WordReviewPatternCorrection(group.Entry.Word, group.Correction, null, -1));
                    else
                    {
                        pattern.AddRange(individuals.Select(pair => new Program.WordReviewPatternCorrection(group.Entry.Word, group.Correction, pair.Item.Context, pair.Item.ContextOccurrence)));
                        // Вхождения-энклиномены исключаются из общей формы: знак встанет на предлог.
                        pattern.AddRange(enclinomen.Select(item => new Program.WordReviewPatternCorrection(group.Entry.Word, group.Correction, item.Context, item.ContextOccurrence)));
                    }
                }
                // Отдельные вхождения: своя форма применяется только к своему месту.
                foreach (var pair in individuals)
                    individual.Add(new Program.WordReviewContextCorrection(group.Entry.Word, pair.Own, pair.Item.Context, pair.Item.ContextOccurrence));
                foreach (var item in enclinomen)
                {
                    // Слово остаётся без знака: если в тексте оно уже было с ударением, знак снимается.
                    individual.Add(new Program.WordReviewContextCorrection(item.Word, item.Word, item.Context, item.ContextOccurrence));
                    prepositions.Add(new Program.WordReviewPrepositionCorrection(item.Word, item.Preposition!, item.Context, item.ContextOccurrence));
                }
            }
            var hasCorrections = pattern.Count > 0 || individual.Count > 0 || prepositions.Count > 0;
            var replacements = !hasCorrections ? 0
                : (isText ? Program.ApplyReviewPatternCorrectionsToTextFile(sourcePath, correctedPath, pattern, individual, prepositions)
                          : Program.ApplyReviewPatternCorrectionsToFb2(sourcePath, correctedPath, pattern, individual, prepositions));
            if (hasCorrections)
            {
                _outputPath = correctedPath;
                Fb2FileTextBox.Text = correctedPath;
            }
            if (savedForms.Count > 0)
            {
                if (!hasCorrections && !string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, correctedPath, overwrite: true);
                    _outputPath = correctedPath;
                    Fb2FileTextBox.Text = correctedPath;
                }
                replacements += (isText ? Program.ApplyWordCorrectionsToTextFile(_outputPath, _outputPath, savedForms) : Program.ApplyWordCorrectionsToFb2(_outputPath, _outputPath, savedForms));
            }
            if (fragmentEdits.Count > 0)
            {
                if (!hasCorrections && savedForms.Count == 0 && !string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, correctedPath, overwrite: true);
                    _outputPath = correctedPath;
                    Fb2FileTextBox.Text = correctedPath;
                }
                var fragmentResult = isText
                    ? Program.ApplyFragmentEditsToTextFile(_outputPath, _outputPath, fragmentEdits)
                    : Program.ApplyFragmentEditsToFb2(_outputPath, _outputPath, fragmentEdits);
                replacements += fragmentResult.Applied;
                if (fragmentResult.Failed > 0)
                    FileStatusTextBlock.Text = $"Не удалось применить правки фрагментов: {fragmentResult.Failed}.";
            }
            _wordCounts = Program.CountReviewWordOccurrences(_outputPath, _entries.Select(item => item.Word));
            foreach (var group in edited)
            {
                // Слово остаётся в отчёте, но помечается как проверенное: пустой отчёт
                // нельзя открыть повторно, и в нём пропадала бы история проверки.
                // Отдельные формы снятых вхождений при этом не затираются.
                foreach (var item in group.Group) item.Checked = true;
            }
            if (fragmentEdits.Count > 0)
            {
                var fragmentContexts = fragmentEdits.Select(item => item.Context).ToHashSet(StringComparer.Ordinal);
                foreach (var entry in _entries.Where(item => fragmentContexts.Contains(Program.SplitReviewContext(item.Context).Context)))
                    entry.Checked = true;
            }
            if (edited.Count > 0 || fragmentEdits.Count > 0) SaveReviewState();
            ApplyReviewItems();
            UpdateNavigationState();
            RememberSession();
            UpdateEditorEnabled();
            FileStatusTextBlock.Text = $"Пакетная правка применена.\nФайл: {_outputPath}\nГрупп слов: {edited.Count}; сохранённых словоформ к применению: {savedForms.Count}; правок фрагментов: {fragmentEdits.Count}; заменено вхождений: {replacements}.\nОтчёт сохранён: проверенные слова помечены строкой «Проверено», к документу можно вернуться позже.";
            OfferToOpenResultFolders("Правки применены.",
                _outputPath,
                Program.FormCorrectionsPathFor(_reviewPath!),
                StatisticsPath);
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

    /// <summary>
    /// Отчёты со статистикой внутри рабочей папки: сначала сравнение RUAccent и Silero,
    /// если его нет — статистика прогона RUAccent. Вне рабочей папки — статистика конвертации.
    /// </summary>
    private string? StatisticsPath
    {
        get
        {
            if (_workspace == null) return _diagnosticsPath;
            return File.Exists(_workspace.ComparisonStatistics) ? _workspace.ComparisonStatistics : _workspace.RuAccentStatistics;
        }
    }

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

    private void ApplyAllEditedGroups()
    {
        ApplyGroupCorrection();
    }

    // Отчёт переписывается целиком: список слов сохраняется, рядом с каждым
    // уже проверенным словом появляется строка «Проверено [слово]: <дата и время>».
    private void SaveReviewState()
    {
        if (_reviewPath == null) return;
        try
        {
            var checkedWords = _entries.Where(entry => entry.Checked)
                .Select(entry => Program.ReviewWordKey(entry.Word))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            SaveFragmentDrafts();
            File.WriteAllText(_reviewPath, Program.FormatWordReview(_entries.Select(entry => entry.ToEntry()), checkedWords), new UTF8Encoding(true));
            Program.LogReview($"Отчёт {Path.GetFileName(_reviewPath)} сохранён: слов {_entries.Count}, проверено {checkedWords.Count}");
        }
        catch (Exception ex)
        {
            // Правки к тексту уже применены, поэтому сбой записи отчёта — предупреждение, а не отмена.
            Program.LogError("Сохранение отчёта нераспознанных слов", ex);
            FileStatusTextBlock.Text = "Правки применены к файлу, но отчёт не удалось сохранить: " + ex.Message;
        }
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

    private void ContextsListBox_ContainerStatusChanged(object? sender, EventArgs e)
    {
        if (ContextsListBox.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            QueueAttachContextMenusToSamples();
    }

    private void QueueAttachContextMenusToSamples()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(QueueAttachContextMenusToSamples), DispatcherPriority.Loaded);
            return;
        }
        if (ContextsListBox.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
        Dispatcher.BeginInvoke(new Action(AttachContextMenusToSamples), DispatcherPriority.ContextIdle);
    }

    private void AttachContextMenusToSamples()
    {
        try
        {
            if (ContextsListBox.Items.Count == 0 || ContextsListBox.ItemsSource is not IEnumerable<SampleItem> currentSamples) return;
            foreach (var sample in currentSamples)
            {
                if (sample.Index < 0 || sample.Index >= _entries.Count) continue;
                if (ContextsListBox.ItemContainerGenerator.ContainerFromItem(sample) is not ListBoxItem container) continue;
                AddGoogleContextMenu(container, _entries[sample.Index].Word, sample.Context);
            }
        }
        catch (Exception error)
        {
            Program.LogError("Установка меню контекста в списке слов", error);
        }
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
        var group = ManualGroupPairs(wordKey);
        if (group.Length == 0) return;
        _selectedGroupAnchor = group[0].index;
        if (!group.Any(pair => pair.index == _selectedSampleIndex)) _selectedSampleIndex = group.FirstOrDefault(pair => pair.entry.Included).index;
        var samples = group.Select(pair => SampleItem.For(pair.index, pair.entry.Context, pair.entry.Word, pair.entry.Included, SampleDetailFor(pair.entry))).ToList();
        _rebuildingSamples = true;
        ContextsListBox.ItemsSource = samples;
        ContextsListBox.SelectedItem = samples.FirstOrDefault(sample => sample.Index == _selectedSampleIndex);
        _rebuildingSamples = false;
        QueueAttachContextMenusToSamples();
        var selectedEntry = group[0].entry;
        var count = _wordCounts.TryGetValue(selectedEntry.Word, out var occurrences) ? occurrences : 0;
        ContextTextBlock.Text = $"Выбрана группа «{selectedEntry.Word}»: совпадений в тексте {count:N0}; контекстов {group.Length}.";
        SelectionCountTextBlock.Text = $"Включено образцов: {group.Count(pair => pair.entry.Included)} из {group.Length}";
        _updatingCorrection = true;
        // В редактор подставляем общую форму группы: значения отмеченных образцов важнее снятых.
        var proposed = group.Where(pair => pair.entry.Included).Select(pair => pair.entry.Proposed).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? group.Select(pair => pair.entry.Proposed).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? selectedEntry.Word;
        var ruAccent = group.Select(pair => pair.entry.RuAccent).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var silero = group.Select(pair => pair.entry.Silero).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var hasDisagreement = ruAccent != null || silero != null;
        _hasModelDisagreements |= hasDisagreement;
        // Расхождения показываем как варианты, не подставляя автоматически в ручное поле.
        var suggestion = _llmSuggestions.TryGetValue(wordKey, out var llmSuggestion) ? llmSuggestion : null;
        if (suggestion != null && !Program.IsValidCorrection(selectedEntry.Word, suggestion)) suggestion = null;
        // Ответ модели другим словом (опечатка, старая форма, имя) принять как ударение нельзя,
        // но если он близок по буквам, его можно предложить как исправление написания — тоже вручную.
        var typoFix = suggestion != null || !_llmRejected.TryGetValue(wordKey, out var rejectedAnswer)
            || string.IsNullOrEmpty(rejectedAnswer.Raw) || !Program.IsValidTypoCorrection(selectedEntry.Word, rejectedAnswer.Raw)
            ? null : rejectedAnswer.Raw;
        if (!_hasModelDisagreements && !proposed.Contains('\u0301') && !proposed.Contains('ё', StringComparison.OrdinalIgnoreCase))
        {
            if (suggestion != null) proposed = suggestion;
            else if (typoFix != null) proposed = typoFix;
        }
        CorrectionTextBox.Text = proposed.Contains('\u0301') || proposed.Contains('ё', StringComparison.OrdinalIgnoreCase) ? proposed : DefaultInitialStress(proposed);
        _updatingCorrection = false;
        var hint = string.Join("     ", new[]
        {
            ruAccent != null ? "RUAccent: " + ruAccent : null,
            silero != null ? "Silero: " + silero : null,
            suggestion != null ? "ИИ: " + suggestion : typoFix != null ? "ИИ: " + typoFix : null
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        LlmHintTextBlock.Text = hint.Length > 0 ? hint : _llmRejected.TryGetValue(wordKey, out var otherAnswer)
            ? string.IsNullOrEmpty(otherAnswer.Raw) ? "ИИ не предложил вариант." : "ИИ ответил: " + otherAnswer.Raw
            : "";
        // Плашка вердикта ИИ: предлагает вариант, не уверен или ИИ ещё не запускали.
        if (_llmSuggestions.TryGetValue(wordKey, out var aiSuggestion))
        {
            AiVerdictTextBlock.Text = "ИИ предлагает: " + aiSuggestion + ". Подтвердите кнопкой «Да ✓» либо расставьте ударение сами.";
            AiVerdictPanel.Visibility = Visibility.Visible;
        }
        else if (_llmRejected.TryGetValue(wordKey, out var aiRejected))
        {
            AiVerdictTextBlock.Text = string.IsNullOrEmpty(aiRejected.Raw)
                ? "ИИ не уверен — варианта нет (" + ReasonLabel(aiRejected.Reason) + "). Поставьте ударение вручную."
                : "ИИ не уверен. Модель ответила «" + aiRejected.Raw + "» (" + ReasonLabel(aiRejected.Reason) + "). Поставьте ударение вручную.";
            AiVerdictPanel.Visibility = Visibility.Visible;
        }
        else AiVerdictPanel.Visibility = Visibility.Collapsed;
        ModelChoicesPanel.Children.Clear();
        ModelChoicesPanel.Children.Add(new TextBlock { Text = "Свой вариант:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
        if (ruAccent != null) AddModelChoice("RUAccent", ruAccent);
        if (silero != null) AddModelChoice("Silero", silero);
        if (suggestion != null && Program.IsValidCorrection(selectedEntry.Word, suggestion)) AddModelChoice("ИИ", suggestion);
        LlmHintPanel.Visibility = hint.Length == 0 && ModelChoicesPanel.Children.Count <= 1 ? Visibility.Collapsed : Visibility.Visible;
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

    private void AddModelChoice(string title, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var button = new Button
        {
            Content = title + " → " + value,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 6, 4),
            ToolTip = "Выбрать это ударение для текущего слова"
        };
        button.Click += (_, _) =>
        {
            if (!Program.IsValidCorrection(WordGroupsListBox.SelectedItem is ReviewItem selected ? selected.Word : "", value))
            {
                FileStatusTextBlock.Text = "Вариант модели не прошёл проверку ударения. Исправьте его вручную.";
                return;
            }
            _updatingCorrection = true;
            CorrectionTextBox.Text = value;
            _workingWord = PlainLetters(value);
            _updatingCorrection = false;
            RebuildStressChoices();
            foreach (var entry in GetCorrectionTargets()) entry.Proposed = value;
            RenderSelectedSample();
            FileStatusTextBlock.Text = $"Выбран вариант {title}: {value}. Нажмите «Да ✓», чтобы принять.";
        };
        ModelChoicesPanel.Children.Add(button);
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
        UpdateNavigationState();
        e.Handled = true;
    }

    // При выборе снятого флажком образца в редактор подставляется его отдельная форма
    // (если она уже задана), иначе — общая форма группы. Отмеченный образец общую форму не меняет.
    private void LoadCorrectionFromSample(MutableReviewEntry entry)
    {
        // В редактор всегда подставляется форма ИМЕННО этого случая: у отмеченного — общая
        // форма группы, у снятого — его собственная (если задана), иначе общая. Иначе при
        // переходе со снятого случая на отмеченный в поле осталась бы чужая форма и «Да ✓»
        // затёрло бы ударение всего большинства.
        var manualGroup = ManualGroupPairs(ExactWordKey(entry.Word)).Select(pair => pair.entry);
        var value = entry.Included
            ? CommonCorrectionFor(manualGroup)
            : CorrectionTextFor(entry) is { Length: > 0 } own
                ? own
                : CommonCorrectionFor(manualGroup);
        if (value.Length == 0) value = DefaultInitialStress(entry.Word);
        if (string.Equals(CorrectionTextBox.Text, value, StringComparison.Ordinal)) return;
        _updatingCorrection = true;
        try
        {
            CorrectionTextBox.Text = value;
            _workingWord = PlainLetters(value);
        }
        finally { _updatingCorrection = false; }
        RebuildStressChoices();
    }

    // Пометка для списка контекстов: у снятого флажка видно, что у него своя форма.
    private static string SampleDetailFor(MutableReviewEntry entry)
    {
        if (entry.Included) return string.Empty;
        var own = CorrectionTextFor(entry);
        return own.Length > 0 ? "   · своя форма: " + own : "   · флажок снят, своя форма не задана";
    }

    // Метка «своя форма» у текущего случая обновляется на месте (без перестройки списка).
    private void UpdateSelectedSampleDetail()
    {
        if (ContextsListBox.ItemsSource is not IEnumerable<SampleItem> samples) return;
        if (_selectedSampleIndex < 0 || _selectedSampleIndex >= _entries.Count) return;
        var item = samples.FirstOrDefault(sample => sample.Index == _selectedSampleIndex);
        if (item != null) item.Detail = SampleDetailFor(_entries[_selectedSampleIndex]);
    }

    private void SelectSample(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count) return;
        _selectedSampleIndex = entryIndex;
        var entry = _entries[entryIndex];
        ContextTextBlock.Text = $"Выбрано предложение: {entry.Word} — {entry.Context}";
        var group = ManualGroupPairs(ExactWordKey(entry.Word)).Select(pair => pair.entry).ToArray();
        SelectionCountTextBlock.Text = $"Включено образцов: {group.Count(item => item.Included)} из {group.Length}";
        var selected = ContextsListBox.Items.Cast<SampleItem>().FirstOrDefault(sample => sample.Index == entryIndex);
        if (selected != null && !ReferenceEquals(ContextsListBox.SelectedItem, selected)) ContextsListBox.SelectedItem = selected;
        LoadCorrectionFromSample(entry);
        RenderSelectedSample();
    }

    private void SetSampleIncluded(int index, bool included)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries[index].Included = included;
        // Не уводим выбор с образца: снятая галочка означает «у этого случая своё
        // ударение», поэтому редактор остаётся на нём и сразу готов принять правку.
        _selectedSampleIndex = index;
        var group = ManualGroupPairs(ExactWordKey(_entries[index].Word)).Select(pair => pair.entry).ToArray();
        SelectionCountTextBlock.Text = $"Включено образцов: {group.Count(entry => entry.Included)} из {group.Length}";
        var samples = ContextsListBox.Items.Cast<SampleItem>()
            .Select(item => item.Index == index ? SampleItem.For(item.Index, item.Context, _entries[index].Word, included, SampleDetailFor(_entries[index])) : item)
            .ToList();
        _rebuildingSamples = true;
        ContextsListBox.ItemsSource = samples;
        ContextsListBox.SelectedItem = samples.FirstOrDefault(item => item.Index == index);
        _rebuildingSamples = false;
        QueueAttachContextMenusToSamples();
        SelectSample(index);
    }

    private void UpdateEditorEnabled()
    {
        var hasReport = !string.IsNullOrWhiteSpace(_reviewPath) && File.Exists(_reviewPath);
        LoadFilesButton.IsEnabled = hasReport;
        OpenCorrectionsButton.IsEnabled = hasReport && File.Exists(Program.FormCorrectionsPathFor(_reviewPath!));
        ApplyAllButton.IsEnabled = hasReport && !string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath);
        SaveCorrectionButton.IsEnabled = ApplyAllButton.IsEnabled && WordGroupsListBox.SelectedItem != null;
        SaveProgressButton.IsEnabled = hasReport;
        FinishWorkButton.IsEnabled = ApplyAllButton.IsEnabled;
    }

    private void UpdateNavigationState()
    {
        PreviousButton.IsEnabled = WordGroupsListBox.SelectedIndex > 0;
        NextButton.IsEnabled = WordGroupsListBox.SelectedIndex >= 0 && WordGroupsListBox.SelectedIndex < WordGroupsListBox.Items.Count - 1;
        var cases = ContextsListBox.Items.Count;
        var caseIndex = ContextsListBox.SelectedIndex;
        PreviousCaseButton.IsEnabled = caseIndex > 0;
        NextCaseButton.IsEnabled = caseIndex >= 0 && caseIndex < cases - 1;
        CasePositionTextBlock.Text = cases > 0 && caseIndex >= 0 ? $"Случай {caseIndex + 1} из {cases}" : string.Empty;
    }

    // Листание случаев (вхождений) внутри выбранного слова: так одно написание можно
    // разнести по разным ударениям, пробежавшись именно по списку примеров.
    private void NavigateCase(int direction)
    {
        var count = ContextsListBox.Items.Count;
        if (count == 0) return;
        var current = ContextsListBox.SelectedIndex;
        if (current < 0) current = 0;
        var next = Math.Clamp(current + direction, 0, count - 1);
        if (next == current) { UpdateNavigationState(); return; }
        ContextsListBox.SelectedIndex = next;
        ContextsListBox.ScrollIntoView(ContextsListBox.SelectedItem);
        UpdateNavigationState();
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
        var accentPosition = ActiveWordText.IndexOf('\u0301') - 1;
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
              "» — на букве, которой нет среди гласных. Так и задумано." +
              (UniqueStressToggle.IsChecked == true ? string.Empty : " Поставить такое ударение можно кнопкой УНИК.")
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
        // Панель УНИК строится только когда включена: нужна она для считанных слов,
        // а перестраивать её на каждое переключение слова незачем.
        if (UniqueStressToggle.IsChecked == true) RebuildUniqueStressChoices();
        else UniqueStressPanel.Children.Clear();
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
        // Имя нужно программам чтения с экрана и автоматическим проверкам: буква сама по себе
        // как имя бесполезна — непонятно, что нажатие сделает.
        System.Windows.Automation.AutomationProperties.SetName(button, $"Буква «{glyph}»" + (isStressed ? ", здесь ударение" : ""));
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
        var accentPosition = ActiveWordText.IndexOf('\u0301') - 1;

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
        if (_fragmentWordEditing)
        {
            var selection = FragmentEditor.Selection;
            if (selection.IsEmpty) return;
            _updatingFragment = true;
            try
            {
                var start = selection.Start;
                selection.Text = value;
                var end = start.GetPositionAtOffset(value.Length) ?? FragmentEditor.Document.ContentEnd;
                FragmentEditor.Selection.Select(start, end);
            }
            finally { _updatingFragment = false; }
            _workingWord = PlainLetters(value);
            RebuildStressChoices();
            SaveCurrentFragmentDraft();
            if (note.Length > 0) FileStatusTextBlock.Text = note;
            return;
        }
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
        var targets = GetCorrectionTargets().ToArray();
        // Снятый флажком образец правится отдельно: у него своё ударение, общая форма группы не трогается.
        var singleCase = targets.Length == 1 && !targets[0].Included;
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
        foreach (var item in targets) item.Proposed = proposed;
        UpdateSelectedSampleDetail();
        var confirmed = ManualGroupPairs(ExactWordKey(entry.Word)).Length;
        var editedGroups = CollectEditedGroups().Count;
        if (singleCase)
        {
            // Остаёмся в группе и сразу переходим к следующему случаю: так удобно
            // «пробежаться» по списку примеров одного слова и разнести ударения.
            var caseIndex = ContextsListBox.SelectedIndex;
            var caseCount = ContextsListBox.Items.Count;
            if (caseIndex >= 0 && caseIndex < caseCount - 1)
            {
                NavigateCase(1);
                FileStatusTextBlock.Text = $"Отдельное ударение принято: «{proposed}» (группа «{entry.Word}»). Перешли к следующему случаю — снимите галочку и задайте своё ударение, если он звучит иначе.";
            }
            else
            {
                FileStatusTextBlock.Text = $"Отдельное ударение принято: «{proposed}» (последний случай группы «{entry.Word}»). Отмеченные образцы не изменены. Общую форму задавайте на образце под галочкой.";
            }
            UpdateNavigationState();
            return;
        }
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
        foreach (var item in GetCorrectionTargets()) item.Proposed = restored;
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

    // «Применить все правки → итог» идёт тем же путём, что и пакетная правка из отчёта:
    // тогда общие формы и отдельные ударения снятых вхождений применяются согласованно.
    private void ApplyAllCorrections() => ApplyGroupCorrection();

    private void ConfirmOutputPath(string sourcePath, string correctedPath, string title)
    {
        if (string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, $"Исходный файл уже имеет суффикс _итог и будет перезаписан:\n{sourcePath}\n\nПродолжить?", title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            throw new OperationCanceledException();
    }

    private static void OpenGoogleStressSearch(string word, string context)
    {
        var index = context.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        var highlighted = index < 0 ? $"{context} Проверяемое слово: **{word}**" : context[..index] + "**" + context.Substring(index, word.Length) + "**" + context[(index + word.Length)..];
        Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString($"куда ставить ударение в предложении {highlighted}")) { UseShellExecute = true });
    }

    private static string ExactWordKey(string word) => word.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    // Ключ конкретного вхождения: слово + фрагмент с номером повторения. Context уже
    // содержит \u0001 и номер, поэтому одинаковые предложения не склеиваются.
    private static string ManualCaseKey(MutableReviewEntry entry) =>
        ExactWordKey(entry.Word) + "\u0001" + entry.Context;

    private bool IsManualReviewCase(MutableReviewEntry entry) =>
        entry.Manual
        || _manualReviewKeys.Contains(ExactWordKey(entry.Word))
        || _manualReviewCaseKeys.Contains(ManualCaseKey(entry));

    private bool IsCaseLevelCurrentGroup()
    {
        if (WordGroupsListBox.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return false;
        var wordKey = ExactWordKey(_entries[selected.Index].Word);
        var all = _entries.Where(entry => ExactWordKey(entry.Word) == wordKey).ToArray();
        var manual = all.Count(IsManualReviewCase);
        return manual > 0 && manual < all.Length;
    }

    private void AddCaseToManualReview(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count) return;
        var entry = _entries[entryIndex];
        entry.Manual = true;
        _manualReviewCaseKeys.Add(ManualCaseKey(entry));
        _controlVariants.Remove(ExactWordKey(entry.Word));
    }

    private void AddWordToManualReview(string wordKey)
    {
        _manualReviewKeys.Add(wordKey);
        foreach (var entry in _entries.Where(item => ExactWordKey(item.Word) == wordKey))
        {
            entry.Manual = true;
            _manualReviewCaseKeys.Remove(ManualCaseKey(entry));
        }
        _controlVariants.Remove(wordKey);
    }

    private void RemoveWordFromManualReview(string wordKey)
    {
        _manualReviewKeys.Remove(wordKey);
        foreach (var entry in _entries.Where(item => ExactWordKey(item.Word) == wordKey))
        {
            entry.Manual = false;
            _manualReviewCaseKeys.Remove(ManualCaseKey(entry));
        }
        _controlVariants.Remove(wordKey);
    }

    private void RemoveCaseFromManualReview(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count) return;
        var entry = _entries[entryIndex];
        var wordKey = ExactWordKey(entry.Word);
        // Если всё слово было перенесено целиком, а обратно тянут только один случай,
        // превращаем перенос в набор конкретных вхождений: остальные остаются на вкладке 4.
        if (_manualReviewKeys.Remove(wordKey))
        {
            foreach (var other in _entries.Where(item => ExactWordKey(item.Word) == wordKey && !ReferenceEquals(item, entry)))
            {
                other.Manual = true;
                _manualReviewCaseKeys.Add(ManualCaseKey(other));
            }
        }
        entry.Manual = false;
        _manualReviewCaseKeys.Remove(ManualCaseKey(entry));
        _controlVariants.Remove(wordKey);
    }

    // ================== Страница «Контроль ИИ результатов» ==================
    // ИИ (omogre) разбирает не слово целиком, а каждый случай расхождения по предложению.
    // Здесь видно, как он разложил омограф: слева вариант 1 (общая форма группы), справа
    // вариант 2 (случаи с отдельным ударением). Неверный случай переносится в другое окно.

    private void RefreshControlPage()
    {
        _controlGroups = BuildControlGroups();
        if (_controlGroupIndex >= _controlGroups.Count) _controlGroupIndex = _controlGroups.Count - 1;
        if (_controlGroupIndex < 0 && _controlGroups.Count > 0) _controlGroupIndex = 0;
        RenderControlGroup();
    }

    // Слова, у которых есть хотя бы один случай с отдельным ударением: именно у них и есть
    // что контролировать (омографы вида «за́мок»/«замо́к»). Сюда же попадают слова с
    // энклиноменом — даже если вариантов ударения у них нет, ударение решается на предлоге.
    private List<ControlGroupData> BuildControlGroups()
    {
        var groups = new List<ControlGroupData>();
        foreach (var wordGroup in _entries.Select((entry, index) => (entry, index))
                     .GroupBy(pair => ExactWordKey(pair.entry.Word), StringComparer.Ordinal))
        {
            var allPairs = wordGroup.ToArray();
            // Регистр крупного слова и заголовков берём по большинству вхождений: первое
            // вхождение часто стоит в начале фразы («Дома»), из-за чего обычное слово
            // показывалось с заглавной буквы во всех случаях.
            var word = CommonCaseWord(allPairs.Select(pair => pair.entry.Word));
            var key = ExactWordKey(word);
            // В контроль ИИ попадают только случаи, ещё не перенесённые на вкладку 4.
            // Один перенесённый случай не должен уносить с собой все остальные вхождения.
            var pairs = allPairs.Where(pair => !IsManualReviewCase(pair.entry)).ToArray();
            if (pairs.Length == 0) continue;
            // Разбор энклиноменов идёт до раскладки по вариантам: перенесённое на предлог
            // вхождение уже решено, и в общую форму группы оно больше не попадает.
            foreach (var pair in pairs) ProposeEnclinomen(pair.entry);
            var hasEnclinomen = pairs.Any(pair => pair.entry.Enclinomen);
            if (!_controlVariants.TryGetValue(key, out var variants))
            {
                // Отчёт открыт без прогона ИИ в этой сессии. Сначала берём уже решённые формы
                // («Правка»), а если их нет — предложения моделей RUAccent и Silero. Без этого
                // у неразрешённых расхождений «Правка [дома]: дома» оставалась безударной и
                // окна вариантов, крупное слово и текст случаев показывались без акута.
                var leftForms = new List<string>();
                var rightForms = new List<string>();
                foreach (var pair in pairs)
                {
                    // У энклиномена слово безударно: вариантом ударения оно не считается.
                    // Раньше его форма подставлялась в leftForm первой и «съедала» знак
                    // у настоящего варианта: над окнами появлялось «полу» вместо «по́лу».
                    if (pair.entry.Enclinomen) continue;
                    var form = CorrectionTextFor(pair.entry);
                    // «Правка» без ударения — это ещё не выбор варианта (обычно так выглядит
                    // неразрешённое расхождение), поэтому знак ударения ищем у моделей.
                    var candidates = new List<string>();
                    if (StressSignature(form).Position >= 0)
                    {
                        candidates.Add(LowerFirstLetter(form));
                    }
                    else
                    {
                        // Silero идёт первой: при равенстве голосов её форма становится вариантом 1.
                        // Для «дома» это «до́ма» (родительный падеж), а не «дома́» (множественное).
                        if (StressSignature(pair.entry.Silero ?? string.Empty).Position >= 0)
                            candidates.Add(LowerFirstLetter(pair.entry.Silero!));
                        if (StressSignature(pair.entry.RuAccent ?? string.Empty).Position >= 0)
                            candidates.Add(LowerFirstLetter(pair.entry.RuAccent!));
                    }
                    // Общая форма и отдельные случаи не смешиваются: иначе одна «своя форма»
                    // делала бы вариантом 1 своё ударение, а все обычные случаи показывались бы с ним.
                    (pair.entry.Included ? leftForms : rightForms).AddRange(candidates);
                }
                var leftForm = MostFrequentForm(leftForms);
                if (leftForm.Length == 0) leftForm = MostFrequentForm(rightForms);
                var leftSignature = StressSignature(leftForm);
                var rightForm = MostFrequentForm(rightForms.Where(form => StressSignature(form) != leftSignature));
                if (rightForm.Length == 0)
                    rightForm = MostFrequentForm(leftForms.Concat(rightForms)
                        .Where(form => StressSignature(form) != leftSignature));
                if (leftForm.Length == 0 || rightForm.Length == 0)
                {
                    // Вариантов нет: у слова только переносы на предлог — показываем его
                    // с обеих сторон без знака, чтобы третье окно было видно.
                    if (!hasEnclinomen) continue;
                    if (leftForm.Length == 0) leftForm = word;
                    // Пустого второго варианта не бывает: если отдельных случаев нет,
                    // повторяем общую форму, а не безударный ключ слова.
                    // Второй вариант не дублируем: если он не найден, окно остаётся пустым,
                    // а не повторяет первый вариант (раньше дубль сбивал с толку).
                    if (rightForm.Length == 0) rightForm = string.Empty;
                }
                // Кандидаты собраны со строчной буквы, а здесь возвращаем регистр слова:
                // «Петя» остаётся с заглавной, обычное «дома» — со строчной.
                leftForm = Program.KeepFirstLetterCase(word, leftForm);
                rightForm = Program.KeepFirstLetterCase(word, rightForm);
                variants = (leftForm, rightForm);
                _controlVariants[key] = variants;
            }
            if (StressSignature(variants.Left) == StressSignature(variants.Right) && !hasEnclinomen) continue;
            var data = new ControlGroupData(word, variants.Left, variants.Right);
            foreach (var pair in pairs)
            {
                if (pair.entry.Enclinomen) { data.Enclinomen.Add(pair.index); continue; }
                var form = CorrectionTextFor(pair.entry);
                // Заведомо несуществующее ударение не должно попадать в окна контроля:
                // такой случай остаётся в ручной доводке, а не дублирует соседний вариант.
                if (form.Length > 0
                    && !Program.IsValidCorrection(pair.entry.Word, form)
                    && !Program.IsValidTypoCorrection(pair.entry.Word, form))
                    continue;
                var inLeft = pair.entry.Included
                    || (form.Length > 0 && StressSignature(form) == StressSignature(variants.Left));
                if (inLeft) data.Left.Add(pair.index); else data.Right.Add(pair.index);
            }
            groups.Add(data);
        }
        return groups.OrderByDescending(group => group.Enclinomen.Count)
            .ThenByDescending(group => group.Right.Count)
            .ThenBy(group => group.Word, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Регистр слова по большинству вхождений: если слово хоть раз встретилось со строчной,
    // это обычное слово, а не имя собственное.
    private static string CommonCaseWord(IEnumerable<string> words)
    {
        var list = words.Where(word => word.Length > 0).ToList();
        if (list.Count == 0) return string.Empty;
        var lowerCount = list.Count(word => char.IsLower(word[0]));
        var lowerWord = list.FirstOrDefault(word => char.IsLower(word[0]));
        var upperWord = list.FirstOrDefault(word => char.IsUpper(word[0]));
        return lowerCount >= list.Count - lowerCount
            ? lowerWord ?? list[0]
            : upperWord ?? list[0];
    }

    private static string LowerFirstLetter(string form) =>
        form.Length == 0 ? form : char.ToLowerInvariant(form[0]) + form[1..];

    // Самая частая форма; при равенстве голосов — та, что встретилась раньше. Порядок
    // кандидатов задаёт вызывающий: для моделей Silero специально идёт перед RUAccent.
    private static string MostFrequentForm(IEnumerable<string> forms)
    {
        return forms
            .Where(form => form.Length > 0)
            .GroupBy(form => form, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .FirstOrDefault() ?? string.Empty;
    }

    // Разбор энклиномена для одного вхождения. Уже решённое не трогаем: и явный перенос из
    // отчёта, и вхождение со своей формой — там человек (или сама раскладка) уже сказал своё.
    private void ProposeEnclinomen(MutableReviewEntry entry)
    {
        if (entry.Enclinomen || !entry.Included) return;
        var match = Program.TryMatchEnclinomen(entry.Context, entry.Word, entry.ContextOccurrence);
        if (match == null) return;
        entry.Preposition = Program.StressPreposition(match.PrepositionStressed);
        entry.EnclinomenDoubtful = match.Doubtful;
        // Вхождение становится отдельным: общая форма группы его не касается, а разбор
        // не предложит его снова, если человек снимет перенос.
        entry.Included = false;
        entry.Proposed = entry.Word;
    }

    private void RenderControlGroup()
    {
        if (_controlGroupIndex < 0 || _controlGroupIndex >= _controlGroups.Count)
        {
            ControlWordTextBlock.Text = string.Empty;
            ControlLeftHeaderTextBlock.Text = string.Empty;
            ControlRightHeaderTextBlock.Text = string.Empty;
            ControlEnclinomenHeaderTextBlock.Text = string.Empty;
            ControlLeftCasesListBox.ItemsSource = null;
            ControlRightCasesListBox.ItemsSource = null;
            ControlEnclinomenCasesListBox.ItemsSource = null;
            ControlPositionTextBlock.Text = string.Empty;
            ControlSummaryTextBlock.Text = _entries.Count == 0
                ? "Откройте отчёт со словами или обработайте книгу — страница заполнится после разбора ИИ."
                : "Разбора по случаям пока нет. Запустите на вкладке 2 «Подсказки ИИ по расхождениям» с движком omogre: он разбирает каждый случай по предложению и раскладывает омографы по двум вариантам.";
            UpdateControlNavigationState();
            return;
        }
        var group = _controlGroups[_controlGroupIndex];
        ControlWordTextBlock.Text = DisplayWordFor(group);
        UpdateControlHeaders();
        ControlLeftCasesListBox.ItemsSource = BuildControlCases(group.Left, group.LeftForm);
        ControlRightCasesListBox.ItemsSource = BuildControlCases(group.Right, group.RightForm);
        ControlEnclinomenCasesListBox.ItemsSource = BuildControlEnclinomenCases(group.Enclinomen);
        ControlPositionTextBlock.Text = $"Слово {_controlGroupIndex + 1} из {_controlGroups.Count}";
        UpdateControlSummary();
        UpdateControlNavigationState();
    }

    // Над окнами показываем слово с принятым ударением, а не безударный ключ отчёта:
    // иначе «полу» висело бы над окнами, где вариант 1 — «по́лу». У энклиномена
    // (слово безударно, знак на предлоге) остаётся исходное написание.
    private static string DisplayWordFor(ControlGroupData group)
    {
        // Общая форма уже содержит знак ударения (или исправленные буквы опечатки).
        // У чисто энклиноменовой группы она совпадает с исходным написанием слова.
        if (group.LeftForm.Length > 0)
            return Program.KeepFirstLetterCase(group.Word, group.LeftForm);
        return group.Word;
    }

    private void UpdateControlSummary()
    {
        var group = CurrentControlGroup();
        if (group == null) return;
        ControlSummaryTextBlock.Text =
            $"Слов с разными вариантами ударения по контексту: {_controlGroups.Count:N0}. " +
            $"«{DisplayWordFor(group)}»: случаев {group.Left.Count + group.Right.Count + group.Enclinomen.Count}, " +
            $"во втором варианте {group.Right.Count}, на предлог {group.Enclinomen.Count}. " +
            "Раскрыт всегда один случай (прежний помечается прочитанным); двойной щелчок, «← →», меню и мышка переносят случай, «Отменить перенос» возвращает.  ·  " + ControlAccuracySummaryLine();
    }

    private List<ControlCaseItem> BuildControlCases(List<int> indices, string form) =>
        indices.Where(index => index >= 0 && index < _entries.Count)
            .Select(index =>
            {
                var entry = _entries[index];
                var moved = ControlCaseMoved(entry);
                return ControlCaseItem.For(index, entry,
                    // Регистр первой буквы подгоняем под написание случая: «Замок» в начале фразы.
                    Program.KeepFirstLetterCase(entry.Word, form),
                    expanded: index == _controlExpandedIndex,
                    viewed: _controlViewedIndices.Contains(index),
                    mark: moved ? "✗" : string.Empty,
                    markHint: moved ? $"ИИ предлагала «{entry.AiForm}», вы оставили «{entry.Proposed}»." : string.Empty);
            })
            .ToList();

    // Случаи третьего окна: знак стоит на предлоге, само слово показывается без ударения.
    // Знак вопроса — у случаев, где после слова стоит ещё одно слово и перенос стоит проверить.
    private List<ControlCaseItem> BuildControlEnclinomenCases(List<int> indices) =>
        indices.Where(index => index >= 0 && index < _entries.Count)
            .Select(index =>
            {
                var entry = _entries[index];
                return ControlCaseItem.For(index, entry, entry.Word,
                    expanded: index == _controlExpandedIndex,
                    viewed: _controlViewedIndices.Contains(index),
                    mark: entry.EnclinomenDoubtful ? "?" : string.Empty,
                    markHint: entry.EnclinomenDoubtful
                        ? "Перенос на предлог выбран вручную или после слова стоит ещё одно слово — проверьте глазами, что предлог действительно ударный."
                        : string.Empty,
                    preposition: entry.Preposition);
            })
            .ToList();

    // Заголовки окон: сколько случаев в варианте, сколько из них прочитано, чьи это варианты
    // (RUAccent/Silero) и выбрала ли этот вариант ИИ. Зелёная метка «✔ выбор ИИ» стоит у того
    // варианта, который ИИ считала общей формой слова.
    private void UpdateControlHeaders()
    {
        var group = CurrentControlGroup();
        if (group == null) return;
        var aiChoice = ControlAiChoiceForm(group);
        // Голоса моделей считаем по всем случаям слова: у пустого второго варианта иначе
        // в подсказке было бы «0 из 0», хотя форму предложила одна из моделей.
        var support = group.Left.Concat(group.Right).ToList();
        SetControlBucketHeader(ControlLeftHeaderTextBlock, "Вариант 1", group.LeftForm, group.Left, aiChoice, support);
        SetControlBucketHeader(ControlRightHeaderTextBlock, "Вариант 2", group.RightForm, group.Right, aiChoice, support);
        SetControlEnclinomenHeader(group);
    }

    // Шапка третьего окна: сколько вхождений получило ударение на предлог и сколько из них
    // помечено вопросом. Голоса моделей здесь не показываются: RUAccent и Silero предлагают
    // ударение слова, а перенос на предлог они не умеют вовсе.
    private void SetControlEnclinomenHeader(ControlGroupData group)
    {
        var block = ControlEnclinomenHeaderTextBlock;
        var indices = group.Enclinomen;
        var viewed = indices.Count(_controlViewedIndices.Contains);
        var doubtful = indices.Count(index => index >= 0 && index < _entries.Count && _entries[index].EnclinomenDoubtful);
        block.Inlines.Clear();
        var head = string.Join(", ", indices.Where(index => index >= 0 && index < _entries.Count)
            .Select(index => EnclinomenDisplay(_entries[index]))
            .GroupBy(display => display, StringComparer.Ordinal)
            .OrderByDescending(item => item.Count())
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(item => item.Key + (item.Count() > 1 ? $" ×{item.Count()}" : string.Empty)));
        block.Inlines.Add(new Run($"На предлог: {head}"));
        if (doubtful > 0)
        {
            block.Inlines.Add(new Run(" ?")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x6A, 0x14)),
                FontWeight = FontWeights.SemiBold,
            });
        }
        block.Inlines.Add(new Run($" — случаев {indices.Count}"
                                  + (viewed == 0 ? string.Empty : $", прочитано {viewed}")));
        block.ToolTip = $"На предлог: {head} — случаев {indices.Count}; прочитано: {viewed}"
                        + (doubtful == 0 ? string.Empty : $"; к проверке: {doubtful}");
    }

    private void SetControlBucketHeader(TextBlock block, string title, string form, List<int> indices, string aiChoice,
        List<int>? support = null)
    {
        var viewed = indices.Count(_controlViewedIndices.Contains);
        var (ru, silero) = ModelVotes(support ?? indices, form);
        block.Inlines.Clear();
        block.Inlines.Add(new Run($"{title}: {form}"));
        // Пустой вариант не может быть «выбором ИИ»: иначе при совпавшей общей форме
        // зелёная метка появлялась бы и над окном, где нет ни одного случая.
        var isAiChoice = indices.Count > 0 && aiChoice.Length > 0 && StressSignature(aiChoice) == StressSignature(form);
        if (isAiChoice)
        {
            block.Inlines.Add(new Run(" ✔ ИИ")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x3E)),
                FontWeight = FontWeights.SemiBold,
            });
        }
        block.Inlines.Add(new Run($" — случаев {indices.Count}"
                                  + (viewed == 0 ? string.Empty : $", прочитано {viewed}")));
        // Полная статистика заголовка не помещается в узкую колонку — она в подсказке.
        block.ToolTip = $"{title}: {form} — случаев {indices.Count}; совпало с RUAccent: {ru}; с Silero: {silero}"
                        + (viewed == 0 ? string.Empty : $"; прочитано: {viewed}")
                        + (isAiChoice ? "; выбор ИИ" : string.Empty);
    }

    // Сколько случаев варианта поддержала каждая модель: у кого ударение совпало с вариантом.
    private (int RuAccent, int Silero) ModelVotes(List<int> indices, string form)
    {
        var ru = 0;
        var silero = 0;
        foreach (var index in indices)
        {
            if (index < 0 || index >= _entries.Count) continue;
            var entry = _entries[index];
            if (MatchesStress(entry.RuAccent, form)) ru++;
            if (MatchesStress(entry.Silero, form)) silero++;
        }
        return (ru, silero);
    }

    // Совпадает ли ударение формы модели с ударением варианта: «по́лу» и «по́лу» — да.
    private static bool MatchesStress(string? form, string variant)
    {
        if (string.IsNullOrWhiteSpace(form)) return false;
        var signature = StressSignature(form);
        return signature.Position >= 0 && signature == StressSignature(variant);
    }

    // Выбор ИИ по слову: самая частая её форма среди случаев. Пусто — ответов ИИ по случаям нет
    // (отчёт открыт без прогона omogre и без сохранённых ответов). При равенстве голосов метка
    // остаётся на варианте 1: именно он и стал общей формой слова в разборе ИИ.
    private string ControlAiChoiceForm(ControlGroupData group)
    {
        var forms = new List<string>();
        foreach (var index in group.Left.Concat(group.Right))
        {
            if (index < 0 || index >= _entries.Count) continue;
            var form = _entries[index].AiForm;
            if (form.Length > 0) forms.Add(form);
        }
        if (forms.Count == 0) return string.Empty;
        var bySignature = forms.GroupBy(StressSignature).OrderByDescending(g => g.Count()).ToList();
        var best = bySignature[0].Count();
        var leaders = bySignature.Where(g => g.Count() == best).Select(g => g.First()).ToList();
        return leaders.FirstOrDefault(form => StressSignature(form) == StressSignature(group.LeftForm)) ?? leaders[0];
    }

    private void UpdateControlNavigationState()
    {
        ControlPreviousWordButton.IsEnabled = _controlGroupIndex > 0;
        ControlNextWordButton.IsEnabled = _controlGroupIndex >= 0 && _controlGroupIndex < _controlGroups.Count - 1;
        ControlSaveButton.IsEnabled = _controlGroups.Count > 0;
        ControlToManualButton.IsEnabled = _entries.Count > 0;
        ControlMoveRightButton.IsEnabled = ControlLeftCasesListBox.SelectedItem != null;
        ControlMoveLeftButton.IsEnabled = ControlRightCasesListBox.SelectedItem != null;
        // «⇢» берёт выделенный случай из любого окна и включается там, где перед словом есть
        // предлог: точный разбор энклиноменов или ручной перенос для слова, которого нет в списке.
        ControlMoveToPrepositionButton.IsEnabled = ControlCanMoveToPreposition(ControlLeftCasesListBox.SelectedItem)
            || ControlCanMoveToPreposition(ControlRightCasesListBox.SelectedItem);
        ControlMoveFromPrepositionButton.IsEnabled = ControlEnclinomenCasesListBox.SelectedItem != null;
        ControlUndoButton.IsEnabled = _controlMoveHistory.Count > 0;
        ControlUndoButton.Content = _controlMoveHistory.Count == 0
            ? "Отменить перенос" : $"Отменить перенос ({_controlMoveHistory.Count})";
        ControlJournalButton.Content = _controlJournal.Count == 0
            ? "Журнал правок" : $"Журнал правок ({_controlJournal.Count})";
        ControlResetViewedButton.IsEnabled = _controlViewedIndices.Count > 0;
        ControlResetViewedButton.Content = _controlViewedIndices.Count == 0
            ? "Сбросить прочитанное" : $"Сбросить прочитанное ({_controlViewedIndices.Count})";
        var accuracy = ComputeControlAccuracy();
        var hasAccuracyWork = accuracy.Verified > 0 || accuracy.EnclinomenViewed > 0;
        ControlAccuracyButton.IsEnabled = hasAccuracyWork;
        ControlAccuracyButton.Content = !hasAccuracyWork
            ? "Отчёт о точности"
            : $"Отчёт о точности ({FormatPercent(accuracy.Correct, accuracy.Correct + accuracy.Wrong)})";
    }

    private void NavigateControlGroup(int direction)
    {
        if (_controlGroups.Count == 0) return;
        _controlGroupIndex = Math.Clamp(_controlGroupIndex + direction, 0, _controlGroups.Count - 1);
        RenderControlGroup();
    }

    // Куда переносится случай: два варианта ударения слова и третье окно — ударение на предлог.
    private enum ControlBucket { Left, Right, Enclinomen }

    // Окна случаев на странице контроля: вариант 1, вариант 2 и ударение на предлог.
    private IEnumerable<ListBox> ControlCaseLists()
    {
        yield return ControlLeftCasesListBox;
        yield return ControlRightCasesListBox;
        yield return ControlEnclinomenCasesListBox;
    }

    // Перенос случая в другое окно: у него меняется ударение. Влево — общая форма группы,
    // вправо — вариант 2, в третье окно — ударение на предлог перед словом.
    private void MoveControlCase(ListBox source, ControlBucket target)
    {
        if (source.SelectedItem is not ControlCaseItem item) { FileStatusTextBlock.Text = "Выберите случай в одном из окон."; return; }
        MoveControlCaseAt(item.Index, target);
    }

    // Кнопка «на предлог» берёт случай из того окна, где есть выделение: так одна кнопка
    // годится и для варианта 1, и для варианта 2. Переносится только случай, перед которым
    // стоит предлог из списка энклиноменов; из третьего окна он сначала переходит в вариант 1.
    private void MoveSelectedControlCase(ControlBucket target)
    {
        foreach (var list in ControlCaseLists())
            if (list.SelectedItem is ControlCaseItem item) { MoveControlCaseAt(item.Index, target); return; }
        FileStatusTextBlock.Text = "Выберите случай в одном из окон.";
    }

    // Годится ли выделенный случай для переноса ударения на предлог. Автоматический разбор
    // знает не все слова, поэтому кнопка доступна и тогда, когда перед словом просто стоит
    // предлог, способный уводить ударение: решение принимает человек, случай пометится вопросом.
    private bool ControlCanMoveToPreposition(object? selected) =>
        selected is ControlCaseItem item
        && item.Index >= 0 && item.Index < _entries.Count
        && (Program.TryMatchEnclinomen(_entries[item.Index].Context, _entries[item.Index].Word,
                _entries[item.Index].ContextOccurrence) != null
            || Program.TryFindManualPreposition(_entries[item.Index].Context, _entries[item.Index].Word,
                _entries[item.Index].ContextOccurrence, out _));

    // Тот же перенос, но по номеру случая: так его вызывают и перетаскивание мышкой,
    // и меню правой кнопки, когда выбранного элемента в списке ещё нет. Каждый перенос
    // записывается в журнал правок и кладётся в стек отмены — его снимает кнопка «Отменить».
    private void MoveControlCaseAt(int index, ControlBucket target)
    {
        if (_controlGroupIndex < 0 || _controlGroupIndex >= _controlGroups.Count) return;
        if (index < 0 || index >= _entries.Count) return;
        var group = _controlGroups[_controlGroupIndex];
        var entry = _entries[index];
        if ((target == ControlBucket.Left && group.Left.Contains(index))
            || (target == ControlBucket.Right && group.Right.Contains(index))
            || (target == ControlBucket.Enclinomen && group.Enclinomen.Contains(index)))
        {
            FileStatusTextBlock.Text = "Случай уже в этом окне.";
            return;
        }
        var wasPreposition = entry.Preposition != null;
        var before = ControlCaseState(entry);
        string? preposition = null;
        var doubtful = false;
        var manual = false;
        if (target == ControlBucket.Enclinomen)
        {
            // Сначала пробуем точный разбор энклиноменов: он учитывает падеж и зависимые слова.
            // Если слова нет в списке, разрешаем ручной перенос на предлог — иначе редкие
            // и новые словоформы («на́ ноги») навсегда оставались бы в вариантах ударения.
            var match = Program.TryMatchEnclinomen(entry.Context, entry.Word, entry.ContextOccurrence);
            if (match != null)
            {
                preposition = Program.StressPreposition(match.PrepositionStressed);
                doubtful = match.Doubtful;
            }
            else if (Program.TryFindManualPreposition(entry.Context, entry.Word, entry.ContextOccurrence,
                         out var manualPreposition))
            {
                preposition = manualPreposition;
                doubtful = true;
                manual = true;
            }
            else
            {
                FileStatusTextBlock.Text = $"Перед словом «{entry.Word}» нет предлога, на который можно перенести ударение.";
                return;
            }
        }
        _controlMoveHistory.Add(new ControlMoveStep(ExactWordKey(group.Word), index, target,
            entry.Included, entry.Proposed, entry.Preposition, entry.EnclinomenDoubtful));
        group.Left.Remove(index);
        group.Right.Remove(index);
        group.Enclinomen.Remove(index);
        if (target == ControlBucket.Enclinomen)
        {
            // Ударение уходит на предлог, слово остаётся без знака — и только для этого места.
            entry.Preposition = preposition;
            entry.EnclinomenDoubtful = doubtful;
            entry.Included = false;
            entry.Proposed = entry.Word;
            group.Enclinomen.Add(index);
        }
        else if (target == ControlBucket.Right)
        {
            var form = ApplySignature(entry.Word, StressSignature(group.RightForm));
            entry.Preposition = null;
            entry.EnclinomenDoubtful = false;
            entry.Included = false;
            entry.Proposed = form.Length > 0 ? form : group.RightForm;
            group.Right.Add(index);
        }
        else
        {
            // Общая форма группы. Возврат из третьего окна делает вхождение отдельным:
            // иначе разбор энклиноменов снова увёл бы ударение на предлог.
            entry.Preposition = null;
            entry.EnclinomenDoubtful = false;
            entry.Included = !wasPreposition;
            entry.Proposed = group.LeftForm;
            group.Left.Add(index);
        }
        var after = ControlCaseState(entry);
        FileStatusTextBlock.Text = target == ControlBucket.Enclinomen
            ? manual
                ? $"Ударение перенесено на предлог вручную: «{after}». Проверьте, что предлог действительно ударный."
                : $"Ударение перенесено на предлог: «{after}». Слово остаётся безударным только в этом месте."
            : $"Случай перенесён в {(target == ControlBucket.Left ? "вариант 1" : "вариант 2")} («{entry.Proposed}»).";
        LogControlEdit(entry.Word, index,
            target == ControlBucket.Enclinomen ? "ударение на предлог" : target == ControlBucket.Left ? "перенос в вариант 1" : "перенос в вариант 2",
            before, after);
        RenderControlGroup();
    }

    // Как случай выглядит сейчас: «на́ пол» для энклиномена и ударение слова во всех остальных.
    private static string ControlCaseState(MutableReviewEntry entry) =>
        entry.Preposition != null ? EnclinomenDisplay(entry) : entry.Proposed;

    private static string EnclinomenDisplay(MutableReviewEntry entry) => entry.Preposition + " " + entry.Word;

    // Отмена переноса: случай возвращается в своё окно с прежним ударением. Отменять можно
    // по шагам назад — стек снимается сверху. Разложение пересобирается из самих записей,
    // поэтому отменённый случай всегда встаёт ровно туда, где был.
    private void UndoControlMove()
    {
        if (_controlMoveHistory.Count == 0)
        {
            FileStatusTextBlock.Text = "Отменять нечего: переносов случаев в этой сессии ещё не было.";
            return;
        }
        var step = _controlMoveHistory[^1];
        _controlMoveHistory.RemoveAt(_controlMoveHistory.Count - 1);
        if (step.Index < 0 || step.Index >= _entries.Count)
        {
            UpdateControlNavigationState();
            FileStatusTextBlock.Text = "Отменить нельзя: отчёт перезагружен, случай найти не удалось.";
            return;
        }
        var entry = _entries[step.Index];
        var before = ControlCaseState(entry);
        entry.Included = step.PreviousIncluded;
        entry.Proposed = step.PreviousProposed;
        entry.Preposition = step.PreviousPreposition;
        entry.EnclinomenDoubtful = step.PreviousDoubtful;
        // Отмена переноса на предлог оставляет вхождение отдельным: иначе разбор тут же
        // предложил бы перенос снова, и отменённый случай вернулся бы в третье окно.
        if (step.Target == ControlBucket.Enclinomen && entry.Preposition == null) entry.Included = false;
        _controlGroups = BuildControlGroups();
        var groupIndex = _controlGroups.FindIndex(group => ExactWordKey(group.Word) == step.WordKey);
        if (groupIndex >= 0) _controlGroupIndex = groupIndex;
        LogControlEdit(entry.Word, step.Index, UndoActionTitle(step.Target), before, ControlCaseState(entry));
        RenderControlGroup();
        FileStatusTextBlock.Text =
            $"Отменено: «{entry.Word}» снова в варианте {UndoBucketTitle(entry)} («{ControlCaseState(entry)}»).";
    }

    // Куда случай вернулся после отмены: подсказка о прежнем состоянии.
    private static string UndoBucketTitle(MutableReviewEntry entry) =>
        entry.Enclinomen ? "на предлог" : entry.Included ? "1" : "2";

    private static string UndoActionTitle(ControlBucket target) => target switch
    {
        ControlBucket.Enclinomen => "отмена ударения на предлог",
        ControlBucket.Left => "отмена переноса в вариант 1",
        _ => "отмена переноса в вариант 2",
    };

    // Запись в журнал правок. Фрагмент хранится вместе с записью: список должен читаться
    // сам по себе и в окне проверки, и в txt-документе, уже без открытого отчёта.
    private void LogControlEdit(string word, int index, string action, string before, string after)
    {
        var context = index >= 0 && index < _entries.Count ? _entries[index].Context : string.Empty;
        _controlJournal.Add(new ControlEditEntry(DateTime.Now, index, word, ExactWordKey(word), action, before, after, context));
        Program.LogReview($"Контроль ИИ: {word} — {action}; {before} -> {after}");
        _refreshControlJournalWindow?.Invoke();
        UpdateControlNavigationState();
    }

    // --- Журнал правок и отмена -----------------------------------------------------------------
    // Каждый перенос случая записан: время, слово, что заменено на что и фрагмент. Журнал
    // открывается в окне быстрой проверки (двойной щелчок возвращает к случаю на странице)
    // и сохраняется в txt-документ рядом с отчётом.

    private void ShowControlJournal()
    {
        if (_controlJournalWindow is { IsLoaded: true } open)
        {
            _refreshControlJournalWindow?.Invoke();
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var window = new Window
        {
            Title = "Журнал правок — страница «3. Контроль ИИ результатов»",
            Width = 940,
            Height = 560,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xED, 0xDD)),
            FontSize = 13,
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x60, 0x4D)),
            Text = "Здесь каждая правка страницы контроля: время, слово, действие и фрагмент. "
                + "Двойной щелчок по строке (или кнопка «Открыть случай») возвращает к этому случаю — "
                + "там его видно в тексте и можно перенести обратно. Список относится к открытому сейчас отчёту.",
        };
        var list = new ListBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0xFC, 0xF8, 0xEF)),
            DisplayMemberPath = nameof(ControlEditEntry.Display),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

        void Refresh()
        {
            var selected = list.SelectedItem;
            list.ItemsSource = new List<ControlEditEntry>(_controlJournal);
            if (selected is ControlEditEntry entry && list.Items.Contains(entry)) list.SelectedItem = entry;
        }

        var open_ = new Button { Content = "Открыть случай", Padding = new Thickness(12, 6, 12, 6) };
        open_.Click += (_, _) => OpenSelectedControlEdit(list);
        var save = new Button { Content = "Сохранить в txt…", Padding = new Thickness(12, 6, 12, 6) };
        save.Click += (_, _) => SaveControlJournal();
        var copy = new Button { Content = "Копировать список", Padding = new Thickness(12, 6, 12, 6) };
        copy.Click += (_, _) => CopyControlJournal();
        var close = new Button { Content = "Закрыть", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(open_);
        buttons.Children.Add(save);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        list.MouseDoubleClick += (_, _) => OpenSelectedControlEdit(list);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        window.Content = panel;
        window.Closed += (_, _) =>
        {
            _controlJournalWindow = null;
            _refreshControlJournalWindow = null;
        };

        _controlJournalWindow = window;
        _refreshControlJournalWindow = Refresh;
        Refresh();
        window.Show();
    }

    private void OpenSelectedControlEdit(ListBox list)
    {
        if (list.SelectedItem is not ControlEditEntry entry)
        {
            FileStatusTextBlock.Text = "Выберите строку журнала.";
            return;
        }
        FocusControlCase(entry.Index);
    }

    // Быстрая проверка из журнала: открываем страницу контроля на нужном слове и выделяем случай.
    private void FocusControlCase(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            FileStatusTextBlock.Text = "К этому случаю вернуться нельзя: отчёт перезагружен.";
            return;
        }
        _controlGroups = BuildControlGroups();
        var groupIndex = _controlGroups.FindIndex(group => group.Left.Contains(index) || group.Right.Contains(index));
        if (groupIndex < 0)
        {
            FileStatusTextBlock.Text = "Случай в текущем разборе не найден: у слова больше нет двух вариантов ударения.";
            return;
        }
        _controlGroupIndex = groupIndex;
        ReviewTabs.SelectedIndex = 2;
        RenderControlGroup();
        var left = (ControlLeftCasesListBox.ItemsSource as List<ControlCaseItem>)?.FirstOrDefault(item => item.Index == index);
        var right = (ControlRightCasesListBox.ItemsSource as List<ControlCaseItem>)?.FirstOrDefault(item => item.Index == index);
        var box = left != null ? ControlLeftCasesListBox : ControlRightCasesListBox;
        var target = left ?? right;
        if (target == null) return;
        box.SelectedItem = target;
        box.ScrollIntoView(target);
        box.Focus();
        FileStatusTextBlock.Text =
            $"Случай слова «{_entries[index].Word}» открыт на странице контроля: «{target.Exact}».";
    }

    private string ControlJournalSuggestedPath()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return Path.Combine(Environment.CurrentDirectory, "правки-контроля.txt");
        var full = Path.GetFullPath(_reviewPath!);
        return Path.Combine(Path.GetDirectoryName(full) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(full) + "_правки-контроля.txt");
    }

    private string ControlJournalText()
    {
        var text = new StringBuilder();
        text.AppendLine("DSR — журнал правок на странице «3. Контроль ИИ результатов»");
        text.AppendLine("Книга: " + (_outputPath ?? "не выбрана"));
        text.AppendLine("Отчёт: " + (_reviewPath ?? "не выбран"));
        text.AppendLine($"Правок в журнале: {_controlJournal.Count}; слов с вариантами ударения: {_controlGroups.Count}");
        text.AppendLine();
        foreach (var item in _controlJournal) text.Append(item.ToText()).AppendLine();
        return text.ToString();
    }

    // Кнопка и сохранение из окна: путь выбирается рядом с отчётом, имя предсказуемое.
    private void SaveControlJournal()
    {
        if (_controlJournal.Count == 0)
        {
            FileStatusTextBlock.Text = "Журнал правок пуст: сохранять пока нечего.";
            return;
        }
        var suggested = ControlJournalSuggestedPath();
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить журнал правок",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested),
            Filter = "Текстовый документ (*.txt)|*.txt|Все файлы (*.*)|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        WriteControlJournal(dialog.FileName);
    }

    private void WriteControlJournal(string target)
    {
        try
        {
            File.WriteAllText(target, ControlJournalText(), new UTF8Encoding(false));
            Program.LogReview($"Журнал правок контроля сохранён: {Path.GetFileName(target)}; правок {_controlJournal.Count}");
            FileStatusTextBlock.Text = "Журнал правок сохранён: " + target;
        }
        catch (Exception error)
        {
            FileStatusTextBlock.Text = "Журнал правок не сохранился: " + error.Message;
            Program.LogError("Сохранение журнала правок", error);
        }
    }

    private void CopyControlJournal()
    {
        if (_controlJournal.Count == 0)
        {
            FileStatusTextBlock.Text = "Журнал правок пуст.";
            return;
        }
        try
        {
            Clipboard.SetText(ControlJournalText());
            FileStatusTextBlock.Text = $"Журнал правок скопирован в буфер: записей {_controlJournal.Count}.";
        }
        catch (Exception error)
        {
            FileStatusTextBlock.Text = "Скопировать журнал не удалось: " + error.Message;
        }
    }

    // --- Отчёт о точности ИИ --------------------------------------------------------------------
    // Кнопка «Отчёт о точности»: итог по проверенным случаям, разбор по словам (чей вариант —
    // RUAccent или Silero, что выбрала ИИ) и списки случаев, где ИИ была права и где ошиблась.
    // Файл пишется рядом с отчётом расхождений, проверенные случаи уходят в журнал примеров.

    private string ControlAccuracySuggestedPath()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return Path.Combine(Environment.CurrentDirectory, "точность-ии.txt");
        var full = Path.GetFullPath(_reviewPath!);
        return Path.Combine(Path.GetDirectoryName(full) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(full) + "_точность-ии.txt");
    }

    private string ControlAccuracyReportText()
    {
        var stats = ComputeControlAccuracy();
        var judged = stats.Correct + stats.Wrong;
        var text = new StringBuilder();
        text.AppendLine("DSR — отчёт о точности ИИ на странице «3. Контроль ИИ результатов»");
        text.AppendLine("Книга: " + (_outputPath ?? "не выбрана"));
        text.AppendLine("Отчёт: " + (_reviewPath ?? "не выбран"));
        text.AppendLine("Когда: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        text.AppendLine("Движок ИИ: " + _llmEngine.Title);
        text.AppendLine();
        text.AppendLine("--- Итог ---");
        text.AppendLine($"Слов с двумя вариантами ударения: {_controlGroups.Count}");
        text.AppendLine($"Случаев всего: {stats.Total}; проверено: {stats.Verified}; не проверено: {stats.Total - stats.Verified}");
        text.AppendLine($"ИИ (omogre) права: {stats.Correct} из {judged} — {FormatPercent(stats.Correct, judged)}");
        text.AppendLine($"ИИ ошиблась: {stats.Wrong} из {judged} — {FormatPercent(stats.Wrong, judged)}");
        if (stats.WithoutAi > 0)
            text.AppendLine($"Проверено, но без ответа ИИ (в точность не входит): {stats.WithoutAi}");
        var totalStress = _outputPath != null && File.Exists(_outputPath)
            ? File.ReadAllText(_outputPath).Count(c => c == '\u0301') : 0;
        text.AppendLine($"Ручных исправлений: {_manualCorrectionCount} из {totalStress} ударений ({FormatPercent(_manualCorrectionCount, totalStress)})");
        text.AppendLine($"RUAccent совпал с вашим решением: {stats.RuMatch} из {stats.Verified} — {FormatPercent(stats.RuMatch, stats.Verified)}");
        text.AppendLine($"Silero совпал с вашим решением: {stats.SileroMatch} из {stats.Verified} — {FormatPercent(stats.SileroMatch, stats.Verified)}");
        text.AppendLine();
        text.AppendLine("--- Ударение на предлог (энклиномены) ---");
        text.AppendLine($"Случаев на предлог: {stats.EnclinomenCases}; прочитано: {stats.EnclinomenViewed}. "
                        + "В точность ИИ они не входят: предлоги ИИ не размечает, решение принимает разбор энклиноменов.");
        foreach (var index in _controlGroups.SelectMany(group => group.Enclinomen))
        {
            if (index < 0 || index >= _entries.Count) continue;
            var entry = _entries[index];
            text.AppendLine($"«{entry.Preposition} {entry.Word}»" + (entry.EnclinomenDoubtful ? " — перенос проверен глазами?" : string.Empty)
                            + $"; ИИ — «{(entry.AiForm.Length > 0 ? entry.AiForm : "нет ответа")}»"
                            + $"; RUAccent — «{entry.RuAccent ?? "нет"}», Silero — «{entry.Silero ?? "нет"}»");
            text.AppendLine("    Фрагмент: " + entry.Context);
        }
        text.AppendLine();
        text.AppendLine("--- Чей вариант (по словам) ---");
        foreach (var group in _controlGroups)
        {
            var aiChoice = ControlAiChoiceForm(group);
            var (leftRu, leftSilero) = ModelVotes(group.Left, group.LeftForm);
            var (rightRu, rightSilero) = ModelVotes(group.Right, group.RightForm);
            text.AppendLine($"«{group.Word}»");
            text.AppendLine($"    вариант 1 «{group.LeftForm}»: случаев {group.Left.Count} (RUAccent {leftRu}, Silero {leftSilero})"
                            + (aiChoice.Length > 0 && StressSignature(aiChoice) == StressSignature(group.LeftForm) ? " — выбор ИИ" : string.Empty));
            text.AppendLine($"    вариант 2 «{group.RightForm}»: случаев {group.Right.Count} (RUAccent {rightRu}, Silero {rightSilero})"
                            + (aiChoice.Length > 0 && StressSignature(aiChoice) == StressSignature(group.RightForm) ? " — выбор ИИ" : string.Empty));
            // Решение по этому слову может уходить на предлог: тогда оба окна с ним пусты,
            // и без этой строки слово выглядело бы так, будто его никто не проверял.
            if (group.Enclinomen.Count > 0)
                text.AppendLine($"    на предлог: случаев {group.Enclinomen.Count}"
                                + string.Concat(group.Enclinomen.Where(index => index >= 0 && index < _entries.Count)
                                    .GroupBy(index => EnclinomenDisplay(_entries[index]))
                                    .Select(pair => $" · «{pair.Key}» ×{pair.Count()}")));
        }
        text.AppendLine();
        text.AppendLine("--- Где ИИ ошиблась (случай перенесён) ---");
        AppendControlVerdicts(text, wrong: true);
        text.AppendLine();
        text.AppendLine("--- Где ИИ права ---");
        AppendControlVerdicts(text, wrong: false);
        return text.ToString();
    }

    private void AppendControlVerdicts(StringBuilder text, bool wrong)
    {
        var cases = ControlVerifiedCases().Where(item => item.Wrong == wrong).ToList();
        if (cases.Count == 0)
        {
            text.AppendLine("    (нет)");
            return;
        }
        foreach (var (index, group, _) in cases)
        {
            var entry = _entries[index];
            text.AppendLine($"«{entry.Word}»: ИИ — «{(entry.AiForm.Length > 0 ? entry.AiForm : "нет ответа")}», "
                            + $"вы — «{entry.Proposed}»; вариант 1 «{group.LeftForm}», вариант 2 «{group.RightForm}»; "
                            + $"RUAccent — «{entry.RuAccent ?? "нет"}», Silero — «{entry.Silero ?? "нет"}»");
            text.AppendLine("    Фрагмент: " + entry.Context);
        }
    }

    private void SaveControlAccuracyReport(bool openAfterSave)
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return;
        var stats = ComputeControlAccuracy();
        if (stats.Verified == 0 && stats.EnclinomenViewed == 0)
        {
            FileStatusTextBlock.Text = "Отчёт о точности пока не из чего собрать: ни один случай не проверен.";
            return;
        }
        var target = ControlAccuracySuggestedPath();
        try
        {
            File.WriteAllText(target, ControlAccuracyReportText(), new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            FileStatusTextBlock.Text = "Отчёт о точности не сохранился: " + error.Message;
            Program.LogError("Сохранение отчёта о точности ИИ", error);
            return;
        }
        var logged = LogControlTrainingCases();
        Program.LogReview($"Отчёт о точности ИИ сохранён: {Path.GetFileName(target)}; проверено {stats.Verified}, "
                          + $"права {stats.Correct}, ошиблась {stats.Wrong}, примеров добавлено {logged}");
        FileStatusTextBlock.Text =
            $"Отчёт о точности сохранён: {target}. Проверено {stats.Verified}, точность "
            + $"{FormatPercent(stats.Correct, stats.Correct + stats.Wrong)}"
            + (logged > 0 ? $"; примеров для обучения добавлено {logged}." : ".");
        if (!openAfterSave) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception error) { Program.LogError("Открытие отчёта о точности", error); }
    }

    // Проверенные случаи — по строке JSON на случай: фрагмент, ответ ИИ, ваше решение и ответы
    // моделей. Повторно тот же случай с тем же решением не пишется.
    private int LogControlTrainingCases()
    {
        var added = 0;
        foreach (var (index, group, wrong) in ControlVerifiedCases())
        {
            var entry = _entries[index];
            if (entry.AiForm.Length == 0) continue;
            if (!_trainingLogged.Add(index + "\u0001" + entry.Proposed)) continue;
            Program.AppendTrainingExample(new
            {
                type = "control-case",
                source = "контроль-ии",
                word = PlainLetters(entry.Word),
                stressed = entry.Word,
                context = entry.Context,
                occurrence = entry.ContextOccurrence,
                ai = entry.AiForm,
                accepted = entry.Proposed,
                aiCorrect = !wrong,
                ruAccent = entry.RuAccent,
                silero = entry.Silero,
                variantLeft = group.LeftForm,
                variantRight = group.RightForm,
                engine = _llmEngine.Title,
                book = Path.GetFileName(_outputPath ?? _reviewPath ?? string.Empty),
                report = Path.GetFileName(_reviewPath ?? string.Empty),
                created = DateTimeOffset.Now,
            });
            added++;
        }
        // Случаи с ударением на предлог: их пишем отдельным типом. По ним видно, какие
        // сочетания «предлог + слово» разбор принимает, — это заготовка для обучения моделей.
        foreach (var (index, group) in ControlEnclinomenCases())
        {
            var entry = _entries[index];
            if (!_controlViewedIndices.Contains(index)) continue;
            if (!_trainingLogged.Add("enclinomen\u0001" + index + "\u0001" + entry.Preposition)) continue;
            Program.AppendTrainingExample(new
            {
                type = "enclinomen",
                source = "контроль-ии",
                word = PlainLetters(entry.Word),
                stressed = entry.Word,
                preposition = entry.Preposition,
                doubtful = entry.EnclinomenDoubtful,
                context = entry.Context,
                occurrence = entry.ContextOccurrence,
                ai = entry.AiForm,
                ruAccent = entry.RuAccent,
                silero = entry.Silero,
                variantLeft = group.LeftForm,
                variantRight = group.RightForm,
                engine = _llmEngine.Title,
                book = Path.GetFileName(_outputPath ?? _reviewPath ?? string.Empty),
                report = Path.GetFileName(_reviewPath ?? string.Empty),
                created = DateTimeOffset.Now,
            });
            added++;
        }
        return added;
    }

    // Щелчок по случаю: один — раскрыть фрагмент целиком для чтения, двойной — перенести
    // случай в другое окно. Оба действия приходят одним событием, поэтому различаем по счётчику
    // щелчков.
    private void ControlCasesMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Отпускание кнопки после перетаскивания щелчком не считается.
        var wasDrag = _controlDragHappened;
        _controlDragItem = null;
        _controlDragHappened = false;
        if (wasDrag) return;
        if (sender is not ListBox list || e.ChangedButton != MouseButton.Left) return;
        if (ControlCaseAt(list, e.GetPosition(list)) is not { } caseItem) return;
        list.SelectedItem = caseItem;
        if (e.ClickCount >= 2)
        {
            // Двойной щелчок переносит случай в соседнее окно: влево — вариант 2, обратно —
            // вариант 1, из третьего окна — тоже вариант 1. Перенос на предлог делают кнопкой
            // «⇢», меню правой кнопки или перетаскиванием.
            MoveControlCase(list, ReferenceEquals(list, ControlLeftCasesListBox) ? ControlBucket.Right : ControlBucket.Left);
            e.Handled = true;
            return;
        }
        ToggleControlCase(caseItem);
    }

    // Раскрыт всегда один случай. Раскрывая новый, сворачиваем прежний и помечаем его
    // просмотренным: иначе непонятно, какие случаи уже прочитаны, а какие нет.
    private void ToggleControlCase(ControlCaseItem caseItem)
    {
        if (caseItem.IsExpanded)
        {
            caseItem.IsExpanded = false;
            _controlExpandedIndex = -1;
            MarkControlCaseViewed(caseItem);
        }
        else
        {
            CollapseExpandedControlCase();
            caseItem.IsExpanded = true;
            _controlExpandedIndex = caseItem.Index;
            // После раскрытия подкручиваем список к случаю: большой фрагмент иначе
            // остаётся за нижней границей окна, и его приходится искать вручную.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var list in ControlCaseLists())
                {
                    if (!list.Items.Contains(caseItem)) continue;
                    list.ScrollIntoView(caseItem);
                    break;
                }
            }), DispatcherPriority.Background);
        }
        UpdateControlHeaders();
        UpdateControlSummary();
        UpdateControlNavigationState();
    }

    private void CollapseExpandedControlCase()
    {
        foreach (var item in ControlCaseItems())
        {
            if (!item.IsExpanded) continue;
            item.IsExpanded = false;
            MarkControlCaseViewed(item);
        }
        _controlExpandedIndex = -1;
    }

    private void MarkControlCaseViewed(ControlCaseItem caseItem)
    {
        if (caseItem.IsViewed) return;
        caseItem.IsViewed = true;
        _controlViewedIndices.Add(caseItem.Index);
    }

    // Случаи всех трёх окон: и для сворачивания, и для счёта прочитанных.
    private IEnumerable<ControlCaseItem> ControlCaseItems()
    {
        foreach (var source in ControlCaseLists().Select(list => list.ItemsSource))
        {
            if (source is not IEnumerable<ControlCaseItem> items) continue;
            foreach (var item in items) yield return item;
        }
    }

    // Сброс пометок «прочитано»: слово можно пройти заново. Переносы остаются — это уже
    // решения, их снимает только «Отменить перенос»; статистика начинает считаться заново.
    private void ResetControlViewed()
    {
        if (_controlViewedIndices.Count == 0)
        {
            FileStatusTextBlock.Text = "Пометок «прочитано» нет.";
            return;
        }
        var count = _controlViewedIndices.Count;
        _controlViewedIndices.Clear();
        foreach (var item in ControlCaseItems()) item.IsViewed = false;
        UpdateControlHeaders();
        UpdateControlSummary();
        UpdateControlNavigationState();
        Program.LogReview($"Контроль ИИ: сброшены пометки «прочитано» ({count})");
        FileStatusTextBlock.Text = $"Пометки «прочитано» сброшены ({count}). Переносы случаев не тронуты, статистика считается заново.";
    }

    // --- Точность ИИ по проверенным случаям ------------------------------------------------------
    // Проверенным считается случай, который раскрывали (прочитано) или переносили. Права ИИ
    // или нет — по её ответу именно для этого вхождения: совпало ли ударение с тем, что вы
    // оставили. Ответы ИИ хранятся в ресурсы\ии-по-случаям.json, поэтому итог переживает
    // перезапуск и ручные переносы.

    private sealed record ControlAccuracy(int Total, int Verified, int Correct, int Wrong, int WithoutAi,
        int RuMatch, int SileroMatch, int EnclinomenCases, int EnclinomenViewed);

    private ControlAccuracy ComputeControlAccuracy()
    {
        var seen = new HashSet<int>();
        int total = 0, verified = 0, correct = 0, wrong = 0, withoutAi = 0, ruMatch = 0, sileroMatch = 0;
        foreach (var group in _controlGroups)
        {
            CountCases(group.Left);
            CountCases(group.Right);
        }
        var enclinomenCases = 0;
        var enclinomenViewed = 0;
        foreach (var (index, _) in ControlEnclinomenCases())
        {
            enclinomenCases++;
            if (_controlViewedIndices.Contains(index)) enclinomenViewed++;
        }
        return new ControlAccuracy(total, verified, correct, wrong, withoutAi, ruMatch, sileroMatch,
            enclinomenCases, enclinomenViewed);

        void CountCases(List<int> indices)
        {
            foreach (var index in indices)
            {
                if (index < 0 || index >= _entries.Count || !seen.Add(index)) continue;
                total++;
                var entry = _entries[index];
                var moved = ControlCaseMoved(entry);
                if (!moved && !_controlViewedIndices.Contains(index)) continue;
                verified++;
                if (entry.AiForm.Length == 0) withoutAi++;
                else if (moved) wrong++;
                else correct++;
                if (MatchesStress(entry.RuAccent, entry.Proposed)) ruMatch++;
                if (MatchesStress(entry.Silero, entry.Proposed)) sileroMatch++;
            }
        }
    }

    // Случаи третьего окна: ударение ушло на предлог. В точность ИИ они не входят —
    // размечать предлоги ИИ не умеет, и сравнивать её ответ со словом тут не с чем.
    // В отчёте они идут отдельной строкой, а проверенными считаются прочитанные.
    private IEnumerable<(int Index, ControlGroupData Group)> ControlEnclinomenCases()
    {
        var seen = new HashSet<int>();
        foreach (var group in _controlGroups)
            foreach (var index in group.Enclinomen)
            {
                if (index < 0 || index >= _entries.Count || !seen.Add(index)) continue;
                yield return (index, group);
            }
    }

    // ИИ предложила другое ударение, чем осталось у случая: её ответ не принят.
    private static bool ControlCaseMoved(MutableReviewEntry entry) =>
        entry.AiForm.Length > 0 && StressSignature(entry.AiForm) != StressSignature(entry.Proposed);

    // Проверенные случаи с вердиктом: ошиблась ИИ или нет.
    private IEnumerable<(int Index, ControlGroupData Group, bool Wrong)> ControlVerifiedCases()
    {
        var seen = new HashSet<int>();
        foreach (var group in _controlGroups)
        {
            foreach (var index in group.Left.Concat(group.Right))
            {
                if (index < 0 || index >= _entries.Count || !seen.Add(index)) continue;
                var entry = _entries[index];
                var wrong = ControlCaseMoved(entry);
                if (!wrong && !_controlViewedIndices.Contains(index)) continue;
                yield return (index, group, wrong);
            }
        }
    }

    private string ControlAccuracySummaryLine()
    {
        var stats = ComputeControlAccuracy();
        var enclinomen = stats.EnclinomenCases == 0
            ? string.Empty
            : $" На предлог (энклиномены): {stats.EnclinomenCases}, прочитано {stats.EnclinomenViewed} — в точность ИИ не входят.";
        if (stats.Verified == 0)
            return (stats.Total == 0
                ? "Случаев для контроля нет."
                : "Проверенных случаев пока нет: раскройте случай (один щелчок) или перенесите его — тогда здесь появится точность ИИ.") + enclinomen;
        var judged = stats.Correct + stats.Wrong;
        var text = $"Проверено {stats.Verified} из {stats.Total}: ИИ права в {stats.Correct}, ошиблась в {stats.Wrong} — точность {FormatPercent(stats.Correct, judged)}.";
        if (stats.WithoutAi > 0) text += $" Без ответа ИИ: {stats.WithoutAi}.";
        text += $" RUAccent совпал с вами в {stats.RuMatch} из {stats.Verified}, Silero — в {stats.SileroMatch}.";
        return text + enclinomen;
    }

    private static string FormatPercent(int part, int total) =>
        total <= 0 ? "—" : (part * 100.0 / total).ToString("0.0") + " %";

    // --- Перенос случая мышкой ------------------------------------------------------------------
    // Случай можно перетащить в другое окно — это тот же перенос, что двойным щелчком. Зажатие
    // запоминаем на нажатии, а перетаскивание начинаем только после сдвига мыши: иначе обычный
    // щелчок с дрожанием руки превращался бы в перетаскивание.

    private void ControlCasesDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        _controlDragHappened = false;
        _controlDragItem = sender is ListBox list ? ControlCaseAt(list, e.GetPosition(list)) : null;
        _controlDragStart = e.GetPosition(this);
    }

    private void ControlCasesDragMouseMove(object sender, MouseEventArgs e)
    {
        if (_controlDragItem is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not ListBox list) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _controlDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _controlDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _controlDragItem = null;
        _controlDragHappened = true;
        list.SelectedItem = item;
        // Данные кладём прямо объектом: перетаскивание не выходит за пределы процесса,
        // поэтому сериализация не нужна.
        DragDrop.DoDragDrop(list, new DataObject(ControlCaseDragFormat, item), DragDropEffects.Move);
    }

    private void ControlCasesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (sender is ListBox list && e.Data.GetData(ControlCaseDragFormat) is ControlCaseItem item)
        {
            var group = CurrentControlGroup();
            // Окно, на которое навели мышь, — приёмник: переносим только туда, где случая ещё
            // нет, чтобы перетаскивание внутри своего же окна ничего не меняло.
            var target = ControlBucketForList(list);
            var current = group == null ? null
                : target == ControlBucket.Left ? group.Left
                : target == ControlBucket.Right ? group.Right
                : group.Enclinomen;
            if (current != null && !current.Contains(item.Index))
            {
                // В третье окно пускаем всё, перед чем есть предлог: точный разбор или
                // ручной перенос. Остальные окна принимают случай без дополнительных условий.
                var canMove = target != ControlBucket.Enclinomen || ControlCanMoveToPreposition(item);
                if (canMove) e.Effects = DragDropEffects.Move;
            }
        }
        e.Handled = true;
    }

    private void ControlCasesDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (e.Data.GetData(ControlCaseDragFormat) is not ControlCaseItem item) return;
        MoveControlCaseAt(item.Index, ControlBucketForList(list));
        e.Handled = true;
    }

    private ControlBucket ControlBucketForList(ListBox list) =>
        ReferenceEquals(list, ControlLeftCasesListBox) ? ControlBucket.Left
        : ReferenceEquals(list, ControlRightCasesListBox) ? ControlBucket.Right
        : ControlBucket.Enclinomen;

    private ControlGroupData? CurrentControlGroup() =>
        _controlGroupIndex >= 0 && _controlGroupIndex < _controlGroups.Count ? _controlGroups[_controlGroupIndex] : null;

    // Правая кнопка — меню как в окне ручной доводки: скопировать слово с контекстом и открыть
    // предложение в Google. Плюс перенос случая в другое окно: то же, что двойной щелчок.
    private void ControlCasesContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list || list.ContextMenu is not { } menu) return;
        var caseItem = ControlCaseAt(list, Mouse.GetPosition(list));
        if (caseItem == null || caseItem.Index < 0 || caseItem.Index >= _entries.Count)
        {
            e.Handled = true; // щелчок по пустому месту под списком
            return;
        }
        list.SelectedItem = caseItem;
        var entry = _entries[caseItem.Index];
        menu.Items.Clear();

        var copy = new MenuItem { Header = "Копировать слово и контекст" };
        copy.Click += (_, _) => CopyGroupText(new[] { entry });
        menu.Items.Add(copy);

        var google = new MenuItem { Header = "Открыть предложение в Google: куда поставить ударение?" };
        google.Click += (_, _) => OpenGoogleStressSearch(entry.Word, entry.Context);
        menu.Items.Add(google);

        menu.Items.Add(new Separator());
        // Перенос случая: в любое из трёх окон, кроме того, где он сейчас. В третье окно —
        // только если перед словом стоит предлог из списка энклиноменов.
        var current = ControlBucketForList(list);
        AddMoveItem(menu, caseItem.Index, ControlBucket.Left, "Перенести в вариант 1", current);
        AddMoveItem(menu, caseItem.Index, ControlBucket.Right, "Перенести в вариант 2", current);
        var match = caseItem.Index < _entries.Count
            ? Program.TryMatchEnclinomen(_entries[caseItem.Index].Context, _entries[caseItem.Index].Word,
                _entries[caseItem.Index].ContextOccurrence)
            : null;
        if (match != null)
        {
            var title = $"Ударение на предлог: «{match.Display}»";
            if (_entries[caseItem.Index].EnclinomenDoubtful || match.Doubtful) title += " (проверить)";
            AddMoveItem(menu, caseItem.Index, ControlBucket.Enclinomen, title, current);
        }
        else if (caseItem.Index < _entries.Count
                 && Program.TryFindManualPreposition(_entries[caseItem.Index].Context, _entries[caseItem.Index].Word,
                     _entries[caseItem.Index].ContextOccurrence, out var manualPreposition))
        {
            var title = $"Ударение на предлог вручную: «{manualPreposition} {_entries[caseItem.Index].Word}» (проверить)";
            AddMoveItem(menu, caseItem.Index, ControlBucket.Enclinomen, title, current);
        }
        else if (current == ControlBucket.Enclinomen)
        {
            AddMoveItem(menu, caseItem.Index, ControlBucket.Enclinomen, "Ударение на предлог (нет предлога перед словом)", current);
        }
    }

    private void AddMoveItem(ContextMenu menu, int index, ControlBucket target, string title, ControlBucket current)
    {
        var item = new MenuItem { Header = title, IsEnabled = current != target };
        item.Click += (_, _) => MoveControlCaseAt(index, target);
        menu.Items.Add(item);
    }

    // Случай под точкой нажатия. Ищем контейнер ListBoxItem от самого нижнего элемента:
    // у текстовых элементов (Run) визуального родителя нет, поэтому поднимаемся по логическому.
    private ControlCaseItem? ControlCaseAt(ListBox list, Point point) =>
        FindControlAncestor<ListBoxItem>(list.InputHitTest(point) as DependencyObject)?.DataContext as ControlCaseItem;

    private static T? FindControlAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // Сохраняет контроль в отчёт: общие формы групп и отдельные ударения случаев.
    // К файлу книги ничего не применяется — итог собирается кнопкой «Завершить работу».
    private void SaveControlResults()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return;
        SaveReviewState();
        RememberSession();
        var groups = CollectEditedGroups().Count;
        // Журнал правок пишется рядом с отчётом сам: переносы случаев — часть работы,
        // и потерять их при закрытии окна не должно.
        var journalNote = string.Empty;
        if (_controlJournal.Count > 0)
        {
            var target = ControlJournalSuggestedPath();
            WriteControlJournal(target);
            journalNote = $" Журнал правок ({_controlJournal.Count:N0}) записан рядом с отчётом: {Path.GetFileName(target)}.";
        }
        // Итог точности пишется рядом с отчётом сам: проверенные случаи — часть работы,
        // и после сохранения контроля их уже не нужно собирать заново.
        var accuracyNote = string.Empty;
        var accuracy = ComputeControlAccuracy();
        if (accuracy.Verified > 0 || accuracy.EnclinomenViewed > 0)
        {
            SaveControlAccuracyReport(openAfterSave: false);
            accuracyNote = $" Отчёт о точности ИИ записан: {Path.GetFileName(ControlAccuracySuggestedPath())}.";
        }
        FileStatusTextBlock.Text =
            $"Контроль ИИ сохранён в отчёт: слов с вариантами {_controlGroups.Count:N0}; групп правок к применению {groups:N0}.{journalNote}{accuracyNote} " +
            "Проверьте спорные случаи на вкладке «4. Ручная доводка», затем соберите итог кнопкой «Завершить работу».";
    }

    // Ударение формы как «позиция гласной + сама гласная»: «За́мок» и «за́мок» — один вариант,
    // а «за́мок» и «замо́к» — разные. По этой подписи случаи и раскладываются по окнам.
    private static (int Position, char Vowel) StressSignature(string word)
    {
        var acute = word.IndexOf('\u0301');
        if (acute > 0)
        {
            var position = acute - 1;
            var plain = PlainLetters(word);
            if (position >= 0 && position < plain.Length) return (position, char.ToLowerInvariant(plain[position]));
        }
        var yo = word.IndexOfAny(new[] { 'ё', 'Ё' });
        return yo >= 0 ? (yo, 'ё') : (-1, '\0');
    }

    // Переносит ударение варианта на конкретное написание случая (с его регистром и е/ё).
    // Пустая строка — если буквы не совпали; тогда вызывающий берёт форму варианта как есть.
    private static string ApplySignature(string letters, (int Position, char Vowel) signature)
    {
        if (signature.Position < 0) return string.Empty;
        var plain = PlainLetters(letters);
        if (signature.Position >= plain.Length) return string.Empty;
        if (signature.Vowel == 'ё')
        {
            var current = char.ToLowerInvariant(plain[signature.Position]);
            if (current != 'е' && current != 'ё') return string.Empty;
            var chars = plain.ToCharArray();
            chars[signature.Position] = char.IsUpper(chars[signature.Position]) ? 'Ё' : 'ё';
            return new string(chars);
        }
        if (char.ToLowerInvariant(plain[signature.Position]) != signature.Vowel) return string.Empty;
        return plain.Insert(signature.Position + 1, "\u0301");
    }

    // Контекстный прогон omogre: разбор каждого случая расхождения, а не слова целиком.
    // Ответы сразу раскладываются по двум вариантам ударения — их показывает страница
    // «3. Контроль ИИ результатов».
    private async Task RunOmogreCaseCheckAsync(bool recheck)
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
        var cases = _entries.Select((entry, index) => (entry, index))
            .Where(pair => HasModelDisagreement(pair.entry))
            .Select(pair => (Index: pair.index,
                Item: new Program.LlmCaseItem(pair.entry.Word, pair.entry.Context, pair.entry.ContextOccurrence)))
            .ToList();
        if (cases.Count == 0)
        {
            FileStatusTextBlock.Text = "В отчёте нет расхождений RUAccent/Silero для контекстного разбора.";
            return;
        }
        var book = !string.IsNullOrWhiteSpace(_outputPath) ? _outputPath! : _reviewPath!;
        LlmCheckButton.IsEnabled = false;
        LlmRecheckButton.IsEnabled = false;
        var progress = new LlmProgressWindow(total: 0);
        progress.Owner = this;
        progress.Show();
        try
        {
            var records = cases;
            var items = cases.Select(pair => pair.Item).ToArray();
            var outcome = await Task.Run(() => Program.RunOmogreCaseCheck(items, book,
                message => Dispatcher.Invoke(() => { if (progress.IsLoaded) progress.SetStatus(message); }),
                progress.Token, _llmEngine));
            if (!outcome.Ok)
            {
                if (progress.Token.IsCancellationRequested) _llmStatus = "Контекстный разбор omogre прерван пользователем.";
                else
                {
                    Program.LogReview("Контекстный разбор omogre не выполнен: " + outcome.Message);
                    MessageBox.Show(this, outcome.Message, "Контроль ИИ", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                ApplyCaseAnswers(records, outcome);
                SaveReviewState();
                _llmStatus = $"omogre по случаям: ответов {outcome.Answered} из {outcome.Items}; устройство {outcome.Device}; {outcome.Seconds:0} с.";
                Program.LogReview(_llmStatus);
                RefreshControlPage();
                var splits = _controlGroups.Count;
                FileStatusTextBlock.Text = _llmStatus + Environment.NewLine + (splits > 0
                    ? $"Слов с разными вариантами ударения по контексту: {splits:N0}. Откройте вкладку «3. Контроль ИИ результатов»: проверьте, верно ли разложены случаи, и перенесите неверные."
                    : "Внутри слов omogre дал один вариант ударения — разносить по окнам нечего. Проверьте слова на вкладке «4. Ручная доводка».");
                ReviewTabs.SelectedIndex = 2;
            }
        }
        catch (Exception error)
        {
            Program.LogError("Контекстный разбор omogre", error);
            MessageBox.Show(this, "Контекстный разбор omogre не удался: " + error.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progress.Close();
            LlmCheckButton.IsEnabled = true;
            LlmRecheckButton.IsEnabled = true;
        }
    }

    // Раскладывает ответы omogre по двум вариантам: самый частый становится общей формой
    // группы, остальные случаи получают своё отдельное ударение (снятый флажок).
    private void ApplyCaseAnswers(List<(int Index, Program.LlmCaseItem Item)> cases, Program.LlmCaseOutcome outcome)
    {
        // Новый разбор переписывает ударения всех случаев: прежние шаги отмены к нему не подходят.
        _controlMoveHistory.Clear();
        // Ответы по случаям сохраняются отдельно: по ним потом считается точность ИИ,
        // в том числе после перезапуска и после ручных переносов.
        var answersForCache = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var wordGroup in cases.GroupBy(pair => ExactWordKey(pair.Item.Word), StringComparer.Ordinal))
        {
            var answered = wordGroup.Select(pair => (
                pair.Index,
                Form: outcome.Cases.TryGetValue(
                    Program.LlmCaseKey(pair.Item.Word, pair.Item.Context, pair.Item.Occurrence), out var form)
                    ? LowerFirstLetter(form) : string.Empty)).ToList();
            var forms = answered.Where(item => item.Form.Length > 0).ToList();
            if (forms.Count == 0) continue;
            var leftForm = forms.GroupBy(item => item.Form, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .First().Key;
            var leftSignature = StressSignature(leftForm);
            var rightForm = forms.Where(item => StressSignature(item.Form) != leftSignature)
                .GroupBy(item => item.Form, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key).FirstOrDefault() ?? leftForm;
            var key = ExactWordKey(wordGroup.First().Item.Word);
            _controlVariants[key] = (leftForm, rightForm);
            // Вкладка 2 показывает большинство слова: единый вариант, который выбрал ИИ.
            _llmSuggestions[key] = leftForm;
            foreach (var (index, form) in answered)
            {
                if (index < 0 || index >= _entries.Count) continue;
                var entry = _entries[index];
                entry.AiForm = form;
                if (form.Length == 0 || StressSignature(form) == leftSignature)
                {
                    entry.Included = true;
                    entry.Proposed = leftForm;
                }
                else
                {
                    entry.Included = false;
                    entry.Proposed = form;
                }
            }
            foreach (var pair in wordGroup)
            {
                var caseKey = Program.LlmCaseKey(pair.Item.Word, pair.Item.Context, pair.Item.Occurrence);
                if (outcome.Cases.TryGetValue(caseKey, out var form) && form.Length > 0) answersForCache[caseKey] = LowerFirstLetter(form);
            }
        }
        Program.SaveCaseAnswers(_reviewPath, answersForCache);
        // Разобрано больше случаев — часть слов переходит с вкладки 4 на страницу контроля.
        Program.RefreshManualReviewCandidates(_reviewPath);
        RefreshNeedsManualFlags();
        RebuildGroupList();
    }

    // Пересчитывает по ответам ИИ, какие случаи ещẽ ждут ручной доводки.
    private void RefreshNeedsManualFlags()
    {
        foreach (var entry in _entries) entry.NeedsManual = Program.IsManualReviewCandidate(entry.ToEntry());
    }

    private sealed class ControlGroupData
    {
        public ControlGroupData(string word, string leftForm, string rightForm)
        {
            Word = word;
            LeftForm = leftForm;
            RightForm = rightForm;
        }

        public string Word { get; }
        public string LeftForm { get; }
        public string RightForm { get; }
        public List<int> Left { get; } = new();
        public List<int> Right { get; } = new();
        // Третье окно: случаи, у которых ударение уходит на предлог («на́ пол»).
        public List<int> Enclinomen { get; } = new();
    }

    // Случай на странице контроля: конкретное вхождение и его ударение в выбранном варианте.
    // Строкой показан сокращённо; один щелчок раскрывает фрагмент целиком (IsExpanded), а после
    // сворачивания случай помечается просмотренным (IsViewed) — полоской слева и спокойным фоном.
    private sealed class ControlCaseItem : INotifyPropertyChanged
    {
        private static readonly Regex WordTokens = new(
            @"[\p{L}\p{M}\p{N}_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private bool _expanded;
        private bool _viewed;

        private ControlCaseItem(int index, string before, string exact, string after, bool expanded, bool viewed,
            string mark, string markHint)
        {
            Index = index;
            Before = before;
            Exact = exact;
            After = after;
            _expanded = expanded;
            _viewed = viewed;
            Mark = mark;
            MarkHint = markHint;
        }

        public int Index { get; }
        public string Before { get; }
        public string Exact { get; }
        public string After { get; }

        /// <summary>Метка «✗» у перенесённого случая: ИИ предлагала другое ударение.</summary>
        public string Mark { get; }

        /// <summary>Подсказка к метке: что предлагала ИИ и что осталось.</summary>
        public string MarkHint { get; }

        /// <summary>Раскрыт ли фрагмент целиком (по щелчку). Раскрыт всегда один случай.</summary>
        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        /// <summary>Случай раскрывали и свернули — считайте, прочитан. Живёт до загрузки другого отчёта.</summary>
        public bool IsViewed
        {
            get => _viewed;
            set
            {
                if (_viewed == value) return;
                _viewed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsViewed)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // Имя элемента для программ чтения с экрана и для автоматических проверок: текст случая.
        public override string ToString() => Before + Exact + After;

        public static ControlCaseItem For(int index, MutableReviewEntry entry, string form, bool expanded, bool viewed,
            string mark, string markHint, string? preposition = null)
        {
            var context = entry.Context;
            // Ищем именно то вхождение, к которому относится случай: в длинном фрагменте слово
            // может встретиться несколько раз. Раньше бралось первое, и знак ударения вставал
            // не над проверяемым словом, а над его первым упоминанием.
            var (start, length) = FindOccurrence(context, entry.Word, entry.ContextOccurrence);
            if (start < 0)
                return new ControlCaseItem(index, context + " ", form, string.Empty, expanded, viewed, mark, markHint);
            var before = StressWordOccurrences(context[..start], entry.Word, form);
            // У случая с переносом знак стоит на предлоге: «по» превращается в «по́».
            if (!string.IsNullOrEmpty(preposition)) before = Program.MarkPrecedingPreposition(before, preposition);
            var after = StressWordOccurrences(context[(start + length)..], entry.Word, form);
            return new ControlCaseItem(index, before, form, after, expanded, viewed, mark, markHint);
        }

        // Отчёт хранит одно расхождение на слово и фрагмент, даже если слово встретилось
        // в нём несколько раз. Чтобы знак не пропадал у второго упоминания, подставляем
        // выбранное ударение во все ещё безударные вхождения этого слова в фрагменте.
        private static string StressWordOccurrences(string text, string word, string form)
        {
            if (text.Length == 0 || word.Length == 0 || StressSignature(form).Position < 0) return text;
            var key = Program.ReviewWordKey(word);
            var result = new StringBuilder(text.Length + 8);
            var last = 0;
            foreach (Match match in WordTokens.Matches(text))
            {
                if (!Program.ReviewWordKey(match.Value).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                // Уже стоящий знак не трогаем: это отдельное решение отчёта.
                if (StressSignature(match.Value).Position >= 0) continue;
                result.Append(text, last, match.Index - last);
                result.Append(Program.KeepFirstLetterCase(match.Value, form));
                last = match.Index + match.Length;
            }
            result.Append(text, last, text.Length - last);
            return result.ToString();
        }

        // Номер вхождения считается по словам без знака ударения: в контексте отчёта другие
        // слова уже стоят с акутом, а проверяемое слово — ещё без него.
        private static (int Index, int Length) FindOccurrence(string context, string word, int occurrence)
        {
            var key = Program.ReviewWordKey(word);
            var seen = 0;
            foreach (Match match in WordTokens.Matches(context))
            {
                if (!Program.ReviewWordKey(match.Value).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen == occurrence) return (match.Index, match.Length);
                seen++;
            }
            return (-1, 0);
        }
    }

    // Шаг отмены: состояние случая до переноса. Хватит и ключа слова, и прежнего ударения —
    // разложение пересобирается из записей, поэтому отменённый случай встаёт на своё место.
    // Перенос на предлог хранит ещё и сам предлог: откат снимает его с случая.
    private sealed record ControlMoveStep(string WordKey, int Index, ControlBucket Target, bool PreviousIncluded,
        string PreviousProposed, string? PreviousPreposition, bool PreviousDoubtful);

    // Правка на странице контроля. Хранит время, слово, действие и фрагмент: одна и та же
    // запись идёт и в окно быстрой проверки, и в txt-документ.
    private sealed record ControlEditEntry(DateTime Time, int Index, string Word, string WordKey,
        string Action, string Before, string After, string Context)
    {
        public string Display => $"{Time:HH:mm:ss}  {Word}  {Action}: {Before} → {After}";

        public string ToText() =>
            $"[{Time:yyyy-MM-dd HH:mm:ss}] {Word}{Environment.NewLine}" +
            $"    Действие: {Action}{Environment.NewLine}" +
            $"    Было: {Before}{Environment.NewLine}" +
            $"    Стало: {After}{Environment.NewLine}" +
            $"    Фрагмент: {Context}{Environment.NewLine}";
    }

    private sealed class MutableReviewEntry
    {
        private readonly (string Context, int Occurrence) _splitContext;
        public MutableReviewEntry(Program.WordReviewEntry entry)
        {
            Word = entry.Word;
            Proposed = entry.Proposed;
            RuAccent = entry.RuAccent;
            Silero = entry.Silero;
            // «Своя форма» из отчёта снова становится снятым флажком: у образца своё ударение.
            Included = !entry.Individual;
            // «На предлог» из отчёта: случай снова встаёт в третье окно, слово — без знака.
            // В отчёте предлог записан без знака («по»), а показывать и переносить его надо
            // с ударением («по́»): иначе третье окно и заголовок выглядят безударными.
            Preposition = string.IsNullOrWhiteSpace(entry.Preposition)
                ? null
                : Program.StressPreposition(entry.Preposition);
            _splitContext = Program.SplitReviewContext(entry.Context);
        }
        public string Word { get; }
        public string Context => _splitContext.Context;
        public int ContextOccurrence => _splitContext.Occurrence;
        public string Proposed { get; set; }
        public string? RuAccent { get; }
        public string? Silero { get; }
        public bool Included { get; set; } = true;
        // Слово нужно в ручной доводке: у него нет готового ответа ИИ по этому вхождению
        // (и это не перенос на предлог). Остальные разобраны на вкладке 3.
        public bool NeedsManual { get; set; } = true;
        // Энклиномен: ударение ушло на предлог перед словом, само слово остаётся без знака —
        // и только в этом вхождении. Пусто — переноса нет.
        public string? Preposition { get; set; }
        // После слова стоит слово, которое может оказаться зависимым: перенос предложен,
        // но его стоит проверить глазами (метка «?» на случае).
        public bool EnclinomenDoubtful { get; set; }
        public bool Enclinomen => Preposition != null;
        // Ответ ИИ именно для этого вхождения, до ручных правок. Пусто — ИИ не ответила
        // или ответы по случаям ещё не загружены: тогда судить о её правоте нельзя.
        public string AiForm { get; set; } = string.Empty;
        // Слово уже посмотрено человеком и правка к нему применена. Такие слова остаются
        // в отчёте (иначе документ нельзя будет открыть повторно), но помечаются галочкой.
        public bool Checked { get; set; }
        // Это вхождение лежит в ручной доводке; пометка сохраняется в отчёте.
        public bool Manual { get; set; }
        public Program.WordReviewEntry ToEntry() => new(Word, $"{Context}\u0001{ContextOccurrence}", Proposed, RuAccent, Silero, !Included)
        {
            Preposition = Preposition,
            Manual = Manual,
        };
    }

    public sealed record ReviewItem(int Index, string Display, string Word, int Count, bool Done);

    private sealed class SampleItem : INotifyPropertyChanged
    {
        private string _detail;

        public SampleItem(int index, string context, bool included, string before, string exact, string after, string detail = "")
        {
            Index = index;
            Context = context;
            Included = included;
            Before = before;
            Exact = exact;
            After = after;
            _detail = detail;
        }

        public int Index { get; }
        public string Context { get; }
        public bool Included { get; }
        public string Before { get; }
        public string Exact { get; }
        public string After { get; }

        // Пометка о своей форме у снятого флажка должна обновляться сразу, как только
        // пользователь задал ударение случаю, — иначе непонятно, сохранилась ли правка.
        public string Detail
        {
            get => _detail;
            set
            {
                if (string.Equals(_detail, value, StringComparison.Ordinal)) return;
                _detail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static SampleItem For(int index, string context, string word, bool included, string detail = "")
        {
            var plain = context.Replace("\u0301", "");
            var start = word.Length == 0 ? -1 : plain.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return new SampleItem(index, context, included, context + " ", word, string.Empty, detail);
            return new SampleItem(index, context, included, plain[..start], plain.Substring(start, word.Length), plain[(start + word.Length)..], detail);
        }
    }


    // --- Перетаскивание фрагментов в ручную доводку и обратно ----------------------------------
    // Слово под мышью: обычная SelectedItem не годится — при перетаскивании невыделенного
    // элемента выбор ещё не успевает сместиться.
    private static ReviewItem? ReviewItemAt(ListBox list, Point point)
    {
        var element = list.InputHitTest(point) as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        return (element as ListBoxItem)?.DataContext as ReviewItem;
    }

    // Данные перетаскивания приводятся к одному ключу слова: и случай со вкладки 3,
    // и строка короткого списка вкладки 2, и слово из ручной доводки.
    private bool TryResolveManualReviewKey(IDataObject data, out string key, out int entryIndex, out bool caseSpecific)
    {
        key = string.Empty;
        entryIndex = -1;
        caseSpecific = false;
        if (data.GetData(ManualReviewDragFormat) is string manual && manual.Length > 0) { key = manual; return true; }
        if (data.GetData(ControlCaseDragFormat) is ControlCaseItem item)
        {
            if (item.Index < 0 || item.Index >= _entries.Count) return false;
            key = ExactWordKey(_entries[item.Index].Word);
            entryIndex = item.Index;
            caseSpecific = true;
            return true;
        }
        if (data.GetData(typeof(ReviewItem)) is ReviewItem reviewItem && !string.IsNullOrEmpty(reviewItem.Word))
        {
            key = ExactWordKey(reviewItem.Word);
            entryIndex = reviewItem.Index;
            return true;
        }
        return false;
    }

    // Вкладка под курсором во время перетаскивания: 2 — «Контроль ИИ результатов»,
    // 3 — «Ручная доводка». Определяется по визуальному дереву, поэтому годится и для
    // заголовка вкладки, и для её содержимого.
    private int? DropTargetTab(DragEventArgs e)
    {
        var element = ReviewTabs.InputHitTest(e.GetPosition(ReviewTabs)) as DependencyObject;
        while (element != null)
        {
            if (element is TabItem tab) return ReviewTabs.Items.IndexOf(tab);
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void ReviewTabs_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropTargetTab(e) switch
        {
            3 when TryResolveManualReviewKey(e.Data, out _, out _, out _) => DragDropEffects.Move,
            2 when e.Data.GetData(ManualReviewDragFormat) is string => DragDropEffects.Move,
            _ => DragDropEffects.None
        };
        e.Handled = true;
    }

    private void ReviewTabs_Drop(object sender, DragEventArgs e)
    {
        var target = DropTargetTab(e);
        // Вкладка 4: перенесённое слово появляется в ручной доводке. Списки пересобираем
        // после завершения перетаскивания, иначе источник теряет перетаскиваемый контейнер.
        if (target == 3 && TryResolveManualReviewKey(e.Data, out var key, out var entryIndex, out var caseSpecific))
        {
            if (caseSpecific && entryIndex >= 0)
                AddCaseToManualReview(entryIndex);
            else
                AddWordToManualReview(key);
            // Сразу после пересборки показываем слово и выбранный случай в ручной доводке —
            // и слово исчезает из списков контроля ИИ.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyReviewItems();
                var note = SelectManualReviewWord(key, entryIndex);
                if (note.Length > 0) FileStatusTextBlock.Text += " " + note;
            }));
            e.Effects = DragDropEffects.Move;
            FileStatusTextBlock.Text = caseSpecific ? "Случай перенесён в ручную доводку и убран из контроля ИИ." : "Слово перенесено в ручную доводку и убрано из контроля ИИ.";
        }
        // Вкладка 3: обратный перенос — слово уходит из ручной доводки.
        else if (target == 2 && e.Data.GetData(ManualReviewDragFormat) is string backKey)
        {
            if (_manualReviewKeys.Contains(backKey) || _entries.Any(item => ExactWordKey(item.Word) == backKey && IsManualReviewCase(item)))
            {
                RemoveWordFromManualReview(backKey);
                Dispatcher.BeginInvoke(new Action(ApplyReviewItems));
                e.Effects = DragDropEffects.Move;
                FileStatusTextBlock.Text = "Слово убрано из ручной доводки.";
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    // Нажатие и перетаскивание строки короткого списка вкладки 2.
    private void Unrecognized_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _unrecognizedDragItem = sender is ListBox list ? ReviewItemAt(list, e.GetPosition(list)) : null;
        _unrecognizedDragStart = e.GetPosition(this);
    }

    private void Unrecognized_MouseMove(object sender, MouseEventArgs e)
    {
        if (_unrecognizedDragItem is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not ListBox list) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _unrecognizedDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _unrecognizedDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _unrecognizedDragItem = null;
        list.SelectedItem = item;
        DragDrop.DoDragDrop(list, new DataObject(typeof(ReviewItem), item), DragDropEffects.Move);
    }

    // Нажатие и перетаскивание слова из ручной доводки (вкладка 4) — обратно на вкладку 3.
    private void ManualReviewWord_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _wordDragItem = sender is ListBox list ? ReviewItemAt(list, e.GetPosition(list)) : null;
        _wordDragStart = e.GetPosition(this);
    }

    private void ManualReviewWord_MouseMove(object sender, MouseEventArgs e)
    {
        if (_wordDragItem is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not ListBox list) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _wordDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _wordDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _wordDragItem = null;
        list.SelectedItem = item;
        DragDrop.DoDragDrop(list, new DataObject(ManualReviewDragFormat, ExactWordKey(item.Word)), DragDropEffects.Move);
    }

    // Кнопка «Перейти к ручной доводке»: отправляет туда слово, открытое в контроле ИИ,
    // и раскрывает тот случай, который был выбран в одном из трёх окон.
    private void SendControlGroupToManualReview()
    {
        var group = CurrentControlGroup();
        if (group == null)
        {
            ReviewTabs.SelectedIndex = 3;
            return;
        }
        var key = ExactWordKey(group.Word);
        var entryIndex = SelectedControlCaseIndex();
        if (entryIndex < 0 && group != null)
            entryIndex = group.Left.Concat(group.Right).Concat(group.Enclinomen).FirstOrDefault(-1);
        if (entryIndex >= 0)
            AddCaseToManualReview(entryIndex);
        else
            AddWordToManualReview(key);
        ApplyReviewItems();
        ReviewTabs.SelectedIndex = 3;
        var note = SelectManualReviewWord(key, entryIndex);
        FileStatusTextBlock.Text = $"«{group!.Word}»: перенесён в ручную доводку случай {(entryIndex >= 0 ? entryIndex + 1 : 0)} и убран из контроля ИИ."
            + (note.Length > 0 ? " " + note : string.Empty);
    }

    // Номер случая, выделенного в любом из трёх окон контроля; -1 — выделения нет.
    private int SelectedControlCaseIndex()
    {
        foreach (var list in ControlCaseLists())
            if (list.SelectedItem is ControlCaseItem item) return item.Index;
        return -1;
    }

    // После переноса показываем слово в списке вкладки 4: иначе вкладка открывается,
    // но выбранным остаётся другое слово или ничего, и кажется, что перенос не сработал.
    // Возвращает пояснение, если для показа пришлось снять фильтр.
    private string SelectManualReviewWord(string key, int entryIndex = -1)
    {
        var note = string.Empty;
        var found = FindManualReviewWord(key);
        if (found == null)
        {
            note = RevealManualReviewWord(key, entryIndex);
            if (note.Length > 0)
            {
                ApplyReviewItems();
                found = FindManualReviewWord(key);
            }
        }
        if (found == null) return string.Empty;
        if (entryIndex >= 0 && entryIndex < _entries.Count
            && ExactWordKey(_entries[entryIndex].Word) == key)
        {
            _selectedSampleIndex = entryIndex;
        }
        if (!ReferenceEquals(WordGroupsListBox.SelectedItem, found)) WordGroupsListBox.SelectedItem = found;
        else SelectGroup(found.Index);
        WordGroupsListBox.ScrollIntoView(found);
        return note;
    }

    private ReviewItem? FindManualReviewWord(string key) =>
        WordGroupsListBox.Items.Cast<ReviewItem>()
            .FirstOrDefault(candidate => ExactWordKey(candidate.Word) == key);

    // Слово могло быть скрыто фильтрами вкладки 4: уже применённая правка («Показывать
    // проверенные» выключено), фильтр «ИИ не разобрала / без ударения» или строка поиска.
    // Если человек явно перенёс слово, оно должно быть видно, поэтому снимаем ровно тот
    // фильтр, который его прячет. Возвращает пояснение для строки состояния.
    private string RevealManualReviewWord(string key, int entryIndex = -1)
    {
        List<MutableReviewEntry> entries;
        if (entryIndex >= 0 && entryIndex < _entries.Count && ExactWordKey(_entries[entryIndex].Word) == key)
            entries = new List<MutableReviewEntry> { _entries[entryIndex] };
        else
            entries = _entries.Where(entry => ExactWordKey(entry.Word) == key).ToList();
        if (entries.Count == 0) return string.Empty;
        var search = SearchTextBox.Text.Trim();
        var searchHides = search.Length > 0 && !entries.Any(entry =>
            entry.Word.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Context.Contains(search, StringComparison.OrdinalIgnoreCase));
        var filterHides = ManualFilterComboBox.SelectedIndex != 0 && !ManualFilterAllows(entries[0].Word);
        var doneHides = entries.Any(entry => entry.Checked) && ShowCheckedManualCheckBox.IsChecked != true;
        if (!searchHides && !filterHides && !doneHides) return string.Empty;

        if (searchHides) SearchTextBox.Text = string.Empty;
        if (filterHides) ManualFilterComboBox.SelectedIndex = 0;
        if (doneHides) ShowCheckedManualCheckBox.IsChecked = true;
        var reasons = string.Join(", ", new[]
        {
            searchHides ? "сброшен поиск" : null,
            filterHides ? "фильтр вкладки 4 переключён на «Всё для ручной доводки»" : null,
            doneHides ? "включён показ проверенных слов" : null
        }.Where(reason => reason != null));
        return $"Чтобы перенесённое слово было видно, {reasons}.";
    }

    // Кнопка «Пропустить ИИ → ручной выбор»: осознанно переносит туда все расхождения.
    private void SendAllDisagreementsToManualReview()
    {
        foreach (var entry in _entries.Where(HasModelDisagreement)) AddCaseToManualReview(_entries.IndexOf(entry));
        ApplyReviewItems();
        ReviewTabs.SelectedIndex = 3;
    }
}

