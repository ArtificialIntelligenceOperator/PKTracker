using System.Text;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Data.Import;
using PKTracker.Engine;
using PKTracker.Parsers;
using PKTracker.Parsers.Common;
using PKTracker.Parsers.IPoker;
using PKTracker.Parsers.Poker888;
using PKTracker.Parsers.PokerStars;

namespace PKTracker.Tests;

/// <summary>Класс готовой руки (ContextVersion 5), исход раздачи в точке решения и импорт дописываемых файлов.</summary>
public class MadeHandAndImportTests
{
    private static short Classify(string hole, string board)
    {
        var h = hole.Split(' ').Select(Card.Parse).ToArray();
        var b = board.Split(' ').Select(Card.Parse).ToArray();
        return MadeHands.Classify(h[0], h[1], b);
    }

    private static MadeHandClass Made(string hole, string board) => MadeHands.Class(Classify(hole, board));

    [Theory]
    [InlineData("Ah Kd", "7c 7d 2s", MadeHandClass.Overcards)]      // пара на борде рукой не считается
    [InlineData("Ah 3d", "7c 7d 2s", MadeHandClass.NoPair)]
    [InlineData("Qs Qc", "8h Td 3d", MadeHandClass.Overpair)]
    [InlineData("9s 9c", "8h Td 3d", MadeHandClass.Underpair)]
    [InlineData("Ts 9c", "8h Td 3d", MadeHandClass.TopPair)]
    [InlineData("8s 9c", "8h Td 3d", MadeHandClass.MiddlePair)]
    [InlineData("3s 9c", "8h Td 3d", MadeHandClass.BottomPair)]
    [InlineData("Ts 8c", "8h Td 3d", MadeHandClass.TwoPair)]
    [InlineData("Ts 9c", "8h Td 8d", MadeHandClass.TopPair)]        // «две пары» со спаренным бордом — по сути пара
    [InlineData("3s 3c", "8h Td 3d", MadeHandClass.Set)]
    [InlineData("8s 9c", "8h Td 8d", MadeHandClass.Trips)]
    [InlineData("Ah Kd", "8h 8d 8s", MadeHandClass.Overcards)]      // трипс на борде есть у всех
    [InlineData("9s Jc", "8h Td 7d", MadeHandClass.Straight)]
    [InlineData("Ad 5d", "8d Td 3d", MadeHandClass.Flush)]
    [InlineData("8s 8c", "8h Td Ts", MadeHandClass.FullHouse)]
    [InlineData("2s 3c", "Ah Kh Qh Jh Th", MadeHandClass.NoPair)]   // играет борд
    public void MadeHandIsClassifiedRelativeToBoard(string hole, string board, MadeHandClass expected) =>
        Assert.Equal(expected, Made(hole, board));

    [Fact]
    public void DrawsAreFlaggedOnlyWhenHoleCardsMakeThem()
    {
        Assert.True((Classify("Ah 5h", "8h Td 3h") & MadeHands.FlushDrawBit) != 0);
        Assert.True((Classify("9s 6c", "8h 7d 2s") & MadeHands.OesdBit) != 0);       // 6-7-8-9: ауты 5 и T
        Assert.True((Classify("Js 9c", "8h 7d 2s") & MadeHands.GutshotBit) != 0);    // нужна только десятка
        // Дро «на борде» есть у всех — рукой игрока не считается.
        Assert.Equal(0, Classify("Ac Kd", "8h 7h 6h 5h") & MadeHands.FlushDrawBit);
        Assert.Equal(0, Classify("Ac Kd", "9s 8h 7d 6c") & (MadeHands.OesdBit | MadeHands.GutshotBit));
        // На ривере дро не бывает.
        Assert.Equal(0, Classify("Ah 5h", "8h Td 3h 2c Ks") & MadeHands.FlushDrawBit);
    }

    [Theory]
    [InlineData("Kh Qd", "7c 5d 2s", "KQ, KJ, QJ")]
    [InlineData("9h 4d", "7c 5d 2s", "Trash")]
    [InlineData("Ah Kd", "7c 7d 2s", "AK, AQ, AJ")]
    [InlineData("Ah 3d", "7c 7d 2s", "A-high")]
    [InlineData("Js 9c", "8h 7d 2s", "Gutshot")]
    [InlineData("Ah 5h", "8h Td 3h", "Draw")]
    [InlineData("8s 9h", "8h Td 3h", "Middle Pair")]
    [InlineData("8h 9h", "8c Td 3h", "Middle Pair")]
    [InlineData("8h 9h", "8c Th 3h", "Draw+Pair")]
    [InlineData("9s 9c", "8h Td 3d", "Low Pockets")]
    [InlineData("3s 9c", "8h Td 3d", "Low Pair")]
    [InlineData("Ts 9c", "8h Td 3d", "TPWK")]
    [InlineData("Ts Kc", "8h Td 3d", "TPGK")]
    [InlineData("Qs Qc", "8h Td 3d", "Overpair")]
    [InlineData("3s 3c", "8h Td 3d", "Set")]
    [InlineData("Ts 8c", "8h Td 3d", "2pair+")]
    [InlineData("Ad 5d", "8d Td 3d", "2pair+")]
    public void StrengthDiagramCategoryFollowsHandAndKicker(string hole, string board, string expected)
    {
        var h = hole.Split(' ').Select(Card.Parse).ToArray();
        int cat = MadeHands.StrengthCategory(Classify(hole, board), h[0], h[1]);
        Assert.Equal(expected, MadeHands.StrengthCategories[cat]);
    }

    [Fact]
    public void HandLineShowsActionsWithSizesInBigBlinds()
    {
        // bb = 100 единиц. Опен до 3bb, колл 2bb; флоп: чек, ставка 3.4bb, фолд.
        var actions = new List<(Street, int, ActionType, long, long, long)>
        {
            (Street.Preflop, 2, ActionType.PostBb, 100, 100, 0),
            (Street.Preflop, 3, ActionType.Fold, 0, 0, 150),          // префлоп-фолды в линию не попадают
            (Street.Preflop, 1, ActionType.Raise, 300, 300, 150),
            (Street.Preflop, 2, ActionType.Call, 200, 300, 450),
            (Street.Flop, 2, ActionType.Check, 0, 0, 650),
            (Street.Flop, 1, ActionType.Bet, 340, 340, 650),
            (Street.Flop, 2, ActionType.Fold, 0, 0, 990),
        };
        var board = "8h Td 3d".Split(' ').Select(Card.Parse).ToList();
        var line = HandLineLoader.Build(7, DateTime.UtcNow, 100, 50, GameKind.Mtt, focusSeat: 1, Card.Parse("Qs"), Card.Parse("Qc"), 310, board, actions);
        Assert.Equal(2, line.Streets.Count);                                   // тёрна не было — его в линии нет
        Assert.Equal(new[] { "R3", "C2" }, line.Streets[0].Actions.Select(a => a.Text));
        Assert.Equal(new[] { "X", "B3.4", "F" }, line.Streets[1].Actions.Select(a => a.Text));
        Assert.True(line.Streets[0].Actions[0].Focus);                         // действия игрока в фокусе помечены
        Assert.False(line.Streets[0].Actions[1].Focus);
        Assert.Equal(line.Streets[0].Actions[0].Actor, line.Streets[1].Actions[1].Actor);   // один игрок — один цвет на всю раздачу
        Assert.Equal(6.5, line.Streets[1].PotBb);
        Assert.Equal(3, line.Streets[1].Cards.Count);
        Assert.Equal(3.1, line.NetBb);
    }

    [Fact]
    public void StrengthReportGroupsDrawsSeparatelyFromAir()
    {
        Assert.Equal("Флеш-дро", AnalyticsRepository.StrengthGroup(Classify("Ah 5h", "8h Td 3h")).Label);
        Assert.Equal("Комбо-дро", AnalyticsRepository.StrengthGroup(Classify("9h 6h", "8h 7d 2h")).Label);
        Assert.Equal("Топ-пара + дро", AnalyticsRepository.StrengthGroup(Classify("Th 9h", "8h Td 3h")).Label);
        Assert.Equal("Оверкарты", AnalyticsRepository.StrengthGroup(Classify("Ah Kd", "7c 7d 2s")).Label);
    }

    // Олл-ин на тёрне с коротким коллом: конвертер пишет и возврат 25, и выигрыш вместе с ними (30175 при банке 30150).
    private const string UncalledInsideWinnings = """
        PokerStars Hand #200000000002: Tournament #3000000002, $9.00+$1.00 USD Hold'em No Limit - Level I (100/200) - 2024/06/26 13:53:02 ET
        Table '3000000002 1' 6-max Seat #1 is the button
        Seat 1: villain (15000 in chips)
        Seat 4: sbguy (20000 in chips)
        Seat 5: hero (15025 in chips)
        Seat 6: utg (20000 in chips)
        villain: posts the ante 25
        sbguy: posts the ante 25
        hero: posts the ante 25
        utg: posts the ante 25
        sbguy: posts small blind 100
        hero: posts big blind 200
        *** HOLE CARDS ***
        Dealt to hero [Ks Kc]
        utg: folds
        villain: raises 250 to 450
        sbguy: folds
        hero: raises 1800 to 2250
        villain: calls 1800
        *** FLOP *** [7h 9d 2d]
        hero: bets 3000
        villain: calls 3000
        *** TURN *** [7h 9d 2d] [7s]
        hero: bets 9750 and is all-in
        villain: calls 9725 and is all-in
        Uncalled bet (25) returned to hero
        *** RIVER *** [7h 9d 2d 7s] [4c]
        *** SHOW DOWN ***
        villain: shows [9h Ac] (two pair, Nines and Sevens)
        hero: shows [Ks Kc] (two pair, Kings and Sevens)
        hero collected 30175 from pot
        *** SUMMARY ***
        Total pot 30150 | Rake 0
        Board [7h 9d 2d 7s 4c]
        Seat 1: villain (button) showed [9h Ac] and lost with two pair, Nines and Sevens
        Seat 4: sbguy (small blind) folded before Flop
        Seat 5: hero (big blind) showed [Ks Kc] and won (30175) with two pair, Kings and Sevens
        Seat 6: utg folded before Flop (didn't bet)
        """;

    private static HandContext StarsContext(string text, out ParsedHand hand)
    {
        var r = new StarsHandParser().Parse(text, new HandSourceInfo("t.txt")).Single();
        Assert.True(r.Ok, r.Error);
        hand = r.Hand!;
        return HandContextBuilder.Build(hand);
    }

    [Fact]
    public void UncalledBetPrintedInsideWinningsDoesNotBreakMoney()
    {
        var ctx = StarsContext(UncalledInsideWinnings, out var h);
        Assert.Empty(Invariants.Check(h, ctx));
        Assert.Equal(3_015_000, h.SeatByName("hero")!.Won);        // 30 150 фишек ×100: возврат 25 в выигрыш не входит
    }

    [Fact]
    public void DecisionSeesHandOutcomeAndMadeHand()
    {
        var ctx = StarsContext(UncalledInsideWinnings, out _);
        var flop = ctx.Decisions.Single(d => d.PlayerName == "hero" && d.Street == Street.Flop);
        Assert.True(flop.HandWon);
        Assert.True(flop.WentToShowdown);
        Assert.Equal(MadeHandClass.Overpair, flop.Made);
        Assert.Equal(2, flop.FlopPlayers);
        var villainFlop = ctx.Decisions.Single(d => d.PlayerName == "villain" && d.Street == Street.Flop);
        Assert.False(villainFlop.HandWon);
        Assert.Equal(MadeHandClass.TopPair, villainFlop.Made);
        // Префлоп: готовой руки нет, но исход раздачи виден и оттуда.
        var pre = ctx.Decisions.First(d => d.PlayerName == "hero" && d.Street == Street.Preflop);
        Assert.False(pre.MadeKnown);
        Assert.True(pre.HandWon);
    }

    [Fact]
    public void UnknownTimeZoneRejectsHandInsteadOfSilentUtc()
    {
        Assert.Equal(new DateTime(2026, 1, 5, 17, 0, 0, DateTimeKind.Utc), HhText.ToUtc("2026/01/05 12:00:00", "ET"));
        Assert.Throws<HandFormatError>(() => HhText.ToUtc("2026/01/05 12:00:00", "MSK"));
    }

    [Fact]
    public void FirstRingSeatIsUtgEvenWithoutSmallBlind()
    {
        // 7 сданных без SB: ринг из шести мест — UTG, UTG1, LJ, HJ, CO, BTN. Второе место — «MP», а не второй «UTG».
        Assert.Equal("UTG", HandPositions.H2n(Position.UTG, 7));
        Assert.Equal("MP", HandPositions.H2n(Position.UTG1, 7));
        Assert.Equal("MP", HandPositions.H2n(Position.UTG, 6));
    }

    [Fact]
    public void CompleteLengthUsesBoundaryOfEachFormat()
    {
        // PokerStars: пока файл пишется, последняя раздача не берётся; затих — берётся целиком.
        var stars = UncalledInsideWinnings.Replace("\r\n", "\n") + "\n\n" + UncalledInsideWinnings.Replace("\r\n", "\n").Replace("200000000002", "200000000003") + "\n";
        var sb = Encoding.UTF8.GetBytes(stars);
        var starsBoundary = new StarsHandParser().Boundary;
        int second = stars.IndexOf("\nPokerStars Hand #200000000003", StringComparison.Ordinal);
        Assert.Equal(second + 1, CompleteHands.CompleteLength(sb, fileQuiet: false, starsBoundary));
        Assert.Equal(sb.Length, CompleteHands.CompleteLength(sb, fileQuiet: true, starsBoundary));

        // 888: свои метки начала и итогов — с метками WPN такой файл всегда давал 0 и молча не импортировался.
        var p888 = "#Game No : 1\n***** 888poker Hand History for Game 1 *****\nSeat 1: a ( 100 )\n** Summary **\na collected [ 10 ]\n\n";
        var b888 = Encoding.UTF8.GetBytes(p888);
        Assert.Equal(0, CompleteHands.CompleteLength(b888, fileQuiet: true));
        Assert.Equal(b888.Length, CompleteHands.CompleteLength(b888, fileQuiet: true, new Poker888HandParser().Boundary));

        // iPoker: раздача закрыта тегом </game>; недописанная следующая не берётся, сколько бы файл ни «молчал».
        var xml = "<session><general><nickname>h</nickname></general><game gamecode=\"1\"><general/></game><game gamecode=\"2\"><gen";
        var bx = Encoding.UTF8.GetBytes(xml);
        var ipoker = new IPokerHandParser().Boundary;
        Assert.True(ipoker.WholeFile);
        Assert.Equal(xml.IndexOf("</game>", StringComparison.Ordinal) + "</game>".Length, CompleteHands.CompleteLength(bx, fileQuiet: true, ipoker));
    }

    [Fact]
    public void EveryRegisteredParserDeclaresBoundary()
    {
        foreach (var room in ParserRegistry.Rooms)
        {
            IHandHistoryParser p = ParserRegistry.ForRoom(room.Code);
            Assert.NotEmpty(p.Boundary.Starts);
            Assert.False(string.IsNullOrEmpty(p.Boundary.EndMarker));
        }
    }
}
