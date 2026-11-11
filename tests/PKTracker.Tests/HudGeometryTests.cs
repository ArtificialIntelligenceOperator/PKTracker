using PKTracker.Core.Model;
using PKTracker.Data;

namespace PKTracker.Tests;

public class HudGeometryTests
{
    private static ActiveTable Cash(string name) =>
        new(1, "G1", "T1", name, GameKind.Cash, null, 6, DateTime.UtcNow, 2, new());

    private static ActiveTable Mtt(string name, string tableId, long tid) =>
        new(2, "G" + tid, tableId, name, GameKind.Mtt, tid, 8, DateTime.UtcNow, 3, new());

    [Theory]
    [InlineData("Los Banos", "Los Banos", true)]
    [InlineData("Los Banos - NL50 - 6 max", "Los Banos", true)]
    [InlineData("PokerKing: Ste. Marie $0.10/$0.25", "Ste. Marie", true)]
    [InlineData("Alsipx", "Alsip", false)]
    [InlineData("Lobby", "Alsip", false)]
    public void CashTitle(string title, string table, bool expected) =>
        Assert.Equal(expected, HudGeometry.TitleMatches(title, Cash(table)));

    [Theory]
    [InlineData("Main Event - $100,000 GTD - Table 7", true)]
    [InlineData("Tournament 40000001 Table 7", true)]
    [InlineData("Tournament 40000001 Table 71", false)]
    [InlineData("Main Event - $100,000 GTD - Table 44", false)]
    [InlineData("Other tournament - Table 7", false)]
    public void TournamentTitle(string title, bool expected) =>
        Assert.Equal(expected, HudGeometry.TitleMatches(title, Mtt("Main Event - $100,000 GTD #7", "T7", 40000001)));

    [Theory]
    [InlineData("$5.00+$0.50 USD Hold'em No Limit - Blinds 100/200 - Tournament 40000001 Table 1 - Logged In as hero", true)]
    [InlineData("Tournament 40000001 Table 12", false)]   // другой стол того же турнира
    [InlineData("Tournament 40000002 Table 1", false)]    // тот же номер стола, другой турнир
    public void TournamentTitleWhenHistoryWritesIdAndTableTogether(string title, bool expected) =>
        // PokerStars пишет в раздаче Table '40000001 1': id турнира и номер стола одним полем,
        // а в заголовке окна они стоят порознь
        Assert.Equal(expected, HudGeometry.TitleMatches(title,
            Mtt("$5.00+$0.50 USD #40000001 стол 1", "T40000001 1", 40000001)));

    [Fact]
    public void HeroIsAlwaysAtBottom()
    {
        for (int hero = 1; hero <= 9; hero++)
        {
            Assert.Equal(0, HudGeometry.VisualIndex(hero, hero, 9));
            var (x, y) = HudGeometry.Anchor(HudGeometry.VisualIndex(hero, hero, 9), 9);
            Assert.Equal(0.5, x, 6);
            Assert.True(y > 0.8);
        }
    }

    [Fact]
    public void SeatsGoClockwiseFromHero()
    {
        // 6-max, Hero на месте 5: место 6 — следующее по часовой (слева снизу), место 4 — справа снизу
        Assert.Equal(1, HudGeometry.VisualIndex(6, 5, 6));
        Assert.Equal(5, HudGeometry.VisualIndex(4, 5, 6));
        Assert.True(HudGeometry.Anchor(1, 6).X < 0.5);
        Assert.True(HudGeometry.Anchor(5, 6).X > 0.5);
        // Все места различны
        Assert.Equal(6, Enumerable.Range(1, 6).Select(s => HudGeometry.VisualIndex(s, 5, 6)).Distinct().Count());
    }
}
