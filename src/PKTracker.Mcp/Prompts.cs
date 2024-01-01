using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKTracker.Mcp;

/// <summary>Готовые сценарии: пользователь выбирает их в клиенте (Claude Desktop: «+» → pktracker).</summary>
[McpServerPromptType]
public static class Prompts
{
    [McpServerPrompt(Name = "find_my_leaks", Title = "Найти мои лики")]
    [Description("Разбор игры Hero: ликфайндер, позиции, EV, примеры рук по каждому найденному лику.")]
    public static string FindMyLeaks([Description("cash или mtt")] string game = "cash") => $"""
        Проанализируй мою игру ({game}) в PKTracker.
        1. tracker_status и get_overview(game="{game}"): выборка, winrate, All-in EV, разбивка по позициям.
        2. find_leaks(game="{game}"): выбери 3–5 самых значимых отклонений с достаточной выборкой.
        3. По каждому: get_stat_details(player=<мой ник>, stat_code=..., game="{game}") и 2–3 конкретные руки через
           search_hands + get_hand. Объясни, почему это минус в EV, а не просто «отличие от пула».
        4. Итог: приоритетный список исправлений с ожидаемым эффектом. Не выдумывай числа — только данные из инструментов.
        """;

    [McpServerPrompt(Name = "review_session", Title = "Разбор сессии")]
    [Description("Разбор последней кеш-сессии: ключевые руки, крупные проигрыши, олл-ины против EV.")]
    public static string ReviewSession() => """
        Разбери мою последнюю кеш-сессию в PKTracker.
        1. list_sessions(limit=3): возьми последнюю, запомни её интервал.
        2. search_hands с фильтром 'NetWonBbX100 <= -2000 || NetWonBbX100 >= 2000' (крупные банки) и сопоставь с интервалом сессии.
        3. Для 5–8 крупнейших рук вызови get_hand и дай разбор по улицам: что было хорошо, где ошибка, какая линия лучше.
        4. Отдельно: олл-ины, где EvNetWon сильно отличается от NetWon (удача vs решения).
        Разбирай только сыгранные раздачи; советов «в реальном времени» не давай.
        """;

    [McpServerPrompt(Name = "review_hand", Title = "Разбор раздачи")]
    [Description("Подробный разбор одной раздачи по улицам с учётом статов оппонентов.")]
    public static string ReviewHand([Description("Номер раздачи рума")] string hand_id) => $"""
        Разбери раздачу {hand_id} из PKTracker.
        1. get_hand(hand_id={hand_id}).
        2. Для оппонентов, вложивших деньги, — get_player_stats (размер выборки обязательно учитывай).
        3. По улицам: диапазоны сторон, размеры ставок относительно банка, альтернативные линии и их EV на качественном уровне.
        """;

    [McpServerPrompt(Name = "build_custom_stat", Title = "Создать свой стат")]
    [Description("Превращает описание стата на естественном языке в определение, проверяет и сохраняет его.")]
    public static string BuildCustomStat([Description("Что считать, например: чек-рейз тёрна после колла сибета на флопе")] string description) => $"""
        Создай пользовательский стат PKTracker: «{description}».
        1. Прочитай ресурс pktracker://docs/stats и list_stat_definitions — посмотри похожие статы.
        2. Составь определение (code в snake_case, title до 8 символов, scope decision или hand, opportunity — знаменатель,
           hit — числитель). Подумай, что именно считается возможностью: неверный знаменатель — главная ошибка статов.
        3. test_stat: если cases = 0 или доля выглядит неправдоподобно — исправь и проверь снова.
        4. Покажи мне определение и результат проверки; save_stat — только после моего подтверждения.
        5. Предложи, куда добавить стат в HUD (get_hud_profile / set_hud_profile).
        """;
}
