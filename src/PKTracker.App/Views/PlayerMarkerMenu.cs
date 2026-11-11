using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PKTracker.Data;
using PKTracker.Data.Notes;

namespace PKTracker.App.Views;

/// <summary>
/// Меню «метка / подпись / заметка» на игрока — одно и то же над столом, в реплеере и во вкладке «Игроки».
/// Метка ставится только руками: автоматические метки по статам запрещены правилами части румов.
/// </summary>
public static class PlayerMarkerMenu
{
    public static async Task<ContextMenu> BuildAsync(AppServices svc, TrackerHost host, long playerId, string playerName, Action? changed = null)
    {
        // Обработчики пунктов — async void: без перехвата сбой базы (сеть, таймаут) дошёл бы до общего
        // обработчика ошибок, а тот закрывает все панели HUD и показывает окно поверх стола.
        async void Safe(Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { svc.Log("Markers", $"Метка игрока {playerName}: {ex.Message}"); }
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = playerName, IsEnabled = false, FontWeight = FontWeights.Bold });
        // Запросы к базе — в фоне: UI-поток в базу не ходит.
        var current = await Task.Run(() => host.Notes.GetAsync(playerId));

        if (svc.Markers.Markers.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Метки не настроены: Настройки → Цветовые метки", IsEnabled = false });
        foreach (var m in svc.Markers.Markers)
        {
            var item = new MenuItem
            {
                Header = m.Title, IsCheckable = true, IsChecked = current?.Marker == m.Id, ToolTip = m.Hint,
                Icon = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Brush(m.Color) },
            };
            item.Click += (_, _) => Safe(async () =>
            {
                var cur = await Task.Run(() => host.Notes.GetAsync(playerId));
                await Task.Run(() => host.Notes.SetAsync(playerId, cur?.Marker == m.Id ? null : m.Id, cur?.Caption));
                changed?.Invoke();
            });
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var caption = new MenuItem { Header = "Подпись…" };
        caption.Click += (_, _) => Safe(async () =>
        {
            var cur = await Task.Run(() => host.Notes.GetAsync(playerId));
            var text = Ask("Подпись к метке", $"Короткая подпись к игроку {playerName} (видна на панели HUD):", cur?.Caption, multiline: false);
            if (text == null) return;
            await Task.Run(() => host.Notes.SetAsync(playerId, cur?.Marker, text));
            changed?.Invoke();
        });
        menu.Items.Add(caption);
        var note = new MenuItem { Header = "Добавить заметку…" };
        note.Click += (_, _) => Safe(async () =>
        {
            var cur = await Task.Run(() => host.Notes.GetAsync(playerId));
            var text = Ask("Заметка", $"Новая заметка на игрока {playerName} (последняя: {cur?.Note ?? "нет"}):", "", multiline: true);
            if (string.IsNullOrWhiteSpace(text)) return;
            await Task.Run(() => host.Notes.AddNoteAsync(playerId, text));
            changed?.Invoke();
        });
        menu.Items.Add(note);
        if (current != null)
        {
            var clear = new MenuItem { Header = "Снять метку и подпись" };
            clear.Click += (_, _) => Safe(async () =>
            {
                var cur = await Task.Run(() => host.Notes.GetAsync(playerId));
                await Task.Run(() => host.Notes.SetAsync(playerId, null, null));
                changed?.Invoke();
            });
            menu.Items.Add(clear);
        }
        return menu;
    }

    public static Brush Brush(string? hex)
    {
        try { return hex != null ? (Brush)new BrushConverter().ConvertFromString(hex)! : Brushes.Gray; }
        catch (FormatException) { return Brushes.Gray; }
    }

    /// <summary>Простое окно ввода текста. null — пользователь отменил.</summary>
    public static string? Ask(string title, string prompt, string? initial, bool multiline)
    {
        var box = new TextBox
        {
            Text = initial ?? "", Margin = new Thickness(0, 6, 0, 8), AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
            Height = multiline ? 120 : double.NaN, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var ok = new Button { Content = "Сохранить", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Content = panel, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("Bg0"), Foreground = (Brush)Application.Current.FindResource("Text"),
        };
        Infrastructure.Win32.ApplyDarkFrame(w);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
