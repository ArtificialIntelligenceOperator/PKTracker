using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Parsers.Common;
using PKTracker.Parsers.PokerStars;
using PKTracker.Parsers.Winamax;

namespace PKTracker.Tests;

/// <summary>
/// Раздачи, записанные наблюдателем, и клоны формата PokerStars: в них нет «Dealt to», а часть строк усечена.
/// Все раздачи здесь синтетические.
/// </summary>
public class ObservedHandsTests
{
    // Клон формата PokerStars: номер турнира с буквами, «shows» без описания, выигрыш из основного и побочного
    // банков двумя «won», «showed [..]» без исхода и «and lost with» без названия комбинации.
    private const string StarsClone = """
        PokerStars Hand #9000000001: Tournament #916-abc123, $11 USD Hold'em No Limit - Level (100/200) - 2026/01/02 03:04:05 UTC
        Table 'CHC_1000000001 2' 6-max Seat #1 is the button
        Seat 1: Anna (1000 in chips)
        Seat 2: Boris (3000 in chips)
        Seat 3: Clara (3000 in chips)
        Anna: posts the ante 25
        Boris: posts the ante 25
        Clara: posts the ante 25
        Boris: posts small blind 100
        Clara: posts big blind 200
        *** HOLE CARDS ***
        Anna: raises 775 to 975 and is all-in
        Boris: calls 875
        Clara: calls 775
        *** FLOP *** [2c 7d 9h]
        Boris: bets 500
        Clara: calls 500
        *** TURN *** [2c 7d 9h] [Ks]
        Boris: checks
        Clara: checks
        *** RIVER *** [2c 7d 9h Ks] [3s]
        Boris: checks
        Clara: checks
        *** SHOW DOWN ***
        Boris: shows [Kd Qd] (Pair, KK)
        Clara: shows [Jh Jc]
        Anna: shows [8h 8c] (Pair, 88)
        Boris collected 3000 from main pot
        Boris collected 1000 from side pot-1
        *** SUMMARY ***
        Total pot 4000 | Rake 0
        Board [2c 7d 9h Ks 3s]
        Seat 1: Anna (button) showed [8h 8c] and lost with
        Seat 2: Boris (small blind) showed [Kd Qd] and won (3000) with Pair, KK, and won (1000) with Pair, KK
        Seat 3: Clara (big blind) showed [Jh Jc]
        """;

    // Winamax глазами наблюдателя: нет «Dealt to», у «shows» и итогов нет названия комбинации.
    private const string WinamaxObserved = """
        Winamax Poker - Tournament "Test" buyIn: 9€ + 1€ level: 3 - HandId: #1000000000000000009-5-1700000000 - Holdem no limit (10/50/100) - 2026/01/02 03:04:05 UTC
        Table: 'Test(100000009)#001' 6-max (real money) Seat #1 is the button
        Seat 1: Anna (2000)
        Seat 2: Boris (2000)
        Seat 3: Clara (2000)
        *** ANTE/BLINDS ***
        Anna posts ante 10
        Boris posts ante 10
        Clara posts ante 10
        Boris posts small blind 50
        Clara posts big blind 100
        *** PRE-FLOP ***
        Anna folds
        Boris calls 50
        Clara checks
        *** FLOP *** [2c 7d 9h]
        Boris checks
        Clara checks
        *** TURN *** [2c 7d 9h][Ks]
        Boris checks
        Clara checks
        *** RIVER *** [2c 7d 9h Ks][3s]
        Boris checks
        Clara checks
        *** SHOW DOWN ***
        Boris shows [Kd Qd]
        Clara shows [Jh Jc]
        Boris collected 230 from pot
        *** SUMMARY ***
        Total pot 230 | No rake
        Board: [2c 7d 9h Ks 3s]
        Seat 2: Boris (small blind) showed [Kd Qd] and won 230
        Seat 3: Clara (big blind) showed [Jh Jc] and lost
        """;

    [Fact]
    public void TournamentIdKeepsNumbersAndHashesTheRest()
    {
        Assert.Equal(3000000001L, HhText.TournamentId("3000000001"));
        long a = HhText.TournamentId("916-1a5b8d2"), b = HhText.TournamentId("916-1e63d3c");
        Assert.Equal(a, HhText.TournamentId("916-1a5b8d2"));   // одинаков между запусками и раздачами одного турнира
        Assert.NotEqual(a, b);
        // Вне диапазона настоящих номеров: с числовым id другого турнира не совпадёт.
        Assert.True(a >= 1L << 62 && b >= 1L << 62);
    }

    [Fact]
    public void StarsFormatCloneParsesAndBalances()
    {
        var r = new StarsHandParser().Parse(StarsClone, new HandSourceInfo("t.txt")).Single();
        Assert.True(r.Ok, r.Error);
        var h = r.Hand!;
        Assert.Equal(HhText.TournamentId("916-abc123"), h.TournamentId);
        Assert.Null(h.HeroName);                                 // карт никому не роздано — владельца у файла нет
        Assert.Equal(400_000, h.SeatByName("Boris")!.Won);       // основной и побочный банк сложены
        Assert.Equal(0, h.SeatByName("Anna")!.Won);
        Assert.True(h.SeatByName("Clara")!.ShowedCards);
        Assert.Empty(Invariants.Check(h, HandContextBuilder.Build(h, computeEv: false)));
    }

    [Fact]
    public void WinamaxHandSeenByObserverParsesAndBalances()
    {
        var r = new WinamaxHandParser().Parse(WinamaxObserved, new HandSourceInfo("t.txt")).Single();
        Assert.True(r.Ok, r.Error);
        var h = r.Hand!;
        Assert.Null(h.HeroName);
        Assert.Equal(23_000, h.SeatByName("Boris")!.Won);
        Assert.True(h.SeatByName("Clara")!.ShowedCards);
        Assert.Empty(Invariants.Check(h, HandContextBuilder.Build(h, computeEv: false)));
    }
}
