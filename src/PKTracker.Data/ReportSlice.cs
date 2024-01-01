using PKTracker.Core.Model;

namespace PKTracker.Data;

/// <summary>Точка решения, по которой сужается срез: «Фолд против рейза, Префлоп».</summary>
public sealed record ActionSlice(Street Street, Facing Facing, Choice Chosen)
{
    public string Label => Facing == Facing.None
        ? $"{ChoiceRu(Chosen)}, {StreetRu(Street)}"
        : $"{ChoiceRu(Chosen)} против {FacingRu(Facing)}, {StreetRu(Street)}";

    public static string StreetRu(Street s) => s switch
    {
        Street.Preflop => "префлоп", Street.Flop => "флоп", Street.Turn => "тёрн", _ => "ривер",
    };

    public static string FacingRu(Facing f) => f switch { Facing.Bet => "ставки", Facing.Raise => "рейза", _ => "—" };

    public static string ChoiceRu(Choice c) => c switch
    {
        Choice.Fold => "Фолд", Choice.Check => "Чек", Choice.Call => "Колл", Choice.Bet => "Ставка", _ => "Рейз",
    };
}

/// <summary>
/// Срез отчёта: то, по чему сейчас отфильтрована статистика. Пустое поле — «все».
///
/// Каждое поле — это «фишка» в хлебных крошках наверху отчёта, и она же появляется, когда
/// в разбивке кликают по строке. Поэтому срез — запись со значениями, а не мутируемый объект:
/// снять фишку — значит взять копию без одного поля (<see cref="Chips"/>).
/// </summary>
public sealed record ReportSlice
{
    /// <summary>Кеш или турниры; турниры включают и SNG, и MTT.</summary>
    public GameKind Kind { get; init; } = GameKind.Mtt;

    /// <summary>Турниры выбранного формата; null — все. Формат считается в приложении, а не в SQL.</summary>
    public IReadOnlySet<long>? Tournaments { get; init; }
    public string? FormatLabel { get; init; }

    public int? MinPlayers { get; init; }
    public int? MaxPlayers { get; init; }
    public string? PlayersLabel { get; init; }

    public Position? Position { get; init; }

    public double? MinStackBb { get; init; }
    public double? MaxStackBb { get; init; }
    public string? StackLabel { get; init; }

    public ActionSlice? Action { get; init; }

    public bool IsTournament => Kind != GameKind.Cash;

    /// <summary>
    /// Режут ли фильтры сами раздачи. Деньги турнира (бай-ин, приз, ROI) — свойство турнира целиком,
    /// и к отфильтрованным рукам они не относятся; по этому флагу отчёт и предупреждает об этом.
    /// </summary>
    public bool NarrowsHands =>
        MinPlayers != null || MaxPlayers != null || Position != null || MinStackBb != null || MaxStackBb != null || Action != null;

    /// <summary>Хлебные крошки: подпись фишки и срез без неё.</summary>
    public IReadOnlyList<(string Label, ReportSlice Without)> Chips()
    {
        var list = new List<(string, ReportSlice)>();
        if (FormatLabel is { } f) list.Add((f, this with { Tournaments = null, FormatLabel = null }));
        if (PlayersLabel is { } p) list.Add((p, this with { MinPlayers = null, MaxPlayers = null, PlayersLabel = null }));
        if (Position is { } pos) list.Add((PositionLabel(pos), this with { Position = null }));
        if (StackLabel is { } s) list.Add((s, this with { MinStackBb = null, MaxStackBb = null, StackLabel = null }));
        if (Action is { } a) list.Add((a.Label, this with { Action = null }));
        return list;
    }

    public static string PositionLabel(Position p) => p == Core.Model.Position.None ? "без позиции" : p.ToString();

    /// <summary>
    /// Полосы стека. У турниров и кеша они разные: в кеше стек почти всегда около сотни блайндов,
    /// и турнирные полосы 10–20bb дали бы там всегда пустые строки.
    /// </summary>
    public static IReadOnlyList<(double From, double To, string Label)> StackBands(bool tournament) => tournament
        ? new (double, double, string)[]
        {
            (0, 10, "до 10bb"), (10, 20, "10–20bb"), (20, 30, "20–30bb"),
            (30, 50, "30–50bb"), (50, 80, "50–80bb"), (80, double.MaxValue, "80bb+"),
        }
        : new (double, double, string)[]
        {
            (0, 40, "до 40bb"), (40, 80, "40–80bb"), (80, 120, "80–120bb"),
            (120, 200, "120–200bb"), (200, double.MaxValue, "200bb+"),
        };
}

/// <summary>Деньги турниров среза: берутся из .ots, а не из раздач, поэтому живут отдельно от статистики.</summary>
public sealed record SliceMoney(int Count, int WithHands, int Entries, int Itm, long Cost, long Won)
{
    public static readonly SliceMoney Empty = new(0, 0, 0, 0, 0, 0);
    public long Profit => Won - Cost;
    public double? Roi => Cost == 0 ? null : Profit * 100.0 / Cost;
    public double? Abi => Entries == 0 ? null : Cost / 100.0 / Entries;
    public double? ItmPct => Entries == 0 ? null : Itm * 100.0 / Entries;

    public static SliceMoney Of(IEnumerable<TournamentRow> rows)
    {
        var all = rows.ToList();
        var money = all.Where(r => r.HasResult).ToList();
        return new SliceMoney(all.Count, all.Count(r => r.Hands > 0), money.Sum(r => r.Entries),
            // ITM — только настоящий приз или билет: баунти в PKO капают и без попадания в призы.
            money.Count(r => r.Prize + r.TicketValue > 0), money.Sum(r => r.Cost), money.Sum(r => r.Won));
    }
}
