using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Parsers.Wpn;

namespace PKTracker.Tests;

/// <summary>
/// Оракулы для полей контекста, добавленных в ContextVersion 2. Ошибка здесь не падает и не ловится
/// инвариантами — попап просто молча покажет не те числа, поэтому значения выписаны руками.
/// </summary>
public class DecisionContextTests
{
    /// <summary>Решение игрока по порядку: <paramref name="nth"/> = 0 — первое в раздаче.</summary>
    private static DecisionPoint Decision(HandContext ctx, string player, int nth = 0) =>
        ctx.Decisions.Where(d => d.PlayerName == player).Skip(nth).First();

    // Раздача: Vik открывает рейзом, Sundialler с кнопки 3-бетит, остальные пасуют.
    private static HandContext OpenThreeBet => TestUtil.Context(SampleHands.OpenThreeBetFold);

    [Fact]
    public void OpenerHasNoOneToActAgainst()
    {
        var open = Decision(OpenThreeBet, "Vik");
        Assert.Equal(Choice.Raise, open.Chosen);
        // До опена рейзов не было: «против кого» и «кто открыл» ещё не определены.
        Assert.Equal(Position.None, open.VsPosition);
        Assert.Equal(Position.None, open.OpenerPosition);
    }

    [Fact]
    public void ThreeBettorSeesOpenerPosition()
    {
        var ctx = OpenThreeBet;
        var opener = Decision(ctx, "Vik");
        var threeBet = Decision(ctx, "Sundialler");
        Assert.Equal(Choice.Raise, threeBet.Chosen);
        Assert.Equal(Position.BTN, threeBet.Position);
        // 3-бет идёт против опенрейзера — он же и открыл банк.
        Assert.Equal(opener.Position, threeBet.VsPosition);
        Assert.Equal(opener.Position, threeBet.OpenerPosition);
    }

    [Fact]
    public void FoldingToThreeBetSeesBothPositions()
    {
        var ctx = OpenThreeBet;
        var opener = Decision(ctx, "Vik");
        var fold = Decision(ctx, "Vik", nth: 1);
        Assert.Equal(Choice.Fold, fold.Chosen);
        // Перед игроком 3-беттор с кнопки, но открывал банк он сам.
        Assert.Equal(Position.BTN, fold.VsPosition);
        Assert.Equal(opener.Position, fold.OpenerPosition);
    }

    [Fact]
    public void BlindsSeeLastAggressorNotFirst()
    {
        var sb = Decision(OpenThreeBet, "Kestrel14");
        Assert.Equal(Position.SB, sb.Position);
        Assert.Equal(Choice.Fold, sb.Chosen);
        // SB пасует уже против 3-бета с кнопки, а не против опена.
        Assert.Equal(Position.BTN, sb.VsPosition);
    }

    [Theory]
    // Опен $0.75 при bb $0.25 — это 3bb поверх блайнда, то есть ровно 3x.
    [InlineData("Vik", 0, 3.0, 1.0, 3.0)]
    // 3-бет $2.50 поверх опена $0.75 — 10bb и чуть больше трёх размеров опена.
    [InlineData("Sundialler", 0, 10.0, 3.0, 10.0 / 3.0)]
    public void BetSizesAreMeasuredInBigBlinds(string player, int nth, double toBb, double facingBb, double ratio)
    {
        var d = Decision(OpenThreeBet, player, nth);
        Assert.Equal(toBb, d.ToAmountBb, 3);
        Assert.Equal(facingBb, d.FacingBb, 3);
        Assert.Equal(ratio, d.RaiseRatio, 3);
    }

    [Fact]
    public void MinRaiseIsRecognisedByRatio()
    {
        // Минрейз — перекрытие ровно вдвое; 3-бет из примера втрое больше и минрейзом считаться не должен.
        var threeBet = Decision(OpenThreeBet, "Sundialler");
        Assert.True(threeBet.RaiseRatio > 2.3);
    }

    // Раздача: Gamma стилит с кнопки, Eps коллирует из BB, на флопе чек-рейзит.
    private static HandContext StealCbet => TestUtil.Context(SampleHands.StealCbetCheckRaise);

    [Fact]
    public void FlopDecisionsHaveNoLineYet()
    {
        // Линия описывает прошлые улицы; на флопе их ещё нет.
        foreach (var d in StealCbet.Decisions.Where(d => d.Street == Street.Flop))
            Assert.Equal("", d.MyLine);
    }

    [Fact]
    public void CheckRaiserSeesBettorPosition()
    {
        var ctx = StealCbet;
        var raise = ctx.Decisions.First(d => d.Street == Street.Flop && d.Chosen == Choice.Raise);
        Assert.Equal("Eps", raise.PlayerName);
        // Чек-рейз идёт против сибета с кнопки.
        Assert.Equal(Position.BTN, raise.VsPosition);
        Assert.True(raise.CheckedThisStreet);
    }

    [Fact]
    public void PreflopOpenerPositionSurvivesToPostflop()
    {
        var ctx = StealCbet;
        var cbet = ctx.Decisions.First(d => d.Street == Street.Flop && d.Chosen == Choice.Bet);
        Assert.Equal("Gamma", cbet.PlayerName);
        // Банк открыл он сам с кнопки — на постфлопе это должно помниться.
        Assert.Equal(Position.BTN, cbet.OpenerPosition);
    }

    [Fact]
    public void FirstToActOnFlopFacesNobody()
    {
        var ctx = StealCbet;
        var check = ctx.Decisions.First(d => d.Street == Street.Flop);
        Assert.Equal("Eps", check.PlayerName);
        Assert.Equal(Choice.Check, check.Chosen);
        // Улица началась заново: агрессия префлопа на флопе уже не считается.
        Assert.Equal(Position.None, check.VsPosition);
        Assert.Equal(0, check.FacingAmount);
    }

    /// <summary>
    /// Спин, где рум вернул «непокрытой ставки» больше, чем игрок поставил: в возврат попал
    /// мёртвый сайд-пот. Без поправки invested уходит в минус и деньги не сходятся.
    /// </summary>
    [Fact]
    public void OverstatedUncalledBetDoesNotBreakMoney()
    {
        var hand = TestUtil.Parse(SampleHands.SpinOverstatedUncalledBet, GameKind.Sng);
        var ctx = HandContextBuilder.Build(hand);
        Assert.Empty(Invariants.Check(hand, ctx));

        // Рейзер поставил 600 и был перекрыт олл-ином за 5: в банке остаётся ровно 5.
        var raiser = ctx.Players.Single(p => p.PlayerName == "MabryUK88");
        Assert.Equal(500, raiser.Invested);
        Assert.All(ctx.Players, p => Assert.True(p.Invested >= 0, $"{p.PlayerName}: invested {p.Invested}"));
        // Σ вложено == банк (рейка в турнирах нет).
        Assert.Equal(hand.TotalPot, ctx.Players.Sum(p => p.Invested));
        Assert.Equal(hand.TotalPot, ctx.Players.Sum(p => p.Collected));
    }

    /// <summary>В обычной раздаче поправка не должна ничего менять: возврат берётся как есть.</summary>
    [Fact]
    public void NormalUncalledBetIsUntouched()
    {
        var ctx = TestUtil.Context(SampleHands.OpenThreeBetFold);
        // Sundialler поднял до 2.50, все сбросили — ему вернулось 1.75, в банке остались 0.75.
        var raiser = ctx.Players.Single(p => p.PlayerName == "Sundialler");
        Assert.Equal(75, raiser.Invested);
    }

    [Fact]
    public void LineRecordsLastActionOfEachStreet()
    {
        // Собираем линию вручную по улицам и сверяем с тем, что несёт решение.
        var ctx = TestUtil.Context(SampleHands.HeadsUpShowdown);
        foreach (var d in ctx.Decisions.Where(d => d.Street == Street.River))
        {
            var own = ctx.Decisions
                .Where(x => x.Seat == d.Seat && x.Street > Street.Preflop && x.Street < Street.River)
                .GroupBy(x => x.Street)
                .Select(g => g.Last().Chosen switch
                {
                    Choice.Check => 'X', Choice.Bet => 'B', Choice.Call => 'C', Choice.Raise => 'R', _ => '?',
                });
            Assert.Equal(new string(own.ToArray()), d.MyLine);
        }
    }
}
