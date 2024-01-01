using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>Посчитанное значение ячейки. <see cref="Value"/> = null — выборки нет.</summary>
public readonly record struct CellValue(double? Value, long Cases, long Hits)
{
    public static readonly CellValue Empty = new(null, 0, 0);
}

/// <summary>
/// Считает страницу раскладки по «срезу» игрока — его точкам решения и фактам рук.
///
/// Порядок обхода выбран из-за цены: в попапе бывает 400 ячеек на 50 000 строк, и наивные
/// «для каждой ячейки пройти все строки» — это 20 млн проверок фильтра. Поэтому внешний цикл
/// идёт по статам: opportunity стата отсеивает подавляющее большинство строк один раз, и фильтры
/// ячеек проверяются только на том, что осталось.
/// </summary>
public sealed class PageEvaluator
{
    private readonly IReadOnlyDictionary<string, CompiledStat> _stats;

    public PageEvaluator(IReadOnlyDictionary<string, CompiledStat> stats) => _stats = stats;

    private sealed class Slot
    {
        public required CompiledFilter Filter { get; init; }
        public long Cases;
        public long Hits;
        public long Sum;
    }

    /// <summary>
    /// Значения всех ячеек страницы. Индекс результата совпадает с индексом в <see cref="HudPage.Cells"/>;
    /// для подписей возвращается <see cref="CellValue.Empty"/>.
    /// </summary>
    public CellValue[] Evaluate(HudPage page, PlayerRows rows)
    {
        var result = new CellValue[page.Cells.Count];
        Array.Fill(result, CellValue.Empty);

        // (код стата, фильтр) → копилка: ячейки с одинаковой парой считаются один раз на двоих.
        var slots = new Dictionary<(string Code, CompiledFilter Filter), Slot>();
        var cellSlot = new Slot?[page.Cells.Count];
        var expressions = new List<(int Index, CompiledStat Stat, CompiledFilter Filter)>();

        Slot SlotFor(string code, CompiledFilter f)
        {
            if (!slots.TryGetValue((code, f), out var s)) slots[(code, f)] = s = new Slot { Filter = f };
            return s;
        }

        for (int i = 0; i < page.Cells.Count; i++)
        {
            var cell = page.Cells[i];
            if (cell.Stat == null || !_stats.TryGetValue(cell.Stat, out var st)) continue;
            var filter = cell.Filter == null ? CompiledFilter.PassAll : rows.Filter(cell.Filter);
            if (st.Scope == StatScope.Expression)
            {
                expressions.Add((i, st, filter));
                // Зависимости expression-стата считаются под тем же фильтром, что и сама ячейка.
                foreach (var dep in st.Dependencies) SlotFor(dep, filter);
            }
            else cellSlot[i] = SlotFor(cell.Stat, filter);
        }

        foreach (var group in slots.GroupBy(kv => kv.Key.Code))
        {
            if (!_stats.TryGetValue(group.Key, out var st)) continue;
            var list = group.Select(kv => kv.Value).ToList();
            if (st.Scope == StatScope.Decision) Accumulate(st, rows.Decisions, list);
            else if (st.Scope == StatScope.Hand) Accumulate(st, rows.Facts, list);
        }

        for (int i = 0; i < page.Cells.Count; i++)
            if (cellSlot[i] is { } s) result[i] = Value(_stats[page.Cells[i].Stat!], s);

        foreach (var (index, st, filter) in expressions)
        {
            var lookup = new StatValues(code => slots.TryGetValue((code, filter), out var s) ? (s.Cases, s.Hits, s.Sum) : default);
            long sample = 0;
            foreach (var dep in st.Dependencies)
                if (slots.TryGetValue((dep, filter), out var s)) sample = Math.Max(sample, s.Cases);
            result[index] = sample == 0 ? CellValue.Empty : new CellValue(st.Expression!(lookup), sample, 0);
        }

        return result;
    }

    private static void Accumulate(CompiledStat st, IReadOnlyList<DecisionPoint> rows, List<Slot> slots)
    {
        for (int r = 0; r < rows.Count; r++)
        {
            var d = rows[r];
            if (!st.DecisionOpportunity!(d)) continue;
            bool hit = st.DecisionHit!(d);
            foreach (var slot in slots)
            {
                if (!slot.Filter.Match(d)) continue;
                slot.Cases++;
                if (hit) slot.Hits++;
            }
        }
    }

    private static void Accumulate(CompiledStat st, IReadOnlyList<HandPlayerFacts> rows, List<Slot> slots)
    {
        for (int r = 0; r < rows.Count; r++)
        {
            var p = rows[r];
            if (!st.HandOpportunity!(p)) continue;
            bool hit = st.HandHit!(p);
            long add = st.HandValue != null ? st.HandValue(p) : 0;
            foreach (var slot in slots)
            {
                if (!slot.Filter.Match(p)) continue;
                slot.Cases++;
                if (hit) slot.Hits++;
                slot.Sum += add;
            }
        }
    }

    private static CellValue Value(CompiledStat st, Slot slot)
    {
        if (slot.Cases == 0) return CellValue.Empty;
        if (st.Definition.Value != null) return new CellValue(slot.Sum, slot.Cases, slot.Hits);
        if (!st.Definition.Format.Percent) return new CellValue(slot.Hits, slot.Cases, slot.Hits);
        return new CellValue(slot.Hits * 100.0 / slot.Cases, slot.Cases, slot.Hits);
    }
}

/// <summary>
/// «Срез» игрока: все его точки решения и факты рук. Загружается один раз на открытие попапа
/// и переиспользуется всеми вкладками — иначе каждая вкладка била бы в базу заново.
/// </summary>
public sealed class PlayerRows
{
    public required IReadOnlyList<DecisionPoint> Decisions { get; init; }
    public required IReadOnlyList<HandPlayerFacts> Facts { get; init; }
    /// <summary>Сколько строк решений прочитано из базы (может упираться в лимит).</summary>
    public int ScannedDecisions { get; init; }
    public bool Truncated { get; init; }

    private readonly Dictionary<CellFilter, CompiledFilter> _cache = new();

    /// <summary>Компиляция фильтра с кешем: одна и та же ссылка на фильтр даёт один и тот же делегат.</summary>
    public CompiledFilter Filter(CellFilter f)
    {
        if (_cache.TryGetValue(f, out var c)) return c;
        return _cache[f] = f.IsEmpty ? CompiledFilter.PassAll : new CompiledFilter(f);
    }

    public static PlayerRows Empty { get; } = new()
    {
        Decisions = Array.Empty<DecisionPoint>(), Facts = Array.Empty<HandPlayerFacts>(),
    };
}
