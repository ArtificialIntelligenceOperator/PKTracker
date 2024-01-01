using System.Text;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Parsers.Wpn;

namespace PKTracker.Tests;

public class ParserTests
{
    [Fact]
    public void LongestNicknamePrefixWins()
    {
        var h = TestUtil.Parse(SampleHands.OpenThreeBetFold);
        var raise = h.Actions.First(a => a.Type == ActionType.Raise);
        Assert.Equal(3, raise.Seat); // "Vik", а не "Vik Storm"
        var fold = h.Actions.First(a => a.Type == ActionType.Fold);
        Assert.Equal(2, fold.Seat);
        Assert.Equal("Vik", h.HeroName);
    }

    [Fact]
    public void RaiseUsesToAmountNotX()
    {
        // "Kestrel14 raises $0.65 to $0.75" при SB $0.10: доплата = 0.65 (ловушка 7)
        const string text = """
            Hand #1 - Holdem (No Limit) - $0.10/$0.25 - 2024/01/05 13:29:01 UTC
            Demorest 6-max Seat #5 is the button
            Seat 2: Vik Storm ($26.62)
            Seat 5: Sundialler ($72.91)
            Seat 6: Kestrel14 ($36.59)
            Kestrel14 posts the small blind $0.10
            Vik Storm posts the big blind $0.25
            *** HOLE CARDS ***
            Sundialler folds
            Kestrel14 raises $0.65 to $0.75
            Vik Storm folds
            Uncalled bet ($0.50) returned to Kestrel14
            Kestrel14 does not show
            *** SUMMARY ***
            Total pot $0.50
            Seat 2: Vik Storm (big blind) folded on the Pre-Flop
            Seat 5: Sundialler (button) folded on the Pre-Flop
            Seat 6: Kestrel14 did not show and won $0.50
            """;
        var ctx = TestUtil.Context(text);
        var axel = ctx.Players.Single(p => p.PlayerName == "Kestrel14");
        Assert.Equal(25, axel.Invested);   // 0.10 + 0.65 − 0.50
        Assert.Equal(50, axel.Collected);
        Assert.Equal(25, axel.NetWon);
        Assert.Equal(65, ctx.Actions.Single(a => a.Action == ActionType.Raise).Amount);
    }

    [Fact]
    public void TournamentHandParsesAntesSidePotsAndDeadButton()
    {
        var h = TestUtil.Parse(SampleHands.TourneyDeadButton, GameKind.Mtt);
        Assert.Equal(40000001, h.TournamentId);
        Assert.Equal(2, h.TournamentLevel);
        Assert.Equal("CHP", h.Currency);
        Assert.Equal(2500, h.Ante);
        Assert.True(h.Has(HandFlags.DeadButton));
        Assert.False(h.SeatByName("Gone")!.Dealt);
        Assert.Equal(3, h.Seats.Count(s => s.Dealt));

        var ctx = HandContextBuilder.Build(h);
        Assert.Empty(Invariants.Check(h, ctx));
        Assert.Equal(Position.SB, ctx.Players.Single(p => p.PlayerName == "12345").Position);
        Assert.Equal(Position.BB, ctx.Players.Single(p => p.PlayerName == "Nina Wolf").Position);
        Assert.Equal(Position.BTN, ctx.Players.Single(p => p.PlayerName == "Hero").Position);
    }

    [Fact]
    public void HeadsUpButtonIsSmallBlind()
    {
        var ctx = TestUtil.Context(SampleHands.HeadsUpShowdown);
        Assert.Equal(Position.BTN, ctx.Players.Single(p => p.PlayerName == "Btn").Position);
        Assert.Equal(Position.BB, ctx.Players.Single(p => p.PlayerName == "Bb").Position);
        var flopBet = ctx.Decisions.First(d => d.Street == Street.Flop);
        Assert.Equal("Bb", flopBet.PlayerName);
        Assert.False(flopBet.IsIp);
        Assert.True(ctx.Decisions.First(d => d.Street == Street.Flop && d.PlayerName == "Btn").IsIp);
    }

    [Fact]
    public void HandStrengthIsAssignedFromShownCards()
    {
        var ctx = TestUtil.Context(SampleHands.HeadsUpShowdown);
        var bbFlop = ctx.Decisions.First(d => d.PlayerName == "Bb" && d.Street == Street.Flop);
        var bbTurn = ctx.Decisions.First(d => d.PlayerName == "Bb" && d.Street == Street.Turn);
        Assert.Equal((short)HandCategory.Pair, bbFlop.HandStrength);
        Assert.Equal((short)HandCategory.Trips, bbTurn.HandStrength);
    }

    [Fact]
    public void UnknownLineRejectsHand()
    {
        var bad = SampleHands.HeadsUpShowdown.Replace("Btn calls $0.10", "Btn hums a tune");
        var r = new WpnHandParser().Parse(bad, new HandSourceInfo("t")).Single();
        Assert.False(r.Ok);
        Assert.Contains("Unknown line", r.Error);
    }

    [Fact]
    public void IncompleteHandWithoutSummaryIsRejected()
    {
        var cut = SampleHands.HeadsUpShowdown[..SampleHands.HeadsUpShowdown.IndexOf("*** SUMMARY", StringComparison.Ordinal)];
        var r = new WpnHandParser().Parse(cut, new HandSourceInfo("t")).Single();
        Assert.False(r.Ok);
    }

    [Fact]
    public void FileNameMetadata()
    {
        Assert.True(WpnFileNameParser.TryParse(
            "HH20240103 CASHID-G40000003T935 TN-Demorest GAMETYPE-Hold'em LIMIT-no CUR-REAL OND-F BUYIN-0 MIN-10 MAX-25.txt", out var fi));
        Assert.Equal(GameKind.Cash, fi.GameKind);
        Assert.Equal("G40000003", fi.GameId);
        Assert.Equal("T935", fi.TableId);
        Assert.Equal("Demorest", fi.TableOrTournamentName);
        Assert.Equal(25m, fi.MaxBuyIn);

        Assert.True(WpnFileNameParser.TryParse(
            "HH20240104 CASHID-G40000004T390 TN-Ste{FULLSTOP} Marie GAMETYPE-Hold'em LIMIT-no CUR-REAL OND-F BUYIN-0 MIN-10 MAX-25.txt", out fi));
        Assert.Equal("Ste. Marie", fi.TableOrTournamentName);

        Assert.True(WpnFileNameParser.TryParse(
            "HH20240102 SITGOID-G40000005T1 TN-$15 NLH Turbo - On Demand GAMETYPE-Hold'em LIMIT-no CUR-REAL OND-T BUYIN-0.txt", out fi));
        Assert.Equal(GameKind.Sng, fi.GameKind);
        Assert.True(fi.OnDemand);

        // Спины: свой префикс SPINZID в имени файла
        Assert.True(WpnFileNameParser.TryParse(
            "HH20240104 SPINZID-G40000006T1 TN-$5 Spinz Holdem GAMETYPE-Hold'em LIMIT-no CUR-REAL OND-F BUYIN-500.txt", out fi));
        Assert.Equal(GameKind.Sng, fi.GameKind);
        Assert.Equal("$5 Spinz Holdem", fi.TableOrTournamentName);
    }

    [Fact]
    public void AmountsAreParsedInvariantCulture()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            Assert.Equal(125, WpnHandParser.Amount("$1.25"));
            Assert.Equal(123456700, WpnHandParser.Amount("1,234,567.00"));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }
}

/// <summary>Прогон по всему реальному корпусу (§15.12): 0 ошибок парсера, инварианты денег и позиций.</summary>
public class CorpusTests
{
    [Fact]
    public void WholeCorpusParsesAndSatisfiesInvariants()
    {
        if (!TestUtil.HasCorpus) return;
        var dir = TestUtil.CorpusDir; // корпус лежит только на машине разработчика

        var parser = new WpnHandParser();
        int hands = 0;
        var failures = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "HH*.txt", SearchOption.AllDirectories))
        {
            foreach (var r in parser.Parse(File.ReadAllText(f, Encoding.UTF8), WpnFileNameParser.ToSource(f)))
            {
                hands++;
                if (!r.Ok) { failures.Add($"parse @{r.Offset} {Path.GetFileName(f)}: {r.Error}"); continue; }
                var ctx = HandContextBuilder.Build(r.Hand!);
                foreach (var e in Invariants.Check(r.Hand!, ctx)) failures.Add($"#{r.Hand!.ExternalHandId}: {e}");
            }
        }
        Assert.True(hands > 0);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void AllInEvIsConservedAcrossCorpus()
    {
        if (!TestUtil.HasCorpus) return;
        var dir = TestUtil.CorpusDir;
        var parser = new WpnHandParser();
        int evHands = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "HH*.txt", SearchOption.AllDirectories))
        foreach (var r in parser.Parse(File.ReadAllText(f), WpnFileNameParser.ToSource(f)).Where(x => x.Ok))
        {
            var ctx = HandContextBuilder.Build(r.Hand!, computeEv: true);
            if (ctx.Players.All(p => p.EvNetWon == null)) continue;
            evHands++;
            // EV перераспределяет банк, но не создаёт и не уничтожает деньги: Σ EV == Σ факт (±округление)
            long diff = ctx.Players.Sum(p => p.EvOrNetWon) - ctx.Players.Sum(p => p.NetWon);
            Assert.InRange(diff, -ctx.Players.Count, ctx.Players.Count);
        }
        Assert.True(evHands > 0);
    }

    [Fact]
    public void RunItTwiceHandHasTwoBoardsAndSplitsCollected()
    {
        if (!TestUtil.HasCorpus) return;
        var dir = TestUtil.CorpusDir;
        var parser = new WpnHandParser();
        var rit = Directory.EnumerateFiles(dir, "HH*.txt", SearchOption.AllDirectories)
            .SelectMany(f => parser.Parse(File.ReadAllText(f), WpnFileNameParser.ToSource(f)))
            .Where(r => r.Ok && r.Hand!.Has(HandFlags.RunItTwice))
            .Select(r => r.Hand!)
            .ToList();
        Assert.NotEmpty(rit);
        foreach (var h in rit)
        {
            Assert.Equal(2, h.Boards.Count);
            Assert.All(h.Boards, b => Assert.Equal(5, b.Count));
            Assert.Equal(h.TotalPot, h.Seats.Sum(s => s.Won));
        }
    }
}
