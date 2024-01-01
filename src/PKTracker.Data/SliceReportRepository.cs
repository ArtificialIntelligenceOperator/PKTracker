using Npgsql;
using PKTracker.Core.Model;

namespace PKTracker.Data;

/// <summary>
/// Итоги по раздачам среза. Дисперсия хранится суммами (n, Σx, Σx²), а не готовым числом:
/// так строки разбивки складываются в итоговую строку без повторного запроса.
/// </summary>
public sealed record SliceStats(
    long Hands, long Vpip, long Pfr, long VpipCases,
    long SawFlop, long SawShowdown, long WonAtShowdown, long WonHand,
    long NetWon, long NetBbX100, long SdBbX100, long NonSdBbX100,
    long EvNetWon, long EvBbX100, long AllInHands, long Rake,
    double SumBbSq, long Tournaments)
{
    public static readonly SliceStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public double? VpipPct => VpipCases == 0 ? null : Vpip * 100.0 / VpipCases;
    public double? PfrPct => VpipCases == 0 ? null : Pfr * 100.0 / VpipCases;
    public double? WtsdPct => SawFlop == 0 ? null : SawShowdown * 100.0 / SawFlop;
    public double? WsdPct => SawShowdown == 0 ? null : WonAtShowdown * 100.0 / SawShowdown;
    public double? WwsfPct => SawFlop == 0 ? null : WonHand * 100.0 / SawFlop;

    public double? Bb100 => Hands == 0 ? null : NetBbX100 / 100.0 / Hands * 100;
    public double? EvBb100 => Hands == 0 ? null : EvBbX100 / 100.0 / Hands * 100;
    public double? NonSdBb100 => Hands == 0 ? null : NonSdBbX100 / 100.0 / Hands * 100;
    public double? SdBb100 => Hands == 0 ? null : SdBbX100 / 100.0 / Hands * 100;

    /// <summary>Удача в олл-инах: факт − EV, в bb/100. Плюс — везло.</summary>
    public double? LuckBb100 => Hands == 0 ? null : (NetBbX100 - EvBbX100) / 100.0 / Hands * 100;

    /// <summary>
    /// Стандартное отклонение в bb/100 по общепринятой в трекерах формуле: σ одной раздачи × 10.
    /// Меньше двух раздач — дисперсии нет.
    /// </summary>
    public double? StdDevBb100
    {
        get
        {
            if (Hands < 2) return null;
            double mean = NetBbX100 / 100.0 / Hands;
            double var1 = (SumBbSq - Hands * mean * mean) / (Hands - 1);
            return var1 <= 0 ? 0 : Math.Sqrt(var1) * 10;
        }
    }

    public static SliceStats operator +(SliceStats a, SliceStats b) => new(
        a.Hands + b.Hands, a.Vpip + b.Vpip, a.Pfr + b.Pfr, a.VpipCases + b.VpipCases,
        a.SawFlop + b.SawFlop, a.SawShowdown + b.SawShowdown, a.WonAtShowdown + b.WonAtShowdown, a.WonHand + b.WonHand,
        a.NetWon + b.NetWon, a.NetBbX100 + b.NetBbX100, a.SdBbX100 + b.SdBbX100, a.NonSdBbX100 + b.NonSdBbX100,
        a.EvNetWon + b.EvNetWon, a.EvBbX100 + b.EvBbX100, a.AllInHands + b.AllInHands, a.Rake + b.Rake,
        a.SumBbSq + b.SumBbSq, a.Tournaments + b.Tournaments);
}

/// <summary>Итоги по точкам решения среза: 3-бет и агрессия считаются только по ним.</summary>
public sealed record SliceActions(long ThreeBetCases, long ThreeBetHits, long Bets, long Raises, long Calls, long Folds)
{
    public static readonly SliceActions Empty = new(0, 0, 0, 0, 0, 0);

    public double? ThreeBetPct => ThreeBetCases == 0 ? null : ThreeBetHits * 100.0 / ThreeBetCases;

    /// <summary>AF постфлопа: (ставки + рейзы) / коллы — то же определение, что у стата <c>af</c>.</summary>
    public double? Af => Bets + Raises + Calls == 0 ? null
        : Calls == 0 ? double.PositiveInfinity : (Bets + Raises) / (double)Calls;

    /// <summary>AGG%: доля агрессии среди небесплатных действий постфлопа (чеки в знаменатель не входят).</summary>
    public double? AggPct => Bets + Raises + Calls + Folds == 0 ? null
        : (Bets + Raises) * 100.0 / (Bets + Raises + Calls + Folds);

    public static SliceActions operator +(SliceActions a, SliceActions b) => new(
        a.ThreeBetCases + b.ThreeBetCases, a.ThreeBetHits + b.ThreeBetHits,
        a.Bets + b.Bets, a.Raises + b.Raises, a.Calls + b.Calls, a.Folds + b.Folds);
}

/// <summary>Строка разбивки: ключ группы плюс её итоги.</summary>
public sealed record SliceGroup(string Key, SliceStats Stats, SliceActions Actions);

/// <summary>Строка вкладки «Следующее действие».</summary>
public sealed record NextActionRow(Street Street, Facing Facing, Choice Chosen, long Decisions, long Hands, long NetBbX100)
{
    public ActionSlice Slice => new(Street, Facing, Chosen);
    public string Label => Slice.Label;
    public double? Bb100 => Hands == 0 ? null : NetBbX100 / 100.0 / Hands * 100;
}

/// <summary>Точка графика среза (в bb — турнирные фишки и доллары в одном показателе не смешиваются).</summary>
public sealed record SlicePoint(DateTime PlayedAt, int NetBbX100, int EvBbX100, bool Showdown);

/// <summary>По чему разбивать: ключ группировки считается в SQL, подпись — в приложении.
/// Tournament бывает только у турниров, Limit — только у кеша.</summary>
public enum Breakdown { Tournament, Limit, Stack, Position, Month }

/// <summary>
/// Отчёты со срезами: одна и та же выборка считается целиком и в разбивке,
/// а клик по строке разбивки сужает срез. Только пул reports, вызывать из фонового потока (§10.3).
/// </summary>
public sealed class SliceReportRepository(TrackerDb db)
{
    private const string HeroesCte = """
        heroes AS (
            SELECT coalesce(p.canonical_id, p.player_id) AS id
              FROM player p JOIN hero_account h ON h.room_id = p.room_id AND h.nickname = p.nickname
        )
        """;

    /// <summary>Позиционные параметры Npgsql: номер выдаётся в порядке добавления.</summary>
    private sealed class Args
    {
        public readonly List<object> Values = new();
        public string Add(object v) { Values.Add(v); return "$" + Values.Count; }
        public void ApplyTo(NpgsqlCommand cmd) { foreach (var v in Values) cmd.Parameters.AddWithValue(v); }
    }

    /// <summary>
    /// Условие среза над hand_players hp / hands h / table_instance ti. Параметр добавляется только
    /// тогда, когда фильтр задан: иначе в запрос попадают NULL-сравнения, которые мешают планировщику
    /// пользоваться индексами.
    /// </summary>
    private static string Where(ReportSlice s, Args a)
    {
        var w = new List<string>
        {
            "hp.player_id IN (SELECT id FROM heroes)",
            "(hp.fact_bits & 1) <> 0",
            $"h.game_kind = ANY({a.Add(s.Kind == GameKind.Cash ? new short[] { 0 } : new short[] { 1, 2 })})",
        };
        if (s.Tournaments is { } t) w.Add($"ti.tournament_id = ANY({a.Add(t.ToArray())})");
        if (s.MinPlayers is { } lo) w.Add($"h.players_dealt >= {a.Add(lo)}");
        if (s.MaxPlayers is { } hi) w.Add($"h.players_dealt <= {a.Add(hi)}");
        if (s.Position is { } p) w.Add($"hp.position = {a.Add((short)p)}");
        if (s.MinStackBb is { } smin) w.Add($"hp.stack_bb_x10 >= {a.Add((int)Math.Round(smin * 10))}");
        if (s.MaxStackBb is { } smax && smax < 1e6) w.Add($"hp.stack_bb_x10 < {a.Add((int)Math.Round(smax * 10))}");
        if (s.Action is { } act)
            w.Add($"EXISTS (SELECT 1 FROM hand_decisions ad WHERE ad.hand_pk = hp.hand_pk AND ad.seat = hp.seat"
                + $" AND ad.street = {a.Add((short)act.Street)} AND ad.facing = {a.Add((short)act.Facing)}"
                + $" AND ad.chosen = {a.Add((short)act.Chosen)})");
        return string.Join("\n   AND ", w);
    }

    /// <summary>Ключ группировки в SQL; подпись к нему выдаёт <see cref="Label"/>.</summary>
    public static string KeyExpr(Breakdown by, bool tournament) => by switch
    {
        Breakdown.Tournament => "coalesce(ti.tournament_id, 0)::text",
        Breakdown.Limit => "h.bb::text",
        // Хедз-ап-позиции — отдельные строки: BB на троих и BB один на один это разные споты.
        Breakdown.Position => "(hp.position::int * 2 + CASE WHEN h.players_dealt <= 2 THEN 1 ELSE 0 END)::text",
        Breakdown.Stack => StackCase(tournament),
        _ => "to_char(h.played_at, 'YYYY-MM')",
    };

    private static string StackCase(bool tournament)
    {
        var bands = ReportSlice.StackBands(tournament);
        var sql = "(CASE";
        for (int i = 0; i < bands.Count - 1; i++)
            sql += $" WHEN hp.stack_bb_x10 < {(int)Math.Round(bands[i].To * 10)} THEN {i}";
        return sql + $" ELSE {bands.Count - 1} END)::text";
    }

    /// <summary>Расшифровка ключа группы в подпись строки.</summary>
    public static string Label(Breakdown by, string key, bool tournament, Func<long, string>? tournamentName = null) => by switch
    {
        Breakdown.Tournament => tournamentName?.Invoke(long.Parse(key)) ?? key,
        Breakdown.Limit => "NL" + key,
        Breakdown.Position => PositionKeyLabel(int.Parse(key)),
        Breakdown.Stack => ReportSlice.StackBands(tournament)[int.Parse(key)].Label,
        _ => key,
    };

    /// <summary>Ключ строки «Позиции» назад в позицию и признак хедз-апа — по нему строится срез при клике.</summary>
    public static (Position Pos, bool HeadsUp) PositionKey(string key)
    {
        int k = int.Parse(key);
        return ((Position)(k / 2), k % 2 == 1);
    }

    private static string PositionKeyLabel(int key)
    {
        var pos = (Position)(key / 2);
        var name = pos == Position.None ? "без позиции" : pos.ToString();
        return key % 2 == 1 ? name + ", хедз-ап" : name;
    }

    private const string Metrics = """
        count(*), count(*) FILTER (WHERE (hp.fact_bits & 2) <> 0), count(*) FILTER (WHERE (hp.fact_bits & 4) <> 0),
        count(*) FILTER (WHERE (h.flags & 1) = 0),
        count(*) FILTER (WHERE (hp.fact_bits & 8) <> 0), count(*) FILTER (WHERE (hp.fact_bits & 64) <> 0),
        count(*) FILTER (WHERE (hp.fact_bits & 128) <> 0),
        -- WonHand ставится и на выигранных префлопом раздачах, поэтому числитель WWSF обязан
        -- нести и SawFlop: иначе доля выходит больше 100%.
        count(*) FILTER (WHERE (hp.fact_bits & 8) <> 0 AND (hp.fact_bits & 256) <> 0),
        coalesce(sum(hp.net_won), 0), coalesce(sum(hp.net_won_bb_x100), 0),
        coalesce(sum(hp.net_won_bb_x100) FILTER (WHERE (hp.fact_bits & 64) <> 0), 0),
        coalesce(sum(hp.net_won_bb_x100) FILTER (WHERE (hp.fact_bits & 64) = 0), 0),
        coalesce(sum(coalesce(hp.ev_net_won, hp.net_won)), 0),
        coalesce(sum(coalesce(hp.ev_net_bb_x100, hp.net_won_bb_x100)), 0),
        count(*) FILTER (WHERE hp.ev_net_won IS NOT NULL), coalesce(sum(hp.rake_share), 0),
        coalesce(sum((hp.net_won_bb_x100 / 100.0) ^ 2), 0)::float8,
        count(DISTINCT ti.tournament_id)
        """;

    private const string FromHands = """
        FROM hand_players hp
          JOIN hands h ON h.hand_pk = hp.hand_pk
          JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
        """;

    private static SliceStats ReadStats(NpgsqlDataReader r, int o) => new(
        r.GetInt64(o), r.GetInt64(o + 1), r.GetInt64(o + 2), r.GetInt64(o + 3), r.GetInt64(o + 4), r.GetInt64(o + 5),
        r.GetInt64(o + 6), r.GetInt64(o + 7), r.GetInt64(o + 8), r.GetInt64(o + 9), r.GetInt64(o + 10), r.GetInt64(o + 11),
        r.GetInt64(o + 12), r.GetInt64(o + 13), r.GetInt64(o + 14), r.GetInt64(o + 15), r.GetDouble(o + 16), r.GetInt64(o + 17));

    /// <summary>Итог по всему срезу — карточка наверху отчёта.</summary>
    public async Task<SliceStats> SummaryAsync(ReportSlice slice, CancellationToken ct = default)
    {
        var a = new Args();
        string where = Where(slice, a);
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT {Metrics}
            {FromHands}
             WHERE {where}
            """, conn);
        a.ApplyTo(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadStats(r, 0) : SliceStats.Empty;
    }

    /// <summary>Итог по точкам решения среза: 3-бет и агрессия.</summary>
    public async Task<SliceActions> ActionsAsync(ReportSlice slice, CancellationToken ct = default)
    {
        var groups = await ActionsByAsync(slice, null, ct);
        return groups.TryGetValue("", out var v) ? v : SliceActions.Empty;
    }

    private async Task<Dictionary<string, SliceActions>> ActionsByAsync(ReportSlice slice, string? keyExpr, CancellationToken ct)
    {
        var a = new Args();
        string where = Where(slice, a);
        string key = keyExpr ?? "''::text";
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT {key},
                   count(*) FILTER (WHERE d.street = 0 AND d.bet_level = 2 AND d.facing = 2),
                   count(*) FILTER (WHERE d.street = 0 AND d.bet_level = 2 AND d.facing = 2 AND d.chosen = 4),
                   count(*) FILTER (WHERE d.street > 0 AND d.chosen = 3),
                   count(*) FILTER (WHERE d.street > 0 AND d.chosen = 4),
                   count(*) FILTER (WHERE d.street > 0 AND d.chosen = 2),
                   count(*) FILTER (WHERE d.street > 0 AND d.chosen = 0)
              FROM hand_decisions d
              JOIN hand_players hp ON hp.hand_pk = d.hand_pk AND hp.seat = d.seat
              JOIN hands h ON h.hand_pk = hp.hand_pk
              JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
             WHERE {where}
             GROUP BY 1
            """, conn);
        a.ApplyTo(cmd);
        var map = new Dictionary<string, SliceActions>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetString(0)] = new SliceActions(r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
        return map;
    }

    /// <summary>Разбивка среза по измерению: строки таблицы внизу отчёта.</summary>
    public async Task<List<SliceGroup>> BreakdownAsync(ReportSlice slice, Breakdown by, CancellationToken ct = default)
    {
        string keyExpr = KeyExpr(by, slice.IsTournament);
        var a = new Args();
        string where = Where(slice, a);
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT {keyExpr}, {Metrics}
            {FromHands}
             WHERE {where}
             GROUP BY 1
            """, conn);
        a.ApplyTo(cmd);
        var stats = new List<(string Key, SliceStats S)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) stats.Add((r.GetString(0), ReadStats(r, 1)));
        var actions = await ActionsByAsync(slice, keyExpr, ct);
        return stats.Select(x => new SliceGroup(x.Key, x.S, actions.GetValueOrDefault(x.Key, SliceActions.Empty))).ToList();
    }

    /// <summary>
    /// Вкладка «Следующее действие». Считается на двух уровнях: решений — сколько раз действие
    /// сделано (из них складывается частота), рук — в скольких раздачах, чтобы деньги не удваивались
    /// там, где игрок принимал на улице два решения подряд.
    /// </summary>
    public async Task<List<NextActionRow>> NextActionAsync(ReportSlice slice, CancellationToken ct = default)
    {
        var a = new Args();
        string where = Where(slice, a);
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte},
            dd AS (
                SELECT d.street, d.facing, d.chosen, d.hand_pk, d.seat,
                       count(*) AS n, min(hp.net_won_bb_x100) AS bb
                  FROM hand_decisions d
                  JOIN hand_players hp ON hp.hand_pk = d.hand_pk AND hp.seat = d.seat
                  JOIN hands h ON h.hand_pk = hp.hand_pk
                  JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
                 WHERE {where}
                 GROUP BY 1, 2, 3, 4, 5
            )
            SELECT street, facing, chosen, sum(n), count(*), coalesce(sum(bb), 0)
              FROM dd GROUP BY 1, 2, 3 ORDER BY 1, 2, 3
            """, conn);
        a.ApplyTo(cmd);
        var list = new List<NextActionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new NextActionRow((Street)r.GetInt16(0), (Facing)r.GetInt16(1), (Choice)r.GetInt16(2),
                r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        return list;
    }

    /// <summary>График среза в bb нарастающим итогом.</summary>
    public async Task<List<SlicePoint>> GraphAsync(ReportSlice slice, CancellationToken ct = default)
    {
        var a = new Args();
        string where = Where(slice, a);
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT h.played_at, hp.net_won_bb_x100, coalesce(hp.ev_net_bb_x100, hp.net_won_bb_x100), (hp.fact_bits & 64) <> 0
            {FromHands}
             WHERE {where}
             ORDER BY h.played_at, h.hand_pk
            """, conn);
        a.ApplyTo(cmd);
        var list = new List<SlicePoint>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new SlicePoint(r.GetDateTime(0), r.GetInt32(1), r.GetInt32(2), r.GetBoolean(3)));
        return list;
    }

    /// <summary>Раздачи среза — вкладка «Руки»; двойной клик по строке открывает реплеер.</summary>
    public async Task<List<HandRow>> HandsAsync(ReportSlice slice, int limit, int offset, CancellationToken ct = default)
    {
        var a = new Args();
        string where = Where(slice, a);
        string lim = a.Add(limit), off = a.Add(offset);
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT h.hand_pk, h.external_hand_id, h.played_at, coalesce(ti.table_name, ''), h.game_kind, h.sb, h.bb, hp.position,
                   hp.hole_card1, hp.hole_card2, hp.net_won, hp.net_won_bb_x100, h.total_pot, ti.currency, hp.ev_net_won
            {FromHands}
             WHERE {where}
             ORDER BY h.played_at DESC, h.hand_pk DESC
             LIMIT {lim} OFFSET {off}
            """, conn);
        a.ApplyTo(cmd);
        var list = new List<HandRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HandRow(r.GetInt64(0), r.GetInt64(1), r.GetDateTime(2), r.GetString(3), (GameKind)r.GetInt16(4),
                r.GetInt64(5), r.GetInt64(6), (Position)r.GetInt16(7),
                r.IsDBNull(8) ? null : new Card((byte)r.GetInt16(8)), r.IsDBNull(9) ? null : new Card((byte)r.GetInt16(9)),
                r.GetInt64(10), r.GetInt32(11), r.GetInt64(12), r.GetString(13).Trim(), r.IsDBNull(14) ? null : r.GetInt64(14)));
        return list;
    }
}
