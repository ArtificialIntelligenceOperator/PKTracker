using PKTracker.Core.Model;
using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>Строка агрегата с сохранёнными измерениями — то, что лежит в player_stat_agg до GROUP BY.</summary>
public readonly record struct AggRow(int StatId, byte DealtBucket, byte Position, byte StackBucket, long Cases, long Hits, long Sum);

/// <summary>
/// Агрегаты игрока по измерениям. В отличие от <see cref="PlayerStatSet"/>, измерения не схлопнуты:
/// панель HUD фильтрует ячейки по позиции, числу игроков и корзине стека, а лезть за этим
/// в normalized для девяти игроков на шести столах — не вариант по времени (§10.2).
/// </summary>
public sealed class PlayerAggSet
{
    private readonly List<AggRow> _rows;
    private readonly IReadOnlyDictionary<string, CompiledStat> _stats;

    public PlayerAggSet(IReadOnlyDictionary<string, CompiledStat> stats, List<AggRow> rows)
    {
        _stats = stats;
        _rows = rows;
    }

    public int RowCount => _rows.Count;

    /// <summary>Сумма по строкам, подходящим под фильтр. Фильтр обязан быть <see cref="CellFilter.AggCompatible"/>.</summary>
    public (long Cases, long Hits, long Sum) Sum(int statId, CellFilter? filter)
    {
        HashSet<Position>? pos = null;
        HashSet<byte>? dealt = null, stack = null;
        if (filter != null)
        {
            pos = filter.PositionSet();
            if (filter.PlayersMin != null || filter.PlayersMax != null) dealt = filter.DealtBuckets().ToHashSet();
            if (filter.StackMin != null || filter.StackMax != null) stack = filter.StackBuckets().ToHashSet();
        }
        long c = 0, h = 0, s = 0;
        foreach (var r in _rows)
        {
            if (r.StatId != statId) continue;
            if (pos != null && !pos.Contains((Position)r.Position)) continue;
            if (dealt != null && !dealt.Contains(r.DealtBucket)) continue;
            if (stack != null && !stack.Contains(r.StackBucket)) continue;
            c += r.Cases; h += r.Hits; s += r.Sum;
        }
        return (c, h, s);
    }

    /// <summary>Значение ячейки панели. Для expression-стата зависимости берутся под тем же фильтром.</summary>
    public CellValue Evaluate(string code, CellFilter? filter)
    {
        if (!_stats.TryGetValue(code, out var st)) return CellValue.Empty;
        if (st.Scope == StatScope.Expression)
        {
            long sample = 0;
            foreach (var dep in st.Dependencies)
                if (_stats.TryGetValue(dep, out var d)) sample = Math.Max(sample, Sum(d.StatId, filter).Cases);
            if (sample == 0) return CellValue.Empty;
            var lookup = new StatValues(dep => _stats.TryGetValue(dep, out var d) ? Sum(d.StatId, filter) : default);
            return new CellValue(st.Expression!(lookup), sample, 0);
        }
        var (c, h, sum) = Sum(st.StatId, filter);
        if (c == 0) return CellValue.Empty;
        if (st.Definition.Value != null) return new CellValue(sum, c, h);
        if (!st.Definition.Format.Percent) return new CellValue(h, c, h);
        return new CellValue(h * 100.0 / c, c, h);
    }

    /// <summary>Значения всех ячеек страницы по агрегатам.</summary>
    public CellValue[] EvaluatePage(HudPage page)
    {
        var result = new CellValue[page.Cells.Count];
        for (int i = 0; i < page.Cells.Count; i++)
        {
            var cell = page.Cells[i];
            result[i] = cell.Stat == null ? CellValue.Empty : Evaluate(cell.Stat, cell.Filter);
        }
        return result;
    }

    public static PlayerAggSet Empty(IReadOnlyDictionary<string, CompiledStat> stats) => new(stats, new List<AggRow>());
}
