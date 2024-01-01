using PKTracker.Core.Model;
using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>
/// Фильтр ячейки, превращённый в делегаты. Компилируется один раз на профиль: попап прогоняет
/// сотни ячеек по десяткам тысяч строк, и разбор фильтра в цикле съел бы всё время.
/// </summary>
public sealed class CompiledFilter
{
    public static readonly CompiledFilter PassAll = new();

    private readonly HashSet<Position>? _pos;
    private readonly HashSet<Position>? _vsPos;
    private readonly HashSet<Position>? _openPos;
    private readonly int _playersMin, _playersMax, _inHandMin, _inHandMax;
    private readonly double _stackMin, _stackMax, _toBbMin, _toBbMax;
    private readonly bool? _allIn, _ante;
    private readonly Street? _street;
    private readonly Func<DecisionPoint, bool>? _whereDecision;
    private readonly Func<HandPlayerFacts, bool>? _whereHand;

    public CellFilter? Source { get; }
    /// <summary>Фильтр ничего не сужает — быстрый путь в циклах.</summary>
    public bool IsPassAll { get; }

    private CompiledFilter()
    {
        _playersMax = int.MaxValue; _inHandMax = int.MaxValue;
        _stackMax = double.PositiveInfinity; _toBbMax = double.PositiveInfinity;
        IsPassAll = true;
    }

    public CompiledFilter(CellFilter f)
    {
        Source = f;
        IsPassAll = f.IsEmpty;
        _pos = CellFilter.Parse(f.Pos);
        _vsPos = CellFilter.Parse(f.VsPos);
        _openPos = CellFilter.Parse(f.OpenPos);
        _playersMin = f.PlayersMin ?? int.MinValue;
        _playersMax = f.PlayersMax ?? int.MaxValue;
        _inHandMin = f.InHandMin ?? int.MinValue;
        _inHandMax = f.InHandMax ?? int.MaxValue;
        _stackMin = f.StackMin ?? double.NegativeInfinity;
        _stackMax = f.StackMax ?? double.PositiveInfinity;
        _toBbMin = f.ToBbMin ?? double.NegativeInfinity;
        _toBbMax = f.ToBbMax ?? double.PositiveInfinity;
        _allIn = f.AllIn;
        _ante = f.Ante;
        if (f.Street != null && Enum.TryParse<Street>(f.Street, ignoreCase: true, out var st)) _street = st;
        if (!string.IsNullOrWhiteSpace(f.Where))
        {
            // Выражение может относиться к любому из двух видов статов; какое из них понадобится,
            // на момент компиляции неизвестно, поэтому компилируем оба и молча пропускаем неподходящее.
            _whereDecision = TryCompile<DecisionPoint>(f.Where);
            _whereHand = TryCompile<HandPlayerFacts>(f.Where);
            if (_whereDecision == null && _whereHand == null)
                throw new StatCompileException("filter", $"не удалось разобрать условие: {f.Where}");
        }
    }

    private static Func<T, bool>? TryCompile<T>(string expr)
    {
        try { return StatCompiler.CompileFilter<T>(expr); }
        catch (Exception) { return null; }
    }

    public bool Match(DecisionPoint d)
    {
        if (IsPassAll) return true;
        if (_pos != null && !_pos.Contains(d.Position)) return false;
        if (_vsPos != null && !_vsPos.Contains(d.VsPosition)) return false;
        if (_openPos != null && !_openPos.Contains(d.OpenerPosition)) return false;
        if (d.Hand.PlayersDealt < _playersMin || d.Hand.PlayersDealt > _playersMax) return false;
        if (d.PlayersIn < _inHandMin || d.PlayersIn > _inHandMax) return false;
        double bb = d.EffStackBb;
        if (bb < _stackMin || bb >= _stackMax) return false;
        if (_toBbMin > double.NegativeInfinity || _toBbMax < double.PositiveInfinity)
        {
            double to = d.ToAmountBb;
            if (to < _toBbMin || to > _toBbMax) return false;
        }
        if (_allIn is { } ai && d.IsAllIn != ai) return false;
        if (_ante is { } an && d.Hand.HasAnte != an) return false;
        if (_street is { } st && d.Street != st) return false;
        if (_whereDecision != null && !_whereDecision(d)) return false;
        return true;
    }

    public bool Match(HandPlayerFacts p)
    {
        if (IsPassAll) return true;
        if (_pos != null && !_pos.Contains(p.Position)) return false;
        // Позиции оппонентов и всё, что привязано к моменту решения, к фактам руки неприменимо:
        // такая ячейка просто не наберёт выборки, и это честнее молчаливого игнорирования фильтра.
        if (_vsPos != null || _openPos != null || _street != null) return false;
        if (p.Hand.PlayersDealt < _playersMin || p.Hand.PlayersDealt > _playersMax) return false;
        double bb = p.StackBb;
        if (bb < _stackMin || bb >= _stackMax) return false;
        if (_allIn is { } ai && p.AllIn != ai) return false;
        if (_ante is { } an && p.Hand.HasAnte != an) return false;
        if (_whereHand != null && !_whereHand(p)) return false;
        return true;
    }
}
