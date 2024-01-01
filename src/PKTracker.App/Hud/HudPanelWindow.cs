using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PKTracker.App.Infrastructure;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.App.Hud;

/// <summary>
/// Одна панель игрока = одно маленькое layered-окно (§5.2). Между панелями окон нет — клики по столу проходят сами.
/// Содержимое — страница раскладки «Панель» из профиля, поэтому панель и попап рисует один и тот же код.
/// </summary>
public sealed class HudPanelWindow : Window
{
    private readonly TextBlock _name = new()
    {
        FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0xE6, 0xDA)),
        Margin = new Thickness(0, 0, 6, 0),
    };
    private readonly TextBlock _hands = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x8A, 0x4A)) };
    private readonly HudGridControl _grid = new();
    private readonly Border _border;
    private HudPage? _page;
    private Point _dragStart;
    private bool _dragging;

    public IntPtr Handle { get; private set; }
    /// <summary>Последняя выставленная позиция (физические пиксели) — чтобы не дёргать SetWindowPos без нужды.</summary>
    public int LastX { get; set; } = int.MinValue;
    public int LastY { get; set; } = int.MinValue;
    public int Seat { get; init; }
    public long PlayerId { get; set; }
    public string PlayerName { get; private set; } = "";

    /// <summary>Клик по ячейке: код стата и вкладка попапа, если она у ячейки задана.</summary>
    public event Action<HudPanelWindow, string?, string?>? CellClicked;
    /// <summary>Курсор зашёл на ячейку и ушёл с неё — те же код стата и вкладка попапа.</summary>
    public event Action<HudPanelWindow, string?, string?>? CellEnter;
    public event Action<HudPanelWindow, string?, string?>? CellLeave;
    /// <summary>При попапе по наведению обычная подсказка не нужна — она бы дублировала его.</summary>
    public bool ShowTooltips
    {
        get => _grid.ShowTooltips;
        set => _grid.ShowTooltips = value;
    }
    /// <summary>Пользователь перетащил панель: смещение в физических пикселях.</summary>
    public event Action<HudPanelWindow, int, int>? DraggedBy;

    public HudPanelWindow(double opacity)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 11;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.SizeAll };
        header.Children.Add(_name);
        header.Children.Add(_hands);
        header.MouseLeftButtonDown += HeaderDown;
        header.MouseMove += HeaderMove;
        header.MouseLeftButtonUp += HeaderUp;

        _grid.CellClicked += (index, _, _) =>
        {
            if (Cell(index) is { } cell) CellClicked?.Invoke(this, cell.Stat, cell.Popup);
        };
        _grid.CellEnter += (index, _) =>
        {
            // Во время перетаскивания панели попапы не нужны: курсор идёт по ячейкам насквозь.
            if (!_dragging && Cell(index) is { } cell) CellEnter?.Invoke(this, cell.Stat, cell.Popup);
        };
        _grid.CellLeave += index =>
        {
            if (Cell(index) is { } cell) CellLeave?.Invoke(this, cell.Stat, cell.Popup);
        };

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(_grid);
        _border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0.3, 1)), 0x1E, 0x17, 0x12)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x6E, 0x4A, 0x2C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 3),
            Child = root,
        };
        // Заголовок — узкая полоска, а попасть по ней над столом трудно. Поэтому панель
        // таскается ещё и правой кнопкой, зажатой в любом месте: левая занята попапами.
        _border.MouseRightButtonDown += HeaderDown;
        _border.MouseMove += HeaderMove;
        _border.MouseRightButtonUp += HeaderUp;

        Content = _border;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            Win32.MakeOverlay(Handle);
        };
    }

    public IReadOnlyDictionary<string, CompiledStat> Stats
    {
        get => _grid.Stats;
        set => _grid.Stats = value;
    }

    public void SetPlayer(string name, long playerId)
    {
        PlayerName = name;
        PlayerId = playerId;
        _name.Text = name.Length > 14 ? name[..13] + "…" : name;
        _name.ToolTip = name;
    }

    /// <summary>Отрисовать панель профиля с посчитанными значениями. Значения null — все ячейки в прочерках.</summary>
    public void SetPage(HudPage page, CellValue[]? values, long hands)
    {
        _page = page;
        _hands.Text = hands > 0 ? hands.ToString() : "—";
        _grid.Render(page, values);
    }

    private HudCell? Cell(int index) =>
        _page != null && index >= 0 && index < _page.Cells.Count ? _page.Cells[index] : null;

    private void HeaderDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
    }

    private void HeaderMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        // Обработчик висит и на заголовке, и на всей панели; без этого движение над заголовком
        // всплыло бы до панели и сдвинуло её вдвое.
        e.Handled = true;
        var now = PointToScreen(e.GetPosition(this));
        int dx = (int)(now.X - _dragStart.X), dy = (int)(now.Y - _dragStart.Y);
        if (dx == 0 && dy == 0) return;
        _dragStart = now;
        DraggedBy?.Invoke(this, dx, dy);
    }

    private void HeaderUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    /// <summary>Размер панели в физических пикселях при заданном DPI.</summary>
    public (int W, int H) PixelSize(uint dpi) =>
        ((int)Math.Ceiling(ActualWidth * dpi / 96.0), (int)Math.Ceiling(ActualHeight * dpi / 96.0));
}
