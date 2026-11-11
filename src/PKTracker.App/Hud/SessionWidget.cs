using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PKTracker.App.Infrastructure;
using PKTracker.Data;

namespace PKTracker.App.Hud;

/// <summary>
/// Виджет текущей сессии: маленькое окно поверх столов — сколько сыграно, итог в bb и мини-график.
///
/// Показывает только результат уже завершённых и записанных раздач — то же, что вкладка «Сессии»,
/// только перед глазами. О текущей раздаче в нём ничего нет, поэтому правила румов он не затрагивает.
/// Окно не забирает фокус у клиента рума (как и панели HUD) и перетаскивается за любое место.
/// </summary>
public sealed class SessionWidget : Window
{
    private readonly AppServices _svc;
    private readonly TextBlock _title = new() { FontSize = 11, Opacity = 0.75 };
    private readonly TextBlock _net = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 1) };
    private readonly TextBlock _detail = new() { FontSize = 11, Opacity = 0.85 };
    private readonly Polyline _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Line _zero = new() { StrokeThickness = 1, Stroke = new SolidColorBrush(Color.FromRgb(0x5A, 0x58, 0x7A)) };
    private readonly Canvas _chart = new() { Width = 196, Height = 44, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private SessionSnapshot _snap = SessionSnapshot.Empty;
    private bool _loading;

    /// <summary>Пользователь закрыл виджет крестиком.</summary>
    public event Action? Dismissed;

    public SessionWidget(AppServices svc)
    {
        _svc = svc;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        var ink = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8));
        _title.Foreground = _detail.Foreground = ink;

        var close = new TextBlock
        {
            Text = "×", FontSize = 14, Foreground = ink, Opacity = 0.6, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, 0, 0),
            ToolTip = "Скрыть виджет (включается на вкладке HUD)",
        };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismissed?.Invoke(); };

        _chart.Children.Add(_zero);
        _chart.Children.Add(_line);
        var stack = new StackPanel();
        foreach (var e in new UIElement[] { _title, _net, _detail, _chart }) stack.Children.Add(e);
        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(close);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1D, 0x1C, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x35, 0x5A)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 8), Child = grid,
        };

        if (svc.Settings.SessionWidgetPos is { Length: 2 } pos) { Left = pos[0]; Top = pos[1]; }
        else { Left = SystemParameters.WorkArea.Right - 240; Top = SystemParameters.WorkArea.Top + 20; }

        SourceInitialized += (_, _) => Win32.MakeOverlay(new WindowInteropHelper(this).Handle);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            _svc.Settings.SessionWidgetPos = new[] { Left, Top };
            try { _svc.Settings.Save(); }
            catch (System.IO.IOException) { }
        };
        // Длительность сессии растёт и без новых раздач — подпись обновляем по таймеру.
        _clock.Tick += (_, _) => Render();
        Loaded += (_, _) => { _clock.Start(); _ = RefreshAsync(); };
        Closed += (_, _) => _clock.Stop();
        Render();
    }

    /// <summary>Перечитать сессию из базы (после импорта новых раздач). Запрос — в фоне.</summary>
    public async Task RefreshAsync()
    {
        if (_loading || _svc.Host is not { } host) return;
        _loading = true;
        try
        {
            int gap = _svc.Settings.SessionGapMinutes;
            _snap = await Task.Run(() => new AnalyticsRepository(host.Db).LastSessionAsync(gap));
            Render();
        }
        catch (Exception ex) { _svc.Log("Session", "Виджет сессии: " + ex.Message); }
        finally { _loading = false; }
    }

    private void Render()
    {
        var s = _snap;
        if (s.Hands == 0)
        {
            _title.Text = "Сессия";
            _net.Text = "—";
            _net.Foreground = _title.Foreground;
            _detail.Text = "раздач пока нет";
            _line.Points.Clear();
            return;
        }
        // Сессия «идёт», пока с последней раздачи прошло меньше перерыва между сессиями; иначе это прошлая.
        bool live = DateTime.UtcNow - s.EndUtc < TimeSpan.FromMinutes(_svc.Settings.SessionGapMinutes);
        var length = (live ? DateTime.UtcNow : s.EndUtc) - s.StartUtc;
        string dur = length.TotalHours >= 1 ? $"{(int)length.TotalHours} ч {length.Minutes:00} м" : $"{Math.Max(1, (int)length.TotalMinutes)} м";
        _title.Text = $"{(live ? "Сессия" : "Прошлая сессия")} · {dur} · столов {s.Tables}";
        var inv = CultureInfo.InvariantCulture;
        _net.Text = (s.NetBb >= 0 ? "+" : "−") + Math.Abs(s.NetBb).ToString("0.0", inv) + " bb";
        // Знак виден и по символу перед числом, цвет лишь дублирует его.
        _net.Foreground = (Brush)Application.Current.FindResource(s.NetBb >= 0 ? "Positive" : "Negative");
        _detail.Text = $"раздач {s.Hands} · {(s.Bb100 ?? 0).ToString("+0.0;−0.0;0.0", inv)} bb/100 · EV {s.EvBb.ToString("+0.0;−0.0;0.0", inv)}";
        DrawChart(s.CumBb);
    }

    private void DrawChart(double[] cum)
    {
        _line.Points.Clear();
        if (cum.Length < 2) return;
        double w = _chart.Width, h = _chart.Height;
        double min = Math.Min(0, cum.Min()), max = Math.Max(0, cum.Max());
        double span = Math.Max(1e-9, max - min);
        // Не больше точки на пиксель: длинная сессия не должна превращаться в тысячи сегментов.
        int step = Math.Max(1, cum.Length / (int)w);
        for (int i = 0; i < cum.Length; i += step)
            _line.Points.Add(new Point(i * w / (cum.Length - 1), h - (cum[i] - min) / span * h));
        _line.Points.Add(new Point(w, h - (cum[^1] - min) / span * h));
        _line.Stroke = new SolidColorBrush(Color.FromRgb(0x9C, 0x92, 0xF8));
        double y0 = h - (0 - min) / span * h;
        _zero.X1 = 0; _zero.X2 = w; _zero.Y1 = _zero.Y2 = y0;
    }
}
