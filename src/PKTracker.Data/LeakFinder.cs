using PKTracker.Core.Model;
using PKTracker.Engine.Stats;

namespace PKTracker.Data;

public sealed record LeakRow(string Code, string Title, double Hero, long HeroSample, double Pool, long PoolSample, double Diff,
    double Score, string Verdict, int Decimals);

/// <summary>Ликфайндер (§7): статы Hero против пула из той же базы, отсортированные по значимости отклонения.</summary>
public static class LeakFinder
{
    private static readonly HashSet<string> Skip = new() { "hands", "pf_bets", "pf_raises", "pf_calls", "pf_folds", "bb100", "ev_bb100", "luck_bb100" };

    public static async Task<List<LeakRow>> ComputeAsync(TrackerHost host, GameKind kind, CancellationToken ct = default)
    {
        var (heroRows, poolRows) = await new ReportRepository(host.Db).HeroVsPoolAsync(kind, ct);
        var byCode = host.Stats.ToDictionary(s => s.Code);
        var hero = new PlayerStatSet(byCode);
        var pool = new PlayerStatSet(byCode);
        foreach (var r in heroRows) hero.Add(r.StatId, r.Cases, r.Hits, r.Sum);
        foreach (var r in poolRows) pool.Add(r.StatId, r.Cases, r.Hits, r.Sum);

        var rows = new List<LeakRow>();
        foreach (var st in host.Stats)
        {
            // Суммы (net/EV) и служебные счётчики с пулом не сравниваются
            if (st.Definition.Value != null || Skip.Contains(st.Code)) continue;
            // Счётчики («Раздач», «Hands»): их значение — число случаев. Оно зависит от того, сколько сыграно,
            // а не от манеры игры, и против пула из тысяч игроков всегда выглядит «сильным отклонением».
            if (st.Scope != StatScope.Expression && !st.Definition.Format.Percent) continue;
            var (hv, hn) = hero.Evaluate(st.Code);
            var (pv, pn) = pool.Evaluate(st.Code);
            if (hv == null || pv == null) continue;
            int dec = Math.Max(st.Definition.Format.Decimals, st.Scope == StatScope.Expression ? 1 : 0);
            double diff = hv.Value - pv.Value;
            // Значимость: для частот — z-оценка разницы долей, для выражений — относительная разница
            double score;
            if (st.Scope != StatScope.Expression)
            {
                double p = pv.Value / 100, se = Math.Sqrt(Math.Max(p * (1 - p), 1e-6) / Math.Max(hn, 1)) * 100;
                score = Math.Abs(diff) / se;
            }
            else score = Math.Abs(diff) / Math.Max(Math.Abs(pv.Value), 0.1) * 4;
            // Мало случаев у Hero или у пула — вывода нет, и строка не должна всплывать наверх списка:
            // «90% против 0%» по трём случаям пула выглядит самым большим отклонением, хотя это шум.
            bool thin = hn < Math.Max(st.Definition.MinSample, 20) || pn < Math.Max(st.Definition.MinSample, 20);
            string verdict = thin ? "мало данных"
                : score >= 3 ? (diff > 0 ? "▲ сильно выше пула" : "▼ сильно ниже пула")
                : score >= 2 ? (diff > 0 ? "△ выше пула" : "▽ ниже пула")
                : "в норме";
            rows.Add(new LeakRow(st.Code, st.Definition.Title, hv.Value, hn, pv.Value, pn, diff, thin ? 0 : score, verdict, dec));
        }
        return rows.OrderByDescending(r => r.Score).ToList();
    }
}
