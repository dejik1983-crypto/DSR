using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Data;

namespace FB2AccentConverter;

internal sealed class WordReviewWindow : Window
{
    private string? _reviewPath;
    private string? _outputPath;
    private readonly TextBox _reviewFile = new() { IsReadOnly = true, MinWidth = 420 };
    private readonly TextBox _fb2File = new() { IsReadOnly = true, MinWidth = 420 };
    private readonly TextBlock _fileStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8), Foreground = Brushes.DimGray };
    private readonly Button _loadFiles = new() { Content = "Открыть отчёт и FB2", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 10) };
    private readonly Button _openCorrections = new() { Content = "Открыть файл форм слов", Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 6), IsEnabled = false };
    private readonly Button _applyAll = new() { Content = "Применить сохранённые исправления ко всему FB2", Padding = new Thickness(8, 5, 8, 5), IsEnabled = false };
    private readonly Button _previous = new() { Content = "◀ Предыдущее", Padding = new Thickness(8, 5, 8, 5), IsEnabled = false };
    private readonly Button _next = new() { Content = "Следующее ▶", Padding = new Thickness(8, 5, 8, 5), IsEnabled = false };
    private readonly ListBox _items = new() { MinWidth = 250, BorderThickness = new Thickness(1), Background = Brushes.White, SelectionMode = SelectionMode.Single, DisplayMemberPath = nameof(ReviewItem.Display) };
    private readonly ListBox _caseContextList = new() { BorderThickness = new Thickness(1), Background = Brushes.White, SelectionMode = SelectionMode.Single };
    private bool _rebuildingSamples;
    private readonly TextBox _correction = new() { MinWidth = 240, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _context = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _selectionCount = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save = new() { Content = "Применить и создать *_corrected.fb2", Padding = new Thickness(12, 7, 12, 7), IsEnabled = false };
    private readonly List<MutableReviewEntry> _entries;
    private readonly StackPanel _formPanel = new();
    private readonly StackPanel _declensionRows = new();
    private readonly List<RadioButton> _vowels = new();
    private readonly List<TextBox> _declensions = new();
    private readonly ComboBox _case = new() { Width = 260, Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBox _search = new() { MinWidth = 210, Margin = new Thickness(0, 0, 0, 6), ToolTip = "Фильтр по слову или контексту" };
    private readonly ComboBox _sortMode = new() { Width = 190, Margin = new Thickness(8, 0, 0, 6) };
    private Dictionary<string, int> _wordCounts = new(StringComparer.Ordinal);
    private int _selectedGroupAnchor = -1;
    private int _selectedSampleIndex = -1;
    private bool _updatingCorrection;

    internal WordReviewWindow(string? reviewPath = null, string? outputPath = null)
    {
        _reviewPath = reviewPath;
        _outputPath = outputPath;
        _entries = new List<MutableReviewEntry>();
        Title = "Проверка ударений и словоформ";
        Width = 1260;
        Height = 860;
        MinWidth = 1040;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ApplyPaperTheme();
        _loadFiles.Background = (Brush)Resources["Accent"];
        _loadFiles.Foreground = Brushes.White;
        _save.Background = (Brush)Resources["Accent"];
        _save.Foreground = Brushes.White;
        var layout = new DockPanel { Margin = new Thickness(16), Background = (Brush)Resources["Paper"] };
        var filePanel = new StackPanel();
        var reviewRow = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var chooseReview = new Button { Content = "Отчёт нераспознанных слов…", Padding = new Thickness(8, 5, 8, 5) };
        chooseReview.Click += (_, _) => SelectReviewFile();
        DockPanel.SetDock(chooseReview, Dock.Right);
        reviewRow.Children.Add(chooseReview);
        reviewRow.Children.Add(_reviewFile);
        filePanel.Children.Add(reviewRow);
        var fb2Row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var chooseFb2 = new Button { Content = "Редактируемый FB2…", Padding = new Thickness(8, 5, 8, 5) };
        chooseFb2.Click += (_, _) => SelectFb2File();
        DockPanel.SetDock(chooseFb2, Dock.Right);
        fb2Row.Children.Add(chooseFb2);
        fb2Row.Children.Add(_fb2File);
        filePanel.Children.Add(fb2Row);
        var loadRow = new DockPanel();
        _loadFiles.Click += (_, _) => LoadReviewFiles();
        DockPanel.SetDock(_loadFiles, Dock.Left);
        loadRow.Children.Add(_loadFiles);
        loadRow.Children.Add(_fileStatus);
        filePanel.Children.Add(loadRow);
        DockPanel.SetDock(filePanel, Dock.Top);
        layout.Children.Add(MakePaperCard(filePanel, new Thickness(14, 10, 14, 8)));

        var groupsAndEditor = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        groupsAndEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groupsAndEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        groupsAndEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var groupsColumn = new DockPanel();
        _items.MinHeight = 0;
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        searchRow.Children.Add(new TextBlock { Text = "Поиск:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        searchRow.Children.Add(_search);
        _sortMode.ItemsSource = new[] { "По алфавиту", "По частоте в тексте" };
        _sortMode.SelectedIndex = 1;
        _sortMode.SelectionChanged += (_, _) => RebuildGroupList();
        DockPanel.SetDock(_sortMode, Dock.Right);
        searchRow.Children.Add(_sortMode);
        DockPanel.SetDock(searchRow, Dock.Top);
        groupsColumn.Children.Add(searchRow);
        _search.TextChanged += (_, _) => RebuildGroupList();
        var explanation = new TextBlock
        {
            Text = "Выберите слово в верхнем списке, затем конкретный контекст ниже. Снимите флажок у контекста, чтобы исключить его из общей правки.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(explanation, Dock.Top);
        groupsColumn.Children.Add(explanation);
        var groupsActions = new StackPanel();
        groupsActions.Children.Add(_openCorrections);
        groupsActions.Children.Add(_applyAll);
        var navigation = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        _previous.Margin = new Thickness(0, 0, 6, 0);
        navigation.Children.Add(_previous);
        DockPanel.SetDock(_next, Dock.Right);
        navigation.Children.Add(_next);
        groupsActions.Children.Add(navigation);
        DockPanel.SetDock(groupsActions, Dock.Bottom);
        groupsColumn.Children.Add(groupsActions);
        var lists = new Grid();
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var groupListPanel = new DockPanel();
        var groupListLabel = new TextBlock { Text = "Группы слов", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(groupListLabel, Dock.Top);
        groupListPanel.Children.Add(groupListLabel);
        groupListPanel.Children.Add(_items);
        Grid.SetRow(groupListPanel, 0);
        lists.Children.Add(groupListPanel);
        var sampleListPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var sampleListLabel = new TextBlock { Text = "Контексты выбранной группы", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(sampleListLabel, Dock.Top);
        sampleListPanel.Children.Add(sampleListLabel);
        sampleListPanel.Children.Add(_caseContextList);
        Grid.SetRow(sampleListPanel, 1);
        lists.Children.Add(sampleListPanel);
        groupsColumn.Children.Add(lists);
        Grid.SetColumn(groupsColumn, 0);
        groupsAndEditor.Children.Add(groupsColumn);

        var splitter = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ShowsPreview = false };
        Grid.SetColumn(splitter, 1);
        groupsAndEditor.Children.Add(splitter);

        var editor = BuildEditorPanel();
        Grid.SetColumn(editor, 2);
        groupsAndEditor.Children.Add(editor);
        DockPanel.SetDock(groupsAndEditor, Dock.Bottom);
        layout.Children.Add(MakePaperCard(groupsAndEditor, new Thickness(14)));

        _items.DisplayMemberPath = nameof(ReviewItem.Display);
        _caseContextList.ItemTemplate = BuildSampleTemplate();
        _items.SelectionChanged += (_, _) =>
        {
            if (_items.SelectedItem is ReviewItem selected) SelectGroup(selected.Index);
            UpdateNavigationState();
        };
        _items.ItemsSource = new List<ReviewItem>();
        _caseContextList.SelectionChanged += (_, _) =>
        {
            if (!_rebuildingSamples && _caseContextList.SelectedItem is SampleItem sample) SelectSample(sample.Index);
        };
        _openCorrections.Click += (_, _) => OpenDeclensionFile();
        _applyAll.Click += (_, _) => ApplyAllCorrections();
        _previous.Click += (_, _) => NavigateGroup(-1);
        _next.Click += (_, _) => NavigateGroup(1);
        _applyAll.Content = "Применить сохранённые исправления → *_corrected.fb2";
        PreviewKeyDown += HandleReviewNavigationKey;
        _items.PreviewKeyDown += HandleReviewNavigationKey;
        _caseContextList.PreviewKeyDown += HandleReviewNavigationKey;
        _correction.TextChanged += CorrectionChanged;
        AddTextEditingContextMenu(_correction);
        AddTextEditingContextMenu(_reviewFile);
        AddTextEditingContextMenu(_fb2File);
        _caseContextList.Loaded += (_, _) => AttachContextMenusToSamples();
        _caseContextList.ItemContainerGenerator.StatusChanged += (_, _) => AttachContextMenusToSamples();
        Content = layout;

        if (!string.IsNullOrWhiteSpace(_reviewPath)) _reviewFile.Text = _reviewPath;
        if (!string.IsNullOrWhiteSpace(_outputPath)) _fb2File.Text = _outputPath;
        UpdateEditorEnabled();
        if (_reviewPath != null && File.Exists(_reviewPath)) LoadReviewFiles();
    }

    private void ApplyPaperTheme()
    {
        var paper = new SolidColorBrush(Color.FromRgb(244, 237, 221));
        var card = new SolidColorBrush(Color.FromRgb(252, 248, 239));
        var ink = new SolidColorBrush(Color.FromRgb(63, 48, 37));
        var mutedInk = new SolidColorBrush(Color.FromRgb(112, 96, 77));
        var line = new SolidColorBrush(Color.FromRgb(210, 194, 165));
        var accent = new SolidColorBrush(Color.FromRgb(116, 70, 42));
        var accentHover = new SolidColorBrush(Color.FromRgb(139, 87, 52));
        var selected = new SolidColorBrush(Color.FromRgb(232, 216, 186));
        Background = paper;
        Foreground = ink;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Resources["Paper"] = paper;
        Resources["Card"] = card;
        Resources["Ink"] = ink;
        Resources["MutedInk"] = mutedInk;
        Resources["Rule"] = line;
        Resources["Accent"] = accent;
        Resources.Add(typeof(TextBlock), new Style(typeof(TextBlock))
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, ink), new Setter(TextBlock.FontSizeProperty, 13.0) }
        });
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, card));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, line));
        buttonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        buttonStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 2, 6, 2)));
        buttonStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        buttonStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        var buttonTemplate = new ControlTemplate(typeof(Button));
        var buttonBorder = new FrameworkElementFactory(typeof(Border));
        buttonBorder.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        buttonBorder.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        buttonBorder.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var buttonContent = new FrameworkElementFactory(typeof(ContentPresenter));
        buttonContent.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        buttonContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        buttonContent.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        buttonBorder.AppendChild(buttonContent);
        buttonTemplate.VisualTree = buttonBorder;
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate));
        var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, accentHover));
        buttonStyle.Triggers.Add(hoverTrigger);
        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.52));
        disabledTrigger.Setters.Add(new Setter(Control.CursorProperty, Cursors.Arrow));
        buttonStyle.Triggers.Add(disabledTrigger);
        Resources.Add(typeof(Button), buttonStyle);
        var textBoxStyle = new Style(typeof(TextBox));
        textBoxStyle.Setters.Add(new Setter(Control.BackgroundProperty, card));
        textBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        textBoxStyle.Setters.Add(new Setter(Control.BorderBrushProperty, line));
        textBoxStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        textBoxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        textBoxStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        textBoxStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Resources.Add(typeof(TextBox), textBoxStyle);
        var listStyle = new Style(typeof(ListBox));
        listStyle.Setters.Add(new Setter(Control.BackgroundProperty, card));
        listStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        listStyle.Setters.Add(new Setter(Control.BorderBrushProperty, line));
        listStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        listStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        Resources.Add(typeof(ListBox), listStyle);
        var comboStyle = new Style(typeof(ComboBox));
        comboStyle.Setters.Add(new Setter(Control.BackgroundProperty, card));
        comboStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        comboStyle.Setters.Add(new Setter(Control.BorderBrushProperty, line));
        comboStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        comboStyle.Setters.Add(new Setter(Control.MinHeightProperty, 32.0));
        Resources.Add(typeof(ComboBox), comboStyle);
        var radioStyle = new Style(typeof(RadioButton));
        radioStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        radioStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        Resources.Add(typeof(RadioButton), radioStyle);
        var checkStyle = new Style(typeof(CheckBox));
        checkStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        checkStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 5, 0, 5)));
        Resources.Add(typeof(CheckBox), checkStyle);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 7, 8, 7)));
        itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(1, 1, 1, 2)));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        var itemTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        itemTrigger.Setters.Add(new Setter(Control.BackgroundProperty, selected));
        itemTrigger.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        itemStyle.Triggers.Add(itemTrigger);
        Resources.Add(typeof(ListBoxItem), itemStyle);
    }

    private Border MakePaperCard(UIElement content, Thickness? padding = null) =>
        new()
        {
            Background = (Brush)Resources["Card"],
            BorderBrush = (Brush)Resources["Rule"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = padding ?? new Thickness(14),
            Child = content
        };

    private void RefreshWordCounts()
    {
        _wordCounts = !string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath)
            ? Program.CountReviewWordOccurrences(_outputPath, _entries.Select(entry => entry.Word))
            : new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private List<ReviewItem> BuildReviewItems()
    {
        var filter = _search.Text.Trim();
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
        return (_sortMode.SelectedIndex == 0
            ? groups.OrderBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)
            : groups.OrderByDescending(item => _wordCounts.TryGetValue(item.Word, out var count) ? count : 0).ThenBy(item => item.Word, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    private void ApplyGroupFilter() => RebuildGroupList();

    private void RebuildGroupList()
    {
        var selectedWord = (_items.SelectedItem as ReviewItem)?.Word;
        _items.ItemsSource = BuildReviewItems();
        if (selectedWord != null)
        {
            var found = _items.Items.Cast<ReviewItem>().FirstOrDefault(item => ExactWordKey(item.Word) == ExactWordKey(selectedWord));
            if (found != null) _items.SelectedItem = found;
        }
        if (_items.SelectedItem == null && _items.Items.Count > 0) _items.SelectedIndex = 0;
        UpdateNavigationState();
    }

    private void SelectReviewFile()
    {
        var dialog = new OpenFileDialog { Title = "Выберите файл нераспознанных слов", Filter = "Отчёт слов (*.txt)|*.txt|Все файлы (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            _reviewPath = dialog.FileName;
            _reviewFile.Text = _reviewPath;
            UpdateEditorEnabled();
            if (File.Exists(_reviewPath)) LoadReviewFiles();
        }
    }

    private void SelectFb2File()
    {
        var dialog = new OpenFileDialog { Title = "Выберите FB2 для ручной коррекции", Filter = "Книга FictionBook (*.fb2)|*.fb2", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            _outputPath = dialog.FileName;
            _fb2File.Text = _outputPath;
            RefreshWordCounts();
            _items.ItemsSource = BuildReviewItems();
            UpdateEditorEnabled();
        }
    }

    private void UpdateEditorEnabled()
    {
        _loadFiles.IsEnabled = !string.IsNullOrWhiteSpace(_reviewPath) && File.Exists(_reviewPath);
        _openCorrections.IsEnabled = _loadFiles.IsEnabled && File.Exists(_reviewPath == null ? string.Empty : Program.FormCorrectionsPathFor(_reviewPath));
        _applyAll.IsEnabled = _loadFiles.IsEnabled && !string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath);
        _save.IsEnabled = _loadFiles.IsEnabled && !string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath) && _items.SelectedItem != null;
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
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть файл словоформ: " + ex.Message, "Ошибка открытия", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            _items.ItemsSource = BuildReviewItems();
            _fileStatus.Text = _entries.Count == 0 ? "Отчёт пуст или не содержит слов для проверки." : $"Загружено случаев: {_entries.Count}; точных групп слов: {_items.Items.Count}. Сортировка: {_sortMode.SelectedItem}.";
            _items.Focusable = true;
            _items.IsTabStop = true;
            _save.IsEnabled = false;
            _context.Text = "Выберите группу слова слева: будет отредактирована вся группа.";
            if (_items.Items.Count > 0) _items.SelectedIndex = 0;
            UpdateEditorEnabled();
            UpdateNavigationState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть отчёт: " + ex.Message, "Ошибка чтения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private UIElement BuildEditorPanel()
    {
        _case.ItemsSource = new[] { "Именительный", "Родительный", "Дательный", "Винительный", "Творительный", "Предложный", "Другая форма / контекст" };
        _case.SelectedIndex = 0;
        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 0, 0) };
        var actionRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var addForm = new Button { Content = "Добавить словоформу", Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 8, 0) };
        addForm.Click += (_, _) => AddDeclensionRow();
        actionRow.Children.Add(addForm);
        _save.Content = "Применить к группе и сохранить";
        _save.Click += (_, _) => SaveSelectedCorrection();
        actionRow.Children.Add(_save);
        DockPanel.SetDock(actionRow, Dock.Bottom);
        panel.Children.Add(actionRow);

        var correctionRow = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        correctionRow.Children.Add(new TextBlock { Text = "Исправленный вариант:", VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(_correction, Dock.Right);
        _correction.Margin = new Thickness(8, 0, 0, 0);
        correctionRow.Children.Add(_correction);
        panel.Children.Add(correctionRow);

        var editorBody = new StackPanel();
        editorBody.Children.Add(new TextBlock { Text = "ТЕКУЩИЙ ФРАГМЕНТ", FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6), Foreground = (Brush)Resources["MutedInk"] });
        editorBody.Children.Add(_context);
        editorBody.Children.Add(new TextBlock { Text = "Правка применяется к точным совпадениям слова; отдельные предложения можно исключить флажками в списке контекстов.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Resources["MutedInk"], Margin = new Thickness(0, 2, 0, 6) });
        editorBody.Children.Add(_selectionCount);
        editorBody.Children.Add(new TextBlock { Text = "УДАРЕНИЕ В СЛОВЕ", FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 4), Foreground = (Brush)Resources["MutedInk"] });
        editorBody.Children.Add(new TextBlock { Text = "Выберите ударную гласную или нажмите «ё» рядом с буквой е. Ё считается ударной автоматически.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Resources["MutedInk"], Margin = new Thickness(0, 0, 0, 4) });
        editorBody.Children.Add(_formPanel);
        editorBody.Children.Add(new TextBlock { Text = "ДОПОЛНИТЕЛЬНЫЕ СЛОВОФОРМЫ", FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 4), Foreground = (Brush)Resources["MutedInk"] });
        editorBody.Children.Add(new TextBlock { Text = "Можно ввести форму вручную, вставить её или выбрать ударение/ё под полем.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Resources["MutedInk"], Margin = new Thickness(0, 0, 0, 4) });
        editorBody.Children.Add(_case);
        editorBody.Children.Add(_declensionRows);
        panel.Children.Add(new ScrollViewer { Content = editorBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return panel;
    }



    private void CorrectionChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingCorrection || _items.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return;
        var corrected = _correction.Text;
        foreach (var entry in GetSelectedGroup()) entry.Proposed = corrected;
        _context.Text = "Правка применяется ко всей выбранной группе. Снимите галочку у образца, чтобы оставить его без изменений.";
    }

    private IEnumerable<MutableReviewEntry> GetSelectedGroup()
    {
        if (_items.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count) return Enumerable.Empty<MutableReviewEntry>();
        return _entries.Where(entry => ExactWordKey(entry.Word) == ExactWordKey(_entries[selected.Index].Word));
    }

    private void ApplyGroupCorrection()
    {
        if (_items.SelectedItem is not ReviewItem selected || selected.Index >= _entries.Count || string.IsNullOrWhiteSpace(_reviewPath) || string.IsNullOrWhiteSpace(_outputPath) || !File.Exists(_outputPath)) return;
        var entry = _entries[selected.Index];
        var correction = _correction.Text.Trim().Replace("ё\u0301", "ё", StringComparison.OrdinalIgnoreCase);
        if (!Program.IsValidCorrection(entry.Word, correction))
        {
            MessageBox.Show(this, "Введите то же слово с одним знаком ударения либо выберите вариант с «ё».", "Проверьте исправление", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var group = GetSelectedGroup().ToArray();
        var sourceForThisApply = _outputPath;
        if (string.IsNullOrWhiteSpace(sourceForThisApply) || !File.Exists(sourceForThisApply))
        {
            MessageBox.Show(this, "Выберите существующий исходный FB2-файл перед применением исправления.", "Не выбран FB2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var excludedSamples = group.Where(item => !item.Included).ToArray();
        var pattern = excludedSamples.Length == 0
            ? new List<Program.WordReviewPatternCorrection> { new(entry.Word, correction, null, -1) }
            : excludedSamples.Select(item => new Program.WordReviewPatternCorrection(entry.Word, correction, item.Context, item.ContextOccurrence)).ToList();
        try
        {
            var correctedPath = GetCorrectedOutputPath(sourceForThisApply);
            if (string.Equals(correctedPath, sourceForThisApply, StringComparison.OrdinalIgnoreCase))
            {
                var overwrite = MessageBox.Show(this, $"Исходный файл уже имеет суффикс _corrected и будет перезаписан:\n{sourceForThisApply}\n\nПродолжить?", "Перезапись corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (overwrite != MessageBoxResult.Yes) return;
            }
            else if (File.Exists(correctedPath))
            {
                var overwrite = MessageBox.Show(this, $"Файл результата уже существует:\n{correctedPath}\n\nПересоздать его из выбранного исходного FB2?", "Пересоздание corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            var correctedForms = new List<Program.DeclensionCorrection>();
            for (var i = 0; i < _declensions.Count; i++)
            {
                var form = _declensions[i].Text.Trim().Replace("ё\u0301", "ё", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(form))
                {
                    MessageBox.Show(this, "Заполните словоформу или удалите пустую строку кнопкой «Убрать».", "Не заполнена словоформа", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!Program.IsValidDeclensionForm(form))
                {
                    MessageBox.Show(this, $"Проверьте форму «{form}»: введите тот же вариант с одним ударением или ё.", "Неверная форма", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var row = (DockPanel)_declensionRows.Children[i];
                var caseName = ((ComboBox)row.Tag).SelectedItem?.ToString() ?? "Словоформа";
                correctedForms.Add(new Program.DeclensionCorrection(entry.Word, form, caseName, entry.Context));
            }
            correctedForms.AddRange(group.Where(item => item.Included).Select(item => new Program.DeclensionCorrection(item.Word, correction, "Исходное слово", item.Context)));
            var replacements = Program.ApplyReviewPatternCorrectionsToFb2(sourceForThisApply, correctedPath, pattern);
            if (replacements == 0)
            {
                _fileStatus.Text = $"Результат не перезаписан: во фрагменте исходного FB2 не найдено «{entry.Word}» в выбранном контексте. Исправление не удалено из списка.";
                return;
            }
            _outputPath = correctedPath;
            _fb2File.Text = correctedPath;
            _wordCounts = Program.CountReviewWordOccurrences(_outputPath, _entries.Select(item => item.Word));
            var selectedWord = entry.Word;
            _items.ItemsSource = BuildReviewItems();
            var selectedGroup = _items.Items.Cast<ReviewItem>().FirstOrDefault(item => ExactWordKey(item.Word) == ExactWordKey(selectedWord));
            if (selectedGroup != null) _items.SelectedItem = selectedGroup;
            Program.SaveUserCorrection(entry.ToEntry(), correction);
            Program.SaveDeclensionForms(_reviewPath!, entry.ToEntry(), correctedForms);
            if (group.Any(item => !item.Included)) SaveRemainingReviewEntries(entry.Word);
            UpdateEditorEnabled();
            _fileStatus.Text = $"Файл создан/обновлён: {correctedPath}\nГруппа «{entry.Word}»: исправлено вхождений {replacements}; исключено образцов: {excludedSamples.Length}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось применить исправление: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveRemainingReviewEntries(string correctedWord)
    {
        if (_reviewPath == null) return;
        var correctedKey = ExactWordKey(correctedWord);
        var remaining = _entries.Where(entry => ExactWordKey(entry.Word) != correctedKey || !entry.Included).Select(entry => entry.ToEntry());
        File.WriteAllText(_reviewPath, Program.FormatWordReview(remaining), new UTF8Encoding(true));
    }

    private void CopyGroupText(IEnumerable<MutableReviewEntry> entries)
    {
        var list = entries.ToArray();
        var text = string.Join(Environment.NewLine, list.Select(entry => $"{entry.Word} — {entry.Context}"));
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            _fileStatus.Text = list.Length == 1 ? "Слово и контекст скопированы." : "Слова и контексты группы скопированы.";
        }
        catch (Exception ex)
        {
            _fileStatus.Text = "Не удалось скопировать текст: " + ex.Message;
        }
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
        if (_caseContextList.Items.Count == 0) return;
        foreach (var sample in _caseContextList.Items.Cast<SampleItem>())
        {
            if (_caseContextList.ItemContainerGenerator.ContainerFromItem(sample) is ListBoxItem container)
                AddGoogleContextMenu(container, _entries[sample.Index].Word, sample.Context);
        }
    }

    private static void AddTextEditingContextMenu(TextBox textBox)
    {
        var menu = new ContextMenu();
        foreach (var (label, command) in new[]
        {
            ("Отменить", ApplicationCommands.Undo),
            ("Вырезать", ApplicationCommands.Cut),
            ("Копировать", ApplicationCommands.Copy),
            ("Вставить", ApplicationCommands.Paste),
            ("Выделить всё", ApplicationCommands.SelectAll)
        })
            menu.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = textBox });
        textBox.ContextMenu = menu;
    }

    private DataTemplate BuildSampleTemplate()
    {
        var template = new DataTemplate(typeof(SampleItem));
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var toggle = new FrameworkElementFactory(typeof(CheckBox));
        toggle.SetValue(DockPanel.DockProperty, Dock.Left);
        toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 8, 2));
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(SampleItem.Included)) { Mode = System.Windows.Data.BindingMode.OneWay });
        toggle.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: SampleItem sample }) SetSampleIncluded(sample.Index, !sample.Included);
        }));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SampleItem.Context)));
        row.AppendChild(toggle);
        row.AppendChild(text);
        template.VisualTree = row;
        return template;
    }

    private static void OpenGoogleStressSearch(string word, string context)
    {
        var highlighted = HighlightQueryWord(context, word);
        var query = $"куда ставить ударение в предложении {highlighted}";
        var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string HighlightQueryWord(string context, string word)
    {
        // Google does not support rich-text formatting in the search box; mark the target
        // clearly while retaining the original spelling so the query remains searchable.
        var index = context.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return $"{context} Проверяемое слово: **{word}**";
        return context[..index] + "**" + context.Substring(index, word.Length) + "**" + context[(index + word.Length)..];
    }

    private void SelectGroup(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count) return;
        var wordKey = ExactWordKey(_entries[entryIndex].Word);
        var group = _entries.Select((entry, index) => (entry, index)).Where(pair => ExactWordKey(pair.entry.Word) == wordKey).ToArray();
        if (group.Length == 0) return;
        _selectedGroupAnchor = group[0].index;
        if (!group.Any(pair => pair.index == _selectedSampleIndex))
            _selectedSampleIndex = group.FirstOrDefault(pair => pair.entry.Included).index;
        var samples = group.Select(pair => new SampleItem(pair.index, pair.entry.Context, pair.entry.Included)).ToList();
        _rebuildingSamples = true;
        _caseContextList.ItemsSource = samples;
        _caseContextList.SelectedItem = samples.FirstOrDefault(sample => sample.Index == _selectedSampleIndex);
        _rebuildingSamples = false;
        AttachContextMenusToSamples();
        var selectedEntry = group[0].entry;
        var count = _wordCounts.TryGetValue(selectedEntry.Word, out var occurrences) ? occurrences : 0;
        _context.Text = $"Выбрана группа «{selectedEntry.Word}»: совпадений в тексте {count:N0}; контекстов {group.Length}.";
        _selectionCount.Text = $"Включено образцов: {group.Count(pair => pair.entry.Included)} из {group.Length}";
        _updatingCorrection = true;
        var proposed = group.Select(pair => pair.entry.Proposed).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? selectedEntry.Word;
        if (proposed.Contains('\u0301') || proposed.Contains('ё', StringComparison.OrdinalIgnoreCase))
            _correction.Text = proposed;
        else
            _correction.Text = DefaultInitialStress(proposed);
        _updatingCorrection = false;
        _formPanel.Children.Clear();
        _declensionRows.Children.Clear();
        _declensions.Clear();
        RebuildVowelChoices(selectedEntry);
        LoadSavedForms(selectedEntry);
        if (_declensions.Count == 0) AddDeclensionRow(selectedEntry.Word);
        UpdateNavigationState();
        UpdateEditorEnabled();
    }

    private void NavigateGroup(int direction)
    {
        if (_items.Items.Count == 0) return;
        var next = Math.Clamp(_items.SelectedIndex + direction, 0, _items.Items.Count - 1);
        _items.SelectedIndex = next;
        _items.ScrollIntoView(_items.SelectedItem);
    }

    private void HandleReviewNavigationKey(object sender, KeyEventArgs e)
    {
        if (_correction.IsKeyboardFocusWithin || _declensionRows.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Left) { NavigateGroup(-1); e.Handled = true; return; }
        if (e.Key == Key.Right) { NavigateGroup(1); e.Handled = true; return; }
        if (e.Key is not (Key.Up or Key.Down) || !_caseContextList.IsKeyboardFocusWithin || _caseContextList.Items.Count == 0) return;
        _caseContextList.SelectedIndex = Math.Clamp(_caseContextList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _caseContextList.Items.Count - 1);
        e.Handled = true;
    }

    private void SelectSample(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count) return;
        _selectedSampleIndex = entryIndex;
        var entry = _entries[entryIndex];
        _context.Text = $"Выбрано предложение: {entry.Word} — {entry.Context}";
        var group = _entries.Where(item => ExactWordKey(item.Word) == ExactWordKey(entry.Word)).ToArray();
        _selectionCount.Text = $"Включено образцов: {group.Count(item => item.Included)} из {group.Length}";
        var selected = _caseContextList.Items.Cast<SampleItem>().FirstOrDefault(sample => sample.Index == entryIndex);
        if (selected != null && !ReferenceEquals(_caseContextList.SelectedItem, selected)) _caseContextList.SelectedItem = selected;
    }

    private void SetSampleIncluded(int index, bool included)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries[index].Included = included;
        var group = _entries.Where(entry => ExactWordKey(entry.Word) == ExactWordKey(_entries[index].Word)).ToArray();
        _selectionCount.Text = $"Включено образцов: {group.Count(entry => entry.Included)} из {group.Length}";
        if (!included && _selectedSampleIndex == index)
        {
            var replacementIndex = _entries.Select((entry, entryIndex) => (entry, entryIndex))
                .Where(pair => ExactWordKey(pair.entry.Word) == ExactWordKey(_entries[index].Word) && pair.entry.Included)
                .Select(pair => pair.entryIndex).DefaultIfEmpty(index).First();
            _selectedSampleIndex = replacementIndex;
        }
        var samples = _caseContextList.Items.Cast<SampleItem>()
            .Select(item => item.Index == index ? item with { Included = included } : item).ToList();
        _rebuildingSamples = true;
        _caseContextList.ItemsSource = samples;
        _caseContextList.SelectedItem = samples.FirstOrDefault(item => item.Index == _selectedSampleIndex);
        _rebuildingSamples = false;
        AttachContextMenusToSamples();
        SelectSample(_selectedSampleIndex >= 0 ? _selectedSampleIndex : index);
    }

    private void UpdateNavigationState()
    {
        _previous.IsEnabled = _items.SelectedIndex > 0;
        _next.IsEnabled = _items.SelectedIndex >= 0 && _items.SelectedIndex < _items.Items.Count - 1;
    }

    private static string DefaultInitialStress(string word)
    {
        var hasStress = word.Contains('\u0301');
        var hasYo = word.Contains('ё', StringComparison.OrdinalIgnoreCase);
        if (hasStress || hasYo) return word;
        var vowels = word.Select((ch, index) => (ch, index)).Where(item => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(item.ch)).ToArray();
        if (vowels.Length < 2 || vowels[0].index > 1) return word;
        return word.Insert(vowels[0].index + 1, "\u0301");
    }

    private void RebuildVowelChoices(MutableReviewEntry entry)
    {
        _formPanel.Children.Clear();
        _vowels.Clear();
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = "Ударная гласная: ", VerticalAlignment = VerticalAlignment.Center });
        var vowels = entry.Word.Select((ch, index) => (ch, index)).Where(item => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(item.ch)).ToArray();
        for (var i = 0; i < vowels.Length; i++)
        {
            var position = vowels[i].index;
            var vowel = vowels[i].ch;
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
                var yo = new Button { Content = "ё", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 1, 5, 1), ToolTip = "Заменить эту е на ё (без отдельного знака ударения)" };
                yo.Click += (_, _) => SetCorrectionToYo(entry.Word, position);
                row.Children.Add(yo);
            }
        }
        var currentPosition = DefaultInitialStress(entry.Proposed).IndexOf('\u0301') - 1;
        var current = _vowels.FirstOrDefault(choice => choice.Tag is int index && index == currentPosition);
        if (current != null) current.IsChecked = true;
        else if (_vowels.Count > 0) _vowels[0].IsChecked = true;
        _formPanel.Children.Add(row);
    }

    private void UpdateCorrectionFromSelectedVowel(string word)
    {
        var selected = _vowels.FirstOrDefault(choice => choice.IsChecked == true);
        if (selected?.Tag is not int index) return;
        var plainWord = word.Replace("\u0301", "");
        if (index >= plainWord.Length || !"аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(plainWord[index])) return;
        if (char.ToLowerInvariant(plainWord[index]) == 'ё') return;
        var corrected = plainWord.Insert(index + 1, "\u0301");
        _correction.TextChanged -= CorrectionChanged;
        _correction.Text = corrected;
        _correction.TextChanged += CorrectionChanged;
        foreach (var entry in GetSelectedGroup()) entry.Proposed = corrected;
    }

    private void SetCorrectionToYo(string word, int position)
    {
        var plainWord = word.Replace("\u0301", "");
        if (position < 0 || position >= plainWord.Length || char.ToLowerInvariant(plainWord[position]) != 'е') return;
        var chars = plainWord.ToCharArray();
        chars[position] = char.IsUpper(chars[position]) ? 'Ё' : 'ё';
        _correction.TextChanged -= CorrectionChanged;
        _correction.Text = new string(chars);
        _correction.TextChanged += CorrectionChanged;
        foreach (var entry in GetSelectedGroup()) entry.Proposed = _correction.Text;
    }

    private void LoadSavedForms(MutableReviewEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_reviewPath)) return;
        var saved = Program.ReadDeclensionForms(_reviewPath, entry.Word);
        foreach (var form in saved.Where(form => !form.Case.Equals("Исходное слово", StringComparison.Ordinal))) AddDeclensionRow(form.Form, form.Case);
    }

    private void AddDeclensionRow(string? initialForm = null, string? grammaticalCase = null)
    {
        var formBox = new TextBox { Text = initialForm ?? "", MinWidth = 230 };
        AddTextEditingContextMenu(formBox);
        var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var remove = new Button { Content = "Убрать", Padding = new Thickness(5, 2, 5, 2), ToolTip = "Удалить эту форму из списка сохраняемых словоформ" };
        remove.Click += (_, _) =>
        {
            var index = _declensions.IndexOf(formBox);
            if (index < 0) return;
            _declensions.RemoveAt(index);
            _declensionRows.Children.Remove(panel);
        };
        DockPanel.SetDock(remove, Dock.Right);
        panel.Children.Add(remove);
        var caseBox = new ComboBox { Width = 170, ItemsSource = _case.ItemsSource, SelectedItem = grammaticalCase ?? _case.SelectedItem, Margin = new Thickness(0, 0, 6, 0) };
        var stressPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void RebuildFormVowels()
        {
            stressPanel.Children.Clear();
            var radios = new List<RadioButton>();
            var form = formBox.Text;
            var plainForm = form.Replace("\u0301", "");
            var vowels = plainForm.Select((ch, index) => (ch, index)).Where(item => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".Contains(item.ch)).ToArray();
            for (var i = 0; i < vowels.Length; i++)
            {
                var position = vowels[i].index;
                var vowel = vowels[i].ch;
                if (char.ToLowerInvariant(vowel) == 'ё')
                {
                    stressPanel.Children.Add(new TextBlock { Text = "ё (ударная)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), Foreground = Brushes.DarkGreen });
                    continue;
                }
                var radio = new RadioButton { Content = vowel.ToString(), GroupName = "form-" + _declensions.Count, Tag = position, Margin = new Thickness(3), ToolTip = "Поставить ударение на эту гласную" };
                radio.Checked += (_, _) =>
                {
                    if (radio.Tag is int vowelIndex && char.ToLowerInvariant(plainForm[vowelIndex]) != 'ё')
                        formBox.Text = plainForm.Insert(vowelIndex + 1, "\u0301");
                };
                radios.Add(radio);
                stressPanel.Children.Add(radio);
                if (char.ToLowerInvariant(vowel) == 'е')
                {
                    var yo = new Button { Content = "ё", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 1, 4, 1), ToolTip = "Заменить эту е на ё без отдельного ударения" };
                    yo.Click += (_, _) =>
                    {
                        var chars = plainForm.ToCharArray();
                        chars[position] = char.IsUpper(chars[position]) ? 'Ё' : 'ё';
                        formBox.Text = new string(chars);
                    };
                    stressPanel.Children.Add(yo);
                }
            }
            var stressedIndex = form.IndexOf('\u0301') - 1;
            var selected = radios.FirstOrDefault(radio => radio.Tag is int vowelIndex && vowelIndex == stressedIndex);
            if (selected != null) selected.IsChecked = true;
        }
        formBox.TextChanged += (_, _) => RebuildFormVowels();
        if (!string.IsNullOrWhiteSpace(formBox.Text)) RebuildFormVowels();
        panel.Children.Add(caseBox);
        panel.Children.Add(formBox);
        panel.Children.Add(stressPanel);
        _declensionRows.Children.Add(panel);
        _declensions.Add(formBox);
        panel.Tag = caseBox;
    }

    private void ApplyAllCorrections()
    {
        if (string.IsNullOrWhiteSpace(_reviewPath) || string.IsNullOrWhiteSpace(_outputPath) || !File.Exists(_outputPath)) return;
        var correctionsPath = Program.FormCorrectionsPathFor(_reviewPath);
        List<Program.DeclensionCorrection> saved;
        try
        {
            saved = File.Exists(correctionsPath)
                ? JsonSerializer.Deserialize<List<Program.DeclensionCorrection>>(File.ReadAllText(correctionsPath)) ?? new List<Program.DeclensionCorrection>()
                : new List<Program.DeclensionCorrection>();
        }
        catch (JsonException ex)
        {
            MessageBox.Show(this, "Не удалось прочитать файл форм слов: " + ex.Message, "Ошибка чтения", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (saved.Count == 0)
        {
            MessageBox.Show(this, "В файле пока нет сохранённых исправлений.", "Нет исправлений", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var correctedPath = GetCorrectedOutputPath(_outputPath);
        if (string.Equals(correctedPath, _outputPath, StringComparison.OrdinalIgnoreCase))
        {
            if (MessageBox.Show(this, $"Файл результата будет перезаписан:\n{correctedPath}\n\nПродолжить?", "Перезапись corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        else if (File.Exists(correctedPath))
        {
            if (MessageBox.Show(this, $"Файл уже существует:\n{correctedPath}\n\nПересоздать его из выбранного FB2?", "Пересоздание corrected-файла", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        var corrections = saved
            .GroupBy(item => Program.NormalizeWordLettersForReview(item.Form), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        try
        {
            if (!string.Equals(correctedPath, _outputPath, StringComparison.OrdinalIgnoreCase)) File.Copy(_outputPath, correctedPath, overwrite: true);
            var count = Program.ApplyWordCorrectionsToFb2(_outputPath, correctedPath, corrections);
            _outputPath = correctedPath;
            _fb2File.Text = correctedPath;
            RefreshWordCounts();
            _items.ItemsSource = BuildReviewItems();
            UpdateEditorEnabled();
            _fileStatus.Text = count == 0
                ? $"Вхождений по сохранённым исправлениям не найдено. Файл не изменён: {correctedPath}"
                : $"Создан/обновлён файл: {correctedPath}\nСохранённые исправления применены: заменено вхождений {count}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось применить исправления: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GetCorrectedOutputPath(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var directory = Path.GetDirectoryName(fullSourcePath)!;
        var sourceBase = Path.GetFileNameWithoutExtension(fullSourcePath);
        if (sourceBase.EndsWith("_corrected", StringComparison.OrdinalIgnoreCase)) return fullSourcePath;
        return Path.Combine(directory, sourceBase + "_corrected" + Path.GetExtension(sourcePath));
    }

    private void SaveSelectedCorrection() => ApplyGroupCorrection();

    private static string ExactWordKey(string word) => word.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private static bool SameWord(string left, string right) =>
        string.Equals(Program.NormalizeWordLettersForReview(left), Program.NormalizeWordLettersForReview(right), StringComparison.OrdinalIgnoreCase);

    private sealed class MutableReviewEntry
    {
        private readonly (string Context, int Occurrence) _splitContext;
        public MutableReviewEntry(Program.WordReviewEntry entry)
        {
            Word = entry.Word;
            Proposed = entry.Proposed;
            _splitContext = Program.SplitReviewContext(entry.Context);
        }
        public string Word { get; }
        public string Context => _splitContext.Context;
        public int ContextOccurrence => _splitContext.Occurrence;
        public string Proposed { get; set; }
        public bool Included { get; set; } = true;
        public Program.WordReviewEntry ToEntry() => new(Word, $"{Context}\u0001{ContextOccurrence}", Proposed);
    }

    private sealed record ReviewItem(int Index, string Display, string Word, int Count);
    private sealed record SampleItem(int Index, string Context, bool Included)
    {
        public string Display => $"{(Included ? "☑" : "☐")}  {Context}";
    }
}
