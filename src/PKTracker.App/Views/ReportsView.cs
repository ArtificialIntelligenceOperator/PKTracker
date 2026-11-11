using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PKTracker.App.Replayer;
using PKTracker.Core.Model;
using PKTracker.Data;

namespace PKTracker.App.Views;

/// <summary>Строка любой разбивки отчёта. Числа форматируются здесь, чтобы колонки были просто биндингами.</summary>
public sealed class SliceRowVm
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsTotal { get; init; }
    public bool Tournament { get; init; }
    public SliceStats S { get; init; } = SliceStats.Empty;
    public SliceActions A { get; init; } = SliceActions.Empty;
    public SliceMoney M { get; init; } = SliceMoney.Empty;

    public string HandsText => S.Hands.ToString("N0");
    public string Vpip => Fmt.P(S.VpipPct);
    public string Pfr => Fmt.P(S.PfrPct);
    public string ThreeBet => Fmt.P(A.ThreeBetPct);
    public string Wtsd => Fmt.P(S.WtsdPct);
    public string Wsd => Fmt.P(S.WsdPct);
    public string Wwsf => Fmt.P(S.WwsfPct);
    public string Af => Fmt.N(A.Af, 1);
    public string Agg => Fmt.P(A.AggPct);
    public string Bb100 => Fmt.Sg(S.Bb100, 1);
    public string EvBb100 => Fmt.Sg(S.EvBb100, 1);
    public string Luck => Fmt.Sg(S.LuckBb100, 1);
    public string StdDev => Fmt.N(S.StdDevBb100, 0);
    public string NetText => Tournament ? Fmt.Chips(S.NetWon) : Fmt.Cash(S.NetWon);
    public string AllIns => S.AllInHands.ToString("N0");

    public string TournText => M.Count == 0 ? "—" : M.Count.ToString("N0");
    public string WithHandsText => S.Tournaments.ToString("N0");
    public string EntriesText => M.Entries == 0 ? "—" : M.Entries.ToString("N0");
    public string ItmText => M.Entries == 0 ? "—" : $"{M.Itm} ({Fmt.P(M.ItmPct)}%)";
    public string CostText => M.Entries == 0 ? "—" : Fmt.Cash(M.Cost);
    public string PrizeText => M.Entries == 0 ? "—" : Fmt.Cash(M.Won);
    public string ProfitText => M.Entries == 0 ? "—" : Fmt.SgCash(M.Profit);
    public string RoiText => M.Roi is null ? "—" : Fmt.Sg(M.Roi, 0) + "%";
    public string AbiText => M.Abi is null ? "—" : "$" + M.Abi.Value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Строка вкладки «Следующее действие».</summary>
public sealed class ActionRowVm
{
    public NextActionRow Row { get; init; } = null!;
    public string Label => Row.Label;
    public string DecisionsText => Row.Decisions.ToString("N0");
    public string HandsText => Row.Hands.ToString("N0");
    public string ShareText { get; init; } = "—";
    public string Bb100 => Fmt.Sg(Row.Bb100, 1);
}

internal static class Fmt
{
    public static string P(double? v) => v is null ? "—" : v.Value.ToString("0");
    public static string N(double? v, int d) =>
        v is null ? "—" : double.IsInfinity(v.Value) ? "∞" : v.Value.ToString("F" + d, CultureInfo.InvariantCulture);
    /// <summary>Со знаком: стиль SignCell красит ячейку по первому символу.</summary>
    public static string Sg(double? v, int d) =>
        v is null ? "—" : (v.Value >= 0 ? "+" : "") + v.Value.ToString("F" + d, CultureInfo.InvariantCulture);
    public static string Cash(long cents) => (cents / 100.0).ToString("$#,0.00", CultureInfo.InvariantCulture);
    public static string SgCash(long cents) => (cents / 100.0).ToString("+$#,0.00;-$#,0.00;$0.00", CultureInfo.InvariantCulture);
    public static string Chips(long v) => (v / 100).ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Отчёты со срезами: сверху карточка итогов и график, снизу разбивки, между ними —
/// хлебные крошки активных фильтров.
///
/// Клик по строке не открывает второй отчёт рядом, а добавляет фишку в крошки и пересчитывает
/// тот же: строка разбивки это полтора десятка колонок, и два окна рядом обрезают обе. Взамен
/// фильтры складываются между собой: формат + позиция + полоса стека + действие одновременно.
/// </summary>
public sealed class ReportsView : UserControl
{
    private AppServices? _svc;
    private ReportSlice _slice = new();
    private List<TournamentRow> _tournaments = new();
    private Dictionary<long, string> _formatOf = new();
    private CancellationTokenSource? _cts;

    private readonly ComboBox _kind = new() { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly WrapPanel _chips = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _kpi = new();
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _scope = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScottPlot.WPF.WpfPlot _plot = new();
    private readonly TabControl _upper = Tabs();
    private readonly TabControl _lower = Tabs();
    private TabItem _groupTab = null!;
    private readonly DataGrid _gTourn = NewGrid(), _gStack = NewGrid(), _gPos = NewGrid(), _gMonth = NewGrid(), _gAction = NewGrid(), _gHands = NewGrid();

    public ReportsView()
    {
        _kind.Items.Add("Турниры");
        _kind.Items.Add("Кеш");
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += async (_, _) =>
        {
            // Полосы стека и денежные колонки у кеша другие — срез начинается с нуля.
            _slice = new ReportSlice { Kind = _kind.SelectedIndex == 1 ? GameKind.Cash : GameKind.Mtt };
            await ReloadAsync();
        };

        BuildColumns();
        foreach (var g in new[] { _gTourn, _gStack, _gPos, _gMonth })
            g.MouseDoubleClick += async (_, _) => await DrillAsync(g);
        _gAction.MouseDoubleClick += async (_, _) => await DrillActionAsync();
        _gHands.MouseDoubleClick += (_, _) => OpenReplayer();

        _upper.Items.Add(new TabItem { Header = "Статы", Content = KpiPane() });
        _upper.Items.Add(new TabItem { Header = "График", Content = _plot });
        _upper.SelectionChanged += async (_, e) => { if (e.OriginalSource == _upper && _upper.SelectedIndex == 1) await LoadGraphAsync(); };

        _groupTab = Tab(_lower, "Турниры", _gTourn);
        Tab(_lower, "Размеры стеков", _gStack);
        Tab(_lower, "Позиции", _gPos);
        Tab(_lower, "Месяцы", _gMonth);
        Tab(_lower, "Следующее действие", _gAction);
        Tab(_lower, "Руки", _gHands);
        _lower.SelectionChanged += async (_, e) => { if (e.OriginalSource == _lower) await LoadLowerAsync(); };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(260) , MinHeight = 120 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 140 });

        root.Children.Add(Header());
        var upper = _upper;
        Grid.SetRow(upper, 1);
        root.Children.Add(upper);
        var splitter = new GridSplitter
        {
            Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 4),
            Background = (Brush)Application.Current.FindResource("BorderBrush"),
        };
        Grid.SetRow(splitter, 2);
        root.Children.Add(splitter);
        Grid.SetRow(_lower, 3);
        root.Children.Add(_lower);
        Content = root;
    }

    private static TabControl Tabs() => new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 6, 0, 0),
        ItemContainerStyle = (Style)Application.Current.FindResource("SubTab"),
    };

    private static TabItem Tab(TabControl host, string header, UIElement content)
    {
        var item = new TabItem { Header = header, Content = content };
        host.Items.Add(item);
        return item;
    }

    private UIElement Header()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var reset = new Button { Content = "Сбросить фильтры", Margin = new Thickness(8, 0, 0, 0) };
        reset.Click += async (_, _) => { _slice = new ReportSlice { Kind = _slice.Kind }; await ReloadAsync(); };
        DockPanel.SetDock(_kind, Dock.Left);
        DockPanel.SetDock(reset, Dock.Right);
        DockPanel.SetDock(_scope, Dock.Right);
        bar.Children.Add(_kind);
        bar.Children.Add(reset);
        bar.Children.Add(_scope);
        bar.Children.Add(_chips);

        var stack = new StackPanel();
        stack.Children.Add(bar);
        _note.Style = (Style)Application.Current.FindResource("Dim");
        _note.Margin = new Thickness(0, 0, 0, 4);
        _note.Text = "Двойной клик по строке разбивки — сузить срез; фишку сверху можно снять крестиком.";
        stack.Children.Add(_note);
        return stack;
    }

    private UIElement KpiPane() => new ScrollViewer
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = _kpi,
    };

    public void Attach(AppServices svc) => _svc = svc;

    // ── загрузка ───────────────────────────────────────────────────────────

    public async Task ReloadAsync()
    {
        var host = _svc?.Host;
        if (host == null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var slice = _slice;
        var repo = new SliceReportRepository(host.Db);

        if (_tournaments.Count == 0)
        {
            _tournaments = await Task.Run(() => new ReportRepository(host.Db).TournamentsAsync(ct), ct);
            _formatOf = _tournaments.ToDictionary(t => t.TournamentId, t => t.FormatLabel);
        }

        var stats = await Task.Run(() => repo.SummaryAsync(slice, ct), ct);
        var actions = await Task.Run(() => repo.ActionsAsync(slice, ct), ct);
        if (ct.IsCancellationRequested) return;

        RenderChips();
        RenderKpi(stats, actions);
        _scope.Text = $"{stats.Hands:N0} рук" + (slice.IsTournament ? $" · {stats.Tournaments:N0} турниров" : "");
        _note.Text = slice.IsTournament && slice.NarrowsHands
            ? "Фильтры по позиции, стеку и действию режут только статистику рук: бай-ин, приз и ROI — свойство турнира целиком, и они показаны по турнирам среза без этих фильтров."
            : "Двойной клик по строке разбивки — сузить срез; фишку сверху можно снять крестиком.";

        await LoadLowerAsync();
        if (_upper.SelectedIndex == 1) await LoadGraphAsync();
    }

    private async Task LoadGraphAsync()
    {
        var host = _svc?.Host;
        if (host == null) return;
        var slice = _slice;
        var points = await Task.Run(() => new SliceReportRepository(host.Db).GraphAsync(slice));
        var plot = _plot.Plot;
        plot.Clear();
        MainWindow.ApplyPlotTheme(plot);
        if (points.Count > 0)
        {
            var net = new double[points.Count];
            var ev = new double[points.Count];
            var nonSd = new double[points.Count];
            double a = 0, b = 0, c = 0;
            for (int i = 0; i < points.Count; i++)
            {
                a += points[i].NetBbX100 / 100.0;
                b += points[i].EvBbX100 / 100.0;
                if (!points[i].Showdown) c += points[i].NetBbX100 / 100.0;
                net[i] = a; ev[i] = b; nonSd[i] = c;
            }
            var s1 = plot.Add.Signal(net); s1.LegendText = "Выигрыш, bb"; s1.LineWidth = 2.5f; s1.Color = ScottPlot.Color.FromHex("#9C92F8");
            var s2 = plot.Add.Signal(ev); s2.LegendText = "EV, bb"; s2.LineWidth = 2; s2.Color = ScottPlot.Color.FromHex("#3ECF8E");
            var s3 = plot.Add.Signal(nonSd); s3.LegendText = "Без шоудауна, bb"; s3.LineWidth = 1.5f; s3.Color = ScottPlot.Color.FromHex("#F0708C");
            var zero = plot.Add.HorizontalLine(0); zero.Color = ScottPlot.Color.FromHex("#524F7A"); zero.LinePattern = ScottPlot.LinePattern.Dashed;
            plot.ShowLegend();
            plot.Axes.Bottom.Label.Text = "Раздачи";
            plot.Axes.Left.Label.Text = "bb";
            plot.Axes.AutoScale();
        }
        _plot.Refresh();
    }

    /// <summary>Считается только открытая вкладка: переключение между ними и так перезагружает данные.</summary>
    private async Task LoadLowerAsync()
    {
        var host = _svc?.Host;
        if (host == null) return;
        var repo = new SliceReportRepository(host.Db);
        var slice = _slice;
        int index = _lower.SelectedIndex;

        if (index == 4)
        {
            var rows = await Task.Run(() => repo.NextActionAsync(slice));
            // Частота считается внутри пары (улица, против чего): фолд против рейза — доля всех решений там.
            var opp = rows.GroupBy(r => (r.Street, r.Facing)).ToDictionary(g => g.Key, g => g.Sum(x => x.Decisions));
            _gAction.ItemsSource = rows.Select(r => new ActionRowVm
            {
                Row = r,
                ShareText = opp[(r.Street, r.Facing)] == 0 ? "—" : Fmt.P(r.Decisions * 100.0 / opp[(r.Street, r.Facing)]) + "%",
            }).ToList();
            return;
        }
        if (index == 5)
        {
            var hands = await Task.Run(() => repo.HandsAsync(slice, 500, 0));
            _gHands.ItemsSource = hands.Select(r => new HandVm(r, r.PlayedAt.ToLocalTime(),
                r.Kind == GameKind.Cash ? Fmt.SgCash(r.NetWon) : Fmt.Chips(r.NetWon), r.NetBbX100 / 100.0,
                r.Kind == GameKind.Cash ? Fmt.Cash(r.TotalPot) : (r.TotalPot / 100).ToString("N0"),
                r.EvNetWon is { } ev ? (r.Kind == GameKind.Cash ? Fmt.SgCash(ev) : Fmt.Chips(ev)) : "")).ToList();
            return;
        }

        // У кеша турниров нет: первая вкладка режет по лимитам.
        var by = index switch
        {
            1 => Breakdown.Stack, 2 => Breakdown.Position, 3 => Breakdown.Month,
            _ => slice.IsTournament ? Breakdown.Tournament : Breakdown.Limit,
        };
        var groups = await Task.Run(() => repo.BreakdownAsync(slice, by));
        var grid = index switch { 1 => _gStack, 2 => _gPos, 3 => _gMonth, _ => _gTourn };
        grid.ItemsSource = by == Breakdown.Tournament ? ByFormat(groups, slice) : Plain(groups, by, slice);
        _groupTab.Header = slice.IsTournament ? "Турниры" : "Лимиты";
    }

    /// <summary>
    /// Вкладка «Турниры»: SQL группирует по tournament_id, а в формат («$5.00 Spin &amp; Go») строки
    /// сворачиваются здесь — формат считается в приложении из бай-ина, типа и размера стола.
    /// </summary>
    private List<SliceRowVm> ByFormat(List<SliceGroup> groups, ReportSlice slice)
    {
        var rows = groups
            .GroupBy(g => _formatOf.GetValueOrDefault(long.Parse(g.Key), "без турнира"))
            .Select(g => new SliceRowVm
            {
                Key = g.Key, Label = g.Key, Tournament = slice.IsTournament,
                S = g.Aggregate(SliceStats.Empty, (acc, x) => acc + x.Stats),
                A = g.Aggregate(SliceActions.Empty, (acc, x) => acc + x.Actions),
                M = SliceMoney.Of(_tournaments.Where(t => t.FormatLabel == g.Key)),
            })
            .OrderByDescending(r => r.S.Hands)
            .ToList();
        var shown = rows.Select(r => r.Key).ToHashSet();
        rows.Add(Total(rows, slice, SliceMoney.Of(_tournaments.Where(t => shown.Contains(t.FormatLabel)))));
        return rows;
    }

    private List<SliceRowVm> Plain(List<SliceGroup> groups, Breakdown by, ReportSlice slice)
    {
        var rows = groups
            .Select(g => new SliceRowVm
            {
                Key = g.Key, Tournament = slice.IsTournament, S = g.Stats, A = g.Actions,
                Label = SliceReportRepository.Label(by, g.Key, slice.IsTournament),
            })
            .OrderBy(r => by is Breakdown.Month or Breakdown.Limit ? r.Key.PadLeft(8, '0') : int.Parse(r.Key).ToString("D3"))
            .ToList();
        rows.Add(Total(rows, slice, SliceMoney.Empty));
        return rows;
    }

    private static SliceRowVm Total(List<SliceRowVm> rows, ReportSlice slice, SliceMoney money) => new()
    {
        Label = "Всего", IsTotal = true, Tournament = slice.IsTournament, M = money,
        S = rows.Aggregate(SliceStats.Empty, (acc, x) => acc + x.S),
        A = rows.Aggregate(SliceActions.Empty, (acc, x) => acc + x.A),
    };

    // ── срез ───────────────────────────────────────────────────────────────

    private void RenderChips()
    {
        _chips.Children.Clear();
        foreach (var (label, without) in _slice.Chips())
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var close = new TextBlock
            {
                Text = "✕", Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
            };
            var target = without;
            close.MouseLeftButtonUp += async (_, _) => { _slice = target; await ReloadAsync(); };
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(text);
            inner.Children.Add(close);
            _chips.Children.Add(new Border
            {
                Background = (Brush)Application.Current.FindResource("Bg3"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
                Child = inner,
            });
        }
    }

    /// <summary>Двойной клик по строке разбивки добавляет соответствующую фишку в срез.</summary>
    private async Task DrillAsync(DataGrid grid)
    {
        if (grid.SelectedItem is not SliceRowVm vm || vm.IsTotal) return;
        if (grid == _gTourn)
        {
            if (!_slice.IsTournament) return;   // строки лимитов — отдельное измерение, по ним пока не режем
            var ids = _tournaments.Where(t => t.FormatLabel == vm.Key).Select(t => t.TournamentId).ToHashSet();
            if (ids.Count == 0) return;
            _slice = _slice with { Tournaments = ids, FormatLabel = vm.Label };
        }
        else if (grid == _gStack)
        {
            var band = ReportSlice.StackBands(_slice.IsTournament)[int.Parse(vm.Key)];
            _slice = _slice with { MinStackBb = band.From, MaxStackBb = band.To, StackLabel = band.Label };
        }
        else if (grid == _gPos)
        {
            // Строки «BB» и «BB, хедз-ап» — разные споты, поэтому клик по обычной строке обязан
            // отсечь хедз-ап: иначе срез окажется шире той строки, по которой кликнули.
            var (pos, hu) = SliceReportRepository.PositionKey(vm.Key);
            _slice = hu
                ? _slice with { Position = pos, MinPlayers = 2, MaxPlayers = 2, PlayersLabel = "хедз-ап" }
                : _slice with { Position = pos, MinPlayers = 3, MaxPlayers = null, PlayersLabel = "3+ игроков" };
        }
        else return; // по месяцу пока не режем: срез по датам — отдельное измерение
        await ReloadAsync();
    }

    private async Task DrillActionAsync()
    {
        if (_gAction.SelectedItem is not ActionRowVm vm) return;
        _slice = _slice with { Action = vm.Row.Slice };
        await ReloadAsync();
    }

    private void OpenReplayer()
    {
        if (_gHands.SelectedItem is HandVm vm && _svc is { Host: { } host })
            new ReplayerWindow(host, vm.Row.HandPk, _svc.Settings) { Owner = Window.GetWindow(this) }.Show();
    }

    // ── карточка итогов ────────────────────────────────────────────────────

    private void RenderKpi(SliceStats s, SliceActions a)
    {
        _kpi.Children.Clear();
        bool mtt = _slice.IsTournament;
        var money = mtt ? SliceMoney.Of(TournamentsOfSlice()) : SliceMoney.Empty;

        if (mtt)
        {
            Kpi("Турниров", money.Count.ToString("N0"));
            Kpi("С раздачами", s.Tournaments.ToString("N0"));
            Kpi("Входов", money.Entries.ToString("N0"));
            Kpi("ITM", money.Entries == 0 ? "—" : $"{Fmt.P(money.ItmPct)}%");
            Kpi("Затраты", money.Entries == 0 ? "—" : Fmt.Cash(money.Cost));
            Kpi("Призы", money.Entries == 0 ? "—" : Fmt.Cash(money.Won));
            Kpi("Профит", money.Entries == 0 ? "—" : Fmt.SgCash(money.Profit), sign: true);
            Kpi("ROI", money.Roi is null ? "—" : Fmt.Sg(money.Roi, 0) + "%", sign: true);
            Kpi("ABI", money.Abi is null ? "—" : "$" + money.Abi.Value.ToString("0.##", CultureInfo.InvariantCulture));
        }

        Kpi("Руки", s.Hands.ToString("N0"));
        Kpi("VPIP", Fmt.P(s.VpipPct));
        Kpi("PFR", Fmt.P(s.PfrPct));
        Kpi("3Bet", Fmt.P(a.ThreeBetPct));
        Kpi("WTSD", Fmt.P(s.WtsdPct));
        Kpi("W$SD", Fmt.P(s.WsdPct));
        Kpi("WWSF", Fmt.P(s.WwsfPct));
        Kpi("AF", Fmt.N(a.Af, 1));
        Kpi("AGG %", Fmt.P(a.AggPct));
        Kpi(mtt ? "Фишки" : "Выигрыш", mtt ? Fmt.Chips(s.NetWon) : Fmt.SgCash(s.NetWon), sign: true);
        Kpi("bb/100", Fmt.Sg(s.Bb100, 1), sign: true);
        Kpi("EV bb/100", Fmt.Sg(s.EvBb100, 1), sign: true);
        Kpi("Удача, bb/100", Fmt.Sg(s.LuckBb100, 1), sign: true);
        Kpi("Без шоудауна, bb/100", Fmt.Sg(s.NonSdBb100, 1), sign: true);
        Kpi("На шоудауне, bb/100", Fmt.Sg(s.SdBb100, 1), sign: true);
        Kpi("Ст. откл., bb/100", Fmt.N(s.StdDevBb100, 0));
        Kpi("Олл-инов", s.AllInHands.ToString("N0"));
        if (!mtt) Kpi("Рейк", Fmt.Cash(s.Rake));
    }

    /// <summary>Турниры, к которым относится срез: выбранный формат или все турниры целиком.</summary>
    private IEnumerable<TournamentRow> TournamentsOfSlice() =>
        _slice.Tournaments is { } ids ? _tournaments.Where(t => ids.Contains(t.TournamentId)) : _tournaments;

    private void Kpi(string label, string value, bool sign = false)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 22, 12), MinWidth = 96 };
        box.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.FindResource(sign ? "SignKpi" : "Kpi"),
        });
        box.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        _kpi.Children.Add(box);
    }

    // ── колонки ────────────────────────────────────────────────────────────

    private static DataGrid NewGrid() => new() { SelectionMode = DataGridSelectionMode.Single, IsReadOnly = true, AutoGenerateColumns = false };

    private static DataGridTextColumn Col(string header, string path, bool sign = false, double? width = null)
    {
        var c = new DataGridTextColumn { Header = header, Binding = new Binding(path) };
        if (sign) c.ElementStyle = (Style)Application.Current.FindResource("SignCell");
        if (width is { } w) c.Width = new DataGridLength(w, DataGridLengthUnitType.Star);
        return c;
    }

    private void BuildColumns()
    {
        foreach (var g in new[] { _gTourn, _gStack, _gPos, _gMonth })
        {
            g.Columns.Add(Col(g == _gTourn ? "Формат" : g == _gStack ? "Стек" : g == _gPos ? "Позиция" : "Месяц", nameof(SliceRowVm.Label), width: 1));
            if (g == _gTourn)
            {
                g.Columns.Add(Col("Турниров", nameof(SliceRowVm.TournText)));
                g.Columns.Add(Col("С раздачами", nameof(SliceRowVm.WithHandsText)));
                g.Columns.Add(Col("Входов", nameof(SliceRowVm.EntriesText)));
                g.Columns.Add(Col("ITM", nameof(SliceRowVm.ItmText)));
                g.Columns.Add(Col("Затраты", nameof(SliceRowVm.CostText)));
                g.Columns.Add(Col("Призы", nameof(SliceRowVm.PrizeText)));
                g.Columns.Add(Col("Профит", nameof(SliceRowVm.ProfitText), sign: true));
                g.Columns.Add(Col("ROI", nameof(SliceRowVm.RoiText), sign: true));
                g.Columns.Add(Col("ABI", nameof(SliceRowVm.AbiText)));
            }
            g.Columns.Add(Col("Руки", nameof(SliceRowVm.HandsText)));
            g.Columns.Add(Col("VPIP", nameof(SliceRowVm.Vpip)));
            g.Columns.Add(Col("PFR", nameof(SliceRowVm.Pfr)));
            g.Columns.Add(Col("3Bet", nameof(SliceRowVm.ThreeBet)));
            g.Columns.Add(Col("WTSD", nameof(SliceRowVm.Wtsd)));
            g.Columns.Add(Col("W$SD", nameof(SliceRowVm.Wsd)));
            g.Columns.Add(Col("WWSF", nameof(SliceRowVm.Wwsf)));
            g.Columns.Add(Col("AF", nameof(SliceRowVm.Af)));
            g.Columns.Add(Col("AGG", nameof(SliceRowVm.Agg)));
            g.Columns.Add(Col("Итог", nameof(SliceRowVm.NetText), sign: true));
            g.Columns.Add(Col("bb/100", nameof(SliceRowVm.Bb100), sign: true));
            g.Columns.Add(Col("EV bb/100", nameof(SliceRowVm.EvBb100), sign: true));
            g.Columns.Add(Col("Удача", nameof(SliceRowVm.Luck), sign: true));
            g.Columns.Add(Col("σ bb/100", nameof(SliceRowVm.StdDev)));
            g.Columns.Add(Col("Олл-ины", nameof(SliceRowVm.AllIns)));
        }

        _gAction.Columns.Add(Col("Действие", nameof(ActionRowVm.Label), width: 1));
        _gAction.Columns.Add(Col("Решений", nameof(ActionRowVm.DecisionsText)));
        _gAction.Columns.Add(Col("Доля", nameof(ActionRowVm.ShareText)));
        _gAction.Columns.Add(Col("Руки", nameof(ActionRowVm.HandsText)));
        _gAction.Columns.Add(Col("bb/100", nameof(ActionRowVm.Bb100), sign: true));

        _gHands.Columns.Add(new DataGridTextColumn { Header = "Время", Binding = new Binding("PlayedLocal") { StringFormat = "dd.MM HH:mm:ss" } });
        _gHands.Columns.Add(Col("Стол", "Row.Table", width: 1));
        _gHands.Columns.Add(Col("Ставки", "Row.Stakes"));
        _gHands.Columns.Add(Col("Поз.", nameof(HandVm.PositionText)));
        _gHands.Columns.Add(Col("Карты", "Row.Cards"));
        _gHands.Columns.Add(Col("Итог", nameof(HandVm.NetText), sign: true));
        _gHands.Columns.Add(new DataGridTextColumn
        {
            Header = "bb", Binding = new Binding(nameof(HandVm.NetBb)) { StringFormat = "F1" },
            ElementStyle = (Style)Application.Current.FindResource("SignCell"),
        });
        _gHands.Columns.Add(Col("EV", nameof(HandVm.EvText), sign: true));
        _gHands.Columns.Add(Col("Банк", nameof(HandVm.PotText)));
    }
}
