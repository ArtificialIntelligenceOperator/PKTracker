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

    /// <summary>Как в файле этого формата понять, что раздача дописана до конца (импорт дописываемых файлов).</summary>
    HandBoundary Boundary { get; }
}

/// <summary>
/// Границы раздачи в файле истории. Клиент рума дописывает файл во время игры, и импорт обязан
/// брать только завершённые раздачи; признаки завершённости у форматов разные, поэтому их задаёт парсер.
/// </summary>
/// <param name="Starts">С чего начинается строка-заголовок раздачи.</param>
/// <param name="EndMarker">
/// Текстовые форматы: метка итогов («*** SUMMARY ***») — после неё ещё идут строки, поэтому раздача
/// считается дописанной, только когда началась следующая или файл перестал меняться.
/// При <paramref name="ClosedByMarker"/> — закрывающий тег, которым раздача заканчивается (iPoker: «&lt;/game&gt;»).
/// </param>
/// <param name="ClosedByMarker">Раздача завершена ровно тогда, когда встретился <paramref name="EndMarker"/>.</param>
/// <param name="WholeFile">
/// Дописанный кусок нельзя разбирать отдельно от начала файла (в iPoker ник и турнир — в заголовке сессии):
/// дозор перечитывает файл целиком, дубликаты отсеивает импорт.
/// </param>
public sealed record HandBoundary(string[] Starts, string EndMarker, bool ClosedByMarker = false, bool WholeFile = false);

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
