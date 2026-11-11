using System.Globalization;
using System.Text.RegularExpressions;
using PKTracker.Core.Model;

namespace PKTracker.Parsers.Common;

/// <summary>Ошибка формата: раздача отклоняется целиком и попадает в import_error.</summary>
public class HandFormatError(string message) : FormatException(message);

/// <summary>
/// Общие для текстовых форматов приёмы: суммы, карты, поиск ника в строке.
/// Ловушки одни и те же у всех румов — поэтому и решения в одном месте.
/// </summary>
public static partial class HhText
{
    /// <summary>
    /// Сумма → long в сотых. Только decimal и InvariantCulture: double и текущая культура уже портили деньги.
    /// Понимает «$1,234.50», «€0.10», «1 234,50 €» не понимает намеренно — такие форматы разбирает свой парсер.
    /// </summary>
    public static long Amount(string s, int scale = 2)
    {
        var span = s.AsSpan().Trim();
        while (span.Length > 0 && (span[0] == '$' || span[0] == '€' || span[0] == '£' || span[0] == '¥')) span = span[1..];
        while (span.Length > 0 && (span[^1] == '€' || span[^1] == '$')) span = span[..^1];
        var d = decimal.Parse(span, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        decimal mul = scale switch { 0 => 1m, 1 => 10m, 2 => 100m, 3 => 1000m, _ => (decimal)Math.Pow(10, scale) };
        var scaled = d * mul;
        if (scaled != decimal.Truncate(scaled)) throw new HandFormatError($"Amount has more than {scale} decimals: {s}");
        return (long)scaled;
    }

    /// <summary>Карты «Ah Kd», «10h» (iPoker/888 пишут десятку двумя символами), «-» — карта не показана.</summary>
    public static Card?[] Cards(string s)
    {
        var parts = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var res = new Card?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p is "-" or "X" or "x" or "??" or "[-]") continue;
            if (p.StartsWith("10", StringComparison.Ordinal)) p = "T" + p[2..];
            if (!Card.TryParse(p, out var c)) throw new HandFormatError($"Bad card '{parts[i]}'");
            res[i] = c;
        }
        return res;
    }

    public static void SetHole(ParsedSeat seat, string cards)
    {
        var cs = Cards(cards);
        if (cs.Length != 2) throw new HandFormatError($"Expected 2 hole cards: [{cards}] (Omaha is not supported)");
        for (int i = 0; i < 2; i++) if (cs[i].HasValue) seat.HoleCards[i] = cs[i];
    }

    public static List<Card> BoardCards(string cards)
    {
        var list = new List<Card>();
        foreach (var c in Cards(cards)) list.Add(c ?? throw new HandFormatError("Unknown board card"));
        return list;
    }

    /// <summary>
    /// Ник в начале строки — самым длинным совпадением из списка мест, и только если за ним идёт разделитель.
    /// Split(' ') нельзя: ники бывают с пробелами и бывают префиксами других ников («Ivan» и «Ivan 2»).
    /// </summary>
    public static (ParsedSeat? Seat, string Tail) ResolvePlayer(ParsedHand h, string[] namesByLengthDesc, string line, params string[] separators)
    {
        foreach (var n in namesByLengthDesc)
        {
            if (line.Length <= n.Length || !line.StartsWith(n, StringComparison.Ordinal)) continue;
            foreach (var sep in separators)
                if (string.CompareOrdinal(line, n.Length, sep, 0, sep.Length) == 0)
                    return (h.SeatByName(n), line[(n.Length + sep.Length)..]);
        }
        return (null, line);
    }

    public static string[] NamesByLength(ParsedHand h) =>
        h.Seats.Select(s => s.Name).Distinct().OrderByDescending(n => n.Length).ToArray();

    /// <summary>Флаги, которые одинаково выводятся для всех форматов после разбора.</summary>
    public static void FinishFlags(ParsedHand h)
    {
        var btn = h.SeatByNumber(h.ButtonSeat);
        if (btn == null || !btn.Dealt) h.Flags |= HandFlags.DeadButton;
        if (!h.Actions.Any(a => a.Type == ActionType.PostSb)) h.Flags |= HandFlags.NoSmallBlind;
        if (h.Seats.Count(s => s.Dealt) == 2) h.Flags |= HandFlags.HeadsUp;
    }

    /// <summary>
    /// «Total pot» в кеше обычно ВАЛОВЫЙ (включает рейк и сборы), а инвариант денег требует TotalPot == Σ выигрыша
    /// (Σ вложено == TotalPot + рейк + сборы). Приводим к чистому банку и заодно сверяем напечатанный итог с суммой
    /// выигрышей: расхождение — значит разобрали не всё (потерянный сайд-пот, «collected» без SUMMARY) и раздачу нельзя принимать.
    /// </summary>
    public static void ReconcilePot(ParsedHand h)
    {
        long won = h.Seats.Sum(s => s.Won);
        long fees = h.Rake + h.TotalExtraFees;
        if (h.TotalPot == won + fees) h.TotalPot = won;
        else if (h.TotalPot != won) throw new HandFormatError($"Total pot {h.TotalPot} does not match winnings {won} (+ fees {fees})");
    }

    public static void ApplyHeroHint(ParsedHand h, HandSourceInfo source)
    {
        if (h.HeroName == null && source.HeroHint != null && h.SeatByName(source.HeroHint) is { } hs)
        {
            hs.IsHero = true;
            h.HeroName = hs.Name;
        }
    }

    /// <summary>Делит текст на раздачи по маркеру начала (offset, length).</summary>
    public static List<(int Start, int Length)> Split(string text, Regex handStart)
    {
        var res = new List<(int, int)>();
        var ms = handStart.Matches(text);
        for (int i = 0; i < ms.Count; i++)
        {
            int start = ms[i].Index;
            int end = i + 1 < ms.Count ? ms[i + 1].Index : text.Length;
            res.Add((start, end - start));
        }
        return res;
    }

    /// <summary>Стандартный цикл: разбить, разобрать каждую раздачу, ошибку формата превратить в отклонение.</summary>
    public static IEnumerable<HandParseResult> ParseAll(string text, HandSourceInfo source, long baseOffset, Regex handStart,
        Func<string, ParsedHand> parseHand)
    {
        foreach (var (start, len) in Split(text, handStart))
        {
            var raw = text.Substring(start, len).TrimEnd('\r', '\n', ' ', '\t');
            if (raw.Length == 0) continue;
            HandParseResult r;
            try
            {
                var h = parseHand(raw);
                h.FileOffset = baseOffset + start;
                r = new HandParseResult { Hand = h, RawText = raw, Offset = baseOffset + start, SourceFile = source.FilePath };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
            {
                r = new HandParseResult { Error = ex.Message, RawText = raw, Offset = baseOffset + start, SourceFile = source.FilePath };
            }
            yield return r;
        }
    }

    private static readonly TimeZoneInfo? Eastern = FindZone("Eastern Standard Time", "America/New_York");
    private static readonly TimeZoneInfo? Central = FindZone("Romance Standard Time", "Europe/Paris");

    private static TimeZoneInfo? FindZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return null;
    }

    /// <summary>
    /// Время «yyyy/MM/dd H:mm:ss» в поясе рума → UTC. PokerStars пишет ET (с переходом на летнее время),
    /// Winamax/iPoker — UTC или CET. Без перевода сессии и графики по часам съезжают на 4–5 часов.
    /// </summary>
    public static DateTime ToUtc(string s, string zone)
    {
        var local = DateTime.ParseExact(s.Trim(), new[] { "yyyy/MM/dd H:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm:ss" },
            CultureInfo.InvariantCulture, DateTimeStyles.None);
        TimeZoneInfo? tz = zone switch
        {
            "ET" or "EST" or "EDT" => Eastern,
            "CET" or "CEST" or "Paris" => Central,
            "UTC" or "GMT" or "" => null,
            // Незнакомая метка пояса (EET, MSK…): принять её за UTC — значит тихо сдвинуть время раздачи на часы.
            _ => throw new HandFormatError($"Unknown time zone '{zone}'"),
        };
        if (tz == null) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    }

    /// <summary>
    /// Стабильный 63-битный id из строки. Winamax пишет HandId как «1000000000000000001-12-1700000000»
    /// (стол-номер раздачи-время): числом это не выразить, а ExternalHandId — long с уникальным ключом (рум, id).
    /// string.GetHashCode не годится — он случаен между запусками; здесь FNV-1a, одинаковый всегда.
    /// </summary>
    public static long StableId(string s)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) { hash ^= b; hash *= 1099511628211UL; }
        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);
    }

    /// <summary>
    /// Номер турнира: число как есть, иначе — стабильный id из строки. Клоны формата PokerStars пишут номер
    /// вида «916-1a5b8d2»; раньше такая шапка отклоняла раздачу целиком. Старший значащий бит выставлен,
    /// чтобы id из строки не совпал с настоящим числовым номером другого турнира.
    /// </summary>
    public static long TournamentId(string s) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : StableId(s) | (1L << 62);

    /// <summary>
    /// То же для рума, который пишет время в поясе компьютера игрока (888, iPoker): зону знает только пользователь,
    /// в тексте её нет. Не задана — UTC.
    /// </summary>
    public static DateTime ToUtc(string s, TimeZoneInfo? tz)
    {
        var local = DateTime.ParseExact(s.Trim(), new[] { "yyyy/MM/dd H:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm:ss" },
            CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (tz == null) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    }

    public static int RomanOrInt(string s)
    {
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return n;
        int total = 0, prev = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            int v = s[i] switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => throw new HandFormatError($"Bad level '{s}'") };
            total += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }
        return total;
    }
}
