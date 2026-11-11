using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PKTracker.Data;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.App.Hud;

/// <summary>
/// Привязка ячеек панели HUD к вкладкам попапа — всё одной таблицей. В редакторе то же самое делается
/// по одной ячейке; здесь удобно пройтись по всей панели сразу и увидеть, что осталось непривязанным.
/// </summary>
public sealed class PopupBindingWindow : Window
{
    private sealed class Row
    {
        public required int TabIndex { get; init; }
        public required int CellIndex { get; init; }
        public required string Tab { get; init; }
        public required string Where { get; init; }
        public required string Stat { get; init; }
        public required string Filter { get; init; }
        public string Popup { get; set; } = "";
    }

    private const string None = "(нет)";
    private readonly HudProfile _profile;
    private readonly HudProfileStore _store = new();
    private readonly List<Row> _rows = new();
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), Opacity = 0.75 };
    private readonly Action<string> _saved;

    /// <param name="saved">Вызывается после сохранения с именем профиля (у встроенной раскладки оно новое).</param>
    public PopupBindingWindow(HudProfile profile, IReadOnlyDictionary<string, CompiledStat> stats, Action<string> saved)
    {
        _profile = HudProfile.FromJson(profile.ToJson());
        _saved = saved;
        Title = $"Привязка попапов — {profile.Name}";
        Width = 900;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg0");
        Foreground = (Brush)Application.Current.FindResource("Text");
        Infrastructure.Win32.ApplyDarkFrame(this);

        var pages = _profile.AllPanelPages().ToList();
        for (int t = 0; t < pages.Count; t++)
            for (int i = 0; i < pages[t].Cells.Count; i++)
            {
                var c = pages[t].Cells[i];
                if (c.Stat == null) continue;
                var title = stats.TryGetValue(c.Stat, out var st) ? st.Definition.Title : c.Stat;
                _rows.Add(new Row
                {
                    TabIndex = t, CellIndex = i, Tab = pages[t].Name, Where = $"{c.Row + 1}:{c.Col + 1}", Stat = title,
                    Filter = c.Filter?.ToString() ?? "",
                    Popup = c.Popup != null && _profile.Page(c.Popup) is { } p ? HudProfile.PageKey(p) : None,
                });
            }

        var choices = new List<string> { None };
        choices.AddRange(_profile.Pages.Select(HudProfile.PageKey));
        _grid.Columns.Add(new DataGridTextColumn { Header = "Вкладка панели", Binding = new System.Windows.Data.Binding(nameof(Row.Tab)), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Ячейка", Binding = new System.Windows.Data.Binding(nameof(Row.Where)), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Стат", Binding = new System.Windows.Data.Binding(nameof(Row.Stat)), IsReadOnly = true, Width = 160 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Фильтр", Binding = new System.Windows.Data.Binding(nameof(Row.Filter)), IsReadOnly = true, Width = 220 });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Открывает вкладку попапа", ItemsSource = choices, Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            SelectedItemBinding = new System.Windows.Data.Binding(nameof(Row.Popup)) { Mode = System.Windows.Data.BindingMode.TwoWay },
        });
        _grid.ItemsSource = _rows;

        var save = new Button { Content = "Сохранить", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(6) };
        save.Click += (_, _) => Save();
        var fillEmpty = new Button { Content = "Непривязанным — как у соседа слева", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6) };
        fillEmpty.Click += (_, _) => FillFromNeighbours();
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(save);
        bar.Children.Add(fillEmpty);
        bar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new TextBlock
        {
            Text = "Ячейка без привязанной вкладки за столом не открывает ничего — ни по клику, ни по наведению.",
            Margin = new Thickness(8, 6, 8, 4), Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
        });
        DockPanel.SetDock(root.Children[^1], Dock.Top);
        root.Children.Add(_grid);
        Content = root;
        UpdateStatus();
    }

    private void UpdateStatus() =>
        _status.Text = $"Ячеек со статом: {_rows.Count}, без привязки: {_rows.Count(r => r.Popup == None)}";

    private void FillFromNeighbours()
    {
        string last = None;
        foreach (var r in _rows)
        {
            if (r.Popup != None) last = r.Popup;
            else if (last != None) r.Popup = last;
        }
        _grid.Items.Refresh();
        UpdateStatus();
    }

    private void Save()
    {
        _grid.CommitEdit();
        var pages = _profile.AllPanelPages().ToList();
        foreach (var r in _rows)
        {
            var page = pages[r.TabIndex];
            var c = page.Cells[r.CellIndex];
            page.Cells[r.CellIndex] = new HudCell
            {
                Row = c.Row, Col = c.Col, RowSpan = c.RowSpan, ColSpan = c.ColSpan, Text = c.Text, Stat = c.Stat, Filter = c.Filter,
                Colors = c.Colors, Style = c.Style, Decimals = c.Decimals, MinSample = c.MinSample, ShowSample = c.ShowSample,
                Popup = r.Popup == None ? null : r.Popup,
            };
        }
        // Встроенная раскладка не перезаписывается, а файл с её именем приложение не подхватит (выбрана «встроенная»):
        // сохраняем под своим именем и сразу делаем эту раскладку выбранной.
        string name = _profile.Name;
        if (DefaultProfiles.All.Any(p => p.Name == name))
        {
            var asked = Views.PlayerMarkerMenu.Ask("Привязка попапов",
                "Встроенную раскладку изменить нельзя. Под каким именем сохранить копию с привязками?", "Моя " + name, multiline: false);
            if (string.IsNullOrWhiteSpace(asked)) return;
            name = asked.Trim();
        }
        var tabs = _profile.PanelTabs;
        var profile = new HudProfile
        {
            Name = name, GameKind = _profile.GameKind, Panel = tabs.Count > 0 ? tabs[0] : _profile.Panel, PanelTabs = tabs,
            Pages = _profile.Pages, Version = _profile.Version,
        };
        _store.Save(profile);
        _status.Text = $"Сохранено: {name}. " + _status.Text;
        _saved(name);
        UpdateStatus();
    }
}
