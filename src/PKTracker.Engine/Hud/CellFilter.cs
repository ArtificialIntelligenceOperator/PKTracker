using System.Text.Json.Serialization;
using PKTracker.Core.Model;

namespace PKTracker.Engine.Hud;

/// <summary>
/// Сужение стата под конкретную ячейку раскладки (§5.4).
///
/// Одна и та же статистика стоит в десятках ячеек, отличаясь только «обстановкой»: позицией,
/// полосой стека, числом игроков за столом, был ли олл-ин. Заводить под каждую ячейку отдельный стат —
/// это сотни определений и сотни ребилдов; вместо этого ячейка ссылается на общий стат и добавляет
/// фильтр. Фильтр применяется поверх opportunity стата, числитель не трогает.
///
/// Пустой фильтр (все поля null) ничего не сужает — <see cref="IsEmpty"/>.
/// </summary>
public sealed class CellFilter
{
    /// <summary>Позиции самого игрока: имена <see cref="Position"/> («BTN», «CO», …).</summary>
    public string[]? Pos { get; init; }
    /// <summary>Позиция последнего агрессора — «BB vs BTN», «Fold vs DONK».</summary>
    public string[]? VsPos { get; init; }
    /// <summary>Позиция открывшего банк рейзом — фильтр 4-бет-спотов по опенрейзеру.</summary>
    public string[]? OpenPos { get; init; }
    /// <summary>Начальное количество игроков за столом.</summary>
    public int? PlayersMin { get; init; }
    public int? PlayersMax { get; init; }
    /// <summary>Игроков, оставшихся в раздаче к моменту решения.</summary>
    public int? InHandMin { get; init; }
    public int? InHandMax { get; init; }
    /// <summary>Эффективный стек в bb: включительно снизу, исключительно сверху.</summary>
    public double? StackMin { get; init; }
    public double? StackMax { get; init; }
    /// <summary>Размер собственной ставки «до», bb — «опен ≤ 4bb».</summary>
    public double? ToBbMin { get; init; }
    public double? ToBbMax { get; init; }
    /// <summary>true — только олл-ин действия, false — только не-олл-ин, null — не важно.</summary>
    public bool? AllIn { get; init; }
    /// <summary>true — только раздачи с анте (колонки NO / ANTE).</summary>
    public bool? Ante { get; init; }
    /// <summary>Улица (для статов, определённых сразу на нескольких улицах).</summary>
    public string? Street { get; init; }
    /// <summary>Произвольное выражение на языке статов — всё, чего не хватило полям выше.</summary>
    public string? Where { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Pos == null && VsPos == null && OpenPos == null && PlayersMin == null && PlayersMax == null
        && InHandMin == null && InHandMax == null && StackMin == null && StackMax == null
        && ToBbMin == null && ToBbMax == null && AllIn == null && Ante == null && Street == null && Where == null;

    /// <summary>
    /// Можно ли посчитать фильтр по агрегатам. player_stat_agg хранит только три измерения
    /// (позиция, число игроков-корзина, стек-корзина), остальное живёт лишь в normalized.
    /// Панель HUD читает агрегаты, попапы — normalized, поэтому граница проходит здесь.
    /// </summary>
    [JsonIgnore]
    public bool AggCompatible =>
        VsPos == null && OpenPos == null && InHandMin == null && InHandMax == null
        && ToBbMin == null && ToBbMax == null && AllIn == null && Ante == null && Street == null && Where == null
        && StackAlignedToBuckets;

    /// <summary>Границы стека совпадают с границами корзин агрегата (10 / 20 / 40 / 100 bb).</summary>
    [JsonIgnore]
    private bool StackAlignedToBuckets =>
        (StackMin == null || Array.IndexOf(BucketEdges, StackMin.Value) >= 0) &&
        (StackMax == null || Array.IndexOf(BucketEdges, StackMax.Value) >= 0);

    private static readonly double[] BucketEdges = { 0, 10, 20, 40, 100 };

    /// <summary>Корзины стека агрегата, попадающие в диапазон [StackMin, StackMax).</summary>
    public IEnumerable<byte> StackBuckets()
    {
        for (byte b = 0; b < 5; b++)
        {
            double lo = b switch { 0 => 0, 1 => 10, 2 => 20, 3 => 40, _ => 100 };
            double hi = b switch { 0 => 10, 1 => 20, 2 => 40, 3 => 100, _ => double.PositiveInfinity };
            if (StackMin is { } mn && hi <= mn) continue;
            if (StackMax is { } mx && lo >= mx) continue;
            yield return b;
        }
    }

    /// <summary>Корзины «начального количества игроков» (0 HU, 1 = 3-4, 2 = 5-6, 3 = 7+), попадающие в диапазон.</summary>
    public IEnumerable<byte> DealtBuckets()
    {
        for (byte b = 0; b < 4; b++)
        {
            int lo = b switch { 0 => 2, 1 => 3, 2 => 5, _ => 7 };
            int hi = b switch { 0 => 2, 1 => 4, 2 => 6, _ => 10 };
            if (PlayersMin is { } mn && hi < mn) continue;
            if (PlayersMax is { } mx && lo > mx) continue;
            yield return b;
        }
    }

    public HashSet<Position>? PositionSet() => Parse(Pos);

    internal static HashSet<Position>? Parse(string[]? names)
    {
        if (names == null || names.Length == 0) return null;
        var set = new HashSet<Position>();
        foreach (var n in names)
            if (Enum.TryParse<Position>(n, ignoreCase: true, out var p)) set.Add(p);
        return set.Count > 0 ? set : null;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Pos != null) parts.Add("поз: " + string.Join("/", Pos));
        if (VsPos != null) parts.Add("против: " + string.Join("/", VsPos));
        if (OpenPos != null) parts.Add("опен: " + string.Join("/", OpenPos));
        if (PlayersMin != null || PlayersMax != null) parts.Add($"игроков {PlayersMin?.ToString() ?? ""}–{PlayersMax?.ToString() ?? ""}");
        if (InHandMin != null || InHandMax != null) parts.Add($"в руке {InHandMin?.ToString() ?? ""}–{InHandMax?.ToString() ?? ""}");
        if (StackMin != null || StackMax != null) parts.Add($"стек {StackMin?.ToString() ?? ""}–{StackMax?.ToString() ?? ""}bb");
        if (ToBbMin != null || ToBbMax != null) parts.Add($"размер {ToBbMin?.ToString() ?? ""}–{ToBbMax?.ToString() ?? ""}bb");
        if (AllIn != null) parts.Add(AllIn.Value ? "олл-ин" : "не олл-ин");
        if (Ante != null) parts.Add(Ante.Value ? "с анте" : "без анте");
        if (Street != null) parts.Add(Street);
        if (Where != null) parts.Add(Where);
        return string.Join(", ", parts);
    }
}
