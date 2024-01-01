using System.Runtime.InteropServices;
using PKTracker.Core.Model;

namespace PKTracker.Engine.Stats;

/// <summary>Ключ агрегата (§3.2 player_stat_agg). Только struct — никаких кортежей/строк (§15.7).</summary>
public readonly record struct AggKey(long PlayerId, int StatId, byte GameKind, byte DealtBucket, byte Position, byte StackBucket);

[StructLayout(LayoutKind.Auto)]
public struct AggValue
{
    public int Cases;
    public int Hits;
    public long Sum;
}

/// <summary>
/// Один проход по раздаче считает все статы сразу (§4.4): точки × decision-статы, игроки × hand-статы.
/// </summary>
public sealed class StatAggregator
{
    private readonly CompiledStat[] _decisionStats;
    private readonly CompiledStat[] _handStats;
    private readonly Dictionary<AggKey, AggValue> _acc = new();

    public StatAggregator(IEnumerable<CompiledStat> stats)
    {
        var list = stats.Where(s => s.StatId > 0).ToList();
        _decisionStats = list.Where(s => s.Scope == StatScope.Decision).ToArray();
        _handStats = list.Where(s => s.Scope == StatScope.Hand).ToArray();
    }

    public int Count => _acc.Count;
    public IReadOnlyDictionary<AggKey, AggValue> Values => _acc;

    public void Clear() => _acc.Clear();

    /// <param name="playerIdBySeat">id игрока для места (индекс = номер места); 0 = не учитывать.</param>
    public void Add(HandContext ctx, long[] playerIdBySeat)
    {
        byte kind = (byte)ctx.Hand.GameKind;
        byte dealtBucket = ctx.Hand.DealtBucket;

        foreach (var dp in ctx.Decisions)
        {
            long pid = playerIdBySeat[dp.Seat];
            if (pid == 0) continue;
            foreach (var st in _decisionStats)
            {
                if (!st.DecisionOpportunity!(dp)) continue;
                ref var v = ref CollectionsMarshal.GetValueRefOrAddDefault(_acc,
                    new AggKey(pid, st.StatId, kind, dealtBucket, (byte)dp.Position, dp.StackBucket), out _);
                v.Cases++;
                if (st.DecisionHit!(dp)) v.Hits++;
            }
        }

        foreach (var pf in ctx.Players)
        {
            long pid = playerIdBySeat[pf.Seat];
            if (pid == 0) continue;
            foreach (var st in _handStats)
            {
                if (!st.HandOpportunity!(pf)) continue;
                ref var v = ref CollectionsMarshal.GetValueRefOrAddDefault(_acc,
                    new AggKey(pid, st.StatId, kind, dealtBucket, (byte)pf.Position, pf.StackBucket), out _);
                v.Cases++;
                if (st.HandHit!(pf)) v.Hits++;
                if (st.HandValue != null) v.Sum += st.HandValue(pf);
            }
        }
    }
}

/// <summary>Сводные значения статов игрока (после GROUP BY stat_id) + вычисление expression-статов.</summary>
public sealed class PlayerStatSet
{
    private readonly Dictionary<int, (long Cases, long Hits, long Sum)> _byId = new();
    private readonly IReadOnlyDictionary<string, CompiledStat> _byCode;

    public PlayerStatSet(IReadOnlyDictionary<string, CompiledStat> statsByCode) => _byCode = statsByCode;

    public void Add(int statId, long cases, long hits, long sum)
    {
        _byId.TryGetValue(statId, out var cur);
        _byId[statId] = (cur.Cases + cases, cur.Hits + hits, cur.Sum + sum);
    }

    public (long Cases, long Hits, long Sum) Get(string code) =>
        _byCode.TryGetValue(code, out var st) && _byId.TryGetValue(st.StatId, out var v) ? v : default;

    /// <summary>Значение стата для отображения + размер выборки. null — нет данных.</summary>
    public (double? Value, long Sample) Evaluate(string code)
    {
        if (!_byCode.TryGetValue(code, out var st)) return (null, 0);
        if (st.Scope == StatScope.Expression)
        {
            var sv = new StatValues(Get);
            long sample = st.Dependencies.Count > 0 ? st.Dependencies.Max(d => Get(d).Cases) : 0;
            if (sample == 0) return (null, 0);
            return (st.Expression!(sv), sample);
        }
        var (c, h, s) = Get(code);
        if (c == 0) return (null, 0);
        if (st.Definition.Value != null) return (s, c);
        if (!st.Definition.Format.Percent) return (h, c);
        return (h * 100.0 / c, c);
    }
}

public static class AggBuckets
{
    public static byte Dealt(int playersDealt) => playersDealt switch { <= 2 => 0, <= 4 => 1, <= 6 => 2, _ => 3 };
    public static GameKind[] Kinds => Enum.GetValues<GameKind>();
}
