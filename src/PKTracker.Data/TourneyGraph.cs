using PKTracker.Core.Model;

namespace PKTracker.Data;

/// <summary>
/// Раздача Hero в турнире для графика фишек и Chip EV (§7.1). `ev_net_won` заполнен только там,
/// где был олл-ин с известными картами; в остальных раздачах EV совпадает с фактом.
/// </summary>
public sealed record TourneyHandRow(long TournamentId, int Players, Position Position, int StackBbX10, long NetWon, long? EvNetWon)
{
    /// <summary>Результат с поправкой на эквити в олл-ине — то, из чего строится линия Chip EV.</summary>
    public long Ev => EvNetWon ?? NetWon;

    public bool AllIn => EvNetWon != null;

    public double StackBb => StackBbX10 / 10.0;
}

/// <summary>
/// Фильтр графика: формат турнира, число игроков за столом (хедз-ап, трое),
/// позиция Hero и глубина стека. Пустое поле — «все».
/// </summary>
public sealed record TourneyGraphFilter(
    IReadOnlySet<long>? Tournaments = null,
    int? MinPlayers = null,
    int? MaxPlayers = null,
    Position? Position = null,
    double? MinStackBb = null,
    double? MaxStackBb = null)
{
    public bool Matches(TourneyHandRow r) =>
        (Tournaments is null || Tournaments.Contains(r.TournamentId))
        && (MinPlayers is null || r.Players >= MinPlayers)
        && (MaxPlayers is null || r.Players <= MaxPlayers)
        && (Position is null || r.Position == Position)
        && (MinStackBb is null || r.StackBb >= MinStackBb)
        && (MaxStackBb is null || r.StackBb < MaxStackBb);
}

/// <summary>Две линии нарастающим итогом (в фишках) и итоги по выборке.</summary>
public sealed record TourneyGraph(double[] Chips, double[] ChipEv, int Hands, long ChipNet, long ChipEvNet, int AllIns, int Tournaments)
{
    /// <summary>Удача в олл-инах: факт − EV.</summary>
    public long Luck => ChipNet - ChipEvNet;
}

public static class TourneyGraphBuilder
{
    /// <summary>
    /// Стандартная модель Chip EV (она же All-in EV): по каждой раздаче берём
    /// факт, а если был олл-ин с известными картами — матожидание банка по эквити, и суммируем нарастающим
    /// итогом. Раздачи должны прийти в порядке игры.
    /// </summary>
    public static TourneyGraph Build(IEnumerable<TourneyHandRow> rows, TourneyGraphFilter? filter = null)
    {
        var chips = new List<double>();
        var ev = new List<double>();
        var tournaments = new HashSet<long>();
        long net = 0, evNet = 0;
        int allIns = 0;

        foreach (var r in rows)
        {
            if (filter != null && !filter.Matches(r)) continue;
            net += r.NetWon;
            evNet += r.Ev;
            if (r.AllIn) allIns++;
            tournaments.Add(r.TournamentId);
            chips.Add(net / 100.0);
            ev.Add(evNet / 100.0);
        }

        return new TourneyGraph(chips.ToArray(), ev.ToArray(), chips.Count, net, evNet, allIns, tournaments.Count);
    }
}
