using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PKTracker.Core.Model;
using PKTracker.Data;

namespace PKTracker.App.Views;

/// <summary>Строка таблицы «сила руки»: числа уже отформатированы, колонки — простые биндинги.</summary>
public sealed class StrengthVm
{
    public required StrengthRow Row { get; init; }
    public long Total { get; init; }
    public string Label => Row.Label;
    public string Hands => Row.Hands.ToString("N0");
    public string Share => Total == 0 ? "—" : (Row.Hands * 100.0 / Total).ToString("0.0", CultureInfo.InvariantCulture);
    public string Bet => Fmt.P(Row.BetPct);
    public string Check => Fmt.P(Row.CheckPct);
    public string Call => Fmt.P(Row.CallPct);
    public string Fold => Fmt.P(Row.FoldPct);
    public string Wtsd => Fmt.P(Row.WtsdPct);
    public string Won => Fmt.P(Row.WonPct);
    public string Bb100 => Fmt.Sg(Row.Bb100, 0);
}

/// <summary>Строка таблицы под графиком-разрезом.</summary>
public sealed class BreakdownVm
{
    public required SplitRow Row { get; init; }
    public string Label => Row.Label;
    public string Hands => Row.Hands.ToString("N0");
    public string Bb100 => Fmt.Sg(Row.Bb100, 1);
    public string EvBb100 => Fmt.Sg(Row.EvBb100, 1);
    public string NetBb => Fmt.Sg(Row.NetBb, 0);
}

/// <summary>
/// Вкладка «Аналитика»: свои стартовые руки тепловой картой 13×13, сила руки на постфлопе по улицам
/// и разрезы выигрыша столбиками. Всё — по завершённым раздачам из базы: разбор после игры.
///
/// Цвет здесь несёт одну задачу на график. Полярность (плюс/минус) — пара «синий ↔ оранжевый» с нейтральной
/// серединой: её различают и при нарушениях цветового зрения, в отличие от пары «зелёный/красный».
/// Величина без знака — один синий тон от фона к яркому. Число в клетке или над столбиком есть всегда,
/// так что цвет никогда не единственный носитель значения.
/// </summary>
public sealed class AnalyticsView : UserControl
{
    private static readonly Color Surface = Color.FromRgb(0x25, 0x24, 0x3C);
    private static readonly Color Plus = Color.FromRgb(0x39, 0x87, 0xE5);
    private static readonly Color Minus = Color.FromRgb(0xD9, 0x59, 0x26);
    // Величина без знака — зелёная шкала от тёмного к яркому, та же, что у матрицы рук в попапе стата.
    private static readonly Color GreenLow = Color.FromRgb(0x1F, 0x8F, 0x35);
    private static readonly Color GreenHigh = Color.FromRgb(0x4D, 0xF2, 0x4D);

    private AppServices? _svc;
    // Прозрачный фон: у TabControl по умолчанию он белый и не подхватывает тёмную тему.
    private readonly TabControl _tabs = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 6, 0, 0),
        ItemContainerStyle = (Style)Application.Current.FindResource("SubTab"),
    };
    /// <summary>Интерфейс собран: до этого события выбора в списках — это заполнение, а не действие пользователя.</summary>
    private bool _ready;
    private CancellationTokenSource? _cts;
    private HoleClassRow[] _holes = Array.Empty<HoleClassRow>();
    private List<SplitRow> _bars = new();

    private readonly ComboBox _kind = Combo(110, "Турниры", "Кеш");
    private readonly ComboBox _pos = Combo(110);
    private readonly ComboBox _stack = Combo(110);
    private readonly ComboBox _players = Combo(120, "Все столы", "Хедз-ап", "3–4 игрока", "5–6 игроков", "7+ игроков");
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    private readonly ComboBox _metric = Combo(150, "bb/100", "Сумма, bb", "Раздач", "VPIP, %", "PFR, %");
    private readonly UniformGrid _matrix = new() { Rows = 13, Columns = 13 };
    private readonly TextBlock _matrixLegend = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _matrixTotal = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly DataGrid _best = Grid(), _worst = Grid();

    private readonly DataGrid _strength = Grid();
    private readonly ComboBox _street = Combo(110, "Флоп", "Тёрн", "Ривер");
    private List<StrengthRow> _strengthRows = new();

    private readonly ComboBox _by = Combo(150, "Позиция", "Стек, bb", "Игроков в раздаче", "День недели", "Час суток", "Исход раздачи");
    private readonly ComboBox _barMetric = Combo(120, "bb/100", "Сумма, bb", "Раздач");
    private readonly ScottPlot.WPF.WpfPlot _plot = new();
    private readonly DataGrid _barGrid = Grid();

    public AnalyticsView()
    {
        _pos.Items.Add("Все позиции");
        foreach (var p in new[] { Position.UTG, Position.UTG1, Position.MP, Position.LJ, Position.HJ, Position.CO, Position.BTN, Position.SB, Position.BB })
            _pos.Items.Add(p.ToString());
        _pos.SelectedIndex = 0;
        FillStacks();

        var refresh = new Button { Content = "Обновить", Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("AccentButton") };
        refresh.Click += async (_, _) => await ReloadAsync();
        _kind.SelectionChanged += async (_, _) => { FillStacks(); await ReloadAsync(); };
        foreach (var c in new[] { _pos, _stack, _players }) c.SelectionChanged += async (_, _) => await ReloadAsync();
        _metric.SelectionChanged += (_, _) => RenderMatrix();
        _by.SelectionChanged += async (_, _) => await ReloadBarsAsync();
        _barMetric.SelectionChanged += (_, _) => RenderBars();

        // Фильтры — одной строкой над всеми вкладками: они общие для карты, таблиц и графика.
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var e in new UIElement[] { _kind, _pos, _stack, _players, refresh, _status }) bar.Children.Add(e);

        BuildColumns();
        var tabs = _tabs;
        tabs.Items.Add(new TabItem { Header = "Стартовые руки", Content = MatrixPane() });
        tabs.Items.Add(new TabItem { Header = "Сила руки", Content = StrengthPane() });
        tabs.Items.Add(new TabItem { Header = "Графики", Content = BarsPane() });

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(tabs);
        Content = root;
        MainWindow.ApplyPlotTheme(_plot.Plot);
        _ready = true;
    }

    public void Attach(AppServices svc) => _svc = svc;

    /// <summary>Открыть внутреннюю вкладку по заголовку («Сила руки», «Графики»).</summary>
    public void SelectTab(string header)
    {
        foreach (var item in _tabs.Items.OfType<TabItem>())
            if (string.Equals(item.Header as string, header, StringComparison.OrdinalIgnoreCase)) { _tabs.SelectedItem = item; return; }
    }

    // ── Данные ──────────────────────────────────────────────────────────────────

    private AnalyticsFilter Filter()
    {
        bool cash = _kind.SelectedIndex == 1;
        var f = new AnalyticsFilter { Kind = cash ? GameKind.Cash : GameKind.Mtt };
        if (_pos.SelectedIndex > 0 && Enum.TryParse<Position>((string)_pos.SelectedItem, out var p)) f = f with { Position = p };
        if (_stack.SelectedIndex > 0)
        {
            var band = ReportSlice.StackBands(!cash)[_stack.SelectedIndex - 1];
            f = f with { MinStackBb = band.From, MaxStackBb = band.To >= 1e9 ? null : band.To };
        }
        (int? lo, int? hi) = _players.SelectedIndex switch
        {
            1 => ((int?)2, (int?)2), 2 => (3, 4), 3 => (5, 6), 4 => (7, null), _ => ((int?)null, (int?)null),
        };
        return f with { MinPlayers = lo, MaxPlayers = hi };
    }

    private void FillStacks()
    {
        int keep = _stack.SelectedIndex;
        _stack.Items.Clear();
        _stack.Items.Add("Любой стек");
        foreach (var b in ReportSlice.StackBands(_kind.SelectedIndex != 1)) _stack.Items.Add(b.Label);
        _stack.SelectedIndex = keep >= 0 && keep < _stack.Items.Count ? keep : 0;
    }

    private BreakdownKind By => _by.SelectedIndex switch
    {
        1 => BreakdownKind.Stack, 2 => BreakdownKind.Players, 3 => BreakdownKind.Weekday, 4 => BreakdownKind.Hour,
        5 => BreakdownKind.Outcome, _ => BreakdownKind.Position,
    };

    public async Task ReloadAsync()
    {
        if (_svc?.Host is not { } host || !_ready) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var f = Filter();
        var by = By;
        _status.Text = "Считаю…";
        try
        {
            // UI-поток в базу не ходит: все три запроса — в фоне, одним заходом.
            var (holes, strength, bars) = await Task.Run(async () =>
            {
                var repo = new AnalyticsRepository(host.Db);
                return (await repo.StartingHandsAsync(f, cts.Token), await repo.HandStrengthAsync(f, cts.Token),
                    await repo.BreakdownAsync(f, by, null, cts.Token));
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _holes = holes;
            _bars = bars;
            RenderMatrix();
            RenderStrength(strength);
            RenderBars();
            long hands = holes.Sum(h => h.Hands);
            _status.Text = hands == 0
                ? "Нет своих раздач под этот фильтр."
                : strength.Count == 0 ? "Сила руки пуста: нужен полный пересчёт базы (rebuild --full) после обновления." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Text = "Ошибка: " + ex.Message;
            _svc.Log("Analytics", ex.ToString());
        }
    }

    private async Task ReloadBarsAsync()
    {
        if (_svc?.Host is not { } host || !_ready) return;
        var f = Filter();
        var by = By;
        try
        {
            _bars = await Task.Run(() => new AnalyticsRepository(host.Db).BreakdownAsync(f, by));
            RenderBars();
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка: " + ex.Message;
            _svc.Log("Analytics", ex.ToString());
        }
    }

    // ── Стартовые руки ──────────────────────────────────────────────────────────

    private UIElement MatrixPane()
    {
        var left = new DockPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        top.Children.Add(new TextBlock { Text = "Показатель:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_metric);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        DockPanel.SetDock(_matrixLegend, Dock.Bottom);
        left.Children.Add(_matrixLegend);
        // Квадратные клетки: карта рук читается как сетка, а не как таблица с вытянутыми строками.
        left.Children.Add(new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = _matrix });
        _matrix.Width = 13 * 54;
        _matrix.Height = 13 * 42;

        var right = new DockPanel { Margin = new Thickness(16, 8, 0, 0), Width = 300 };
        DockPanel.SetDock(_matrixTotal, Dock.Top);
        right.Children.Add(_matrixTotal);
        var lists = new UniformGrid { Rows = 2 };
        lists.Children.Add(Titled("Лучшие руки по сумме bb", _best));
        lists.Children.Add(Titled("Худшие руки по сумме bb", _worst));
        right.Children.Add(lists);

        var grid = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        grid.Children.Add(right);
        grid.Children.Add(left);
        return grid;
    }

    private void RenderMatrix()
    {
        _matrix.Children.Clear();
        if (_holes.Length != 169) return;
        int metric = Math.Max(0, _metric.SelectedIndex);
        const int minHands = 5;

        double Value(HoleClassRow h) => metric switch
        {
            0 => h.Bb100 ?? 0, 1 => h.NetBb, 2 => h.Hands, 3 => h.VpipPct ?? 0, _ => h.PfrPct ?? 0,
        };
        bool diverging = metric <= 1;
        // Шкала — по 90-му перцентилю: одна рука с +3000 bb/100 на десяти раздачах иначе обесцветила бы всю карту.
        var scaleSet = _holes.Where(h => h.Hands >= (metric == 0 ? minHands : 1)).Select(h => Math.Abs(Value(h))).OrderBy(x => x).ToList();
        double scale = metric >= 3 ? 100 : scaleSet.Count == 0 ? 1 : Math.Max(1e-9, scaleSet[(int)((scaleSet.Count - 1) * 0.9)]);

        var ink = (Brush)Application.Current.FindResource("Text");
        var dim = (Brush)Application.Current.FindResource("TextDim");
        for (int cls = 0; cls < 169; cls++)
        {
            var h = _holes[cls];
            double v = Value(h);
            double t = Math.Clamp(Math.Abs(v) / scale, 0, 1);
            // bb/100 на паре раздач — шум: такую клетку не красим, значение показываем приглушённо.
            bool thin = h.Hands == 0 || (metric == 0 && h.Hands < minHands);
            var fill = thin ? Surface : diverging
                ? Mix(Surface, v < 0 ? Minus : Plus, 0.15 + 0.85 * t)
                : Mix(GreenLow, GreenHigh, t);
            // На зелёной клетке — чёрный текст (как в матрице попапа), на остальных — светлый.
            bool darkInk = !thin && !diverging;
            string text = h.Hands == 0 ? "" : metric switch
            {
                0 => Fmt.Sg(h.Bb100, 0), 1 => Fmt.Sg(h.NetBb, 0), 2 => h.Hands.ToString("N0"), 3 => Fmt.P(h.VpipPct), _ => Fmt.P(h.PfrPct),
            };
            var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(new TextBlock { Text = h.Name, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = darkInk ? Brushes.Black : ink, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = darkInk ? Brushes.Black : thin ? dim : ink, HorizontalAlignment = HorizontalAlignment.Center });
            _matrix.Children.Add(new Border
            {
                // Зазор цвета фона между клетками: соседние заливки не сливаются.
                Margin = new Thickness(1), CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(fill), Child = cell,
                ToolTip = h.Hands == 0
                    ? $"{h.Name}: нет раздач"
                    : $"{h.Name}\nРаздач: {h.Hands:N0}\nVPIP {Fmt.P(h.VpipPct)}%   PFR {Fmt.P(h.PfrPct)}%\n" +
                      $"Итог: {Fmt.Sg(h.NetBb, 1)} bb\nbb/100: {Fmt.Sg(h.Bb100, 1)}   EV bb/100: {Fmt.Sg(h.EvBb100, 1)}",
            });
        }

        _matrixLegend.Text = diverging
            ? "Синий — плюс, оранжевый — минус, чем ярче — тем дальше от нуля (шкала по 90-му перцентилю). " +
              "Справа сверху от диагонали — одномастные руки, слева снизу — разномастные. Клетки с выборкой меньше 5 раздач не окрашены."
            : "Зелёная шкала: чем ярче клетка, тем больше значение. Справа сверху от диагонали — одномастные руки, слева снизу — разномастные.";

        long hands = _holes.Sum(x => x.Hands);
        double net = _holes.Sum(x => x.NetBb);
        _matrixTotal.Text = hands == 0 ? "" :
            $"Раздач с известными картами: {hands:N0}\nИтог: {Fmt.Sg(net, 0)} bb   ({Fmt.Sg(net / hands * 100, 1)} bb/100)\n" +
            $"VPIP {Fmt.P(_holes.Sum(x => x.Vpip) * 100.0 / hands)}%   PFR {Fmt.P(_holes.Sum(x => x.Pfr) * 100.0 / hands)}%";
        var played = _holes.Where(x => x.Hands > 0).ToList();
        _best.ItemsSource = played.OrderByDescending(x => x.NetBbX100).Take(12).Select(HoleVm).ToList();
        _worst.ItemsSource = played.OrderBy(x => x.NetBbX100).Take(12).Select(HoleVm).ToList();
    }

    private static object HoleVm(HoleClassRow h) => new
    {
        h.Name, Hands = h.Hands.ToString("N0"), NetBb = Fmt.Sg(h.NetBb, 0), Bb100 = Fmt.Sg(h.Bb100, 0),
    };

    // ── Сила руки ───────────────────────────────────────────────────────────────

    private UIElement StrengthPane()
    {
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), Foreground = (Brush)Application.Current.FindResource("TextDim"),
            Text = "С какой рукой вы пришли на улицу и чем её начали: раздача считается один раз на улицу, по первому вашему ходу. " +
                   "«До вскрытия» и «Выиграл» — итог раздачи целиком, bb/100 — средний результат таких раздач. " +
                   "Пара на борде рукой не считается: A♠K♠ на 7-7-2 — это «оверкарты».",
        };
        // Одна широкая таблица с выбором улицы: три таблицы рядом не помещались, и название руки сжималось до буквы.
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = "Улица:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_street);
        _street.SelectionChanged += (_, _) => ShowStrength();
        var root = new DockPanel();
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(note);
        root.Children.Add(top);
        root.Children.Add(_strength);
        return root;
    }

    private void RenderStrength(List<StrengthRow> rows)
    {
        _strengthRows = rows;
        ShowStrength();
    }

    private void ShowStrength()
    {
        var street = _street.SelectedIndex switch { 1 => Street.Turn, 2 => Street.River, _ => Street.Flop };
        var mine = _strengthRows.Where(r => r.Street == street).ToList();
        long total = mine.Sum(r => r.Hands);
        _strength.ItemsSource = mine.Select(r => new StrengthVm { Row = r, Total = total }).ToList();
    }

    // ── Графики ─────────────────────────────────────────────────────────────────

    private UIElement BarsPane()
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        top.Children.Add(new TextBlock { Text = "Разрез:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_by);
        top.Children.Add(new TextBlock { Text = "Показатель:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        top.Children.Add(_barMetric);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        // Таблица рядом с графиком: точные числа всегда доступны, а не только высота столбика.
        _barGrid.Width = 360;
        DockPanel.SetDock(_barGrid, Dock.Right);
        root.Children.Add(_barGrid);
        root.Children.Add(_plot);
        return root;
    }

    private void RenderBars()
    {
        var plot = _plot.Plot;
        plot.Clear();
        _barGrid.ItemsSource = _bars.Select(b => new BreakdownVm { Row = b }).ToList();
        if (_bars.Count > 0)
        {
            int metric = Math.Max(0, _barMetric.SelectedIndex);
            double[] values = _bars.Select(b => metric switch { 0 => b.Bb100 ?? 0, 1 => b.NetBb, _ => (double)b.Hands }).ToArray();
            var bars = plot.Add.Bars(values);
            for (int i = 0; i < bars.Bars.Count; i++)
            {
                var bar = bars.Bars[i];
                // Знак — и положением относительно нуля, и цветом; число над столбиком — только пока столбиков немного.
                bar.FillColor = ScottPlot.Color.FromHex(metric == 2 || values[i] >= 0 ? "#3987E5" : "#D95926");
                bar.Size = 0.7;
                if (_bars.Count <= 10)
                    bar.Label = metric == 2 ? values[i].ToString("N0") : Fmt.Sg(values[i], metric == 0 ? 1 : 0);
            }
            bars.ValueLabelStyle.ForeColor = ScottPlot.Color.FromHex("#F1F0F8");
            bars.ValueLabelStyle.FontSize = 12;
            plot.Axes.Bottom.SetTicks(Enumerable.Range(0, _bars.Count).Select(i => (double)i).ToArray(), _bars.Select(b => b.Label).ToArray());
            plot.Axes.Bottom.Label.Text = (string)_by.SelectedItem;
            plot.Axes.Left.Label.Text = (string)_barMetric.SelectedItem;
            var zero = plot.Add.HorizontalLine(0);
            zero.Color = ScottPlot.Color.FromHex("#A3A1BD");
            zero.LineWidth = 1;
            plot.Axes.AutoScale();
            plot.Axes.Margins(0.05, 0.15);
        }
        _plot.Refresh();
    }

    // ── Вспомогательное ─────────────────────────────────────────────────────────

    private void BuildColumns()
    {
        foreach (var g in new[] { _best, _worst })
        {
            g.Columns.Add(Col("Рука", "Name", width: 1));
            g.Columns.Add(Col("Раздач", "Hands"));
            g.Columns.Add(Col("Сумма bb", "NetBb", sign: true));
            g.Columns.Add(Col("bb/100", "Bb100", sign: true));
        }
        foreach (var g in new[] { _strength })
        {
            g.Columns.Add(Col("Рука", nameof(StrengthVm.Label), width: 2));
            g.Columns.Add(Col("Раздач", nameof(StrengthVm.Hands), width: 1));
            g.Columns.Add(Col("Доля, %", nameof(StrengthVm.Share), width: 1));
            g.Columns.Add(Col("Ставка", nameof(StrengthVm.Bet), width: 1));
            g.Columns.Add(Col("Чек", nameof(StrengthVm.Check), width: 1));
            g.Columns.Add(Col("Колл", nameof(StrengthVm.Call), width: 1));
            g.Columns.Add(Col("Фолд", nameof(StrengthVm.Fold), width: 1));
            g.Columns.Add(Col("WTSD", nameof(StrengthVm.Wtsd), width: 1));
            g.Columns.Add(Col("Выиграл", nameof(StrengthVm.Won), width: 1));
            g.Columns.Add(Col("bb/100", nameof(StrengthVm.Bb100), sign: true, width: 1));
        }
        _barGrid.Columns.Add(Col("Группа", nameof(BreakdownVm.Label), width: 1));
        _barGrid.Columns.Add(Col("Раздач", nameof(BreakdownVm.Hands)));
        _barGrid.Columns.Add(Col("bb/100", nameof(BreakdownVm.Bb100), sign: true));
        _barGrid.Columns.Add(Col("EV bb/100", nameof(BreakdownVm.EvBb100), sign: true));
        _barGrid.Columns.Add(Col("Сумма bb", nameof(BreakdownVm.NetBb), sign: true));
    }

    private static DataGridTextColumn Col(string header, string path, bool sign = false, double? width = null)
    {
        var c = new DataGridTextColumn { Header = header, Binding = new Binding(path) };
        if (sign) c.ElementStyle = (Style)Application.Current.FindResource("SignCell");
        if (width is { } w) c.Width = new DataGridLength(w, DataGridLengthUnitType.Star);
        return c;
    }

    private static DataGrid Grid() => new() { SelectionMode = DataGridSelectionMode.Single, IsReadOnly = true, AutoGenerateColumns = false };

    private static ComboBox Combo(double width, params string[] items)
    {
        var c = new ComboBox { Width = width, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var i in items) c.Items.Add(i);
        if (items.Length > 0) c.SelectedIndex = 0;
        return c;
    }

    private static UIElement Titled(string title, UIElement content)
    {
        var p = new DockPanel { Margin = new Thickness(0, 0, 8, 8) };
        var t = new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("H"), Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(t, Dock.Top);
        p.Children.Add(t);
        p.Children.Add(content);
        return p;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
