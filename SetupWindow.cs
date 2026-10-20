using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FB2AccentConverter;

/// <summary>
/// Окно первой подготовки DSR: показывает, что именно скачивается, и общую шкалу
/// прогресса. Окно немалое по смыслу, но маленькое по коду: оно только отображает
/// сообщения, которые приходят из Program через SetupProgress, и закрывается само,
/// когда подготовка завершилась.
/// </summary>
internal sealed class SetupWindow : Window
{
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly TextBlock _elapsed;
    private readonly Button _close;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _percent;
    private bool _failed;

    public SetupWindow(bool automatic)
    {
        Title = automatic ? "DSR — первый запуск" : "DSR — подготовка к работе";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xED, 0xDD));
        Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0x30, 0x25));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = automatic ? "Первый запуск: DSR готовит ресурсы" : "Подготовка DSR к работе",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Сейчас DSR проверит Python и пакеты и скачает всё, чего не хватает: "
                 + "RUAccent, ONNX Runtime и модель ударений. Это может занять несколько минут.\n\n"
                 + "Всё скачанное размещается только в папке «ресурсы» рядом с DSR.exe. "
                 + "Сам DSR.exe и ваши книги не изменяются; папку «ресурсы» можно удалить "
                 + "и загрузить заново кнопкой в программе.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 14),
        });

        _bar.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(_bar);

        _status = new TextBlock
        {
            Text = "Проверяю, что уже готово…",
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 40,
        };
        panel.Children.Add(_status);

        _detail = new TextBlock
        {
            Text = "0%",
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.75,
        };
        panel.Children.Add(_detail);

        _elapsed = new TextBlock
        {
            Text = "Прошло: 00:00",
            Margin = new Thickness(0, 2, 0, 0),
            Opacity = 0.65,
        };
        panel.Children.Add(_elapsed);

        _close = new Button
        {
            Content = "Закрыть",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _close.Click += (_, _) => Close();
        panel.Children.Add(_close);

        Content = panel;
        _clock.Tick += (_, _) =>
            _elapsed.Text = "Прошло: " + _watch.Elapsed.ToString(@"mm\:ss");
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
    }

    /// <summary>Сообщение от подготовки; процент можно не указывать — он оценится по этапу.</summary>
    public void Report(string message, double? percent = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var clean = CleanMessage(message);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_failed) return;
            _status.Text = clean;
            var estimate = percent ?? Estimate(clean);
            Advance(estimate);
        }));
    }

    public void Complete()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_failed) return;
            Advance(100);
            _status.Text = "Готово. Ресурсы подготовлены — DSR больше не будет проверять их при каждом запуске.";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }));
    }

    public void Fail(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _failed = true;
            _bar.Value = _percent;
            _bar.Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x4A, 0x3A));
            _status.Text = "Подготовка не завершена: " + message;
            _detail.Text = "Можно закрыть окно и повторить позже кнопкой «Загрузить ресурсы заново».";
            _close.Visibility = Visibility.Visible;
            _clock.Stop();
        }));
    }

    private void Advance(double percent)
    {
        if (percent > _percent) _percent = Math.Clamp(percent, 0, 100);
        _bar.Value = _percent;
        _bar.IsIndeterminate = false;
        _detail.Text = _percent.ToString("0") + "%";
    }

    private double Estimate(string message)
    {
        if (message.Contains("Проверяю локальное окружение", StringComparison.OrdinalIgnoreCase)) return 3;
        if (message.Contains("Python не найден", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Скачиваю переносимый Python", StringComparison.OrdinalIgnoreCase)) return 6;
        if (message.Contains("Распаковываю переносимый Python", StringComparison.OrdinalIgnoreCase)) return 10;
        if (message.Contains("Создаю локальное окружение", StringComparison.OrdinalIgnoreCase)) return 14;
        if (message.Contains("Переносимый Python готов", StringComparison.OrdinalIgnoreCase)) return 16;
        if (message.Contains("Устанавливаю RUAccent", StringComparison.OrdinalIgnoreCase)) return 20;
        if (message.Contains("Словари распакованы", StringComparison.OrdinalIgnoreCase)) return 24;
        if (message.Contains("Python и зависимости проверены", StringComparison.OrdinalIgnoreCase)) return 30;
        if (message.Contains("Подготовка модели", StringComparison.OrdinalIgnoreCase)) return 38;
        if (message.Contains("Загрузка модели", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Скачиваю модель", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(message, @"\((\d+)%\)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var downloadPercent))
                return 40 + downloadPercent * 0.52;
            return Math.Max(_percent, 40);
        }
        if (message.Contains("не загрузилась (попытка", StringComparison.OrdinalIgnoreCase)) return Math.Max(_percent, 42);
        if (message.Contains("Убираю недокачанный", StringComparison.OrdinalIgnoreCase)) return Math.Max(_percent, 44);
        if (message.Contains("загружена; размер файлов модели", StringComparison.OrdinalIgnoreCase)) return 94;
        if (message.Contains("Подготовка завершена", StringComparison.OrdinalIgnoreCase)) return 100;
        if (message.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Collecting", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Installing collected", StringComparison.OrdinalIgnoreCase))
            return Math.Max(_percent, 22);
        return _percent;
    }

    private static string CleanMessage(string message)
    {
        // pip и huggingface иногда печатают служебные проценты и длинные пути.
        var clean = message.Trim();
        if (clean.Length > 500) clean = clean[..500] + "…";
        return clean;
    }
}
