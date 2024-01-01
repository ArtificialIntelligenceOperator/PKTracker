using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>Готовая к отрисовке ячейка: текст, цвета, подсказка. Общая и для панели, и для попапа, и для превью редактора.</summary>
public readonly record struct RenderedCell(
    string Text, string? Fg, string? Bg, bool Bold, string Align, bool LowSample, string Tooltip, double? FontSize);

public static class CellRender
{
    /// <summary>Значение без выборки — прочерк, а не ноль: ноль здесь врёт (§5.3).</summary>
    public const string NoData = "–";

    public static RenderedCell Render(HudCell cell, CellValue value, CompiledStat? stat)
    {
        var style = cell.Style;
        string align = style?.Align ?? (cell.IsLabel ? "left" : "center");
        bool bold = style?.Bold ?? false;

        if (cell.IsLabel)
            return new RenderedCell(cell.Text ?? "", style?.Fg, style?.Bg, bold, align, false, "", style?.FontSize);

        string title = cell.Text ?? stat?.Definition.Title ?? cell.Stat ?? "";
        if (value.Value is not { } v)
            return new RenderedCell(Compose(cell.Text, NoData), style?.Fg, style?.Bg, bold, align, true,
                $"{title}: нет данных", style?.FontSize);

        int decimals = cell.Decimals ?? stat?.Definition.Format.Decimals ?? 0;
        string suffix = stat?.Definition.Format.Suffix ?? "";
        string text = v.ToString("F" + decimals) + suffix;
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
        return new RenderedCell(Compose(cell.Text, text), fg, style?.Bg, bold, align, low, tooltip, style?.FontSize);
    }

    private static string Compose(string? label, string value) =>
        string.IsNullOrEmpty(label) ? value : label + " " + value;
}
