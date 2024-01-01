using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>
/// Сборка страницы раскладки в коде. Нужна только встроенным профилям: пользовательские
/// правятся редактором и живут в JSON. Без неё описание вкладки на 300 ячеек нечитаемо.
/// </summary>
public sealed class PageBuilder(string name, double colWidth = 30, double rowHeight = 17)
{
    private readonly List<HudCell> _cells = new();
    private readonly Dictionary<int, double> _widths = new();

    public string Name => name;

    public PageBuilder Width(int col, double width)
    {
        _widths[col] = width;
        return this;
    }

    /// <summary>Подпись без стата.</summary>
    public PageBuilder Label(int row, int col, string text, string? bg = null, string? fg = null,
        bool bold = false, int colSpan = 1, int rowSpan = 1, string? align = null)
    {
        _cells.Add(new HudCell
        {
            Row = row, Col = col, ColSpan = colSpan, RowSpan = rowSpan, Text = text,
            Style = new CellStyle { Bg = bg, Fg = fg, Bold = bold, Align = align },
        });
        return this;
    }

    /// <summary>Ряд подписей-заголовков подряд.</summary>
    public PageBuilder Heads(int row, int col, string?[] titles, string? bg = Theme.Head, string? fg = Theme.HeadFg)
    {
        for (int i = 0; i < titles.Length; i++)
            if (titles[i] != null) Label(row, col + i, titles[i]!, bg, fg, bold: true, align: "center");
        return this;
    }

    public PageBuilder Stat(int row, int col, string code, CellFilter? filter = null, string? bg = null, string? fg = null,
        bool bold = false, bool showSample = false, int? decimals = null, string? text = null, string? popup = null,
        List<StatColor>? colors = null, int? minSample = null, int colSpan = 1)
    {
        _cells.Add(new HudCell
        {
            Row = row, Col = col, ColSpan = colSpan, Stat = code, Filter = filter, Text = text, Popup = popup,
            ShowSample = showSample, Decimals = decimals, Colors = colors, MinSample = minSample,
            Style = new CellStyle { Bg = bg, Fg = fg, Bold = bold, Align = "center" },
        });
        return this;
    }

    /// <summary>Подпись слева и ряд значений одного стата под разными фильтрами.</summary>
    public PageBuilder Row(int row, int col, string label, string code, IReadOnlyList<CellFilter?> filters,
        string? labelBg = null, string? bg = null, string? fg = null, int labelSpan = 1, bool showSample = false)
    {
        Label(row, col, label, labelBg, fg, bold: true, colSpan: labelSpan);
        for (int i = 0; i < filters.Count; i++)
            Stat(row, col + labelSpan + i, code, filters[i], bg, showSample: showSample);
        return this;
    }

    public HudPage Build() => new()
    {
        Name = name, Cells = _cells, ColWidth = colWidth, RowHeight = rowHeight,
        ColWidths = _widths.Count > 0 ? _widths : null,
    };

    // ── Фильтры ──────────────────────────────────────────────────────────────

    /// <summary>Короткая запись фильтра — читаемость раскладок держится на ней.</summary>
    public static CellFilter F(string[]? pos = null, string[]? vs = null, string[]? open = null,
        double? stackFrom = null, double? stackTo = null, int? players = null, int? playersFrom = null, int? playersTo = null,
        int? inHandFrom = null, int? inHandTo = null, bool? allIn = null, bool? ante = null, string? where = null,
        double? toBbFrom = null, double? toBbTo = null) => new()
    {
        Pos = pos, VsPos = vs, OpenPos = open,
        StackMin = stackFrom, StackMax = stackTo,
        PlayersMin = players ?? playersFrom, PlayersMax = players ?? playersTo,
        InHandMin = inHandFrom, InHandMax = inHandTo,
        AllIn = allIn, Ante = ante, Where = where, ToBbMin = toBbFrom, ToBbMax = toBbTo,
    };

    /// <summary>Слияние фильтров: поля правого перебивают левый. Позволяет держать «общее» отдельно от «частного».</summary>
    public static CellFilter Merge(CellFilter? a, CellFilter? b)
    {
        if (a == null) return b ?? new CellFilter();
        if (b == null) return a;
        return new CellFilter
        {
            Pos = b.Pos ?? a.Pos, VsPos = b.VsPos ?? a.VsPos, OpenPos = b.OpenPos ?? a.OpenPos,
            PlayersMin = b.PlayersMin ?? a.PlayersMin, PlayersMax = b.PlayersMax ?? a.PlayersMax,
            InHandMin = b.InHandMin ?? a.InHandMin, InHandMax = b.InHandMax ?? a.InHandMax,
            StackMin = b.StackMin ?? a.StackMin, StackMax = b.StackMax ?? a.StackMax,
            ToBbMin = b.ToBbMin ?? a.ToBbMin, ToBbMax = b.ToBbMax ?? a.ToBbMax,
            AllIn = b.AllIn ?? a.AllIn, Ante = b.Ante ?? a.Ante, Street = b.Street ?? a.Street,
            Where = b.Where ?? a.Where,
        };
    }
}

/// <summary>
/// Позиции колонок. PKTracker различает девять позиций, другие трекеры — до одиннадцати со своими именами;
/// сведены к именам PKTracker, чтобы подпись колонки и условие стата говорили об одном и том же.
/// </summary>
public static class P
{
    /// <summary>Ранние: UTG, UTG+1, MP — в других трекерах колонка «LG».</summary>
    public static readonly string[] Ep = { "UTG", "UTG1", "MP" };
    /// <summary>Средние: LJ, HJ — в других трекерах колонка «HJ».</summary>
    public static readonly string[] Mp = { "LJ", "HJ" };
    public static readonly string[] Co = { "CO" };
    public static readonly string[] Btn = { "BTN" };
    public static readonly string[] Sb = { "SB" };
    public static readonly string[] Bb = { "BB" };
    public static readonly string[] EpBtn = { "UTG", "UTG1", "MP", "LJ", "HJ", "CO", "BTN" };
    public static readonly string[] SbBb = { "SB", "BB" };
    public static readonly string[] Blinds = { "SB", "BB" };
    public static readonly string[] All = { "UTG", "UTG1", "MP", "LJ", "HJ", "CO", "BTN", "SB", "BB" };
}

/// <summary>Палитра раскладок: цвет кодирует роль блока, в теме PKTracker.</summary>
public static class Theme
{
    public const string Head = "#33383D";
    public const string HeadFg = "#E8E8E8";
    public const string Name = "#8B4A22";
    public const string Open = "#6B4423";     // блок опенрейзов
    public const string Limp = "#2C4F2C";     // лимпы
    public const string Cold = "#3A3A3A";     // холодные коллы
    public const string Iso = "#6B5D1F";      // изоляция
    public const string Post = "#1F4F4F";     // постфлоп-сводка
    public const string Line = "#1B1B4A";     // блоки линий (BXB/XXF)
    public const string Fold = "#8C3A2E";     // фолды
    public const string Bet = "#8C7A1F";      // ставки
    public const string Donk = "#2E6B34";     // донки
    public const string Call = "#1F6B7A";     // коллы и флоаты
    public const string Push = "#7A6A1F";     // пуш-фолд
    public const string Sep = "#2A2A2A";
}
