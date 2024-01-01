using System.Diagnostics;
using System.Windows.Threading;
using PKTracker.App.Infrastructure;

namespace PKTracker.App.Hud;

public sealed class TableWindow
{
    public IntPtr Hwnd { get; init; }
    public uint ProcessId { get; init; }
    public string Title { get; set; } = "";
    public Win32.RECT Rect { get; set; }
    public bool Visible { get; set; }
    public bool Minimized { get; set; }
    public uint Dpi { get; set; } = 96;
}

/// <summary>
/// Слежение за окнами столов клиента (§5.2). Хук ставится из UI-потока (у него есть цикл сообщений) — хук из
/// Task.Run молча никогда не сработает. Колбэк только помечает окно «грязным», перепозиционирование — по таймеру 60 Гц.
/// </summary>
public sealed class TableWindowTracker : IDisposable
{
    private readonly Func<string> _processName;
    private readonly Func<string> _windowClass;
    private readonly Dictionary<IntPtr, TableWindow> _windows = new();
    private readonly HashSet<IntPtr> _dirty = new();
    private readonly List<IntPtr> _hooks = new();
    private readonly Win32.WinEventDelegate _callback; // держим ссылку — иначе GC соберёт делегат
    private readonly DispatcherTimer _scanTimer;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _occlusionTimer;
    private IntPtr _globalForegroundHook;
    private HashSet<uint> _pids = new();
    private bool _allDirty;

    /// <summary>Окна, у которых изменилась геометрия/видимость (вызывается не чаще 60 раз в секунду).</summary>
    public event Action<IReadOnlyCollection<TableWindow>>? WindowsMoved;
    /// <summary>Появились/исчезли окна или сменился заголовок.</summary>
    public event Action<IReadOnlyCollection<TableWindow>>? WindowsChanged;

    public IReadOnlyCollection<TableWindow> Windows => _windows.Values;

    public TableWindowTracker(Func<string> processName, Func<string> windowClass)
    {
        _processName = processName;
        _windowClass = windowClass;
        _callback = OnWinEvent;
        _scanTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _scanTimer.Tick += (_, _) => Scan();
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frameTimer.Tick += (_, _) => Flush();
        // Перекрытие стола окном другого приложения событий клиента не порождает — перепроверяем по таймеру
        _occlusionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _occlusionTimer.Tick += (_, _) => { if (_windows.Count > 0) _allDirty = true; };
    }

    public void Start()
    {
        Scan();
        if (_globalForegroundHook == IntPtr.Zero)
            _globalForegroundHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _callback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        _scanTimer.Start();
        _frameTimer.Start();
        _occlusionTimer.Start();
    }

    private void Scan()
    {
        var name = _processName();
        var pids = new HashSet<uint>();
        try
        {
            foreach (var p in Process.GetProcessesByName(name)) { pids.Add((uint)p.Id); p.Dispose(); }
        }
        catch (InvalidOperationException) { }

        if (!pids.SetEquals(_pids))
        {
            _pids = pids;
            Rehook();
        }

        var found = new HashSet<IntPtr>();
        var cls = _windowClass();
        bool changed = false;
        if (pids.Count > 0)
        {
            Win32.EnumWindows((hwnd, _) =>
            {
                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains(pid)) return true;
                if (!Win32.IsWindowVisible(hwnd)) return true;
                if (cls.Length > 0 && Win32.ClassName(hwnd) != cls) return true;
                if (!Win32.GetWindowRect(hwnd, out var rect) || rect.Width < 200 || rect.Height < 150) return true;
                var title = Win32.WindowText(hwnd);
                if (string.IsNullOrWhiteSpace(title)) return true;
                found.Add(hwnd);
                if (!_windows.TryGetValue(hwnd, out var w))
                {
                    _windows[hwnd] = new TableWindow { Hwnd = hwnd, ProcessId = pid, Title = title, Rect = rect, Visible = true, Dpi = Win32.GetDpiForWindow(hwnd) };
                    changed = true;
                }
                else if (w.Title != title)
                {
                    w.Title = title;
                    changed = true;
                }
                return true;
            }, IntPtr.Zero);
        }

        foreach (var gone in _windows.Keys.Where(k => !found.Contains(k)).ToList())
        {
            _windows.Remove(gone);
            changed = true;
        }
        if (changed) WindowsChanged?.Invoke(_windows.Values.ToList());
        _allDirty = true;
    }

    private void Rehook()
    {
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
        foreach (var pid in _pids)
        {
            _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _callback, pid, 0, Win32.WINEVENT_OUTOFCONTEXT));
            _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _callback, pid, 0, Win32.WINEVENT_OUTOFCONTEXT));
        }
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW) return;
        if (evt == Win32.EVENT_SYSTEM_FOREGROUND) { _allDirty = true; return; } // z-order изменился
        if (_windows.ContainsKey(hwnd)) _dirty.Add(hwnd);
        else if (evt is Win32.EVENT_OBJECT_SHOW or Win32.EVENT_OBJECT_NAMECHANGE) _scanTimer.Interval = TimeSpan.FromMilliseconds(100);
    }

    private void Flush()
    {
        if (_scanTimer.Interval.TotalMilliseconds < 1000)
        {
            _scanTimer.Interval = TimeSpan.FromSeconds(1);
            Scan();
        }
        if (!_allDirty && _dirty.Count == 0) return;
        var moved = new List<TableWindow>();
        IEnumerable<IntPtr> targets = _allDirty ? _windows.Keys.ToList() : _dirty.ToList();
        foreach (var hwnd in targets)
        {
            if (!_windows.TryGetValue(hwnd, out var w)) continue;
            if (!Win32.IsWindow(hwnd)) { _windows.Remove(hwnd); continue; }
            Win32.GetWindowRect(hwnd, out var rect);
            w.Rect = rect;
            w.Minimized = Win32.IsIconic(hwnd);
            w.Visible = Win32.IsWindowVisible(hwnd) && !w.Minimized;
            w.Dpi = Win32.GetDpiForWindow(hwnd);
            moved.Add(w);
        }
        _dirty.Clear();
        _allDirty = false;
        if (moved.Count > 0) WindowsMoved?.Invoke(moved);
    }

    public void Dispose()
    {
        _scanTimer.Stop();
        _frameTimer.Stop();
        _occlusionTimer.Stop();
        if (_globalForegroundHook != IntPtr.Zero) Win32.UnhookWinEvent(_globalForegroundHook);
        _globalForegroundHook = IntPtr.Zero;
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
