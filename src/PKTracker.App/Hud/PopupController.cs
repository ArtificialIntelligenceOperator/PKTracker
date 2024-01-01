using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using PKTracker.App.Infrastructure;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Hud;

/// <summary>
/// К чему прицепить попап — прямоугольник на экране в физических пикселях. Панель HUD это
/// отдельное окно, а ячейка попапа — элемент внутри окна, и без общей системы координат
/// размещать попап пришлось бы двумя разными способами.
/// </summary>
public readonly record struct PopupAnchor(int Left, int Top, int Right, int Bottom)
{
    public static PopupAnchor OfWindow(IntPtr hwnd)
    {
        Win32.GetWindowRect(hwnd, out var r);
        return new PopupAnchor(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static PopupAnchor OfElement(FrameworkElement el)
    {
        // PointToScreen отдаёт уже физические пиксели — то же, в чём работает SetWindowPos.
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        return new PopupAnchor((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
    }

    /// <summary>
    /// Ячейка внутри окна: по вертикали — сама ячейка, по горизонтали — всё окно. Иначе попап
    /// встал бы поверх той самой таблицы, из которой его открыли, и закрыл бы соседние статы.
    /// </summary>
    public static PopupAnchor OfElementWithin(FrameworkElement el, Window owner)
    {
        var cell = OfElement(el);
        var win = OfElement(owner);
        return new PopupAnchor(win.Left, cell.Top, win.Right, cell.Bottom);
    }
}

/// <summary>Окно, которым управляет <see cref="PopupController"/>: закреплённое он больше не закрывает.</summary>
public interface IPinnable
{
    bool Pinned { get; }
    event Action<Window>? PinToggled;
}

/// <summary>
/// Открывает и закрывает попапы по правилам из настроек: по наведению с задержкой (и тогда сам
/// закрывает, когда курсор ушёл) или только по клику.
///
/// Попапы вложены: с панели открывается вкладка профиля, из неё — разбивка стата. Поэтому
/// уровней два, у каждого свой слот, и закрытие идёт сверху вниз: курсор на разбивке не даёт
/// закрыться ни ей, ни вкладке под ней. Переход курсора со стата на сам попап его тоже не
/// закрывает — иначе попап нельзя было бы пролистать. Закреплённый булавкой попап контроллер
/// отпускает: он живёт, пока его не закроют, а следующее наведение откроет новый.
/// </summary>
public sealed class PopupController
{
    private sealed record CacheKey(long PlayerId, string Code, GameKind Kind);

    /// <summary>Один уровень вложенности попапов.</summary>
    private sealed class Slot
    {
        public Window? Window;
        public object? Key;
        /// <summary>Курсор над этим окном: пока он там, не закрывается ни оно, ни уровни под ним.</summary>
        public bool Over;
        public int Generation;
        public DispatcherTimer Hide = null!;
    }

    private readonly Func<TrackerSettings> _settings;
    private readonly Func<TrackerHost?> _host;
    private readonly Action<string> _log;
    private readonly bool _overlay;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _showTimer;
    private readonly Slot[] _slots;
    private readonly ConcurrentDictionary<CacheKey, (DateTime At, StatPopupData Data)> _cache = new();

    private (PopupTarget Target, PopupAnchor Anchor)? _pending;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <param name="overlay">
    /// Попапы висят поверх стола рума и не забирают у него фокус. В реплеере это не нужно:
    /// там обычное окно приложения, и топмост-попап поверх него только мешал бы.
    /// </param>
    public PopupController(Func<TrackerSettings> settings, Func<TrackerHost?> host, Action<string> log, Dispatcher ui,
        bool overlay = true)
    {
        _settings = settings;
        _host = host;
        _log = log;
        _overlay = overlay;
        _ui = ui;
        _showTimer = new DispatcherTimer(DispatcherPriority.Input, ui);
        _showTimer.Tick += (_, _) => { _showTimer.Stop(); if (_pending is { } p) Open(p.Target, p.Anchor); };
        _slots = new[] { new Slot(), new Slot() };
        for (int i = 0; i < _slots.Length; i++)
        {
            int level = i;
            var t = new DispatcherTimer(DispatcherPriority.Input, ui);
            t.Tick += (_, _) => { t.Stop(); if (!OverAtOrAbove(level)) CloseFrom(level); };
            _slots[i].Hide = t;
        }
    }

    public PopupTrigger Trigger => _settings().PopupTrigger;
    public bool HoverMode => Trigger == PopupTrigger.Hover;

    /// <summary>Курсор зашёл на ячейку. В режиме клика ничего не делает.</summary>
    public void Enter(PopupTarget target, PopupAnchor anchor)
    {
        if (!HoverMode || target.PlayerId == 0) return;
        // Курсор на ячейке этого уровня держит открытым и его, и всё под ним.
        for (int i = 0; i <= target.Level; i++) _slots[i].Hide.Stop();
        if (Equals(_slots[target.Level].Key, target.Key)) return;
        _pending = (target, anchor);
        _showTimer.Stop();
        _showTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings().HoverDelayMs, 0, 10_000));
        _showTimer.Start();
    }

    /// <summary>Курсор ушёл с ячейки: закрываем, если он не перешёл на сам попап.</summary>
    public void Leave(PopupTarget target)
    {
        if (!HoverMode) return;
        if (_pending is { } p && Equals(p.Target.Key, target.Key))
        {
            _showTimer.Stop();
            _pending = null;
        }
        if (_slots[target.Level].Window != null) StartHide(target.Level);
    }

    /// <summary>
    /// Клик по ячейке. В режиме клика открывает и закрывает повторным кликом по той же ячейке,
    /// в режиме наведения — открывает сразу, не дожидаясь задержки.
    /// </summary>
    public void Click(PopupTarget target, PopupAnchor anchor)
    {
        if (target.PlayerId == 0) return;
        _showTimer.Stop();
        _pending = null;
        if (Equals(_slots[target.Level].Key, target.Key)) { CloseFrom(target.Level); return; }
        Open(target, anchor);
    }

    private bool OverAtOrAbove(int level)
    {
        for (int i = level; i < _slots.Length; i++)
            if (_slots[i].Over) return true;
        return false;
    }

    private void StartHide(int level)
    {
        var t = _slots[level].Hide;
        t.Stop();
        t.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings().HoverHideMs, 0, 10_000));
        t.Start();
    }

    private void Open(PopupTarget target, PopupAnchor anchor)
    {
        var host = _host();
        _pending = null;
        if (host == null || target.PlayerId == 0) return;

        int level = target.Level;
        CloseFrom(level);                       // свой уровень и всё, что над ним
        var slot = _slots[level];
        slot.Key = target.Key;
        int gen = ++slot.Generation;

        if (target.Profile is { } profile)
        {
            var page = new ProfilePopupWindow(host, _ui, target.PlayerName, target.PlayerId, target.Kind, profile, target.Page, this)
            {
                ShowActivated = !_overlay,
            };
            Adopt(page, level);
            // Не по центру экрана: в режиме наведения до окна нужно успеть довести курсор,
            // пока не сработало автозакрытие.
            page.WindowStartupLocation = WindowStartupLocation.Manual;
            page.Show();
            PlaceNear(page, anchor);
            return;
        }

        if (target.StatCode is not { } code || !host.Hud.Stats.TryGetValue(code, out var stat)) { slot.Key = null; return; }
        var win = new StatDetailWindow(host, _overlay);
        Adopt(win, level);
        win.ShowLoading(target.PlayerName, stat);
        win.Show();
        win.PlaceNear(anchor);
        _ = FillAsync(gen, level, host, win, target, stat, anchor);
    }

    private async Task FillAsync(int gen, int level, TrackerHost host, StatDetailWindow win, PopupTarget t,
        Engine.Stats.CompiledStat stat, PopupAnchor anchor)
    {
        try
        {
            var key = new CacheKey(t.PlayerId, stat.Code, t.Kind);
            var set = await Task.Run(() => host.Hud.GetAsync(t.PlayerId, t.Kind));
            StatPopupData data;
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl) data = hit.Data;
            else
            {
                data = await Task.Run(() => new StatPopupService(host.Db).LoadAsync(t.PlayerId, stat, t.Kind));
                _cache[key] = (DateTime.UtcNow, data);
            }
            // Курсор уже ушёл на другой стат — это окно больше никому не нужно.
            if (gen != _slots[level].Generation || !win.IsVisible) return;
            win.ShowData(t.PlayerName, stat, set, data, t.Kind);
            win.PlaceNear(anchor);
        }
        catch (Exception ex)
        {
            _log("Попап стата: " + ex.Message);
        }
    }

    /// <summary>Рядом с тем, на что навели: справа, а если не помещается на мониторе — слева.</summary>
    private static void PlaceNear(Window w, PopupAnchor anchor)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        uint dpi = Win32.GetDpiForWindow(hwnd);
        int width = (int)Math.Ceiling(w.Width * dpi / 96.0), height = (int)Math.Ceiling(w.Height * dpi / 96.0);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchor.Left, anchor.Top)).WorkingArea;
        int x = anchor.Right + 6;
        if (x + width > screen.Right) x = Math.Max(screen.Left, anchor.Left - width - 6);
        int y = Math.Clamp(anchor.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Взять окно под управление: следить за курсором над ним, булавкой и закрытием.</summary>
    private void Adopt(Window w, int level)
    {
        var slot = _slots[level];
        slot.Window = w;
        slot.Over = false;
        w.MouseEnter += (_, _) =>
        {
            slot.Over = true;
            for (int i = 0; i <= level; i++) _slots[i].Hide.Stop();
        };
        w.MouseLeave += (_, _) =>
        {
            slot.Over = false;
            if (HoverMode) StartHide(level);
        };
        w.Closed += (_, _) =>
        {
            if (!ReferenceEquals(slot.Window, w)) return;
            slot.Window = null;
            slot.Key = null;
            slot.Over = false;
            CloseFrom(level + 1);   // попап без родителя висел бы сиротой
        };
        if (w is IPinnable pin)
            pin.PinToggled += pinned =>
            {
                // Закреплённый попап живёт сам по себе: слот освобождается, и следующее наведение
                // откроет новое окно рядом — так и сравнивают два спота.
                if (!ReferenceEquals(slot.Window, pinned) || !((IPinnable)pinned).Pinned) return;
                slot.Window = null;
                slot.Key = null;
                slot.Over = false;
                slot.Hide.Stop();
            };
    }

    /// <summary>Закрыть уровень и все, что над ним; закреплённые окна только отпускаются.</summary>
    private void CloseFrom(int level)
    {
        for (int i = _slots.Length - 1; i >= level; i--)
        {
            var slot = _slots[i];
            var w = slot.Window;
            slot.Hide.Stop();
            slot.Window = null;
            slot.Key = null;
            slot.Over = false;
            slot.Generation++;
            if (w is IPinnable { Pinned: true }) continue;
            w?.Close();
        }
    }

    public void Hide()
    {
        _showTimer.Stop();
        _pending = null;
        CloseFrom(0);
    }

    /// <summary>После импорта новых рук выборка изменилась.</summary>
    public void Invalidate(IEnumerable<long> players)
    {
        var set = players.ToHashSet();
        foreach (var k in _cache.Keys.Where(k => set.Contains(k.PlayerId)).ToList()) _cache.TryRemove(k, out _);
    }

    public void Close() => Hide();
}
