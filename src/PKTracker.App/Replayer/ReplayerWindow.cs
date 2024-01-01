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
    private readonly Canvas _canvas = new() { Width = 820, Height = 480 };
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
        Width = 1160;
        Height = 600;
        Background = (Brush)Application.Current.Resources["Bg0"];
        Foreground = (Brush)Application.Current.Resources["Text"];
        FontFamily = new FontFamily("Segoe UI");
        PKTracker.App.Infrastructure.Win32.ApplyDarkFrame(this);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 8) };
        void Btn(string text, Action a)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => { a(); Render(); };
            buttons.Children.Add(b);
        }
        Btn("⏮", () => _step = 0);
        Btn("◀", () => _step = Math.Max(0, _step - 1));
        Btn("▶", () => _step = Math.Min(_hand?.Actions.Count ?? 0, _step + 1));
        Btn("⏭", () => _step = _hand?.Actions.Count ?? 0);
        foreach (var st in new[] { Street.Flop, Street.Turn, Street.River })
        {
            var s = st;
            Btn(s.ToString(), () =>
            {
                if (_hand == null) return;
                int idx = _hand.Actions.FindIndex(a => a.Street >= s);
                _step = idx < 0 ? _hand.Actions.Count : idx;
            });
        }

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
        _hand = await Task.Run(() => new ReportRepository(_host.Db).LoadReplayAsync(_handPk));
        if (_hand == null) { _header.Text = "Раздача не найдена"; return; }
        _step = 0;
        Render();
        await LoadHudAsync();
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
            try { _agg[id] = await _host.Hud.GetAggAsync(id, kind); }
            catch (Exception) { /* нет агрегатов — панель просто покажет прочерки */ }
        }
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
        var page = profile.Panel;
        var grid = new HudGridControl { Stats = _host.Hud.Stats, ShowTooltips = true };
        grid.Render(page, agg.EvaluatePage(page));
        // Ячейка открывает тот же попап, что и за столом, и по тому же правилу: только если
        // вкладка к ней привязана в редакторе.
        FrameworkElement? hovered = null;
        PopupTarget? Target(int index)
        {
            if (index < 0 || index >= page.Cells.Count || profile.Pages.Count == 0) return null;
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
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x17, 0x12)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x6E, 0x4A, 0x2C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(3, 2, 3, 2),
            Child = grid,
        };
        // Панель таскается правой кнопкой, как и за столом: левая занята попапами. Сдвиг
        // запоминается по месту за столом — панель мешает всегда в одном и том же месте.
        string key = $"{_hand!.MaxSeats}:{seat.Seat}";
        var offset = _settings.ReplayerPanelOffsets.TryGetValue(key, out var saved) && saved.Length == 2
            ? saved : new double[2];
        double baseX = cx - 75, baseY = cy - 23 - 8 - page.Rows * page.RowHeight - 6;
        Place();
        DragWithRightButton(box, offset, key, Place);
        _canvas.Children.Add(box);

        void Place()
        {
            Canvas.SetLeft(box, Math.Clamp(baseX + offset[0], 0, Math.Max(0, _canvas.Width - 40)));
            Canvas.SetTop(box, Math.Clamp(baseY + offset[1], 0, Math.Max(0, _canvas.Height - 20)));
        }
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

    private string Amt(long v) => _hand!.Kind == GameKind.Cash ? $"${v / 100.0:0.00}" : (v / 100.0).ToString("#,0");

    private void Render()
    {
        var h = _hand;
        if (h == null) return;
        _header.Text = $"#{h.ExternalId}  {h.Table}  {Amt(h.Sb)}/{Amt(h.Bb)}{(h.Ante > 0 ? " ante " + Amt(h.Ante) : "")}  " +
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
        if (finished) _log.Items.Add("*** Итог ***");
        _log.ScrollIntoView(_log.Items.Count > 0 ? _log.Items[^1] : null);

        _canvas.Children.Clear();
        var felt = new Ellipse
        {
            Width = 620, Height = 300,
            Fill = new RadialGradientBrush(Color.FromRgb(0x5A, 0x40, 0x2C), Color.FromRgb(0x2A, 0x1E, 0x16)),
            Stroke = new LinearGradientBrush(Color.FromRgb(0x8A, 0x5E, 0x38), Color.FromRgb(0x3A, 0x28, 0x1B), 90), StrokeThickness = 16,
        };
        Canvas.SetLeft(felt, 100); Canvas.SetTop(felt, 90);
        _canvas.Children.Add(felt);

        // Доски: видимая часть зависит от улицы; в конце — полностью (при RIT — обе)
        int visible = finished ? 5 : street switch { Street.Flop => 3, Street.Turn => 4, Street.River => 5, _ => 0 };
        for (int b = 0; b < h.Boards.Count; b++)
        {
            if (b > 0 && !finished) break;
            var cards = h.Boards[b].Take(visible).ToList();
            for (int i = 0; i < cards.Count; i++)
                AddCard(cards[i], 300 + i * 46, (h.Boards.Count > 1 && finished ? 190 + b * 66 : 215));
        }
        AddText($"Банк: {Amt(pot)}", 360, 290, 14, Brushes.White);

        int max = Math.Max(h.MaxSeats, 2);
        int heroSeat = h.Seats.FirstOrDefault(s => s.IsHero)?.Seat ?? 1;
        var profile = _settings.ReplayerHud ? Profile() : null;
        foreach (var s in h.Seats)
        {
            int v = ((s.Seat - heroSeat) % max + max) % max;
            double angle = Math.PI / 2 + v * 2 * Math.PI / max;
            double cx = 410 + 350 * Math.Cos(angle), cy = 240 + 190 * Math.Sin(angle);
            bool isFolded = folded.Contains(s.Seat);
            var box = new Border
            {
                Width = 150, Height = 46, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(isFolded ? Color.FromRgb(0x2A, 0x24, 0x20) : Color.FromRgb(0x1E, 0x17, 0x12)),
                BorderBrush = new SolidColorBrush(s.IsHero ? Color.FromRgb(0xD0, 0x8A, 0x4A) : Color.FromRgb(0x40, 0x33, 0x2A)), BorderThickness = new Thickness(s.IsHero ? 2 : 1),
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
                AddCard(c1, cx - 44, cy - 72, 0.8);
                AddCard(c2, cx - 4, cy - 72, 0.8);
            }
            if (streetPut.GetValueOrDefault(s.Seat) > 0 && !finished)
                AddText(Amt(streetPut[s.Seat]), 410 + 230 * Math.Cos(angle) - 20, 240 + 110 * Math.Sin(angle) - 8, 12, new SolidColorBrush(Color.FromRgb(0xE3, 0xA4, 0x5A)));

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
