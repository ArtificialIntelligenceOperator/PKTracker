using System.Globalization;
using System.Text.RegularExpressions;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Parsers.Common;

namespace PKTracker.Parsers.PokerStars;

/// <summary>
/// Парсер hand history PokerStars (и клонов формата: тот же текст пишут Chico/«PokerStars-формат» конвертеры).
/// Кеш, Zoom, турниры (MTT/SNG/Spin), анте, сайд-поты, Run It Twice, мёртвые блайнды.
/// Строгий: неизвестная строка отклоняет раздачу целиком — смена формата должна быть видна сразу, а не через месяц
/// по кривым статам. Служебные строки (чат, вход/выход, таймауты) перечислены явно.
/// </summary>
public sealed partial class StarsHandParser : IHandHistoryParser
{
    public string RoomCode => "POKERSTARS";
    public HandBoundary Boundary { get; } = new(["PokerStars "], "*** SUMMARY ***");

    public bool CanParse(ReadOnlySpan<char> fileHead)
    {
        var s = fileHead.TrimStart('\uFEFF').TrimStart();
        return s.StartsWith("PokerStars Hand #") || s.StartsWith("PokerStars Game #") || s.StartsWith("PokerStars Zoom Hand #")
               || s.StartsWith("PokerStars Home Game Hand #");
    }

    [GeneratedRegex(@"^PokerStars (?:Zoom |Home Game )?(?:Hand|Game) #", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HandStartRx();

    // PokerStars Hand #200000000001: Tournament #3000000001, $9.00+$1.00 USD Hold'em No Limit - Level IX (400/800) - 2024/08/23 21:07:17 ET
    [GeneratedRegex(@"^PokerStars (?:Zoom |Home Game )?(?:Hand|Game) #(\d+): +Tournament #([\w-]+), (.*?) ?(Hold'em No Limit|Hold'em Pot Limit|Hold'em Limit) - (?:Match Round \w+, )?Level ?(\w*) \(([\d.,$€£]+)/([\d.,$€£]+)\) - (.+)$")]
    private static partial Regex TourneyHeaderRx();

    // PokerStars Hand #123: Hold'em No Limit ($0.05/$0.10 USD) - 2020/01/01 12:00:00 ET
    [GeneratedRegex(@"^PokerStars (?:Zoom |Home Game )?(?:Hand|Game) #(\d+): +(?:\{[^}]*\} +)?(Hold'em No Limit|Hold'em Pot Limit|Hold'em Limit) \(([\d.,$€£]+)/([\d.,$€£]+)(?: ([A-Z]{3}))?\) - (.+)$")]
    private static partial Regex CashHeaderRx();

    // 2024/08/23 21:07:17 ET  |  2020/01/01 18:00:00 CET [2020/01/01 12:00:00 ET]
    [GeneratedRegex(@"(\d{4}/\d\d/\d\d \d{1,2}:\d\d:\d\d) ([A-Z]{2,4})")]
    private static partial Regex TimeRx();

    [GeneratedRegex(@"^Table '(.+)' (\d+)-max (?:\(Play Money\) )?Seat #(\d+) is the button$")]
    private static partial Regex TableRx();

    // Seat 1: name (38820 in chips) | (1500 in chips, $5 bounty) | ($2.00 in chips) is sitting out | out of hand (...)
    [GeneratedRegex(@"^Seat (\d+): (.+) \((\$?[\d.,]+|€[\d.,]+|[\d.,]+€?) in chips(?:, [^)]*?bounty)?\)(?: (is sitting out|out of hand.*))?$")]
    private static partial Regex SeatRx();

    [GeneratedRegex(@"^\*\*\* (HOLE CARDS|FLOP|TURN|RIVER|SHOW DOWN|SUMMARY|FIRST FLOP|FIRST TURN|FIRST RIVER|SECOND FLOP|SECOND TURN|SECOND RIVER|FIRST SHOW DOWN|SECOND SHOW DOWN) \*\*\*(.*)$")]
    private static partial Regex SectionRx();

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketRx();

    [GeneratedRegex(@"^posts (small blind|big blind|the ante|small & big blinds) (\$?[\d.,]+|€[\d.,]+)( and is all-in)?$")]
    private static partial Regex PostRx();

    [GeneratedRegex(@"^(calls|bets) (\$?[\d.,]+|€[\d.,]+)( and is all-in)?$")]
    private static partial Regex CallBetRx();

    [GeneratedRegex(@"^raises (?:\$?[\d.,]+|€[\d.,]+) to (\$?[\d.,]+|€[\d.,]+)( and is all-in)?$")]
    private static partial Regex RaiseRx();

    [GeneratedRegex(@"^shows \[([^\]]*)\](?: \(.*\))?$")]
    private static partial Regex ShowsRx();

    [GeneratedRegex(@"^collected (\$?[\d.,]+|€[\d.,]+) from (pot|main pot|side pot(?:-(\d+))?)$")]
    private static partial Regex CollectedRx();

    [GeneratedRegex(@"^Uncalled bet \((\$?[\d.,]+|€[\d.,]+)\) returned to (.+)$")]
    private static partial Regex UncalledRx();

    [GeneratedRegex(@"^Dealt to (.+?) \[([^\]]*)\]$")]
    private static partial Regex DealtRx();

    [GeneratedRegex(@"^(?:(FIRST|SECOND) )?Board \[([^\]]*)\]$")]
    private static partial Regex BoardRx();

    // Seat 1: name (small blind) collected (6900) | showed [..] and won (123) with ... | folded before Flop (didn't bet)
    // Клоны формата пишут и усечённо: «showed [..]» без исхода и «and lost with» без названия комбинации.
    [GeneratedRegex(@"^((?:\((?:button|small blind|big blind)\) )*)(?:folded (?:before Flop|on the Flop|on the Turn|on the River)(?: \(didn't bet\))?|collected \((\$?[\d.,]+|€[\d.,]+)\)|showed \[([^\]]*)\](?: and (?:won \((\$?[\d.,]+|€[\d.,]+)\)|lost))?(?: with.*)?|mucked(?: \[([^\]]*)\])?)$")]
    private static partial Regex SummarySeatRx();

    // RIT: «showed [..] and won (100) with ..., and won (100) with ...» — суммы всех досок
    [GeneratedRegex(@"won \((\$?[\d.,]+|€[\d.,]+)\)")]
    private static partial Regex WonRx();

    /// <summary>Строки состояния игрока, которые не влияют на раздачу. Разделитель — пробел (без двоеточия).</summary>
    private static readonly string[] StatusTails =
    {
        "is sitting out", "has timed out", "has timed out while disconnected", "has timed out while being disconnected",
        "is disconnected", "is connected", "has returned", "leaves the table", "will be allowed to play after the button",
        "was removed from the table for failing to post", "sits out",
    };

    private static readonly string[] StatusPrefixes =
    {
        "said, \"", "joins the table at seat #", "finished the tournament", "wins the tournament", "re-buys and receives",
        "takes the add-on", "wins the $", "wins $", "wins the €", "wins €", "wins an entry", "wins a ", "has been disconnected", "is feeling",
        "has requested TIME", "was removed from the table",
    };

    public IEnumerable<HandParseResult> Parse(string text, HandSourceInfo source, long baseOffset = 0) =>
        HhText.ParseAll(text, source, baseOffset, HandStartRx(), raw => ParseHand(raw, source));

    private enum Section { Header, Preflop, Flop, Turn, River, Showdown, Summary }

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

        for (; li < lines.Length; li++)
        {
            var line = lines[li].TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("*** ", StringComparison.Ordinal))
            {
                var m = SectionRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Unknown section: {line}");
                var name = m.Groups[1].Value;
                int boardNo = name.StartsWith("SECOND", StringComparison.Ordinal) ? 2 : 1;
                if (name.StartsWith("FIRST", StringComparison.Ordinal) || name.StartsWith("SECOND", StringComparison.Ordinal))
                    h.Flags |= HandFlags.RunItTwice;
                var bare = name.Replace("FIRST ", "").Replace("SECOND ", "");
                switch (bare)
                {
                    case "HOLE CARDS": section = Section.Preflop; street = Street.Preflop; sawHole = true; break;
                    case "FLOP": section = Section.Flop; street = Street.Flop; SetBoard(h, boardNo, m.Groups[2].Value, 3); break;
                    case "TURN": section = Section.Turn; street = Street.Turn; SetBoard(h, boardNo, m.Groups[2].Value, 4); break;
                    case "RIVER": section = Section.River; street = Street.River; SetBoard(h, boardNo, m.Groups[2].Value, 5); break;
                    case "SHOW DOWN": section = Section.Showdown; break;
                    case "SUMMARY": section = Section.Summary; sawSummary = true; break;
                }
                continue;
            }

            if (section == Section.Summary) { ParseSummary(h, line); continue; }

            if (line.StartsWith("Dealt to ", StringComparison.Ordinal))
            {
                var m = DealtRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Bad dealt line: {line}");
                var seat = h.SeatByName(m.Groups[1].Value) ?? throw new HandFormatError($"Dealt to unknown player: {line}");
                // В Zoom/старых версиях «Dealt to» бывает и с «[-]» — карты Hero всегда полные.
                if (m.Groups[2].Value.Trim().Length > 0) HhText.SetHole(seat, m.Groups[2].Value);
                seat.IsHero = true;
                h.HeroName = seat.Name;
                continue;
            }
            if (line.StartsWith("Uncalled bet ", StringComparison.Ordinal))
            {
                var m = UncalledRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Bad uncalled line: {line}");
                var seat = h.SeatByName(m.Groups[2].Value) ?? throw new HandFormatError($"Uncalled to unknown player: {line}");
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = ActionType.UncalledReturn, Amount = HhText.Amount(m.Groups[1].Value) });
                continue;
            }
            if (line == "Hand was run twice" || line.StartsWith("Hand cancelled", StringComparison.Ordinal))
            {
                if (line.StartsWith("Hand cancelled", StringComparison.Ordinal)) throw new HandFormatError("Hand cancelled");
                continue;
            }

            // «ник: действие» — с двоеточием; «ник collected …», «ник is sitting out» — без.
            var (player, rest) = HhText.ResolvePlayer(h, names, line, ": ", " ");
            if (player == null)
            {
                // Строка чата или статуса про игрока, которого нет в шапке (зашёл после раздачи), безвредна.
                if (line.Contains(" said, \"", StringComparison.Ordinal) || line.Contains(" joins the table", StringComparison.Ordinal)
                    || line.Contains(" leaves the table", StringComparison.Ordinal)
                    || StatusTails.Any(t => line.EndsWith(" " + t, StringComparison.Ordinal))) continue;
                throw new HandFormatError($"Unknown line: {line}");
            }
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
                    _ => ActionType.PostDead, // «small & big blinds» — мёртвый SB + живой BB одной строкой
                };
                // Пост BB вне очереди (новичок за столом) Stars пишет так же, как обычный BB: второй BB в раздаче — вне очереди.
                if (type == ActionType.PostBb && h.Actions.Any(a => a.Type == ActionType.PostBb)) type = ActionType.PostBbOot;
                if (type == ActionType.PostSb && h.Actions.Any(a => a.Type == ActionType.PostSb)) type = ActionType.PostDead;
                if (type == ActionType.PostAnte && amt > h.Ante) h.Ante = amt;
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = Street.Preflop, Seat = player.Seat, Type = type, Amount = amt, IsAllIn = m.Groups[3].Success });
            }
            else if (colon && rest == "folds" || colon && rest.StartsWith("folds [", StringComparison.Ordinal))
            {
                if (rest.StartsWith("folds [", StringComparison.Ordinal)) SetShown(player, BracketRx().Match(rest).Groups[1].Value);
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Fold });
            }
            else if (colon && rest == "checks")
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Check });
            else if (colon && (rest.StartsWith("calls ", StringComparison.Ordinal) || rest.StartsWith("bets ", StringComparison.Ordinal)))
            {
                var m = CallBetRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad call/bet: {line}");
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
                // «raises X to Y»: X — прибавка, берём только Y (уровень «до»), доплату считает движок.
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Raise, Amount = HhText.Amount(m.Groups[1].Value), IsAllIn = m.Groups[2].Success });
            }
            else if (colon && rest.StartsWith("shows ", StringComparison.Ordinal))
            {
                var m = ShowsRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad shows: {line}");
                // Клоны формата позволяют показать одну карту («shows [Jd]»): известна она одна, вторая — нет.
                var shown = HhText.Cards(m.Groups[1].Value);
                if (shown.Length == 1) player.HoleCards[0] ??= shown[0];
                else HhText.SetHole(player, m.Groups[1].Value);
                player.ShowedCards = true;
            }
            else if (colon && (rest == "mucks hand" || rest == "doesn't show hand" || rest.StartsWith("mucks hand [", StringComparison.Ordinal)))
            {
                if (rest.StartsWith("mucks", StringComparison.Ordinal)) player.Mucked = true;
            }
            else if (!colon && rest.StartsWith("collected ", StringComparison.Ordinal))
            {
                var m = CollectedRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad collected: {line}");
                int potNo = m.Groups[2].Value.StartsWith("side", StringComparison.Ordinal) ? (m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1) : 0;
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Collect, Amount = HhText.Amount(m.Groups[1].Value), PotNo = potNo });
            }
            else if (!colon && rest.StartsWith("cashed out the hand", StringComparison.Ordinal))
                throw new HandFormatError("Cash Out hand is not supported (money invariant would break)");
            else if (colon && (rest == "sits out" || rest == "is sitting out")) player.SittingOut = true;
            else if (!colon && StatusTails.Contains(rest)) { if (rest is "is sitting out" or "sits out") player.SittingOut = true; }
            else if (!colon && StatusPrefixes.Any(p => rest.StartsWith(p, StringComparison.Ordinal))) { }
            else if (colon && rest.StartsWith("said", StringComparison.Ordinal)) { }
            else throw new HandFormatError($"Unknown action: {line}");
        }

        if (!sawHole) throw new HandFormatError("No HOLE CARDS section");
        if (!sawSummary) throw new HandFormatError("No SUMMARY section (incomplete hand)");
        UncalledOutOfWinnings(h);
        // В кеше «Total pot» напечатан вместе с рейком — приводим к чистому банку и сверяем с выигрышами.
        HhText.ReconcilePot(h);
        HhText.FinishFlags(h);
        HhText.ApplyHeroHint(h, source);
        return h;
    }

    /// <summary>
    /// Некоторые конвертеры истории в формат PokerStars при коротком колле олл-ина печатают и
    /// «Uncalled bet (25) returned to X», и выигрыш X вместе с этими 25: «collected 30175» при «Total pot 30150».
    /// Возврат при этом учтён дважды, и деньги раздачи не сходятся ровно на его сумму. Если расхождение
    /// в точности равно возвратам победителей — вычитаем их из выигрыша; любое другое расхождение
    /// остаётся ошибкой раздачи (её поймает сверка банка).
    /// </summary>
    private static void UncalledOutOfWinnings(ParsedHand h)
    {
        long won = h.Seats.Sum(s => s.Won);
        if (won == h.TotalPot || won == h.TotalPot - h.Rake - h.TotalExtraFees) return;
        var returned = h.Actions.Where(a => a.Type == ActionType.UncalledReturn && h.SeatByNumber(a.Seat) is { Won: > 0 }).ToList();
        if (returned.Count == 0 || won - returned.Sum(a => a.Amount) != h.TotalPot - h.Rake - h.TotalExtraFees) return;
        foreach (var r in returned)
        {
            var seat = h.SeatByNumber(r.Seat)!;
            if (seat.Won < r.Amount) return;
            seat.Won -= r.Amount;
            var collect = h.Actions.LastOrDefault(a => a.Type == ActionType.Collect && a.Seat == r.Seat && a.Amount >= r.Amount);
            if (collect != null) collect.Amount -= r.Amount;
        }
    }

    private static void ParseHeader(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = TourneyHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            h.TournamentId = HhText.TournamentId(m.Groups[2].Value);
            h.GameVariant = Variant(m.Groups[4].Value);
            // Клоны формата (Chico) пишут «Level (625/1250)» без номера уровня.
            h.TournamentLevel = m.Groups[5].Value.Length > 0 ? HhText.RomanOrInt(m.Groups[5].Value) : null;
            h.Currency = "CHP";
            h.SmallBlind = HhText.Amount(m.Groups[6].Value);
            h.BigBlind = HhText.Amount(m.Groups[7].Value);
            h.PlayedAtUtc = Time(m.Groups[8].Value, line);
            // Тип турнира в тексте раздачи не различим; по умолчанию MTT, подсказка — из имени файла.
            h.GameKind = src.GameKind is GameKind.Sng ? GameKind.Sng : GameKind.Mtt;
            var buyin = m.Groups[3].Value.Trim();
            h.TableName = buyin; // уточняется строкой стола
            return;
        }
        m = CashHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            h.GameVariant = Variant(m.Groups[2].Value);
            h.SmallBlind = HhText.Amount(m.Groups[3].Value);
            h.BigBlind = HhText.Amount(m.Groups[4].Value);
            h.Currency = m.Groups[5].Success ? m.Groups[5].Value : (m.Groups[3].Value.Contains('$') ? "USD" : "CHP");
            h.PlayedAtUtc = Time(m.Groups[6].Value, line);
            h.GameKind = GameKind.Cash;
            return;
        }
        throw new HandFormatError($"Bad hand header: {line}");
    }

    private static DateTime Time(string tail, string line)
    {
        // Если есть время в ET (в квадратных скобках) — берём его: зона однозначна.
        DateTime? best = null;
        foreach (Match t in TimeRx().Matches(tail))
        {
            var utc = HhText.ToUtc(t.Groups[1].Value, t.Groups[2].Value);
            if (t.Groups[2].Value == "ET" || best == null) best = utc;
        }
        return best ?? throw new HandFormatError($"Bad hand time: {line}");
    }

    private static string Variant(string s) => s switch
    {
        "Hold'em No Limit" => "NLHE",
        "Hold'em Pot Limit" => "PLHE",
        "Hold'em Limit" => "FLHE",
        _ => throw new HandFormatError($"Unsupported game variant: {s}"),
    };

    private static void ParseTable(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = TableRx().Match(line);
        if (!m.Success) throw new HandFormatError($"Bad table line: {line}");
        var tname = m.Groups[1].Value;
        h.MaxSeats = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        h.ButtonSeat = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var buyin = h.TableName;
        h.TableName = h.TournamentId is { } tid
            ? $"{(buyin.Length > 0 ? buyin + " " : "")}#{tid} стол {tname.Split(' ')[^1]}"
            : tname;
        h.ExternalGameId = src.ExternalGameId ?? (h.TournamentId is { } t ? "G" + t : "N" + tname);
        h.ExternalTableId = src.ExternalTableId ?? "T" + tname;
    }

    private static void ParseSummary(ParsedHand h, string line)
    {
        if (line.StartsWith("Total pot ", StringComparison.Ordinal))
        {
            // Total pot 28145 Main pot 14580. Side pot-1 13565. | Rake 0
            var parts = line.Split(" | ");
            var head = parts[0]["Total pot ".Length..];
            int sp = head.IndexOf(' ');
            h.TotalPot = HhText.Amount(sp > 0 ? head[..sp] : head);
            for (int i = 1; i < parts.Length; i++)
            {
                int k = parts[i].LastIndexOf(' ');
                if (k <= 0) throw new HandFormatError($"Bad total pot line: {line}");
                var key = parts[i][..k];
                var val = HhText.Amount(parts[i][(k + 1)..]);
                if (key == "Rake") h.Rake = val;
                else h.ExtraFees[key] = h.ExtraFees.GetValueOrDefault(key) + val;
            }
            return;
        }
        if (line == "Hand was run twice") return;
        if (line.StartsWith("Board", StringComparison.Ordinal) || line.StartsWith("FIRST Board", StringComparison.Ordinal) || line.StartsWith("SECOND Board", StringComparison.Ordinal))
        {
            var m = BoardRx().Match(line);
            if (!m.Success) throw new HandFormatError($"Bad board line: {line}");
            int n = m.Groups[1].Value == "SECOND" ? 2 : 1;
            var cards = HhText.BoardCards(m.Groups[2].Value);
            while (h.Boards.Count < n) h.Boards.Add(new List<Card>());
            var b = h.Boards[n - 1];
            if (b.Count == 0) b.AddRange(cards);
            else if (b.Count != cards.Count) throw new HandFormatError($"Board mismatch: {line}");
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
            var rest = body[(seat.Name.Length + 1)..];
            var m = SummarySeatRx().Match(rest);
            // Run It Twice: «… and won (x) with …, and won (y) with …». Общий шаблон проглатывает второй выигрыш
            // в хвост «with …», поэтому такие строки разбираем отдельно и суммируем все «won (…)».
            // Клоны формата так же перечисляют выигрыш из основного и побочных банков: «won (main) …, and won (side) …».
            int wins = WonRx().Matches(rest).Count;
            if (m.Success && wins > 1) m = Match.Empty;
            if (!m.Success)
            {
                // RIT: «showed [..] and won (x) with …, and won (y) with …» / «… and lost with …, and won (y) …»
                if (rest.Contains("showed [", StringComparison.Ordinal) && ((h.Flags & HandFlags.RunItTwice) != 0 || wins > 1))
                {
                    seat.Dealt = true;
                    var c = BracketRx().Match(rest);
                    if (c.Success) { HhText.SetHole(seat, c.Groups[1].Value); seat.ShowedCards = true; }
                    foreach (Match w in WonRx().Matches(rest)) seat.Won += HhText.Amount(w.Groups[1].Value);
                    return;
                }
                throw new HandFormatError($"Unknown summary result: {line}");
            }
            seat.Dealt = true;
            if (m.Groups[2].Success) seat.Won += HhText.Amount(m.Groups[2].Value);
            if (m.Groups[4].Success) seat.Won += HhText.Amount(m.Groups[4].Value);
            if (m.Groups[3].Success) { SetShown(seat, m.Groups[3].Value); seat.ShowedCards = true; }
            if (m.Groups[5].Success) { SetShown(seat, m.Groups[5].Value); seat.Mucked = true; }
            return;
        }
        throw new HandFormatError($"Unknown summary line: {line}");
    }

    /// <summary>Показанные карты: две — как обычно; одна (клоны формата позволяют показать одну) — только она.</summary>
    private static void SetShown(ParsedSeat seat, string cards)
    {
        var cs = HhText.Cards(cards);
        if (cs.Length == 1) seat.HoleCards[0] ??= cs[0];
        else HhText.SetHole(seat, cards);
    }

    private static void SetBoard(ParsedHand h, int boardNo, string tail, int expected)
    {
        var cards = new List<Card>();
        foreach (Match b in BracketRx().Matches(tail)) cards.AddRange(HhText.BoardCards(b.Groups[1].Value));
        // На второй доске RIT общие карты могут не повторяться: «*** SECOND TURN *** [..] [Kd]» — тогда берём их с первой.
        if (cards.Count < expected && boardNo == 2 && h.Boards.Count >= 1)
        {
            var first = h.Boards[0];
            var merged = first.Take(expected - cards.Count).ToList();
            merged.AddRange(cards);
            cards = merged;
        }
        if (cards.Count != expected) throw new HandFormatError($"Board has {cards.Count} cards, expected {expected}");
        int idx = boardNo - 1;
        while (h.Boards.Count <= idx) h.Boards.Add(new List<Card>());
        h.Boards[idx].Clear();
        h.Boards[idx].AddRange(cards);
    }
}
