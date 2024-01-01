using PKTracker.Core.Model;

namespace PKTracker.Core.Abstractions;

/// <summary>Контракт L1: всё специфичное для рума живёт за этим интерфейсом (§9).</summary>
public interface IHandHistoryParser
{
    string RoomCode { get; }

    /// <summary>Узнаёт свой формат по началу файла.</summary>
    bool CanParse(ReadOnlySpan<char> fileHead);

    /// <summary>Разбирает текст (с \n или \r\n). Смещения — в символах от начала <paramref name="text"/> + baseOffset.</summary>
    IEnumerable<HandParseResult> Parse(string text, HandSourceInfo source, long baseOffset = 0);
}

/// <summary>Живой канал рассадки (только "кто где сидит" — §0.2, §0.4).</summary>
public interface ILiveTableFeed
{
    event Action<LiveTableSeating>? SeatingChanged;
}

public sealed record LiveSeat(int Seat, string Name, Guid? ExternalId, string? Country, bool SitIn);

/// <summary>
/// Рассадка стола. Seat — в нумерации HH (1-based, гипотеза §0.4 hh = data + 1 — проверяется
/// флагом <see cref="SeatOffset"/>), MySeat — место Hero.
/// </summary>
public sealed record LiveTableSeating(long GameNumber, int TableNumber, int MaxSeats, int? MySeat,
    IReadOnlyList<LiveSeat> Seats, int SeatOffset, DateTime ReceivedUtc);
