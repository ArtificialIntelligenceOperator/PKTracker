using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Hud;

/// <summary>
/// Мелкий значок, по клику на который выбирается раскладка HUD. Двигается мышью.
///
/// Отдельный элемент, а не пункт в настройках: раскладку меняют по ходу игры — увидел незнакомого
/// оппонента, переключился на разбор префлопа, — и лезть ради этого в главное окно не годится.
/// Выбор и положение запоминаются: значок бесполезен, если после перезапуска всё сбрасывается.
/// </summary>
public sealed class ProfileBadge : Border
{
    private readonly HudProfileStore _store = new();
    private readonly Func<GameKind> _kind;
    private readonly Action<string?> _onPicked;
    private readonly Func<string?> _current;
    private Point _dragStart;
    private bool _dragging;
    private bool _moved;

    /// <summary>Значок перетащили: смещение в DIP относительно прошлого положения.</summary>
    public event Action<double, double>? Dragged;

    public ProfileBadge(Func<GameKind> kind, Func<string?> current, Action<string?> onPicked)
    {
        _kind = kind;
        _current = current;
        _onPicked = onPicked;

        Width = 18;
        Height = 18;
        CornerRadius = new CornerRadius(9);
        Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x17, 0x12));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0x8A, 0x4A));
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        ToolTip = "Раскладка HUD: клик — выбрать, перетаскиванием — переместить";
        Child = new TextBlock
        {
            Text = "▤",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0xA4, 0x5A)),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragging = true;
        _moved = false;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = PointToScreen(e.GetPosition(this));
        double dx = now.X - _dragStart.X, dy = now.Y - _dragStart.Y;
        // Микродрожь при клике не должна засчитываться за перетаскивание, иначе меню не откроется.
        if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
        _dragStart = now;
        _moved = true;
        Dragged?.Invoke(dx, dy);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
        if (!_moved) ShowMenu();
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, IsOpen = false };
        string? current = _current();

        void Add(string header, string? value)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = string.Equals(current, value, StringComparison.Ordinal),
            };
            item.Click += (_, _) => _onPicked(value);
            menu.Items.Add(item);
        }

        Add("Встроенная (" + (_kind() == GameKind.Cash ? DefaultProfiles.Cash.Name : DefaultProfiles.Mtt.Name) + ")", null);
        var saved = _store.List();
        if (saved.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var name in saved) Add(name, name);
        }
        menu.Items.Add(new Separator());
        Add(HudProfile.LegacyName, HudProfile.LegacyName);
        menu.Items.Add(new Separator());
        var off = new MenuItem { Header = "Скрыть HUD", IsCheckable = true, IsChecked = current == Hidden };
        off.Click += (_, _) => _onPicked(Hidden);
        menu.Items.Add(off);

        menu.IsOpen = true;
    }

    /// <summary>Значение «HUD выключен» — отличается от null, который означает «встроенная раскладка».</summary>
    public const string Hidden = "\u0000hidden";
}
