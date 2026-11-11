using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PKTracker.App.Infrastructure;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Hud;

public sealed record HudTableStatus(IntPtr Hwnd, string WindowTitle, string? TableName, int Players, string Source);

/// <summary>
/// Оркестрация HUD: окна столов ↔ рассадка ↔ панели. Всё, что трогает окна, — в UI-потоке;
/// все запросы к БД — в фоне через Task.Run (§10.3, §15.11).
/// </summary>
public sealed class HudManager : IDisposable
{
    private sealed class BoundTable
    {
        public required TableWindow Window { get; init; }
        public ActiveTable? Table { get; set; }
        public int MaxSeats { get; set; }
        public int? HeroSeat { get; set; }
        public GameKind Kind { get; set; }
        public List<(int Seat, string Name, long PlayerId)> Seats { get; set; } = new();
        public Dictionary<int, HudPanelWindow> Panels { get; } = new();
        public BadgeWindow? Badge { get; set; }
        public string Source { get; set; } = "";
    }

    private readonly AppServices _svc;
    private readonly Dispatcher _ui;
    private readonly TableWindowTracker _tracker;
    private readonly PopupController _popups;
    private readonly Dictionary<IntPtr, BoundTable> _bound = new();
    private readonly Dictionary<IntPtr, long> _manual = new();
    private readonly Dictionary<(string Game, string Table), LiveTableSeating> _live = new();
    private List<ActiveTable> _active = new();
    private Dictionary<string, double[]> _layout = new();
    private readonly HudProfileStore _profiles = new();
    private HudProfile? _cashProfile, _mttProfile;
    private bool _enabled = true;

    private static string LayoutPath => Path.Combine(TrackerSettings.Folder, "hud_layout.json");

    public event Action? StatusChanged;

    public HudManager(AppServices svc, Dispatcher ui)
    {
        _svc = svc;
        _ui = ui;
        _tracker = new TableWindowTracker(() => svc.Settings.ClientProcessName, () => svc.Settings.TableWindowClass);
        _tracker.WindowsChanged += _ => Rebind();
        _tracker.WindowsMoved += Reposition;
        _popups = new PopupController(() => _svc.Settings, () => _svc.Host, m => _svc.Log("Hud", m), ui);
        try
        {
            if (File.Exists(LayoutPath))
                _layout = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(LayoutPath)) ?? new();
        }
        catch (JsonException) { }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) CloseAllPanels();
            else Rebind();
        }
    }

    /// <summary>Разрешён ли оверлей правилами активного рума (GG — нет).</summary>
    private bool Allowed => _svc.Host is not { } h || RoomHudPolicy.For(h.Db.RoomCode).HudAllowed;

    public void Start()
    {
        if (_svc.Host is { } h && !RoomHudPolicy.For(h.Db.RoomCode).HudAllowed)
        {
            // Рум могли сменить при работающем приложении: панели прежнего рума убираем.
            CloseAllPanels();
            // Импорт и анализ работают, но оверлей над столом этого рума запрещён его правилами.
            _svc.Log("Hud", "HUD над столом выключен: " + RoomHudPolicy.For(h.Db.RoomCode).Summary);
            return;
        }
        _tracker.Start();
        _ = RefreshTablesAsync();
    }

    public IReadOnlyList<ActiveTable> ActiveTables => _active;

    public IReadOnlyList<HudTableStatus> Status() =>
        _tracker.Windows.Select(w => _bound.TryGetValue(w.Hwnd, out var b)
            ? new HudTableStatus(w.Hwnd, w.Title, b.Table?.TableName, b.Seats.Count, b.Source)
            : new HudTableStatus(w.Hwnd, w.Title, null, 0, "не привязан")).ToList();

    /// <summary>Ручная привязка окна к столу (fallback, §5.1 п.3).</summary>
    public void BindManually(IntPtr hwnd, long tableInstanceId)
    {
        _manual[hwnd] = tableInstanceId;
        Rebind();
    }

    // ── Источники рассадки ───────────────────────────────────────────────────

    /// <summary>После импорта новых раздач: инвалидация кеша статов и обновление рассадки из БД.</summary>
    public void OnHandsImported(IReadOnlyCollection<long> affectedPlayers)
    {
        _svc.Host?.Hud.Invalidate(affectedPlayers);
        _popups.Invalidate(affectedPlayers);
        _ = RefreshTablesAsync();
    }

    public void OnLiveSeating(LiveTableSeating s)
    {
        _ui.InvokeAsync(() =>
        {
            _live[("G" + s.GameNumber, "T" + s.TableNumber)] = s;
            Rebind();
        });
        // UUID → идентичность игрока: смена ника склеивает статистику (§0.4)
        if (_svc.Host is { } host)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var merges = await new PlayerIdentityService(host.Db).ObserveAsync(s.Seats);
                    if (merges.Count == 0) return;
                    foreach (var m in merges) _svc.Log("Live", $"Смена ника: {m.AliasNickname} склеен с игроком {m.CanonicalId}");
                    host.Hud.Invalidate(merges.SelectMany(m => new[] { m.CanonicalId, m.AliasId }));
                    await RefreshTablesAsync();
                }
                catch (Exception ex)
                {
                    _svc.Log("Live", "Идентичность игроков: " + ex.Message);
                }
            });
        }
    }

    public async Task RefreshTablesAsync()
    {
        var host = _svc.Host;
        if (host == null) return;
        try
        {
            var tables = await Task.Run(() => new ReportRepository(host.Db).ActiveTablesAsync(TimeSpan.FromHours(_svc.Settings.HudActiveTableHours), host.Db.Hud));
            await _ui.InvokeAsync(() =>
            {
                _active = tables;
                Rebind();
                RefreshAllStats();
            });
        }
        catch (Exception ex)
        {
            _svc.Log("Hud", "Не удалось получить активные столы: " + ex.Message);
        }
    }

    // ── Привязка ─────────────────────────────────────────────────────────────

    private ActiveTable? Match(TableWindow w)
    {
        if (_manual.TryGetValue(w.Hwnd, out var id))
            return _active.FirstOrDefault(t => t.TableInstanceId == id);

        var candidates = _active.Where(t => HudGeometry.TitleMatches(w.Title, t)).OrderByDescending(t => t.LastHandAt).ToList();
        if (candidates.Count > 0) return candidates[0];
        // Единственное окно и единственный активный стол — привязываем без эвристики
        if (_tracker.Windows.Count == 1 && _active.Count(t => t.LastHandAt > DateTime.UtcNow.AddMinutes(-10)) == 1)
            return _active.First(t => t.LastHandAt > DateTime.UtcNow.AddMinutes(-10));
        return null;
    }

    private void Rebind()
    {
        if (!_enabled) return;
        // Трекер окон мог быть запущен ещё при разрешённом руме — запрет проверяем при каждой привязке.
        if (!Allowed)
        {
            if (_bound.Count > 0) CloseAllPanels();
            return;
        }
        var alive = new HashSet<IntPtr>(_tracker.Windows.Select(w => w.Hwnd));
        foreach (var gone in _bound.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            foreach (var p in _bound[gone].Panels.Values) p.Close();
            _bound[gone].Badge?.Close();
            _bound.Remove(gone);
        }

        foreach (var w in _tracker.Windows)
        {
            var table = Match(w);
            if (!_bound.TryGetValue(w.Hwnd, out var b))
            {
                b = new BoundTable { Window = w };
                _bound[w.Hwnd] = b;
            }
            b.Table = table;
            if (table == null)
            {
                foreach (var p in b.Panels.Values) p.Close();
                b.Panels.Clear();
                b.Badge?.Close();
                b.Badge = null;
                b.Seats.Clear();
                b.Source = "не привязан";
                continue;
            }

            b.Kind = table.Kind;
            b.MaxSeats = table.MaxSeats;
            // .data-канал точнее и быстрее (§0.4); иначе — рассадка последней раздачи из HH (отстаёт на руку)
            if (_live.TryGetValue((table.ExternalGameId, table.ExternalTableId), out var live) && live.ReceivedUtc > table.LastHandAt)
            {
                b.MaxSeats = live.MaxSeats > 0 ? live.MaxSeats : table.MaxSeats;
                b.HeroSeat = live.MySeat;
                var ids = table.Seats.ToDictionary(s => s.Name, s => s.PlayerId);
                b.Seats = live.Seats.Select(s => (s.Seat, s.Name, ids.GetValueOrDefault(s.Name))).ToList();
                b.Source = ".data";
                ResolveMissingIds(b);
            }
            else
            {
                b.HeroSeat = table.HeroSeat;
                b.Seats = table.Seats.ToList();
                b.Source = "HH";
            }
            SyncPanels(b);
        }
        Reposition(_tracker.Windows);
        App.SetLowLatency(_bound.Values.Any(b => b.Panels.Count > 0));
        StatusChanged?.Invoke();
    }

    private void ResolveMissingIds(BoundTable b)
    {
        var host = _svc.Host;
        var missing = b.Seats.Where(s => s.PlayerId == 0).Select(s => s.Name).ToList();
        if (host == null || missing.Count == 0) return;
        _ = Task.Run(async () =>
        {
            var ids = await host.Hud.FindPlayersAsync(missing);
            await _ui.InvokeAsync(() =>
            {
                b.Seats = b.Seats.Select(s => s.PlayerId == 0 && ids.TryGetValue(s.Name, out var id) ? (s.Seat, s.Name, id) : s).ToList();
                SyncPanels(b);
            });
        });
    }

    private void SyncPanels(BoundTable b)
    {
        if (PanelsHidden(b.Kind))
        {
            foreach (var p in b.Panels.Values) p.Close();
            b.Panels.Clear();
            SyncBadge(b);
            return;
        }
        var seats = b.Seats.ToDictionary(s => s.Seat);
        foreach (var seat in b.Panels.Keys.Where(k => !seats.ContainsKey(k)).ToList())
        {
            b.Panels[seat].Close();
            b.Panels.Remove(seat);
        }
        var changed = new List<HudPanelWindow>();
        foreach (var (seat, name, pid) in b.Seats)
        {
            if (!b.Panels.TryGetValue(seat, out var panel))
            {
                // Подсказка-тултип дублировала бы попап, который и так открывается по наведению.
                panel = new HudPanelWindow(_svc.Settings.PanelOpacity)
                {
                    Seat = seat, ShowTooltips = _svc.Settings.PopupTrigger != PopupTrigger.Hover,
                };
                panel.CellClicked += (p, _, popup) =>
                {
                    if (Target(b, p, popup) is { } t) _popups.Click(t, PopupAnchor.OfWindow(p.Handle));
                };
                panel.CellEnter += (p, _, popup) =>
                {
                    if (Target(b, p, popup) is { } t) _popups.Enter(t, PopupAnchor.OfWindow(p.Handle));
                };
                // Ячейка без привязанной вкладки уход курсора не обрабатывает: иначе попап
                // закрывался бы в тот момент, когда мышь пересекает такую ячейку по дороге к нему.
                panel.CellLeave += (p, _, popup) => { if (Target(b, p, popup) is { } t) _popups.Leave(t); };
                panel.DraggedBy += (p, dx, dy) => OnDragged(b, p, dx, dy);
                panel.MenuRequested += async p =>
                {
                    if (_svc.Host is not { } host || p.PlayerId == 0) return;
                    try
                    {
                        // Тот же игрок может сидеть и за другим столом — обновляем все панели.
                        var menu = await Views.PlayerMarkerMenu.BuildAsync(_svc, host, p.PlayerId, p.PlayerName, () => _ui.InvokeAsync(RefreshAll));
                        menu.IsOpen = true;
                    }
                    catch (Exception ex) { _svc.Log("Hud", "Меню метки: " + ex.Message); }
                };
                b.Panels[seat] = panel;
                panel.SetPlayer(name, pid);
                panel.Show();
                changed.Add(panel);
            }
            else if (panel.PlayerName != name || panel.PlayerId != pid)
            {
                panel.SetPlayer(name, pid);
                changed.Add(panel);
            }
        }
        if (changed.Count > 0) RefreshStats(b, changed);
        SyncBadge(b);
    }

    /// <summary>
    /// Значок выбора раскладки — по одному на стол. Появляется только когда над столом есть панели:
    /// висеть над неприкреплённым окном ему незачем.
    /// </summary>
    private void SyncBadge(BoundTable b)
    {
        if (b.Table == null)
        {
            b.Badge?.Close();
            b.Badge = null;
            return;
        }
        if (b.Badge != null) return;
        var kind = b.Kind;
        var badge = new ProfileBadge(() => kind, () => Current(kind), name =>
        {
            if (kind == GameKind.Cash) _svc.Settings.CashProfile = name; else _svc.Settings.MttProfile = name;
            _svc.Settings.Save();
            ReloadProfile();
        });
        var window = new BadgeWindow(badge);
        badge.Dragged += (dx, dy) => OnBadgeDragged(b, dx, dy);
        b.Badge = window;
        window.Show();
    }

    private string? Current(GameKind kind) => kind == GameKind.Cash ? _svc.Settings.CashProfile : _svc.Settings.MttProfile;

    private void OnBadgeDragged(BoundTable b, double dx, double dy)
    {
        var r = b.Window.Rect;
        if (r.Width <= 0 || r.Height <= 0) return;
        var key = BadgeKey(b);
        var off = _layout.TryGetValue(key, out var o) ? o : new[] { 0.02, 0.02 };
        off[0] += dx / r.Width;
        off[1] += dy / r.Height;
        _layout[key] = off;
        Reposition(new[] { b.Window });
        SaveLayoutSoon();
    }

    /// <summary>Положение значка запоминается на размер стола, как и положение панелей.</summary>
    private static string BadgeKey(BoundTable b) => $"badge:{b.MaxSeats}";

    // ── Статы ────────────────────────────────────────────────────────────────

    /// <summary>Профиль HUD изменился — перечитать раскладку и перерисовать все панели.</summary>
    public void ReloadProfile()
    {
        _cashProfile = null;
        _mttProfile = null;
        _paired.Clear();
        foreach (var b in _bound.Values)
        {
            foreach (var p in b.Panels.Values) p.Close();
            b.Panels.Clear();
            b.Badge?.Close();
            b.Badge = null;
        }
        Rebind();
    }

    /// <summary>Профиль для вида игры: старый плоский список, сохранённый пользователем либо встроенный.</summary>
    public HudProfile Profile(GameKind kind) => kind == GameKind.Cash
        ? _cashProfile ??= Resolve(_svc.Settings.CashProfile, GameKind.Cash)
        : _mttProfile ??= Resolve(_svc.Settings.MttProfile, GameKind.Mtt);

    private readonly Dictionary<(GameKind, bool), HudProfile> _paired = new();

    /// <summary>
    /// Раскладка для конкретного стола. Раскладки часто идут парами «…9max…» / «…6max…»: статы в них отфильтрованы
    /// по числу игроков, и на шестиместном столе панель «9max» показывала бы чужую выборку. Если выбранная
    /// раскладка — одна из такой пары и вторая сохранена, для стола берётся подходящая по числу мест.
    /// Размер стола по ходу раздачи не меняется — это не «динамический» HUD.
    /// </summary>
    private HudProfile Profile(BoundTable b)
    {
        var basic = Profile(b.Kind);
        if (!_svc.Settings.AutoProfileBySeats || b.MaxSeats <= 0) return basic;
        bool small = b.MaxSeats <= 6;
        if (_paired.TryGetValue((b.Kind, small), out var hit)) return hit;
        string want = small ? "6max" : "9max", other = small ? "9max" : "6max";
        var result = basic;
        int at = basic.Name.IndexOf(other, StringComparison.OrdinalIgnoreCase);
        if (at >= 0 && _profiles.Load(basic.Name[..at] + want + basic.Name[(at + other.Length)..]) is { } twin) result = twin;
        return _paired[(b.Kind, small)] = result;
    }

    private HudProfile Resolve(string? name, GameKind kind)
    {
        if (name == HudProfile.LegacyName)
            return HudProfile.FromCodes(kind == GameKind.Cash ? _svc.Settings.CashStats : _svc.Settings.MttStats,
                kind == GameKind.Cash ? "cash" : "mtt");
        return _profiles.Resolve(name, kind);
    }

    /// <summary>
    /// Панели скрыты значком. Сам значок при этом остаётся на столе — иначе включить HUD обратно
    /// было бы нечем, пришлось бы идти в настройки.
    /// </summary>
    private bool PanelsHidden(GameKind kind) => Current(kind) == ProfileBadge.Hidden;

    /// <summary>Перерисовать все панели (поменялись метки или вид статов).</summary>
    public void RefreshAll()
    {
        _svc.Host?.Notes.Invalidate();
        RefreshAllStats();
    }

    private void RefreshAllStats()
    {
        foreach (var b in _bound.Values) RefreshStats(b, b.Panels.Values.ToList());
    }

    private void RefreshStats(BoundTable b, List<HudPanelWindow> panels)
    {
        var host = _svc.Host;
        if (host == null || panels.Count == 0) return;
        var kind = b.Kind;
        var profile = Profile(b);
        var page = profile.Panel;
        var pages = profile.AllPanelPages().ToList();
        var work = panels.Select(p => (Panel: p, p.PlayerId)).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var (panel, pid) in work)
            {
                if (pid == 0)
                {
                    await _ui.InvokeAsync(() =>
                    {
                        panel.SetPages(pages, null, 0);
                        panel.SetMarker(null, null, null);
                    }, DispatcherPriority.Background);
                    continue;
                }
                try
                {
                    // Панель читает только агрегаты: девять игроков на шести столах по normalized
                    // не уложились бы в отрисовку между раздачами (§3.1, §15.9).
                    var agg = await host.Hud.GetAggAsync(pid, kind);
                    var values = pages.Select(pg => (CellValue[]?)agg.EvaluatePage(pg)).ToList();
                    long hands = agg.Evaluate("hands", null).Cases;
                    var note = await host.Notes.GetAsync(pid);
                    await _ui.InvokeAsync(() =>
                    {
                        if (panel.PlayerId != pid) return;
                        panel.Stats = host.Hud.Stats;
                        panel.SetPages(pages, values, hands);
                        var m = note?.Marker is { } mk ? _svc.Markers.Find(mk) : null;
                        panel.SetMarker(m?.Color, m?.Title, note?.Caption, m?.Hint);
                        if (_bound.TryGetValue(b.Window.Hwnd, out var bt)) Reposition(new[] { bt.Window });
                    }, DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    _svc.Log("Hud", $"Статы игрока {pid}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Что открывает ячейка панели. Открывается только вкладка попапа, привязанная к ячейке
    /// в редакторе: без привязки ячейка не реагирует ни на клик, ни на наведение. Иначе любая
    /// ячейка за столом открывала бы что-нибудь, и HUD дёргался бы от каждого движения мышью.
    /// </summary>
    private PopupTarget? Target(BoundTable b, HudPanelWindow panel, string? popupPage)
    {
        if (panel.PlayerId == 0 || popupPage == null) return null;
        var profile = Profile(b);
        if (profile.Page(popupPage) == null) return null;
        return new PopupTarget
        {
            PlayerId = panel.PlayerId, PlayerName = panel.PlayerName, Kind = b.Kind,
            Profile = profile, Page = popupPage,
        };
    }

    // ── Геометрия ────────────────────────────────────────────────────────────

    private void OnDragged(BoundTable b, HudPanelWindow panel, int dx, int dy)
    {
        var r = b.Window.Rect;
        if (r.Width <= 0 || r.Height <= 0) return;
        var key = LayoutKey(b, panel.Seat);
        var off = _layout.TryGetValue(key, out var o) ? o : new double[2];
        off[0] += (double)dx / r.Width;
        off[1] += (double)dy / r.Height;
        _layout[key] = off;
        Reposition(new[] { b.Window });
        SaveLayoutSoon();
    }

    private string LayoutKey(BoundTable b, int seat) => $"{b.MaxSeats}:{VisualIndex(b, seat)}";

    private static int VisualIndex(BoundTable b, int seat) => HudGeometry.VisualIndex(seat, b.HeroSeat, b.MaxSeats);

    private DispatcherTimer? _saveTimer;

    private void SaveLayoutSoon()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            try
            {
                Directory.CreateDirectory(TrackerSettings.Folder);
                File.WriteAllText(LayoutPath, JsonSerializer.Serialize(_layout));
            }
            catch (IOException) { }
        }, _ui);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Reposition(IReadOnlyCollection<TableWindow> windows)
    {
        if (!_enabled) return;
        var ours = new HashSet<IntPtr>(_bound.Values.SelectMany(b => b.Panels.Values).Select(p => p.Handle));
        foreach (var b in _bound.Values) if (b.Badge is { Handle: not 0 } bw) ours.Add(bw.Handle);
        foreach (var w in windows)
        {
            if (!_bound.TryGetValue(w.Hwnd, out var b) || (b.Panels.Count == 0 && b.Badge == null)) continue;
            var r = w.Rect;
            foreach (var panel in b.Panels.Values)
            {
                if (panel.Handle == IntPtr.Zero) continue;
                if (!w.Visible)
                {
                    if (panel.IsVisible) panel.Hide();
                    continue;
                }
                int v = VisualIndex(b, panel.Seat);
                var (fx, fy) = HudGeometry.Anchor(v, b.MaxSeats);
                if (_layout.TryGetValue($"{b.MaxSeats}:{v}", out var off)) { fx += off[0]; fy += off[1]; }
                var (pw, ph) = panel.PixelSize(w.Dpi);
                int x = r.Left + (int)(r.Width * fx) - pw / 2;
                int y = r.Top + (int)(r.Height * fy) - ph / 2;
                // Позиция — в физических пикселях через SetWindowPos, а не Window.Left/Top в DIP (§5.2);
                // вызывается только при реальном сдвиге — проверка перекрытия идёт 4 раза в секунду
                if (panel.LastX != x || panel.LastY != y)
                {
                    Win32.SetWindowPos(panel.Handle, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                    panel.LastX = x;
                    panel.LastY = y;
                }

                // Z-order: точки по бокам панели, но внутри окна стола; если хоть одна занята чужим окном —
                // стол (частично) перекрыт, и панель не должна рисоваться поверх чужого окна (§5.2)
                int cy = Math.Clamp(y + ph / 2, r.Top + 1, r.Bottom - 2);
                int left = Math.Clamp(x - 3, r.Left + 1, r.Right - 2);
                int right = Math.Clamp(x + pw + 3, r.Left + 1, r.Right - 2);
                bool covered = !Owns(w.Hwnd, ours, left, cy) || !Owns(w.Hwnd, ours, right, cy);
                if (covered && panel.IsVisible) panel.Hide();
                else if (!covered && !panel.IsVisible) panel.Show();
            }

            if (b.Badge is { Handle: not 0 } badge)
            {
                var off = _layout.TryGetValue(BadgeKey(b), out var o) ? o : new[] { 0.02, 0.02 };
                var (bw, bh) = badge.PixelSize(w.Dpi);
                int bx = r.Left + (int)(r.Width * off[0]);
                int by = r.Top + (int)(r.Height * off[1]);
                if (badge.LastX != bx || badge.LastY != by)
                {
                    Win32.SetWindowPos(badge.Handle, IntPtr.Zero, bx, by, 0, 0,
                        Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                    badge.LastX = bx;
                    badge.LastY = by;
                }
                if (!w.Visible && badge.IsVisible) badge.Hide();
                else if (w.Visible && !badge.IsVisible) badge.Show();
            }
        }
    }

    private static bool Owns(IntPtr table, HashSet<IntPtr> ours, int x, int y)
    {
        var hwnd = Win32.WindowFromPoint(new Win32.POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return false;
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        return root == table || ours.Contains(root);
    }

    public void CloseAllPanels()
    {
        _popups.Close();
        foreach (var b in _bound.Values)
        {
            foreach (var p in b.Panels.Values) p.Close();
            b.Panels.Clear();
            b.Badge?.Close();
            b.Badge = null;
        }
        _bound.Clear();
    }

    public void Dispose()
    {
        _tracker.Dispose();
        CloseAllPanels();
    }
}
