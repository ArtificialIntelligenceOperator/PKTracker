using System.Globalization;
using Npgsql;
using PKTracker.Core.Model;

namespace PKTracker.Data;

public sealed record OverviewRow(long Hands, long NetWon, long NetBbX100, long Rake, long WonAtSd, long WonNonSd,
    long VpipHands, long PfrHands, double Hours, int Sessions, long EvNetWon, long EvBbX100, long AllInHands)
{
    public double Bb100 => Hands == 0 ? 0 : NetBbX100 / 100.0 / Hands * 100;
    public double EvBb100 => Hands == 0 ? 0 : EvBbX100 / 100.0 / Hands * 100;
    public double VpipPct => Hands == 0 ? 0 : VpipHands * 100.0 / Hands;
    public double PfrPct => Hands == 0 ? 0 : PfrHands * 100.0 / Hands;
}

public sealed record GraphPoint(DateTime PlayedAt, long NetWon, int NetBbX100, bool Showdown, long EvNetWon, int EvBbX100);

public sealed record SessionRow(DateTime Start, DateTime End, long Hands, long NetWon, long NetBbX100, int Tables, int Tournaments = 0)
{
    public TimeSpan Duration => End - Start;
    public double Bb100 => Hands == 0 ? 0 : NetBbX100 / 100.0 / Hands * 100;
}

public sealed record GroupRow(string Key, long Hands, long NetWon, long NetBbX100, long VpipHands, long PfrHands)
{
    public double Bb100 => Hands == 0 ? 0 : NetBbX100 / 100.0 / Hands * 100;
    public double VpipPct => Hands == 0 ? 0 : VpipHands * 100.0 / Hands;
    public double PfrPct => Hands == 0 ? 0 : PfrHands * 100.0 / Hands;
}

public sealed record TournamentRow(long TournamentId, string? Name, DateTime? Started, long Hands, long? BuyIn, long? Fee,
    string? Currency, int Entries, int? BestFinish, long Prize, long TicketValue, long Bounty, int? PlayerCount,
    GameKind? Kind = null, int? MaxSeats = null, bool IsKo = false, short KoKind = 0)
{
    public long Cost => ((BuyIn ?? 0) + (Fee ?? 0)) * Math.Max(Entries, 1);
    public long Won => Prize + TicketValue + Bounty;
    public bool HasResult => Entries > 0;

    /// <summary>Стоимость одного входа: по ней турниры одного формата и различаются.</summary>
    public long? EntryCost => BuyIn is null ? null : BuyIn + (Fee ?? 0);

    // «$5.00+$0.50 USD», «€9+€1», «$4.50+$4.50+$1.00 USD» (нокаут: баунти + призовой фонд + сбор) — только в начале названия:
    // сумма где-то в середине («Sunday $109 Special») стоимостью входа не является.
    private static readonly System.Text.RegularExpressions.Regex BuyInInName = new(
        @"^\s*[$€£]?(\d+(?:\.\d{1,2})?)\s*\+\s*[$€£]?(\d+(?:\.\d{1,2})?)(?:\s*\+\s*[$€£]?(\d+(?:\.\d{1,2})?))?(?:\s+([A-Z]{3})\b)?",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Бай-ин из названия турнира — когда в базе его нет. Стоимость входа приходит из итогов турнира; без них все
    /// турниры сваливались в один формат «бай-ин ?». Но часть румов пишет её прямо в заголовке раздачи
    /// («$5.00+$0.50 USD»), и заголовок сохраняется как название турнира: этого хватает, чтобы различать форматы
    /// и показать бай-ин. Призы, место и ROI отсюда не появляются — они по-прежнему только из итогов.
    /// </summary>
    public TournamentRow WithBuyInFromName()
    {
        if (BuyIn != null || Name is null || BuyInInName.Match(Name) is not { Success: true } m) return this;
        // Деньги — в сотых и без double: разбор через decimal с инвариантной культурой.
        static long Cents(string s) => (long)Math.Round(decimal.Parse(s, CultureInfo.InvariantCulture) * 100m);
        long first = Cents(m.Groups[1].Value), second = Cents(m.Groups[2].Value);
        // Последнее слагаемое — сбор рума; всё, что перед ним, идёт в призовой фонд и баунти.
        var (buyIn, fee) = m.Groups[3].Success ? (first + second, Cents(m.Groups[3].Value)) : (first, second);
        // «CHP» у турнира без итогов — это валюта фишек из раздач, а не валюта входа.
        string? currency = m.Groups[4].Success && Currency is null or "" or "CHP" ? m.Groups[4].Value : Currency;
        return this with { BuyIn = buyIn, Fee = fee, Currency = currency };
    }

    /// <summary>
    /// Формат турнира для группировки статистики: стоимость входа + тип (MTT/SNG) + размер стола + KO.
    /// Тип берётся из раздач (game_kind), а если раздач нет — по размеру поля из .ots.
    /// </summary>
    public string FormatLabel
    {
        get
        {
            var kind = Kind switch
            {
                GameKind.Sng => "SNG",
                GameKind.Mtt => "MTT",
                _ => PlayerCount is { } pc ? (pc <= 10 ? "SNG" : "MTT") : "турнир",
            };
            // Spin & Go: стол на троих и 100% первому — свой формат, $EV по нему не считается
            if (kind == "SNG" && (MaxSeats == 3 || PlayerCount == 3)) kind = "Spin & Go";
            var parts = new List<string>
            {
                EntryCost is { } c ? (c / 100.0).ToString("$0.00", CultureInfo.InvariantCulture) : "бай-ин ?",
                kind,
            };
            if (MaxSeats is > 0 && kind != "Spin & Go") parts.Add($"{MaxSeats}-max");
            if (KoKind == 2) parts.Add("Mystery KO");
            else if (IsKo) parts.Add("PKO");
            // «CHP» — фишки: так помечен турнир, у которого валюта входа неизвестна. В названии формата это шум.
            if (Currency is { Length: > 0 } cur && cur != "USD" && cur != "CHP") parts.Add(cur);
            return string.Join(" ", parts);
        }
    }
}

public sealed record HandRow(long HandPk, long ExternalId, DateTime PlayedAt, string Table, GameKind Kind, long Sb, long Bb,
    Position Position, Card? C1, Card? C2, long NetWon, int NetBbX100, long TotalPot, string Currency, long? EvNetWon)
{
    public string Cards => C1 is { } a && C2 is { } b ? $"{a} {b}" : "";
    public string Stakes => Kind == GameKind.Cash ? $"${Sb / 100.0:0.##}/${Bb / 100.0:0.##}" : $"{Sb / 100}/{Bb / 100}";
}

public sealed record PlayerSearchRow(long PlayerId, string Nickname, long Hands, DateTime? LastSeen);

public sealed record NoteRow(long NoteId, DateTime CreatedAt, string Body, short? ColorTag);

public sealed record ImportErrorRow(DateTime SeenAt, string? SourceFile, long? Offset, string? Message, string? Body);

public sealed record BreakdownRow(string Key, long Cases, long Hits)
{
    public double Pct => Cases == 0 ? 0 : Hits * 100.0 / Cases;
}

/// <summary>PlayerId нужен реплееру, чтобы показать рядом с местом ту же панель HUD, что за столом.</summary>
public sealed record ReplaySeat(int Seat, string Name, long StartStack, Position Position, Card? C1, Card? C2, bool IsHero,
    long NetWon, long PlayerId);

public sealed record ReplayHand(long HandPk, long ExternalId, DateTime PlayedAt, string Table, GameKind Kind, int MaxSeats,
    int ButtonSeat, long Sb, long Bb, long Ante, long TotalPot, long Rake, string Currency, HandFlags Flags,
    List<ReplaySeat> Seats, List<(short Seq, Street Street, int Seat, ActionType Action, long Amount, long ToAmount, long PotBefore, bool AllIn)> Actions,
    List<List<Card>> Boards);

public sealed record ActiveTable(long TableInstanceId, string ExternalGameId, string ExternalTableId, string TableName, GameKind Kind,
    long? TournamentId, int MaxSeats, DateTime LastHandAt, int? HeroSeat, List<(int Seat, string Name, long PlayerId)> Seats);

/// <summary>
/// Порядок позиций для таблиц: как ходят за столом префлоп — от UTG к баттону, затем блайнды. Значения enum
/// идут иначе (BTN = 0) и менять их нельзя: они хранятся в базе.
/// </summary>
public static class PositionOrder
{
    public static int AtTable(Position p) => p switch
    {
        Position.UTG => 0, Position.UTG1 => 1, Position.MP => 2, Position.LJ => 3, Position.HJ => 4, Position.CO => 5,
        Position.BTN => 6, Position.SB => 7, Position.BB => 8, _ => 9,
    };
}

/// <summary>Отчёты дашборда: только пул reports (§15.10), вызывать из фонового потока (§10.3).</summary>
public sealed class ReportRepository(TrackerDb db)
{
    private const string HeroesCte = """
        heroes AS (
            SELECT coalesce(p.canonical_id, p.player_id) AS id
              FROM player p JOIN hero_account h ON h.room_id = p.room_id AND h.nickname = p.nickname
        )
        """;

    private static short[] Kinds(GameKind kind) => kind == GameKind.Cash ? new short[] { 0 } : new short[] { 1, 2 };

    public async Task<OverviewRow> OverviewAsync(GameKind kind, int sessionGapMinutes, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte},
            hh AS (
                SELECT h.played_at, hp.net_won, hp.net_won_bb_x100, hp.rake_share, hp.fact_bits,
                       coalesce(hp.ev_net_won, hp.net_won) AS ev_won, coalesce(hp.ev_net_bb_x100, hp.net_won_bb_x100) AS ev_bb,
                       hp.ev_net_won IS NOT NULL AS has_ev
                  FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
                 WHERE hp.player_id IN (SELECT id FROM heroes) AND h.game_kind = ANY($1) AND (hp.fact_bits & 1) <> 0
            ), g AS (
                SELECT played_at, CASE WHEN played_at - lag(played_at) OVER (ORDER BY played_at) > make_interval(mins => $2)
                                       THEN 1 ELSE 0 END AS brk FROM hh
            ), s AS (
                SELECT played_at, sum(brk) OVER (ORDER BY played_at ROWS UNBOUNDED PRECEDING) AS sid FROM g
            ), ses AS (
                SELECT sid, extract(epoch FROM max(played_at) - min(played_at)) AS secs FROM s GROUP BY sid
            )
            SELECT count(*), coalesce(sum(net_won), 0), coalesce(sum(net_won_bb_x100), 0), coalesce(sum(rake_share), 0),
                   coalesce(sum(net_won) FILTER (WHERE (fact_bits & 64) <> 0), 0),
                   coalesce(sum(net_won) FILTER (WHERE (fact_bits & 64) = 0), 0),
                   count(*) FILTER (WHERE (fact_bits & 2) <> 0), count(*) FILTER (WHERE (fact_bits & 4) <> 0),
                   (SELECT coalesce(sum(secs), 0) FROM ses)::float8 / 3600, (SELECT count(*) FROM ses)::int,
                   coalesce(sum(ev_won), 0), coalesce(sum(ev_bb), 0), count(*) FILTER (WHERE has_ev)
              FROM hh
            """, conn);
        cmd.Parameters.AddWithValue(Kinds(kind));
        cmd.Parameters.AddWithValue(sessionGapMinutes);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new OverviewRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
            r.GetInt64(6), r.GetInt64(7), r.GetDouble(8), r.GetInt32(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12));
    }

    public async Task<List<GraphPoint>> GraphAsync(GameKind kind, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT h.played_at, hp.net_won, hp.net_won_bb_x100, (hp.fact_bits & 64) <> 0,
                   coalesce(hp.ev_net_won, hp.net_won), coalesce(hp.ev_net_bb_x100, hp.net_won_bb_x100)
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE hp.player_id IN (SELECT id FROM heroes) AND h.game_kind = ANY($1) AND (hp.fact_bits & 1) <> 0
             ORDER BY h.played_at, h.hand_pk
            """, conn);
        cmd.Parameters.AddWithValue(Kinds(kind));
        var list = new List<GraphPoint>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new GraphPoint(r.GetDateTime(0), r.GetInt64(1), r.GetInt32(2), r.GetBoolean(3), r.GetInt64(4), r.GetInt32(5)));
        return list;
    }

    /// <summary>
    /// Сессии — серии раздач без длинного перерыва. Для турниров рядом со столами считается число турниров:
    /// игрока пересаживают, и «столов» за вечер набирается больше, чем сыграно турниров.
    /// </summary>
    public async Task<List<SessionRow>> SessionsAsync(int gapMinutes, GameKind kind = GameKind.Cash, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte},
            hh AS (
                SELECT h.played_at, h.table_instance_id, ti.tournament_id, hp.net_won, hp.net_won_bb_x100
                  FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
                  LEFT JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
                 WHERE hp.player_id IN (SELECT id FROM heroes) AND h.game_kind = ANY($2) AND (hp.fact_bits & 1) <> 0
            ), g AS (
                SELECT *, CASE WHEN played_at - lag(played_at) OVER (ORDER BY played_at) > make_interval(mins => $1)
                               THEN 1 ELSE 0 END AS brk FROM hh
            ), s AS (
                SELECT *, sum(brk) OVER (ORDER BY played_at ROWS UNBOUNDED PRECEDING) AS sid FROM g
            )
            SELECT min(played_at), max(played_at), count(*), sum(net_won), sum(net_won_bb_x100), count(DISTINCT table_instance_id)::int,
                   count(DISTINCT tournament_id)::int
              FROM s GROUP BY sid ORDER BY min(played_at) DESC
            """, conn);
        cmd.Parameters.AddWithValue(gapMinutes);
        cmd.Parameters.AddWithValue(Kinds(kind));
        var list = new List<SessionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SessionRow(r.GetDateTime(0), r.GetDateTime(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5), r.GetInt32(6)));
        return list;
    }

    /// <summary>
    /// Сколько у Hero раздач в кеше и в турнирах. По этому числу вкладки выбирают вид игры при первом показе:
    /// турнирный игрок не должен на каждой вкладке сам переключать «Кеш» на «Турниры».
    /// </summary>
    public async Task<(long Cash, long Tourney)> HeroHandCountsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT count(*) FILTER (WHERE h.game_kind = 0), count(*) FILTER (WHERE h.game_kind <> 0)
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE hp.player_id IN (SELECT id FROM heroes) AND (hp.fact_bits & 1) <> 0
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
    }

    public async Task<List<GroupRow>> ByPositionAsync(GameKind kind, CancellationToken ct = default)
    {
        // В базе позиции идут в порядке значений enum (BTN, SB, BB, UTG…); в таблице — в порядке хода за столом.
        var rows = await GroupAsync(kind, "hp.position", k => k, ct);
        return rows.OrderBy(r => PositionOrder.AtTable((Position)short.Parse(r.Key)))
            .Select(r => r with { Key = (Position)short.Parse(r.Key) == Position.None ? "без позиции" : ((Position)short.Parse(r.Key)).ToString() })
            .ToList();
    }

    public async Task<List<GroupRow>> ByLimitAsync(CancellationToken ct = default) =>
        await GroupAsync(GameKind.Cash, "h.bb", k => $"NL{long.Parse(k)}", ct);

    public async Task<List<GroupRow>> ByTableSizeAsync(GameKind kind, CancellationToken ct = default) =>
        await GroupAsync(kind, "h.players_dealt", k => $"{k} игроков", ct);

    private async Task<List<GroupRow>> GroupAsync(GameKind kind, string key, Func<string, string> label, CancellationToken ct)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT ({key})::text, count(*), sum(hp.net_won), sum(hp.net_won_bb_x100),
                   count(*) FILTER (WHERE (hp.fact_bits & 2) <> 0), count(*) FILTER (WHERE (hp.fact_bits & 4) <> 0)
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE hp.player_id IN (SELECT id FROM heroes) AND h.game_kind = ANY($1) AND (hp.fact_bits & 1) <> 0
             GROUP BY {key} ORDER BY {key}
            """, conn);
        cmd.Parameters.AddWithValue(Kinds(kind));
        var list = new List<GroupRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new GroupRow(label(r.GetString(0)), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        return list;
    }

    public async Task<List<TournamentRow>> TournamentsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte},
            th AS (
                SELECT ti.tournament_id, count(*) AS hands, min(h.played_at) AS first_hand,
                       max(ti.game_kind) AS game_kind, max(ti.max_seats) AS max_seats
                  FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
                  JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
                 WHERE hp.player_id IN (SELECT id FROM heroes) AND ti.tournament_id IS NOT NULL
                 GROUP BY ti.tournament_id
            ), tr AS (
                SELECT tournament_id, count(*)::int AS entries, min(finish_position) AS best,
                       sum(prize) AS prize, sum(ticket_value) AS tickets, sum(coalesce(bounty_won, 0)) AS bounty
                  FROM tournament_result WHERE player_id IN (SELECT id FROM heroes) GROUP BY tournament_id
            )
            SELECT t.tournament_id, t.name, coalesce(t.started_at, th.first_hand), coalesce(th.hands, 0), t.buy_in, t.fee, t.currency,
                   coalesce(tr.entries, 0), tr.best, coalesce(tr.prize, 0), coalesce(tr.tickets, 0), coalesce(tr.bounty, 0), t.player_count,
                   th.game_kind, th.max_seats, coalesce(t.is_ko, false), coalesce(t.ko_kind, 0::smallint)
              FROM tournament t
              LEFT JOIN th ON th.tournament_id = t.tournament_id
              LEFT JOIN tr ON tr.tournament_id = t.tournament_id
             WHERE th.tournament_id IS NOT NULL OR tr.tournament_id IS NOT NULL
             ORDER BY 3 DESC NULLS LAST
            """, conn);
        var list = new List<TournamentRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new TournamentRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetDateTime(2),
                r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetString(6).Trim(), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8),
                r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.IsDBNull(12) ? null : r.GetInt32(12),
                r.IsDBNull(13) ? null : (GameKind)r.GetInt16(13), r.IsDBNull(14) ? null : r.GetInt16(14),
                r.GetBoolean(15), r.GetInt16(16)).WithBuyInFromName());
        }
        return list;
    }

    /// <summary>
    /// Раздачи Hero во всех турнирах, в порядке игры: из них строится график фишек и Chip EV
    /// и по ним же работают фильтры (число игроков за столом, позиция, глубина стека).
    /// </summary>
    public async Task<List<TourneyHandRow>> TournamentHandsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT ti.tournament_id, h.players_dealt, hp.position, hp.stack_bb_x10, hp.net_won, hp.ev_net_won
              FROM hand_players hp
              JOIN hands h ON h.hand_pk = hp.hand_pk
              JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
             WHERE hp.player_id IN (SELECT id FROM heroes) AND ti.tournament_id IS NOT NULL
             ORDER BY h.played_at, h.hand_pk
            """, conn);
        var list = new List<TourneyHandRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new TourneyHandRow(r.GetInt64(0), r.GetInt16(1), (Position)r.GetInt16(2), r.GetInt16(3),
                r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5)));
        }
        return list;
    }

    /// <summary>Руки Hero с серверной пагинацией (§1.1).</summary>
    public async Task<List<HandRow>> HeroHandsAsync(GameKind? kind, Position? position, int limit, int offset, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT h.hand_pk, h.external_hand_id, h.played_at, coalesce(ti.table_name, ''), h.game_kind, h.sb, h.bb, hp.position,
                   hp.hole_card1, hp.hole_card2, hp.net_won, hp.net_won_bb_x100, h.total_pot, ti.currency, hp.ev_net_won
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
              JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
             WHERE hp.player_id IN (SELECT id FROM heroes)
               AND ($1::smallint IS NULL OR h.game_kind = $1 OR ($1 = 1 AND h.game_kind = 2))
               AND ($2::smallint IS NULL OR hp.position = $2)
             ORDER BY h.played_at DESC, h.hand_pk DESC
             LIMIT $3 OFFSET $4
            """, conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Smallint, kind is null ? DBNull.Value : (object)(short)kind);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Smallint, position is null ? DBNull.Value : (object)(short)position);
        cmd.Parameters.AddWithValue(limit);
        cmd.Parameters.AddWithValue(offset);
        var list = new List<HandRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new HandRow(r.GetInt64(0), r.GetInt64(1), r.GetDateTime(2), r.GetString(3), (GameKind)r.GetInt16(4),
                r.GetInt64(5), r.GetInt64(6), (Position)r.GetInt16(7),
                r.IsDBNull(8) ? null : new Card((byte)r.GetInt16(8)), r.IsDBNull(9) ? null : new Card((byte)r.GetInt16(9)),
                r.GetInt64(10), r.GetInt32(11), r.GetInt64(12), r.GetString(13).Trim(), r.IsDBNull(14) ? null : r.GetInt64(14)));
        }
        return list;
    }

    /// <summary>
    /// Поиск рук Hero по произвольному выражению над фактами руки (§7). Условие вычисляется в приложении тем же
    /// скомпилированным делегатом, что и статы; строки читаются потоком от новых к старым до нужной страницы.
    /// </summary>
    public async Task<(List<HandRow> Rows, long Scanned)> HeroHandsFilteredAsync(Func<Engine.HandPlayerFacts, bool> filter, GameKind? kind,
        int limit, int offset, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT h.hand_pk, h.external_hand_id, h.played_at, coalesce(ti.table_name, ''), h.game_kind, h.sb, h.bb, hp.position,
                   hp.hole_card1, hp.hole_card2, hp.net_won, hp.net_won_bb_x100, h.total_pot, ti.currency, hp.ev_net_won,
                   hp.seat, hp.start_stack, hp.stack_bb_x10, hp.hole_class, hp.invested, hp.collected, hp.rake_share, hp.fact_bits,
                   h.flags, h.players_dealt, h.max_seats, h.ante
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
              JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
             WHERE hp.player_id IN (SELECT id FROM heroes)
               AND ($1::smallint IS NULL OR h.game_kind = $1 OR ($1 = 1 AND h.game_kind = 2))
             ORDER BY h.played_at DESC, h.hand_pk DESC
            """, conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Smallint, kind is null ? DBNull.Value : (object)(short)kind);
        var rows = new List<HandRow>();
        long scanned = 0;
        int matched = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            scanned++;
            var info = new Engine.HandInfo
            {
                GameKind = (GameKind)r.GetInt16(4), Flags = (HandFlags)r.GetInt32(23), PlayersDealt = r.GetInt16(24),
                MaxSeats = r.GetInt16(25), SmallBlind = r.GetInt64(5), BigBlind = r.GetInt64(6), Ante = r.GetInt64(26),
            };
            var facts = new Engine.HandPlayerFacts
            {
                Hand = info, Seat = (byte)r.GetInt16(15), PlayerName = "", Position = (Position)r.GetInt16(7),
                StartStack = r.GetInt64(16), StackBbX10 = r.GetInt16(17),
                StackBucket = Engine.HandContextBuilder.StackBucket(r.GetInt64(16), info.BigBlind),
                HoleCard1 = r.IsDBNull(8) ? null : new Card((byte)r.GetInt16(8)), HoleCard2 = r.IsDBNull(9) ? null : new Card((byte)r.GetInt16(9)),
                HoleClass = r.IsDBNull(18) ? null : r.GetInt16(18), Invested = r.GetInt64(19), Collected = r.GetInt64(20),
                RakeShare = r.GetInt64(21), Bits = (FactBits)r.GetInt64(22), EvNetWon = r.IsDBNull(14) ? null : r.GetInt64(14),
            };
            if (!filter(facts)) continue;
            if (matched++ < offset) continue;
            rows.Add(new HandRow(r.GetInt64(0), r.GetInt64(1), r.GetDateTime(2), r.GetString(3), info.GameKind, info.SmallBlind, info.BigBlind,
                facts.Position, facts.HoleCard1, facts.HoleCard2, r.GetInt64(10), r.GetInt32(11), r.GetInt64(12), r.GetString(13).Trim(), facts.EvNetWon));
            if (rows.Count >= limit) break;
        }
        return (rows, scanned);
    }

    public async Task<List<PlayerSearchRow>> SearchPlayersAsync(string prefix, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT p.player_id, p.nickname,
                   coalesce((SELECT sum(a.cases) FROM player_stat_agg a
                              JOIN stat_definition d ON d.stat_id = a.stat_id AND d.code = 'hands'
                             WHERE a.player_id = p.player_id), 0)::bigint AS hands,
                   p.last_seen
              FROM player p
             WHERE p.canonical_id IS NULL AND p.nickname ILIKE $1
             ORDER BY hands DESC, p.nickname
             LIMIT $2
            """, conn);
        cmd.Parameters.AddWithValue(prefix.Replace("%", "\\%").Replace("_", "\\_") + "%");
        cmd.Parameters.AddWithValue(limit);
        var list = new List<PlayerSearchRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PlayerSearchRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetDateTime(3)));
        return list;
    }

    public async Task<List<NoteRow>> NotesAsync(long playerId, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT note_id, created_at, body, color_tag FROM player_note WHERE player_id = $1 ORDER BY created_at DESC", conn);
        cmd.Parameters.AddWithValue(playerId);
        var list = new List<NoteRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new NoteRow(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt16(3)));
        return list;
    }

    public async Task AddNoteAsync(long playerId, string body, short? color, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO player_note (player_id, body, color_tag) VALUES ($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue(body);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Smallint, color is null ? DBNull.Value : (object)color);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ImportErrorRow>> ImportErrorsAsync(int limit = 500, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT seen_at, source_file, file_offset, message, body FROM import_error ORDER BY seen_at DESC LIMIT $1", conn);
        cmd.Parameters.AddWithValue(limit);
        var list = new List<ImportErrorRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ImportErrorRow(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    /// <summary>
    /// Суммарные агрегаты статов Hero и пула (все остальные игроки из вашей же базы) — для ликфайндера (§7).
    /// </summary>
    public async Task<(List<(int StatId, long Cases, long Hits, long Sum)> Hero, List<(int StatId, long Cases, long Hits, long Sum)> Pool)>
        HeroVsPoolAsync(GameKind kind, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH {HeroesCte}
            SELECT a.player_id IN (SELECT id FROM heroes), a.stat_id, sum(a.cases)::bigint, sum(a.hits)::bigint, sum(a.sum_value)::bigint
              FROM player_stat_agg a
             WHERE a.game_kind = ANY($1)
             GROUP BY 1, 2
            """, conn);
        cmd.Parameters.AddWithValue(Kinds(kind));
        var hero = new List<(int, long, long, long)>();
        var pool = new List<(int, long, long, long)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            (r.GetBoolean(0) ? hero : pool).Add((r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        return (hero, pool);
    }

    /// <summary>Находит раздачу по номеру рума (external_hand_id) или внутреннему hand_pk.</summary>
    public async Task<long?> ResolveHandPkAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT hand_pk FROM hands WHERE (room_id = $1 AND external_hand_id = $2) OR hand_pk = $2 ORDER BY (external_hand_id = $2) DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue(db.RoomId);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) is long pk ? pk : null;
    }

    /// <summary>Исходный текст раздачи из hand_raw (zstd).</summary>
    public async Task<string?> RawHandTextAsync(long handPk, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT body FROM hand_raw WHERE hand_pk = $1", conn);
        cmd.Parameters.AddWithValue(handPk);
        if (await cmd.ExecuteScalarAsync(ct) is not byte[] body) return null;
        using var dec = new ZstdSharp.Decompressor();
        return System.Text.Encoding.UTF8.GetString(dec.Unwrap(body));
    }

    public async Task<ReplayHand?> LoadReplayAsync(long handPk, CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        ReplayHand? hand = null;
        await using (var cmd = new NpgsqlCommand("""
            SELECT h.external_hand_id, h.played_at, coalesce(ti.table_name, ''), h.game_kind, h.max_seats, h.button_seat, h.sb, h.bb,
                   h.ante, h.total_pot, h.rake, ti.currency, h.flags, h.hero_seat
              FROM hands h JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
             WHERE h.hand_pk = $1
            """, conn))
        {
            cmd.Parameters.AddWithValue(handPk);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            hand = new ReplayHand(handPk, r.GetInt64(0), r.GetDateTime(1), r.GetString(2), (GameKind)r.GetInt16(3), r.GetInt16(4),
                r.GetInt16(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetString(11).Trim(),
                (HandFlags)r.GetInt32(12), new(), new(), new());
        }
        await using (var cmd = new NpgsqlCommand("""
            SELECT hp.seat, p.nickname, hp.start_stack, hp.position, hp.hole_card1, hp.hole_card2, (hp.fact_bits & 16384) <> 0, hp.net_won,
                   coalesce(p.canonical_id, hp.player_id)
              FROM hand_players hp JOIN player p ON p.player_id = hp.player_id
             WHERE hp.hand_pk = $1 ORDER BY hp.seat
            """, conn))
        {
            cmd.Parameters.AddWithValue(handPk);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                hand.Seats.Add(new ReplaySeat(r.GetInt16(0), r.GetString(1), r.GetInt64(2), (Position)r.GetInt16(3),
                    r.IsDBNull(4) ? null : new Card((byte)r.GetInt16(4)), r.IsDBNull(5) ? null : new Card((byte)r.GetInt16(5)),
                    r.GetBoolean(6), r.GetInt64(7), r.GetInt64(8)));
        }
        await using (var cmd = new NpgsqlCommand(
            "SELECT seq, street, seat, action, amount, to_amount, pot_before, is_all_in FROM hand_actions WHERE hand_pk = $1 ORDER BY seq", conn))
        {
            cmd.Parameters.AddWithValue(handPk);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                hand.Actions.Add((r.GetInt16(0), (Street)r.GetInt16(1), r.GetInt16(2), (ActionType)r.GetInt16(3), r.GetInt64(4),
                    r.GetInt64(5), r.GetInt64(6), r.GetBoolean(7)));
        }
        await using (var cmd = new NpgsqlCommand("SELECT c1, c2, c3, c4, c5 FROM hand_boards WHERE hand_pk = $1 ORDER BY board_no", conn))
        {
            cmd.Parameters.AddWithValue(handPk);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var b = new List<Card>();
                for (int i = 0; i < 5; i++) if (!r.IsDBNull(i)) b.Add(new Card((byte)r.GetInt16(i)));
                hand.Boards.Add(b);
            }
        }
        return hand;
    }

    /// <summary>Столы с раздачами за последнее время и рассадка их последней раздачи — для привязки HUD (§5.1, способ 2).</summary>
    public async Task<List<ActiveTable>> ActiveTablesAsync(TimeSpan window, NpgsqlDataSource? ds = null, CancellationToken ct = default)
    {
        await using var conn = await (ds ?? db.Reports).OpenConnectionAsync(ct);
        var tables = new List<ActiveTable>();
        var lastPk = new Dictionary<long, long>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT DISTINCT ON (h.table_instance_id) h.table_instance_id, h.hand_pk, ti.external_game_id, ti.external_table_id,
                   coalesce(ti.table_name, ''), ti.game_kind, ti.tournament_id, ti.max_seats, h.played_at, h.hero_seat
              FROM hands h JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
             WHERE h.played_at > now() - $1
             ORDER BY h.table_instance_id, h.played_at DESC
            """, conn))
        {
            cmd.Parameters.AddWithValue(window);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lastPk[r.GetInt64(0)] = r.GetInt64(1);
                tables.Add(new ActiveTable(r.GetInt64(0), r.GetString(2), r.GetString(3), r.GetString(4), (GameKind)r.GetInt16(5),
                    r.IsDBNull(6) ? null : r.GetInt64(6), r.GetInt16(7), r.GetDateTime(8), r.IsDBNull(9) ? null : r.GetInt16(9), new()));
            }
        }
        if (tables.Count == 0) return tables;
        await using (var cmd = new NpgsqlCommand("""
            SELECT hp.hand_pk, hp.seat, p.nickname, hp.player_id FROM hand_players hp JOIN player p ON p.player_id = hp.player_id
             WHERE hp.hand_pk = ANY($1)
            """, conn))
        {
            cmd.Parameters.AddWithValue(lastPk.Values.ToArray());
            var byPk = tables.ToDictionary(t => lastPk[t.TableInstanceId]);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) byPk[r.GetInt64(0)].Seats.Add((r.GetInt16(1), r.GetString(2), r.GetInt64(3)));
        }
        return tables;
    }
}
