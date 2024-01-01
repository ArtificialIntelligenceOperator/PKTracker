using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Engine.Stats;
using PKTracker.Parsers.Wpn;

namespace PKTracker.Tests;

internal static class TestUtil
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PKTracker.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
        }
    }

    public static string CorpusDir => Path.Combine(RepoRoot, "handHistory");

    /// <summary>
    /// Есть ли что проверять в корпусе. Пустая папка — то же, что её отсутствие: после клона
    /// репозитория истории рук нет (и не должно быть), а тест, падающий на пустой папке,
    /// выглядит как сломанная сборка.
    /// </summary>
    public static bool HasCorpus =>
        Directory.Exists(CorpusDir) && Directory.EnumerateFiles(CorpusDir, "HH*.txt", SearchOption.AllDirectories).Any();

    public static ParsedHand Parse(string text, GameKind kind = GameKind.Cash)
    {
        var parser = new WpnHandParser();
        var r = parser.Parse(text.Replace("\r\n", "\n"), new HandSourceInfo("test.txt", kind)).Single();
        Assert.True(r.Ok, r.Error);
        return r.Hand!;
    }

    public static HandContext Context(string text, GameKind kind = GameKind.Cash) => HandContextBuilder.Build(Parse(text, kind));

    private static List<CompiledStat>? _stats;

    public static List<CompiledStat> Stats
    {
        get
        {
            if (_stats != null) return _stats;
            int id = 1;
            var defs = DefaultStats.All.Select(d => { d.StatId = id++; return d; }).ToList();
            return _stats = StatCompiler.CompileAll(defs);
        }
    }

    /// <summary>(cases, hits) стата для игрока в одной раздаче.</summary>
    public static (int Cases, int Hits) StatFor(HandContext ctx, string player, string code)
    {
        var st = Stats.Single(s => s.Code == code);
        int cases = 0, hits = 0;
        if (st.Scope == StatScope.Decision)
        {
            foreach (var dp in ctx.Decisions.Where(d => d.PlayerName == player))
                if (st.DecisionOpportunity!(dp)) { cases++; if (st.DecisionHit!(dp)) hits++; }
        }
        else
        {
            foreach (var pf in ctx.Players.Where(p => p.PlayerName == player))
                if (st.HandOpportunity!(pf)) { cases++; if (st.HandHit!(pf)) hits++; }
        }
        return (cases, hits);
    }
}
