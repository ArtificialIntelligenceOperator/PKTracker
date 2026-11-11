using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PKTracker.Data.Notes;

namespace PKTracker.App.Views;

/// <summary>
/// Каталог цветовых меток: тип оппонента, цвет, «регуляр». Файл — %APPDATA%\PKTracker\markers.json,
/// у пользователя, а не в репозитории. Готовый набор можно загрузить из файла.
/// </summary>
public sealed class MarkersWindow : Window
{
    private readonly AppServices _svc;
    private readonly ObservableCollection<MarkerDef> _items;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false };

    public MarkersWindow(AppServices svc)
    {
        _svc = svc;
        // Копии: таблица правит объекты на месте, и без копий несохранённые имена и цвета сразу попадали бы на панели.
        _items = new ObservableCollection<MarkerDef>(svc.Markers.Markers.Select(m =>
            new MarkerDef { Id = m.Id, Title = m.Title, Color = m.Color, IsReg = m.IsReg, Hint = m.Hint }));
        Title = "Цветовые метки";
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg0");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
        Infrastructure.Win32.ApplyDarkFrame(this);

        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "", Width = 24,
            CellTemplate = new DataTemplate
            {
                VisualTree = Swatch(),
            },
        });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Название", Binding = new System.Windows.Data.Binding(nameof(MarkerDef.Title)), Width = 160 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Цвет #RRGGBB", Binding = new System.Windows.Data.Binding(nameof(MarkerDef.Color)), Width = 100 });
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Рег", Binding = new System.Windows.Data.Binding(nameof(MarkerDef.IsReg)) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Подсказка: как узнать", Binding = new System.Windows.Data.Binding(nameof(MarkerDef.Hint)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.ItemsSource = _items;

        var add = Btn("+ метка", () => _items.Add(new MarkerDef { Id = Guid.NewGuid().ToString("N")[..8], Title = "Новая", Color = "#9E9E9E" }));
        var del = Btn("− удалить", () => { if (_grid.SelectedItem is MarkerDef m) _items.Remove(m); });
        var load = Btn("Загрузить из файла…", LoadFromFile);
        var save = Btn("Сохранить", Save);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        foreach (var b in new[] { add, del, load, save }) bar.Children.Add(b);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        var hint = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4), Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
            Text = "Метка ставится вручную: правый клик по панели HUD (без перетаскивания) или во вкладке «Игроки». " +
                   "Автоматических меток по статам нет намеренно — на iPoker они прямо запрещены правилами рума.",
        };
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(hint);
        root.Children.Add(_grid);
        Content = root;
    }

    private static FrameworkElementFactory Swatch()
    {
        var f = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        f.SetValue(WidthProperty, 12.0);
        f.SetValue(HeightProperty, 12.0);
        f.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding(nameof(MarkerDef.Color)));
        return f;
    }

    private static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4) };
        b.Click += (_, _) => a();
        return b;
    }

    private void LoadFromFile()
    {
        var dlg = new OpenFileDialog { Filter = "Метки (*.json)|*.json", Title = "Файл с метками" };
        if (dlg.ShowDialog(this) != true) return;
        var other = MarkerCatalog.Load(dlg.FileName);
        int added = 0;
        foreach (var m in other.Markers)
            if (_items.All(x => !string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase))) { _items.Add(m); added++; }
        MessageBox.Show(this, $"Добавлено меток: {added}. Не забудьте сохранить.", Title);
    }

    private void Save()
    {
        _grid.CommitEdit();
        var catalog = new MarkerCatalog { Markers = _items.ToList() };
        try { catalog.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Не удалось сохранить метки: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _svc.Markers = catalog;
        _svc.Hud.RefreshAll();
        Close();
    }
}
