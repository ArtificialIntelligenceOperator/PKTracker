using System.Text.RegularExpressions;
using PKTracker.Core.Model;

namespace PKTracker.Data;

/// <summary>Чистая геометрия и сопоставление HUD — без окон, чтобы покрывать тестами (§5.1, §5.2).</summary>
public static class HudGeometry
{
    /// <summary>Клиент всегда сажает Hero снизу: визуальный индекс = (seat − mySeat) mod max (§0.4). Без Hero — seat−1.</summary>
    public static int VisualIndex(int seat, int? heroSeat, int maxSeats)
    {
        int max = Math.Max(maxSeats, 2);
        int hero = heroSeat ?? 1;
        return ((seat - hero) % max + max) % max;
    }

    /// <summary>Якорь центра панели в долях окна стола: эллипс, индекс 0 — снизу по центру, далее по часовой стрелке.</summary>
    public static (double X, double Y) Anchor(int visualIndex, int maxSeats)
    {
        int max = Math.Max(maxSeats, 2);
        double angle = Math.PI / 2 + visualIndex * 2 * Math.PI / max;
        return (0.5 + 0.40 * Math.Cos(angle), 0.47 + 0.37 * Math.Sin(angle));
    }

    /// <summary>Окно стола ↔ стол из HH: кеш — имя стола целым словом; турнир — номер стола и (id или название турнира).</summary>
    public static bool TitleMatches(string title, ActiveTable t)
    {
        if (t.Kind == GameKind.Cash)
            return t.TableName.Length > 0 && Regex.IsMatch(title, $@"(^|\W){Regex.Escape(t.TableName)}(\W|$)", RegexOptions.IgnoreCase);

        var tableNo = t.ExternalTableId.TrimStart('T');
        // Часть румов записывает стол в истории как «<id турнира> <номер стола>» (PokerStars: Table '40000001 1'),
        // а в заголовке окна эти числа стоят порознь: «Tournament 40000001 Table 1». Номер стола — то, что после id;
        // без этого такое окно не совпадало ни с одним столом, и HUD над ним не появлялся.
        if (t.TournamentId is { } own && tableNo.StartsWith(own + " ", StringComparison.Ordinal))
            tableNo = tableNo[(own.ToString().Length + 1)..].Trim();
        bool tableOk = Regex.IsMatch(title, $@"(Table|Стол|#)\s*'?{Regex.Escape(tableNo)}(\D|$)", RegexOptions.IgnoreCase);
        bool idOk = t.TournamentId is { } tid && title.Contains(tid.ToString());
        int hash = t.TableName.LastIndexOf(" #", StringComparison.Ordinal);
        var baseName = hash > 0 ? t.TableName[..hash] : t.TableName;
        bool nameOk = baseName.Length > 3 && title.Contains(baseName, StringComparison.OrdinalIgnoreCase);
        return tableOk && (idOk || nameOk);
    }
}
