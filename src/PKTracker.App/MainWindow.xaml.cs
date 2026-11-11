using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PKTracker.App.Replayer;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Data.Import;
using PKTracker.App.Hud;
using PKTracker.App.Views;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.App;

public sealed record SessionVm(DateTime StartLocal, string DurationText, long Hands, int Tables, double Net, double Bb100);

public sealed record TournamentVm(DateTime? StartLocal, string Name, long Hands, int Entries, string BuyInText, string FinishText,
    string WonText, string ProfitText, string? Currency, string DollarEvText, string ChipText, string ChipEvText, int AllIns,
    string Format);

/// <summary>Строка сводки по формату турниров: сколько сыграно этого формата и с каким EV.</summary>
public sealed record TournamentFormatVm(string Format, int Count, int WithHands, int Entries, long Hands, string ItmText, string AvgFinishText,
    string CostText, string WonText, string ProfitText, string RoiText, string DollarEvText, string ChipText, string ChipEvText,
    string ChipLuckText, int AllIns, string FieldText);

public sealed record HandVm(HandRow Row, DateTime PlayedLocal, string NetText, double NetBb, string PotText, string EvText)
{
    public string PositionText => Row.Position == Position.None ? "—" : Row.Position.ToString();
}

public sealed record StatVm(string Title, string Value, long Sample, string Code);

public sealed record NoteVm(string Text);

public sealed record LeakVm(string Title, string HeroText, long HeroSample, string PoolText, long PoolSample, string DiffText, string Verdict, double Score);

public sealed record StatDefVm(string Code, string Title, StatScope Scope, int Version, StatDefinition Def);

public partial class MainWindow : Window
{
    private readonly AppServices _svc;
    private CancellationTokenSource? _importCts;
    private int _handsPage;
    private const int HandsPageSize = 200;
    private PlayerSearchRow? _selectedPlayer;
    private bool _overviewAutoSwitched;

    public MainWindow(AppServices svc)
    {
        _svc = svc;
        InitializeComponent();
        Infrastructure.Win32.ApplyDarkFrame(this);
        SourceInitialized += (_, _) => RegisterHudHotkey();
        LogList.ItemsSource = _svc.LogLines;
        HandsPosition.ItemsSource = new[] { "Все" }.Concat(Enum.GetNames<Position>().Where(p => p != nameof(Position.None))).ToList();
        HandsPosition.SelectedIndex = 0;
        TourneyPositionFilter.ItemsSource = new[] { "Все" }.Concat(Enum.GetNames<Position>().Where(p => p != nameof(Position.None))).ToList();
        TourneyPositionFilter.SelectedIndex = 0;
        Reports.Attach(svc);
        Analytics.Attach(svc);
        _svc.StatusChanged += OnServicesStatus;
        _svc.HandsImported += result => { if (Tabs.SelectedItem == OverviewTab) _ = LoadOverviewAsync(); };
        _svc.Hud.StatusChanged += () => { if (Tabs.SelectedItem == HudTab) LoadHudTab(); };
        LoadSettingsForm();
    }

    private const int HudHotkeyId = 0x4B48;
    private System.Windows.Interop.HwndSource? _hwndSource;

    /// <summary>Глобальная горячая клавиша HUD (§13.16): работает и когда окно трекера скрыто в трей.</summary>
    private void RegisterHudHotkey()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (_hwndSource == null)
        {
            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            _hwndSource.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == Infrastructure.Win32.WM_HOTKEY && wParam.ToInt32() == HudHotkeyId)
                {
                    _svc.Hud.Enabled = !_svc.Hud.Enabled;
                    _svc.Log("Hud", _svc.Hud.Enabled ? "HUD включён (горячая клавиша)" : "HUD скрыт (горячая клавиша)");
                    handled = true;
                }
                return IntPtr.Zero;
            });
        }
        Infrastructure.Win32.UnregisterHotKey(hwnd, HudHotkeyId);
        if (Infrastructure.Win32.ParseHotkey(_svc.Settings.HudHotkey) is { } hk
            && !Infrastructure.Win32.RegisterHotKey(hwnd, HudHotkeyId, hk.Mods, hk.Vk))
            _svc.Log("Hud", $"Горячая клавиша {_svc.Settings.HudHotkey} занята другим приложением");
    }

    /// <summary>Закрытие окна сворачивает в трей: HUD и авто-импорт продолжают работать.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private TrackerHost? Host => _svc.Host;

    private void OnServicesStatus()
    {
        DbStatusText.Text = _svc.DbStatus;
        WatcherStatusText.Text = _svc.WatcherStatus;
        LoadSettingsForm();
        _ = LoadHeroesAsync();
        Tabs_SelectionChanged(Tabs, null);
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs? e)
    {
        if (e != null && e.OriginalSource != Tabs) return;
        if (Host == null) return;
        try
        {
            if (Tabs.SelectedItem == OverviewTab) await LoadOverviewAsync();
            else if (Tabs.SelectedItem == SessionsTab) await LoadSessionsAsync();
            else if (Tabs.SelectedItem == TournamentsTab) await LoadTournamentsAsync();
            else if (Tabs.SelectedItem == ReportsTab) await Reports.ReloadAsync();
            else if (Tabs.SelectedItem == AnalyticsTab) await Analytics.ReloadAsync();
            else if (Tabs.SelectedItem == HandsTab && HandsGrid.ItemsSource == null) await LoadHandsAsync();
            else if (Tabs.SelectedItem == HudTab) { LoadHudTab(); LoadProfileEditor(); ReloadHudProfileList(); }
            else if (Tabs.SelectedItem == LeaksTab) await LoadLeaksAsync();
            else if (Tabs.SelectedItem == ImportTab) { await LoadHeroesAsync(); await LoadImportErrorsAsync(); }
            else if (Tabs.SelectedItem == StatsTab) await LoadStatDefsAsync();
        }
        catch (Exception ex)
        {
            _svc.Log("Db", ex.Message);
        }
    }

    // Все обращения к БД — через Task.Run: UI-поток не должен ждать запрос даже частично (§10.2)
    private static Task<T> Bg<T>(Func<Task<T>> f) => Task.Run(f);

    private static string Money(long amount, GameKind kind) =>
        kind == GameKind.Cash ? (amount / 100.0).ToString("+$#,0.00;-$#,0.00;$0.00") : (amount / 100.0).ToString("+#,0;-#,0;0");

    // ── Обзор ──────────────────────────────────────────────────────────────

    private GameKind OverviewGameKind => OverviewKind.SelectedIndex == 1 ? GameKind.Mtt : GameKind.Cash;

    private async void OverviewKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && Host != null) await LoadOverviewAsync();
    }

    private async void RefreshOverview_Click(object sender, RoutedEventArgs e) => await LoadOverviewAsync();

    private async Task LoadOverviewAsync()
    {
        var host = Host;
        if (host == null) return;
        var kind = OverviewGameKind;
        int gap = _svc.Settings.SessionGapMinutes;
        var reports = new ReportRepository(host.Db);
        var overview = await Bg(() => reports.OverviewAsync(kind, gap));
        // Первый показ: если в выбранном виде игры раздач нет, а в другом есть — переключаемся сами,
        // чтобы турнирный игрок не видел пустой «Кеш» при каждом запуске.
        if (overview.Hands == 0 && !_overviewAutoSwitched)
        {
            _overviewAutoSwitched = true;
            var other = kind == GameKind.Cash ? GameKind.Mtt : GameKind.Cash;
            var alt = await Bg(() => reports.OverviewAsync(other, gap));
            if (alt.Hands > 0)
            {
                OverviewKind.SelectedIndex = other == GameKind.Mtt ? 1 : 0;
                return; // смена выбора сама перезагрузит обзор
            }
        }
        var graph = await Bg(() => reports.GraphAsync(kind));
        var byPos = await Bg(() => reports.ByPositionAsync(kind));
        var byLimit = kind == GameKind.Cash ? await Bg(() => reports.ByLimitAsync()) : new List<GroupRow>();
        var bySize = await Bg(() => reports.ByTableSizeAsync(kind));

        KpiHands.Text = overview.Hands.ToString("N0");
        KpiNetLabel.Text = kind == GameKind.Cash ? "Выигрыш" : "Фишки (chip EV, не деньги)";
        KpiNet.Text = Money(overview.NetWon, kind);
        KpiBb100.Text = overview.Bb100.ToString("F2");
        KpiEvBb100.Text = overview.EvBb100.ToString("F2");
        KpiEvLabel.Text = (kind == GameKind.Cash ? "All-in EV bb/100 (" : "Chip EV bb/100 (") + Money(overview.EvNetWon, kind) + ")";
        KpiRake.Text = kind == GameKind.Cash ? $"${overview.Rake / 100.0:N2}" : "—";
        KpiVpip.Text = $"{overview.VpipPct:F0} / {overview.PfrPct:F0}";
        KpiHours.Text = $"{overview.Hours:F1} / {overview.Sessions}";
        OverviewNote.Text = kind == GameKind.Cash
            ? $"На шоудауне {Money(overview.WonAtSd, kind)}, без шоудауна {Money(overview.WonNonSd, kind)}, " +
              $"удача в олл-инах {Money(overview.NetWon - overview.EvNetWon, kind)} ({overview.AllInHands} олл-инов)"
            : "Турнирные фишки и деньги не смешиваются: денежный результат — во вкладке «Турниры» (из .ots).";

        var plot = NetPlot.Plot;
        plot.Clear();
        ApplyPlotTheme(plot);
        if (graph.Count > 0)
        {
            var evLine = new double[graph.Count];
            double evAcc = 0;
            for (int i = 0; i < graph.Count; i++) { evAcc += graph[i].EvBbX100 / 100.0; evLine[i] = evAcc; }
            var net = new double[graph.Count];
            var sd = new double[graph.Count];
            var nonSd = new double[graph.Count];
            double a = 0, b = 0, c = 0;
            for (int i = 0; i < graph.Count; i++)
            {
                double v = graph[i].NetBbX100 / 100.0;
                a += v;
                if (graph[i].Showdown) b += v; else c += v;
                net[i] = a; sd[i] = b; nonSd[i] = c;
            }
            var s1 = plot.Add.Signal(net); s1.LegendText = "Net won, bb"; s1.LineWidth = 2.5f; s1.Color = ScottPlot.Color.FromHex("#9C92F8");
            var s2 = plot.Add.Signal(sd); s2.LegendText = "На шоудауне"; s2.LineWidth = 1.5f; s2.Color = ScottPlot.Color.FromHex("#5BB8F0");
            var s3 = plot.Add.Signal(nonSd); s3.LegendText = "Без шоудауна"; s3.LineWidth = 1.5f; s3.Color = ScottPlot.Color.FromHex("#F0708C");
            var s4 = plot.Add.Signal(evLine); s4.LegendText = kind == GameKind.Cash ? "All-in EV, bb" : "Chip EV, bb"; s4.LineWidth = 2; s4.Color = ScottPlot.Color.FromHex("#3ECF8E");
            var zero = plot.Add.HorizontalLine(0); zero.Color = ScottPlot.Color.FromHex("#524F7A"); zero.LinePattern = ScottPlot.LinePattern.Dashed;
            plot.ShowLegend();
            plot.Axes.Bottom.Label.Text = "Раздачи";
            plot.Axes.Left.Label.Text = "bb";
            plot.Axes.AutoScale();
        }
        NetPlot.Refresh();

        ByPositionGrid.ItemsSource = byPos;
        ByLimitGrid.ItemsSource = byLimit;
        BySizeGrid.ItemsSource = bySize;
    }

    /// <summary>Тёмно-коричневая тема графика в тон приложению.</summary>
    internal static void ApplyPlotTheme(ScottPlot.Plot plot)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1D1C30");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#171626");
        plot.Axes.Color(ScottPlot.Color.FromHex("#A3A1BD"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2C2A47");
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#25243C");
        plot.Legend.FontColor = ScottPlot.Color.FromHex("#F1F0F8");
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#37355A");
        plot.Legend.Alignment = ScottPlot.Alignment.UpperLeft;
    }

    // ── Сессии / турниры ───────────────────────────────────────────────────

    private async Task LoadSessionsAsync()
    {
        var host = Host;
        if (host == null) return;
        int gap = _svc.Settings.SessionGapMinutes;
        var rows = await Bg(() => new ReportRepository(host.Db).SessionsAsync(gap));
        SessionsGrid.ItemsSource = rows.Select(r => new SessionVm(r.Start.ToLocalTime(),
            $"{(int)r.Duration.TotalHours}:{r.Duration.Minutes:00}", r.Hands, r.Tables, r.NetWon / 100.0, r.Bb100)).ToList();
    }

    private List<TournamentVm> _tournaments = new();
    /// <summary>Раздачи Hero во всех турнирах в порядке игры — источник графика и фильтров.</summary>
    private List<TourneyHandRow> _tourneyHands = new();
    /// <summary>Какие турниры относятся к формату: фильтр графика по выбранной строке сводки.</summary>
    private Dictionary<string, HashSet<long>> _formatTournaments = new();
    private string? _formatFilter;

    private static string TourneyCash(long v) => (v / 100.0).ToString("0.##");
    private static string TourneyChips(long v) => (v / 100).ToString("+#,0;-#,0;0");
    private static string Signed(long cents) => (cents >= 0 ? "+" : "") + TourneyCash(cents);

    private async Task LoadTournamentsAsync()
    {
        var host = Host;
        if (host == null) return;
        var rows = await Bg(() => new ReportRepository(host.Db).TournamentsAsync());
        var evs = await Bg(() => new TournamentEvService(host.Db).ComputeAsync());

        _tournaments = rows.Select(r => new TournamentVm(
            r.Started?.ToLocalTime(), r.Name ?? r.TournamentId.ToString(), r.Hands, r.Entries,
            r.BuyIn is null ? "—" : $"{TourneyCash(r.BuyIn.Value)}+{TourneyCash(r.Fee ?? 0)}",
            r.BestFinish is { } f ? $"{f}{(r.PlayerCount is { } pc ? " / " + pc : "")}" : r.HasResult ? "—" : "нет .ots",
            r.HasResult ? TourneyCash(r.Won) : "—",
            r.HasResult && r.BuyIn != null ? Signed(r.Won - r.Cost) : "—",
            r.Currency,
            DollarEv(r, evs.GetValueOrDefault(r.TournamentId)),
            evs.TryGetValue(r.TournamentId, out var e1) ? TourneyChips(e1.ChipNet) : "—",
            evs.TryGetValue(r.TournamentId, out var e2) ? TourneyChips(e2.ChipEvNet) : "—",
            evs.TryGetValue(r.TournamentId, out var e3) ? e3.AllInHands : 0,
            r.FormatLabel)).ToList();

        _tourneyHands = await Bg(() => new ReportRepository(host.Db).TournamentHandsAsync());
        _formatTournaments = rows.GroupBy(r => r.FormatLabel)
            .ToDictionary(g => g.Key, g => g.Select(r => r.TournamentId).ToHashSet());

        var formats = rows.GroupBy(r => r.FormatLabel).Select(g => FormatVm(g.Key, g.ToList(), evs))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Format, StringComparer.Ordinal).ToList();

        // Присвоение ItemsSource сбрасывает выделение (и через SelectionChanged — фильтр), поэтому восстанавливаем сами
        var wanted = _formatFilter;
        FormatsGrid.ItemsSource = formats;
        _formatFilter = wanted;
        if (wanted != null)
        {
            var keep = formats.FirstOrDefault(x => x.Format == wanted);
            if (keep == null) _formatFilter = null;
            else FormatsGrid.SelectedItem = keep;
        }
        ApplyTournamentFilter();

        string DollarEv(TournamentRow r, TournamentEv? e)
        {
            if (!r.HasResult || r.BuyIn == null || e?.DollarEvAdjustment is not { } adj) return "—";
            double v = (r.Won - r.Cost + adj) / 100.0;
            return (v >= 0 ? "+" : "") + v.ToString("0.##") + (e.DollarMethod == "ICM" ? "" : " ≈");
        }

        var withResult = rows.Where(r => r.HasResult && r.BuyIn != null && r.Currency != "TTS").ToList();
        long cost = withResult.Sum(r => r.Cost), won = withResult.Sum(r => r.Won);
        int itm = withResult.Count(r => r.Won > 0);
        double evAdj = withResult.Sum(r => evs.GetValueOrDefault(r.TournamentId)?.DollarEvAdjustment ?? 0);
        var finishes = withResult.Where(r => r.BestFinish != null && r.PlayerCount is > 0).ToList();
        string avgFinish = finishes.Count == 0 ? "—"
            : $"{finishes.Average(r => r.BestFinish!.Value):F0} (в среднем топ-{finishes.Average(r => 100.0 * r.BestFinish!.Value / r.PlayerCount!.Value):F0}% поля)";
        long bounties = withResult.Sum(r => r.Bounty);
        long chipLuck = evs.Values.Sum(e => e.ChipLuck);
        TournamentSummaryText.Text = withResult.Count == 0
            ? $"Турниров с раздачами: {rows.Count}. Удача в олл-инах (факт − Chip EV): {TourneyChips(chipLuck)} фишек. " +
              "Для денежного результата и $EV загрузите итоги (.ots) кнопкой выше — в HH нет ни призовых, ни баунти, ни места (§0.5)."
            : $"С итогами: {withResult.Count}  Затраты: {TourneyCash(cost)}  Выигрыш: {TourneyCash(won)}  Профит: {TourneyCash(won - cost)}  " +
              $"ROI: {(cost > 0 ? (won - cost) * 100.0 / cost : 0):F1}%  ITM: {itm * 100.0 / withResult.Count:F1}%  " +
              $"Средний финиш: {avgFinish}  Баунти: {TourneyCash(bounties)}  " +
              $"$EV: {TourneyCash(won - cost + (long)evAdj)} (удача {TourneyCash((long)-evAdj)}).  Удача в фишках: {TourneyChips(chipLuck)}. " +
              "$EV: ICM для SNG, «≈» — линейная оценка для MTT; для Spin & Go (100% первому) считается только Chip EV. " +
              "Турниры с призом-билетом TTS в ROI не входят.";
    }

    /// <summary>Сводка по одному формату: фишки и Chip EV — по всем турнирам, деньги и ROI — только по тем, где есть .ots.</summary>
    private static TournamentFormatVm FormatVm(string format, List<TournamentRow> g, Dictionary<long, TournamentEv> evs)
    {
        long chip = 0, chipEv = 0;
        int allIns = 0, withEv = 0;
        foreach (var r in g)
        {
            if (!evs.TryGetValue(r.TournamentId, out var e)) continue;
            withEv++;
            chip += e.ChipNet;
            chipEv += e.ChipEvNet;
            allIns += e.AllInHands;
        }

        // Билетные турниры (TTS): призы в .ots нулевые, деньги по ним считать нельзя
        var money = g.Where(r => r.HasResult && r.BuyIn != null && r.Currency != "TTS").ToList();
        long cost = money.Sum(r => r.Cost), won = money.Sum(r => r.Won);
        int itm = money.Count(r => r.Won > 0);

        var withAdj = money.Where(r => evs.GetValueOrDefault(r.TournamentId)?.DollarEvAdjustment != null).ToList();
        string dollarEv = "—";
        if (withAdj.Count > 0)
        {
            double adj = money.Sum(r => evs.GetValueOrDefault(r.TournamentId)?.DollarEvAdjustment ?? 0);
            bool exact = withAdj.All(r => evs[r.TournamentId].DollarMethod == "ICM");
            dollarEv = Signed(won - cost + (long)adj) + (exact ? "" : " ≈");
        }

        var finishes = g.Where(r => r.BestFinish != null).ToList();
        var ofField = finishes.Where(r => r.PlayerCount is > 0).ToList();
        var fields = g.Where(r => r.PlayerCount is > 0).ToList();

        return new TournamentFormatVm(
            format,
            g.Count,
            g.Count(r => r.Hands > 0),
            g.Sum(r => r.Entries == 0 && r.Hands > 0 ? 1 : r.Entries),
            g.Sum(r => r.Hands),
            money.Count == 0 ? "—" : $"{itm * 100.0 / money.Count:F0}% ({itm}/{money.Count})",
            finishes.Count == 0 ? "—"
                : $"{finishes.Average(r => r.BestFinish!.Value):F1}" +
                  (ofField.Count == 0 ? "" : $" (топ-{ofField.Average(r => 100.0 * r.BestFinish!.Value / r.PlayerCount!.Value):F0}%)"),
            money.Count == 0 ? "—" : TourneyCash(cost),
            money.Count == 0 ? "—" : TourneyCash(won),
            money.Count == 0 ? "—" : Signed(won - cost),
            cost == 0 ? "—" : ((won - cost) * 100.0 / cost).ToString("+0.0;-0.0;0.0") + "%",
            dollarEv,
            withEv == 0 ? "—" : TourneyChips(chip),
            withEv == 0 ? "—" : TourneyChips(chipEv),
            withEv == 0 ? "—" : TourneyChips(chip - chipEv),
            allIns,
            fields.Count == 0 ? "—" : $"{fields.Average(r => r.PlayerCount!.Value):F0}");
    }

    private void ApplyTournamentFilter()
    {
        var shown = _formatFilter is null ? _tournaments : _tournaments.Where(t => t.Format == _formatFilter).ToList();
        TournamentsGrid.ItemsSource = shown;
        TournamentListHeader.Text = _formatFilter is null
            ? $"Турниры: {_tournaments.Count}"
            : $"Турниры формата «{_formatFilter}»: {shown.Count}";
        DrawTournamentChart();
    }

    /// <summary>
    /// График: фишки нарастающим итогом и вторая линия — тот же счёт,
    /// но в олл-инах вместо факта берётся матожидание по эквити (Chip EV). Считается по раздачам,
    /// а не по турнирам, и подчиняется фильтрам (формат, игроков за столом, позиция, стек).
    /// </summary>
    private void DrawTournamentChart()
    {
        if (TourneyPlot == null) return;
        var g = TourneyGraphBuilder.Build(_tourneyHands, CurrentGraphFilter());
        var plot = TourneyPlot.Plot;
        plot.Clear();
        ApplyPlotTheme(plot);

        string scope = _formatFilter is null ? "все форматы" : _formatFilter;
        string filters = FilterSummary();
        if (g.Hands > 0)
        {
            // Scatter, а не Signal: раздач может быть всего несколько — точки должны быть видны
            var xs = new double[g.Hands];
            for (int i = 0; i < g.Hands; i++) xs[i] = i + 1;
            float marker = g.Hands > 200 ? 0 : g.Hands > 40 ? 2 : g.Hands == 1 ? 8 : 4;
            var s1 = plot.Add.Scatter(xs, g.Chips); s1.LegendText = "Фишки (факт)"; s1.LineWidth = 2.5f; s1.MarkerSize = marker; s1.Color = ScottPlot.Color.FromHex("#9C92F8");
            var s2 = plot.Add.Scatter(xs, g.ChipEv); s2.LegendText = "Chip EV"; s2.LineWidth = 2; s2.MarkerSize = marker; s2.Color = ScottPlot.Color.FromHex("#3ECF8E");
            var zero = plot.Add.HorizontalLine(0); zero.Color = ScottPlot.Color.FromHex("#524F7A"); zero.LinePattern = ScottPlot.LinePattern.Dashed;
            plot.ShowLegend();
            plot.Axes.Bottom.Label.Text = "Раздачи";
            plot.Axes.Left.Label.Text = "Фишки";
            plot.Axes.AutoScale();
            TourneyChartNote.Text = $"Фишки и Chip EV нарастающим итогом: {scope}{filters} — раздач {g.Hands:N0} " +
                                    $"из {g.Tournaments} турниров, фишки {TourneyChips(g.ChipNet)}, Chip EV {TourneyChips(g.ChipEvNet)}, " +
                                    $"удача {TourneyChips(g.Luck)}, олл-инов {g.AllIns}";
        }
        else
        {
            TourneyChartNote.Text = $"Фишки и Chip EV: {scope}{filters} — подходящих раздач нет. " +
                                    (_tourneyHands.Count == 0
                                        ? "Chip EV считается по hand history: загрузите её во вкладке «Импорт» (папка клиента, обычно C:\\PokerKing\\handHistory\\<ник>)."
                                        : "Снимите часть фильтров или выберите формат, по которому есть раздачи (колонка «С раздачами»).");
        }
        TourneyPlot.Refresh();
    }

    /// <summary>Фильтр графика из выбранного формата и трёх комбобоксов.</summary>
    private TourneyGraphFilter CurrentGraphFilter()
    {
        HashSet<long>? tourneys = _formatFilter != null && _formatTournaments.TryGetValue(_formatFilter, out var ids) ? ids : null;
        (int? minP, int? maxP) = TourneyPlayersFilter?.SelectedIndex switch
        {
            1 => ((int?)2, (int?)2),
            2 => (3, 3),
            3 => (4, 6),
            4 => (7, null),
            _ => (null, null),
        };
        (double? minBb, double? maxBb) = TourneyStackFilter?.SelectedIndex switch
        {
            1 => ((double?)null, (double?)10),
            2 => (10, 25),
            3 => (25, 50),
            4 => (50, null),
            _ => (null, null),
        };
        Position? pos = TourneyPositionFilter?.SelectedItem is string p && p != "Все" && Enum.TryParse<Position>(p, out var parsed)
            ? parsed : null;
        return new TourneyGraphFilter(tourneys, minP, maxP, pos, minBb, maxBb);
    }

    private string FilterSummary()
    {
        var parts = new List<string>();
        if (TourneyPlayersFilter?.SelectedIndex > 0) parts.Add($"игроков {((ComboBoxItem)TourneyPlayersFilter.SelectedItem).Content}");
        if (TourneyPositionFilter?.SelectedItem is string p && p != "Все") parts.Add(p);
        if (TourneyStackFilter?.SelectedIndex > 0) parts.Add($"стек {((ComboBoxItem)TourneyStackFilter.SelectedItem).Content}");
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
    }

    private void TourneyFilter_Changed(object sender, SelectionChangedEventArgs e) => DrawTournamentChart();

    private void TourneyFiltersReset_Click(object sender, RoutedEventArgs e)
    {
        TourneyPlayersFilter.SelectedIndex = 0;
        TourneyStackFilter.SelectedIndex = 0;
        TourneyPositionFilter.SelectedIndex = 0;
        DrawTournamentChart();
    }

    private void FormatsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatsGrid.ItemsSource == null) return;
        _formatFilter = (FormatsGrid.SelectedItem as TournamentFormatVm)?.Format;
        ApplyTournamentFilter();
    }

    private void FormatsReset_Click(object sender, RoutedEventArgs e)
    {
        FormatsGrid.UnselectAll();
        _formatFilter = null;
        ApplyTournamentFilter();
    }

    private async void TournamentsRefresh_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadTournamentsAsync(); }
        catch (Exception ex) { TournamentLoadStatus.Text = "Ошибка: " + ex.Message; _svc.Log("Db", ex.Message); }
    }

    private void TournamentBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с итогами турниров (*.ots)" };
        var current = TournamentFolderBox.Text.Trim();
        if (current.Length > 0 && System.IO.Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == true) TournamentFolderBox.Text = dlg.FolderName;
    }

    /// <summary>Загрузка истории турниров: только *.ots из папки и подпапок, раздачи не трогаем (§6.1).</summary>
    private async void TournamentLoad_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        var folder = TournamentFolderBox.Text.Trim();
        if (host == null) { TournamentLoadStatus.Text = "База ещё не готова — см. статус внизу окна."; return; }
        if (folder.Length == 0 || !System.IO.Directory.Exists(folder)) { TournamentLoadStatus.Text = "Папка не найдена."; return; }

        TournamentLoadButton.IsEnabled = false;
        var progress = new Progress<ImportProgress>(p =>
        {
            TournamentProgressBar.Maximum = Math.Max(1, p.FilesTotal);
            TournamentProgressBar.Value = p.FilesDone;
            TournamentLoadStatus.Text = $"Файлов {p.FilesDone}/{p.FilesTotal}" + (p.Errors > 0 ? $"  ошибок {p.Errors}" : "")
                                        + (p.CurrentFile is { } f ? "  " + f : "");
        });
        try
        {
            var rep = await Task.Run(() => host.CreateFileImporter().ImportTournamentsFolderAsync(folder, progress));
            _svc.Settings.TournamentSummaryFolder = folder;
            _svc.Settings.Save();
            TournamentProgressBar.Value = TournamentProgressBar.Maximum;
            TournamentLoadStatus.Text = rep.Files == 0
                ? "В папке нет файлов .ots. Итоги турниров клиент кладёт рядом с hand history, имя вида «TS20260919 T35989335 … .ots»."
                : $"Загружено турниров: {rep.Tournaments} из {rep.Files} файлов за {rep.Elapsed.TotalSeconds:F1} c"
                  + (rep.Errors > 0 ? $", ошибок {rep.Errors}" : "")
                  + (host.HeroNames.Count == 0
                      ? ". Добавьте свой ник во вкладке «Импорт»: без Hero статистику по себе посчитать не из чего."
                      : "")
                  + (rep.Messages.Count > 0 ? "\n" + string.Join("\n", rep.Messages.Take(5)) : "");
            _svc.Log("Import", TournamentLoadStatus.Text);
            await LoadTournamentsAsync();
        }
        catch (Exception ex)
        {
            TournamentLoadStatus.Text = "Ошибка: " + ex.Message;
            _svc.Log("Import", ex.ToString());
        }
        finally
        {
            TournamentLoadButton.IsEnabled = true;
        }
    }

    // ── Раздачи ────────────────────────────────────────────────────────────

    private async void HandsShow_Click(object sender, RoutedEventArgs e) { _handsPage = 0; await LoadHandsAsync(); }
    private async void HandsPrev_Click(object sender, RoutedEventArgs e) { if (_handsPage > 0) { _handsPage--; await LoadHandsAsync(); } }
    private async void HandsNext_Click(object sender, RoutedEventArgs e) { _handsPage++; await LoadHandsAsync(); }

    private async Task LoadHandsAsync()
    {
        var host = Host;
        if (host == null) return;
        GameKind? kind = HandsKind.SelectedIndex switch { 1 => GameKind.Cash, 2 => GameKind.Mtt, _ => null };
        Position? pos = HandsPosition.SelectedIndex > 0 ? Enum.Parse<Position>((string)HandsPosition.SelectedItem) : null;
        int offset = _handsPage * HandsPageSize;
        var filterText = HandsFilterBox.Text.Trim();
        List<HandRow> rows;
        string scanInfo = "";
        if (filterText.Length > 0 || pos != null)
        {
            // Позиция добавляется к фильтру: один проход, одна семантика
            var expr = string.Join(" && ", new[] { filterText.Length > 0 ? $"({filterText})" : null, pos != null ? $"Position == \"{pos}\"" : null }
                .Where(x => x != null));
            Func<Engine.HandPlayerFacts, bool> filter;
            try { filter = StatCompiler.CompileHandFilter(expr); }
            catch (StatCompileException ex) { HandsPageText.Text = "Ошибка в фильтре: " + ex.InnerException?.Message; return; }
            var (found, scanned) = await Bg(() => new ReportRepository(host.Db).HeroHandsFilteredAsync(filter, kind, HandsPageSize, offset));
            rows = found;
            scanInfo = $", просмотрено {scanned:N0} рук";
        }
        else rows = await Bg(() => new ReportRepository(host.Db).HeroHandsAsync(kind, pos, HandsPageSize, offset));
        HandsGrid.ItemsSource = rows.Select(r => new HandVm(r, r.PlayedAt.ToLocalTime(), Money(r.NetWon, r.Kind), r.NetBbX100 / 100.0,
            r.Kind == GameKind.Cash ? $"${r.TotalPot / 100.0:0.00}" : (r.TotalPot / 100).ToString(),
            r.EvNetWon is { } ev ? Money(ev, r.Kind) : "")).ToList();
        HandsPageText.Text = rows.Count == 0 ? $"стр. {_handsPage + 1}: ничего не найдено{scanInfo}" : $"стр. {_handsPage + 1} ({offset + 1}–{offset + rows.Count}){scanInfo}";
    }

    private async void HandsFilterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _handsPage = 0;
        await LoadHandsAsync();
    }

    private async void HandsFilterReset_Click(object sender, RoutedEventArgs e)
    {
        HandsFilterBox.Clear();
        _handsPage = 0;
        await LoadHandsAsync();
    }

    private void HandsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HandsGrid.SelectedItem is HandVm vm && Host != null)
            new ReplayerWindow(Host, vm.Row.HandPk, _svc.Settings) { Owner = this }.Show();
    }

    // ── Игроки ─────────────────────────────────────────────────────────────

    private void PlayerSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PlayerSearch_Click(sender, e);
    }

    private async void PlayerSearch_Click(object sender, RoutedEventArgs e)
    {
        try { await SearchPlayersAsync(); }
        catch (Exception ex) { _svc.Log("Players", "Поиск игроков: " + ex.Message); }
    }

    /// <summary>Перечитать список игроков; <paramref name="reselect"/> — кого оставить выбранным.</summary>
    private async Task SearchPlayersAsync(long? reselect = null)
    {
        var host = Host;
        if (host == null) return;
        var q = PlayerSearchBox.Text.Trim();
        var rows = await Bg(() => new ReportRepository(host.Db).SearchPlayersAsync(q));
        var notes = await Bg(() => host.Notes.GetAllAsync());
        PlayersGrid.ItemsSource = rows.Select(r =>
        {
            var n = notes.GetValueOrDefault(r.PlayerId);
            var m = n?.Marker is { } id ? _svc.Markers.Find(id) : null;
            return new PlayerListItem(r, m != null ? PlayerMarkerMenu.Brush(m.Color) : System.Windows.Media.Brushes.Transparent, m?.Title, n?.Caption);
        }).ToList();
        if (reselect is { } id && PlayersGrid.Items.OfType<PlayerListItem>().FirstOrDefault(i => i.Row.PlayerId == id) is { } again)
        {
            PlayersGrid.SelectedItem = again;
            PlayersGrid.ScrollIntoView(again);
        }
    }

    /// <summary>Строка списка игроков с меткой: цвет, тип и подпись — как над столом.</summary>
    public sealed record PlayerListItem(PlayerSearchRow Row, System.Windows.Media.Brush MarkerColor, string? MarkerTitle, string? Caption)
    {
        public string Nickname => Row.Nickname;
        public long Hands => Row.Hands;
    }

    private async void PlayersGrid_RightClick(object sender, MouseButtonEventArgs e)
    {
        // Игрок — по строке под курсором: правый клик строку не выделяет, и метка ушла бы ранее выбранному.
        if (Host is not { } host) return;
        if (ItemsControl.ContainerFromElement(PlayersGrid, e.OriginalSource as DependencyObject) is not DataGridRow { Item: PlayerListItem item }) return;
        PlayersGrid.SelectedItem = item;
        long id = item.Row.PlayerId;
        try
        {
            var menu = await PlayerMarkerMenu.BuildAsync(_svc, host, id, item.Row.Nickname, async () =>
            {
                try { await SearchPlayersAsync(id); }
                catch (Exception ex) { _svc.Log("Players", "Список игроков: " + ex.Message); }
                _svc.Hud.RefreshAll();
            });
            menu.IsOpen = true;
        }
        catch (Exception ex) { _svc.Log("Players", "Меню метки: " + ex.Message); }
    }

    private async void PlayersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPlayer = (PlayersGrid.SelectedItem as PlayerListItem)?.Row;
        await LoadPlayerAsync();
    }

    private async void PlayerKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadPlayerAsync();
    }

    private async Task LoadPlayerAsync()
    {
        var host = Host;
        var p = _selectedPlayer;
        if (host == null || p == null) return;
        var kind = PlayerKind.SelectedIndex == 1 ? GameKind.Mtt : GameKind.Cash;
        PlayerTitle.Text = p.Nickname;
        var set = await Bg(() => host.Hud.GetAsync(p.PlayerId, kind));
        PlayerStatsGrid.ItemsSource = host.Stats.Select(st =>
        {
            var (v, n) = set.Evaluate(st.Code);
            return new StatVm(st.Definition.Title, v is { } x ? x.ToString("F" + Math.Max(st.Definition.Format.Decimals, st.Code == "net_bb" ? 0 : 1)) : "–", n, st.Code);
        }).ToList();
        var notes = await Bg(() => new ReportRepository(host.Db).NotesAsync(p.PlayerId));
        NotesList.ItemsSource = notes.Select(n => new NoteVm($"{n.CreatedAt.ToLocalTime():dd.MM.yy HH:mm}  {n.Body}")).ToList();
    }

    private async void PlayerStatsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var host = Host;
        var p = _selectedPlayer;
        if (host == null || p == null || PlayerStatsGrid.SelectedItem is not StatVm vm) return;
        var stat = host.Stats.FirstOrDefault(s => s.Code == vm.Code);
        if (stat == null) return;
        var kind = PlayerKind.SelectedIndex == 1 ? GameKind.Mtt : GameKind.Cash;
        var set = await Bg(() => host.Hud.GetAsync(p.PlayerId, kind));
        var data = await Bg(() => new StatPopupService(host.Db).LoadAsync(p.PlayerId, stat, kind));
        var win = new Hud.StatDetailWindow(host, overlay: false) { Owner = this };
        win.ShowData(p.Nickname, stat, set, data, kind);
        win.Show();
    }

    private async void MergePlayer_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        var p = _selectedPlayer;
        var aliasNick = MergeBox.Text.Trim();
        if (host == null || p == null || aliasNick.Length == 0) return;
        var alias = await Bg(() => host.Hud.FindPlayerAsync(aliasNick));
        if (alias == null) { MessageBox.Show($"Игрок «{aliasNick}» не найден.", "Склейка"); return; }
        if (alias.PlayerId == p.PlayerId) { MessageBox.Show("Это уже один и тот же игрок.", "Склейка"); return; }
        if (MessageBox.Show($"Склеить «{alias.Nickname}» с «{p.Nickname}»? Раздачи, статы и заметки перейдут к «{p.Nickname}». Отменить нельзя.",
                "Склейка", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Task.Run(() => new PlayerIdentityService(host.Db).MergeAsync(p.PlayerId, alias.PlayerId));
        host.Hud.Invalidate(new[] { p.PlayerId, alias.PlayerId });
        _svc.Log("Db", $"Склеены игроки: {alias.Nickname} → {p.Nickname}");
        MergeBox.Clear();
        await LoadPlayerAsync();
    }

    private async void AddNote_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        var p = _selectedPlayer;
        var text = NoteBox.Text.Trim();
        if (host == null || p == null || text.Length == 0) return;
        await Task.Run(() => new ReportRepository(host.Db).AddNoteAsync(p.PlayerId, text, null));
        NoteBox.Clear();
        await LoadPlayerAsync();
    }

    // ── Ликфайндер ─────────────────────────────────────────────────────────

    private async void LeaksKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && Host != null) await LoadLeaksAsync();
    }

    private async Task LoadLeaksAsync()
    {
        var host = Host;
        if (host == null) return;
        var kind = LeaksKind.SelectedIndex == 1 ? GameKind.Mtt : GameKind.Cash;
        var leaks = await Bg(() => LeakFinder.ComputeAsync(host, kind));
        var rows = leaks.Select(l => new LeakVm(l.Title, l.Hero.ToString("F" + l.Decimals), l.HeroSample, l.Pool.ToString("F" + l.Decimals),
            l.PoolSample, FormatDiff(l.Diff, l.Decimals), l.Verdict, l.Score)).ToList();
        LeaksGrid.ItemsSource = rows;

        static string FormatDiff(double diff, int dec)
        {
            double rounded = Math.Round(diff, dec);
            return rounded == 0 ? "0" : (rounded > 0 ? "+" : "") + rounded.ToString("F" + dec);
        }
    }

    // ── HUD ────────────────────────────────────────────────────────────────

    private sealed record ProfileItem(string Code, string Text)
    {
        public override string ToString() => Text;
    }

    private const string BreakText = "── новая строка ──";

    private GameKind ProfileGameKind => ProfileKind.SelectedIndex == 1 ? GameKind.Mtt : GameKind.Cash;

    private void LoadProfileEditor()
    {
        var host = Host;
        if (host == null) return;
        var codes = (ProfileGameKind == GameKind.Cash ? _svc.Settings.CashStats : _svc.Settings.MttStats)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var byCode = host.Stats.ToDictionary(s => s.Code);
        ProfileSelected.Items.Clear();
        foreach (var c in codes)
        {
            if (c == "|") ProfileSelected.Items.Add(new ProfileItem("|", BreakText));
            else if (byCode.TryGetValue(c, out var st)) ProfileSelected.Items.Add(new ProfileItem(c, $"{st.Definition.Title}  ({c})"));
        }
        ProfileAvailable.Items.Clear();
        foreach (var st in host.Stats.Where(s => s.Definition.Value == null || s.Code == "hands").Where(s => !codes.Contains(s.Code)))
            ProfileAvailable.Items.Add(new ProfileItem(st.Code, $"{st.Definition.Title}  ({st.Code})"));
        ProfileStatus.Text = "";
        UpdateProfilePreview();
    }

    private void UpdateProfilePreview()
    {
        var host = Host;
        ProfilePreview.Children.Clear();
        if (host == null) return;
        ProfilePreview.Children.Add(new TextBlock { Text = "Nickname  352", FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)FindResource("Text") });
        var row = new WrapPanel { MaxWidth = 220 };
        ProfilePreview.Children.Add(row);
        foreach (ProfileItem item in ProfileSelected.Items)
        {
            if (item.Code == "|") { row = new WrapPanel { MaxWidth = 220 }; ProfilePreview.Children.Add(row); continue; }
            var def = host.Stats.First(s => s.Code == item.Code).Definition;
            var color = def.Colors.Count > 1 ? def.Colors[1].Color : "#F1F0F8";
            row.Children.Add(new TextBlock
            {
                Text = $"{def.Title} {(item.Code == "hands" ? "352" : "27")}", Margin = new Thickness(0, 0, 6, 0), FontSize = 11,
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!,
            });
        }
    }

    // ── Раскладка HUD (сетки и попапы) ───────────────────────────────────────

    private readonly HudProfileStore _hudProfiles = new();
    /// <summary>Пока список наполняется, SelectionChanged срабатывать не должен — иначе он перезапишет настройку.</summary>
    private bool _fillingProfiles;

    private void ReloadHudProfileList()
    {
        _fillingProfiles = true;
        HudProfileBox.Items.Clear();
        HudProfileBox.Items.Add(BuiltinProfileItem);
        foreach (var name in _hudProfiles.List()) HudProfileBox.Items.Add(name);
        // Старый плоский список статов остаётся доступным выбором, иначе редактор под ним
        // и MCP-инструмент set_hud_profile ни на что не влияли бы.
        HudProfileBox.Items.Add(HudProfile.LegacyName);
        HudProfileBox.Items.Add(HiddenProfileItem);
        var kind = ProfileGameKind;
        string? current = kind == GameKind.Cash ? _svc.Settings.CashProfile : _svc.Settings.MttProfile;
        HudProfileBox.SelectedItem = current switch
        {
            null or "" => BuiltinProfileItem,
            ProfileBadge.Hidden => HiddenProfileItem,
            _ => current,
        };
        if (HudProfileBox.SelectedItem == null) HudProfileBox.SelectedIndex = 0;
        _fillingProfiles = false;
    }

    private const string BuiltinProfileItem = "[встроенная]";
    private const string HiddenProfileItem = "[HUD скрыт]";

    private void HudProfileBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingProfiles || !IsLoaded) return;
        string? name = HudProfileBox.SelectedItem as string;
        if (name == BuiltinProfileItem) name = null;
        else if (name == HiddenProfileItem) name = ProfileBadge.Hidden;
        if (ProfileGameKind == GameKind.Cash) _svc.Settings.CashProfile = name;
        else _svc.Settings.MttProfile = name;
        _svc.Settings.Save();
        _svc.Hud.ReloadProfile();
    }

    private void HudEditor_Click(object sender, RoutedEventArgs e)
    {
        var host = _svc.Host;
        if (host == null)
        {
            MessageBox.Show("Нет подключения к базе: список статов недоступен.", "Редактор HUD");
            return;
        }
        var profile = _svc.Hud.Profile(ProfileGameKind);
        new HudEditorWindow(host.Hud.Stats, profile, () =>
        {
            ReloadHudProfileList();
            _svc.Hud.ReloadProfile();
        }) { Owner = this }.Show();
    }

    private void ProfileKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        LoadProfileEditor();
        ReloadHudProfileList();
    }

    private void ProfileAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileAvailable.SelectedItem is not ProfileItem item) return;
        ProfileAvailable.Items.Remove(item);
        int at = ProfileSelected.SelectedIndex >= 0 ? ProfileSelected.SelectedIndex + 1 : ProfileSelected.Items.Count;
        ProfileSelected.Items.Insert(at, item);
        ProfileSelected.SelectedItem = item;
        UpdateProfilePreview();
    }

    private void ProfileRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileSelected.SelectedItem is not ProfileItem item) return;
        ProfileSelected.Items.Remove(item);
        if (item.Code != "|") ProfileAvailable.Items.Add(item);
        UpdateProfilePreview();
    }

    private void ProfileBreak_Click(object sender, RoutedEventArgs e)
    {
        int at = ProfileSelected.SelectedIndex >= 0 ? ProfileSelected.SelectedIndex + 1 : ProfileSelected.Items.Count;
        ProfileSelected.Items.Insert(at, new ProfileItem("|", BreakText));
        UpdateProfilePreview();
    }

    private void ProfileUp_Click(object sender, RoutedEventArgs e) => MoveProfileItem(-1);
    private void ProfileDown_Click(object sender, RoutedEventArgs e) => MoveProfileItem(1);

    private void MoveProfileItem(int delta)
    {
        int i = ProfileSelected.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= ProfileSelected.Items.Count) return;
        var item = ProfileSelected.Items[i];
        ProfileSelected.Items.RemoveAt(i);
        ProfileSelected.Items.Insert(j, item);
        ProfileSelected.SelectedIndex = j;
        UpdateProfilePreview();
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e)
    {
        var codes = string.Join(",", ProfileSelected.Items.Cast<ProfileItem>().Select(i => i.Code));
        var s = _svc.Settings;
        if (ProfileGameKind == GameKind.Cash) s.CashStats = codes; else s.MttStats = codes;
        _svc.SaveSettings(s);
        SetCashStats.Text = s.CashStats;
        SetMttStats.Text = s.MttStats;
        _svc.Hud.ReloadProfile();
        ProfileStatus.Text = "Сохранено, панели обновлены.";
    }

    private void LoadHudTab()
    {
        HudEnabledBox.IsChecked = _svc.Hud.Enabled;
        SessionWidgetBox.IsChecked = _svc.Settings.SessionWidget;
        AutoProfileBox.IsChecked = _svc.Settings.AutoProfileBySeats;
        HudWindowsGrid.ItemsSource = _svc.Hud.Status();
        ActiveTablesGrid.ItemsSource = _svc.Hud.ActiveTables;
    }

    private void HudEnabled_Click(object sender, RoutedEventArgs e) => _svc.Hud.Enabled = HudEnabledBox.IsChecked == true;

    private async void HudRefresh_Click(object sender, RoutedEventArgs e)
    {
        await _svc.Hud.RefreshTablesAsync();
        LoadHudTab();
    }

    private void HudBind_Click(object sender, RoutedEventArgs e)
    {
        if (HudWindowsGrid.SelectedItem is Hud.HudTableStatus w && ActiveTablesGrid.SelectedItem is ActiveTable t)
        {
            _svc.Hud.BindManually(w.Hwnd, t.TableInstanceId);
            LoadHudTab();
        }
        else MessageBox.Show("Выберите окно слева и стол справа.", "HUD");
    }

    // ── Импорт ─────────────────────────────────────────────────────────────

    private async Task LoadHeroesAsync()
    {
        var host = Host;
        if (host == null) return;
        HeroList.ItemsSource = await Bg(() => host.Heroes.ListAsync());
    }

    private async void HeroAdd_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        var nick = HeroBox.Text.Trim();
        if (host == null || nick.Length == 0) return;
        await Task.Run(async () => { await host.Heroes.AddAsync(nick); await host.ReloadAsync(); });
        HeroBox.Clear();
        await LoadHeroesAsync();
        _svc.Log("Import", $"Добавлен Hero: {nick}");
    }

    private async void HeroRemove_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host == null || HeroList.SelectedItem is not string nick) return;
        await Task.Run(async () => { await host.Heroes.RemoveAsync(nick); await host.ReloadAsync(); });
        await LoadHeroesAsync();
    }

    private void ImportBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с hand history (HH*.txt) и итогами турниров (*.ots)" };
        if (dlg.ShowDialog(this) == true) ImportFolderBox.Text = dlg.FolderName;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        var folder = ImportFolderBox.Text.Trim();
        if (host == null) return;
        if (!System.IO.Directory.Exists(folder)) { ImportStatusText.Text = "Папка не найдена."; return; }
        if (host.HeroNames.Count == 0) { ImportStatusText.Text = "Сначала добавьте свой ник (Hero): импорт чужих рук запрещён правилами рума."; return; }

        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;
        ImportButton.IsEnabled = false;
        ImportCancelButton.IsEnabled = true;
        var progress = new Progress<ImportProgress>(p =>
        {
            ImportProgressBar.Maximum = Math.Max(1, p.FilesTotal);
            ImportProgressBar.Value = p.FilesDone;
            ImportStatusText.Text = $"Файлов {p.FilesDone}/{p.FilesTotal}  рук {p.Hands:N0}  импортировано {p.Imported:N0}  дубликатов {p.Duplicates:N0}  " +
                                    $"ошибок {p.Errors}  отклонено файлов {p.RejectedFiles}  {p.HandsPerSecond:F0} рук/с" +
                                    (p.Eta is { } eta ? $"  осталось ~{eta:mm\\:ss}" : "");
        });
        try
        {
            var rep = await Task.Run(() => host.CreateFileImporter().ImportFolderAsync(folder, progress, ct), ct);
            ImportStatusText.Text = $"Готово за {rep.Elapsed.TotalSeconds:F1} c: раздач {rep.Hands:N0}, импортировано {rep.Imported:N0}, " +
                                    $"дубликатов {rep.Duplicates:N0}, ошибок {rep.Errors}, отклонено файлов {rep.RejectedFiles}, турниров {rep.Tournaments}." +
                                    (rep.Messages.Count > 0 ? "\n" + string.Join("\n", rep.Messages.Take(10)) : "");
            _svc.Log("Import", ImportStatusText.Text);
            host.Hud.Invalidate(rep.AffectedPlayers);
            await _svc.Hud.RefreshTablesAsync();
            await LoadImportErrorsAsync();
        }
        catch (OperationCanceledException)
        {
            ImportStatusText.Text = "Импорт отменён (уже записанные пачки сохранены, повторный импорт продолжит без дублей).";
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = "Ошибка: " + ex.Message;
            _svc.Log("Import", ex.ToString());
        }
        finally
        {
            ImportButton.IsEnabled = true;
            ImportCancelButton.IsEnabled = false;
        }
    }

    private void ImportCancel_Click(object sender, RoutedEventArgs e) => _importCts?.Cancel();

    private async void ImportErrors_Click(object sender, RoutedEventArgs e) => await LoadImportErrorsAsync();

    private async Task LoadImportErrorsAsync()
    {
        var host = Host;
        if (host == null) return;
        ImportErrorsGrid.ItemsSource = await Bg(() => new ReportRepository(host.Db).ImportErrorsAsync());
    }

    private void ImportErrorsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ImportErrorBody.Text = ImportErrorsGrid.SelectedItem is ImportErrorRow r
            ? $"{r.SourceFile} @ {r.Offset}\n{r.Message}\n\n{r.Body}" : "";
    }

    // ── Редактор статов ────────────────────────────────────────────────────

    private async Task LoadStatDefsAsync()
    {
        var host = Host;
        if (host == null) return;
        var defs = await Bg(() => host.StatsRepo.LoadActiveAsync());
        StatDefsGrid.ItemsSource = defs.Select(d => new StatDefVm(d.Code, d.Title, d.Scope, d.Version, d)).ToList();
    }

    private void StatDefsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatDefsGrid.SelectedItem is StatDefVm vm) StatJsonBox.Text = PrettyJson(vm.Def.ToJson());
    }

    private void StatNew_Click(object sender, RoutedEventArgs e)
    {
        StatDefsGrid.SelectedItem = null;
        StatJsonBox.Text = PrettyJson(new StatDefinition
        {
            Code = "my_stat", Title = "My", Scope = StatScope.Decision,
            Opportunity = "Street==\"Turn\" && Facing==\"Bet\"", Hit = "Chosen==\"Raise\"",
            Colors = new() { new(0, 10, "#4FC3F7"), new(10, 101, "#EF5350") },
        }.ToJson());
    }

    private static string PrettyJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private (StatDefinition? Def, CompiledStat? Compiled, string? Error) CompileEditor()
    {
        StatDefinition def;
        try { def = StatDefinition.FromJson(StatJsonBox.Text); }
        catch (Exception ex) { return (null, null, "JSON: " + ex.Message); }
        try
        {
            var known = Host!.Stats.Select(s => s.Definition).Where(d => d.Code != def.Code).Append(def).ToDictionary(d => d.Code);
            return (def, StatCompiler.Compile(def, known), null);
        }
        catch (StatCompileException ex) { return (def, null, ex.Message); }
    }

    private async void StatTrial_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host == null) return;
        var (_, compiled, error) = CompileEditor();
        if (compiled == null) { StatResultText.Text = "Ошибка компиляции: " + error; return; }
        if (compiled.Scope == StatScope.Expression) { StatResultText.Text = "Скомпилировано (expression считается из агрегатов)."; return; }
        var (cases, hits) = await Bg(() => new StatRebuilder(host.Db).TrialRunAsync(compiled));
        StatResultText.Text = cases == 0
            ? "Скомпилировано, но cases = 0 на последних 1000 раздачах — почти наверняка ошибка в условии."
            : $"OK: cases {cases}, hits {hits} ({hits * 100.0 / cases:F1}%) на последних 1000 раздачах.";
    }

    private async void StatSave_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host == null) return;
        var (def, compiled, error) = CompileEditor();
        if (def == null || compiled == null) { StatResultText.Text = "Не сохранено: " + error; return; }
        StatResultText.Text = "Сохранение и селективный пересчёт…";
        try
        {
            int n = await Task.Run(async () =>
            {
                await host.StatsRepo.SaveAsync(def);
                await host.ReloadAsync();
                return await host.CatchUpStaleStatsAsync();
            });
            StatResultText.Text = $"Сохранено. Пересчитано статов: {n}. HUD использует новое определение.";
            _svc.Hud.OnHandsImported(Array.Empty<long>());
            host.Hud.InvalidateAll();
            await LoadStatDefsAsync();
        }
        catch (Exception ex)
        {
            StatResultText.Text = "Ошибка: " + ex.Message;
        }
    }

    // ── Настройки ──────────────────────────────────────────────────────────

    private void PopupTrigger_Changed(object sender, SelectionChangedEventArgs e) => UpdatePopupTriggerFields();

    /// <summary>Задержки относятся только к наведению: в режиме клика их прятать честнее, чем гасить.</summary>
    private void UpdatePopupTriggerFields()
    {
        bool hover = SetPopupTrigger.SelectedIndex != 1;
        foreach (var el in new UIElement[] { SetHoverDelayLabel, SetHoverDelay, SetHoverHideLabel, SetHoverHide })
            el.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadSettingsForm()
    {
        var s = _svc.Settings;
        SetConn.Text = s.ConnectionString;
        // Список румов — из реестра парсеров: новый формат появляется здесь без правки разметки.
        if (SetRoom.Items.Count == 0)
            foreach (var room in PKTracker.Parsers.ParserRegistry.Rooms)
                SetRoom.Items.Add(new ComboBoxItem { Content = room.Code, ToolTip = room.Title });
        SetRoom.SelectedItem = SetRoom.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == s.RoomCode) ?? SetRoom.Items[0];
        SetHhFolder.Text = s.HandHistoryFolder ?? "";
        SetDataFolder.Text = s.LiveDataFolder ?? "";
        SetAutoImport.IsChecked = s.AutoImport;
        SetHud.IsChecked = s.HudEnabled;
        SetProcess.Text = s.ClientProcessName;
        SetWindowClass.Text = s.TableWindowClass;
        SetSeatOffset.Text = s.LiveSeatOffset.ToString();
        SetCashStats.Text = s.CashStats;
        SetMttStats.Text = s.MttStats;
        SetOpacity.Text = s.PanelOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetPopupTrigger.SelectedIndex = s.PopupTrigger == PopupTrigger.Click ? 1 : 0;
        UpdatePopupTriggerFields();
        SetHoverDelay.Text = s.HoverDelayMs.ToString();
        SetSampleBelow.Text = s.SampleSubscriptBelow.ToString();
        SetCap99.IsChecked = s.Cap100To99;
        SetHoverHide.Text = s.HoverHideMs.ToString();
        SetHotkey.Text = s.HudHotkey;
        SetBackupDays.Text = s.AutoBackupDays.ToString();
        SetKeepBackups.Text = s.KeepBackups.ToString();
        SetSessionGap.Text = s.SessionGapMinutes.ToString();
        if (string.IsNullOrEmpty(ImportFolderBox.Text)) ImportFolderBox.Text = s.HandHistoryFolder ?? "";
        if (string.IsNullOrEmpty(TournamentFolderBox.Text))
            TournamentFolderBox.Text = s.TournamentSummaryFolder ?? s.HandHistoryFolder ?? "";
    }

    private async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var s = new TrackerSettings
        {
            ConnectionString = SetConn.Text.Trim(),
            RoomCode = (string)((ComboBoxItem)SetRoom.SelectedItem).Content,
            HandHistoryFolder = NullIfEmpty(SetHhFolder.Text),
            LiveDataFolder = NullIfEmpty(SetDataFolder.Text),
            TournamentSummaryFolder = _svc.Settings.TournamentSummaryFolder,
            AutoImport = SetAutoImport.IsChecked == true,
            HudEnabled = SetHud.IsChecked == true,
            ClientProcessName = SetProcess.Text.Trim(),
            TableWindowClass = SetWindowClass.Text.Trim(),
            LiveSeatOffset = int.TryParse(SetSeatOffset.Text, out var off) ? off : 1,
            CashStats = SetCashStats.Text.Trim(),
            MttStats = SetMttStats.Text.Trim(),
            PanelOpacity = double.TryParse(SetOpacity.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, inv, out var op) ? op : 0.85,
            SessionGapMinutes = int.TryParse(SetSessionGap.Text, out var gap) ? gap : 30,
            HudActiveTableHours = _svc.Settings.HudActiveTableHours,
            PopupTrigger = SetPopupTrigger.SelectedIndex == 1 ? PopupTrigger.Click : PopupTrigger.Hover,
            HoverDelayMs = int.TryParse(SetHoverDelay.Text, out var hd) ? Math.Clamp(hd, 0, 10_000) : 500,
            HoverHideMs = int.TryParse(SetHoverHide.Text, out var hh) ? Math.Clamp(hh, 0, 10_000) : 400,
            HudHotkey = string.IsNullOrWhiteSpace(SetHotkey.Text) ? "Ctrl+Alt+H" : SetHotkey.Text.Trim(),
            SampleSubscriptBelow = int.TryParse(SetSampleBelow.Text, out var sb) ? Math.Clamp(sb, 0, 1000) : 10,
            Cap100To99 = SetCap99.IsChecked == true,
            McpAnonymizeOpponents = _svc.Settings.McpAnonymizeOpponents, McpAllowWrites = _svc.Settings.McpAllowWrites,
            ReplayerHud = _svc.Settings.ReplayerHud, ReplayerProfile = _svc.Settings.ReplayerProfile,
            ReplayerPanelOffsets = _svc.Settings.ReplayerPanelOffsets,
            ReplayerBadgeX = _svc.Settings.ReplayerBadgeX, ReplayerBadgeY = _svc.Settings.ReplayerBadgeY,
            CashProfile = _svc.Settings.CashProfile, MttProfile = _svc.Settings.MttProfile,
            AutoBackupDays = int.TryParse(SetBackupDays.Text, out var bd) ? Math.Max(0, bd) : 1,
            KeepBackups = int.TryParse(SetKeepBackups.Text, out var kb) ? Math.Max(1, kb) : 7,
        };
        _svc.SaveSettings(s);
        SettingsStatus.Text = "Сохранено, перезапуск подсистем…";
        await _svc.StartAsync();
        SettingsStatus.Text = "Готово. " + _svc.DbStatus;
        RegisterHudHotkey();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SettingsStatus.Text = "Бэкап…";
            var file = await _svc.BackupAsync();
            SettingsStatus.Text = "Бэкап сохранён: " + file;
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = "Ошибка бэкапа: " + ex.Message;
        }
    }
}
