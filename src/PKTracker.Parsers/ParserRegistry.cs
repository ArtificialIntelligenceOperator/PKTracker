using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Parsers.GgPoker;
using PKTracker.Parsers.IPoker;
using PKTracker.Parsers.Poker888;
using PKTracker.Parsers.PokerStars;
using PKTracker.Parsers.Winamax;
using PKTracker.Parsers.Wpn;

namespace PKTracker.Parsers;

/// <summary>Рум, который понимает трекер: код в базе, сеть (общий формат истории) и подпись в интерфейсе.</summary>
public sealed record RoomInfo(string Code, string Network, string Title);

/// <summary>
/// Единственное место, которое знает, какой парсер отвечает за какой рум. Уровни выше получают
/// <see cref="IHandHistoryParser"/> и ничего не знают о формате.
/// </summary>
public static class ParserRegistry
{
    private static readonly List<(RoomInfo Room, Func<string, IHandHistoryParser> Create)> Entries = new()
    {
        (new RoomInfo("POKERKING", "WPN", "PokerKing (WPN)"), code => new WpnHandParser(code)),
        (new RoomInfo("ACR", "WPN", "Americas Cardroom (WPN)"), code => new WpnHandParser(code)),
        (new RoomInfo("BLACKCHIP", "WPN", "Black Chip Poker (WPN)"), code => new WpnHandParser(code)),
        (new RoomInfo("TRUEPOKER", "WPN", "True Poker (WPN)"), code => new WpnHandParser(code)),
        (new RoomInfo("POKERSTARS", "POKERSTARS", "PokerStars"), _ => new StarsHandParser()),
        (new RoomInfo("WINAMAX", "WINAMAX", "Winamax"), _ => new WinamaxHandParser()),
        (new RoomInfo("GGPOKER", "GG", "GGPoker / PokerOK"), _ => new GgPokerHandParser()),
        (new RoomInfo("888POKER", "888", "888poker"), _ => new Poker888HandParser()),
        (new RoomInfo("IPOKER", "IPOKER", "iPoker"), _ => new IPokerHandParser()),
    };

    /// <summary>Подключение рума из другой сборки/формата без правок вызывающего кода.</summary>
    public static void Register(RoomInfo room, Func<string, IHandHistoryParser> create)
    {
        Entries.RemoveAll(e => e.Room.Code == room.Code);
        Entries.Add((room, create));
    }

    public static IReadOnlyList<RoomInfo> Rooms => Entries.Select(e => e.Room).ToList();

    public static RoomInfo? Room(string code) => Entries.FirstOrDefault(e => string.Equals(e.Room.Code, code, StringComparison.OrdinalIgnoreCase)).Room;

    /// <summary>Парсер активного рума: все раздачи одного рума в базе — одного формата (нужно ребилду из сырого текста).</summary>
    public static IHandHistoryParser ForRoom(string roomCode)
    {
        foreach (var (room, create) in Entries)
            if (string.Equals(room.Code, roomCode, StringComparison.OrdinalIgnoreCase)) return create(room.Code);
        throw new ArgumentException($"Неизвестный рум: {roomCode}");
    }

    /// <summary>
    /// Определяет формат файла по его началу. Скины одной сети (WPN) неразличимы по тексту —
    /// для них возвращается парсер активного рума, если он из той же сети.
    /// </summary>
    public static (IHandHistoryParser Parser, RoomInfo Room)? Detect(ReadOnlySpan<char> head, string activeRoomCode)
    {
        var active = Room(activeRoomCode);
        // Сначала активный рум: так скин сети WPN получает свой код, а не первый попавшийся.
        if (active != null)
        {
            var p = ForRoom(active.Code);
            if (p.CanParse(head)) return (p, active);
        }
        foreach (var (room, create) in Entries)
        {
            if (active != null && room.Code == active.Code) continue;
            var p = create(room.Code);
            if (p.CanParse(head)) return (p, room);
        }
        return null;
    }

    /// <summary>Сеть, к которой относится раздача, совпадает с сетью активного рума.</summary>
    public static bool SameNetwork(RoomInfo a, string activeRoomCode) =>
        Room(activeRoomCode) is { } r && r.Network == a.Network && (a.Network == "WPN" || a.Code == r.Code);

    /// <summary>Расширения файлов истории: текстовые форматы и XML (iPoker).</summary>
    public static bool LooksLikeHistoryFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Метаданные из имени файла: у WPN оттуда берётся тип игры и id стола, у остальных — пусто.</summary>
    public static HandSourceInfo SourceFor(string path, string? heroHint = null)
    {
        if (WpnFileNameParser.TryParse(path, out _)) return WpnFileNameParser.ToSource(path, heroHint);
        var name = Path.GetFileName(path);
        // PokerStars: «HH20200101 T1234567 No Limit Hold'em $9 + $1.txt»; SNG/Spin в имени не отличить от MTT надёжно.
        GameKind? kind = name.Contains("Spin", StringComparison.OrdinalIgnoreCase) || name.Contains("Sit & Go", StringComparison.OrdinalIgnoreCase)
            ? GameKind.Sng : null;
        return new HandSourceInfo(path, kind, HeroHint: heroHint);
    }
}
