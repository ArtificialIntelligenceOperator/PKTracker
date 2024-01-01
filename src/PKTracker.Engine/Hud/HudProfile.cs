using System.Text.Json;
using System.Text.Json.Serialization;
using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>Как ячейка выглядит. Пустые поля наследуются от определения стата.</summary>
public sealed class CellStyle
{
    /// <summary>Фон «#RRGGBB»; null — прозрачный.</summary>
    public string? Bg { get; init; }
    /// <summary>Цвет текста; перебивается порогами <see cref="HudCell.Colors"/>, если значение в них попало.</summary>
    public string? Fg { get; init; }
    public bool Bold { get; init; }
    /// <summary>left / center / right; по умолчанию — center для значений, left для подписей.</summary>
    public string? Align { get; init; }
    public double? FontSize { get; init; }
}

/// <summary>
/// Ячейка раскладки. Либо подпись (<see cref="Text"/> без <see cref="Stat"/>), либо значение стата.
/// Координаты абсолютные: страница — одна большая сетка.
/// </summary>
public sealed class HudCell
{
    public int Row { get; init; }
    public int Col { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColSpan { get; init; } = 1;

    /// <summary>Статическая подпись. Для ячейки со статом — префикс перед значением («CB 65»).</summary>
    public string? Text { get; init; }
    /// <summary>Код стата; null — ячейка чисто декоративная.</summary>
    public string? Stat { get; init; }
    public CellFilter? Filter { get; init; }

    /// <summary>Пороги цветов; null — взять из определения стата.</summary>
    public List<StatColor>? Colors { get; init; }
    public CellStyle? Style { get; init; }
    /// <summary>Знаков после запятой; null — из определения стата.</summary>
    public int? Decimals { get; init; }
    /// <summary>Ниже этой выборки значение приглушается; null — из определения стата.</summary>
    public int? MinSample { get; init; }
    /// <summary>Показывать выборку в скобках — «99 (48)».</summary>
    public bool ShowSample { get; init; }

    /// <summary>Имя страницы попапа, открываемой по клику. Только для панели.</summary>
    public string? Popup { get; init; }

    [JsonIgnore] public bool IsLabel => Stat == null;

    public HudCell With(int row, int col) => Clone(row, col);

    private HudCell Clone(int row, int col) => new()
    {
        Row = row, Col = col, RowSpan = RowSpan, ColSpan = ColSpan, Text = Text, Stat = Stat, Filter = Filter,
        Colors = Colors, Style = Style, Decimals = Decimals, MinSample = MinSample, ShowSample = ShowSample, Popup = Popup,
    };
}

/// <summary>Страница раскладки: панель HUD или одна вкладка попапа.</summary>
public sealed class HudPage
{
    public required string Name { get; init; }
    public List<HudCell> Cells { get; init; } = new();
    /// <summary>Ширина колонки по умолчанию, DIP.</summary>
    public double ColWidth { get; init; } = 34;
    public double RowHeight { get; init; } = 18;
    /// <summary>Индивидуальные ширины колонок: «индекс → ширина».</summary>
    public Dictionary<int, double>? ColWidths { get; init; }
    public double FontSize { get; init; } = 11;
    /// <summary>Фон страницы; null — тема приложения.</summary>
    public string? Bg { get; init; }

    [JsonIgnore] public int Rows => Cells.Count == 0 ? 0 : Cells.Max(c => c.Row + c.RowSpan);
    [JsonIgnore] public int Cols => Cells.Count == 0 ? 0 : Cells.Max(c => c.Col + c.ColSpan);
}

/// <summary>
/// Профиль HUD: панель на столе + вкладки попапа. Хранится файлом в папке настроек,
/// а не в базе: профиль правят и возят между машинами, база для этого не нужна.
/// </summary>
public sealed class HudProfile
{
    public required string Name { get; init; }
    /// <summary>cash | mtt — какому виду игры соответствует профиль.</summary>
    public string GameKind { get; init; } = "mtt";
    /// <summary>Панель, висящая над местом игрока.</summary>
    public required HudPage Panel { get; init; }
    /// <summary>Вкладки попапа в порядке отображения.</summary>
    public List<HudPage> Pages { get; init; } = new();
    /// <summary>Версия формата — на будущее, если модель придётся менять несовместимо.</summary>
    public int Version { get; init; } = 1;

    public HudPage? Page(string name) => Pages.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Все коды статов, которые встречаются в профиле.</summary>
    public IEnumerable<string> StatCodes() =>
        Pages.Prepend(Panel).SelectMany(p => p.Cells).Select(c => c.Stat).Where(s => s != null).Select(s => s!).Distinct();

    /// <summary>
    /// Профиль из старого плоского списка кодов («vpip,pfr,|,three_bet»). Вкладок у него нет,
    /// поэтому клик по стату открывает разбивку одного стата, как было до раскладок.
    /// Оставлен, чтобы настройка CashStats/MttStats и MCP-инструмент set_hud_profile не стали
    /// молча ни на что не влияющими.
    /// </summary>
    public static HudProfile FromCodes(string codes, string gameKind)
    {
        var page = new HudPage { Name = "Панель", ColWidth = 44, RowHeight = 15 };
        int row = 0, col = 0;
        foreach (var code in codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (code == "|") { row++; col = 0; continue; }
            page.Cells.Add(new HudCell { Row = row, Col = col++, Stat = code });
        }
        return new HudProfile { Name = LegacyName, GameKind = gameKind, Panel = page };
    }

    /// <summary>Имя, под которым старый плоский список показывается в выборе раскладки.</summary>
    public const string LegacyName = "[список статов (старый)]";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static HudProfile FromJson(string json) => JsonSerializer.Deserialize<HudProfile>(json, Json)!;
}
