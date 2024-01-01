using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PKTracker.App.Infrastructure;
using PKTracker.App.Replayer;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Stats;

namespace PKTracker.App.Hud;

/// <summary>
/// Попап одного стата: значение, во что обошёлся спот, диапазоны по каждому действию,
/// разбивки и список рук.
///
/// Одно окно и для наведения, и для клика: раньше их было два — компактное по наведению
/// и подробное по клику, — и они расходились в том, что показывают. Разница осталась только
/// в поведении: по наведению окно закрывается само, по клику висит, а закреплённое булавкой
/// не закрывается никогда.
/// </summary>
public sealed class StatDetailWindow : Window, IPinnable
{
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private readonly TrackerHost _host;
    private readonly bool _overlay;
    private readonly DockPanel _root = new();
    private readonly ToggleButton _pin;
    private readonly StackPanel _head = new();
    private readonly ContentControl _body = new();
    private Point _dragFrom;
    private bool _dragging;

    /// <summary>Закреплённый попап не закрывается ни по уходу курсора, ни при наведении на другой стат.</summary>
    public bool Pinned => _pin.IsChecked == true;

    public event Action<Window>? PinToggled;

    public IntPtr Handle { get; private set; }

    public StatDetailWindow(TrackerHost host, bool overlay)
    {
        _host = host;
        _overlay = overlay;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        Foreground = Res("Text");
        Topmost = overlay;
        ShowActivated = !overlay;

        _pin = new ToggleButton
        {
            Content = "📌", Width = 26, Height = 22, ToolTip = "Закрепить: не закрывать при уходе курсора",
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Res("TextDim"),
            Cursor = Cursors.Hand,
        };
        _pin.Checked += (_, _) => { _pin.Foreground = Res("Accent"); PinToggled?.Invoke(this); };
        _pin.Unchecked += (_, _) => { _pin.Foreground = Res("TextDim"); PinToggled?.Invoke(this); };

        var close = new Button
        {
            Content = "✕", Width = 26, Height = 22, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Res("TextDim"), Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_pin);
        buttons.Children.Add(close);

        var headRow = new DockPanel { Margin = new Thickness(12, 8, 6, 0), Cursor = Cursors.SizeAll };
        DockPanel.SetDock(buttons, Dock.Right);
        headRow.Children.Add(buttons);
        headRow.Children.Add(_head);
        headRow.MouseLeftButtonDown += HeadDown;
        headRow.MouseMove += HeadMove;
        headRow.MouseLeftButtonUp += HeadUp;

        DockPanel.SetDock(headRow, Dock.Top);
        _root.Children.Add(headRow);
        _body.Margin = new Thickness(12, 4, 12, 10);
        _root.Children.Add(_body);

        Content = new Border
        {
            Background = Res("Bg1"), BorderBrush = Res("AccentDim"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = _root, MaxWidth = 640,
        };
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            // Над столом окно не должно отбирать фокус у клиента рума (§5.2); в трекере это не нужно.
            if (_overlay) Win32.MakeOverlay(Handle);
        };
    }

    // ── содержимое ─────────────────────────────────────────────────────────

    public void ShowLoading(string player, CompiledStat stat)
    {
        Head(player, stat, null, null);
        _body.Content = new TextBlock { Text = "загрузка…", Foreground = Res("TextDim"), Margin = new Thickness(0, 6, 0, 6) };
    }

    public void ShowData(string player, CompiledStat stat, PlayerStatSet set, StatPopupData data, GameKind kind)
    {
        Head(player, stat, set, data);
        var sp = new StackPanel();
        sp.Children.Add(Metrics(data));
        sp.Children.Add(Tabs(stat, data, kind));
        _body.Content = sp;
    }

    private void Head(string player, CompiledStat stat, PlayerStatSet? set, StatPopupData? data)
    {
        _head.Children.Clear();
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent") };
        title.Inlines.Add(new Run(stat.Definition.Title));
        if (stat.Scope != StatScope.Expression)
            title.ToolTip = $"cases: {stat.Definition.Opportunity}\nhits:  {stat.Definition.Hit}";
        _head.Children.Add(title);

        var sub = new TextBlock { Foreground = Res("TextDim"), FontSize = 11 };
        if (set != null)
        {
            var (v, n) = set.Evaluate(stat.Code);
            var value = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
            value.Inlines.Add(new Run(v is { } x ? x.ToString("F" + stat.Definition.Format.Decimals) : "–"));
            value.Inlines.Add(new Run($" {n}") { Foreground = Res("TextDim"), FontSize = 11, FontWeight = FontWeights.Normal });
            _head.Children.Add(value);
            sub.Text = data == null ? player : $"{player} · {data.Hits}/{data.Cases} по последним {data.Scanned:N0} строкам";
        }
        else sub.Text = player;
        _head.Children.Add(sub);
    }

    /// <summary>Во что обходится спот: не «как часто», а «чем кончается» — это и есть смысл попапа.</summary>
    private static UIElement Metrics(StatPopupData data)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        Add("Профит, bb/рука", data.ActionProfitBb is { } p ? (p >= 0 ? "+" : "") + p.ToString("F2") : "–", data.HitHands, sign: true);
        Add("Выиграл руку", Pct(data.WonHandPct), data.HitHands);
        Add("WTSD", Pct(data.WtsdPct), data.HitSawFlop);
        Add("W$SD", Pct(data.WsdPct), data.HitShowdown);
        return wrap;

        void Add(string label, string value, long n, bool sign = false)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 20, 6), MinWidth = 92 };
            var v = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold };
            v.Inlines.Add(new Run(value));
            v.Inlines.Add(new Run($" {n}") { Foreground = Res("TextDim"), FontSize = 10, FontWeight = FontWeights.Normal });
            if (sign) v.SetBinding(ForegroundProperty, new Binding("Text")
            {
                RelativeSource = RelativeSource.Self,
                Converter = (IValueConverter)Application.Current.Resources["SignBrush"],
            });
            box.Children.Add(v);
            box.Children.Add(new TextBlock { Text = label, Foreground = Res("TextDim"), FontSize = 10 });
            wrap.Children.Add(box);
        }
    }

    private static string Pct(double? v) => v is null ? "–" : v.Value.ToString("F0");

    private UIElement Tabs(CompiledStat stat, StatPopupData data, GameKind kind)
    {
        var tabs = new TabControl
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0),
        };

        // Вкладка на каждое действие, которое в этом споте вообще встречалось: «чем рейзил»,
        // «чем коллировал», «что сбрасывал» — рядом.
        foreach (var choice in new[] { Choice.Raise, Choice.Bet, Choice.Call, Choice.Check, Choice.Fold })
        {
            if (!data.RangeByAction.TryGetValue(choice, out var range) || range.Sum() == 0) continue;
            tabs.Items.Add(Tab(ChoiceName(choice), RangePane(range, $"Руки, с которыми выбрано «{ChoiceName(choice)}» в этом споте")));
        }
        if (data.KnownHands > 0 || tabs.Items.Count == 0)
            tabs.Items.Add(Tab("Диапазон", RangePane(data.RangeHits, "Вскрытые руки в срабатываниях стата")));

        tabs.Items.Add(Tab("Позиции", Stack(
            Table("По позициям", data.ByPosition.Select(r => (r.Key, $"{r.Pct:F0}%", $"{r.Hits}/{r.Cases}"))),
            Table("По глубине стека", data.ByStack.Select(r => (r.Key, $"{r.Pct:F0}%", $"{r.Hits}/{r.Cases}"))))));
        if (stat.Scope == StatScope.Decision)
        {
            tabs.Items.Add(Tab("Next Actions", Counts("Следующее решение после срабатывания стата", data.NextActions)));
            tabs.Items.Add(Tab("Сайзинги", Counts("Размер ставки/рейза в % банка", data.BetSizing)));
            tabs.Items.Add(Tab("Сила руки", Counts("Категория руки на улице (только известные карты)", data.HandStrength)));
            tabs.Items.Add(Tab("Борды", Counts("Текстура борда (доля от срабатываний)", data.Boards)));
        }
        tabs.Items.Add(Tab("Руки", HandsList(data)));
        return tabs;
    }

    private static string ChoiceName(Choice c) => c switch
    {
        Choice.Raise => "Рейз", Choice.Bet => "Ставка", Choice.Call => "Колл", Choice.Check => "Чек", _ => "Фолд",
    };

    private static TabItem Tab(string title, UIElement content) => new()
    {
        Header = title,
        Content = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 380, Padding = new Thickness(0, 8, 4, 0),
        },
    };

    private static StackPanel Stack(params UIElement[] items)
    {
        var sp = new StackPanel();
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    private static UIElement RangePane(long[] range, string caption)
    {
        var panel = new StackPanel();
        long total = range.Sum();
        panel.Children.Add(new TextBlock
        {
            Text = total == 0
                ? "Нет вскрытых рук: карты оппонентов известны только на шоудауне."
                : $"{caption}: {total}. Справа сверху — одномастные, слева снизу — разномастные.",
            Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), MaxWidth = 560,
        });
        long max = Math.Max(1, range.Max());
        var grid = new UniformGrid { Rows = 13, Columns = 13, Width = 13 * 42, Height = 13 * 30 };
        var accent = ((SolidColorBrush)Res("Accent")).Color;
        var empty = ((SolidColorBrush)Res("Bg2")).Color;
        for (int cls = 0; cls < 169; cls++)
        {
            long n = range[cls];
            double t = n == 0 ? 0 : 0.25 + 0.75 * n / max;
            var color = Color.FromRgb((byte)(empty.R + (accent.R - empty.R) * t), (byte)(empty.G + (accent.G - empty.G) * t),
                (byte)(empty.B + (accent.B - empty.B) * t));
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(color), BorderBrush = Res("Bg0"), BorderThickness = new Thickness(0.5),
                ToolTip = $"{Card.HoleClassName(cls)}: {n}",
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Card.HoleClassName(cls), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = n > 0 ? Brushes.Black : Res("TextDim"),
                        },
                        new TextBlock
                        {
                            Text = n > 0 ? n.ToString() : "", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = Brushes.Black,
                        },
                    },
                },
            });
        }
        panel.Children.Add(new Viewbox { Child = grid, Stretch = Stretch.Uniform, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left });
        return panel;
    }

    private static UIElement Table(string title, IEnumerable<(string A, string B, string C)> rows)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var list = rows.ToList();
        if (list.Count == 0) panel.Children.Add(new TextBlock { Text = "нет данных", Foreground = Res("TextDim") });
        foreach (var (a, b, c) in list)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            AddCell(g, a, 0, Res("Text"));
            AddCell(g, b, 1, Res("Accent"));
            AddCell(g, c, 2, Res("TextDim"));
            panel.Children.Add(g);
        }
        return panel;
    }

    private static void AddCell(Grid g, string text, int col, Brush brush)
    {
        var tb = new TextBlock { Text = text, Foreground = brush };
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
    }

    /// <summary>Горизонтальные полосы долей.</summary>
    private static UIElement Counts(string title, List<CountRow> rows)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Foreground = Res("TextDim"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        if (rows.Count == 0) panel.Children.Add(new TextBlock { Text = "нет данных", Foreground = Res("TextDim") });
        foreach (var r in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2), Width = 540, HorizontalAlignment = HorizontalAlignment.Left };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            AddCell(g, r.Key, 0, Res("Text"));
            var track = new Border { Background = Res("Bg2"), CornerRadius = new CornerRadius(3), Height = 12, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Background = Res("Accent"), CornerRadius = new CornerRadius(3), Height = 12, HorizontalAlignment = HorizontalAlignment.Left };
            var holder = new Grid();
            holder.Children.Add(track);
            holder.Children.Add(bar);
            holder.SizeChanged += (_, e) => bar.Width = Math.Max(2, e.NewSize.Width * r.Pct / 100);
            Grid.SetColumn(holder, 1);
            g.Children.Add(holder);
            AddCell(g, $"  {r.Pct:F0}%  ({r.Count})", 2, Res("TextDim"));
            panel.Children.Add(g);
        }
        return panel;
    }

    private UIElement HandsList(StatPopupData data)
    {
        var grid = new DataGrid { Height = 320, Width = 560, ItemsSource = data.Hands, IsReadOnly = true, AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Время (UTC)", Binding = new Binding(nameof(PopupHand.PlayedAt)) { StringFormat = "dd.MM HH:mm" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Улица", Binding = new Binding(nameof(PopupHand.Street)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Поз.", Binding = new Binding(nameof(PopupHand.Position)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Действие", Binding = new Binding(nameof(PopupHand.Action)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Размер, %", Binding = new Binding(nameof(PopupHand.SizePct)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Карты", Binding = new Binding(nameof(PopupHand.Cards)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var host = _host;
        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is PopupHand h) new ReplayerWindow(host, h.HandPk).Show();
        };
        return Stack(
            new TextBlock { Text = "Последние срабатывания стата (двойной клик — реплеер)", Foreground = Res("TextDim"), Margin = new Thickness(0, 0, 0, 6) },
            grid);
    }

    // ── перетаскивание и размещение ────────────────────────────────────────

    private void HeadDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = PointToScreen(e.GetPosition(this));
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
    }

    private void HeadMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Handle == IntPtr.Zero) return;
        var now = PointToScreen(e.GetPosition(this));
        int dx = (int)(now.X - _dragFrom.X), dy = (int)(now.Y - _dragFrom.Y);
        if (dx == 0 && dy == 0) return;
        _dragFrom = now;
        Win32.GetWindowRect(Handle, out var r);
        Win32.SetWindowPos(Handle, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void HeadUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    /// <summary>
    /// Рядом с тем, на что навели: справа, а если не помещается на мониторе — слева, и с подъёмом
    /// вверх, когда попап не влезает вниз. Координаты физические.
    /// </summary>
    public void PlaceNear(PopupAnchor anchor)
    {
        if (Handle == IntPtr.Zero) return;
        UpdateLayout();
        uint dpi = Win32.GetDpiForWindow(Handle);
        int w = (int)Math.Ceiling(ActualWidth * dpi / 96.0), h = (int)Math.Ceiling(ActualHeight * dpi / 96.0);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchor.Left, anchor.Top)).WorkingArea;
        int x = anchor.Right + 6;
        if (x + w > screen.Right) x = Math.Max(screen.Left, anchor.Left - w - 6);
        int y = Math.Clamp(anchor.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - h));
        Win32.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }
}
