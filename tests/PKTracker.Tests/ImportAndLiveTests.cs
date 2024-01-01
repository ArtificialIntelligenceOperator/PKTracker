using System.Text;
using PKTracker.Data.Import;
using PKTracker.Live;
using PKTracker.Tournaments;

namespace PKTracker.Tests;

public class CompleteHandsTests
{
    private static readonly string Two = SampleHands.HeadsUpShowdown.Replace("\r\n", "\n") + "\n\n" +
                                         SampleHands.StealCbetCheckRaise.Replace("\r\n", "\n") + "\n\n";

    [Fact]
    public void HandFollowedByNextHandIsComplete()
    {
        var bytes = Encoding.UTF8.GetBytes(Two);
        int second = Two.IndexOf("\nHand #1002", StringComparison.Ordinal);
        // Файл активно пишется: вторая раздача ещё может дописываться
        Assert.Equal(second + 1, CompleteHands.CompleteLength(bytes, fileQuiet: false));
        // Файл не менялся ≥ 1 с и последний блок содержит SUMMARY — забираем всё
        Assert.Equal(bytes.Length, CompleteHands.CompleteLength(bytes, fileQuiet: true));
    }

    [Fact]
    public void TruncatedTailIsLeftForNextTick()
    {
        var cut = Two[..Two.LastIndexOf("*** SUMMARY", StringComparison.Ordinal)];
        var bytes = Encoding.UTF8.GetBytes(cut);
        int second = Two.IndexOf("\nHand #1002", StringComparison.Ordinal);
        Assert.Equal(second + 1, CompleteHands.CompleteLength(bytes, fileQuiet: true));
    }

    [Fact]
    public void SingleIncompleteHandYieldsNothing()
    {
        var one = SampleHands.HeadsUpShowdown.Replace("\r\n", "\n");
        var bytes = Encoding.UTF8.GetBytes(one[..one.IndexOf("*** SUMMARY", StringComparison.Ordinal)]);
        Assert.Equal(0, CompleteHands.CompleteLength(bytes, fileQuiet: true));
        Assert.Equal(0, CompleteHands.CompleteLength(bytes, fileQuiet: false));
    }
}

public class LiveFeedTests
{
    private static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void DecodesSeatingAndShiftsSeatsToHhNumbering()
    {
        var d = new PokerKingDataDecoder(seatOffset: 1);
        var s = d.Feed(B64("""
            {"header":{"type":"TableState","channel":1},"payload":{"gameNumber":40000007,"tableNumber":20,"maxSeatCount":8,"mySeat":3,
             "sitters":[{"seat":2,"name":"riverbend7","id":"11111111-2222-3333-4444-555555555551","country":"BRA","sitIn":true},
                        {"seat":3,"name":"Hero","id":"11111111-2222-3333-4444-555555555552","country":"CAN","sitIn":true}]}}
            """));
        Assert.NotNull(s);
        Assert.Equal(4, s!.MySeat);
        Assert.Equal(new[] { 3, 4 }, s.Seats.Select(x => x.Seat));
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555551"), s.Seats[0].ExternalId);

        s = d.Feed(B64("""{"header":{"type":"EmptySeat","channel":1},"payload":{"seat":2,"movedTable":25}}"""));
        Assert.Single(s!.Seats);
    }

    [Fact]
    public void IgnoresCurrentHandInformationAndGarbage()
    {
        var d = new PokerKingDataDecoder();
        Assert.Null(d.Feed("!!!not base64!!!"));
        Assert.Null(d.Feed(B64("""{"header":{"type":"HoleCards"},"payload":{"holeCards":[{"seat":3,"cards":["5c","8c"]}]}}""")));
        Assert.Null(d.Feed(B64("""{"header":{"type":"HandEquities"},"payload":{"equities":{"3":"41 / 50"}}}""")));
    }

    [Fact]
    public void RealDataFileHasSeating()
    {
        // Любой .data из корпуса: имя файла содержит ник, поэтому оно не зашито в тест.
        var f = Directory.Exists(TestUtil.CorpusDir)
            ? Directory.EnumerateFiles(TestUtil.CorpusDir, "*.data", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (f == null) return;
        var d = new PokerKingDataDecoder();
        var last = File.ReadLines(f).Select(d.Feed).FirstOrDefault(x => x != null); // первая рассадка — TableState; позже Hero пересаживают
        Assert.NotNull(last);
        Assert.InRange(last!.MaxSeats, 2, 10);
        // Ради этого тест и нужен: сдвиг нумерации мест .data → HH должен попадать в живое место Hero.
        Assert.Contains(last.Seats, s => s.Seat == last.MySeat);
    }
}

public class TournamentSummaryTests
{
    [Fact]
    public void ParsesReentriesTicketsAndBuyInFromFileName()
    {
        const string json = """
            {"spec_version":"1.0.0","network_name":"WinningPokerNetwork","site_name":"PokerKing","tournament_number":"T#40000002",
             "start_date_utc":"2024-01-01T16:30:00Z","prize_pool":15000,"player_count":332,"currency":"TTS",
             "tournament_finishes_and_winnings":[
               {"player_name":"alpha","finish_position":1,"prize":0,"ticket_value":215},
               {"player_name":"beta","finish_position":2,"prize":100.5,"ticket_value":0},
               {"player_name":"alpha","finish_position":250,"prize":0,"ticket_value":0}]}
            """;
        var s = TournamentSummaryParser.Parse(json, @"C:\x\TS20240101 T40000002 Mystery Bounty NL Hold'em $30.00 + $3.00.ots");
        Assert.Equal(40000002, s.TournamentId);
        Assert.Equal("TTS", s.Currency);
        Assert.Equal(1_500_000, s.PrizePool);
        Assert.Equal(3000, s.BuyIn);
        Assert.Equal(300, s.Fee);
        Assert.True(s.IsMystery);
        var alpha = s.Finishes.Where(f => f.PlayerName == "alpha").OrderBy(f => f.EntryNo).ToList();
        Assert.Equal(2, alpha.Count);
        Assert.Equal(250, alpha[0].FinishPosition); // первый вход вылетел раньше
        Assert.Equal(1, alpha[1].FinishPosition);
        Assert.Equal(21500, alpha[1].TicketValue);
        Assert.Equal(10050, s.Finishes.Single(f => f.PlayerName == "beta").Prize);
    }
}
