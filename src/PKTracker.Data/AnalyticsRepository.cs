using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Engine;

namespace PKTracker.Data;

/// <summary>Фильтр аналитики по своим раздачам. Пустое поле — «все».</summary>
public sealed record AnalyticsFilter
{
    public GameKind Kind { get; init; } = GameKind.Mtt;
    public Position? Position { get; init; }
    public double? MinStackBb { get; init; }
    public double? MaxStackBb { get; init; }
    public int? MinPlayers { get; init; }
    public int? MaxPlayers { get; init; }
}

/// <summary>Клетка матрицы стартовых рук (индекс — <see cref="Card.HoleClass"/>).</summary>
public sealed record HoleClassRow(int Cls, long Hands, long Vpip, long Pfr, long NetBbX100, long EvBbX100, long Won)
{
    public string Name => Card.HoleClassName(Cls);
    public double? VpipPct => Hands == 0 ? null : Vpip * 100.0 / Hands;
    public double? PfrPct => Hands == 0 ? null : Pfr * 100.0 / Hands;
    public double NetBb => NetBbX100 / 100.0;
    public double? Bb100 => Hands == 0 ? null : NetBbX100 / 100.0 / Hands * 100;
    public double? EvBb100 => Hands == 0 ? null : EvBbX100 / 100.0 / Hands * 100;
}

/// <summary>
/// Строка отчёта «сила руки»: первая точка решения героя на улице с такой рукой.
/// Проценты действий — чем герой начал улицу; исходы — по раздаче целиком.
/// </summary>
public sealed record StrengthRow(Street Street, string Label, int Order, long Hands, long Bets, long Checks, long Calls, long Folds, long Raises,
    long Showdowns, long Won, long NetBbX100)
{
    public double? BetPct => Pct(Bets + Raises);
    public double? CheckPct => Pct(Checks);
    public double? CallPct => Pct(Calls);
    public double? FoldPct => Pct(Folds);
    public double? WtsdPct => Pct(Showdowns);
    public double? WonPct => Pct(Won);
    public double? Bb100 => Hands == 0 ? null : NetBbX100 / 100.0 / Hands * 100;
    private double? Pct(long k) => Hands == 0 ? null : k * 100.0 / Hands;
}

/// <summary>Последняя игровая сессия героя: раздачи подряд без перерыва дольше заданного.</summary>
/// <param name="CumBb">Накопленный итог в bb после каждой раздачи (для мини-графика).</param>
public sealed record SessionSnapshot(DateTime StartUtc, DateTime EndUtc, int Hands, int Tables, long NetBbX100, long EvBbX100, double[] CumBb)
{
    public static readonly SessionSnapshot Empty = new(default, default, 0, 0, 0, 0, Array.Empty<double>());
    public double NetBb => NetBbX100 / 100.0;
    public double? Bb100 => Hands == 0 ? null : NetBbX100 / 100.0 / Hands * 100;
    public double EvBb => EvBbX100 / 100.0;
}

/// <summary>Столбик графика-разреза: ключ, подпись и итоги.</summary>
public sealed record SplitRow(string Label, int Order, long Hands, long NetBbX100, long EvBbX100)
{
    public double? Bb100 => Hands == 0 ? null : NetBbX100 / 100.0 / Hands * 100;
    public double? EvBb100 => Hands == 0 ? null : EvBbX100 / 100.0 / Hands * 100;
    public double NetBb => NetBbX100 / 100.0;
}

public enum BreakdownKind { Position, Stack, Players, Weekday, Hour, Outcome }

/// <summary>
/// Аналитика по своим раздачам: матрица стартовых рук, сила руки на постфлопе и разрезы выигрыша.
/// Только завершённые раздачи из базы и только пул reports — это разбор после игры, не подсказка за столом.
/// </summary>
public sealed class AnalyticsRepository(TrackerDb db)
{
    private const string HeroesCte = """
        heroes AS (
            SELECT coalesce(p.canonical_id, p.player_id) AS id
              FROM player p JOIN hero_account h ON h.room_id = p.room_id AND h.nickname = p.nickname
        )
        """;

    private static short[] Kinds(GameKind kind) => kind == GameKind.Cash ? new short[] { 0 } : new short[] { 1, 2 };

    /// <summary>Общие условия фильтра по hand_players hp / hands h; параметры добавляются в команду.</summary>
    private static string Where(AnalyticsFilter f, NpgsqlCommand cmd)
    {
        var sql = new System.Text.StringBuilder("hp.player_id IN (SELECT id FROM heroes) AND (hp.fact_bits & 1) <> 0");
        void Add(string cond, object value)
        {
            cmd.Parameters.AddWithValue(value);
            sql.Append(" AND ").Append(cond.Replace("?", "$" + cmd.Parameters.Count));
        }
        Add("h.game_kind = ANY(?)", Kinds(f.Kind));
        if (f.Position is { } p) Add("hp.position = ?", (short)p);
        if (f.MinStackBb is { } lo) Add("hp.stack_bb_x10 >= ?", (int)Math.Round(lo * 10));
        if (f.MaxStackBb is { } hi) Add("hp.stack_bb_x10 < ?", (int)Math.Round(hi * 10));
        if (f.MinPlayers is { } pl) Add("h.players_dealt >= ?", pl);
        if (f.MaxPlayers is { } ph) Add("h.players_dealt <= ?", ph);
        return sql.ToString();
    }

    /// <summary>
    /// Последняя сессия героя: идём от самой свежей раздачи назад, пока перерыв между соседними не превысит
    /// <paramref name="gapMinutes"/>. Считаются только записанные (то есть завершённые) раздачи.
    /// </summary>
    public async Task<SessionSnapshot> LastSessionAsync(int gapMinutes, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT h.played_at, hp.net_won_bb_x100, coalesce(hp.ev_net_bb_x100, hp.net_won_bb_x100), h.table_instance_id
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE hp.player_id IN (SELECT id FROM heroes) AND (hp.fact_bits & 1) <> 0
             ORDER BY h.played_at DESC, h.hand_pk DESC
             LIMIT 20000
            """, conn);
        var rows = new List<(DateTime At, int Net, int Ev, long Table)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var at = r.GetDateTime(0);
                if (rows.Count > 0 && rows[^1].At - at > TimeSpan.FromMinutes(gapMinutes)) break;
                rows.Add((at, r.GetInt32(1), r.GetInt32(2), r.GetInt64(3)));
            }
        if (rows.Count == 0) return SessionSnapshot.Empty;
        rows.Reverse();
        var cum = new double[rows.Count];
        long net = 0, ev = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            net += rows[i].Net; ev += rows[i].Ev;
            cum[i] = net / 100.0;
        }
        return new SessionSnapshot(rows[0].At, rows[^1].At, rows.Count, rows.Select(x => x.Table).Distinct().Count(), net, ev, cum);
    }

    /// <summary>Свои стартовые руки: 169 классов, пустые клетки — с нулями.</summary>
    public async Task<HoleClassRow[]> StartingHandsAsync(AnalyticsFilter f, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        string where = Where(f, cmd);
        cmd.CommandText = $"""
            WITH {HeroesCte}
            SELECT hp.hole_class, count(*), count(*) FILTER (WHERE (hp.fact_bits & 2) <> 0), count(*) FILTER (WHERE (hp.fact_bits & 4) <> 0),
                   coalesce(sum(hp.net_won_bb_x100), 0), coalesce(sum(coalesce(hp.ev_net_bb_x100, hp.net_won_bb_x100)), 0),
                   count(*) FILTER (WHERE (hp.fact_bits & 256) <> 0)
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE {where} AND hp.hole_class IS NOT NULL
             GROUP BY hp.hole_class
            """;
        var res = new HoleClassRow[169];
        for (int i = 0; i < res.Length; i++) res[i] = new HoleClassRow(i, 0, 0, 0, 0, 0, 0);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            int cls = r.GetInt16(0);
            if (cls is < 0 or >= 169) continue;
            res[cls] = new HoleClassRow(cls, r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
        }
        return res;
    }

    /// <summary>
    /// Сила руки героя на постфлопе: по одной строке на улицу и класс руки. Считается первая точка решения
    /// на улице — раздача попадает в улицу один раз, сколько бы раз герой на ней ни ходил.
    /// </summary>
    public async Task<List<StrengthRow>> HandStrengthAsync(AnalyticsFilter f, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        string where = Where(f, cmd);
        // ctx_bits & 8 — FirstActionOnStreet; fact_bits: 64 — дошёл до вскрытия, 256 — выиграл раздачу.
        cmd.CommandText = $"""
            WITH {HeroesCte}
            SELECT d.street, d.made_hand, count(*),
                   count(*) FILTER (WHERE d.chosen = 3), count(*) FILTER (WHERE d.chosen = 1), count(*) FILTER (WHERE d.chosen = 2),
                   count(*) FILTER (WHERE d.chosen = 0), count(*) FILTER (WHERE d.chosen = 4),
                   count(*) FILTER (WHERE (hp.fact_bits & 64) <> 0), count(*) FILTER (WHERE (hp.fact_bits & 256) <> 0),
                   coalesce(sum(hp.net_won_bb_x100), 0)
              FROM hand_decisions d
              JOIN hand_players hp ON hp.hand_pk = d.hand_pk AND hp.seat = d.seat
              JOIN hands h ON h.hand_pk = d.hand_pk
             WHERE {where} AND d.street > 0 AND d.made_hand <> 0 AND (d.ctx_bits & 8) <> 0
             GROUP BY d.street, d.made_hand
            """;
        var acc = new Dictionary<(Street, int), StrengthRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var street = (Street)r.GetInt16(0);
            var (label, order) = StrengthGroup(r.GetInt16(1));
            var row = new StrengthRow(street, label, order, r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
                r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10));
            acc[(street, order)] = acc.TryGetValue((street, order), out var cur)
                ? cur with
                {
                    Hands = cur.Hands + row.Hands, Bets = cur.Bets + row.Bets, Checks = cur.Checks + row.Checks, Calls = cur.Calls + row.Calls,
                    Folds = cur.Folds + row.Folds, Raises = cur.Raises + row.Raises, Showdowns = cur.Showdowns + row.Showdowns,
                    Won = cur.Won + row.Won, NetBbX100 = cur.NetBbX100 + row.NetBbX100,
                }
                : row;
        }
        return acc.Values.OrderBy(x => x.Street).ThenByDescending(x => x.Order).ToList();
    }

    /// <summary>
    /// Группа отчёта для руки: готовая рука от пары — по классу; без пары — по дро (с дро рука играется
    /// иначе, чем «воздух», и смешивать их в одну строку «ничего» бессмысленно).
    /// </summary>
    public static (string Label, int Order) StrengthGroup(short madeHand)
    {
        var cls = MadeHands.Class(madeHand);
        bool fd = (madeHand & MadeHands.FlushDrawBit) != 0, oesd = (madeHand & MadeHands.OesdBit) != 0, gut = (madeHand & MadeHands.GutshotBit) != 0;
        if (cls >= MadeHandClass.Underpair) return (MadeHands.Ru(cls) + (fd || oesd ? " + дро" : ""), (int)cls * 10 + (fd || oesd ? 1 : 0));
        if (fd && (oesd || gut)) return ("Комбо-дро", 9);
        if (fd) return ("Флеш-дро", 8);
        if (oesd) return ("Стрит-дро (8 аутов)", 7);
        if (gut) return ("Гатшот", 6);
        return (MadeHands.Ru(cls), (int)cls);
    }

    /// <summary>Разрез выигрыша для графика: по позиции, стеку, числу игроков, дню недели, часу или исходу раздачи.</summary>
    public async Task<List<SplitRow>> BreakdownAsync(AnalyticsFilter f, BreakdownKind by, TimeZoneInfo? zone = null, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        string where = Where(f, cmd);
        // Время раздачи хранится в UTC; день недели и час считаем в поясе пользователя.
        string shift = ((long)(zone ?? TimeZoneInfo.Local).BaseUtcOffset.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string key = by switch
        {
            BreakdownKind.Position => "hp.position::int",
            BreakdownKind.Stack => f.Kind == GameKind.Cash
                ? "CASE WHEN hp.stack_bb_x10 < 400 THEN 0 WHEN hp.stack_bb_x10 < 800 THEN 1 WHEN hp.stack_bb_x10 < 1200 THEN 2 WHEN hp.stack_bb_x10 < 2000 THEN 3 ELSE 4 END"
                : "CASE WHEN hp.stack_bb_x10 < 100 THEN 0 WHEN hp.stack_bb_x10 < 200 THEN 1 WHEN hp.stack_bb_x10 < 300 THEN 2 WHEN hp.stack_bb_x10 < 500 THEN 3 WHEN hp.stack_bb_x10 < 800 THEN 4 ELSE 5 END",
            BreakdownKind.Players => "h.players_dealt::int",
            BreakdownKind.Weekday => $"extract(isodow FROM (h.played_at AT TIME ZONE 'UTC') + make_interval(secs => {shift}))::int",
            BreakdownKind.Hour => $"extract(hour FROM (h.played_at AT TIME ZONE 'UTC') + make_interval(secs => {shift}))::int",
            // Исход: где раздача закончилась для героя и чем. fact_bits: 8 — увидел флоп, 64 — вскрытие, 256 — выиграл.
            _ => """
                 CASE WHEN (hp.fact_bits & 8) = 0 THEN (CASE WHEN (hp.fact_bits & 256) <> 0 THEN 1 ELSE 0 END)
                      WHEN (hp.fact_bits & 64) <> 0 THEN (CASE WHEN (hp.fact_bits & 256) <> 0 THEN 4 ELSE 5 END)
                      ELSE (CASE WHEN (hp.fact_bits & 256) <> 0 THEN 2 ELSE 3 END) END
                 """,
        };
        cmd.CommandText = $"""
            WITH {HeroesCte}
            SELECT {key} AS k, count(*), coalesce(sum(hp.net_won_bb_x100), 0), coalesce(sum(coalesce(hp.ev_net_bb_x100, hp.net_won_bb_x100)), 0)
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE {where}
             GROUP BY 1 ORDER BY 1
            """;
        var list = new List<SplitRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            int k = r.GetInt32(0);
            list.Add(new SplitRow(Label(by, k, f.Kind), Order(by, k), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }
        return list.OrderBy(x => x.Order).ToList();
    }

    private static readonly Position[] TableOrder =
    {
        Position.UTG, Position.UTG1, Position.MP, Position.LJ, Position.HJ, Position.CO, Position.BTN, Position.SB, Position.BB, Position.None,
    };

    private static int Order(BreakdownKind by, int k) => by == BreakdownKind.Position ? Array.IndexOf(TableOrder, (Position)k) : k;

    private static string Label(BreakdownKind by, int k, GameKind kind) => by switch
    {
        BreakdownKind.Position => (Position)k == Position.None ? "—" : ((Position)k).ToString(),
        BreakdownKind.Stack => kind == GameKind.Cash
            ? k switch { 0 => "<40", 1 => "40–80", 2 => "80–120", 3 => "120–200", _ => "200+" }
            : k switch { 0 => "<10", 1 => "10–20", 2 => "20–30", 3 => "30–50", 4 => "50–80", _ => "80+" },
        BreakdownKind.Players => k.ToString(),
        BreakdownKind.Weekday => k switch { 1 => "Пн", 2 => "Вт", 3 => "Ср", 4 => "Чт", 5 => "Пт", 6 => "Сб", _ => "Вс" },
        BreakdownKind.Hour => $"{k:00}",
        _ => k switch
        {
            0 => "Сброс до флопа", 1 => "Взял до флопа", 2 => "Без вскрытия +", 3 => "Без вскрытия −", 4 => "Вскрытие +", _ => "Вскрытие −",
        },
    };
}
