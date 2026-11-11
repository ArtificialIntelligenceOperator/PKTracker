using System.Diagnostics;
using System.Linq.Dynamic.Core;
using System.Text;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;
using PKTracker.Parsers;

namespace PKTracker.Cli;

/// <summary>
/// «Лаборатория» статов: считает статы и целые страницы профиля HUD по одному игроку прямо из файлов
/// истории, без базы. Нужна, чтобы сверять определения статов с эталоном (скриншотами другого трекера):
/// правка условия → секунды до результата, без импорта и ребилда.
///
///   lab &lt;папка&gt; --hero &lt;ник&gt; [--pack файл.json] [--page имя|all] [--opp "…" --hit "…" [--scope hand] [--where "…"] [--group "…"]]
/// </summary>
internal static class LabCommand
{
    public static int Run(Commands.Options opts)
    {
        var dir = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("Не указана папка с историей");
        var hero = opts.Get("hero") ?? throw new ArgumentException("Не указан --hero");
        var sw = Stopwatch.StartNew();
        var rows = Load(dir, hero, opts.Has("ev"), out int rejected);
        Console.WriteLine($"{hero}: раздач {rows.Facts.Count}, решений {rows.Decisions.Count}  ({sw.ElapsedMilliseconds} мс)"
                          + (rejected > 0 ? $"; отклонено парсером или инвариантами: {rejected} (подробности — команда verify)" : ""));

        var defs = DefaultStats.All.ToDictionary(d => d.Code);
        HudProfile? profile = null;
        if (opts.Get("pack") is { } packPath)
        {
            var pack = HudPackage.FromJson(File.ReadAllText(packPath, Encoding.UTF8));
            foreach (var d in pack.Stats) defs[d.Code] = d;
            profile = pack.Profile;
        }
        var compiled = new Dictionary<string, CompiledStat>();
        foreach (var d in defs.Values)
        {
            try { compiled[d.Code] = StatCompiler.Compile(d, defs); }
            catch (StatCompileException ex) { Console.WriteLine($"  ОШИБКА СТАТА {d.Code}: {ex.Message}"); }
        }

        if (opts.Get("opp") is { } opp)
        {
            AdHoc(rows, opp, opts.Get("hit") ?? "true", opts.Get("scope") == "hand", opts.Get("where"), opts.Get("group"), opts.Get("sum"));
            return 0;
        }
        if (opts.Get("stat") is { } statList)
        {
            foreach (var code in statList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!compiled.TryGetValue(code, out var st)) { Console.WriteLine($"  нет стата {code}"); continue; }
                var page = new HudPage { Name = code, Cells = { new HudCell { Stat = code } } };
                var v = new PageEvaluator(compiled).Evaluate(page, rows)[0];
                Console.WriteLine($"  {code,-24} {Fmt(v),8}   {v.Hits}/{v.Cases}");
            }
            return 0;
        }
        if (profile == null) { Console.WriteLine("Нужен --pack (профиль) или --opp/--hit (разовый стат)"); return 2; }

        var pageName = opts.Get("page") ?? "all";
        var evaluator = new PageEvaluator(compiled);
        var pages = profile.AllPanelPages().Concat(profile.Pages)
            .Where(p => pageName == "all" || string.Equals(p.Name, pageName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Group, pageName, StringComparison.OrdinalIgnoreCase));
        foreach (var code in profile.StatCodes().Where(c => !compiled.ContainsKey(c)))
            Console.WriteLine($"  НЕТ ТАКОГО СТАТА: {code}");
        foreach (var page in pages) Print(page, evaluator.Evaluate(page, rows));
        return 0;
    }

    private static string Fmt(CellValue v) => v.Value is { } x ? (v.Cases < 10 ? $"{x:F0}({v.Cases})" : $"{x:F0}") : "--";

    /// <summary>Страница сеткой: подписи как есть, значения — «процент» и выборка в скобках, если она меньше 10.</summary>
    internal static void Print(HudPage page, CellValue[] values)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {(page.Group != null ? page.Group + " / " : "")}{page.Name}");
        int rowsN = page.Rows, colsN = page.Cols;
        var grid = new string?[rowsN, colsN];
        for (int i = 0; i < page.Cells.Count; i++)
        {
            var c = page.Cells[i];
            grid[c.Row, c.Col] = c.Stat == null ? (c.Text ?? "") : Fmt(values[i]);
        }
        var widths = new int[colsN];
        for (int c = 0; c < colsN; c++)
        {
            widths[c] = 2;
            for (int r = 0; r < rowsN; r++) widths[c] = Math.Max(widths[c], Math.Min(14, grid[r, c]?.Length ?? 0));
        }
        for (int r = 0; r < rowsN; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < colsN; c++)
            {
                var s = grid[r, c] ?? "";
                if (s.Length > 14) s = s[..14];
                sb.Append(s.PadLeft(widths[c])).Append(' ');
            }
            Console.WriteLine(sb.ToString().TrimEnd());
        }
    }

    private static void AdHoc(PlayerRows rows, string opp, string hit, bool hand, string? where, string? group, string? sum)
    {
        if (!hand)
        {
            var o = StatCompiler.CompileFilter<DecisionPoint>(opp);
            var h = StatCompiler.CompileFilter<DecisionPoint>(hit);
            var w = where != null ? StatCompiler.CompileFilter<DecisionPoint>(where) : null;
            var g = Group<DecisionPoint>(group);
            Tally(rows.Decisions.Where(d => o(d) && (w == null || w(d))), d => h(d), g, Sum<DecisionPoint>(sum));
        }
        else
        {
            var o = StatCompiler.CompileFilter<HandPlayerFacts>(opp);
            var h = StatCompiler.CompileFilter<HandPlayerFacts>(hit);
            var w = where != null ? StatCompiler.CompileFilter<HandPlayerFacts>(where) : null;
            var g = Group<HandPlayerFacts>(group);
            Tally(rows.Facts.Where(f => o(f) && (w == null || w(f))), f => h(f), g, Sum<HandPlayerFacts>(sum));
        }
    }

    /// <summary>Группировка «A | B | C»: ключ — значения выражений через «/».</summary>
    private static Func<T, object>? Group<T>(string? group)
    {
        if (group == null) return null;
        var parts = group.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(g => DynamicExpressionParser.ParseLambda<T, object>(new ParsingConfig(), false, g).Compile()).ToList();
        return x => string.Join("/", parts.Select(p => p(x)?.ToString() ?? "null"));
    }

    /// <summary>«--sum выражение»: сумма числового выражения по группе — доли денег, а не раздач.</summary>
    private static Func<T, double>? Sum<T>(string? expr) =>
        expr == null ? null : DynamicExpressionParser.ParseLambda<T, double>(new ParsingConfig(), false, expr).Compile();

    private static void Tally<T>(IEnumerable<T> items, Func<T, bool> hit, Func<T, object>? group, Func<T, double>? sum = null)
    {
        var acc = new SortedDictionary<string, (long N, long K, double S)>(StringComparer.Ordinal);
        long tn = 0, tk = 0;
        double ts = 0;
        foreach (var x in items)
        {
            bool k = hit(x);
            double v = sum?.Invoke(x) ?? 0;
            tn++; if (k) tk++;
            ts += v;
            if (group == null) continue;
            var key = group(x)?.ToString() ?? "null";
            var (n, kk, s) = acc.GetValueOrDefault(key);
            acc[key] = (n + 1, kk + (k ? 1 : 0), s + v);
        }
        Console.WriteLine($"  ИТОГО {Pct(tk, tn),6}   {tk}/{tn}" + (sum != null ? $"   Σ {ts:F1}" : ""));
        foreach (var (key, (n, k, s)) in acc)
            Console.WriteLine($"  {key,-20} {Pct(k, n),6}   {k}/{n}" + (sum != null ? $"   Σ {s:F1}" : ""));
    }

    private static string Pct(long k, long n) => n == 0 ? "--" : (k * 100.0 / n).ToString("F1");

    /// <summary>Все решения и факты одного игрока из папки (формат каждого файла — по его началу).</summary>
    public static PlayerRows Load(string dir, string hero, bool ev, out int rejected)
    {
        int bad = 0;
        var decisions = new List<DecisionPoint>();
        var facts = new List<HandPlayerFacts>();
        var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(ParserRegistry.LooksLikeHistoryFile).ToList();
        var lockObj = new object();
        Parallel.ForEach(files, f =>
        {
            var text = File.ReadAllText(f, Encoding.UTF8);
            var det = ParserRegistry.Detect(text.AsSpan(0, Math.Min(text.Length, 4096)), "POKERKING");
            if (det == null) return;
            var ld = new List<DecisionPoint>();
            var lf = new List<HandPlayerFacts>();
            foreach (var r in det.Value.Parser.Parse(text, ParserRegistry.SourceFor(f, hero)))
            {
                if (!r.Ok) { Interlocked.Increment(ref bad); continue; }
                var h = r.Hand!;
                if (h.SeatByName(hero) == null) continue;
                HandContext ctx;
                try { ctx = HandContextBuilder.Build(h, computeEv: ev); }
                catch (InvalidOperationException) { Interlocked.Increment(ref bad); continue; }
                if (Invariants.Check(h, ctx).Count > 0) { Interlocked.Increment(ref bad); continue; }
                foreach (var d in ctx.Decisions) if (d.PlayerName == hero) ld.Add(d);
                foreach (var p in ctx.Players) if (p.PlayerName == hero) lf.Add(p);
            }
            lock (lockObj) { decisions.AddRange(ld); facts.AddRange(lf); }
        });
        rejected = bad;
        return new PlayerRows { Decisions = decisions, Facts = facts, ScannedDecisions = decisions.Count };
    }
}
