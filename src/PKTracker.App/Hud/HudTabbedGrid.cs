using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Hud;

/// <summary>
/// Панель HUD с вкладками-буквами слева (P / B / C / V…). Видна одна вкладка; переключение —
/// только кликом, прямо за столом. Автоматическое переключение по ходу раздачи («динамический» HUD)
/// намеренно не сделано: оно запрещено правилами iPoker и Winamax, а на остальных румах сбивает с толку.
///
/// Полоса букв занимает всю высоту панели — вместе с шапкой (ник, раздачи), которую владелец кладёт
/// в <see cref="Header"/>: буквы получаются крупными мишенями, в них легко попасть между раздачами.
/// </summary>
public sealed class HudTabbedGrid : DockPanel
{
    private static readonly Brush ActiveFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush IdleFg = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));

    /// <summary>Вкладка, выбранная последней на любой панели: новые панели открываются на ней.</summary>
    public static int RememberedTab { get; set; }

    private readonly UniformGrid _strip = new() { Columns = 1, Margin = new Thickness(0, 0, 4, 0), MinWidth = 16 };
    private readonly StackPanel _body = new();
    private readonly ContentPresenter _header = new();
    private IReadOnlyList<HudPage> _pages = Array.Empty<HudPage>();
    private IReadOnlyList<CellValue[]?> _values = Array.Empty<CellValue[]?>();

    public HudGridControl Grid { get; } = new();
    public int Selected { get; private set; } = -1;
    public HudPage? Page => Selected >= 0 && Selected < _pages.Count ? _pages[Selected] : null;
    public event Action<int>? TabChanged;

    /// <summary>Шапка над сеткой (ник, число раздач, метка). Полоса вкладок идёт и вдоль неё.</summary>
    public UIElement? Header
    {
        get => _header.Content as UIElement;
        set => _header.Content = value;
    }

    /// <summary>Размер букв полосы вкладок.</summary>
    public double StripFontSize { get; set; } = 11;

    public HudTabbedGrid()
    {
        LastChildFill = true;
        SetDock(_strip, Dock.Left);
        Children.Add(_strip);
        _body.Children.Add(_header);
        _body.Children.Add(Grid);
        Children.Add(_body);
    }

    public void Render(IReadOnlyList<HudPage> pages, IReadOnlyList<CellValue[]?>? values)
    {
        bool same = pages.Count == _pages.Count && pages.Select(p => p.Name).SequenceEqual(_pages.Select(p => p.Name));
        _pages = pages;
        _values = values ?? pages.Select(_ => (CellValue[]?)null).ToList();
        if (!same || Selected < 0 || Selected >= pages.Count) Selected = Math.Clamp(RememberedTab, 0, Math.Max(0, pages.Count - 1));
        BuildStrip();
        Show();
    }

    private void BuildStrip()
    {
        _strip.Children.Clear();
        _strip.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _strip.Rows = Math.Max(1, _pages.Count);
        for (int i = 0; i < _pages.Count; i++)
        {
            int index = i;
            var name = _pages[i].Name;
            bool on = i == Selected;
            var t = new TextBlock
            {
                Text = name.Length > 3 ? name[..3] : name,
                FontSize = StripFontSize,
                FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = on ? ActiveFg : IdleFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Выбранная вкладка отмечена и полужирным белым, и чертой слева — не одним лишь цветом буквы.
            var cell = new Border
            {
                Child = t, ToolTip = name, Cursor = Cursors.Hand, Background = Brushes.Transparent, Padding = new Thickness(3, 0, 2, 0),
                BorderBrush = on ? ActiveFg : Brushes.Transparent, BorderThickness = new Thickness(2, 0, 0, 0),
            };
            cell.MouseLeftButtonUp += (_, e) => { Select(index); e.Handled = true; };
            _strip.Children.Add(cell);
        }
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _pages.Count || index == Selected) return;
        Selected = index;
        RememberedTab = index;
        BuildStrip();
        Show();
        TabChanged?.Invoke(index);
    }

    private void Show()
    {
        if (Page is { } page) Grid.Render(page, Selected < _values.Count ? _values[Selected] : null);
    }
}
