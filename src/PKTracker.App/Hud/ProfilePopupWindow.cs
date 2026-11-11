using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Hud;

/// <summary>
/// Попап профиля: слева группы вкладок, сверху вкладки группы, под ними сетка (§5.4).
/// Профиль без групп выглядит как раньше: одна группа, вкладки сверху.
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

    private readonly ListBox _groups = new();
    private readonly ListBox _tabs = new();
    // Подсказка под вкладками — светлым приглушённым текстом: тёмный по умолчанию на тёмном фоне попапа не виден.
    private readonly TextBlock _note = new()
    {
        Margin = new Thickness(8, 2, 8, 0), FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xA1, 0xBD)),
    };
    private readonly List<string?> _groupKeys = new();
    private HudPage? _current;
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
    private HudPage? _startPage;

    private void FillTabs()
    {
        if (_groups.SelectedIndex < 0) return;
        var group = _groupKeys[_groups.SelectedIndex];
        _tabs.Items.Clear();
        int select = 0;
        foreach (var page in _profile.Pages.Where(p => p.Group == group))
        {
            if (ReferenceEquals(page, _startPage)) select = _tabs.Items.Count;
            _tabs.Items.Add(new ListBoxItem { Content = page.Name, Tag = page, Padding = new Thickness(10, 3, 10, 3) });
        }
        _startPage = null;
        _tabs.SelectedIndex = _tabs.Items.Count > 0 ? select : -1;
    }
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
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x14, 0x24));
        // Светлый текст по умолчанию: строка состояния и подписи наследуют его, иначе на тёмном фоне их не видно.
        Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8));
        FontFamily = new FontFamily("Segoe UI");
        Infrastructure.Win32.ApplyDarkFrame(this);

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

        var panelBg = new SolidColorBrush(Color.FromRgb(0x1D, 0x1C, 0x30));
        var fg = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8));
        _groups.Width = 110;
        _groups.Background = panelBg;
        _groups.Foreground = fg;
        _groups.BorderThickness = new Thickness(0);
        foreach (var g in profile.Pages.Select(p => p.Group).Distinct())
        {
            _groupKeys.Add(g);
            _groups.Items.Add(new ListBoxItem { Content = g ?? "Вкладки", FontWeight = FontWeights.SemiBold, Padding = new Thickness(8, 5, 8, 5) });
        }
        _groups.SelectionChanged += (_, _) => FillTabs();
        // Вкладки группы — горизонтальной полосой, как в Hand2Note: группа слева, её вкладки сверху.
        _tabs.Background = Brushes.Transparent;
        _tabs.Foreground = fg;
        _tabs.BorderThickness = new Thickness(0);
        _tabs.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        // Без этого ListBox даёт панели бесконечную ширину, и WrapPanel ничего не переносит.
        ScrollViewer.SetHorizontalScrollBarVisibility(_tabs, ScrollBarVisibility.Disabled);
        _tabs.SelectionChanged += (_, _) => ShowPage((_tabs.SelectedItem as ListBoxItem)?.Tag as HudPage);

        var bottom = new DockPanel();
        DockPanel.SetDock(_pin, Dock.Right);
        bottom.Children.Add(_pin);
        bottom.Children.Add(_status);

        var top = new StackPanel();
        top.Children.Add(_tabs);
        top.Children.Add(_note);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_groups, Dock.Left);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(bottom);
        if (_groupKeys.Count > 1) root.Children.Add(_groups);
        root.Children.Add(top);
        root.Children.Add(_scroll);
        Content = root;

        // Ячейка панели ссылается на вкладку «группа/вкладка» или просто по имени.
        var start = startPage != null ? profile.Page(startPage) : null;
        _startPage = start ?? profile.Pages.FirstOrDefault();
        int gi = _startPage != null ? _groupKeys.IndexOf(_startPage.Group) : 0;
        _groups.SelectedIndex = gi >= 0 ? gi : 0;

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
                ShowPage(_current);
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
        if (_current is not { } page) return null;
        if (index < 0 || index >= page.Cells.Count || page.Cells[index].Stat is not { } code) return null;
        return new PopupTarget { PlayerId = _playerId, PlayerName = _playerName, Kind = _kind, StatCode = code };
    }

    private void ShowPage(HudPage? page)
    {
        if (page == null) return;
        _current = page;
        _note.Text = page.Note ?? "";
        _note.Visibility = string.IsNullOrEmpty(page.Note) ? Visibility.Collapsed : Visibility.Visible;
        var name = HudProfile.PageKey(page);
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
