using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FB2AccentConverter;

internal sealed class MainWindow : Window
{
    private readonly TextBox _input = new() { IsReadOnly = true, MinWidth = 390 };
    private readonly TextBox _output = new() { MinWidth = 390 };
    private readonly ListBox _chapters = new() { SelectionMode = SelectionMode.Extended, MinHeight = 150 };
    private readonly StackPanel _chapterItems = new();
    private readonly List<CheckBox> _chapterChecks = new();
    private readonly RadioButton _wholeBook = new() { Content = "Вся книга", IsChecked = true, GroupName = "scope" };
    private readonly RadioButton _selectedChapters = new() { Content = "Только выбранные главы", GroupName = "scope" };
    private readonly CheckBox _illustrations = new() { Content = "Сохранить иллюстрации", IsChecked = true, IsEnabled = false };
    private readonly ComboBox _model = new() { Width = 170, SelectedIndex = 0 };
    private readonly ComboBox _device = new() { Width = 120, SelectedIndex = 0 };
    private readonly Button _convert = new() { Content = "Начать конвертацию", Padding = new Thickness(18, 9, 18, 9), FontWeight = FontWeights.SemiBold };
    private readonly Button _clearCacheButton = new() { Content = "Очистить модели и кэш", Padding = new Thickness(10, 8, 10, 8), ToolTip = "Удаляет только локальную папку кэша этой программы; приложение и Python не затрагиваются." };
    private readonly TextBlock _status = new() { Text = "Выберите FB2-файл, чтобы начать.", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new Thickness(0, 8, 0, 0), Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly TextBlock _chapterHelp = new() { Text = "Выберите главы мышью. Ctrl+клик добавляет отдельные главы, Shift+клик — диапазон.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private string? _loadedFile;

    public MainWindow()
    {
        Title = "FB2 Accent Converter — ударения в книге";
        Width = 850;
        Height = 820;
        MinWidth = 720;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        FontFamily = new FontFamily("Segoe UI");
        Content = BuildLayout();
        _wholeBook.Checked += (_, _) => UpdateScope();
        _selectedChapters.Checked += (_, _) => UpdateScope();
        _convert.Click += async (_, _) => await ConvertAsync();
    }

    private UIElement BuildLayout()
    {
        var outer = new DockPanel { Margin = new Thickness(26) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock { Text = "Расстановка ударений в FB2", FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(32, 48, 70)) });
        header.Children.Add(new TextBlock { Text = "Создайте отдельную копию книги с ударениями. Исходный файл не изменяется.", Margin = new Thickness(0, 5, 0, 0), Foreground = Brushes.DimGray });
        DockPanel.SetDock(header, Dock.Top);
        outer.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_status);
        footer.Children.Add(_progress);
        var actions = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _clearCacheButton.Click += (_, _) => ClearCache();
        DockPanel.SetDock(_clearCacheButton, Dock.Left);
        actions.Children.Add(_clearCacheButton);
        DockPanel.SetDock(_convert, Dock.Right);
        actions.Children.Add(_convert);
        footer.Children.Add(actions);
        outer.Children.Add(footer);

        var content = new StackPanel();
        content.Children.Add(Section("1. Файл книги", BuildFilePanel()));
        content.Children.Add(Section("2. Куда сохранить результат", BuildOutputPanel()));
        content.Children.Add(Section("3. Что конвертировать", BuildScopePanel()));
        content.Children.Add(Section("4. Параметры обработки", BuildModelPanel()));
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        outer.Children.Add(scroll);
        return outer;
    }

    private UIElement BuildFilePanel()
    {
        var row = new DockPanel();
        var browse = new Button { Content = "Выбрать FB2…", Padding = new Thickness(12, 7, 12, 7) };
        browse.Click += (_, _) => SelectInput();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        _input.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(_input);
        return row;
    }

    private UIElement BuildOutputPanel()
    {
        var panel = new StackPanel();
        var row = new DockPanel();
        var browse = new Button { Content = "Путь…", Padding = new Thickness(12, 7, 12, 7) };
        browse.Click += (_, _) => SelectOutput();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_output);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "По умолчанию результат сохраняется рядом с книгой как «Имя_ударения.fb2». Можно указать другой путь и имя.", Margin = new Thickness(0, 7, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private UIElement BuildScopePanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(_wholeBook);
        panel.Children.Add(_selectedChapters);
        panel.Children.Add(_chapterHelp);
        _chapterHelp.Margin = new Thickness(0, 4, 0, 7);
        _chapters.IsEnabled = false;
        _chapters.Visibility = Visibility.Collapsed;
        panel.Children.Add(_chapterItems);
        _illustrations.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_illustrations);
        panel.Children.Add(new TextBlock { Text = "Если снять галочку, из результирующего FB2 будут удалены изображения и их данные; оригинал останется без изменений.", Margin = new Thickness(0, 4, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private UIElement BuildModelPanel()
    {
        var panel = new StackPanel();
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "Модель ударений:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var model in new[] { "turbo3.1", "turbo3.0", "turbo2.0", "big_poetry", "tiny" }) _model.Items.Add(model);
        _model.SelectedIndex = 0;
        row.Children.Add(_model);
        row.Children.Add(new TextBlock { Text = "Устройство:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        _device.Items.Add("CPU");
        _device.Items.Add("CUDA");
        _device.SelectedIndex = 0;
        row.Children.Add(_device);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "Turbo — универсальная модель; big_poetry подходит для стихов; tiny экономнее по ресурсам. CUDA требует совместимой видеокарты и библиотек, при сомнениях оставьте CPU. При первом запуске выбранная модель загружается из интернета.", Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static UIElement Section(string title, UIElement child)
    {
        var group = new GroupBox { Header = title, Content = child, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12), FontWeight = FontWeights.SemiBold };
        return group;
    }

    private void SelectInput()
    {
        var dialog = new OpenFileDialog { Filter = "Книги FictionBook (*.fb2)|*.fb2|Все файлы (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _loadedFile = dialog.FileName;
        _input.Text = _loadedFile;
        _output.Text = Path.Combine(Path.GetDirectoryName(_loadedFile)!, Path.GetFileNameWithoutExtension(_loadedFile) + "_ударения.fb2");
        _chapters.Items.Clear();
        _chapterItems.Children.Clear();
        _chapterChecks.Clear();
        _status.Text = "Читаю оглавление…";
        SetBusy(true);
        _ = Task.Run(() => Program.ReadChapters(_loadedFile)).ContinueWith(task => Dispatcher.Invoke(() =>
        {
            SetBusy(false);
            if (task.IsFaulted)
            {
                _status.Text = "Не удалось прочитать FB2: " + (task.Exception?.GetBaseException().Message ?? "неизвестная ошибка");
                return;
            }
            foreach (var chapter in task.Result)
            {
                var item = new ChapterItem(chapter.Id, chapter.Title, chapter.Level);
                _chapters.Items.Add(item);
                var check = new CheckBox
                {
                    Content = item.Title,
                    Tag = item,
                    IsChecked = true,
                    Padding = new Thickness(0, 2, 0, 2),
                    Margin = new Thickness(item.Level * 18, 0, 0, 0),
                    ToolTip = item.Title
                };
                _chapterChecks.Add(check);
                _chapterItems.Children.Add(check);
            }
            _status.Text = task.Result.Count == 0 ? "Файл открыт, главы в оглавлении не найдены; доступна конвертация всей книги." : $"Файл готов. Найдено глав: {task.Result.Count}. Снимите отметки с глав, которые не нужно обрабатывать.";
            UpdateScope();
        }));
    }

    private void SelectOutput()
    {
        var dialog = new SaveFileDialog { Filter = "Книга FictionBook (*.fb2)|*.fb2", DefaultExt = ".fb2", AddExtension = true, FileName = Path.GetFileName(_output.Text) };
        if (!string.IsNullOrWhiteSpace(_output.Text)) dialog.InitialDirectory = Path.GetDirectoryName(_output.Text);
        if (dialog.ShowDialog(this) == true) _output.Text = dialog.FileName;
    }

    private async Task ConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(_loadedFile) || !File.Exists(_loadedFile)) { _status.Text = "Сначала выберите существующий FB2-файл."; return; }
        if (string.IsNullOrWhiteSpace(_output.Text)) { _status.Text = "Укажите путь сохранения результата."; return; }
        if (string.Equals(Path.GetFullPath(_loadedFile), Path.GetFullPath(_output.Text), StringComparison.OrdinalIgnoreCase)) { _status.Text = "Результат не должен совпадать с исходным файлом."; return; }
        var ids = _selectedChapters.IsChecked == true
            ? _chapterChecks.Where(check => check.IsChecked == true).Select(check => ((ChapterItem)check.Tag!).Id).ToArray()
            : null;
        if (_selectedChapters.IsChecked == true && (ids == null || ids.Length == 0)) { _status.Text = "Отметьте хотя бы одну главу в оглавлении."; return; }
        var input = _loadedFile;
        var output = _output.Text;
        var model = _model.SelectedItem?.ToString() ?? "turbo3.1";
        var device = _device.SelectedItem?.ToString() ?? "CPU";
        var includeImages = _illustrations.IsChecked == true;
        SetBusy(true);
        _status.Text = "Подготовка модели… При первом запуске загрузка может занять время.";
        _progress.Value = 0;
        try
        {
            await Task.Run(() => Program.ConvertInteractive(input, output, model, device, ids, includeImages,
                message => Dispatcher.Invoke(() =>
                {
                    _status.Text = message;
                    if (message.StartsWith("Модель ", StringComparison.Ordinal) && message.Contains("загружена", StringComparison.Ordinal))
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = 0;
                    }
                    else if (message.StartsWith("Готово:", StringComparison.Ordinal))
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = 100;
                    }
                }),
                (completed, total) => Dispatcher.Invoke(() =>
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = total == 0 ? 100 : completed * 100.0 / total;
                    _status.Text = $"Обработка текста: {completed:N0} из {total:N0} фрагментов ({_progress.Value:0}%).";
                })));
            _status.Text = "Конвертация завершена: " + output;
            if (MessageBox.Show(this, "Книга готова. Открыть папку с результатом?", "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _status.Text = "Ошибка: " + ex.GetBaseException().Message; }
        finally { SetBusy(false); }
    }

    private void UpdateScope()
    {
        var selecting = _selectedChapters.IsChecked == true;
        _chapterItems.IsEnabled = selecting && _loadedFile != null && _chapters.Items.Count > 0;
        _illustrations.IsEnabled = _loadedFile != null;
        _chapterItems.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        _chapterHelp.Text = selecting
            ? "Структура и главы соответствуют оглавлению DeepReader. Отключите отметку у глав, которые пропускаем."
            : "Вся книга будет обработана; чтобы выбрать главы, включите режим выше.";
    }

    private void SetBusy(bool busy)
    {
        _convert.IsEnabled = !busy;
        _clearCacheButton.IsEnabled = !busy;
        _status.Opacity = busy ? 0.8 : 1;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy;
    }

    private void ClearCache()
    {
        var cache = Path.Combine(AppContext.BaseDirectory, "model-cache");
        var answer = MessageBox.Show(this,
            "Удалить загруженные этой программой модели и её кэш? Это освободит место; при следующей конвертации модели будут скачаны снова. Файлы программы, Python и ваши книги не удаляются.",
            "Очистка данных программы", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            _status.Text = "Кэш моделей удалён.";
        }
        catch (Exception ex) { _status.Text = "Не удалось удалить кэш: " + ex.Message; }
    }

    private sealed record ChapterItem(int Id, string Title, int Level)
    {
        public override string ToString() => Title;
    }
}
