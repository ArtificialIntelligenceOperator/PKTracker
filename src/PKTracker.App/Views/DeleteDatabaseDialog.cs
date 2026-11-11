using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PKTracker.Data;

namespace PKTracker.App.Views;

/// <summary>
/// Подтверждение удаления базы. Удаление необратимо, поэтому одной кнопки «Да» мало:
/// нужно набрать имя базы — так её не снести промахом по соседней строке списка.
/// </summary>
public sealed class DeleteDatabaseDialog : Window
{
    private readonly CheckBox _backup;

    /// <summary>Перед удалением сделать копию pg_dump в папку бэкапов.</summary>
    public bool BackupFirst => _backup.IsChecked == true;

    public DeleteDatabaseDialog(DatabaseInfo db, string switchTo, string backupFolder)
    {
        Title = "Удаление базы";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Bg0");
        Foreground = (Brush)Application.Current.FindResource("Text");
        Infrastructure.Win32.ApplyDarkFrame(this);

        var box = new TextBox { Margin = new Thickness(0, 6, 0, 10) };
        var delete = new Button { Content = "Удалить базу", IsEnabled = false, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, IsDefault = true, Padding = new Thickness(14, 3, 14, 3) };
        _backup = new CheckBox
        {
            Content = "Сначала сохранить резервную копию", IsChecked = db.Hands is > 0, Margin = new Thickness(0, 0, 0, 2),
            ToolTip = "Файл pg_dump в папке " + backupFolder,
        };

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"База «{db.Name}» будет удалена целиком: " +
                   (db.Hands is { } h ? $"{h:N0} раздач, " : "") + $"{db.SizeText} на диске — раздачи, статистика, заметки и метки игроков.\n\n" +
                   "Отменить удаление нельзя. Файлы истории на диске и раскладки HUD не затрагиваются.\n" +
                   $"После удаления трекер переключится на базу «{switchTo}».",
        });
        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(_backup);
        panel.Children.Add(new TextBlock
        {
            Text = "Папка копий: " + backupFolder, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 10),
            Foreground = (Brush)Application.Current.FindResource("TextDim"), FontSize = 11,
        });
        panel.Children.Add(new TextBlock { Text = $"Чтобы подтвердить, введите имя базы: {db.Name}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(delete);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        box.TextChanged += (_, _) => delete.IsEnabled = string.Equals(box.Text.Trim(), db.Name, StringComparison.Ordinal);
        delete.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => box.Focus();
    }
}
