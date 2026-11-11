using System.Globalization;
using System.Text.RegularExpressions;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Parsers.Common;

namespace PKTracker.Parsers.GgPoker;

/// <summary>
/// Парсер hand history GGPoker / PokerOK («Poker Hand #TM…: Tournament #…», «Poker Hand #RC…: Hold'em …»).
/// Формат — потомок PokerStars, но с отличиями-ловушками: (1) суммы с разделителем тысяч «18,823»;
/// (2) шапка уровня «Level9(100/200(25))» — анте в скобках, бывает без него; (3) «Dealt to ник» без карт
/// пишется про каждого — карты только у Hero (имя «Hero»), остальные — обезличенные хэши; (4) секция
/// «*** SHOWDOWN ***» одним словом и присутствует в каждой раздаче; (5) в SUMMARY победитель — «won (N)»
/// или «collected (N)», а скрытая карта при частичном показе — «showed [7h]» одной картой;
/// (6) «Total pot … | Rake … | Jackpot … | Bingo … | Fortune … | Tax …» — сборы кроме рейка идут в ExtraFees.
/// Строгий: неизвестная строка отклоняет раздачу.
/// </summary>
public sealed partial class GgPokerHandParser : IHandHistoryParser
{
    public string RoomCode => "GGPOKER";
    public HandBoundary Boundary { get; } = new(["Poker Hand #"], "*** SUMMARY ***");

    /// <summary>
    /// Пояс, в котором GG пишет время. В заголовке зоны нет; по корпусу (имя файла «…-1515» = старт турнира, а первая
    /// раздача уровня 2 в 18:23) время — локальное компьютера игрока, а не UTC. Зону знает только пользователь;
    /// не задана — считаем UTC, и времена сдвинуты на смещение пояса игрока.
    /// </summary>
    public TimeZoneInfo? ClientTimeZone { get; init; }

    public bool CanParse(ReadOnlySpan<char> fileHead)
    {
        var s = fileHead.TrimStart('\uFEFF').TrimStart();
        return s.StartsWith("Poker Hand #");
    }

    [GeneratedRegex(@"^Poker Hand #", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HandStartRx();

    // Poker Hand #TM1000000001: Tournament #100000001, Bounty Hunters $21.60 Hold'em No Limit - Level2(60/120) - 2025/09/08 18:23:21
    // … - Level4(100/200(30)) - …
    [GeneratedRegex(@"^Poker Hand #(?<pre>[A-Z]{2})(?<id>\d+): Tournament #(?<tid>\d+), (?<name>.*?) (?<game>[^ ]+) (?<limit>No Limit|Pot Limit|Limit) - Level ?(?<level>\d+)\((?<sb>[\d.,]+)/(?<bb>[\d.,]+)(?:\((?<ante>[\d.,]+)\))?\) - (?<ts>\d{4}/\d\d/\d\d \d\d?:\d\d:\d\d)$")]
    private static partial Regex TourneyHeaderRx();

    // Poker Hand #RC1234567: Hold'em No Limit ($0.10/$0.20) - 2023/03/01 12:00:00
    [GeneratedRegex(@"^Poker Hand #(?<pre>[A-Z]{2})(?<id>\d+): (?<game>[^ ]+) (?<limit>No Limit|Pot Limit|Limit) \((?<cur1>[$€£]?)(?<sb>[\d.,]+)/(?<cur2>[$€£]?)(?<bb>[\d.,]+)\) - (?<ts>\d{4}/\d\d/\d\d \d\d?:\d\d:\d\d)$")]
    private static partial Regex CashHeaderRx();

    [GeneratedRegex(@"^Table '(.+)' (\d+)-max Seat #(\d+) is the button$")]
    private static partial Regex TableRx();

    // Seat 1: a1b2c3d4 (18,823 in chips) | (… in chips, $5 bounty) | ($20 in chips)
    [GeneratedRegex(@"^Seat (\d+): (.+) \(([$€£]?[\d.,]+) in chips(?:, [^)]*?bounty)?\)(?: (is sitting out))?$")]
    private static partial Regex SeatRx();

    [GeneratedRegex(@"^\*\*\* (HOLE CARDS|FLOP|TURN|RIVER|SHOWDOWN|SUMMARY) \*\*\*(.*)$")]
    private static partial Regex SectionRx();

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketRx();

    private const string Amt = @"[$€£]?[\d.,]+";

    [GeneratedRegex(@"^posts (small blind|big blind|the ante|small & big blinds) (" + Amt + @")( and is all-in)?$")]
    private static partial Regex PostRx();

    [GeneratedRegex(@"^(calls|bets) (" + Amt + @")( and is all-in)?$")]
    private static partial Regex CallBetRx();

    [GeneratedRegex(@"^raises " + Amt + @" to (" + Amt + @")( and is all-in)?$")]
    private static partial Regex RaiseRx();

    [GeneratedRegex(@"^shows \[([^\]]*)\](?: \(.*\))?$")]
    private static partial Regex ShowsRx();

    [GeneratedRegex(@"^collected (" + Amt + @") from pot$")]
    private static partial Regex CollectedRx();

    [GeneratedRegex(@"^Uncalled bet \((" + Amt + @")\) returned to (.+)$")]
    private static partial Regex UncalledRx();

    [GeneratedRegex(@"^Dealt to (.+?) \[([^\]]*)\]$")]
    private static partial Regex DealtCardsRx();

    // Seat 3: a1b2c3d4 won (4,640) | (big blind) collected (220) | showed [Qs Kd] and won (45,581) with a flush, Ace high | showed [7h] and won (3,080) | folded before Flop
    [GeneratedRegex(@"^((?:\((?:button|small blind|big blind)\) )*)(?:folded (?:before Flop|on the Flop|on the Turn|on the River)(?: \(didn't bet\))?|(?:won|collected) \((" + Amt + @")\)|showed \[([^\]]*)\] and (?:(?:won|collected) \((" + Amt + @")\)(?: with .*)?|lost(?: with .*)?)|mucked(?: \[([^\]]*)\])?)$")]
    private static partial Regex SummarySeatRx();

    private enum Section { Header, Preflop, Flop, Turn, River, Showdown, Summary }

    public IEnumerable<HandParseResult> Parse(string text, HandSourceInfo source, long baseOffset = 0) =>
        HhText.ParseAll(text, source, baseOffset, HandStartRx(), raw => ParseHand(raw, source));

    public ParsedHand ParseHand(string raw, HandSourceInfo source)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var h = new ParsedHand { RoomCode = RoomCode, SourceFile = source.FilePath, RawText = raw, Scale = 2 };
        int li = 0;
        ParseHeader(h, lines[li++].TrimStart('\uFEFF'), source);
        if (li >= lines.Length) throw new HandFormatError("Truncated hand: no table line");
        ParseTable(h, lines[li++], source);

        while (li < lines.Length && lines[li].StartsWith("Seat ", StringComparison.Ordinal))
        {
            var m = SeatRx().Match(lines[li]);
            if (!m.Success) throw new HandFormatError($"Bad seat line: {lines[li]}");
            h.Seats.Add(new ParsedSeat
            {
                Seat = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Name = m.Groups[2].Value,
                Stack = HhText.Amount(m.Groups[3].Value),
                SittingOut = m.Groups[4].Success,
            });
            li++;
        }
        if (h.Seats.Count == 0) throw new HandFormatError("No seats in header");
        var names = HhText.NamesByLength(h);

        var section = Section.Header;
        var street = Street.Preflop;
        int seq = 0;
        bool sawSummary = false, sawHole = false;
        var ledger = new BetLedger();   // вклады улицы без анте — чтобы отличить возврат ставки от возврата анте (Flipout)

        for (; li < lines.Length; li++)
        {
            var line = lines[li].TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("*** ", StringComparison.Ordinal))
            {
                var m = SectionRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Unknown section: {line}");
                switch (m.Groups[1].Value)
                {
                    case "HOLE CARDS": section = Section.Preflop; street = Street.Preflop; sawHole = true; break;
                    case "FLOP": ledger.FlushStreet(); section = Section.Flop; street = Street.Flop; SetBoard(h, m.Groups[2].Value, 3); break;
                    case "TURN": ledger.FlushStreet(); section = Section.Turn; street = Street.Turn; SetBoard(h, m.Groups[2].Value, 4); break;
                    case "RIVER": ledger.FlushStreet(); section = Section.River; street = Street.River; SetBoard(h, m.Groups[2].Value, 5); break;
                    case "SHOWDOWN": section = Section.Showdown; break;
                    case "SUMMARY": section = Section.Summary; sawSummary = true; break;
                }
                continue;
            }

            if (section == Section.Summary) { ParseSummary(h, line); continue; }

            if (line.StartsWith("Dealt to ", StringComparison.Ordinal))
            {
                var m = DealtCardsRx().Match(line);
                if (m.Success)
                {
                    var seat = h.SeatByName(m.Groups[1].Value) ?? throw new HandFormatError($"Dealt to unknown player: {line}");
                    HhText.SetHole(seat, m.Groups[2].Value);
                    seat.IsHero = true;
                    h.HeroName = seat.Name;
                }
                else
                {
                    // «Dealt to a1b2c3d4 » — GG пишет это про каждого игрока без карт; важно лишь, что игрок есть в шапке.
                    var nm = line["Dealt to ".Length..].TrimEnd();
                    if (h.SeatByName(nm) == null) throw new HandFormatError($"Dealt to unknown player: {line}");
                }
                continue;
            }
            if (line.StartsWith("Uncalled bet ", StringComparison.Ordinal))
            {
                var m = UncalledRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Bad uncalled line: {line}");
                var seat = h.SeatByName(m.Groups[2].Value) ?? throw new HandFormatError($"Uncalled to unknown player: {line}");
                var back = HhText.Amount(m.Groups[1].Value);
                // Flipout: анте = весь стек, блайнды 0, и «Uncalled bet» возвращает часть АНТЕ самого большого стека.
                // Движок возвращает только ставки улицы, поэтому лишнее снимаем с анте этого игрока (он и вложил
                // лишь уравненную часть) — иначе Σ вложено не сойдётся с банком.
                long fromAnte = Math.Max(0, back - ledger.Put(seat.Seat));
                if (fromAnte > 0)
                {
                    var ante = h.Actions.FirstOrDefault(a => a.Type == ActionType.PostAnte && a.Seat == seat.Seat)
                               ?? throw new HandFormatError($"Uncalled {back} exceeds bets of {seat.Name} and he has no ante: {line}");
                    if (ante.Amount < fromAnte) throw new HandFormatError($"Uncalled {back} exceeds ante+bets of {seat.Name}: {line}");
                    ante.Amount -= fromAnte;
                    h.Ante = h.Actions.Where(a => a.Type == ActionType.PostAnte).Max(a => a.Amount);
                    back -= fromAnte;
                }
                if (back > 0)
                    h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = ActionType.UncalledReturn, Amount = back });
                continue;
            }

            // «ник: действие» — с двоеточием; «ник collected …» — без.
            var (player, rest) = HhText.ResolvePlayer(h, names, line, ": ", " ");
            if (player == null) throw new HandFormatError($"Unknown line: {line}");
            bool colon = line.Length > player.Name.Length && line[player.Name.Length] == ':';

            if (colon && rest.StartsWith("posts ", StringComparison.Ordinal))
            {
                var m = PostRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad post: {line}");
                var amt = HhText.Amount(m.Groups[2].Value);
                var type = m.Groups[1].Value switch
                {
                    "small blind" => ActionType.PostSb,
                    "big blind" => ActionType.PostBb,
                    "the ante" => ActionType.PostAnte,
                    _ => ActionType.PostDead,
                };
                if (type == ActionType.PostBb && h.Actions.Any(a => a.Type == ActionType.PostBb)) type = ActionType.PostBbOot;
                if (type == ActionType.PostSb && h.Actions.Any(a => a.Type == ActionType.PostSb)) type = ActionType.PostDead;
                if (type == ActionType.PostAnte && amt > h.Ante) h.Ante = amt;
                if (type != ActionType.PostAnte) ledger.Add(player.Seat, amt);
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = Street.Preflop, Seat = player.Seat, Type = type, Amount = amt, IsAllIn = m.Groups[3].Success });
            }
            else if (colon && rest == "folds")
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Fold });
            else if (colon && rest == "checks")
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Check });
            else if (colon && (rest.StartsWith("calls ", StringComparison.Ordinal) || rest.StartsWith("bets ", StringComparison.Ordinal)))
            {
                var m = CallBetRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad call/bet: {line}");
                ledger.Add(player.Seat, HhText.Amount(m.Groups[2].Value));
                h.Actions.Add(new ParsedAction
                {
                    Seq = seq++, Street = street, Seat = player.Seat,
                    Type = m.Groups[1].Value == "calls" ? ActionType.Call : ActionType.Bet,
                    Amount = HhText.Amount(m.Groups[2].Value), IsAllIn = m.Groups[3].Success,
                });
            }
            else if (colon && rest.StartsWith("raises ", StringComparison.Ordinal))
            {
                var m = RaiseRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad raise: {line}");
                // «raises X to Y»: в Amount — уровень «до» (Y), доплату считает движок.
                var to = HhText.Amount(m.Groups[1].Value);
                ledger.Add(player.Seat, to - ledger.Put(player.Seat));
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Raise, Amount = HhText.Amount(m.Groups[1].Value), IsAllIn = m.Groups[2].Success });
            }
            else if (colon && rest.StartsWith("shows ", StringComparison.Ordinal))
            {
                var m = ShowsRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad shows: {line}");
                SetShown(player, m.Groups[1].Value);
            }
            else if (!colon && rest.StartsWith("collected ", StringComparison.Ordinal))
            {
                var m = CollectedRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad collected: {line}");
                // GG не делит банк на main/side в строках — всегда «from pot».
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Collect, Amount = HhText.Amount(m.Groups[1].Value) });
            }
            else throw new HandFormatError($"Unknown action: {line}");
        }

        if (!sawHole) throw new HandFormatError("No HOLE CARDS section");
        if (!sawSummary) throw new HandFormatError("No SUMMARY section (incomplete hand)");
        HhText.ReconcilePot(h);
        HhText.FinishFlags(h);
        HhText.ApplyHeroHint(h, source);
        return h;
    }

    /// <summary>
    /// Вскрытие: две карты — обычный показ; одна («showed [7h]») — GG открывает лишь нужную для победы карту,
    /// какая это из двух (первая или вторая) неизвестно, поэтому карту не сохраняем, но факт показа помечаем.
    /// </summary>
    private static void SetShown(ParsedSeat seat, string cards)
    {
        var cs = HhText.Cards(cards);
        if (cs.Length == 1) { seat.ShowedCards = true; return; }
        HhText.SetHole(seat, cards);
        seat.ShowedCards = true;
    }

    private void ParseHeader(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = TourneyHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = HandId(m.Groups["pre"].Value, m.Groups["id"].Value);
            h.TournamentId = long.Parse(m.Groups["tid"].Value, CultureInfo.InvariantCulture);
            h.GameVariant = Variant(m.Groups["game"].Value, m.Groups["limit"].Value);
            h.TournamentLevel = int.Parse(m.Groups["level"].Value, CultureInfo.InvariantCulture);
            h.Currency = "CHP";
            h.SmallBlind = HhText.Amount(m.Groups["sb"].Value);
            h.BigBlind = HhText.Amount(m.Groups["bb"].Value);
            if (m.Groups["ante"].Success) h.Ante = HhText.Amount(m.Groups["ante"].Value);
            h.PlayedAtUtc = HhText.ToUtc(m.Groups["ts"].Value, ClientTimeZone);
            h.GameKind = src.GameKind is GameKind.Sng ? GameKind.Sng : GameKind.Mtt;
            h.TableName = m.Groups["name"].Value;   // уточняется строкой стола
            return;
        }
        m = CashHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = HandId(m.Groups["pre"].Value, m.Groups["id"].Value);
            h.GameVariant = Variant(m.Groups["game"].Value, m.Groups["limit"].Value);
            h.SmallBlind = HhText.Amount(m.Groups["sb"].Value);
            h.BigBlind = HhText.Amount(m.Groups["bb"].Value);
            var cur = m.Groups["cur1"].Value;
            h.Currency = cur switch { "$" => "USD", "€" => "EUR", "£" => "GBP", _ => throw new HandFormatError($"Cash game without currency: {line}") };
            h.PlayedAtUtc = HhText.ToUtc(m.Groups["ts"].Value, ClientTimeZone);
            h.GameKind = GameKind.Cash;
            return;
        }
        throw new HandFormatError($"Bad hand header: {line}");
    }

    /// <summary>
    /// «TM1000000001» → число. Префикс TM (турнир) даёт просто цифры; другие префиксы (RC — кеш) сдвигаются в
    /// старшие разряды, чтобы одинаковые цифры в разных счётчиках GG не слились в один ExternalHandId.
    /// </summary>
    private static long HandId(string prefix, string digits)
    {
        long n = long.Parse(digits, CultureInfo.InvariantCulture);
        if (prefix == "TM") return n;
        long code = (prefix[0] - 'A') * 26L + (prefix[1] - 'A') + 1;
        return code * 1_000_000_000_000L + n;
    }

    private static string Variant(string game, string limit) => (game, limit) switch
    {
        ("Hold'em", "No Limit") => "NLHE",
        ("Hold'em", "Pot Limit") => "PLHE",
        ("Hold'em", "Limit") => "FLHE",
        _ => throw new HandFormatError($"Unsupported game variant: {game} {limit}"),
    };

    private static void ParseTable(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = TableRx().Match(line);
        if (!m.Success) throw new HandFormatError($"Bad table line: {line}");
        var tname = m.Groups[1].Value;
        h.MaxSeats = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        h.ButtonSeat = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        h.TableName = h.TournamentId is { } tid ? $"{h.TableName} #{tid} стол {tname}" : tname;
        h.ExternalGameId = src.ExternalGameId ?? (h.TournamentId is { } t ? "G" + t : "N" + tname);
        h.ExternalTableId = src.ExternalTableId ?? "T" + tname;
    }

    private static void ParseSummary(ParsedHand h, string line)
    {
        if (line.StartsWith("Total pot ", StringComparison.Ordinal))
        {
            // Total pot 45,581 | Rake 0 | Jackpot 0 | Bingo 0 | Fortune 0 | Tax 0
            var parts = line.Split(" | ");
            h.TotalPot = HhText.Amount(parts[0]["Total pot ".Length..]);
            for (int i = 1; i < parts.Length; i++)
            {
                int k = parts[i].LastIndexOf(' ');
                if (k <= 0) throw new HandFormatError($"Bad total pot line: {line}");
                var key = parts[i][..k];
                var val = HhText.Amount(parts[i][(k + 1)..]);
                if (key == "Rake") h.Rake = val;
                else if (val != 0) h.ExtraFees[key] = h.ExtraFees.GetValueOrDefault(key) + val;
            }
            return;
        }
        if (line.StartsWith("Board", StringComparison.Ordinal))
        {
            var cards = HhText.BoardCards(string.Concat(BracketRx().Matches(line).Select(b => b.Groups[1].Value + " ")));
            if (h.Boards.Count == 0) h.Boards.Add(new List<Card>());
            var b = h.Boards[0];
            if (b.Count == 0) b.AddRange(cards);
            else if (!b.SequenceEqual(cards)) throw new HandFormatError($"Board mismatch: {line}");
            return;
        }
        if (line.StartsWith("Seat ", StringComparison.Ordinal))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0 || !int.TryParse(line.AsSpan(5, colon - 5), NumberStyles.None, CultureInfo.InvariantCulture, out int seatNo))
                throw new HandFormatError($"Bad summary seat: {line}");
            var seat = h.SeatByNumber(seatNo) ?? throw new HandFormatError($"Summary for unknown seat: {line}");
            var body = line[(colon + 2)..];
            if (!body.StartsWith(seat.Name + " ", StringComparison.Ordinal)) throw new HandFormatError($"Summary seat name mismatch: {line}");
            var m = SummarySeatRx().Match(body[(seat.Name.Length + 1)..]);
            if (!m.Success) throw new HandFormatError($"Unknown summary result: {line}");
            // В SUMMARY перечислены все, кому сданы карты (в том числе сбросившие), — отсюда и Dealt.
            seat.Dealt = true;
            if (m.Groups[2].Success) seat.Won += HhText.Amount(m.Groups[2].Value);
            if (m.Groups[4].Success) seat.Won += HhText.Amount(m.Groups[4].Value);
            if (m.Groups[3].Success) SetShown(seat, m.Groups[3].Value);
            if (m.Groups[5].Success) { HhText.SetHole(seat, m.Groups[5].Value); seat.Mucked = true; }
            return;
        }
        throw new HandFormatError($"Unknown summary line: {line}");
    }

    /// <summary>«*** TURN *** [Ad 6d 7d] [2d]» — доска повторяется целиком, новая карта в отдельных скобках.</summary>
    private static void SetBoard(ParsedHand h, string tail, int expected)
    {
        var cards = new List<Card>();
        foreach (Match b in BracketRx().Matches(tail)) cards.AddRange(HhText.BoardCards(b.Groups[1].Value));
        if (cards.Count != expected) throw new HandFormatError($"Board has {cards.Count} cards, expected {expected}");
        if (h.Boards.Count == 0) h.Boards.Add(new List<Card>());
        h.Boards[0].Clear();
        h.Boards[0].AddRange(cards);
    }
}
