using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace PKTracker.Engine.Stats;

public sealed class StatCompileException(string code, string message, Exception? inner = null)
    : Exception($"Stat '{code}': {message}", inner);

/// <summary>Скомпилированный стат: делегаты вместо интерпретации (§4.3).</summary>
public sealed class CompiledStat
{
    public required StatDefinition Definition { get; init; }
    public int StatId => Definition.StatId;
    public string Code => Definition.Code;
    public StatScope Scope => Definition.Scope;

    public Func<DecisionPoint, bool>? DecisionOpportunity { get; init; }
    public Func<DecisionPoint, bool>? DecisionHit { get; init; }
    public Func<HandPlayerFacts, bool>? HandOpportunity { get; init; }
    public Func<HandPlayerFacts, bool>? HandHit { get; init; }
    public Func<HandPlayerFacts, long>? HandValue { get; init; }
    public Func<StatValues, double>? Expression { get; init; }
    /// <summary>Коды plain-статов, на которые ссылается expression.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
}

/// <summary>Агрегаты plain-статов игрока, доступные expression-статам как code.cases / code.hits / code.sum.</summary>
public sealed class StatValues
{
    private readonly Func<string, (long Cases, long Hits, long Sum)> _lookup;
    public StatValues(Func<string, (long Cases, long Hits, long Sum)> lookup) => _lookup = lookup;

    public double V(string code, int field)
    {
        var (c, h, s) = _lookup(code);
        return field switch { 0 => c, 1 => h, _ => s };
    }
}

public static partial class StatCompiler
{
    private static readonly ParsingConfig Config = new()
    {
        IsCaseSensitive = true,
        AllowNewToEvaluateAnyType = false,
    };

    [GeneratedRegex(@"\b([a-z][a-z0-9_]*)\.(cases|hits|sum)\b")]
    private static partial Regex RefRx();

    [GeneratedRegex(@"\bmax\(")]
    private static partial Regex MaxRx();

    [GeneratedRegex(@"\bmin\(")]
    private static partial Regex MinRx();

    public static CompiledStat Compile(StatDefinition def, IReadOnlyDictionary<string, StatDefinition>? known = null)
    {
        try
        {
            switch (def.Scope)
            {
                case StatScope.Decision:
                    return new CompiledStat
                    {
                        Definition = def,
                        DecisionOpportunity = Lambda<DecisionPoint, bool>(Require(def, def.Opportunity, "opportunity")),
                        DecisionHit = Lambda<DecisionPoint, bool>(Require(def, def.Hit, "hit")),
                    };
                case StatScope.Hand:
                    return new CompiledStat
                    {
                        Definition = def,
                        HandOpportunity = Lambda<HandPlayerFacts, bool>(Require(def, def.Opportunity, "opportunity")),
                        HandHit = Lambda<HandPlayerFacts, bool>(def.Hit ?? "true"),
                        HandValue = def.Value != null ? Lambda<HandPlayerFacts, long>(def.Value) : null,
                    };
                case StatScope.Expression:
                {
                    var formula = Require(def, def.Hit, "formula");
                    var deps = new List<string>();
                    var refs = new List<(string Name, string Code, int Field)>();
                    // code.hits → параметр code__hits; вызовы S.V(code, field) подставляются при сборке лямбды.
                    var rewritten = RefRx().Replace(formula, m =>
                    {
                        var code = m.Groups[1].Value;
                        if (known != null)
                        {
                            if (!known.TryGetValue(code, out var d))
                                throw new StatCompileException(def.Code, $"unknown stat '{code}'");
                            if (d.Scope == StatScope.Expression)
                                throw new StatCompileException(def.Code, $"expression cannot reference expression '{code}'");
                        }
                        if (!deps.Contains(code)) deps.Add(code);
                        int field = m.Groups[2].Value switch { "cases" => 0, "hits" => 1, _ => 2 };
                        var name = $"{code}__{m.Groups[2].Value}";
                        if (!refs.Exists(r => r.Name == name)) refs.Add((name, code, field));
                        return name;
                    });
                    rewritten = MaxRx().Replace(rewritten, "Math.Max(");
                    rewritten = MinRx().Replace(rewritten, "Math.Min(");
                    var ps = refs.Select(r => Expression.Parameter(typeof(double), r.Name)).ToArray();
                    var inner = DynamicExpressionParser.ParseLambda(Config, ps, typeof(double), rewritten);
                    var sParam = Expression.Parameter(typeof(StatValues), "S");
                    var vMethod = typeof(StatValues).GetMethod(nameof(StatValues.V))!;
                    var args = refs.Select(r => (Expression)Expression.Call(sParam, vMethod, Expression.Constant(r.Code), Expression.Constant(r.Field)));
                    var outer = Expression.Lambda<Func<StatValues, double>>(Expression.Invoke(inner, args), sParam);
                    return new CompiledStat
                    {
                        Definition = def,
                        Expression = outer.Compile(),
                        Dependencies = deps,
                    };
                }
                default:
                    throw new StatCompileException(def.Code, $"unknown scope {def.Scope}");
            }
        }
        catch (StatCompileException) { throw; }
        catch (Exception ex)
        {
            throw new StatCompileException(def.Code, ex.Message, ex);
        }
    }

    /// <summary>Фильтр поиска рук (§7): выражение над фактами (рука, игрок), тот же язык, что у статов.</summary>
    public static Func<HandPlayerFacts, bool> CompileHandFilter(string expression) => CompileFilter<HandPlayerFacts>(expression);

    /// <summary>Логическое выражение над <typeparamref name="T"/> — фильтры ячеек HUD и поиск рук.</summary>
    public static Func<T, bool> CompileFilter<T>(string expression)
    {
        try { return Lambda<T, bool>(expression); }
        catch (Exception ex) { throw new StatCompileException("filter", ex.Message, ex); }
    }

    public static List<CompiledStat> CompileAll(IEnumerable<StatDefinition> defs)
    {
        var list = defs.ToList();
        var known = list.ToDictionary(d => d.Code);
        return list.Select(d => Compile(d, known)).ToList();
    }

    private static string Require(StatDefinition def, string? s, string what) =>
        string.IsNullOrWhiteSpace(s) ? throw new StatCompileException(def.Code, $"{what} is empty") : s;

    private static Func<T, TResult> Lambda<T, TResult>(string expr)
    {
        var p = Expression.Parameter(typeof(T), "it");
        var parsed = DynamicExpressionParser.ParseLambda(Config, new[] { p }, null, expr);
        var body = parsed.Body.Type == typeof(TResult) ? parsed.Body : Expression.Convert(parsed.Body, typeof(TResult));
        return Expression.Lambda<Func<T, TResult>>(body, p).Compile();
    }
}
