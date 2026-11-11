using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PKTracker.App.Hud;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Replayer;

/// <summary>Реплеер (§8): строится из hand_actions + hand_boards, независимо от исходного текста.</summary>
public sealed class ReplayerWindow : Window
{
    private readonly TrackerHost _host;
    private readonly long _handPk;
    private ReplayHand? _hand;
    private int _step; // число применённых действий
    /// <summary>Первое решение игроков: анте и блайнды до него «проигрывать» незачем — раздача открывается уже с ними.</summary>
    private int _first;
    // Стол: центр и радиусы кольца мест. Холст с запасом по краям — панели HUD стоят снаружи от мест и не должны
    // закрывать ни сами места, ни друг друга.
    private const double W = 1280, H = 660, CX = W / 2, CY = H / 2, RX = 365, RY = 185;
    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private readonly System.Windows.Threading.DispatcherTimer _play = new();
    private Button _playButton = null!;
    private bool _loaded;
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly System.Windows.Controls.Primitives.ToggleButton _bbToggle = new()
    {
        Content = "BB", ToolTip = "Стеки, ставки и банк в больших блайндах вместо фишек (клавиша B)", Padding = new Thickness(12, 3, 12, 3),
        Margin = new Thickness(14, 0, 0, 0), Focusable = false,
    };
    private readonly ComboBox _speed = new() { Width = 64, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Панели HUD по местам: строятся один раз, а не на каждый шаг — иначе воспроизведение шло бы рывками.</summary>
    private readonly Dictionary<int, Border> _hudCache = new();
    private readonly ListBox _log = new() { Width = 300, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _header = new() { Margin = new Thickness(12, 8, 12, 6), FontWeight = FontWeights.SemiBold, FontSize = 13 };

    private readonly TrackerSettings _settings;
    /// <summary>Те же правила открытия попапов, что и за столом, но без топмоста поверх реплеера.</summary>
    private readonly PopupController _popups;
    private readonly HudProfileStore _profiles = new();
    /// <summary>Агрегаты игроков раздачи: читаются один раз, дальше перерисовка бесплатна.</summary>
    private readonly Dictionary<long, PlayerAggSet> _agg = new();
    private ProfileBadge? _badge;

    public ReplayerWindow(TrackerHost host, long handPk) : this(host, handPk, TrackerSettings.Load()) { }

    public ReplayerWindow(TrackerHost host, long handPk, TrackerSettings settings)
    {
        _settings = settings;
        _popups = new PopupController(() => _settings, () => host, _ => { }, Dispatcher, overlay: false);
        Closed += (_, _) => _popups.Close();
        _host = host;
        _handPk = handPk;
        Title = "Реплеер";
        Width = 1500;
        Height = 780;
        Background = (Brush)Application.Current.Resources["Bg0"];
        Foreground = (Brush)Application.Current.Resources["Text"];
        FontFamily = new FontFamily("Segoe UI");
        PKTracker.App.Infrastructure.Win32.ApplyDarkFrame(this);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 8) };
        // Любая ручная навигация останавливает воспроизведение: иначе таймер тут же увёл бы раздачу дальше.
        Button Btn(string text, string tip, Action a)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
            b.Click += (_, _) => { Pause(); a(); Render(); };
            buttons.Children.Add(b);
            return b;
        }
        Btn("⏮", "В начало (Home)", () => _step = _first);
        Btn("◀", "Шаг назад (←)", () => _step = Math.Max(0, _step - 1));
        _playButton = new Button { Content = "▶", ToolTip = "Воспроизвести / пауза (пробел)", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0), Focusable = false,
            Style = (Style)Application.Current.FindResource("AccentButton") };
        _playButton.Click += (_, _) => TogglePlay();
        buttons.Children.Add(_playButton);
        Btn("▶|", "Шаг вперёд (→)", () => _step = Math.Min(_hand?.Actions.Count ?? 0, _step + 1));
        Btn("⏭", "В конец (End)", () => _step = _hand?.Actions.Count ?? 0);
        foreach (var st in new[] { Street.Flop, Street.Turn, Street.River })
        {
            var s = st;
            Btn(s.ToString(), $"К началу улицы: {s}", () =>
            {
                if (_hand == null) return;
                int idx = _hand.Actions.FindIndex(a => a.Street >= s);
                _step = idx < 0 ? _hand.Actions.Count : idx;
            });
        }

        buttons.Children.Add(new TextBlock { Text = "Скорость", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        foreach (var x in new[] { "×1", "×2", "×4" }) _speed.Items.Add(x);
        _speed.SelectedIndex = 0;
        _speed.Focusable = false;
        _speed.SelectionChanged += (_, _) => _play.Interval = StepInterval();
        buttons.Children.Add(_speed);

        // В турнире глубину стека и размер ставки считают в блайндах: «17 bb», а не «2 091 706».
        _bbToggle.IsChecked = _settings.ReplayerInBb;
        _bbToggle.Click += (_, _) =>
        {
            _settings.ReplayerInBb = _bbToggle.IsChecked == true;
            try { _settings.Save(); } catch (System.IO.IOException) { }
            Render();
        };
        buttons.Children.Add(_bbToggle);

        // Копирование: ход раздачи читаемым текстом (в текущих единицах — фишки или BB) либо исходный текст рума.
        var copy = new Button { Content = "Копировать ▾", ToolTip = "Ход раздачи в буфер обмена (Ctrl+C)", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0), Focusable = false };
        var copyMenu = new ContextMenu { PlacementTarget = copy, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var copyCourse = new MenuItem { Header = "Ход раздачи", InputGestureText = "Ctrl+C" };
        copyCourse.Click += (_, _) => CopyTranscript();
        var copyRaw = new MenuItem { Header = "Исходный текст раздачи" };
        copyRaw.Click += async (_, _) => await CopyRawAsync();
        copyMenu.Items.Add(copyCourse);
        copyMenu.Items.Add(copyRaw);
        copy.Click += (_, _) => copyMenu.IsOpen = true;
        buttons.Children.Add(copy);
        buttons.Children.Add(_status);

        _play.Interval = StepInterval();
        _play.Tick += (_, _) =>
        {
            if (_hand == null || _step >= _hand.Actions.Count) { Pause(); return; }
            _step++;
            Render();
            if (_step >= _hand.Actions.Count) Pause();
        };
        Closed += (_, _) => _play.Stop();
        PreviewKeyDown += (_, e) =>
        {
            if (_hand == null) return;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Space: TogglePlay(); break;
                case System.Windows.Input.Key.Right: Pause(); _step = Math.Min(_hand.Actions.Count, _step + 1); Render(); break;
                case System.Windows.Input.Key.Left: Pause(); _step = Math.Max(0, _step - 1); Render(); break;
                case System.Windows.Input.Key.Home: Pause(); _step = _first; Render(); break;
                case System.Windows.Input.Key.End: Pause(); _step = _hand.Actions.Count; Render(); break;
                case System.Windows.Input.Key.C when System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control:
                    CopyTranscript();
                    break;
                case System.Windows.Input.Key.B:
                    _bbToggle.IsChecked = _settings.ReplayerInBb = !_settings.ReplayerInBb;
                    try { _settings.Save(); } catch (System.IO.IOException) { }
                    Render();
                    break;
                default: return;
            }
            e.Handled = true;
        };

        var dock = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_log, Dock.Right);
        dock.Children.Add(_header);
        dock.Children.Add(buttons);
        dock.Children.Add(_log);
        dock.Children.Add(new Viewbox { Child = _canvas, Stretch = Stretch.Uniform });
        Content = dock;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Loaded у окна может прийти повторно (смена темы, перенос между мониторами) — раздача загружается один раз.
        if (_loaded) return;
        _loaded = true;
        _hand = await Task.Run(() => new ReportRepository(_host.Db).LoadReplayAsync(_handPk));
        if (_hand == null) { _header.Text = "Раздача не найдена"; return; }
        _first = _hand.Actions.FindIndex(a => IsDecision(a.Action));
        if (_first < 0) _first = 0;
        _step = _first;
        Render();
        await LoadHudAsync();
        // Реплеер открывают, чтобы посмотреть раздачу: она начинает проигрываться сама, пробел — пауза.
        Play();
    }

    /// <summary>
    /// Ход раздачи текстом — вся раздача целиком, независимо от того, на каком шаге остановлен реплеер.
    /// Суммы — в тех же единицах, что на столе (фишки или BB): так текст совпадает с тем, что видно в окне.
    /// </summary>
    private string Transcript()
    {
        var h = _hand!;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Раздача #{h.ExternalId}  {h.Table}  блайнды {Chips(h.Sb)}/{Chips(h.Bb)}{(h.Ante > 0 ? " анте " + Chips(h.Ante) : "")}  {h.PlayedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
        foreach (var seat in h.Seats.OrderBy(x => x.Seat))
        {
            string cards = seat.C1 is { } a && seat.C2 is { } b ? $"  [{a} {b}]" : "";
            sb.AppendLine($"  {seat.Position,-4} {seat.Name}{(seat.IsHero ? " (Hero)" : "")}: {Amt(seat.StartStack)}{cards}");
        }
        var board = h.Boards.Count > 0 ? h.Boards[0] : new List<Card>();
        var street = Street.Preflop;
        bool posts = true;
        foreach (var a in h.Actions)
        {
            if (IsDecision(a.Action) && (posts || a.Street != street))
            {
                posts = false;
                street = a.Street;
                var shown = street switch { Street.Flop => board.Take(3), Street.Turn => board.Take(4), Street.River => board.Take(5), _ => Enumerable.Empty<Card>() };
                string cards = string.Join(" ", shown);
                sb.AppendLine($"*** {street} ***{(cards.Length > 0 ? $"  [{cards}]" : "")}");
            }
            // Анте в ход раздачи не идут: восемь одинаковых строк ничего не сообщают — размер анте есть в шапке.
            if (a.Action == ActionType.PostAnte) continue;
            var name = h.Seats.FirstOrDefault(x => x.Seat == a.Seat)?.Name ?? $"Seat {a.Seat}";
            string text = a.Action switch
            {
                ActionType.Raise => $"raises to {Amt(a.ToAmount)}",
                ActionType.Fold or ActionType.Check => a.Action.ToString().ToLowerInvariant(),
                ActionType.Call => $"calls {Amt(a.Amount)}",
                ActionType.Bet => $"bets {Amt(a.Amount)}",
                ActionType.UncalledReturn => $"uncalled {Amt(a.Amount)} returned",
                ActionType.Collect => $"collects {Amt(a.Amount)}",
                ActionType.PostSb => $"posts SB {Amt(a.Amount)}",
                ActionType.PostBb or ActionType.PostBbOot => $"posts BB {Amt(a.Amount)}",
                _ => $"{a.Action} {Amt(a.Amount)}",
            } + (a.AllIn ? " (all-in)" : "");
            sb.AppendLine($"{name}: {text}");
        }
        if (board.Count > 0) sb.AppendLine($"Борд: [{string.Join(" ", board)}]");
        sb.AppendLine($"Банк: {Amt(h.TotalPot)}");
        foreach (var seat in h.Seats.Where(x => x.NetWon != 0).OrderByDescending(x => x.NetWon))
            sb.AppendLine($"  {seat.Name}: {(seat.NetWon > 0 ? "+" : "−")}{Amt(Math.Abs(seat.NetWon))}");
        return sb.ToString();
    }

    private void CopyTranscript()
    {
        if (_hand == null) return;
        ToClipboard(Transcript(), "Ход раздачи скопирован");
    }

    private async Task CopyRawAsync()
    {
        try
        {
            var text = await Task.Run(() => new ReportRepository(_host.Db).RawHandTextAsync(_handPk));
            if (string.IsNullOrEmpty(text)) { _status.Text = "Исходного текста нет в базе"; return; }
            ToClipboard(text, "Исходный текст скопирован");
        }
        catch (Exception ex) { _status.Text = "Не удалось прочитать текст: " + ex.Message; }
    }

    private void ToClipboard(string text, string done)
    {
        // Буфер обмена бывает занят другой программой — это не повод ронять реплеер.
        try { Clipboard.SetText(text); _status.Text = done; }
        catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "Буфер обмена занят — повторите"; }
    }

    private static bool IsDecision(ActionType t) => t is ActionType.Fold or ActionType.Check or ActionType.Call or ActionType.Bet or ActionType.Raise;

    private TimeSpan StepInterval() => TimeSpan.FromMilliseconds(900 / Math.Pow(2, Math.Max(0, _speed.SelectedIndex)));

    private void Play()
    {
        if (_hand == null) return;
        if (_step >= _hand.Actions.Count) { _step = _first; Render(); }   // с конца — заново
        _play.Start();
        _playButton.Content = "❚❚";
    }

    private void Pause()
    {
        _play.Stop();
        _playButton.Content = "▶";
    }

    private void TogglePlay()
    {
        if (_play.IsEnabled) Pause(); else Play();
    }

    /// <summary>
    /// Панели HUD в реплеере строятся из тех же агрегатов, что за столом, поэтому цифры совпадают
    /// с тем, что было видно во время игры. Читаются в фоне: реплеер живёт в UI-потоке (§10.2).
    /// </summary>
    private async Task LoadHudAsync()
    {
        var h = _hand;
        if (h == null || !_settings.ReplayerHud || Profile() == null) return;
        var ids = h.Seats.Select(s => s.PlayerId).Where(id => id != 0).Distinct().ToList();
        var kind = h.Kind;
        foreach (var id in ids)
        {
            if (_agg.ContainsKey(id)) continue;
            try { _agg[id] = await Task.Run(() => _host.Hud.GetAggAsync(id, kind)); }   // в фоне: UI-поток в базу не ходит
            catch (Exception) { /* нет агрегатов — панель просто покажет прочерки */ }
        }
        _hudCache.Clear();
        Render();
    }

    /// <summary>Раскладка, выбранная значком; null — HUD скрыт.</summary>
    private HudProfile? Profile()
    {
        var h = _hand;
        if (h == null) return null;
        var name = _settings.ReplayerProfile;
        if (name == ProfileBadge.Hidden) return null;
        if (name == HudProfile.LegacyName)
            return HudProfile.FromCodes(h.Kind == GameKind.Cash ? _settings.CashStats : _settings.MttStats,
                h.Kind == GameKind.Cash ? "cash" : "mtt");
        return _profiles.Resolve(name, h.Kind);
    }

    /// <summary>Панель игрока рядом с его местом: над боксом, чтобы не перекрывать карты и стек.</summary>
    private void AddHudPanel(ReplaySeat seat, double cx, double cy, HudProfile profile)
    {
        if (seat.PlayerId == 0 || !_agg.TryGetValue(seat.PlayerId, out var agg)) return;
        if (_hudCache.TryGetValue(seat.Seat, out var cached)) { _canvas.Children.Add(cached); return; }
        var pages = profile.AllPanelPages().ToList();
        var tabbed = new HudTabbedGrid { StripFontSize = 13 };
        var grid = tabbed.Grid;
        grid.CellWeight = FontWeights.SemiBold;
        grid.Stats = _host.Hud.Stats;
        grid.ShowTooltips = true;
        tabbed.Render(pages, pages.Select(pg => (CellValue[]?)agg.EvaluatePage(pg)).ToList());
        // Ячейка открывает тот же попап, что и за столом, и по тому же правилу: только если
        // вкладка к ней привязана в редакторе.
        FrameworkElement? hovered = null;
        PopupTarget? Target(int index)
        {
            var page = tabbed.Page;
            if (page == null || index < 0 || index >= page.Cells.Count || profile.Pages.Count == 0) return null;
            if (page.Cells[index].Popup is not { } popup || profile.Page(popup) == null) return null;
            return new PopupTarget
            {
                PlayerId = seat.PlayerId, PlayerName = seat.Name, Kind = _hand!.Kind, Profile = profile, Page = popup,
            };
        }
        grid.CellEnter += (index, el) =>
        {
            hovered = el;
            if (Target(index) is { } t) _popups.Enter(t, PopupAnchor.OfElement(el));
        };
        grid.CellLeave += index => { if (Target(index) is { } t) _popups.Leave(t); };
        grid.CellClicked += (index, _, _) =>
        {
            if (hovered is { } el && Target(index) is { } t) _popups.Click(t, PopupAnchor.OfElement(el));
        };
        var box = new Border
        {
            // Как панель над столом рума: чёрная подложка, тонкая серая рамка.
            Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2, 1, 3, 2),
            Child = tabbed,
        };
        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double pw = box.DesiredSize.Width, ph = box.DesiredSize.Height;
        // Панель таскается правой кнопкой, как и за столом: левая занята попапами. Сдвиг
        // запоминается по месту за столом — панель мешает всегда в одном и том же месте.
        // «v2»: панели переехали наружу от мест, и сдвиги, сохранённые для прежней расстановки, к новой не подходят.
        string key = $"v2:{_hand!.MaxSeats}:{seat.Seat}";
        var offset = _settings.ReplayerPanelOffsets.TryGetValue(key, out var saved) && saved.Length == 2
            ? saved : new double[2];
        // Панель стоит снаружи от места: у боковых мест — сбоку, у верхних — над боксом, у нижних — под ним.
        // Так она не закрывает ни бокс игрока, ни стол, ни соседние панели.
        var (sideX, sideY) = PanelSide(cx, cy);
        double baseX = sideX != 0 ? (sideX < 0 ? cx - 75 - 6 - pw : cx + 75 + 6) : cx - 75;
        double baseY = sideX != 0 ? cy - ph / 2 : sideY < 0 ? cy - 23 - 6 - ph : cy + 23 + 6;
        Place();
        DragWithRightButton(box, offset, key, Place);
        _hudCache[seat.Seat] = box;
        _canvas.Children.Add(box);

        void Place()
        {
            // Целиком в пределах холста: за его краем панель наезжала бы на журнал действий справа.
            Canvas.SetLeft(box, Math.Clamp(baseX + offset[0], 0, Math.Max(0, _canvas.Width - pw)));
            Canvas.SetTop(box, Math.Clamp(baseY + offset[1], 0, Math.Max(0, _canvas.Height - ph)));
        }
    }

    /// <summary>С какой стороны от места ставить панель HUD: (−1/+1, 0) — слева/справа, (0, −1/+1) — над/под боксом.</summary>
    private static (int X, int Y) PanelSide(double cx, double cy)
    {
        double dx = (cx - CX) / RX, dy = (cy - CY) / RY;
        if (Math.Abs(dx) > 0.8) return (dx < 0 ? -1 : 1, 0);
        return (0, dy < 0 ? -1 : 1);
    }

    /// <summary>
    /// Перетаскивание зажатой правой кнопкой. Ячейки гасят правый клик у себя, но нажатие
    /// всплывает до рамки панели, а дальше мышь захвачена — события идут уже ей.
    /// </summary>
    private void DragWithRightButton(Border box, double[] offset, string key, Action place)
    {
        Point from = default;
        bool dragging = false;
        box.MouseRightButtonDown += (_, e) =>
        {
            from = e.GetPosition(_canvas);
            dragging = true;
            box.CaptureMouse();
            e.Handled = true;
        };
        box.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var now = e.GetPosition(_canvas);
            offset[0] += now.X - from.X;
            offset[1] += now.Y - from.Y;
            from = now;
            place();
        };
        box.MouseRightButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            box.ReleaseMouseCapture();
            e.Handled = true;
            _settings.ReplayerPanelOffsets[key] = offset;
            _settings.Save();
        };
    }

    /// <summary>Значок выбора раскладки: кладётся последним, чтобы ничто его не перекрывало.</summary>
    private void AddBadge()
    {
        _badge = new ProfileBadge(() => _hand?.Kind ?? GameKind.Cash, () => _settings.ReplayerProfile, name =>
        {
            _settings.ReplayerProfile = name;
            _settings.Save();
            _hudCache.Clear();
            _ = LoadHudAsync();
            Render();
        });
        _badge.Dragged += (dx, dy) =>
        {
            _settings.ReplayerBadgeX = Math.Clamp(_settings.ReplayerBadgeX + dx, 0, _canvas.Width - 18);
            _settings.ReplayerBadgeY = Math.Clamp(_settings.ReplayerBadgeY + dy, 0, _canvas.Height - 18);
            Canvas.SetLeft(_badge!, _settings.ReplayerBadgeX);
            Canvas.SetTop(_badge!, _settings.ReplayerBadgeY);
            _settings.Save();
        };
        Canvas.SetLeft(_badge, Math.Clamp(_settings.ReplayerBadgeX, 0, _canvas.Width - 18));
        Canvas.SetTop(_badge, Math.Clamp(_settings.ReplayerBadgeY, 0, _canvas.Height - 18));
        _canvas.Children.Add(_badge);
    }

    /// <summary>Сумма в единицах раздачи: доллары в кеше, фишки в турнире.</summary>
    private string Chips(long v) => _hand!.Kind == GameKind.Cash ? $"${v / 100.0:0.00}" : (v / 100.0).ToString("#,0");

    /// <summary>Сумма для стола и журнала: в больших блайндах, если включён режим «BB», иначе в фишках.</summary>
    private string Amt(long v) => _settings.ReplayerInBb && _hand!.Bb > 0
        ? ((double)v / _hand.Bb).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " bb"
        : Chips(v);

    private void Render()
    {
        var h = _hand;
        if (h == null) return;
        _header.Text = $"#{h.ExternalId}  {h.Table}  {Chips(h.Sb)}/{Chips(h.Bb)}{(h.Ante > 0 ? " ante " + Chips(h.Ante) : "")}  " +
                       $"{h.PlayedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}  банк {Amt(h.TotalPot)}" +
                       (h.Flags.HasFlag(HandFlags.RunItTwice) ? "  RUN IT TWICE" : "") + (h.Flags.HasFlag(HandFlags.BombPot) ? "  BOMB POT" : "");

        // Состояние после _step действий
        var invested = new Dictionary<int, long>();
        var streetPut = new Dictionary<int, long>();
        var folded = new HashSet<int>();
        var lastAction = new Dictionary<int, string>();
        var street = Street.Preflop;
        long pot = 0;
        _log.Items.Clear();
        for (int i = 0; i < _step; i++)
        {
            var a = h.Actions[i];
            if (a.Street != street && a.Action is ActionType.Fold or ActionType.Check or ActionType.Call or ActionType.Bet or ActionType.Raise)
            {
                street = a.Street;
                streetPut.Clear();
                lastAction.Clear();
                _log.Items.Add($"*** {street} ***");
            }
            var name = h.Seats.FirstOrDefault(s => s.Seat == a.Seat)?.Name ?? $"Seat {a.Seat}";
            switch (a.Action)
            {
                case ActionType.Fold: folded.Add(a.Seat); break;
                case ActionType.UncalledReturn:
                    invested[a.Seat] = invested.GetValueOrDefault(a.Seat) - a.Amount;
                    streetPut[a.Seat] = Math.Max(0, streetPut.GetValueOrDefault(a.Seat) - a.Amount);
                    break;
                case ActionType.Collect: break;
                default:
                    invested[a.Seat] = invested.GetValueOrDefault(a.Seat) + a.Amount;
                    if (a.Action != ActionType.PostAnte) streetPut[a.Seat] = a.ToAmount;
                    break;
            }
            pot = invested.Values.Sum();
            string text = a.Action switch
            {
                ActionType.Raise => $"raises to {Amt(a.ToAmount)}",
                ActionType.Fold or ActionType.Check => a.Action.ToString().ToLowerInvariant(),
                ActionType.UncalledReturn => $"uncalled {Amt(a.Amount)} returned",
                ActionType.Collect => $"collects {Amt(a.Amount)}",
                _ => $"{a.Action} {Amt(a.Amount)}",
            } + (a.AllIn ? " (all-in)" : "");
            lastAction[a.Seat] = text;
            _log.Items.Add($"{name}: {text}");
        }
        bool finished = _step >= h.Actions.Count;
        // Торги улицы закончены, следующее решение — уже на новой: её карты на столе видны до первого хода,
        // иначе кнопка «Flop» показывала бы стол ещё без флопа.
        if (!finished && IsDecision(h.Actions[_step].Action) && h.Actions[_step].Street > street)
        {
            street = h.Actions[_step].Street;
            streetPut.Clear();
            lastAction.Clear();
        }
        if (finished) _log.Items.Add("*** Итог ***");
        _log.ScrollIntoView(_log.Items.Count > 0 ? _log.Items[^1] : null);

        _canvas.Children.Clear();
        var felt = new Ellipse
        {
            Width = 620, Height = 300,
            Fill = new RadialGradientBrush(Color.FromRgb(0x45, 0x3F, 0x86), Color.FromRgb(0x23, 0x21, 0x3C)),
            Stroke = new LinearGradientBrush(Color.FromRgb(0x5A, 0x52, 0xB0), Color.FromRgb(0x2E, 0x2B, 0x52), 90), StrokeThickness = 16,
        };
        Canvas.SetLeft(felt, CX - 310); Canvas.SetTop(felt, CY - 150);
        _canvas.Children.Add(felt);

        // Доски: видимая часть зависит от улицы; в конце — полностью (при RIT — обе)
        int visible = finished ? 5 : street switch { Street.Flop => 3, Street.Turn => 4, Street.River => 5, _ => 0 };
        for (int b = 0; b < h.Boards.Count; b++)
        {
            if (b > 0 && !finished) break;
            var cards = h.Boards[b].Take(visible).ToList();
            for (int i = 0; i < cards.Count; i++)
                AddCard(cards[i], CX - 110 + i * 46, (h.Boards.Count > 1 && finished ? CY - 50 + b * 66 : CY - 25));
        }
        AddText($"Банк: {Amt(pot)}", CX - 50, CY + 50, 14, Brushes.White);

        int max = Math.Max(h.MaxSeats, 2);
        int heroSeat = h.Seats.FirstOrDefault(s => s.IsHero)?.Seat ?? 1;
        var profile = _settings.ReplayerHud ? Profile() : null;
        foreach (var s in h.Seats)
        {
            int v = ((s.Seat - heroSeat) % max + max) % max;
            double angle = Math.PI / 2 + v * 2 * Math.PI / max;
            double cx = CX + RX * Math.Cos(angle), cy = CY + RY * Math.Sin(angle);
            bool isFolded = folded.Contains(s.Seat);
            var box = new Border
            {
                Width = 150, Height = 46, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(isFolded ? Color.FromRgb(0x25, 0x24, 0x3C) : Color.FromRgb(0x1A, 0x19, 0x30)),
                BorderBrush = new SolidColorBrush(s.IsHero ? Color.FromRgb(0x7C, 0x6F, 0xF0) : Color.FromRgb(0x37, 0x35, 0x5A)), BorderThickness = new Thickness(s.IsHero ? 2 : 1),
                Opacity = isFolded ? 0.6 : 1,
                Child = new StackPanel
                {
                    Margin = new Thickness(6, 2, 6, 2),
                    Children =
                    {
                        new TextBlock { Text = $"{s.Name}  ({s.Position})", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12 },
                        new TextBlock
                        {
                            Text = finished
                                ? $"{Amt(s.StartStack - invested.GetValueOrDefault(s.Seat))}   {(s.NetWon >= 0 ? "+" : "")}{Amt(s.NetWon)}"
                                : $"{Amt(s.StartStack - invested.GetValueOrDefault(s.Seat))}   {lastAction.GetValueOrDefault(s.Seat)}",
                            Foreground = Brushes.Gainsboro, FontSize = 11,
                        },
                    },
                },
            };
            Canvas.SetLeft(box, cx - 75); Canvas.SetTop(box, cy - 23);
            _canvas.Children.Add(box);

            // Карты оппонентов — только если были вскрыты; Hero — всегда
            if (s.C1 is { } c1 && s.C2 is { } c2 && (s.IsHero || finished || !isFolded))
            {
                // Карты — со стороны стола: над боксом у нижних и боковых мест, под ним у верхних (там над боксом панель HUD).
                double cardY = PanelSide(cx, cy) is (0, -1) ? cy + 27 : cy - 72;
                AddCard(c1, cx - 44, cardY, 0.8);
                AddCard(c2, cx - 4, cardY, 0.8);
            }
            if (streetPut.GetValueOrDefault(s.Seat) > 0 && !finished)
                AddText(Amt(streetPut[s.Seat]), CX + 215 * Math.Cos(angle) - 20, CY + 95 * Math.Sin(angle) - 8, 12, new SolidColorBrush(Color.FromRgb(0x9C, 0x92, 0xF8)));

            if (profile != null) AddHudPanel(s, cx, cy, profile);
        }

        AddBadge();
    }

    private void AddCard(Card c, double x, double y, double scale = 1)
    {
        var suitColor = c.Suit switch
        {
            0 => Color.FromRgb(0x1B, 0x8A, 0x3C), // clubs
            1 => Color.FromRgb(0x1E, 0x63, 0xC8), // diamonds
            2 => Color.FromRgb(0xC6, 0x28, 0x28), // hearts
            _ => Color.FromRgb(0x22, 0x22, 0x22), // spades
        };
        var border = new Border
        {
            Width = 40 * scale, Height = 56 * scale, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(suitColor),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = $"{Card.Ranks[c.Rank]}{"♣♦♥♠"[c.Suit]}", Foreground = Brushes.White, FontSize = 18 * scale, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
        _canvas.Children.Add(border);
    }

    private void AddText(string text, double x, double y, double size, Brush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
        _canvas.Children.Add(tb);
    }
}
