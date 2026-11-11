using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PKTracker.Core.Model;
using PKTracker.Engine.Hud;

namespace PKTracker.App.Hud;

/// <summary>Окно предпросмотра раскладки по реальному игроку: панель (все вкладки) и попап профиля.</summary>
public static class LayoutPreview
{
    /// <param name="statCode">Сразу открыть разбор этого стата (матрица рук, сила руки) — как при наведении на него.</param>
    public static async Task ShowAsync(AppServices svc, string nickname, string? page, string? statCode = null)
    {
        if (svc.Host is not { } host) return;
        var p = await Task.Run(() => host.Hud.FindPlayerAsync(nickname));
        if (p == null) { svc.Log("Preview", $"Игрок {nickname} не найден"); return; }
        var kind = GameKind.Mtt;
        var profile = svc.Hud.Profile(kind);
        var agg = await Task.Run(() => host.Hud.GetAggAsync(p.PlayerId, kind));
        var pages = profile.AllPanelPages().ToList();
        var values = pages.Select(pg => (CellValue[]?)agg.EvaluatePage(pg)).ToList();
        var note = await Task.Run(() => host.Notes.GetAsync(p.PlayerId));

        var panel = new HudPanelWindow(svc.Settings.PanelOpacity) { Seat = 1 };
        panel.Stats = host.Hud.Stats;
        panel.SetPlayer(p.Nickname, p.PlayerId);
        panel.SetPages(pages, values, agg.Evaluate("hands", null).Cases);
        var m = note?.Marker is { } id ? svc.Markers.Find(id) : null;
        panel.SetMarker(m?.Color, m?.Title, note?.Caption, m?.Hint);
        panel.Left = 40;
        panel.Top = 40;
        panel.Show();

        // Свой контроллер попапов: наведение на стат внутри попапа открывает его разбор (матрица рук, сила руки) —
        // так же, как за столом. Это предпросмотр, а не оверлей, поэтому ограничения рума на него не действуют.
        var popups = new PopupController(() => svc.Settings, () => svc.Host, m2 => svc.Log("Popup", m2), Application.Current.Dispatcher, overlay: false);
        var popup = new ProfilePopupWindow(host, Application.Current.Dispatcher, p.Nickname, p.PlayerId, kind, profile, page, popups)
        {
            Left = 40, Top = 160, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        popup.Show();
        if (statCode != null)
            popups.Click(new PopupTarget { PlayerId = p.PlayerId, PlayerName = p.Nickname, Kind = kind, StatCode = statCode },
                PopupAnchor.OfWindow(new System.Windows.Interop.WindowInteropHelper(popup).Handle));
    }
}
