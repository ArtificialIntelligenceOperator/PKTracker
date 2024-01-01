using PKTracker.Core.Model;
using PKTracker.Data;

namespace PKTracker.Tests;

/// <summary>
/// Модель Chip EV: линия факта и линия, где в олл-инах вместо
/// результата взято матожидание по эквити. Ошибка здесь тихо рисует неверную «удачу».
/// </summary>
public class TourneyGraphTests
{
    private static TourneyHandRow Hand(long tid, long net, long? ev = null, int players = 6,
        Position pos = Position.BTN, int stackBbX10 = 300) =>
        new(tid, players, pos, stackBbX10, net, ev);

    [Fact]
    public void EvLineDivergesFromFactOnlyInAllIns()
    {
        var rows = new[]
        {
            Hand(1, -1000),              // обычная раздача: EV == факт
            Hand(1, +5000, +2000),       // олл-ин: выиграл 50, а «по-честному» стоил 20
            Hand(1, -2000),
        };

        var g = TourneyGraphBuilder.Build(rows);

        Assert.Equal(3, g.Hands);
        Assert.Equal(new[] { -10.0, 40.0, 20.0 }, g.Chips);
        Assert.Equal(new[] { -10.0, 10.0, -10.0 }, g.ChipEv);
        Assert.Equal(2000, g.ChipNet);
        Assert.Equal(-1000, g.ChipEvNet);
        Assert.Equal(3000, g.Luck);      // факт − EV: столько «подарила» удача
        Assert.Equal(1, g.AllIns);
    }

    [Fact]
    public void FilterByTableSizeSplitsHeadsUpFromThreeHanded()
    {
        var rows = new[]
        {
            Hand(1, +1000, players: 3),
            Hand(1, -400, players: 2),
            Hand(1, +300, players: 2),
        };

        var hu = TourneyGraphBuilder.Build(rows, new TourneyGraphFilter(MinPlayers: 2, MaxPlayers: 2));
        Assert.Equal(2, hu.Hands);
        Assert.Equal(-100, hu.ChipNet);

        var threeWay = TourneyGraphBuilder.Build(rows, new TourneyGraphFilter(MinPlayers: 3, MaxPlayers: 3));
        Assert.Equal(1, threeWay.Hands);
        Assert.Equal(1000, threeWay.ChipNet);
    }

    [Fact]
    public void FiltersCombine()
    {
        var rows = new[]
        {
            Hand(1, +100, players: 3, pos: Position.BTN, stackBbX10: 80),
            Hand(1, +200, players: 3, pos: Position.BB, stackBbX10: 80),
            Hand(2, +400, players: 3, pos: Position.BTN, stackBbX10: 300),
        };

        // Турнир 1 + BTN + стек меньше 10bb — подходит только первая раздача
        var g = TourneyGraphBuilder.Build(rows, new TourneyGraphFilter(
            Tournaments: new HashSet<long> { 1 }, Position: Position.BTN, MaxStackBb: 10));

        Assert.Equal(1, g.Hands);
        Assert.Equal(100, g.ChipNet);
        Assert.Equal(1, g.Tournaments);
    }

    [Fact]
    public void EmptySelectionGivesEmptyLines()
    {
        var g = TourneyGraphBuilder.Build(new[] { Hand(1, +100, players: 6) },
            new TourneyGraphFilter(MinPlayers: 2, MaxPlayers: 2));

        Assert.Equal(0, g.Hands);
        Assert.Empty(g.Chips);
        Assert.Equal(0, g.Luck);
    }
}
