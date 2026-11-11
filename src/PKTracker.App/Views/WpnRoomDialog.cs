using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PKTracker.Data.Import;
using PKTracker.Parsers;

namespace PKTracker.App.Views;

/// <summary>
/// Раздачи сети WPN: её румы (PokerKing, ACR, Black Chip, True Poker) пишут историю одинаково,
/// и по файлу не понять, на каком из них играл владелец. Поэтому вместе с вопросом «ваш ли это аккаунт»
/// спрашиваем рум — один раз, дальше он берётся из списка «Мои ники».
/// </summary>
public sealed class WpnRoomDialog : Window
{
    private readonly ComboBox _room = new() { Margin = new Thickness(0, 6, 0, 12), Width = 240, HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>Код выбранного рума.</summary>
    public string RoomCode => (string)((ComboBoxItem)_room.SelectedItem).Tag;

    public WpnRoomDialog(UnknownHero wpn, string? preferredRoom)
    {
        Title = "Импорт: раздачи сети WPN";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Bg0");
        Foreground = (Brush)Application.Current.FindResource("Text");
        Infrastructure.Win32.ApplyDarkFrame(this);

        foreach (var room in ParserRegistry.Rooms.Where(r => r.Network == "WPN"))
            _room.Items.Add(new ComboBoxItem { Content = room.Title, Tag = room.Code });
        _room.SelectedItem = _room.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == preferredRoom) ?? _room.Items[0];

        var mine = new Button { Content = "Это мой аккаунт", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
        var notMine = new Button { Content = "Не мой", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(mine);
        buttons.Children.Add(notMine);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"В выбранных файлах {wpn.Files:N0} файл(ов) сети WPN, записанных клиентом игрока «{wpn.Nickname}».\n\n" +
                   "Румы этой сети пишут историю одинаково, поэтому по файлу не определить, какой из них ваш. " +
                   "Если это ваш аккаунт, выберите рум — раздачи загрузятся под ним:",
        });
        panel.Children.Add(_room);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            Text = "Чужие раздачи трекер не принимает (правила румов). «Не мой» — оставить эти файлы незагруженными.",
        });
        panel.Children.Add(buttons);
        Content = panel;
        mine.Click += (_, _) => DialogResult = true;
    }
}
