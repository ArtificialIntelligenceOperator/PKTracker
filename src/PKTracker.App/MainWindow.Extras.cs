using System.Windows;
using Microsoft.Win32;
using PKTracker.App.Hud;
using PKTracker.App.Views;
using PKTracker.Data;

namespace PKTracker.App;

/// <summary>Кнопки вкладки HUD: пакеты раскладок, привязка попапов, цветовые метки.</summary>
public partial class MainWindow
{
    private void PopupBinding_Click(object sender, RoutedEventArgs e)
    {
        if (_svc.Host is not { } host)
        {
            MessageBox.Show("Нет подключения к базе: список статов недоступен.", "Привязка попапов");
            return;
        }
        var profile = _svc.Hud.Profile(ProfileGameKind);
        if (profile.Pages.Count == 0)
        {
            MessageBox.Show("У этой раскладки нет вкладок попапа — привязывать нечего.", "Привязка попапов");
            return;
        }
        var kind = ProfileGameKind;
        new PopupBindingWindow(profile, host.Hud.Stats, name =>
        {
            // Копия встроенной раскладки сохранена под новым именем — делаем её выбранной, иначе привязки не применятся.
            if (name != profile.Name)
            {
                if (kind == PKTracker.Core.Model.GameKind.Cash) _svc.Settings.CashProfile = name;
                else _svc.Settings.MttProfile = name;
                _svc.Settings.Save();
            }
            ReloadHudProfileList();
            _svc.Hud.ReloadProfile();
        }) { Owner = this }.ShowDialog();
    }

    private async void HudPackageImport_Click(object sender, RoutedEventArgs e)
    {
        if (_svc.Host is not { } host)
        {
            MessageBox.Show("Нет подключения к базе.", "Импорт пакета HUD");
            return;
        }
        var dlg = new OpenFileDialog { Filter = "Пакет HUD (*.hudpack.json)|*.hudpack.json|JSON (*.json)|*.json", Title = "Пакет HUD" };
        if (dlg.ShowDialog(this) != true) return;
        ProfileStatus.Text = "Импорт пакета…";
        try
        {
            var progress = new Progress<RebuildProgress>(p => ProfileStatus.Text = $"{p.Stage}: {p.Done}/{p.Total}");
            var res = await Task.Run(() => new HudPackageService(host).ImportAsync(dlg.FileName, progress));
            ReloadHudProfileList();
            _svc.Hud.ReloadProfile();
            ProfileStatus.Text = $"Раскладка «{res.ProfileName}» установлена: статов {res.Stats}, новых/изменённых {res.ChangedStats.Count}. " +
                                 "Выберите её в поле «Раскладка».";
        }
        catch (Exception ex)
        {
            ProfileStatus.Text = "";
            MessageBox.Show(ex.Message, "Импорт пакета HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Открыть вкладку по её заголовку (без учёта регистра); неизвестное имя — ничего не делает.
    /// «Аналитика/Графики» — сразу внутренняя вкладка аналитики.
    /// </summary>
    public void SelectTab(string header)
    {
        int slash = header.IndexOf('/');
        if (slash > 0)
        {
            SelectTab(header[..slash]);
            if (Tabs.SelectedItem == AnalyticsTab) Analytics.SelectTab(header[(slash + 1)..]);
            return;
        }
        foreach (var item in Tabs.Items.OfType<System.Windows.Controls.TabItem>())
            if (string.Equals(item.Header as string, header, StringComparison.OrdinalIgnoreCase)) { Tabs.SelectedItem = item; return; }
    }

    private void Markers_Click(object sender, RoutedEventArgs e) => new MarkersWindow(_svc) { Owner = this }.ShowDialog();

    private void SessionWidget_Click(object sender, RoutedEventArgs e)
    {
        _svc.Settings.SessionWidget = SessionWidgetBox.IsChecked == true;
        SaveSettingsQuietly();
        _svc.ApplySessionWidget();
    }

    private void AutoProfile_Click(object sender, RoutedEventArgs e)
    {
        _svc.Settings.AutoProfileBySeats = AutoProfileBox.IsChecked == true;
        SaveSettingsQuietly();
        _svc.Hud.ReloadProfile();
    }

    private void SaveSettingsQuietly()
    {
        try { _svc.Settings.Save(); }
        catch (System.IO.IOException ex) { _svc.Log("App", "Настройки не сохранены: " + ex.Message); }
    }

    /// <summary>Что именно означает выбранный режим совместной игры с GG — словами, под самим списком.</summary>
    private void SetGgMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SetGgModeHint == null) return;   // список создаётся раньше подписи под ним
        SetGgModeHint.Text = SetGgMode.SelectedIndex switch
        {
            1 => "Соблюдение правила GG по существу: пока её клиент открыт, закрыты панели HUD и попапы, остановлено слежение за окнами столов " +
                 "и .data-канал, убран виджет сессии — трекер не обращается ни к столам, ни к игровым данным. Остаётся только чтение файлов " +
                 "истории других румов с диска, чтобы статистика не отставала. Клиент GG закрыли — всё включается само. Процесс трекера при " +
                 "этом остаётся запущенным.",
            2 => "Соблюдение правила GG буквально: как только её клиент запущен, PKTracker и его MCP-сервер закрываются — рядом с клиентом GG " +
                 "не остаётся ни одного процесса трекера. Раздачи других румов, сыгранные за это время, подхватятся при следующем запуске.",
            _ => "HUD, авто-импорт и .data-канал для других румов работают и рядом с клиентом GG; с самой сетью GG трекер не взаимодействует. " +
                 "С 01.12.2026 GG запрещает любой сторонний покерный софт рядом со своим клиентом и проверяет запущенные программы — " +
                 "в этом режиме риск на вас.",
        };
    }

    private void SetRoom_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SetRoom.SelectedItem is System.Windows.Controls.ComboBoxItem { Content: string code })
            SetRoomRules.Text = RoomHudPolicy.For(code).Summary;
    }
}
