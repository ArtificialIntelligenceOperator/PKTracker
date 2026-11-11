using PKTracker.Data;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.Tests;

/// <summary>
/// Правила румов: что политика требует от трекера и как раскладка HUD приводится к «статичному» виду.
/// Тесты не трогают настоящие процессы — список запущенных программ подменяется.
/// </summary>
[Collection(nameof(RoomClientProbeCollection))]
public class RoomRulesTests
{
    private static IDisposable Running(params string[] processes)
    {
        var before = RoomClientProbe.ProcessSource;
        RoomClientProbe.ProcessSource = () => processes;
        RoomClientProbe.Reset();
        return new Restore(() => { RoomClientProbe.ProcessSource = before; RoomClientProbe.Reset(); });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void GgForbidsHudImportAndRunningSideBySide()
    {
        var gg = RoomHudPolicy.For("GGPOKER");
        Assert.False(gg.HudAllowed);
        Assert.True(gg.NoCoexistence);
        Assert.True(gg.ImportNeedsOptIn);
        // Остальные румы стороннего импорта своих раздач не запрещают и рядом с собой трекер терпят.
        foreach (var code in new[] { "POKERKING", "ACR", "POKERSTARS", "WINAMAX", "IPOKER", "888POKER" })
        {
            Assert.True(RoomHudPolicy.For(code).HudAllowed, code);
            Assert.False(RoomHudPolicy.For(code).NoCoexistence, code);
            Assert.False(RoomHudPolicy.For(code).ImportNeedsOptIn, code);
        }
    }

    [Fact]
    public void StaticRoomsLimitColorsWpnDoesNot()
    {
        foreach (var code in new[] { "POKERSTARS", "WINAMAX", "IPOKER", "888POKER" })
        {
            Assert.True(RoomHudPolicy.For(code).StaticOnly, code);
            Assert.Equal(3, RoomHudPolicy.For(code).MaxColorRanges);
        }
        Assert.False(RoomHudPolicy.For("POKERKING").StaticOnly);
        Assert.Equal(int.MaxValue, RoomHudPolicy.For("POKERKING").MaxColorRanges);
    }

    [Fact]
    public void GgClientIsDetectedWhateverRoomIsActive()
    {
        using (Running("explorer", "ggnet", "PokerKing"))
        {
            Assert.Equal("GGnet", RoomClientProbe.ForbiddenCoexistence());
            Assert.NotNull(RoomClientProbe.AnyRoomClient(null));
        }
        using (Running("explorer", "PokerKing"))
        {
            Assert.Null(RoomClientProbe.ForbiddenCoexistence());
            Assert.Equal("PokerKing", RoomClientProbe.AnyRoomClient("PokerKing"));
        }
        using (Running("explorer", "notepad"))
        {
            Assert.Null(RoomClientProbe.AnyRoomClient("PokerKing"));
            // Имя процесса из настроек (симулятор столов, другой скин) тоже считается клиентом рума.
            Assert.Null(RoomClientProbe.AnyRoomClient("PKTracker.FakeTable"));
        }
        using (Running("PKTracker.FakeTable"))
            Assert.Equal("PKTracker.FakeTable", RoomClientProbe.AnyRoomClient("PKTracker.FakeTable"));
    }

    [Fact]
    public void StarsRestrictsReplayerOnlyWhileClientIsOpen()
    {
        var stars = RoomHudPolicy.For("POKERSTARS");
        var ipoker = RoomHudPolicy.For("IPOKER");
        var wpn = RoomHudPolicy.For("POKERKING");
        using (Running("PokerStars"))
        {
            Assert.True(stars.RestrictedNow(overlay: false));
            Assert.True(stars.RestrictedNow(overlay: true));
            Assert.False(ipoker.RestrictedNow(overlay: false));   // у iPoker ограничения только над столом
            Assert.True(ipoker.RestrictedNow(overlay: true));
            Assert.False(wpn.RestrictedNow(overlay: true));
        }
        using (Running("explorer"))
        {
            Assert.False(stars.RestrictedNow(overlay: false));
            Assert.True(stars.RestrictedNow(overlay: true));      // над столом — всегда
        }
    }

    private static Dictionary<string, CompiledStat> Stats(params StatDefinition[] defs) =>
        StatCompiler.CompileAll(defs).ToDictionary(s => s.Code);

    [Fact]
    public void CardBasedStatsAreRecognised()
    {
        Assert.True(HudCompliance.UsesCards("Street == Street.River && Made < MadeHandClass.TopPair"));
        Assert.True(HudCompliance.UsesCards("FlushDraw || Oesd"));
        Assert.True(HudCompliance.UsesCards("BoardTexture > 0"));
        Assert.True(HudCompliance.UsesCards("HoleClass < 20"));
        Assert.False(HudCompliance.UsesCards("Street == Street.Flop && PfAggressor && FacingCbet"));
        Assert.False(HudCompliance.UsesCards("SawShowdown && CardsKnown"));
        Assert.False(HudCompliance.UsesCards(null));

        var stats = Stats(
            new StatDefinition { Code = "cbet", Title = "CB", Opportunity = "PfAggressor", Hit = "Chosen==\"Bet\"" },
            new StatDefinition { Code = "river_bluff", Title = "RB", Opportunity = "Street==\"River\" && MadeKnown", Hit = "!StrongMade" },
            new StatDefinition { Code = "bluff_share", Title = "BS", Scope = StatScope.Expression, Hit = "river_bluff.hits * 100.0 / max(1, cbet.cases)" },
            new StatDefinition { Code = "cbet_x2", Title = "CB2", Scope = StatScope.Expression, Hit = "cbet.hits * 2.0" });
        Assert.False(HudCompliance.IsCardBased(new HudCell { Stat = "cbet" }, stats));
        Assert.True(HudCompliance.IsCardBased(new HudCell { Stat = "river_bluff" }, stats));
        Assert.True(HudCompliance.IsCardBased(new HudCell { Stat = "bluff_share" }, stats));    // формула наследует от слагаемых
        Assert.False(HudCompliance.IsCardBased(new HudCell { Stat = "cbet_x2" }, stats));
        // Обычный стат, суженный в ячейке условием по картам, — тот же «стат по картам».
        Assert.True(HudCompliance.IsCardBased(new HudCell { Stat = "cbet", Filter = new CellFilter { Where = "AnyDraw" } }, stats));
        Assert.False(HudCompliance.IsCardBased(new HudCell { Text = "Made" }, stats));           // подпись — не стат
    }

    [Fact]
    public void RestrictHidesCardStatsAndDropsWideColorScales()
    {
        var five = Enumerable.Range(0, 5).Select(i => new StatColor(i * 20, i * 20 + 20, "#00FF00")).ToList();
        var three = five.Take(3).ToList();
        var stats = Stats(
            new StatDefinition { Code = "cbet", Title = "CB", Opportunity = "PfAggressor", Hit = "Chosen==\"Bet\"", Colors = five },
            new StatDefinition { Code = "vpip", Title = "VPIP", Scope = StatScope.Hand, Opportunity = "Dealt", Hit = "Vpip", Colors = three },
            new StatDefinition { Code = "river_bluff", Title = "RB", Opportunity = "Street==\"River\" && MadeKnown", Hit = "!StrongMade" });

        var panel = new HudPage
        {
            Name = "Панель",
            Cells =
            {
                new HudCell { Row = 0, Col = 0, Stat = "vpip", Popup = "Pre" },
                new HudCell { Row = 0, Col = 1, Stat = "cbet", Popup = "Post" },
                new HudCell { Row = 0, Col = 2, Stat = "river_bluff", Text = "RB", Popup = "Post" },
                new HudCell { Row = 0, Col = 3, Stat = "cbet", Colors = three },      // своя шкала ячейки укладывается в три диапазона
                new HudCell { Row = 1, Col = 0, Text = "подпись" },
            },
        };
        var popup = new HudPage { Name = "Post", Cells = { new HudCell { Stat = "river_bluff" }, new HudCell { Col = 1, Stat = "vpip" } } };
        var profile = new HudProfile { Name = "t", Panel = panel, PanelTabs = { panel }, Pages = { popup } };

        var safe = HudCompliance.Restrict(profile, stats, maxColorRanges: 3, out var report);

        Assert.Equal(2, report.HiddenCells);       // одна на панели (вкладка — та же страница, считается один раз) и одна в попапе
        Assert.Equal(1, report.UncoloredCells);
        Assert.Same(safe.Panel, safe.PanelTabs[0]);

        var cells = safe.Panel.Cells;
        Assert.Equal("vpip", cells[0].Stat);
        Assert.Null(cells[0].Colors);                                  // три диапазона из определения — не трогаем
        Assert.Equal("cbet", cells[1].Stat);
        Assert.Empty(cells[1].Colors!);                                // пять диапазонов — раскраска снята
        Assert.Equal("Post", cells[1].Popup);
        Assert.True(cells[2].IsLabel);                                 // стат по картам — прочерк без попапа
        Assert.Equal("RB " + CellRender.NoData, cells[2].Text);
        Assert.Null(cells[2].Popup);
        Assert.Same(three, cells[3].Colors);
        Assert.Equal("подпись", cells[4].Text);
        Assert.True(safe.Pages[0].Cells[0].IsLabel);
        Assert.Equal("vpip", safe.Pages[0].Cells[1].Stat);

        // Исходная раскладка не изменилась: после игры в реплеере видно всё.
        Assert.Equal("river_bluff", profile.Panel.Cells[2].Stat);
        // Снятая раскраска действительно не красит значение.
        var plain = CellRender.Render(cells[1], new CellValue(50, 5, 10), stats["cbet"]);
        Assert.Null(plain.Fg);
        var colored = CellRender.Render(profile.Panel.Cells[1], new CellValue(50, 5, 10), stats["cbet"]);
        Assert.Equal("#00FF00", colored.Fg);

        // На руме без ограничений (WPN) раскладка остаётся как есть.
        var free = HudCompliance.Restrict(profile, stats, int.MaxValue, out var none);
        Assert.Equal(2, none.HiddenCells);   // Restrict вызывается только для статичных румов; сам по себе он карты прячет всегда
        Assert.Equal(0, none.UncoloredCells);
    }
}

/// <summary>Список процессов в <see cref="RoomClientProbe"/> — общее состояние: тесты, которые его подменяют, не идут параллельно.</summary>
[CollectionDefinition(nameof(RoomClientProbeCollection), DisableParallelization = true)]
public class RoomClientProbeCollection;
