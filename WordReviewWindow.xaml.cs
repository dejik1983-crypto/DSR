using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FB2AccentConverter;

public sealed partial class WordReviewWindow : Window
{
    private string? _reviewPath;
    private string? _outputPath;
    private readonly List<MutableReviewEntry> _entries = new();
    private readonly List<RadioButton> _vowels = new();
    private readonly List<TextBox> _declensions = new();
    private Dictionary<string, int> _wordCounts = new(StringComparer.Ordinal);
    private int _selectedGroupAnchor = -1;
    private int _selectedSampleIndex = -1;
    private bool _rebuildingSamples;
    private bool _updatingCorrection;

    internal WordReviewWindow(string? reviewPath = null, string? outputPath = null)
    {
        InitializeComponent();
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
        PreviewKeyDown += HandleReviewNavigationKey;
        WordGroupsListBox.PreviewKeyDown += HandleReviewNavigationKey;
        ContextsListBox.PreviewKeyDown += HandleReviewNavigationKey;
        if (_reviewPath != null && File.Exists(_reviewPath)) LoadReviewFiles();
        else UpdateEditorEnabled();
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
                return new ReviewItem(first.index, $"{first.entry.Word} — {count:N0} вхождений", first.entry.Word, count);
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
        WordGroupsListBox.ItemsSource = BuildReviewItems();
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
        var dialog = new OpenFileDialog { Title = "Выберите FB2 для ручной коррекции", Filter = "Книга FictionBook (*.fb2)|*.fb2", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _outputPath = dialog.FileName;
        Fb2FileTextBox.Text = _outputPath;
        RefreshWordCounts();
        WordGroupsListBox.ItemsSource = BuildReviewItems();
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
            WordGroupsListBox.ItemsSource = BuildReviewItems();
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
        ContextTextBlock.Text = "Правка применяется ко всей выбранной группе. Снимите галочку у образца, чтобы оставить его без изменений.";
    }

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
            .Where(item => item.Correction.Length > 0 && Program.IsValidCorrection(item.Entry.Word, item.Correction))
            .ToList();
    }

    private static string CorrectionTextFor(MutableReviewEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Proposed)) return string.Empty;
        var proposed = entry.Proposed.Trim().Replace("ё\u0301", "ё", StringComparison.OrdinalIgnoreCase);
        // Только вариант с ударением или ё считается осознанной правкой: он попадает в пакет.
        return proposed.Contains('\u0301') || proposed.Contains('ё', StringComparison.OrdinalIgnoreCase) ? proposed : string.Empty;
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
            MessageBox.Show(this, "Выберите файл отчёта и редактируемый FB2.", "Нужны файлы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var sourcePath = _outputPath;
        var correctedPath = GetCorrectedOutputPath(sourcePath);
        try
        {
            ConfirmOutputPath(sourcePath, correctedPath, "Перезапись corrected-файла");
            if (!string.Equals(correctedPath, sourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(correctedPath) &&
                MessageBox.Show(this, $"Файл результата уже существует:\n{correctedPath}\n\nПересоздать его из выбранного исходного FB2?", "Пересоздание corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var pattern = new List<Program.WordReviewPatternCorrection>();
            foreach (var group in edited)
            {
                var excluded = group.Group.Where(item => !item.Included).ToArray();
                if (excluded.Length == 0) pattern.Add(new Program.WordReviewPatternCorrection(group.Entry.Word, group.Correction, null, -1));
                else pattern.AddRange(excluded.Select(item => new Program.WordReviewPatternCorrection(group.Entry.Word, group.Correction, item.Context, item.ContextOccurrence)));
            }
            var replacements = pattern.Count == 0 ? 0 : Program.ApplyReviewPatternCorrectionsToFb2(sourcePath, correctedPath, pattern);
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
                replacements += Program.ApplyWordCorrectionsToFb2(_outputPath, _outputPath, savedForms);
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
        WordGroupsListBox.ItemsSource = BuildReviewItems();
        var restored = selectedWord == null ? null : WordGroupsListBox.Items.Cast<ReviewItem>().FirstOrDefault(item => ExactWordKey(item.Word) == ExactWordKey(selectedWord));
        if (restored != null) WordGroupsListBox.SelectedItem = restored;
        else if (WordGroupsListBox.Items.Count > 0) WordGroupsListBox.SelectedIndex = 0;
        else
        {
            ContextsListBox.ItemsSource = null;
            StressChoicesPanel.Children.Clear();
            DeclensionRowsPanel.Children.Clear();
            _declensions.Clear();
            _vowels.Clear();
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
        CorrectionTextBox.Text = proposed.Contains('\u0301') || proposed.Contains('ё', StringComparison.OrdinalIgnoreCase) ? proposed : DefaultInitialStress(proposed);
        _updatingCorrection = false;
        StressChoicesPanel.Children.Clear();
        _vowels.Clear();
        DeclensionRowsPanel.Children.Clear();
        _declensions.Clear();
        RebuildVowelChoices(selectedEntry);
        LoadSavedForms(selectedEntry);
        if (_declensions.Count == 0) AddDeclensionRow(selectedEntry.Word);
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
        if (CorrectionTextBox.IsKeyboardFocusWithin || DeclensionRowsPanel.IsKeyboardFocusWithin) return;
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

    private void RebuildVowelChoices(MutableReviewEntry entry)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = "Ударная гласная: ", VerticalAlignment = VerticalAlignment.Center });
        foreach (var (vowel, position) in entry.Word.Select((ch, index) => (ch, index)).Where(item => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(item.ch)))
        {
            if (char.ToLowerInvariant(vowel) == 'ё')
            {
                row.Children.Add(new TextBlock { Text = "ё (ударная)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0), Foreground = Brushes.DarkGreen });
                continue;
            }
            var choice = new RadioButton { Content = vowel.ToString(), GroupName = "stress-vowel", Margin = new Thickness(4), Tag = position, ToolTip = "Поставить ударение на эту гласную" };
            choice.Checked += (_, _) => UpdateCorrectionFromSelectedVowel(entry.Word);
            _vowels.Add(choice);
            row.Children.Add(choice);
            if (char.ToLowerInvariant(vowel) == 'е')
            {
                var yo = new Button { Content = "ё", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 1, 5, 1), ToolTip = "Заменить эту е на ё" };
                yo.Click += (_, _) => SetCorrectionToYo(entry.Word, position);
                row.Children.Add(yo);
            }
        }
        StressChoicesPanel.Children.Add(row);
        var currentPosition = CorrectionTextBox.Text.IndexOf('\u0301') - 1;
        var current = _vowels.FirstOrDefault(choice => choice.Tag is int index && index == currentPosition);
        if (current != null)
        {
            _updatingCorrection = true;
            current.IsChecked = true;
            _updatingCorrection = false;
        }
    }

        private void UpdateCorrectionFromSelectedVowel(string word)
    {
        if (_updatingCorrection) return;
        var selected = _vowels.FirstOrDefault(choice => choice.IsChecked == true);
        if (selected?.Tag is not int index) return;
        var plainWord = word.Replace("\u0301", "");
        if (index >= plainWord.Length || !"аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(plainWord[index]) || char.ToLowerInvariant(plainWord[index]) == 'ё') return;
        CorrectionTextBox.Text = plainWord.Insert(index + 1, "\u0301");
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
        if (!Program.IsValidCorrection(entry.Word, proposed))
        {
            MessageBox.Show(this, "Не удалось подтвердить ударение: проверьте, что в поле стоит то же слово с одним знаком ударения или с «ё».", "Проверьте ударение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var item in GetSelectedGroup()) item.Proposed = proposed;
        var confirmed = _entries.Count(item => ExactWordKey(item.Word) == ExactWordKey(entry.Word));
        var editedGroups = CollectEditedGroups().Count;
        FileStatusTextBlock.Text = $"Принято: «{proposed}» (группа «{entry.Word}», образцов {confirmed}). Подтверждено групп: {editedGroups}. В конце нажмите «Применить всё и сохранить».";
        NavigateGroup(1);
    }

    private void SetCorrectionToYo(string word, int position)
    {
        var plainWord = word.Replace("\u0301", "");
        if (position < 0 || position >= plainWord.Length || char.ToLowerInvariant(plainWord[position]) != 'е') return;
        var chars = plainWord.ToCharArray();
        chars[position] = char.IsUpper(chars[position]) ? 'Ё' : 'ё';
        CorrectionTextBox.Text = new string(chars);
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
            var count = Program.ApplyWordCorrectionsToFb2(sourcePath, correctedPath, corrections);
            _outputPath = correctedPath;
            Fb2FileTextBox.Text = correctedPath;
            RefreshWordCounts();
            WordGroupsListBox.ItemsSource = BuildReviewItems();
            UpdateEditorEnabled();
            FileStatusTextBlock.Text = count == 0 ? $"Вхождений по сохранённым исправлениям не найдено. Файл не изменён: {correctedPath}" : $"Создан/обновлён файл: {correctedPath}\nСохранённые исправления применены: заменено вхождений {count}.";
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

