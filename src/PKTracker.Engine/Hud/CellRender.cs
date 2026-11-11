using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>Готовая к отрисовке ячейка: текст, цвета, подсказка. Общая и для панели, и для попапа, и для превью редактора.</summary>
public readonly record struct RenderedCell(
    string Text, string? Fg, string? Bg, bool Bold, string Align, bool LowSample, string Tooltip, double? FontSize,
    long? SmallSample = null, string? Border = null);

/// <summary>Как показывать значения (настройки «Внешний вид статов»); одни на всё приложение.</summary>
public sealed class CellRenderOptions
{
    /// <summary>Выборку меньше этого числа писать мелкой цифрой справа от значения («50₂»); 0 — не писать.</summary>
    public int SampleSubscriptBelow { get; set; } = 10;
    /// <summary>Показывать 99 вместо 100: в узкой ячейке трёхзначное число не помещается и сдвигает сетку.</summary>
    public bool Cap100To99 { get; set; } = true;
}

public static class CellRender
{
    /// <summary>Значение без выборки — прочерк, а не ноль: ноль здесь врёт (§5.3).</summary>
    public const string NoData = "–";

    public static CellRenderOptions Options { get; set; } = new();

    public static RenderedCell Render(HudCell cell, CellValue value, CompiledStat? stat)
    {
        var style = cell.Style;
        string align = style?.Align ?? (cell.IsLabel ? "left" : "center");
        bool bold = style?.Bold ?? false;

        if (cell.IsLabel)
            return new RenderedCell(cell.Text ?? "", style?.Fg, style?.Bg, bold, align, false, "", style?.FontSize, null, style?.Border);

        string title = cell.Text ?? stat?.Definition.Title ?? cell.Stat ?? "";
        if (value.Value is not { } v)
            return new RenderedCell(Compose(cell.Text, NoData), style?.Fg, style?.Bg, bold, align, true,
                $"{title}: нет данных", style?.FontSize, null, style?.Border);

        int decimals = cell.Decimals ?? stat?.Definition.Format.Decimals ?? 0;
        string suffix = stat?.Definition.Format.Suffix ?? "";
        double shown = Options.Cap100To99 && decimals == 0 && stat?.Definition.Format.Percent != false && Math.Round(v) >= 100 ? 99 : v;
        string text = shown.ToString("F" + decimals) + suffix;
        if (cell.ShowSample) text += $" ({value.Cases})";

        string? fg = style?.Fg;
        var colors = cell.Colors ?? stat?.Definition.Colors;
        if (colors != null)
            foreach (var c in colors)
                if (v >= c.From && v < c.To) { fg = c.Color; break; }

        int minSample = cell.MinSample ?? stat?.Definition.MinSample ?? 0;
        bool low = minSample > 0 && value.Cases < minSample;

        string filter = cell.Filter is { IsEmpty: false } f ? "\n" + f : "";
        string tooltip = $"{title}: {v:F1}{suffix}   {value.Hits}/{value.Cases}{filter}";
        long? small = !cell.ShowSample && Options.SampleSubscriptBelow > 0 && value.Cases < Options.SampleSubscriptBelow
                      && stat?.Definition.Format.Percent != false ? value.Cases : null;
        return new RenderedCell(Compose(cell.Text, text), fg, style?.Bg, bold, align, low, tooltip, style?.FontSize, small, style?.Border);
    }

    private static string Compose(string? label, string value) =>
        string.IsNullOrEmpty(label) ? value : label + " " + value;
}
