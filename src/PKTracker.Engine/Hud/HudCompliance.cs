using System.Text.RegularExpressions;
using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>
/// Приведение раскладки к правилам «статичного» HUD (PokerStars, iPoker, Winamax, 888).
///
/// Эти румы разрешают за столом только числовую статистику и отдельно запрещают статы, условие которых
/// смотрит на карты: «агрессия» — можно, «агрессия на флеш-дро бордах» или «блеф на ривере» — нельзя.
/// Раскладку пользователь собирает сам (и переносит пакетами), поэтому полагаться на то, что в ней таких
/// ячеек нет, нельзя — они вычищаются здесь, перед показом над столом. Сама раскладка на диске не меняется:
/// в реплеере и отчётах после игры всё показывается как задумано.
/// </summary>
public static partial class HudCompliance
{
    // Свойства DecisionPoint / HandPlayerFacts, значение которых зависит от карт игрока или борда.
    // CardsKnown сюда не входит: «дошёл до вскрытия и показал» — факт, а не значение карт.
    [GeneratedRegex(@"\b(HandStrength|BoardTexture|MadeHandBits|MadeHandClass|MadeKnown|Made|FlushDraw|Oesd|Gutshot|AnyDraw|GoodKicker|StrongMade|HoleCard1|HoleCard2|HoleClass)\b")]
    private static partial Regex CardRx();

    [GeneratedRegex(@"\b([a-z][a-z0-9_]*)\.(cases|hits|sum)\b")]
    private static partial Regex RefRx();

    /// <summary>Выражение на языке статов обращается к картам игрока или борда.</summary>
    public static bool UsesCards(string? expression) => !string.IsNullOrEmpty(expression) && CardRx().IsMatch(expression);

    /// <summary>
    /// Стат считается по картам: его условия смотрят на силу руки, дро, текстуру борда или карманные карты.
    /// Формула (expression) наследует это от статов, на которые ссылается.
    /// </summary>
    public static bool IsCardBased(StatDefinition def, Func<string, StatDefinition?> lookup)
    {
        if (def.Scope != StatScope.Expression)
            return UsesCards(def.Opportunity) || UsesCards(def.Hit) || UsesCards(def.Value);
        foreach (Match m in RefRx().Matches(def.Hit ?? ""))
            // Формула на формулу ссылаться не может (StatCompiler), так что рекурсия здесь в один шаг.
            if (lookup(m.Groups[1].Value) is { } dep && dep.Scope != StatScope.Expression && IsCardBased(dep, lookup)) return true;
        return false;
    }

    public static bool IsCardBased(HudCell cell, IReadOnlyDictionary<string, CompiledStat> stats) =>
        !cell.IsLabel && (UsesCards(cell.Filter?.Where)
            || (stats.TryGetValue(cell.Stat!, out var s) && IsCardBased(s.Definition, c => stats.TryGetValue(c, out var d) ? d.Definition : null)));

    /// <summary>Что пришлось убрать из раскладки — для строки в журнале.</summary>
    public readonly record struct Report(int HiddenCells, int UncoloredCells)
    {
        public bool Changed => HiddenCells > 0 || UncoloredCells > 0;
    }

    /// <summary>
    /// Копия профиля, пригодная для стола на руме со статичным HUD: ячейки со статами по картам превращаются
    /// в прочерк (место в сетке сохраняется, попап с них не открывается), а раскраска значения, в которой больше
    /// <paramref name="maxColorRanges"/> диапазонов, снимается целиком — обрезать шкалу до первых трёх значило бы
    /// красить значения не тем цветом, который задумал автор раскладки.
    /// </summary>
    public static HudProfile Restrict(HudProfile profile, IReadOnlyDictionary<string, CompiledStat> stats, int maxColorRanges, out Report report)
    {
        int hidden = 0, uncolored = 0;
        var seen = new Dictionary<HudPage, HudPage>(ReferenceEqualityComparer.Instance);

        HudPage Page(HudPage p)
        {
            if (seen.TryGetValue(p, out var done)) return done;
            var cells = new List<HudCell>(p.Cells.Count);
            foreach (var c in p.Cells)
            {
                if (IsCardBased(c, stats))
                {
                    hidden++;
                    cells.Add(new HudCell
                    {
                        Row = c.Row, Col = c.Col, RowSpan = c.RowSpan, ColSpan = c.ColSpan, Style = c.Style,
                        Text = string.IsNullOrEmpty(c.Text) ? CellRender.NoData : c.Text + " " + CellRender.NoData,
                    });
                    continue;
                }
                var colors = c.IsLabel ? null : c.Colors ?? (stats.TryGetValue(c.Stat!, out var s) ? s.Definition.Colors : null);
                if (colors != null && colors.Count > maxColorRanges)
                {
                    uncolored++;
                    cells.Add(new HudCell
                    {
                        Row = c.Row, Col = c.Col, RowSpan = c.RowSpan, ColSpan = c.ColSpan, Text = c.Text, Stat = c.Stat, Filter = c.Filter,
                        Colors = new List<StatColor>(), Style = c.Style, Decimals = c.Decimals, MinSample = c.MinSample,
                        ShowSample = c.ShowSample, Popup = c.Popup,
                    });
                    continue;
                }
                cells.Add(c);
            }
            return seen[p] = new HudPage
            {
                Name = p.Name, Cells = cells, ColWidth = p.ColWidth, RowHeight = p.RowHeight, ColWidths = p.ColWidths,
                FontSize = p.FontSize, Bg = p.Bg, Group = p.Group, Note = p.Note,
            };
        }

        var result = new HudProfile
        {
            Name = profile.Name, GameKind = profile.GameKind, Version = profile.Version,
            Panel = Page(profile.Panel),
            PanelTabs = profile.PanelTabs.Select(Page).ToList(),
            Pages = profile.Pages.Select(Page).ToList(),
        };
        report = new Report(hidden, uncolored);
        return result;
    }
}
