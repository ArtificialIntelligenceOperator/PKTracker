using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Parsers;

/// <summary>
/// Синтетические раздачи для комнат, у которых нет корпуса (кеш Winamax/GG/iPoker), и проверки строгости:
/// неизвестная строка, не-холдем и оборванная раздача должны отклоняться, а не молча пропускаться.
/// Запуск: VerifyParsers --selftest
/// </summary>
static class SelfTest
{
    static int _fail;

    const string WmxCash =
"Winamax Poker - CashGame - HandId: #12345-7-1700000000 - Holdem no limit (0.01€/0.02€) - 2026/10/10 10:00:10 UTC\n" +
"Table: 'Paris 05' 6-max (real money) Seat #1 is the button\n" +
"Seat 1: Alice (2€)\n" +
"Seat 2: Bob (2.50€)\n" +
"Seat 3: Carol (1.20€)\n" +
"*** ANTE/BLINDS ***\n" +
"Bob posts small blind 0.01€\n" +
"Carol posts big blind 0.02€\n" +
"Dealt to Alice [Ah Kd]\n" +
"*** PRE-FLOP ***\n" +
"Alice raises 0.04€ to 0.06€\n" +
"Bob folds\n" +
"Carol calls 0.04€\n" +
"*** FLOP *** [2c 7d 9h]\n" +
"Carol checks\n" +
"Alice bets 0.10€\n" +
"Carol folds\n" +
"Alice collected 0.22€ from pot\n" +
"*** SUMMARY ***\n" +
"Total pot 0.23€ | Rake 0.01€\n" +
"Board: [2c 7d 9h]\n" +
"Seat 1: Alice (button) won 0.22€\n";

    const string GgCash =
"Poker Hand #RC1234567: Hold'em No Limit ($0.01/$0.02) - 2026/10/10 10:00:10\n" +
"Table 'RushAndCash123' 6-max Seat #1 is the button\n" +
"Seat 1: Hero ($2 in chips)\n" +
"Seat 2: a1b2c3d4 ($2.50 in chips)\n" +
"Seat 3: e5f6a7b8 ($1.20 in chips)\n" +
"a1b2c3d4: posts small blind $0.01\n" +
"e5f6a7b8: posts big blind $0.02\n" +
"*** HOLE CARDS ***\n" +
"Dealt to Hero [Ah Kd]\n" +
"Dealt to a1b2c3d4 \n" +
"Dealt to e5f6a7b8 \n" +
"Hero: raises $0.04 to $0.06\n" +
"a1b2c3d4: folds\n" +
"e5f6a7b8: calls $0.04\n" +
"*** FLOP *** [2c 7d 9h]\n" +
"e5f6a7b8: checks\n" +
"Hero: bets $0.10\n" +
"e5f6a7b8: folds\n" +
"Uncalled bet ($0.10) returned to Hero\n" +
"*** SHOWDOWN ***\n" +
"Hero collected $0.12 from pot\n" +
"*** SUMMARY ***\n" +
"Total pot $0.13 | Rake $0.01 | Jackpot $0 | Bingo $0 | Fortune $0 | Tax $0\n" +
"Board [2c 7d 9h]\n" +
"Seat 1: Hero (button) collected ($0.12)\n" +
"Seat 2: a1b2c3d4 (small blind) folded before Flop\n" +
"Seat 3: e5f6a7b8 (big blind) folded on the Flop\n";

    const string P888 =
"#Game No : 100000001\n" +
"***** 888poker Hand History for Game 100000001 *****\n" +
"20/40 Blinds No Limit Holdem - *** 03 10 2026 13:09:31\n" +
"Tournament #111 $30 + $3 - Table #1 8 Max (Real Money)\n" +
"Seat 3 is the button\n" +
"Total number of players : 3\n" +
"Seat 1: Alice ( 1,000 )\n" +
"Seat 3: Bob ( 1,000 )\n" +
"Seat 5: Carol ( 1,000 )\n" +
"Alice posts ante [5]\n" +
"Bob posts ante [5]\n" +
"Carol posts ante [5]\n" +
"Carol posts small blind [20]\n" +
"Alice posts big blind [40]\n" +
"** Dealing down cards **\n" +
"Dealt to Bob [ Ah, Kd ]\n" +
"Bob raises [100]\n" +
"Carol folds\n" +
"Alice calls [60]\n" +
"** First runout **\n" +
"** Dealing flop ** [ 2c, 7d, 9h ]\n" +
"Alice checks\n" +
"Bob bets [150]\n" +
"Alice folds\n" +
"** Summary **\n" +
"First runout Bob collected [ 235 ]\n";

    const string IpCash =
"<root><general><mode>real</mode><gametype>Holdem NL</gametype><tablename>Table 1</tablename><currency>EUR</currency><nickname>Alice</nickname><tablesize>6</tablesize></general>" +
"<game gamecode=\"1001\"><general><startdate>2026-10-03 14:41:57</startdate><smallblind>€0.01</smallblind><bigblind>€0.02</bigblind><ante></ante><players>\n" +
"<player seat=\"1\" name=\"Alice\" chips=\"€2\" dealer=\"1\" bet=\"€0.16\" win=\"€0.12\"/>\n" +
"<player seat=\"2\" name=\"Bob\" chips=\"€2.50\" dealer=\"0\" bet=\"€0.01\" win=\"€0\"/>\n" +
"<player seat=\"3\" name=\"Carol\" chips=\"€1.20\" dealer=\"0\" bet=\"€0.06\" win=\"€0\"/>\n" +
"</players></general>\n" +
"<round no=\"0\"><action no=\"1\" player=\"Bob\" sum=\"€0.01\" type=\"1\"/><action no=\"2\" player=\"Carol\" sum=\"€0.02\" type=\"2\"/></round>\n" +
"<round no=\"1\"><cards player=\"Alice\" type=\"Pocket\">SA DK</cards><cards player=\"Bob\" type=\"Pocket\">X X</cards><cards player=\"Carol\" type=\"Pocket\">X X</cards>\n" +
"<action no=\"3\" player=\"Alice\" sum=\"€0.06\" type=\"23\"/><action no=\"4\" player=\"Bob\" sum=\"0\" type=\"0\"/><action no=\"5\" player=\"Carol\" sum=\"€0.04\" type=\"3\"/></round>\n" +
"<round no=\"2\"><cards type=\"Flop\">C2 D7 H9</cards><action no=\"6\" player=\"Carol\" sum=\"0\" type=\"4\"/><action no=\"7\" player=\"Alice\" sum=\"€0.10\" type=\"5\"/><action no=\"8\" player=\"Carol\" sum=\"0\" type=\"0\"/></round>\n" +
"</game></root>";

    public static int Run()
    {
        // ---------- Winamax: кеш, рейк, неуравненная ставка внутри банка ----------
        var h = One("WINAMAX cash", WmxCash);
        if (h != null)
        {
            Check("WINAMAX cash: EUR/cash", h.GameKind == GameKind.Cash && h.Currency == "EUR");
            Check("WINAMAX cash: blinds", h.SmallBlind == 1 && h.BigBlind == 2);
            Check("WINAMAX cash: rake 1, pot 12 (net)", h.Rake == 1 && h.TotalPot == 12);
            Check("WINAMAX cash: uncalled 10 returned", h.Actions.Any(a => a.Type == ActionType.UncalledReturn && a.Amount == 10));
            Check("WINAMAX cash: raise to 6", h.Actions.Any(a => a.Type == ActionType.Raise && a.Amount == 6));
            Check("WINAMAX cash: hero", h.HeroName == "Alice" && h.SeatByName("Alice")!.CardsKnown);
        }
        Reject("WINAMAX unknown line", WmxCash.Replace("Carol folds", "Carol does a backflip"), "Unknown");
        Reject("WINAMAX omaha", WmxCash.Replace("Holdem no limit", "Omaha5 pot limit"), "Unsupported game variant");
        Reject("WINAMAX play money", WmxCash.Replace("real money", "play money"), "Play money");
        Reject("WINAMAX pot mismatch", WmxCash.Replace("Total pot 0.23€", "Total pot 0.50€"), "Total pot");
        Reject("WINAMAX truncated", WmxCash[..WmxCash.IndexOf("*** SUMMARY", StringComparison.Ordinal)], "SUMMARY");

        // ---------- GG: кеш с рейком, Uncalled, обезличенные Dealt to ----------
        h = One("GGPOKER cash", GgCash);
        if (h != null)
        {
            Check("GGPOKER cash: USD, hero", h.Currency == "USD" && h.HeroName == "Hero");
            Check("GGPOKER cash: rake 1, pot 12 (net)", h.Rake == 1 && h.TotalPot == 12);
            Check("GGPOKER cash: RC id is offset away from TM ids", h.ExternalHandId > 1_000_000_000_000L);
            Check("GGPOKER cash: all three dealt", h.Seats.All(s => s.Dealt));
        }
        Reject("GGPOKER unknown line", GgCash.Replace("e5f6a7b8: checks", "e5f6a7b8: dances"), "Unknown action");
        Reject("GGPOKER omaha", GgCash.Replace("Hold'em No Limit", "Omaha Pot Limit"), "Unsupported game variant");
        Reject("GGPOKER 4 cards", GgCash.Replace("[Ah Kd]", "[Ah Kd Qs Jc]"), "Omaha");

        // ---------- 888: рейз в скобках = доложено, неявный возврат ставки ----------
        h = One("888POKER", P888);
        if (h != null)
        {
            Check("888: raise stored as 'to' 100", h.Actions.Any(a => a.Type == ActionType.Raise && a.Amount == 10000));
            Check("888: uncalled 150 synthesized", h.Actions.Any(a => a.Type == ActionType.UncalledReturn && a.Amount == 15000));
            Check("888: pot 235, hero Bob", h.TotalPot == 23500 && h.HeroName == "Bob");
        }
        Reject("888POKER unknown line", P888.Replace("Alice checks", "Alice juggles"), "Unknown");
        Reject("888POKER run it twice", P888.Replace("** Summary **", "** Second runout **\n** Summary **"), "Unknown section");
        Reject("888POKER cash", P888.Replace("Tournament #111 $30 + $3 - Table #1 8 Max (Real Money)", "Table Cash Game 5 6 Max"), "Unsupported");
        Reject("888POKER two nameless", P888.Replace("Seat 1: Alice", "Seat 1: ").Replace("Seat 3: Bob", "Seat 3: "), "nameless");

        // ---------- iPoker: кеш, рейк = вложено − выиграно ----------
        h = One("IPOKER cash", IpCash);
        if (h != null)
        {
            Check("IPOKER cash: EUR cash", h.Currency == "EUR" && h.GameKind == GameKind.Cash);
            Check("IPOKER cash: rake 1 derived, pot 12", h.Rake == 1 && h.TotalPot == 12);
            Check("IPOKER cash: hero Alice As", h.HeroName == "Alice" && h.SeatByName("Alice")!.HoleCards[0]!.Value.ToString() == "As");
            Check("IPOKER cash: raise to 6, uncalled 10", h.Actions.Any(a => a.Type == ActionType.Raise && a.Amount == 6) && h.Actions.Any(a => a.Type == ActionType.UncalledReturn && a.Amount == 10));
        }
        Reject("IPOKER unknown action type", IpCash.Replace("type=\"4\"", "type=\"99\""), "Unknown action type");
        Reject("IPOKER omaha", IpCash.Replace("Holdem NL", "Omaha PL"), "Unsupported game variant");
        Reject("IPOKER truncated", IpCash[..(IpCash.IndexOf("</game>", StringComparison.Ordinal) - 40)], "Truncated");
        Reject("IPOKER unknown element", IpCash.Replace("<round no=\"2\">", "<round no=\"2\"><weird/>"), "Unknown element");
        Reject("IPOKER play money", IpCash.Replace("<mode>real", "<mode>play"), "play money");

        // Определение формата по началу файла
        Check("Detect winamax", ParserRegistry.Detect(WmxCash, "POKERKING")?.Room.Code == "WINAMAX");
        Check("Detect gg", ParserRegistry.Detect(GgCash, "POKERKING")?.Room.Code == "GGPOKER");
        Check("Detect 888", ParserRegistry.Detect(P888, "POKERKING")?.Room.Code == "888POKER");
        Check("Detect ipoker", ParserRegistry.Detect(IpCash, "POKERKING")?.Room.Code == "IPOKER");
        Check("Winamax tournament summary is not a hand",
            Parse("WINAMAX", "Winamax Poker - Tournament summary : X(1) - Late Registration\nPlayer : A\n").Count == 0);

        Console.WriteLine(_fail == 0 ? "SELFTEST OK" : $"SELFTEST FAILED: {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    static List<HandParseResult> Parse(string room, string text) =>
        ParserRegistry.ForRoom(room).Parse(text, new HandSourceInfo("selftest")).ToList();

    static string RoomOf(string name) => name.Split(' ')[0];

    /// <summary>Раздача должна разобраться, пройти движок и денежные инварианты.</summary>
    static ParsedHand? One(string name, string text)
    {
        var rs = Parse(RoomOf(name), text);
        if (rs.Count != 1 || !rs[0].Ok) { Check($"{name}: parses ({(rs.Count == 1 ? rs[0].Error : rs.Count + " results")})", false); return null; }
        var h = rs[0].Hand!;
        var errs = Invariants.Check(h, HandContextBuilder.Build(h, computeEv: false));
        Check($"{name}: invariants ({string.Join("; ", errs)})", errs.Count == 0);
        return h;
    }

    static void Reject(string name, string text, string expectInMessage)
    {
        var rs = Parse(RoomOf(name), text);
        bool ok = rs.Count >= 1 && rs.All(r => !r.Ok) && rs.Any(r => r.Error!.Contains(expectInMessage, StringComparison.OrdinalIgnoreCase));
        Check($"{name}: rejected with '{expectInMessage}'" + (ok ? "" : $" (got: {string.Join(" | ", rs.Select(r => r.Ok ? "OK" : r.Error))})"), ok);
    }

    static void Check(string what, bool ok)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _fail++;
    }
}
