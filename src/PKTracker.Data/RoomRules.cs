namespace PKTracker.Data;

/// <summary>
/// Что правила рума разрешают показывать за столом. Трекер (импорт, отчёты, реплеер) разрешён везде;
/// ограничивается только оверлей HUD во время игры. Сверено с официальными текстами на 10.11.2026:
/// iPoker — redstarpoker.eu/play_poker/prohibited_software; Winamax — регламент покера от 31.01.2023,
/// п. 1.2; WPN — acrpoker.eu/security, раздел External Assistance Tools. Правила меняются без
/// предупреждения — ответственность за соблюдение на игроке (см. docs/ROOM_RULES.md).
/// </summary>
public sealed record RoomHudPolicy(
    bool HudAllowed,
    bool StaticOnly,
    string Summary)
{
    /// <summary>
    /// Для рума. StaticOnly: за столом только числовые статы — без диаграмм диапазонов по вскрытым картам,
    /// позиционного/динамического HUD и автоматических меток (их в PKTracker и нет).
    /// </summary>
    public static RoomHudPolicy For(string roomCode) => roomCode.ToUpperInvariant() switch
    {
        "GGPOKER" => new(false, true,
            "GGPoker запрещает сторонние HUD за столом (есть встроенный Smart HUD). Импорт и анализ раздач — можно."),
        "IPOKER" => new(true, true,
            "iPoker: только статичный HUD из своих раздач. Нельзя: динамический/позиционный HUD, диаграммы и статы по известным картам, автометки, значки, советы."),
        "WINAMAX" => new(true, true,
            "Winamax: личная статистика по своим раздачам разрешена. Нельзя: солверы, ICM/эквити-калькуляторы, «динамические» диапазоны, скрипты рассадки."),
        "POKERSTARS" => new(true, true,
            "PokerStars: только статичный HUD; без диапазонов по вскрытым картам и динамических элементов во время игры."),
        "888POKER" => new(true, true,
            "888poker: статистика по своим раздачам; без советов и калькуляторов во время игры."),
        _ => new(true, false,
            "WPN (ACR, PokerKing, Black Chip, True Poker): числовые статы по своим раздачам. Нельзя: информация о текущей раздаче (шансы, ауты, эквити), RTA, солверы, сбор чужих раздач, скрипты рассадки."),
    };
}
