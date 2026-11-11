using System.Globalization;
using System.Text.RegularExpressions;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;

namespace PKTracker.Parsers.Wpn;

public sealed class HandFormatException(string message) : Exception(message);

/// <summary>
/// Парсер hand history сети Winning Poker Network (PokerKing, ACR, BCP). §0.3, §0.6, §15.1.
/// Строгий: любая неизвестная строка отклоняет раздачу целиком.
/// </summary>
public sealed partial class WpnHandParser : IHandHistoryParser
{
    public const int AmountScale = 2;

    public string RoomCode { get; }
    public HandBoundary Boundary { get; } = new(["Hand #", "Game Hand #"], "*** SUMMARY ***");

    public WpnHandParser(string roomCode = "POKERKING") => RoomCode = roomCode;

    public bool CanParse(ReadOnlySpan<char> fileHead)
    {
        var s = fileHead.TrimStart('\uFEFF').TrimStart();
        return (s.StartsWith("Hand #") || s.StartsWith("Game Hand #")) && s.IndexOf(" UTC") > 0;
    }

    // ── Регулярки ───────────────────────────────────────────────────────────────
    [GeneratedRegex(@"^(?:Game )?Hand #\d+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HandStartRx();

    [GeneratedRegex(@"^Hand #(\d+) - (.+?) - (\$?)([\d.,]+)/\$?([\d.,]+) - (\d{4}/\d\d/\d\d \d\d:\d\d:\d\d) UTC$")]
    private static partial Regex CashHeaderRx();

    [GeneratedRegex(@"^Game Hand #(\d+) - Tournament #(\d+) - (.+?) - Level (\d+) \((\$?)([\d.,]+)/\$?([\d.,]+)\) - (\d{4}/\d\d/\d\d \d\d:\d\d:\d\d) UTC$")]
    private static partial Regex TourneyHeaderRx();

    [GeneratedRegex(@"^(.*) (\d+)-max Seat #(\d+) is the button$")]
    private static partial Regex TableRx();

    [GeneratedRegex(@"^Seat (\d+): (.+) \((\$?[\d.,]+)\)( is sitting out)?$")]
    private static partial Regex SeatStackRx();

    [GeneratedRegex(@"^Seat (\d+): (.+?) (will be allowed to play after the button|is sitting out)$")]
    private static partial Regex SeatNoStackRx();

    [GeneratedRegex(@"^\*\*\* (HOLE CARDS|FLOP|TURN|RIVER|SHOW DOWN|SUMMARY|RUN IT TWICE)(?: (\d+))? \*\*\*(.*)$")]
    private static partial Regex SectionRx();

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketRx();

    [GeneratedRegex(@"^Main pot(?: \d+)? \$?[\d.,]+(?: \| [A-Za-z ]+ \$?[\d.,]+)*$")]
    private static partial Regex MainPotRx();

    [GeneratedRegex(@"^Side pot\(\d+\) \$?[\d.,]+(?: \| [A-Za-z ]+ \$?[\d.,]+)*$")]
    private static partial Regex SidePotRx();

    [GeneratedRegex(@"^Uncalled bet \((\$?[\d.,]+)\) returned to (.+)$")]
    private static partial Regex UncalledRx();

    [GeneratedRegex(@"^Dealt to (.+) \[([^\]]*)\]$")]
    private static partial Regex DealtRx();

    [GeneratedRegex(@"^Board(?: (\d+))? \[([^\]]*)\]$")]
    private static partial Regex BoardRx();

    // Действия — якорятся к "rest" после ника.
    [GeneratedRegex(@"^posts (the small blind |the big blind |ante |dead )?(\$?[\d.,]+)( and is all-in)?$")]
    private static partial Regex PostRx();

    [GeneratedRegex(@"^(calls|bets) (\$?[\d.,]+)( and is all-in)?$")]
    private static partial Regex CallBetRx();

    [GeneratedRegex(@"^raises \$?[\d.,]+ to (\$?[\d.,]+)( and is all-in)?$")]
    private static partial Regex RaiseRx();

    [GeneratedRegex(@"^shows \[([^\]]*)\](?: \(.*\))?$")]
    private static partial Regex ShowsRx();

    [GeneratedRegex(@"^mucks(?: \[([^\]]*)\])?$")]
    private static partial Regex MucksRx();

    [GeneratedRegex(@"^collected (\$?[\d.,]+) from (main|side) pot(?:-(\d+))?(?: (\d+))?$")]
    private static partial Regex CollectedRx();

    // SUMMARY: "(role) " необязателен; результат — одна из форм ниже.
    [GeneratedRegex(@"^(?:\((?:button|small blind|big blind)\) )?(?:(folded on the (?:Pre-Flop|Flop|Turn|River)(?: and did not bet)?)|did not show and won (\$?[\d.,]+)|showed \[([^\]]*)\] and (?:won (\$?[\d.,]+)|lost|folded on the (?:Pre-Flop|Flop|Turn|River)(?: and did not bet)?)(?: with .*)?|mucked(?: \[([^\]]*)\])?(?: .*)?)$")]
    private static partial Regex SummarySeatRx();

    private static readonly string[] Verbs =
    {
        "folds", "checks", "calls ", "bets ", "raises ", "posts ", "shows ", "mucks", "collected ",
        "does not show", "sits out", "waits for big blind",
    };

    // ── Разбиение файла ─────────────────────────────────────────────────────────

    /// <summary>Делит текст на блоки раздач по маркеру начала строки (§15.1). Возвращает (offset, length).</summary>
    public static List<(int Start, int Length)> Split(string text)
    {
        var res = new List<(int, int)>();
        var ms = HandStartRx().Matches(text);
        for (int i = 0; i < ms.Count; i++)
        {
            int start = ms[i].Index;
            int end = i + 1 < ms.Count ? ms[i + 1].Index : text.Length;
            res.Add((start, end - start));
        }
        return res;
    }

    public IEnumerable<HandParseResult> Parse(string text, HandSourceInfo source, long baseOffset = 0)
    {
        foreach (var (start, len) in Split(text))
        {
            var raw = text.Substring(start, len).TrimEnd('\r', '\n', ' ');
            HandParseResult r;
            try
            {
                var h = ParseHand(raw, source);
                h.FileOffset = baseOffset + start;
                r = new HandParseResult { Hand = h, RawText = raw, Offset = baseOffset + start, SourceFile = source.FilePath };
            }
            catch (Exception ex) when (ex is HandFormatException or FormatException or OverflowException)
            {
                r = new HandParseResult { Error = ex.Message, RawText = raw, Offset = baseOffset + start, SourceFile = source.FilePath };
            }
            yield return r;
        }
    }

    // ── Разбор одной раздачи ────────────────────────────────────────────────────

    private enum Section { Header, Preflop, Flop, Turn, River, Showdown, Summary }

    public ParsedHand ParseHand(string raw, HandSourceInfo source)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var h = new ParsedHand { RoomCode = RoomCode, SourceFile = source.FilePath, RawText = raw, Scale = AmountScale };
        int li = 0;

        ParseHeader(h, lines[li++], source);
        if (li >= lines.Length) throw new HandFormatException("Truncated hand: no table line");
        ParseTableLine(h, lines[li++], source);

        // Seat-строки шапки
        while (li < lines.Length && lines[li].StartsWith("Seat ", StringComparison.Ordinal))
        {
            if (!TryParseHeaderSeat(h, lines[li])) break;
            li++;
        }
        if (h.Seats.Count == 0) throw new HandFormatException("No seats in header");

        // Ники по убыванию длины — для разрешения строк с ником (ловушка 1, §15.1).
        var names = h.Seats.Select(s => s.Name).Distinct().OrderByDescending(n => n.Length).ToArray();

        var section = Section.Header;
        var street = Street.Preflop;
        int currentBoardNo = 0;   // при RIT — номер прогона текущей секции
        int seq = 0;
        bool sawSummary = false, sawHoleCards = false, rit = false;

        for (; li < lines.Length; li++)
        {
            var line = lines[li];
            if (line.Length == 0) continue;

            // 1. Маркер секции
            if (line.StartsWith("*** ", StringComparison.Ordinal))
            {
                var m = SectionRx().Match(line);
                if (!m.Success) throw new HandFormatException($"Unknown section: {line}");
                var name = m.Groups[1].Value;
                int n = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                switch (name)
                {
                    case "HOLE CARDS": section = Section.Preflop; street = Street.Preflop; sawHoleCards = true; break;
                    case "FLOP": section = Section.Flop; street = Street.Flop; SetBoard(h, n, m.Groups[3].Value, 3); currentBoardNo = n; break;
                    case "TURN": section = Section.Turn; street = Street.Turn; SetBoard(h, n, m.Groups[3].Value, 4); currentBoardNo = n; break;
                    case "RIVER": section = Section.River; street = Street.River; SetBoard(h, n, m.Groups[3].Value, 5); currentBoardNo = n; break;
                    case "SHOW DOWN": section = Section.Showdown; currentBoardNo = n; break;
                    case "RUN IT TWICE": rit = true; h.Flags |= HandFlags.RunItTwice; break;
                    case "SUMMARY": section = Section.Summary; sawSummary = true; break;
                }
                continue;
            }

            // 2. SUMMARY
            if (section == Section.Summary)
            {
                ParseSummaryLine(h, line, names);
                continue;
            }

            // 3. Фиксированные префиксы
            if (line.StartsWith("Main pot", StringComparison.Ordinal))
            {
                if (!MainPotRx().IsMatch(line)) throw new HandFormatException($"Bad main pot line: {line}");
                continue;
            }
            if (line.StartsWith("Side pot", StringComparison.Ordinal))
            {
                if (!SidePotRx().IsMatch(line)) throw new HandFormatException($"Bad side pot line: {line}");
                continue;
            }
            if (line.StartsWith("Dealt to ", StringComparison.Ordinal))
            {
                var m = DealtRx().Match(line);
                if (!m.Success) throw new HandFormatException($"Bad dealt line: {line}");
                var seat = h.SeatByName(m.Groups[1].Value) ?? throw new HandFormatException($"Dealt to unknown player: {line}");
                SetCards(seat, m.Groups[2].Value);
                seat.IsHero = true;
                h.HeroName = seat.Name;
                continue;
            }
            if (line.StartsWith("Uncalled bet ", StringComparison.Ordinal))
            {
                var m = UncalledRx().Match(line);
                if (!m.Success) throw new HandFormatException($"Bad uncalled line: {line}");
                var seat = h.SeatByName(m.Groups[2].Value) ?? throw new HandFormatException($"Uncalled to unknown player: {line}");
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = ActionType.UncalledReturn, Amount = Amount(m.Groups[1].Value) });
                continue;
            }

            // 4. Строка с ником
            var (player, rest) = ResolvePlayer(h, names, line);
            if (player == null) throw new HandFormatException($"Unknown line: {line}");

            if (rest.StartsWith("posts ", StringComparison.Ordinal))
            {
                var m = PostRx().Match(rest);
                if (!m.Success) throw new HandFormatException($"Bad post: {line}");
                var type = m.Groups[1].Value switch
                {
                    "the small blind " => ActionType.PostSb,
                    "the big blind " => ActionType.PostBb,
                    "ante " => ActionType.PostAnte,
                    "dead " => ActionType.PostDead,
                    _ => ActionType.PostBbOot,
                };
                var amt = Amount(m.Groups[2].Value);
                if (type == ActionType.PostAnte && amt > h.Ante) h.Ante = amt;
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = Street.Preflop, Seat = player.Seat, Type = type, Amount = amt, IsAllIn = m.Groups[3].Success });
            }
            else if (rest == "folds")
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Fold });
            else if (rest == "checks")
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Check });
            else if (rest.StartsWith("calls ", StringComparison.Ordinal) || rest.StartsWith("bets ", StringComparison.Ordinal))
            {
                var m = CallBetRx().Match(rest);
                if (!m.Success) throw new HandFormatException($"Bad call/bet: {line}");
                if (rit) throw new HandFormatException($"Betting action after RUN IT TWICE: {line}");
                h.Actions.Add(new ParsedAction
                {
                    Seq = seq++, Street = street, Seat = player.Seat,
                    Type = m.Groups[1].Value == "calls" ? ActionType.Call : ActionType.Bet,
                    Amount = Amount(m.Groups[2].Value), IsAllIn = m.Groups[3].Success,
                });
            }
            else if (rest.StartsWith("raises ", StringComparison.Ordinal))
            {
                var m = RaiseRx().Match(rest);
                if (!m.Success) throw new HandFormatException($"Bad raise: {line}");
                if (rit) throw new HandFormatException($"Betting action after RUN IT TWICE: {line}");
                // Ловушка 7: X не используем; Amount = Y (уровень "to").
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Raise, Amount = Amount(m.Groups[1].Value), IsAllIn = m.Groups[2].Success });
            }
            else if (rest.StartsWith("shows ", StringComparison.Ordinal))
            {
                var m = ShowsRx().Match(rest);
                if (!m.Success) throw new HandFormatException($"Bad shows: {line}");
                SetCards(player, m.Groups[1].Value);
                player.ShowedCards = true;
            }
            else if (rest.StartsWith("mucks", StringComparison.Ordinal))
            {
                var m = MucksRx().Match(rest);
                if (!m.Success) throw new HandFormatException($"Bad mucks: {line}");
                if (m.Groups[1].Success) SetCards(player, m.Groups[1].Value);
                player.Mucked = true;
            }
            else if (rest.StartsWith("collected ", StringComparison.Ordinal))
            {
                var m = CollectedRx().Match(rest);
                if (!m.Success) throw new HandFormatException($"Bad collected: {line}");
                int potNo = m.Groups[2].Value == "side" ? (m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1) : 0;
                int boardNo = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : currentBoardNo;
                h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = player.Seat, Type = ActionType.Collect, Amount = Amount(m.Groups[1].Value), PotNo = potNo, BoardNo = boardNo });
            }
            else if (rest == "does not show") { }
            else if (rest == "sits out") player.SittingOut = true;
            else if (rest == "waits for big blind") player.SittingOut = true;
            else throw new HandFormatException($"Unknown action: {line}");
        }

        if (!sawHoleCards) throw new HandFormatException("No HOLE CARDS section");
        if (!sawSummary) throw new HandFormatException("No SUMMARY section (incomplete hand)");

        // Мёртвая кнопка / отсутствие SB / хедз-ап
        var btn = h.SeatByNumber(h.ButtonSeat);
        if (btn == null || !btn.Dealt) h.Flags |= HandFlags.DeadButton;
        if (!h.Actions.Any(a => a.Type == ActionType.PostSb)) h.Flags |= HandFlags.NoSmallBlind;
        if (h.Seats.Count(s => s.Dealt) == 2) h.Flags |= HandFlags.HeadsUp;

        if (h.HeroName == null && source.HeroHint != null && h.SeatByName(source.HeroHint) is { } hs)
        {
            hs.IsHero = true;
            h.HeroName = hs.Name;
        }
        return h;
    }

    private static void ParseHeader(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = CashHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            h.GameVariant = Variant(m.Groups[2].Value);
            h.Currency = m.Groups[3].Value == "$" ? "USD" : "CHP";
            h.SmallBlind = Amount(m.Groups[4].Value);
            h.BigBlind = Amount(m.Groups[5].Value);
            h.PlayedAtUtc = Time(m.Groups[6].Value);
            h.GameKind = src.GameKind ?? GameKind.Cash;
            return;
        }
        m = TourneyHeaderRx().Match(line);
        if (m.Success)
        {
            h.ExternalHandId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            h.TournamentId = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            h.GameVariant = Variant(m.Groups[3].Value);
            h.TournamentLevel = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            h.Currency = m.Groups[5].Value == "$" ? "USD" : "CHP";
            h.SmallBlind = Amount(m.Groups[6].Value);
            h.BigBlind = Amount(m.Groups[7].Value);
            h.PlayedAtUtc = Time(m.Groups[8].Value);
            h.GameKind = src.GameKind is GameKind.Sng ? GameKind.Sng : GameKind.Mtt;
            return;
        }
        throw new HandFormatException($"Bad hand header: {line}");
    }

    private static string Variant(string s) => s switch
    {
        "Holdem (No Limit)" => "NLHE",
        "Holdem (Pot Limit)" => "PLHE",
        "Holdem (Fixed Limit)" => "FLHE",
        _ => throw new HandFormatException($"Unsupported game variant: {s}"),
    };

    private static void ParseTableLine(ParsedHand h, string line, HandSourceInfo src)
    {
        var m = TableRx().Match(line);
        if (!m.Success) throw new HandFormatException($"Bad table line: {line}");
        var tname = m.Groups[1].Value;
        h.MaxSeats = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        h.ButtonSeat = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        string? quoted = null;
        if (tname.StartsWith("Table '", StringComparison.Ordinal) && tname.EndsWith('\''))
            quoted = tname[7..^1];
        h.TableName = quoted != null ? (src.TableName != null ? $"{src.TableName} #{quoted}" : $"Table {quoted}") : tname;
        h.ExternalGameId = src.ExternalGameId ?? (h.TournamentId is { } t ? "G" + t : "N" + tname);
        h.ExternalTableId = src.ExternalTableId ?? (quoted != null ? "T" + quoted : tname);
    }

    private static bool TryParseHeaderSeat(ParsedHand h, string line)
    {
        var m = SeatStackRx().Match(line);
        if (m.Success)
        {
            h.Seats.Add(new ParsedSeat
            {
                Seat = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Name = m.Groups[2].Value,
                Stack = Amount(m.Groups[3].Value),
                SittingOut = m.Groups[4].Success,
            });
            return true;
        }
        m = SeatNoStackRx().Match(line);
        if (m.Success)
        {
            h.Seats.Add(new ParsedSeat
            {
                Seat = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Name = m.Groups[2].Value,
                StackUnknown = true,
                SittingOut = true,
            });
            return true;
        }
        throw new HandFormatException($"Bad seat line: {line}");
    }

    private static void ParseSummaryLine(ParsedHand h, string line, string[] names)
    {
        if (line.StartsWith("Total pot ", StringComparison.Ordinal))
        {
            var parts = line.Split(" | ");
            h.TotalPot = Amount(parts[0]["Total pot ".Length..]);
            for (int i = 1; i < parts.Length; i++)
            {
                int sp = parts[i].LastIndexOf(' ');
                if (sp <= 0) throw new HandFormatException($"Bad total pot line: {line}");
                var key = parts[i][..sp];
                var val = Amount(parts[i][(sp + 1)..]);
                if (key == "Rake") h.Rake = val;
                else h.ExtraFees[key] = h.ExtraFees.GetValueOrDefault(key) + val;
            }
            return;
        }
        if (line == "BombPot") { h.Flags |= HandFlags.BombPot; return; }
        if (line.StartsWith("Board", StringComparison.Ordinal))
        {
            var m = BoardRx().Match(line);
            if (!m.Success) throw new HandFormatException($"Bad board line: {line}");
            int n = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
            var cards = ParseCards(m.Groups[2].Value);
            while (h.Boards.Count < n) h.Boards.Add(new List<Card>());
            var b = h.Boards[n - 1];
            if (b.Count == 0) b.AddRange(cards.Where(c => c.HasValue).Select(c => c!.Value));
            else if (b.Count != cards.Length) throw new HandFormatException($"Board mismatch: {line}");
            return;
        }
        if (line.StartsWith("Seat ", StringComparison.Ordinal))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0 || !int.TryParse(line.AsSpan(5, colon - 5), NumberStyles.None, CultureInfo.InvariantCulture, out int seatNo))
                throw new HandFormatException($"Bad summary seat: {line}");
            var seat = h.SeatByNumber(seatNo) ?? throw new HandFormatException($"Summary for unknown seat: {line}");
            var body = line[(colon + 2)..];
            if (!body.StartsWith(seat.Name + " ", StringComparison.Ordinal))
                throw new HandFormatException($"Summary seat name mismatch: {line}");
            var rest = body[(seat.Name.Length + 1)..];
            var m = SummarySeatRx().Match(rest);
            if (!m.Success) throw new HandFormatException($"Unknown summary result: {line}");
            seat.Dealt = true;
            // Выигрыш — только из SUMMARY: строк "collected" в руках без шоудауна нет. При RIT суммируется по доскам.
            if (m.Groups[2].Success) seat.Won += Amount(m.Groups[2].Value);
            if (m.Groups[4].Success) seat.Won += Amount(m.Groups[4].Value);
            if (m.Groups[3].Success) { SetCards(seat, m.Groups[3].Value); seat.ShowedCards = true; }
            if (m.Groups[5].Success) { SetCards(seat, m.Groups[5].Value); seat.Mucked = true; }
            return;
        }
        throw new HandFormatException($"Unknown summary line: {line}");
    }

    private static (ParsedSeat? Seat, string Tail) ResolvePlayer(ParsedHand h, string[] names, string line)
    {
        foreach (var n in names)
        {
            if (line.Length > n.Length && line[n.Length] == ' ' && line.StartsWith(n, StringComparison.Ordinal))
            {
                var rest = line[(n.Length + 1)..];
                foreach (var v in Verbs)
                    if (rest.StartsWith(v, StringComparison.Ordinal))
                        return (h.SeatByName(n), rest);
            }
        }
        return (null, line);
    }

    private static void SetBoard(ParsedHand h, int boardNo, string tail, int expected)
    {
        var cards = new List<Card>();
        foreach (Match b in BracketRx().Matches(tail))
            foreach (var c in ParseCards(b.Groups[1].Value))
                cards.Add(c ?? throw new HandFormatException("Unknown board card"));
        if (cards.Count != expected) throw new HandFormatException($"Board has {cards.Count} cards, expected {expected}");
        int idx = Math.Max(boardNo, 1) - 1;
        while (h.Boards.Count <= idx) h.Boards.Add(new List<Card>());
        h.Boards[idx].Clear();
        h.Boards[idx].AddRange(cards);
    }

    private static Card?[] ParseCards(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var res = new Card?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "-") continue; // частичный показ "[8h -]"
            if (!Card.TryParse(parts[i], out var c)) throw new HandFormatException($"Bad card '{parts[i]}'");
            res[i] = c;
        }
        return res;
    }

    private static void SetCards(ParsedSeat seat, string s)
    {
        var cards = ParseCards(s);
        if (cards.Length != 2) throw new HandFormatException($"Expected 2 hole cards: [{s}]");
        for (int i = 0; i < 2; i++)
            if (cards[i].HasValue) seat.HoleCards[i] = cards[i];
    }

    /// <summary>Сумма → long в единицах 10^-2. Только decimal и InvariantCulture (§15.1).</summary>
    public static long Amount(string s)
    {
        var span = s.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '$') span = span[1..];
        var d = decimal.Parse(span, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var scaled = d * 100m;
        if (scaled != decimal.Truncate(scaled)) throw new HandFormatException($"Amount has more than 2 decimals: {s}");
        return (long)scaled;
    }

    private static DateTime Time(string s) =>
        DateTime.ParseExact(s, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
