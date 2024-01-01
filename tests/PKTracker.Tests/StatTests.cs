using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Engine.Stats;

namespace PKTracker.Tests;

/// <summary>Оракулы: ожидаемые cases/hits, выбранные вручную (§15.12).</summary>
public class StatTests
{
    [Fact]
    public void AllDefaultStatsCompile()
    {
        Assert.Equal(DefaultStats.All.Count, TestUtil.Stats.Count);
        Assert.Equal(DefaultStats.All.Count, DefaultStats.All.Select(d => d.Code).Distinct().Count());
    }

    [Fact]
    public void ExpressionCannotReferenceExpression()
    {
        var defs = DefaultStats.All.ToDictionary(d => d.Code);
        var bad = new StatDefinition { Code = "bad", Title = "bad", Scope = StatScope.Expression, Hit = "af.hits * 2" };
        Assert.Throws<StatCompileException>(() => StatCompiler.Compile(bad, defs));
    }

    [Fact]
    public void BadConditionFailsToCompile()
    {
        var bad = new StatDefinition { Code = "x", Title = "x", Scope = StatScope.Decision, Opportunity = "Street == ", Hit = "true" };
        Assert.Throws<StatCompileException>(() => StatCompiler.Compile(bad));
    }

    [Theory]
    // player, stat, cases, hits
    [InlineData("Vik", "rfi", 1, 1)]
    [InlineData("Vik", "fold_to_3bet", 1, 1)]
    [InlineData("Vik", "four_bet", 1, 0)]
    [InlineData("Vik", "vpip", 1, 1)]
    [InlineData("Vik", "pfr", 1, 1)]
    [InlineData("Vik Storm", "rfi", 1, 0)]
    [InlineData("Vik Storm", "vpip", 1, 0)]
    [InlineData("LMason07", "three_bet", 1, 0)]
    [InlineData("LMason07", "cold_call", 1, 0)]
    [InlineData("LMason07", "steal", 0, 0)]
    [InlineData("Sundialler", "three_bet", 1, 1)]
    [InlineData("Sundialler", "cold_call", 1, 0)]
    [InlineData("Sundialler", "rfi", 0, 0)]
    [InlineData("Sundialler", "vpip", 1, 1)]
    [InlineData("Sundialler", "pfr", 1, 1)]
    [InlineData("Kestrel14", "three_bet", 0, 0)]
    [InlineData("Nord player", "fold_bb_vs_steal", 0, 0)]
    [InlineData("Nord player", "vpip", 1, 0)]
    public void OpenThreeBetFold(string player, string code, int cases, int hits)
    {
        var ctx = TestUtil.Context(SampleHands.OpenThreeBetFold);
        Assert.Equal((cases, hits), TestUtil.StatFor(ctx, player, code));
    }

    [Theory]
    [InlineData("Gamma", "steal", 1, 1)]
    [InlineData("Gamma", "rfi", 1, 1)]
    [InlineData("Gamma", "cbet_flop", 1, 1)]
    [InlineData("Gamma", "fold_to_cbet_flop", 0, 0)]
    [InlineData("Gamma", "wtsd", 1, 0)]
    [InlineData("Gamma", "wwsf", 1, 0)]
    [InlineData("Alpha", "rfi", 1, 0)]
    [InlineData("Beta", "steal", 1, 0)]
    [InlineData("Delta", "three_bet", 1, 0)]
    [InlineData("Delta", "steal", 0, 0)]
    [InlineData("Eps", "fold_bb_vs_steal", 1, 0)]
    [InlineData("Eps", "three_bet", 1, 0)]
    [InlineData("Eps", "cold_call", 0, 0)]
    [InlineData("Eps", "donk_flop", 1, 0)]
    [InlineData("Eps", "check_raise_flop", 1, 1)]
    [InlineData("Eps", "fold_to_cbet_flop", 1, 0)]
    [InlineData("Eps", "raise_cbet_flop", 1, 1)]
    [InlineData("Eps", "wwsf", 1, 1)]
    [InlineData("Eps", "vpip", 1, 1)]
    [InlineData("Eps", "pfr", 1, 0)]
    public void StealCbetCheckRaise(string player, string code, int cases, int hits)
    {
        var ctx = TestUtil.Context(SampleHands.StealCbetCheckRaise);
        Assert.Equal((cases, hits), TestUtil.StatFor(ctx, player, code));
    }

    [Theory]
    [InlineData("Btn", "limp", 1, 1)]
    [InlineData("Btn", "vpip", 1, 1)]
    [InlineData("Btn", "wtsd", 1, 1)]
    [InlineData("Btn", "wsd", 1, 0)]
    [InlineData("Bb", "wsd", 1, 1)]
    [InlineData("Bb", "vpip", 1, 0)]
    [InlineData("Bb", "donk_flop", 0, 0)]
    [InlineData("Bb", "limp", 0, 0)]
    [InlineData("Btn", "cbet_flop", 0, 0)]
    public void HeadsUp(string player, string code, int cases, int hits)
    {
        var ctx = TestUtil.Context(SampleHands.HeadsUpShowdown);
        Assert.Equal((cases, hits), TestUtil.StatFor(ctx, player, code));
    }

    [Theory]
    [InlineData("Hero", "steal", 1, 1)]
    [InlineData("Hero", "fold_to_3bet", 1, 0)]
    [InlineData("Hero", "four_bet", 1, 0)]
    [InlineData("Hero", "call_shove", 1, 1)]
    [InlineData("Hero", "open_shove", 1, 0)]
    [InlineData("Nina Wolf", "squeeze", 1, 1)]
    [InlineData("Nina Wolf", "fold_bb_vs_steal", 0, 0)]
    [InlineData("12345", "three_bet", 1, 0)]
    [InlineData("12345", "wtsd", 1, 1)]
    [InlineData("12345", "wsd", 1, 1)]
    [InlineData("Hero", "wsd", 1, 0)]
    public void Tournament(string player, string code, int cases, int hits)
    {
        var ctx = TestUtil.Context(SampleHands.TourneyDeadButton, GameKind.Mtt);
        Assert.Equal((cases, hits), TestUtil.StatFor(ctx, player, code));
    }

    [Fact]
    public void AggregatorAndExpressions()
    {
        var stats = TestUtil.Stats;
        var agg = new StatAggregator(stats);
        foreach (var text in new[] { SampleHands.OpenThreeBetFold, SampleHands.StealCbetCheckRaise, SampleHands.HeadsUpShowdown })
        {
            var ctx = TestUtil.Context(text);
            var ids = new long[ctx.Positions!.BySeat.Length];
            foreach (var p in ctx.Players) ids[p.Seat] = p.PlayerName.GetHashCode() & 0x7FFFFFFF | 1;
            agg.Add(ctx, ids);
        }
        var byCode = stats.ToDictionary(s => s.Code);
        long eps = "Eps".GetHashCode() & 0x7FFFFFFF | 1;
        var set = new PlayerStatSet(byCode);
        foreach (var (k, v) in agg.Values.Where(kv => kv.Key.PlayerId == eps)) set.Add(k.StatId, v.Cases, v.Hits, v.Sum);

        Assert.Equal(1.0, set.Evaluate("hands").Value);
        // Eps постфлоп: check, raise → AF = (0+1)/max(0,1) = 1; AGG% = 1*100/max(1,1) = 100
        Assert.Equal(1.0, set.Evaluate("af").Value);
        Assert.Equal(100.0, set.Evaluate("agg_pct").Value);
        // net: +0.55 при bb 0.10 → +5.5 bb → bb/100 = 550
        Assert.Equal(550.0, set.Evaluate("bb100").Value!.Value, 3);
    }
}

public class EvaluatorTests
{
    [Fact]
    public void FiveCardCategoryCountsMatchCombinatorics()
    {
        var counts = new long[9];
        Span<Card> hand = stackalloc Card[5];
        for (int a = 0; a < 52; a++)
        for (int b = a + 1; b < 52; b++)
        for (int c = b + 1; c < 52; c++)
        for (int d = c + 1; d < 52; d++)
        for (int e = d + 1; e < 52; e++)
        {
            hand[0] = new Card((byte)a); hand[1] = new Card((byte)b); hand[2] = new Card((byte)c);
            hand[3] = new Card((byte)d); hand[4] = new Card((byte)e);
            counts[(int)HandEvaluator.Category(HandEvaluator.Evaluate(hand))]++;
        }
        Assert.Equal(new long[] { 1_302_540, 1_098_240, 123_552, 54_912, 10_200, 5_108, 3_744, 624, 40 }, counts);
    }

    [Fact]
    public void ComparesKickersAndWheel()
    {
        int Eval(string s) => HandEvaluator.Evaluate(s.Split(' ').Select(Card.Parse).ToArray());
        Assert.True(Eval("As Ad Kc 7h 2d") > Eval("Ah Ac Qc 7s 2c"));
        Assert.True(Eval("5s 4d 3c 2h Ad") < Eval("6s 5d 4c 3h 2d"));
        Assert.Equal(HandCategory.Straight, HandEvaluator.Category(Eval("5s 4d 3c 2h Ad Kd Kc")));
        Assert.Equal(HandCategory.FullHouse, HandEvaluator.Category(Eval("Ks Kd Kc 2h 2d 2s Ac")));
    }

    [Fact]
    public void HoleClassGrid()
    {
        Assert.Equal(0, Card.HoleClass(Card.Parse("As"), Card.Parse("Ad")));
        Assert.Equal("AKs", Card.HoleClassName(Card.HoleClass(Card.Parse("As"), Card.Parse("Ks"))));
        Assert.Equal("AKo", Card.HoleClassName(Card.HoleClass(Card.Parse("Kd"), Card.Parse("As"))));
        Assert.Equal("22", Card.HoleClassName(Card.HoleClass(Card.Parse("2c"), Card.Parse("2d"))));
        Assert.Equal(33, Card.Parse("Td").Value);
    }
}

public class HandFilterTests
{
    [Fact]
    public void FilterUsesSameLanguageAsStats()
    {
        var ctx = TestUtil.Context(SampleHands.HeadsUpShowdown);
        var btn = ctx.Players.Single(p => p.PlayerName == "Btn");
        var bb = ctx.Players.Single(p => p.PlayerName == "Bb");
        var lostAtShowdown = PKTracker.Engine.Stats.StatCompiler.CompileHandFilter("SawShowdown && !WonAtShowdown && Position == \"BTN\"");
        Assert.True(lostAtShowdown(btn));
        Assert.False(lostAtShowdown(bb));
        var aces = PKTracker.Engine.Stats.StatCompiler.CompileHandFilter("HoleClass == 0 && NetWonBbX100 > 0");
        Assert.True(aces(bb));   // Bb показал AA и выиграл
        Assert.Throws<PKTracker.Engine.Stats.StatCompileException>(() => PKTracker.Engine.Stats.StatCompiler.CompileHandFilter("Pfr &&"));
    }
}

public class AllInEvTests
{
    private static (Card, Card) H(string a, string b) => (Card.Parse(a), Card.Parse(b));

    [Fact]
    public void AcesVsKingsPreflopExact()
    {
        var eq = AllInEv.Equity(new[] { H("As", "Ah"), H("Ks", "Kh") }, Array.Empty<Card>());
        Assert.InRange(eq[0], 0.815, 0.830);   // известное значение ≈ 82%
        Assert.Equal(1.0, eq[0] + eq[1], 6);
    }

    [Fact]
    public void FlushDrawOnFlop()
    {
        // AhKh против 7s7c на 7h 2h 9d: сет против натс-флеш-дро ≈ 25% у дро
        var eq = AllInEv.Equity(new[] { H("Ah", "Kh"), H("7s", "7c") }, new[] { Card.Parse("7h"), Card.Parse("2h"), Card.Parse("9d") });
        Assert.InRange(eq[0], 0.22, 0.29);
    }

    [Fact]
    public void ThreeWayMonteCarloIsDeterministic()
    {
        var hands = new[] { H("As", "Ah"), H("Ks", "Kh"), H("Qs", "Qh") };
        var a = AllInEv.Equity(hands, Array.Empty<Card>(), seed: 7);
        var b = AllInEv.Equity(hands, Array.Empty<Card>(), seed: 7);
        Assert.Equal(a, b);
        Assert.Equal(1.0, a.Sum(), 6);
        Assert.True(a[0] > a[1] && a[1] > a[2]);
    }

    [Fact]
    public void TournamentAllInHandGetsEvWithSidePot()
    {
        var h = TestUtil.Parse(SampleHands.TourneyDeadButton, GameKind.Mtt);
        var ctx = HandContextBuilder.Build(h, computeEv: true);
        Assert.All(ctx.Players, p => Assert.NotNull(p.EvNetWon));
        // EV сохраняет сумму: Σ EV-выигрышей = −рейк (здесь рейка нет) → 0
        Assert.Equal(0, ctx.Players.Sum(p => p.EvNetWon!.Value), 2.0);
        // AKs против QQ и 65o: EV Hero больше факта (факт — проиграл всё)
        var hero = ctx.Players.Single(p => p.PlayerName == "Hero");
        Assert.True(hero.EvNetWon > hero.NetWon);
    }

    [Fact]
    public void NoEvWithoutAllIn()
    {
        var ctx = HandContextBuilder.Build(TestUtil.Parse(SampleHands.HeadsUpShowdown), computeEv: true);
        Assert.All(ctx.Players, p => Assert.Null(p.EvNetWon));
    }
}

public class IcmTests
{
    [Fact]
    public void EqualStacksShareEqually()
    {
        var eq = Icm.Equity(new double[] { 1000, 1000, 1000 }, new double[] { 50, 30, 20 });
        Assert.All(eq, e => Assert.Equal(100.0 / 3, e, 6));
    }

    [Fact]
    public void KnownThreeHandedValues()
    {
        // Стеки 50/30/20, выплаты 50/30/20; значения посчитаны вручную по Malmuth-Harville
        var eq = Icm.Equity(new double[] { 50, 30, 20 }, new double[] { 50, 30, 20 });
        Assert.Equal(100, eq.Sum(), 6);
        Assert.Equal(38.3928571, eq[0], 4);
        Assert.Equal(32.75, eq[1], 4);
        Assert.Equal(28.8571429, eq[2], 4);
    }

    [Fact]
    public void WinnerTakesAllIsLinear()
    {
        var eq = Icm.Equity(new double[] { 300, 100, 100 }, new double[] { 25 });
        Assert.Equal(15, eq[0], 6);
        Assert.Equal(5, eq[1], 6);
    }

    [Fact]
    public void BustedPlayerGetsNothing()
    {
        var eq = Icm.Equity(new double[] { 600, 0, 400 }, new double[] { 70, 30, 10 });
        Assert.Equal(0, eq[1]);
        Assert.Equal(100, eq.Sum(), 6);
    }
}
