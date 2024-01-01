using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Hud;

/// <summary>
/// Попап профиля: слева список вкладок, справа сетка (§5.4).
///
/// Срез игрока читается один раз на открытие и держится в памяти окна: переключение вкладок
/// не должно бить в базу, иначе за столом это ощущается как подвисание.
/// </summary>
public sealed class ProfilePopupWindow : Window, IPinnable
{
    private readonly TrackerHost _host;
    private readonly HudProfile _profile;
    private readonly long _playerId;
    private readonly string _playerName;
    private readonly GameKind _kind;
    private readonly Dispatcher _ui;

    private readonly ListBox _tabs = new();
    private readonly HudGridControl _grid = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 3, 8, 3), Opacity = 0.7, FontSize = 11 };
    private readonly System.Windows.Controls.Primitives.ToggleButton _pin = new()
    {
        Content = "📌 закрепить", Margin = new Thickness(8, 2, 8, 2), Padding = new Thickness(6, 1, 6, 1), FontSize = 11,
        ToolTip = "Не закрывать попап, когда курсор уходит (нужно в режиме открытия по наведению)",
    };
    private readonly PopupController? _popups;
    /// <summary>Последняя ячейка под курсором: к ней прицепляется попап стата при клике.</summary>
    private FrameworkElement? _hovered;

    /// <summary>Закреплённый попап контроллер больше не закрывает сам.</summary>
    public bool Pinned => _pin.IsChecked == true;
    public event Action<Window>? PinToggled;

    private PlayerRows? _rows;
    private readonly Dictionary<string, CellValue[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProfilePopupWindow(TrackerHost host, Dispatcher ui, string playerName, long playerId, GameKind kind,
        HudProfile profile, string? startPage, PopupController? popups = null)
    {
        _popups = popups;
        _host = host;
        _ui = ui;
        _playerId = playerId;
        _playerName = playerName;
        _kind = kind;
        _profile = profile;

        Title = $"{playerName} — {profile.Name}";
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        FontFamily = new FontFamily("Segoe UI");

        _grid.Stats = host.Hud.Stats;
        // Наведение на ячейку попапа открывает разбивку её стата — это второй уровень HUD.
        _grid.CellEnter += (index, el) =>
        {
            _hovered = el;
            if (Target(index) is { } t) _popups?.Enter(t, PopupAnchor.OfElementWithin(el, this));
        };
        _grid.CellLeave += index => { if (Target(index) is { } t) _popups?.Leave(t); };
        _grid.CellClicked += (index, _, _) =>
        {
            if (_hovered is { } el && Target(index) is { } t) _popups?.Click(t, PopupAnchor.OfElementWithin(el, this));
        };
        _pin.Checked += (_, _) => PinToggled?.Invoke(this);
        _pin.Unchecked += (_, _) => PinToggled?.Invoke(this);
        _scroll = new ScrollViewer
        {
            Content = _grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8),
        };

        _tabs.Width = 140;
        _tabs.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        _tabs.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        _tabs.BorderThickness = new Thickness(0);
        foreach (var page in profile.Pages) _tabs.Items.Add(page.Name);
        _tabs.SelectionChanged += (_, _) => ShowPage(_tabs.SelectedItem as string);

        var bottom = new DockPanel();
        DockPanel.SetDock(_pin, Dock.Right);
        bottom.Children.Add(_pin);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_tabs, Dock.Left);
        root.Children.Add(bottom);
        root.Children.Add(_tabs);
        root.Children.Add(_scroll);
        Content = root;

        int index = startPage != null ? profile.Pages.FindIndex(p => string.Equals(p.Name, startPage, StringComparison.OrdinalIgnoreCase)) : 0;
        _tabs.SelectedIndex = index >= 0 ? index : 0;

        Loaded += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = "Чтение раздач игрока…";
        try
        {
            // Запрос в фоне: окно попапа живёт в UI-потоке, а срез — это десятки тысяч строк (§10.2).
            var rows = await _host.Hud.GetRowsAsync(_playerId, _kind);
            await _ui.InvokeAsync(() =>
            {
                _rows = rows;
                _cache.Clear();
                _status.Text = rows.Truncated
                    ? $"Раздач: {rows.Facts.Count}, решений: {rows.Decisions.Count} (выборка урезана лимитом)"
                    : $"Раздач: {rows.Facts.Count}, решений: {rows.Decisions.Count}";
                ShowPage(_tabs.SelectedItem as string);
            });
        }
        catch (Exception ex)
        {
            await _ui.InvokeAsync(() => _status.Text = "Ошибка: " + ex.Message);
        }
    }

    /// <summary>Что открыть при наведении на ячейку: разбивку её стата. У подписи стата нет.</summary>
    private PopupTarget? Target(int index)
    {
        if (_tabs.SelectedItem is not string name || _profile.Page(name) is not { } page) return null;
        if (index < 0 || index >= page.Cells.Count || page.Cells[index].Stat is not { } code) return null;
        return new PopupTarget { PlayerId = _playerId, PlayerName = _playerName, Kind = _kind, StatCode = code };
    }

    private void ShowPage(string? name)
    {
        if (name == null || _profile.Page(name) is not { } page) return;
        if (_rows == null)
        {
            _grid.Render(page, null);
            return;
        }
        if (!_cache.TryGetValue(name, out var values))
        {
            // Считается синхронно: страница на сотню ячеек по готовому срезу — это миллисекунды,
            // а уход в фон ради них дал бы мигание при переключении вкладок.
            values = new PageEvaluator(_host.Hud.Stats).Evaluate(page, _rows);
            _cache[name] = values;
        }
        _grid.Render(page, values);
    }
}
