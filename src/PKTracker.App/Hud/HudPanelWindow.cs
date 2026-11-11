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
    // Ник и число раздач — тёплым оранжевым, как в привычных HUD: шапка отличается от сетки статов с одного взгляда.
    private static readonly Brush HeaderInk = new SolidColorBrush(Color.FromRgb(0xF0, 0xA6, 0x3C));
    private readonly TextBlock _name = new()
    {
        FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = HeaderInk, Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _hands = new()
    {
        FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = HeaderInk, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 2, 0),
    };
    private readonly HudTabbedGrid _tabbed = new() { StripFontSize = 13 };
    private HudGridControl _grid => _tabbed.Grid;
    // Метка — цветной скруглённый квадрат: на чёрной панели он заметнее маленькой точки.
    private readonly System.Windows.Shapes.Rectangle _marker = new()
    {
        Width = 13, Height = 12, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _caption = new()
    {
        FontSize = 11, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly Border _border;
    private HudPage? _page => _tabbed.Page;
    private Point _dragStart;
    private bool _dragging;
    private bool _moved;

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

        // Шапка: ник слева, число раздач справа, между ними — метка и подпись к ней.
        var header = new DockPanel { Cursor = Cursors.SizeAll, Background = Brushes.Transparent, LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(_name, Dock.Left);
        DockPanel.SetDock(_hands, Dock.Right);
        DockPanel.SetDock(_marker, Dock.Left);
        header.Children.Add(_name);
        header.Children.Add(_hands);
        header.Children.Add(_marker);
        header.Children.Add(_caption);
        _grid.CellWeight = FontWeights.SemiBold;
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

        _tabbed.Header = header;
        var root = _tabbed;
        _border = new Border
        {
            // Чёрная подложка: над сукном любого цвета цветные цифры на чёрном читаются лучше всего.
            Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0.3, 1)), 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2, 1, 3, 2),
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
        // За местом другой игрок — метка и подпись прежнего к нему не относятся (свою он получит при обновлении статов).
        if (playerId != PlayerId) SetMarker(null, null, null);
        PlayerName = name;
        PlayerId = playerId;
        _name.Text = name.Length > 14 ? name[..13] + "…" : name;
        _name.ToolTip = name;
    }

    /// <summary>Отрисовать панель профиля с посчитанными значениями. Значения null — все ячейки в прочерках.</summary>
    public void SetPage(HudPage page, CellValue[]? values, long hands) => SetPages(new[] { page }, new[] { values }, hands);

    /// <summary>Панель с вкладками P / B / C / V: значения посчитаны для всех вкладок, видна одна.</summary>
    public void SetPages(IReadOnlyList<HudPage> pages, IReadOnlyList<CellValue[]?>? values, long hands)
    {
        _hands.Text = hands > 0 ? ShortCount(hands) : "—";
        _hands.ToolTip = hands > 0 ? $"Раздач в базе: {hands:N0}" : null;
        _tabbed.Render(pages, values);
    }

    /// <summary>Число раздач коротко, как в привычных HUD: 9k вместо 8 970, 1.5k вместо 1 480.</summary>
    internal static string ShortCount(long n) =>
        n < 1000 ? n.ToString()
        : n < 9950 ? (n / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : Math.Round(n / 1000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "k";

    /// <summary>
    /// Цветовая метка игрока: цветной значок и подпись рядом. Своей подписи у игрока нет — показывается название
    /// типа: метка читается сразу, без наведения, и не одним лишь цветом. null — без метки.
    /// </summary>
    /// <param name="hint">Описание типа из каталога меток — в подсказку при наведении.</param>
    public void SetMarker(string? color, string? title, string? caption, string? hint = null)
    {
        // Цвет вводится в каталоге меток руками: опечатка не должна срывать отрисовку панели.
        Brush? brush = color != null ? Views.PlayerMarkerMenu.Brush(color) : null;
        _marker.Fill = brush;
        _marker.Visibility = brush != null ? Visibility.Visible : Visibility.Collapsed;
        string? tip = title == null ? null : string.IsNullOrWhiteSpace(hint) ? title : title + "\n" + hint;
        _marker.ToolTip = tip;
        string? shown = string.IsNullOrWhiteSpace(caption) ? title : caption;
        var text = string.IsNullOrWhiteSpace(shown) ? null : shown!.Length > 18 ? shown[..17] + "…" : shown;
        _caption.Text = text ?? "";
        // Подпись — обычным светлым текстом: цвет несёт значок, а тёмные метки (тёмно-синяя, бордовая) текстом на тёмной панели не читаются.
        _caption.Foreground = Brushes.Gainsboro;
        _caption.ToolTip = string.IsNullOrWhiteSpace(caption) ? tip : caption + (tip != null ? "\n" + tip : "");
        _caption.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Правый клик без перетаскивания: меню панели (метка, подпись, заметка).</summary>
    public event Action<HudPanelWindow>? MenuRequested;

    private HudCell? Cell(int index) =>
        _page != null && index >= 0 && index < _page.Cells.Count ? _page.Cells[index] : null;

    private void HeaderDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragging = true;
        _moved = false;
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
        // Мёртвая зона: рука при обычном клике сдвигает мышь на пиксель-другой, и без неё правый клик
        // считался бы перетаскиванием — меню не открывалось, а панель съезжала.
        if (!_moved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
        _moved = true;
        _dragStart = now;
        DraggedBy?.Invoke(this, dx, dy);
    }

    private void HeaderUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        // Правую кнопку отпустили, не сдвинув панель, — это не перетаскивание, а запрос меню.
        if (!_moved && e.ChangedButton == MouseButton.Right) MenuRequested?.Invoke(this);
    }

    /// <summary>Размер панели в физических пикселях при заданном DPI.</summary>
    public (int W, int H) PixelSize(uint dpi) =>
        ((int)Math.Ceiling(ActualWidth * dpi / 96.0), (int)Math.Ceiling(ActualHeight * dpi / 96.0));
}
