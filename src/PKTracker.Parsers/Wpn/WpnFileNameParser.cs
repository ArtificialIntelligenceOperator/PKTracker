using System.Globalization;
using System.Text.RegularExpressions;
using PKTracker.Core.Model;

namespace PKTracker.Parsers.Wpn;

/// <summary>Метаданные из имени файла HH WPN (§0.3). Тип игры и id стола берём только отсюда (§15.1).</summary>
public sealed record WpnFileInfo(
    DateOnly Date,
    GameKind GameKind,
    string Kind,
    string GameId,
    string TableId,
    string TableOrTournamentName,
    string GameType,
    string Limit,
    string Currency,
    bool OnDemand,
    decimal? MinBuyIn,
    decimal? MaxBuyIn);

public static partial class WpnFileNameParser
{
    [GeneratedRegex(@"^HH(\d{8}) (CASHID|SCHEDULEDID|SITGOID|SPINZID)-G(\d+)T(\d+) TN-(.*?) GAMETYPE-(.*?) LIMIT-(\S+) CUR-(\S+) OND-(\S) BUYIN-(\S+)(?: MIN-(\S+) MAX-(\S+))?\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex FileRx();

    private static readonly (string Token, string Value)[] Escapes =
    {
        ("{FULLSTOP}", "."), ("{SLASH}", "/"), ("{BACKSLASH}", "\\"), ("{COLON}", ":"),
        ("{ASTERISK}", "*"), ("{QUESTION}", "?"), ("{QUOTE}", "\""), ("{LT}", "<"), ("{GT}", ">"), ("{PIPE}", "|"),
    };

    public static string Unescape(string s)
    {
        foreach (var (t, v) in Escapes) s = s.Replace(t, v, StringComparison.Ordinal);
        return s;
    }

    public static bool TryParse(string path, out WpnFileInfo info)
    {
        info = null!;
        var m = FileRx().Match(Path.GetFileName(path));
        if (!m.Success) return false;
        var kind = m.Groups[2].Value;
        info = new WpnFileInfo(
            DateOnly.ParseExact(m.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture),
            kind switch { "CASHID" => GameKind.Cash, "SITGOID" or "SPINZID" => GameKind.Sng, _ => GameKind.Mtt },
            kind,
            "G" + m.Groups[3].Value,
            "T" + m.Groups[4].Value,
            Unescape(m.Groups[5].Value),
            m.Groups[6].Value,
            m.Groups[7].Value,
            m.Groups[8].Value,
            m.Groups[9].Value == "T",
            m.Groups[11].Success ? decimal.Parse(m.Groups[11].Value, CultureInfo.InvariantCulture) : null,
            m.Groups[12].Success ? decimal.Parse(m.Groups[12].Value, CultureInfo.InvariantCulture) : null);
        return true;
    }

    public static HandSourceInfo ToSource(string path, string? heroHint = null)
    {
        if (!TryParse(path, out var fi)) return new HandSourceInfo(path, HeroHint: heroHint);
        return new HandSourceInfo(path, fi.GameKind, fi.GameId, fi.TableId, fi.TableOrTournamentName, heroHint);
    }
}
