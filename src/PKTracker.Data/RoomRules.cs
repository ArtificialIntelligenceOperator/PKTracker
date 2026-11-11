using System.Diagnostics;

namespace PKTracker.Data;

/// <summary>
/// Что правила рума разрешают трекеру. Сверено 11.11.2026 с текстами румов и с ограничениями, которые
/// Hand2Note включает у себя на тех же румах (hand2note.com/Help/supported-sites, раздел «HUD restrictions»):
/// WPN — политика Security and Game Integrity, п. 4.1.3, 4.2, 4.3, 9.5, 10; iPoker —
/// redstarpoker.eu/play_poker/prohibited_software; PokerStars — Third Party Tools and Services Policy
/// (Reference Guide); Winamax — регламент покера, раздел «Aide extérieure au jeu»; 888 — User Agreement,
/// приложение Permitted Aids; GG — Security &amp; Ecology Policy и запрет стороннего софта от 15.10.2026.
/// Правила меняются без предупреждения — ответственность за соблюдение на игроке (docs/ROOM_RULES.md).
/// </summary>
public sealed record RoomHudPolicy
{
    /// <summary>Оверлей над столом разрешён вообще.</summary>
    public bool HudAllowed { get; init; } = true;

    /// <summary>
    /// «Статичный» HUD: за столом только числовые статы. Скрываются диапазоны и сила руки по вскрытым картам,
    /// статы и разбивки, зависящие от карт (сила руки, текстура борда), а цвет значения — не больше трёх диапазонов.
    /// Автометок, значков и динамического/позиционного HUD в PKTracker нет вовсе.
    /// </summary>
    public bool StaticOnly { get; init; }

    /// <summary>
    /// Ограничения действуют не только на оверлей, но и на реплеер и окна анализа, пока клиент рума запущен.
    /// Так у PokerStars: трекер остаётся разрешённым, только если сам урезает запрещённое при открытом клиенте.
    /// </summary>
    public bool StrictWhileClientOpen { get; init; }

    /// <summary>
    /// Рум запрещает держать сторонний покерный софт запущенным рядом со своим клиентом (GG с 01.12.2026:
    /// клиент сканирует процессы, даже если программа используется для другого рума). Трекер из-за этого
    /// не останавливается — играют на нескольких румах сразу, — но, пока такой клиент открыт, с его сетью
    /// не взаимодействует вовсе: импорт её раздач закрыт, а в журнал пишется напоминание о риске.
    /// </summary>
    public bool NoCoexistence { get; init; }

    /// <summary>
    /// Рум запрещает сторонним программам импортировать его истории раздач (GG, Security &amp; Ecology Policy):
    /// импорт только после явного включения игроком — на его риск.
    /// </summary>
    public bool ImportNeedsOptIn { get; init; }

    /// <summary>Имена процессов клиента рума (без .exe) — чтобы понять, запущен ли он.</summary>
    public IReadOnlyList<string> ClientProcesses { get; init; } = Array.Empty<string>();

    public string Summary { get; init; } = "";

    /// <summary>Сколько цветовых диапазонов можно дать значению стата за столом.</summary>
    public int MaxColorRanges => StaticOnly ? 3 : int.MaxValue;

    /// <summary>
    /// Действуют ли ограничения «статичного» HUD для окна прямо сейчас: над столом — всегда, в реплеере
    /// и окнах анализа — только на румах с <see cref="StrictWhileClientOpen"/> и только при запущенном клиенте.
    /// </summary>
    public bool RestrictedNow(bool overlay) =>
        StaticOnly && (overlay || (StrictWhileClientOpen && RoomClientProbe.AnyRunning(ClientProcesses)));

    private static readonly RoomHudPolicy Gg = new()
    {
        HudAllowed = false, StaticOnly = true, NoCoexistence = true, ImportNeedsOptIn = true,
        // GGnet.exe — клиент всех скинов сети; остальные имена — на случай скина со своим загрузчиком.
        ClientProcesses = new[] { "GGnet", "GGPoker", "PokerOK", "Natural8" },
        Summary = "GG (GGPoker, PokerOK): сторонний HUD запрещён — над столами GG его нет. С 01.12.2026 клиент GG сканирует процессы " +
                  "и запрещает любой сторонний покерный софт рядом с собой: PKTracker продолжает работать для других румов, но это ваш риск. " +
                  "Политика GG запрещает и сторонний импорт раздач (в том числе из PokerCraft): он выключен, включается на вкладке «Импорт» " +
                  "на ваш риск и не идёт, пока клиент GG открыт.",
    };

    private static readonly RoomHudPolicy IPoker = new()
    {
        StaticOnly = true,
        Summary = "iPoker: только статичный HUD из своих раздач. За столом скрыты диапазоны и статы по известным картам; автометок, " +
                  "значков, позиционного/динамического HUD нет. При запущенном клиенте нельзя держать открытыми эквити-калькуляторы, " +
                  "солверы и чарты. Трекеры «не запрещены, но и не рекомендованы» — на риск игрока.",
    };

    private static readonly RoomHudPolicy Winamax = new()
    {
        StaticOnly = true, ClientProcesses = new[] { "Winamax" },
        Summary = "Winamax: личная статистика по своим раздачам разрешена, динамический HUD — закрытие счёта. PKTracker нет в списке " +
                  "разрешённых программ: до игры получите согласие службы Intégrité (integrity@winamax.com). Winamax проверяет " +
                  "запущенные процессы. Нельзя: солверы, ICM/эквити-калькуляторы, скрипты рассадки.",
    };

    private static readonly RoomHudPolicy Stars = new()
    {
        StaticOnly = true, StrictWhileClientOpen = true, ClientProcesses = new[] { "PokerStars" },
        Summary = "PokerStars: статичный числовой HUD по своим раздачам. Нельзя: статы и разбивки по картам (сила руки, текстура борда), " +
                  "HUD, меняющийся по позиции/стеку/действиям, автоматические метки, больше трёх цветов у стата. Пока клиент открыт, " +
                  "те же ограничения действуют в реплеере. PKTracker нет в списке разрешённых программ Stars — уточните в поддержке.",
    };

    private static readonly RoomHudPolicy P888 = new()
    {
        StaticOnly = true,
        Summary = "888poker: разрешён трекер, показывающий статистику по своим раздачам; запрещены советы в реальном времени и любые " +
                  "помощники с ИИ. На столах SNAP (fast-fold) сторонний HUD запрещён — их раздачи PKTracker не принимает.",
    };

    private static readonly RoomHudPolicy Wpn = new()
    {
        ClientProcesses = new[] { "PokerKing", "AmericasCardroom", "ACRPoker", "BlackChipPoker", "TruePoker" },
        Summary = "WPN (ACR, PokerKing, Black Chip, True Poker), п. 4.1.3: HUD с числовыми статами только по своим раздачам и без сведений " +
                  "о текущей раздаче. Нельзя: RTA, солверы и ICM вживую, чарты с автоподбором, сбор чужих раздач, скрипты рассадки, " +
                  "перекрытие графики стола (4.2.11 — не ставьте панели на карты, банк и кнопки), виртуальные машины и удалённый доступ " +
                  "(TeamViewer, AnyDesk, RDP — 4.3). Клиент проверяет запущенные программы (9.5).",
    };

    public static RoomHudPolicy For(string roomCode) => roomCode.ToUpperInvariant() switch
    {
        "GGPOKER" => Gg,
        "IPOKER" => IPoker,
        "WINAMAX" => Winamax,
        "POKERSTARS" => Stars,
        "888POKER" => P888,
        _ => Wpn,
    };

    /// <summary>Все известные политики — чтобы найти запущенный клиент любого рума.</summary>
    public static IReadOnlyList<RoomHudPolicy> All { get; } = new[] { Wpn, Stars, Winamax, IPoker, P888, Gg };
}

/// <summary>
/// Запущен ли клиент рума. Нужен трём правилам: пока открыт клиент GG, трекер не трогает ничего, что относится
/// к её сети; PokerStars распространяет ограничения HUD на реплеер при открытом клиенте; а анализ через MCP
/// (ИИ-ассистент) во время игры — это real-time assistance на любом руме.
/// </summary>
public static class RoomClientProbe
{
    private static readonly object Gate = new();
    private static DateTime _at;
    private static HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Источник имён процессов; подменяется в тестах.</summary>
    public static Func<IEnumerable<string>> ProcessSource { get; set; } = SystemProcesses;

    private static IEnumerable<string> SystemProcesses()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch (InvalidOperationException) { }   // процесс завершился, пока шёл перебор
            finally { p.Dispose(); }
        }
        return names;
    }

    /// <summary>Сбросить кеш — после подмены <see cref="ProcessSource"/>.</summary>
    public static void Reset()
    {
        lock (Gate) _at = default;
    }

    private static HashSet<string> Snapshot()
    {
        lock (Gate)
        {
            // Перебор процессов стоит десятки миллисекунд, а спрашивают о нём из UI-потока при каждом попапе.
            if (DateTime.UtcNow - _at < TimeSpan.FromSeconds(2)) return _names;
            try { _names = new HashSet<string>(ProcessSource(), StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            _at = DateTime.UtcNow;
            return _names;
        }
    }

    public static string? FirstRunning(IEnumerable<string> names)
    {
        var running = Snapshot();
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(n) && running.Contains(n.Trim())) return n.Trim();
        return null;
    }

    public static bool AnyRunning(IEnumerable<string> names) => FirstRunning(names) != null;

    /// <summary>Запущенный клиент рума, запрещающего сторонний покерный софт рядом с собой (GG); null — такого нет.</summary>
    public static string? ForbiddenCoexistence() =>
        FirstRunning(RoomHudPolicy.All.Where(p => p.NoCoexistence).SelectMany(p => p.ClientProcesses));

    /// <summary>Клиент любого известного рума (плюс имя из настроек); null — ни один не запущен.</summary>
    public static string? AnyRoomClient(string? configuredProcess) =>
        FirstRunning(RoomHudPolicy.All.SelectMany(p => p.ClientProcesses).Append(configuredProcess ?? ""));
}
