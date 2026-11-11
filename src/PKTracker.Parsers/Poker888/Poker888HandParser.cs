using System.Globalization;
using System.Text.RegularExpressions;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Parsers.Common;

namespace PKTracker.Parsers.Poker888;

/// <summary>
/// Парсер hand history 888poker («#Game No : …», «***** 888poker Hand History for Game … *****»).
/// Сейчас — турниры no limit hold'em (в корпусе кеша нет, формат его шапки неизвестен: такая раздача отклоняется).
/// Ловушки формата: (1) суммы в скобках — СКОЛЬКО ДОЛОЖИЛ игрок этим действием, а не «рейз до» (даже у raises);
/// уровень «до» считаем сами по вкладам улицы; (2) олл-ин не помечается — выводим по стеку из шапки;
/// (3) SUMMARY только «collected [N]» без банка и рейка, а неуравненная ставка не возвращается строкой и в collected
/// не входит — возврат вычисляем по вкладам (BetLedger); (4) игрок бывает без ника («Seat 4:  ( 10,000 )»,
/// строки действий начинаются с пробела) — берём ник seatN, двое таких в раздаче недопустимы;
/// (5) кто раздан, в тексте не сказано — по участию в раздаче; (6) время в заголовке — часовой пояс компьютера
/// игрока, без указания пояса (см. <see cref="ClientTimeZone"/>); формат даты ДД ММ ГГГГ.
/// </summary>
public sealed partial class Poker888HandParser : IHandHistoryParser
{
    public string RoomCode => "888POKER";
    public HandBoundary Boundary { get; } = new(["#Game No : "], "** Summary **");

    /// <summary>
    /// Пояс компьютера игрока, в котором 888 пишет время (по файлам корпуса — локальное время, не UTC; зона в тексте
    /// не указана). Не задан — считаем UTC, и тогда времена сдвинуты на смещение пояса игрока.
    /// </summary>
    public TimeZoneInfo? ClientTimeZone { get; init; }

    public bool CanParse(ReadOnlySpan<char> fileHead)
    {
        var s = fileHead.TrimStart('﻿').TrimStart();
        return s.StartsWith("#Game No : ") || s.StartsWith("***** 888poker Hand History");
    }

    [GeneratedRegex(@"^#Game No : ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HandStartRx();

    [GeneratedRegex(@"^#Game No : (\d+)$")]
    private static partial Regex GameNoRx();

    [GeneratedRegex(@"^\*\*\*\*\* 888poker Hand History for Game (\d+) \*\*\*\*\*$")]
    private static partial Regex TitleRx();

    // 20/40 Blinds No Limit Holdem - *** 03 10 2026 13:09:31
    [GeneratedRegex(@"^([\d,.]+)/([\d,.]+) Blinds (.+?) - \*\*\* (\d\d) (\d\d) (\d{4}) (\d\d):(\d\d):(\d\d)$")]
    private static partial Regex BlindsRx();

    // Tournament #100000002 $30 + $3 - Table #1 8 Max (Real Money)
    [GeneratedRegex(@"^Tournament #(\d+) (.*?) - Table #(\S+) (\d+) Max \(Real Money\)$")]
    private static partial Regex TournamentRx();

    [GeneratedRegex(@"^Seat (\d+) is the button$")]
    private static partial Regex ButtonRx();

    [GeneratedRegex(@"^Total number of players : (\d+)$")]
    private static partial Regex TotalPlayersRx();

    // Seat 1: Player1 ( 9,875 ) | Seat 4:  ( 10,000 )
    [GeneratedRegex(@"^Seat (\d+): (.*) \( ([\d,.]+) \)$")]
    private static partial Regex SeatRx();

    [GeneratedRegex(@"^\*\* (Dealing down cards|First runout|Dealing flop|Dealing turn|Dealing river|Summary) \*\*(.*)$")]
    private static partial Regex SectionRx();

    [GeneratedRegex(@"^posts (ante|small blind|big blind) \[ ?([\d,.]+) ?\]$")]
    private static partial Regex PostRx();

    [GeneratedRegex(@"^(calls|bets|raises) \[ ?([\d,.]+) ?\]$")]
    private static partial Regex BetRx();

    [GeneratedRegex(@"^(shows|mucks) \[ ([^\]]*) \]$")]
    private static partial Regex ShowRx();

    [GeneratedRegex(@"^Dealt to (.*) \[ ([^\]]*) \]$")]
    private static partial Regex DealtRx();

    [GeneratedRegex(@"^collected \[ ?([\d,.]+) ?\]$")]
    private static partial Regex CollectedRx();

    private enum Section { Header, Preflop, Flop, Turn, River, Summary }

    public IEnumerable<HandParseResult> Parse(string text, HandSourceInfo source, long baseOffset = 0) =>
        HhText.ParseAll(text, source, baseOffset, HandStartRx(), raw => ParseHand(raw, source));

    public ParsedHand ParseHand(string raw, HandSourceInfo source)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var h = new ParsedHand { RoomCode = RoomCode, SourceFile = source.FilePath, RawText = raw, Scale = 2 };
        int li = 0;

        string NextLine(string what)
        {
            while (li < lines.Length && lines[li].Trim().Length == 0) li++;
            if (li >= lines.Length) throw new HandFormatError($"Truncated hand: no {what}");
            return lines[li++].TrimEnd();
        }

        var first = GameNoRx().Match(NextLine("game number").TrimStart('﻿'));
        if (!first.Success) throw new HandFormatError($"Bad game number line: {lines[0]}");
        var title = TitleRx().Match(NextLine("title"));
        if (!title.Success || title.Groups[1].Value != first.Groups[1].Value) throw new HandFormatError("Bad 888poker title line");
        h.ExternalHandId = long.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);

        var bl = NextLine("blinds line");
        var bm = BlindsRx().Match(bl);
        if (!bm.Success) throw new HandFormatError($"Bad blinds line (cash game?): {bl}");
        h.GameVariant = Variant(bm.Groups[3].Value);
        h.SmallBlind = HhText.Amount(bm.Groups[1].Value);
        h.BigBlind = HhText.Amount(bm.Groups[2].Value);
        h.PlayedAtUtc = Time(bm, ClientTimeZone);

        var tl = NextLine("tournament line");
        var tm = TournamentRx().Match(tl);
        if (!tm.Success) throw new HandFormatError($"Unsupported table line (cash game or unknown format): {tl}");
        h.TournamentId = long.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture);
        h.MaxSeats = int.Parse(tm.Groups[4].Value, CultureInfo.InvariantCulture);
        h.Currency = "CHP";
        h.GameKind = source.GameKind is GameKind.Sng ? GameKind.Sng : GameKind.Mtt;
        h.TableName = $"{tm.Groups[2].Value} #{h.TournamentId} стол {tm.Groups[3].Value}";
        h.ExternalGameId = source.ExternalGameId ?? "G" + h.TournamentId;
        h.ExternalTableId = source.ExternalTableId ?? $"T{h.TournamentId}_{tm.Groups[3].Value}";

        var btn = ButtonRx().Match(NextLine("button line"));
        if (!btn.Success) throw new HandFormatError("Bad button line");
        h.ButtonSeat = int.Parse(btn.Groups[1].Value, CultureInfo.InvariantCulture);
        var total = TotalPlayersRx().Match(NextLine("players count"));
        if (!total.Success) throw new HandFormatError("Bad total players line");

        int nameless = 0;
        ParsedSeat? anon = null;
        while (li < lines.Length && lines[li].StartsWith("Seat ", StringComparison.Ordinal))
        {
            var m = SeatRx().Match(lines[li].TrimEnd());
            if (!m.Success) throw new HandFormatError($"Bad seat line: {lines[li]}");
            int no = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var nm = m.Groups[2].Value;
            bool isAnon = nm.Length == 0;
            var seat = new ParsedSeat { Seat = no, Name = isAnon ? "seat" + no : nm, Stack = HhText.Amount(m.Groups[3].Value) };
            if (isAnon) { nameless++; anon = seat; }
            h.Seats.Add(seat);
            li++;
        }
        if (h.Seats.Count == 0) throw new HandFormatError("No seats in header");
        if (h.Seats.Count != int.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture)) throw new HandFormatError("Seat count != total number of players");
        // Строки действий безымянного начинаются с пробела; двух безымянных не различить — отклоняем.
        if (nameless > 1) throw new HandFormatError("Two nameless players in one hand");
        var names = HhText.NamesByLength(h).Where(n => !(anon != null && n == anon.Name)).ToArray();

        var section = Section.Header;
        var street = Street.Preflop;
        int seq = 0;
        bool sawDealing = false, sawSummary = false;
        var ledger = new BetLedger();
        var totalPut = new Dictionary<int, long>();   // анте + все ставки — для вывода олл-ина по стеку
        var active = new HashSet<int>();

        for (; li < lines.Length; li++)
        {
            var line = lines[li].TrimEnd();
            if (line.Trim().Length == 0) continue;

            if (line.StartsWith("** ", StringComparison.Ordinal))
            {
                var m = SectionRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Unknown section: {line}");
                switch (m.Groups[1].Value)
                {
                    case "Dealing down cards": section = Section.Preflop; street = Street.Preflop; sawDealing = true; break;
                    case "First runout": break;   // метка единственного прогона; «Second runout» (Run It Twice) в корпусе нет — упадёт как неизвестная секция
                    case "Dealing flop": FlushLedger(); section = Section.Flop; street = Street.Flop; SetBoard(h, m.Groups[2].Value, 3); break;
                    case "Dealing turn": FlushLedger(); section = Section.Turn; street = Street.Turn; SetBoard(h, m.Groups[2].Value, 4); break;
                    case "Dealing river": FlushLedger(); section = Section.River; street = Street.River; SetBoard(h, m.Groups[2].Value, 5); break;
                    case "Summary": FlushLedger(); section = Section.Summary; sawSummary = true; break;
                }
                continue;
            }

            if (section == Section.Header)
            {
                // До «Dealing down cards» — только посты анте и блайндов.
                var (p0, r0) = Resolve(line);
                if (p0 == null || !r0.StartsWith("posts ", StringComparison.Ordinal)) throw new HandFormatError($"Unknown line before deal: {line}");
                Action(p0, r0, line);
                continue;
            }

            if (line.StartsWith("Dealt to ", StringComparison.Ordinal))
            {
                var m = DealtRx().Match(line);
                if (!m.Success) throw new HandFormatError($"Bad dealt line: {line}");
                var seat = m.Groups[1].Value.Length == 0 ? anon : h.SeatByName(m.Groups[1].Value);
                if (seat == null) throw new HandFormatError($"Dealt to unknown player: {line}");
                HhText.SetHole(seat, m.Groups[2].Value);
                seat.IsHero = true;
                h.HeroName = seat.Name;
                active.Add(seat.Seat);
                continue;
            }

            if (section == Section.Summary && line.StartsWith("First runout ", StringComparison.Ordinal))
            {
                // «First runout NAME collected [ 300 ]»; у безымянного — «First runout  collected [ 300 ]»
                var tail = line["First runout ".Length..];
                var (p1, r1) = Resolve(tail);
                var cm = p1 == null ? Match.Empty : CollectedRx().Match(r1);
                if (p1 == null || !cm.Success) throw new HandFormatError($"Bad collected line: {line}");
                var amt = HhText.Amount(cm.Groups[1].Value);
                p1.Won += amt;
                h.TotalPot += amt;
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = p1.Seat, Type = ActionType.Collect, Amount = amt });
                active.Add(p1.Seat);
                continue;
            }

            var (player, rest) = Resolve(line);
            if (player == null) throw new HandFormatError($"Unknown line: {line}");
            Action(player, rest, line);
        }

        if (!sawDealing) throw new HandFormatError("No 'Dealing down cards' section");
        if (!sawSummary) throw new HandFormatError("No Summary section (incomplete hand)");

        foreach (var s in h.Seats)
            if (active.Contains(s.Seat)) s.Dealt = true;
            else s.SittingOut = true;

        HhText.FinishFlags(h);
        HhText.ApplyHeroHint(h, source);
        return h;

        // ---- локальные функции ----

        void FlushLedger() => ledger.FlushInto(h, ref seq, street);

        (ParsedSeat?, string) Resolve(string line)
        {
            if (line.StartsWith(' ') && anon != null) return (anon, line[1..]);
            return HhText.ResolvePlayer(h, names, line, " ");
        }

        long Put(ParsedSeat p) => totalPut.GetValueOrDefault(p.Seat);

        bool AllIn(ParsedSeat p) => Put(p) >= p.Stack;

        void Action(ParsedSeat player, string rest, string line)
        {
            active.Add(player.Seat);
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
                if (type == ActionType.PostBb && h.Actions.Any(a => a.Type == ActionType.PostBb)) type = ActionType.PostBbOot;
                if (type == ActionType.PostSb && h.Actions.Any(a => a.Type == ActionType.PostSb)) type = ActionType.PostDead;
                totalPut[player.Seat] = Put(player) + amt;
                if (type == ActionType.PostAnte) { if (amt > h.Ante) h.Ante = amt; }
                else ledger.Add(player.Seat, amt);
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = Street.Preflop, Seat = player.Seat, Type = type, Amount = amt, IsAllIn = AllIn(player) });
            }
            else if (rest == "folds")
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Fold });
            else if (rest == "checks")
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Check });
            else if (rest.StartsWith("calls ", StringComparison.Ordinal) || rest.StartsWith("bets ", StringComparison.Ordinal) || rest.StartsWith("raises ", StringComparison.Ordinal))
            {
                var m = BetRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad call/bet/raise: {line}");
                var added = HhText.Amount(m.Groups[2].Value);
                var kind = m.Groups[1].Value;
                // В скобках — доложенное, поэтому уровень «до» у рейза = вклад улицы + доложенное.
                long amount = kind == "raises" ? ledger.Put(player.Seat) + added : added;
                ledger.Add(player.Seat, added);
                totalPut[player.Seat] = Put(player) + added;
                h.Actions.Add(new ParsedAction
                {
                    Seq = seq++, Street = street, Seat = player.Seat,
                    Type = kind switch { "calls" => ActionType.Call, "bets" => ActionType.Bet, _ => ActionType.Raise },
                    Amount = amount, IsAllIn = AllIn(player),
                });
            }
            else if (rest.StartsWith("shows ", StringComparison.Ordinal) || rest.StartsWith("mucks ", StringComparison.Ordinal))
            {
                var m = ShowRx().Match(rest);
                if (!m.Success) throw new HandFormatError($"Bad show: {line}");
                HhText.SetHole(player, m.Groups[2].Value);
                if (m.Groups[1].Value == "shows") player.ShowedCards = true; else player.Mucked = true;
            }
            else if (rest == "did not show his hand") { }
            else throw new HandFormatError($"Unknown action: {line}");
        }
    }

    private static DateTime Time(Match m, TimeZoneInfo? zone)
    {
        // ДД ММ ГГГГ → строка в формате, который понимает HhText.ToUtc
        var s = $"{m.Groups[6].Value}/{m.Groups[5].Value}/{m.Groups[4].Value} {m.Groups[7].Value}:{m.Groups[8].Value}:{m.Groups[9].Value}";
        return HhText.ToUtc(s, zone);
    }

    private static string Variant(string s) => s switch
    {
        "No Limit Holdem" => "NLHE",
        "Pot Limit Holdem" => "PLHE",
        "Fixed Limit Holdem" => "FLHE",
        _ => throw new HandFormatError($"Unsupported game variant: {s}"),
    };

    /// <summary>«** Dealing flop ** [ 9h, Qh, Ts ]» — у каждой улицы только новые карты; доска накапливается.</summary>
    private static void SetBoard(ParsedHand h, string tail, int expected)
    {
        var m = Regex.Match(tail, @"^ \[ ([^\]]*) \]$");
        if (!m.Success) throw new HandFormatError($"Bad board line: {tail}");
        var cards = HhText.BoardCards(m.Groups[1].Value);
        if (h.Boards.Count == 0) h.Boards.Add(new List<Card>());
        var board = h.Boards[0];
        board.AddRange(cards);
        if (board.Count != expected) throw new HandFormatError($"Board has {board.Count} cards, expected {expected}");
    }
}
