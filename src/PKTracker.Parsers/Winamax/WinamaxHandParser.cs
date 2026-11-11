using System.Globalization;
using System.Text.RegularExpressions;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Parsers.Common;

namespace PKTracker.Parsers.Winamax;

/// <summary>
/// Парсер hand history Winamax («Winamax Poker - Tournament "…" …», «Winamax Poker - CashGame - …»).
/// Только холдем no limit. Строгий: неизвестная строка отклоняет раздачу целиком.
/// Ловушки формата: (1) HandId составной «стол-номер-время» — числом не выражается; (2) в SUMMARY перечислены
/// только выигравшие и вскрывшиеся, сбросившие карты не перечислены — «раздан» выводится по участию в раздаче;
/// (3) «Uncalled bet» не пишется: неуравненная часть ставки входит в «Total pot» и в «collected»
/// (часто отдельным «side pot») — мы возвращаем её явным UncalledReturn и вычитаем из банка и выигрыша,
/// чтобы деньги считались так же, как у PokerStars.
/// </summary>
public sealed partial class WinamaxHandParser : IHandHistoryParser
{
    public string RoomCode => "WINAMAX";
    public HandBoundary Boundary { get; } = new(["Winamax Poker - "], "*** SUMMARY ***");

    public bool CanParse(ReadOnlySpan<char> fileHead)
    {
        var s = fileHead.TrimStart('\uFEFF').TrimStart();
        return s.StartsWith("Winamax Poker - ");
    }

    // «Tournament summary» — не раздача, а итоги турнира; в начало раздачи не попадает.
    [GeneratedRegex(@"^Winamax Poker - (?!Tournament summary)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HandStartRx();

    // Winamax Poker - Tournament "THUNDER" buyIn: 9€ + 1€ level: 1 - HandId: #1000000000000000001-1-1700000001 - Holdem no limit (25/100/200) - 2026/10/10 10:00:10 UTC
    [GeneratedRegex(@"^Winamax Poker - (?<kind>Tournament|Go Fast|Expresso[^""]*?) ""(?<name>.*)"" buyIn: (?<buyin>.*?) (?:level: (?<level>\d+) )?- HandId: #(?<id>[\d-]+) - (?<game>.+?) \((?<blinds>[^)]+)\) - (?<ts>\d{4}/\d\d/\d\d \d\d?:\d\d:\d\d) UTC$")]
    private static partial Regex TourneyHeaderRx();

    // Winamax Poker - CashGame - HandId: #1000001-2-1300000001 - Holdem no limit (0.01€/0.02€) - 2013/06/04 17:41:24 UTC
    [GeneratedRegex(@"^Winamax Poker - CashGame - HandId: #(?<id>[\d-]+) - (?<game>.+?) \((?<blinds>[^)]+)\) - (?<ts>\d{4}/\d\d/\d\d \d\d?:\d\d:\d\d) UTC$")]
    private static partial Regex CashHeaderRx();

    // Table: 'COFFEE TIME(100000003)#006' 6-max (real money) Seat #4 is the button
    [GeneratedRegex(@"^Table: '(?<name>.+)' (?<max>\d+)-max \((?<money>real money|play money)\) Seat #(?<btn>\d+) is the button$")]
    private static partial Regex TableRx();

    [GeneratedRegex(@"\((\d+)\)#\d+$")]
    private static partial Regex TourneyIdRx();

    // Seat 1: Player1 (20000, 5€ bounty) | Seat 1: Player (2.22€)
    [GeneratedRegex(@"^Seat (\d+): (.+) \(([\d.,]+[€$£]?)(?:, [^)]*bounty)?\)$")]
    private static partial Regex SeatRx();

    [GeneratedRegex(@"^\*\*\* (ANTE/BLINDS|PRE-FLOP|FLOP|TURN|RIVER|SHOW DOWN|SUMMARY) \*\*\*(.*)$")]
    private static partial Regex SectionRx();

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketRx();

    [GeneratedRegex(@"^posts (ante|small blind|big blind) ([$€£]?[\d.,]+[€$£]?)( and is all-in)?$")]
    private static partial Regex PostRx();

    [GeneratedRegex(@"^(calls|bets) ([$€£]?[\d.,]+[€$£]?)( and is all-in)?$")]
    private static partial Regex CallBetRx();

    [GeneratedRegex(@"^raises [$€£]?[\d.,]+[€$£]? to ([$€£]?[\d.,]+[€$£]?)( and is all-in)?$")]
    private static partial Regex RaiseRx();

    [GeneratedRegex(@"^shows \[([^\]]*)\] \(.*\)$")]
    private static partial Regex ShowsRx();

    [GeneratedRegex(@"^collected ([$€£]?[\d.,]+[€$£]?) from (pot|main pot|side pot(?: (\d+))?)$")]
    private static partial Regex CollectedRx();

    [GeneratedRegex(@"^Dealt to (.+) \[([^\]]*)\]$")]
    private static partial Regex DealtRx();

    // Seat 2: name (small blind) (button) won 2895 | showed [Ad Th] and won 2950 with … | showed [..] and lost with …
    [GeneratedRegex(@"^((?:\((?:button|small blind|big blind)\) ?)*)(?:won ([$€£]?[\d.,]+[€$£]?)|showed \[([^\]]*)\] and (?:won ([$€£]?[\d.,]+[€$£]?) with .+|lost with .+)|folded (?:before (?:the )?Flop|on the (?:Flop|Turn|River))(?: \(didn't bet\))?)$")]
    private static partial Regex SummarySeatRx();

    private enum Section { AnteBlinds, Preflop, Flop, Turn, River, Showdown, Summary }

    public IEnumerable<HandParseResult> Parse(string text, HandSourceInfo source, long baseOffset = 0) =>
        HhText.ParseAll(text, source, baseOffset, HandStartRx(), raw => ParseHand(raw, source));

    public ParsedHand ParseHand(string raw, HandSourceInfo source)
    {
        // Файл может заканчиваться блоком «Tournament summary» — он не раздача и не должен попасть в разбор.
        int cut = raw.IndexOf("\nWinamax Poker - Tournament summary", StringComparison.Ordinal);
        if (cut >= 0) raw = raw[..cut];
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
            });
            li++;
        }
        if (h.Seats.Count == 0) throw new HandFormatError("No seats in header");
        var names = HhText.NamesByLength(h);

        var section = Section.AnteBlinds;
        var street = Street.Preflop;
        int seq = 0;
        bool sawAnteBlinds = false, sawPreflop = false, sawSummary = false;
        var ledger = new BetLedger();
        var uncalled = new List<(int Seat, long Amount)>();
        var collected = new Dictionary<int, long>();   // «collected» из тела раздачи — для сверки с SUMMARY
        var dealt = new HashSet<int>();                // кто участвовал: у Winamax в SUMMARY сбросившие не перечислены
        var summarySeats = new HashSet<int>();

        void FlushLedger()
        {
            if (ledger.FlushInto(h, ref seq, street) is { } u) uncalled.Add(u);
        }

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
                    case "ANTE/BLINDS": section = Section.AnteBlinds; sawAnteBlinds = true; break;
                    case "PRE-FLOP": section = Section.Preflop; street = Street.Preflop; sawPreflop = true; break;
                    case "FLOP": FlushLedger(); section = Section.Flop; street = Street.Flop; SetBoard(h, m.Groups[2].Value, 3); break;
                    case "TURN": FlushLedger(); section = Section.Turn; street = Street.Turn; SetBoard(h, m.Groups[2].Value, 4); break;
                    case "RIVER": FlushLedger(); section = Section.River; street = Street.River; SetBoard(h, m.Groups[2].Value, 5); break;
                    case "SHOW DOWN": section = Section.Showdown; break;
                    case "SUMMARY": FlushLedger(); section = Section.Summary; sawSummary = true; break;
                }
                continue;
            }
            if (!sawAnteBlinds) throw new HandFormatError($"Line before ANTE/BLINDS section: {line}");

            if (section == Section.Summary) { ParseSummary(h, line, summarySeats); continue; }

            if (line.StartsWith("Dealt to ", StringComparison.Ordinal))
            {
                var m = DealtRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Bad dealt line: {line}");
                var seat = h.SeatByName(m.Groups[1].Value) ?? throw new HandFormatError($"Dealt to unknown player: {line}");
                HhText.SetHole(seat, m.Groups[2].Value);
                seat.IsHero = true;
                h.HeroName = seat.Name;
                dealt.Add(seat.Seat);
                continue;
            }

            var (player, rest) = HhText.ResolvePlayer(h, names, line, " ");
            if (player == null) throw new HandFormatError($"Unknown line: {line}");

            if (rest.StartsWith("posts ", StringComparison.Ordinal))
            {
                var m = PostRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad post: {line}");
                var amt = HhText.Amount(m.Groups[2].Value);
                var type = m.Groups[1].Value switch
                {
                    "small blind" => ActionType.PostSb,
                    "big blind" => ActionType.PostBb,
                    _ => ActionType.PostAnte,
                };
                // Второй BB в раздаче — пост вне очереди (вернувшийся игрок), второй SB — мёртвый: как у PokerStars.
                if (type == ActionType.PostBb && h.Actions.Any(a => a.Type == ActionType.PostBb)) type = ActionType.PostBbOot;
                if (type == ActionType.PostSb && h.Actions.Any(a => a.Type == ActionType.PostSb)) type = ActionType.PostDead;
                if (type == ActionType.PostAnte) { if (amt > h.Ante) h.Ante = amt; }
                else ledger.Add(player.Seat, amt);
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = Street.Preflop, Seat = player.Seat, Type = type, Amount = amt, IsAllIn = m.Groups[3].Success });
                dealt.Add(player.Seat);
            }
            else if (rest == "folds")
            {
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Fold });
                dealt.Add(player.Seat);
            }
            else if (rest == "checks")
            {
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Check });
                dealt.Add(player.Seat);
            }
            else if (rest.StartsWith("calls ", StringComparison.Ordinal) || rest.StartsWith("bets ", StringComparison.Ordinal))
            {
                var m = CallBetRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad call/bet: {line}");
                var amt = HhText.Amount(m.Groups[2].Value);
                ledger.Add(player.Seat, amt);
                h.Actions.Add(new ParsedAction
                {
                    Seq = seq++, Street = street, Seat = player.Seat,
                    Type = m.Groups[1].Value == "calls" ? ActionType.Call : ActionType.Bet,
                    Amount = amt, IsAllIn = m.Groups[3].Success,
                });
                dealt.Add(player.Seat);
            }
            else if (rest.StartsWith("raises ", StringComparison.Ordinal))
            {
                var m = RaiseRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad raise: {line}");
                // «raises X to Y»: X — прибавка к текущему уровню, в Amount кладём уровень «до» (Y).
                var to = HhText.Amount(m.Groups[1].Value);
                ledger.Add(player.Seat, to - ledger.Put(player.Seat));
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Raise, Amount = to, IsAllIn = m.Groups[2].Success });
                dealt.Add(player.Seat);
            }
            else if (rest.StartsWith("shows ", StringComparison.Ordinal))
            {
                var m = ShowsRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad shows: {line}");
                HhText.SetHole(player, m.Groups[1].Value);
                player.ShowedCards = true;
                dealt.Add(player.Seat);
            }
            else if (rest.StartsWith("collected ", StringComparison.Ordinal))
            {
                var m = CollectedRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad collected: {line}");
                int potNo = m.Groups[2].Value.StartsWith("side", StringComparison.Ordinal) ? (m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1) : 0;
                var amt = HhText.Amount(m.Groups[1].Value);
                collected[player.Seat] = collected.GetValueOrDefault(player.Seat) + amt;
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Collect, Amount = amt, PotNo = potNo });
                dealt.Add(player.Seat);
            }
            else throw new HandFormatError($"Unknown action: {line}");
        }

        if (!sawPreflop) throw new HandFormatError("No PRE-FLOP section");
        if (!sawSummary) throw new HandFormatError("No SUMMARY section (incomplete hand)");

        foreach (var s in h.Seats)
            if (dealt.Contains(s.Seat) || summarySeats.Contains(s.Seat)) s.Dealt = true;
            else s.SittingOut = true;   // в шапке есть, но в раздаче не участвовал

        // Сверка: «collected» в теле раздачи и «won» в SUMMARY должны совпасть у каждого игрока.
        foreach (var s in h.Seats)
            if (collected.GetValueOrDefault(s.Seat) != s.Won)
                throw new HandFormatError($"collected ({collected.GetValueOrDefault(s.Seat)}) != summary won ({s.Won}) for {s.Name}");

        // Неуравненная часть ставки у Winamax внутри банка и выигрыша — выносим её в UncalledReturn.
        foreach (var (seat, amount) in uncalled)
        {
            var s = h.SeatByNumber(seat)!;
            if (s.Won < amount) throw new HandFormatError($"Uncalled {amount} exceeds win of {s.Name} ({s.Won})");
            s.Won -= amount;
            h.TotalPot -= amount;
            // Информационные Collect тоже уменьшаем (с конца: возврат Winamax пишет последним «side pot»).
            long left = amount;
            for (int i = h.Actions.Count - 1; i >= 0 && left > 0; i--)
            {
                var a = h.Actions[i];
                if (a.Type != ActionType.Collect || a.Seat != seat) continue;
                long take = Math.Min(a.Amount, left);
                a.Amount -= take;
                left -= take;
                if (a.Amount == 0) h.Actions.RemoveAt(i);
            }
        }

        HhText.ReconcilePot(h);
        HhText.FinishFlags(h);
        HhText.ApplyHeroHint(h, source);
        return h;
    }

    private static void ParseHeader(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = TourneyHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = HhText.StableId(m.Groups["id"].Value);
            h.GameVariant = Variant(m.Groups["game"].Value);
            h.Currency = "CHP";
            if (m.Groups["level"].Success) h.TournamentLevel = int.Parse(m.Groups["level"].Value, CultureInfo.InvariantCulture);
            SetBlinds(h, m.Groups["blinds"].Value);
            h.PlayedAtUtc = HhText.ToUtc(m.Groups["ts"].Value, "UTC");
            // Expresso/Go Fast — спин-подобные SNG; обычный турнир по тексту не отличить от SNG, подсказка из имени файла.
            h.GameKind = m.Groups["kind"].Value == "Tournament" ? (src.GameKind is GameKind.Sng ? GameKind.Sng : GameKind.Mtt) : GameKind.Sng;
            h.TableName = m.Groups["name"].Value;   // уточняется строкой стола
            return;
        }
        m = CashHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = HhText.StableId(m.Groups["id"].Value);
            h.GameVariant = Variant(m.Groups["game"].Value);
            SetBlinds(h, m.Groups["blinds"].Value);
            h.Currency = CurrencyOf(m.Groups["blinds"].Value);
            h.PlayedAtUtc = HhText.ToUtc(m.Groups["ts"].Value, "UTC");
            h.GameKind = GameKind.Cash;
            return;
        }
        throw new HandFormatError($"Bad hand header: {line}");
    }

    private static string CurrencyOf(string amounts) =>
        amounts.Contains('€') ? "EUR" : amounts.Contains('$') ? "USD" : amounts.Contains('£') ? "GBP"
        : throw new HandFormatError($"Unknown currency in cash blinds: {amounts}");

    /// <summary>«25/100/200» = анте/SB/BB, «100/200» = SB/BB.</summary>
    private static void SetBlinds(ParsedHand h, string blinds)
    {
        var p = blinds.Split('/');
        if (p.Length == 3) { h.Ante = HhText.Amount(p[0]); h.SmallBlind = HhText.Amount(p[1]); h.BigBlind = HhText.Amount(p[2]); }
        else if (p.Length == 2) { h.SmallBlind = HhText.Amount(p[0]); h.BigBlind = HhText.Amount(p[1]); }
        else throw new HandFormatError($"Bad blinds: {blinds}");
    }

    private static string Variant(string s) => s switch
    {
        "Holdem no limit" => "NLHE",
        _ => throw new HandFormatError($"Unsupported game variant: {s}"),
    };

    private static void ParseTable(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = TableRx().Match(line);
        if (!m.Success) throw new HandFormatError($"Bad table line: {line}");
        if (m.Groups["money"].Value != "real money") throw new HandFormatError("Play money tables are not supported");
        var tname = m.Groups["name"].Value;
        h.MaxSeats = int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture);
        h.ButtonSeat = int.Parse(m.Groups["btn"].Value, CultureInfo.InvariantCulture);
        if (h.GameKind != GameKind.Cash)
        {
            // 'COFFEE TIME(100000003)#006' — id турнира в скобках, номер стола после решётки.
            var t = TourneyIdRx().Match(tname);
            if (!t.Success) throw new HandFormatError($"No tournament id in table name: {line}");
            h.TournamentId = long.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
            h.TableName = $"{h.TableName} #{h.TournamentId} стол {tname[(tname.LastIndexOf('#') + 1)..]}";
            h.ExternalGameId = src.ExternalGameId ?? "G" + h.TournamentId;
        }
        else
        {
            h.TableName = tname;
            h.ExternalGameId = src.ExternalGameId ?? "N" + tname;
        }
        h.ExternalTableId = src.ExternalTableId ?? "T" + tname;
    }

    private static void ParseSummary(ParsedHand h, string line, HashSet<int> summarySeats)
    {
        if (line.StartsWith("Total pot ", StringComparison.Ordinal))
        {
            // Total pot 2750 | No rake   |   Total pot 0.12€ | Rake 0.01€
            var parts = line["Total pot ".Length..].Split(" | ");
            h.TotalPot = HhText.Amount(parts[0]);
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i] == "No rake") continue;
                if (parts[i].StartsWith("Rake ", StringComparison.Ordinal)) h.Rake = HhText.Amount(parts[i]["Rake ".Length..]);
                else throw new HandFormatError($"Unknown pot field '{parts[i]}': {line}");
            }
            return;
        }
        if (line.StartsWith("Board:", StringComparison.Ordinal))
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
            summarySeats.Add(seatNo);
            if (m.Groups[2].Success) seat.Won += HhText.Amount(m.Groups[2].Value);
            if (m.Groups[4].Success) seat.Won += HhText.Amount(m.Groups[4].Value);
            if (m.Groups[3].Success) { HhText.SetHole(seat, m.Groups[3].Value); seat.ShowedCards = true; }
            return;
        }
        throw new HandFormatError($"Unknown summary line: {line}");
    }

    /// <summary>
    /// «*** TURN *** [Ks 9d 9c][Td]» — Winamax повторяет всю доску, разбитую скобками: флоп и новая карта.
    /// </summary>
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
