using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PKTracker.Tournaments;

public sealed record TournamentFinish(string PlayerName, int? FinishPosition, long Prize, long TicketValue, long BountyWon, int EntryNo);

/// <summary>Итоги турнира из WPN .ots (§0.5). Суммы — в сотых (как везде).</summary>
public sealed class TournamentSummary
{
    public long TournamentId { get; init; }
    public string? Name { get; init; }
    public string? NetworkName { get; init; }
    public string? SiteName { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public string? Currency { get; init; }
    public long? PrizePool { get; init; }
    public int? PlayerCount { get; init; }
    /// <summary>Из имени файла ("... $30.00 + $3.00.ots") — в JSON бай-ина нет.</summary>
    public long? BuyIn { get; init; }
    public long? Fee { get; init; }
    public bool IsKnockout { get; init; }
    public bool IsMystery { get; init; }
    public List<TournamentFinish> Finishes { get; } = new();
}

public static partial class TournamentSummaryParser
{
    [GeneratedRegex(@"\$?(\d[\d,]*(?:\.\d+)?) \+ \$?(\d[\d,]*(?:\.\d+)?)(?: \+ \$?(\d[\d,]*(?:\.\d+)?))?\.ots$", RegexOptions.IgnoreCase)]
    private static partial Regex BuyInRx();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex DigitsRx();

    public static TournamentSummary Parse(string json, string? fileName = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long id = 0;
        if (Str(root, "tournament_number") is { } tn && DigitsRx().Match(tn) is { Success: true } m)
            id = long.Parse(m.Value, CultureInfo.InvariantCulture);
        if (id == 0) throw new FormatException("tournament_number missing");

        long? buyIn = null, fee = null;
        if (fileName != null && BuyInRx().Match(Path.GetFileName(fileName)) is { Success: true } bm)
        {
            buyIn = Money(bm.Groups[1].Value);
            // "$X + $Y" — бай-ин + фи; "$X + $B + $Y" (PKO) — бай-ин + баунти + фи
            fee = Money(bm.Groups[3].Success ? bm.Groups[3].Value : bm.Groups[2].Value);
            if (bm.Groups[3].Success) buyIn += Money(bm.Groups[2].Value);
        }

        var name = Str(root, "tournament_name") ?? Str(root, "name");
        var s = new TournamentSummary
        {
            TournamentId = id,
            Name = name,
            NetworkName = Str(root, "network_name"),
            SiteName = Str(root, "site_name"),
            StartedUtc = Date(root, "start_date_utc"),
            FinishedUtc = Date(root, "end_date_utc"),
            Currency = Str(root, "currency"),
            PrizePool = Num(root, "prize_pool"),
            PlayerCount = (int?)NumRaw(root, "player_count"),
            BuyIn = buyIn,
            Fee = fee,
            IsKnockout = (name ?? fileName ?? "").Contains("PKO", StringComparison.OrdinalIgnoreCase)
                         || (name ?? fileName ?? "").Contains("Bounty", StringComparison.OrdinalIgnoreCase),
            IsMystery = (name ?? fileName ?? "").Contains("Mystery", StringComparison.OrdinalIgnoreCase),
        };

        if (root.TryGetProperty("tournament_finishes_and_winnings", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            // Ре-энтри: один ник встречается несколько раз → номер входа по порядку мест (§7.1)
            var entries = new Dictionary<string, int>(StringComparer.Ordinal);
            var rows = arr.EnumerateArray()
                .Select(e => (Name: Str(e, "player_name") ?? "", Pos: (int?)NumRaw(e, "finish_position"), E: e))
                .OrderByDescending(r => r.Pos ?? int.MaxValue)
                .ToList();
            foreach (var (pname, pos, e) in rows)
            {
                if (pname.Length == 0) continue;
                int entry = entries[pname] = entries.GetValueOrDefault(pname) + 1;
                s.Finishes.Add(new TournamentFinish(pname, pos, Num(e, "prize") ?? 0, Num(e, "ticket_value") ?? 0,
                    Num(e, "bounty_prize") ?? Num(e, "bounty") ?? 0, entry));
            }
        }
        return s;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static decimal? NumRaw(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString()?.TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static long? Num(JsonElement e, string name) => NumRaw(e, name) is { } d ? (long)(d * 100m) : null;

    private static long Money(string s) => (long)(decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture) * 100m);

    private static DateTime? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s == null) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;
    }
}
