using PKTracker.Core.Model;
using PKTracker.Data;

namespace PKTracker.Tests;

/// <summary>
/// Формат турнира — ключ группировки статистики во вкладке «Турниры»: по нему считаются
/// количество сыгранных, ROI и Chip EV. Ошибка в ключе смешивает разные турниры в одну строку.
/// </summary>
public class TournamentFormatTests
{
    private static TournamentRow Row(long? buyIn, long? fee, string? currency, int? players,
        GameKind? kind = null, int? maxSeats = null, bool ko = false, short koKind = 0) =>
        new(1, null, DateTime.UtcNow, 0, buyIn, fee, currency, 1, null, 0, 0, 0, players, kind, maxSeats, ko, koKind);

    [Fact]
    public void BuyInIsCostOfOneEntry()
    {
        // 5 + 0,50 — один формат, 5 + 0 — другой: рейк входит в стоимость входа
        Assert.Equal("$5.50 MTT", Row(500, 50, "USD", 46, GameKind.Mtt).FormatLabel);
        Assert.Equal("$5.00 MTT", Row(500, 0, "USD", 46, GameKind.Mtt).FormatLabel);
    }

    [Fact]
    public void SpinAndGoIsItsOwnFormat()
    {
        // Стол на троих: 100% первому, $EV по таким турнирам не считается — только Chip EV
        Assert.Equal("$5.00 Spin & Go", Row(500, 0, "USD", 3, GameKind.Sng, 3).FormatLabel);
        Assert.Equal("$5.00 Spin & Go", Row(500, 0, "USD", 3, GameKind.Sng).FormatLabel);
        Assert.Equal("$11.00 SNG 8-max", Row(1000, 100, "USD", 18, GameKind.Sng, 8).FormatLabel);
    }

    [Fact]
    public void KindFallsBackToFieldSizeWithoutHands()
    {
        // Раздач нет (game_kind неизвестен) — тип определяем по размеру поля из .ots
        Assert.Equal("$1.00 SNG", Row(100, 0, "USD", 6, null).FormatLabel);
        Assert.Equal("$1.00 MTT", Row(100, 0, "USD", 120, null).FormatLabel);
        Assert.Equal("$1.00 турнир", Row(100, 0, "USD", null, null).FormatLabel);
    }

    [Fact]
    public void KnockoutAndTicketCurrencyAreSeparateFormats()
    {
        Assert.Equal("$33.00 MTT PKO", Row(3000, 300, "USD", 200, GameKind.Mtt, ko: true).FormatLabel);
        Assert.Equal("$33.00 MTT Mystery KO", Row(3000, 300, "USD", 200, GameKind.Mtt, ko: true, koKind: 2).FormatLabel);
        // Билетные турниры не смешиваются с долларовыми: призы в .ots там нулевые
        Assert.Equal("$215.00 MTT 8-max TTS", Row(20000, 1500, "TTS", 775, GameKind.Mtt, 8).FormatLabel);
    }

    [Fact]
    public void UnknownBuyInDoesNotPretendToBeFree()
    {
        // Бай-ин берётся из имени файла .ots; без него формат честно помечается как неизвестный
        Assert.Equal("бай-ин ? MTT", Row(null, null, "USD", 46, GameKind.Mtt).FormatLabel);
    }
}
