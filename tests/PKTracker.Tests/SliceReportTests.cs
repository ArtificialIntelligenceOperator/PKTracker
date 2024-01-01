using PKTracker.Core.Model;
using PKTracker.Data;

namespace PKTracker.Tests;

/// <summary>
/// Отчёты со срезами. Считает их SQL, но всё, что может тихо разъехаться, — сложение групп
/// в итоговую строку, расшифровка ключей группировки и границы полос стека — живёт в C#
/// и проверяется здесь без базы.
/// </summary>
public class SliceReportTests
{
    private static SliceStats Hand(double bb, bool vpip = false, bool pfr = false, bool flop = false,
        bool showdown = false, bool wonSd = false, bool won = false) => new(
        Hands: 1, Vpip: vpip ? 1 : 0, Pfr: pfr ? 1 : 0, VpipCases: 1,
        SawFlop: flop ? 1 : 0, SawShowdown: showdown ? 1 : 0, WonAtShowdown: wonSd ? 1 : 0,
        WonHand: flop && won ? 1 : 0,
        NetWon: 0, NetBbX100: (long)Math.Round(bb * 100), SdBbX100: 0, NonSdBbX100: 0,
        EvNetWon: 0, EvBbX100: 0, AllInHands: 0, Rake: 0,
        SumBbSq: bb * bb, Tournaments: 0);

    private static SliceStats Sum(IEnumerable<SliceStats> xs) => xs.Aggregate(SliceStats.Empty, (a, b) => a + b);

    /// <summary>
    /// Главное свойство итоговой строки: она обязана совпасть с отдельным пересчётом по всей выборке.
    /// Дисперсия ради этого и хранится суммами — среднее от средних дало бы другое число.
    /// </summary>
    [Fact]
    public void TotalRowMatchesWholeSample()
    {
        double[] all = { 12.5, -3.25, 0, 40, -18.75, 7, -1.5, 96 };
        var whole = Sum(all.Select(x => Hand(x)));
        // Те же раздачи, разложенные по трём группам разбивки.
        var byGroups = Sum(new[] { Sum(all.Take(3).Select(x => Hand(x))), Sum(all.Skip(3).Take(1).Select(x => Hand(x))), Sum(all.Skip(4).Select(x => Hand(x))) });

        Assert.Equal(whole.Hands, byGroups.Hands);
        Assert.Equal(whole.NetBbX100, byGroups.NetBbX100);
        Assert.Equal(whole.Bb100!.Value, byGroups.Bb100!.Value, 6);
        Assert.Equal(whole.StdDevBb100!.Value, byGroups.StdDevBb100!.Value, 6);
    }

    /// <summary>σ считается по несмещённой оценке и в bb/100, то есть σ одной раздачи ×10.</summary>
    [Fact]
    public void StdDevIsPerHandSigmaTimesTen()
    {
        double[] all = { 1, 2, 3, 4, 5 };
        var s = Sum(all.Select(x => Hand(x)));
        double mean = all.Average();
        double expect = Math.Sqrt(all.Sum(x => (x - mean) * (x - mean)) / (all.Length - 1)) * 10;
        Assert.Equal(expect, s.StdDevBb100!.Value, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void StdDevNeedsTwoHands(int hands)
    {
        var s = Sum(Enumerable.Repeat(Hand(5), hands));
        Assert.Null(s.StdDevBb100);
    }

    /// <summary>Пустая выборка не должна давать 0% — иначе «нет данных» не отличить от «никогда».</summary>
    [Fact]
    public void EmptySampleHasNoPercentages()
    {
        Assert.Null(SliceStats.Empty.VpipPct);
        Assert.Null(SliceStats.Empty.WtsdPct);
        Assert.Null(SliceStats.Empty.Bb100);
        Assert.Null(SliceActions.Empty.ThreeBetPct);
        Assert.Null(SliceActions.Empty.Af);
        Assert.Null(SliceActions.Empty.AggPct);
    }

    /// <summary>WWSF считается только по раздачам с флопом — числитель без флопа туда попасть не может.</summary>
    [Fact]
    public void WwsfNeverExceedsHundred()
    {
        var s = Sum(new[]
        {
            Hand(1, won: true),                          // выиграна префлопом: во WWSF не входит
            Hand(1, flop: true, won: true),
            Hand(1, flop: true),
        });
        Assert.Equal(2, s.SawFlop);
        Assert.Equal(1, s.WonHand);
        Assert.Equal(50, s.WwsfPct!.Value, 6);
    }

    /// <summary>Агрессия без единого колла — не ноль и не деление на ноль.</summary>
    [Fact]
    public void AggressionWithoutCallsIsInfinite()
    {
        var a = new SliceActions(0, 0, Bets: 3, Raises: 1, Calls: 0, Folds: 2);
        Assert.True(double.IsPositiveInfinity(a.Af!.Value));
        Assert.Equal(4 * 100.0 / 6, a.AggPct!.Value, 6);
    }

    // ── срез и хлебные крошки ──────────────────────────────────────────────

    private static ReportSlice Full => new()
    {
        Kind = GameKind.Mtt,
        Tournaments = new HashSet<long> { 1, 2 }, FormatLabel = "$5.00 Spin & Go",
        MinPlayers = 2, MaxPlayers = 2, PlayersLabel = "хедз-ап",
        Position = Position.BB,
        MinStackBb = 10, MaxStackBb = 20, StackLabel = "10–20bb",
        Action = new ActionSlice(Street.Preflop, Facing.Raise, Choice.Fold),
    };

    [Fact]
    public void EveryFilterBecomesOneChip()
    {
        var labels = Full.Chips().Select(c => c.Label).ToList();
        Assert.Equal(new[] { "$5.00 Spin & Go", "хедз-ап", "BB", "10–20bb", "Фолд против рейза, префлоп" }, labels);
    }

    /// <summary>Снятая фишка убирает своё измерение целиком и не трогает соседние.</summary>
    [Fact]
    public void RemovingChipClearsOnlyItsOwnFields()
    {
        var withoutStack = Full.Chips().Single(c => c.Label == "10–20bb").Without;
        Assert.Null(withoutStack.MinStackBb);
        Assert.Null(withoutStack.MaxStackBb);
        Assert.Null(withoutStack.StackLabel);
        Assert.Equal(Position.BB, withoutStack.Position);
        Assert.Equal(Full.Tournaments, withoutStack.Tournaments);
        Assert.Equal(4, withoutStack.Chips().Count);
    }

    [Fact]
    public void RemovingFormatDropsBothIdsAndLabel()
    {
        var w = Full.Chips().First().Without;
        Assert.Null(w.Tournaments);
        Assert.Null(w.FormatLabel);
    }

    [Fact]
    public void EmptySliceHasNoChips() => Assert.Empty(new ReportSlice().Chips());

    /// <summary>
    /// Деньги турнира — свойство турнира целиком, и по срезу рук их считать нельзя; отчёт
    /// узнаёт об этом по флагу и предупреждает.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyHandLevelFiltersMarkTheSliceNarrowed(bool onlyFormat)
    {
        var s = onlyFormat
            ? new ReportSlice { Tournaments = new HashSet<long> { 1 }, FormatLabel = "x" }
            : new ReportSlice { Position = Position.BTN };
        Assert.Equal(!onlyFormat, s.NarrowsHands);
    }

    // ── полосы стека ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StackBandsAreContiguousAndCoverEverything(bool tournament)
    {
        var bands = ReportSlice.StackBands(tournament);
        Assert.Equal(0, bands[0].From);
        for (int i = 1; i < bands.Count; i++) Assert.Equal(bands[i - 1].To, bands[i].From);
        Assert.Equal(double.MaxValue, bands[^1].To);
    }

    /// <summary>У кеша и турниров полосы разные: сотня блайндов в турнирных полосах всегда в последней.</summary>
    [Fact]
    public void CashAndTournamentBandsDiffer() =>
        Assert.NotEqual(ReportSlice.StackBands(true).Select(b => b.To), ReportSlice.StackBands(false).Select(b => b.To));

    /// <summary>
    /// CASE в SQL строится из того же списка полос. Если он разойдётся со списком, строки получат
    /// чужие подписи — молча, поэтому пороги сверяются явно.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StackCaseUsesTheSameThresholds(bool tournament)
    {
        var sql = SliceReportRepository.KeyExpr(Breakdown.Stack, tournament);
        var bands = ReportSlice.StackBands(tournament);
        for (int i = 0; i < bands.Count - 1; i++)
            Assert.Contains($"< {(int)(bands[i].To * 10)} THEN {i}", sql);
        Assert.Contains($"ELSE {bands.Count - 1} END", sql);
    }

    [Theory]
    [InlineData(true, 0, "до 10bb")]
    [InlineData(true, 5, "80bb+")]
    [InlineData(false, 0, "до 40bb")]
    public void StackLabelsComeFromTheBandList(bool tournament, int key, string expected) =>
        Assert.Equal(expected, SliceReportRepository.Label(Breakdown.Stack, key.ToString(), tournament));

    // ── ключи группировки ──────────────────────────────────────────────────

    /// <summary>
    /// Позиция и хедз-ап пакуются в одно число: SQL кодирует, приложение разбирает. Разъедутся —
    /// строки «Позиции» покажут не ту позицию, поэтому кодирование проверяется в обе стороны.
    /// </summary>
    [Theory]
    [InlineData(Position.BTN, false, "BTN")]
    [InlineData(Position.BTN, true, "BTN, хедз-ап")]
    [InlineData(Position.BB, true, "BB, хедз-ап")]
    [InlineData(Position.None, false, "без позиции")]
    public void PositionKeyRoundTrips(Position pos, bool headsUp, string label)
    {
        // Так же, как считает SQL: position * 2 + признак хедз-апа.
        string key = ((int)pos * 2 + (headsUp ? 1 : 0)).ToString();
        Assert.Equal(label, SliceReportRepository.Label(Breakdown.Position, key, true));
        Assert.Equal((pos, headsUp), SliceReportRepository.PositionKey(key));
    }

    [Fact]
    public void PositionKeyExprMatchesTheDecoder()
    {
        var sql = SliceReportRepository.KeyExpr(Breakdown.Position, true);
        Assert.Contains("hp.position::int * 2", sql);
        Assert.Contains("players_dealt <= 2", sql);
    }

    [Theory]
    [InlineData(Street.Preflop, Facing.Raise, Choice.Fold, "Фолд против рейза, префлоп")]
    [InlineData(Street.Flop, Facing.Bet, Choice.Raise, "Рейз против ставки, флоп")]
    [InlineData(Street.Turn, Facing.None, Choice.Check, "Чек, тёрн")]
    public void ActionLabelsReadAsSentences(Street s, Facing f, Choice c, string expected) =>
        Assert.Equal(expected, new ActionSlice(s, f, c).Label);

    // ── деньги турниров ────────────────────────────────────────────────────

    private static TournamentRow T(long id, long buyIn, long fee, int entries, long prize, long ticket = 0, long bounty = 0, long hands = 10) =>
        new(id, "t" + id, DateTime.UtcNow, hands, buyIn, fee, "USD", entries, entries > 0 ? 1 : null, prize, ticket, bounty, 3);

    /// <summary>Затраты считаются по числу входов: ре-энтри стоит столько же, сколько первый вход.</summary>
    [Fact]
    public void CostCountsEveryEntry()
    {
        var m = SliceMoney.Of(new[] { T(1, 450, 50, entries: 2, prize: 0) });
        Assert.Equal(1000, m.Cost);
        Assert.Equal(2, m.Entries);
        Assert.Equal(-1000, m.Profit);
        Assert.Equal(-100, m.Roi!.Value, 6);
        Assert.Equal(5.0, m.Abi!.Value, 6);
    }

    /// <summary>
    /// ITM — попадание в призы, а не любая выплата: в PKO баунти капают и при вылете вне призов,
    /// и если считать их, ITM у KO-турниров окажется почти 100%.
    /// </summary>
    [Fact]
    public void BountyAloneIsNotInTheMoney()
    {
        var m = SliceMoney.Of(new[]
        {
            T(1, 450, 50, entries: 1, prize: 0, bounty: 300),   // баунти есть, призов нет
            T(2, 450, 50, entries: 1, prize: 1500),             // приз
            T(3, 450, 50, entries: 1, prize: 0, ticket: 500),   // билет
        });
        Assert.Equal(2, m.Itm);
        Assert.Equal(3, m.Entries);
        Assert.Equal(2300, m.Won);
    }

    /// <summary>Турниры без итогов (.ots ещё не загружен) в деньги не попадают, но в счёт формата — да.</summary>
    [Fact]
    public void TournamentsWithoutResultsAddNoMoney()
    {
        var m = SliceMoney.Of(new[] { T(1, 450, 50, entries: 0, prize: 0), T(2, 450, 50, entries: 1, prize: 900) });
        Assert.Equal(2, m.Count);
        Assert.Equal(1, m.Entries);
        Assert.Equal(500, m.Cost);
        Assert.Equal(400, m.Profit);
    }

    [Fact]
    public void EmptyMoneyHasNoRatios()
    {
        Assert.Null(SliceMoney.Empty.Roi);
        Assert.Null(SliceMoney.Empty.Abi);
        Assert.Null(SliceMoney.Empty.ItmPct);
    }
}
