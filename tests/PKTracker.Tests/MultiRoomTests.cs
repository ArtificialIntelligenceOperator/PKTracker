using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;
using PKTracker.Parsers;
using PKTracker.Parsers.PokerStars;

namespace PKTracker.Tests;

/// <summary>Реестр форматов, парсер PokerStars, споты постфлопа (ContextVersion 4) и вид ячеек HUD.</summary>
public class MultiRoomTests
{
    // Синтетическая раздача в формате PokerStars: опен BTN, колл BB, c-бет — колл, тёрн чек-чек, ривер ставка-фолд.
    private const string StarsHand = """
        PokerStars Hand #200000000001: Tournament #3000000001, $9.00+$1.00 USD Hold'em No Limit - Level IX (400/800) - 2024/08/23 21:07:17 ET
        Table '3000000001 1' 6-max Seat #6 is the button
        Seat 1: Sb Guy (40000 in chips)
        Seat 2: bbguy (32000 in chips)
        Seat 3: utgguy (20000 in chips)
        Seat 6: hero (18500 in chips)
        Sb Guy: posts the ante 100
        bbguy: posts the ante 100
        utgguy: posts the ante 100
        hero: posts the ante 100
        Sb Guy: posts small blind 400
        bbguy: posts big blind 800
        *** HOLE CARDS ***
        Dealt to hero [Ah Kd]
        utgguy: folds
        hero: raises 1000 to 1800
        Sb Guy: folds
        bbguy: calls 1000
        *** FLOP *** [8c Td 4d]
        bbguy: checks
        hero: bets 1500
        bbguy: calls 1500
        *** TURN *** [8c Td 4d] [2s]
        bbguy: checks
        hero: checks
        *** RIVER *** [8c Td 4d 2s] [Kc]
        bbguy: checks
        hero: bets 4000
        bbguy: folds
        Uncalled bet (4000) returned to hero
        hero collected 7400 from pot
        hero: doesn't show hand
        *** SUMMARY ***
        Total pot 7400 | Rake 0
        Board [8c Td 4d 2s Kc]
        Seat 1: Sb Guy (small blind) folded before Flop
        Seat 2: bbguy (big blind) folded on the River
        Seat 3: utgguy folded before Flop (didn't bet)
        Seat 6: hero (button) collected (7400)
        """;

    private static ParsedHand ParseStars(string text) =>
        new StarsHandParser().Parse(text, new HandSourceInfo("t.txt")).Single().Hand
        ?? throw new Xunit.Sdk.XunitException("hand not parsed");

    [Fact]
    public void RegistryDetectsFormatByContent()
    {
        Assert.Equal("POKERSTARS", ParserRegistry.Detect(StarsHand, "POKERKING")!.Value.Room.Code);
        Assert.Equal("WINAMAX", ParserRegistry.Detect("Winamax Poker - Tournament \"X\" buyIn: 1€ + 0€ level: 1 - HandId: #1-1-1 - Holdem no limit (10/20) - 2024/01/01 10:00:00 UTC", "POKERKING")!.Value.Room.Code);
        Assert.Null(ParserRegistry.Detect("Tournament #123, Buy-in: $10", "POKERKING"));
        // Скины одной сети неразличимы по тексту: WPN-файл получает код активного скина.
        Assert.True(ParserRegistry.SameNetwork(ParserRegistry.Room("ACR")!, "POKERKING"));
        Assert.False(ParserRegistry.SameNetwork(ParserRegistry.Room("POKERSTARS")!, "POKERKING"));
    }

    [Fact]
    public void StarsHandParsesWithMoneyAndPositionInvariants()
    {
        var h = ParseStars(StarsHand);
        Assert.Equal("hero", h.HeroName);
        Assert.Equal(GameKind.Mtt, h.GameKind);
        Assert.Equal(9, h.TournamentLevel);
        Assert.Equal(80000, h.BigBlind);              // фишки ×100
        Assert.Equal(new DateTime(2024, 8, 24, 1, 7, 17, DateTimeKind.Utc), h.PlayedAtUtc); // ET летом = UTC−4
        Assert.Equal("Sb Guy", h.SeatByNumber(1)!.Name); // ник с пробелом
        var ctx = HandContextBuilder.Build(h);
        Assert.Empty(Invariants.Check(h, ctx));
    }

    [Fact]
    public void UnknownLineRejectsHand()
    {
        var broken = StarsHand.Replace("hero: checks", "hero: does a dance");
        var r = new StarsHandParser().Parse(broken, new HandSourceInfo("t.txt")).Single();
        Assert.False(r.Ok);
        Assert.Contains("Unknown", r.Error);
    }

    [Fact]
    public void PostflopSpotAndOpponentLinesAreKnown()
    {
        var ctx = HandContextBuilder.Build(ParseStars(StarsHand));
        var river = ctx.Decisions.Single(d => d.PlayerName == "hero" && d.Street == Street.River);
        Assert.Equal("RvBB", river.Spot);
        Assert.Equal("BX", river.MyLine);
        Assert.Equal("CX", river.OppLine);   // линия единственного оппонента
        Assert.Equal(1, river.PotRaises);
        Assert.Equal(2, river.FlopPlayers);
        var bbRiver = ctx.Decisions.Single(d => d.PlayerName == "bbguy" && d.Street == Street.River && d.Chosen == Choice.Fold);
        Assert.Equal("BX", bbRiver.PfaLine);  // линия префлоп-агрессора видна коллеру
        var open = ctx.Decisions.Single(d => d.PlayerName == "hero" && d.Street == Street.Preflop);
        Assert.Equal("BTN", open.HPos);
        Assert.Equal(0, open.PotRaises);      // свой рейз не входит в «тип банка» своей точки решения
    }

    [Fact]
    public void H2nPositionNamesFollowButtonDistance()
    {
        Assert.Equal("MP", HandPositions.H2n(Position.UTG, 6));   // 6-max: первая позиция — MP
        Assert.Equal("UTG", HandPositions.H2n(Position.UTG, 9));
        Assert.Equal("MP", HandPositions.H2n(Position.UTG1, 9));
        Assert.Equal("HJ", HandPositions.H2n(Position.HJ, 8));
        Assert.Equal("BB", HandPositions.H2n(Position.BB, 8));
    }

    [Fact]
    public void CellShowsSmallSampleAndCaps100()
    {
        var stat = StatCompiler.Compile(new StatDefinition { Code = "x", Title = "X", Opportunity = "true", Hit = "true" });
        var cell = new HudCell { Stat = "x" };
        var r = CellRender.Render(cell, new CellValue(100, 4, 4), stat);
        Assert.Equal("99", r.Text);
        Assert.Equal(4, r.SmallSample);
        Assert.Null(CellRender.Render(cell, new CellValue(50, 40, 20), stat).SmallSample);
    }

    [Fact]
    public void PackageRoundTripsPanelTabsAndGroups()
    {
        var p = new HudProfile
        {
            Name = "T", Panel = new HudPage { Name = "P" },
            PanelTabs = { new HudPage { Name = "P" }, new HudPage { Name = "B" } },
            Pages = { new HudPage { Name = "RvBB", Group = "RvBB", Cells = { new HudCell { Stat = "x", Style = new CellStyle { Border = "#4DD0E1" } } } } },
        };
        var pkg = HudPackage.FromJson(new HudPackage { Profile = p, Stats = { new StatDefinition { Code = "x", Title = "X", Opportunity = "true", Hit = "true" } } }.ToJson());
        Assert.Equal(2, pkg.Profile.PanelTabs.Count);
        Assert.Equal("RvBB/RvBB", HudProfile.PageKey(pkg.Profile.Pages[0]));
        Assert.Same(pkg.Profile.Pages[0], pkg.Profile.Page("RvBB/RvBB"));
        Assert.Equal("#4DD0E1", pkg.Profile.Pages[0].Cells[0].Style!.Border);
        Assert.Single(pkg.Stats);
    }

    [Fact]
    public void RoomPolicyKeepsHudStaticWhereRulesDemand()
    {
        Assert.False(RoomHudPolicy.For("GGPOKER").HudAllowed);
        Assert.True(RoomHudPolicy.For("IPOKER").StaticOnly);
        Assert.True(RoomHudPolicy.For("WINAMAX").StaticOnly);
        Assert.False(RoomHudPolicy.For("POKERKING").StaticOnly);
    }
}
