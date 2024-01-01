using Npgsql;
using NpgsqlTypes;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Engine.Stats;
using ZstdSharp;

namespace PKTracker.Data.Import;

public sealed record ImportBatchResult(int Imported, int Duplicates, int Errors, IReadOnlyCollection<long> AffectedPlayers, long MaxHandPk)
{
    public static readonly ImportBatchResult Empty = new(0, 0, 0, Array.Empty<long>(), 0);
}

/// <summary>
/// Пишет пачку раздач: normalized + raw + агрегаты + watermark — ОДНОЙ транзакцией (§4.4, §15.7).
/// Live-путь = пачка из одной раздачи; bulk — пачки по 5–10k.
/// </summary>
public sealed class HandImporter
{
    private readonly TrackerDb _db;
    private readonly short _roomId;
    private readonly List<CompiledStat> _stats;
    private readonly HashSet<string> _heroes;

    public HandImporter(TrackerDb db, IEnumerable<CompiledStat> stats, IEnumerable<string> heroes, short? roomId = null)
    {
        _db = db;
        _roomId = roomId ?? db.RoomId;
        _stats = stats.Where(s => s.Scope != StatScope.Expression && s.StatId > 0).ToList();
        _heroes = new HashSet<string>(heroes, StringComparer.Ordinal);
    }

    public IReadOnlySet<string> Heroes => _heroes;

    /// <summary>Hero (любой из hero_account) сидит за столом (§6.1).</summary>
    public bool HeroSeated(ParsedHand h)
    {
        foreach (var s in h.Seats) if (_heroes.Contains(s.Name)) return true;
        return false;
    }

    public async Task<ImportBatchResult> ImportAsync(IReadOnlyList<HandParseResult> batch, CancellationToken ct = default)
    {
        if (batch.Count == 0) return ImportBatchResult.Empty;

        var errors = new List<(HandParseResult R, string Message)>();
        var candidates = new List<(ParsedHand Hand, HandContext Ctx)>();
        var seenIds = new HashSet<long>();
        int duplicates = 0;

        foreach (var r in batch)
        {
            if (!r.Ok) { errors.Add((r, r.Error!)); continue; }
            var h = r.Hand!;
            if (!HeroSeated(h))
            {
                errors.Add((r, "Rejected: hero is not seated at the table (room rules: only own hands)"));
                continue;
            }
            foreach (var s in h.Seats)
                if (_heroes.Contains(s.Name)) { s.IsHero = true; h.HeroName ??= s.Name; }
            if (!seenIds.Add(h.ExternalHandId)) { duplicates++; continue; }
            try
            {
                var ctx = HandContextBuilder.Build(h, computeEv: true);
                var inv = Invariants.Check(h, ctx);
                if (inv.Count > 0) { errors.Add((r, "Invariant: " + string.Join("; ", inv))); continue; }
                candidates.Add((h, ctx));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                errors.Add((r, "Context: " + ex.Message));
            }
        }

        // Импорты сериализуются: ручной импорт и авто-дозор не должны писать одну раздачу параллельно.
        await Gate.WaitAsync(ct);
        try
        {
            return await WriteAsync(candidates, errors, duplicates, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private async Task<ImportBatchResult> WriteAsync(List<(ParsedHand Hand, HandContext Ctx)> candidates,
        List<(HandParseResult R, string Message)> errors, int duplicates, CancellationToken ct)
    {
        await using var conn = await _db.Import.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Дедупликация с базой
        if (candidates.Count > 0)
        {
            var existing = new HashSet<long>();
            await using (var cmd = new NpgsqlCommand("SELECT external_hand_id FROM hands WHERE room_id = $1 AND external_hand_id = ANY($2)", conn, tx))
            {
                cmd.Parameters.AddWithValue(_roomId);
                cmd.Parameters.AddWithValue(candidates.Select(c => c.Hand.ExternalHandId).ToArray());
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) existing.Add(r.GetInt64(0));
            }
            duplicates += candidates.RemoveAll(c => existing.Contains(c.Hand.ExternalHandId));
        }

        if (errors.Count > 0) await WriteErrorsAsync(conn, tx, errors, ct);

        if (candidates.Count == 0)
        {
            await tx.CommitAsync(ct);
            return new ImportBatchResult(0, duplicates, errors.Count, Array.Empty<long>(), 0);
        }

        var players = await ResolvePlayersAsync(conn, tx, candidates.Select(c => c.Hand), ct);
        var tables = await ResolveTablesAsync(conn, tx, candidates.Select(c => c.Hand), ct);
        var pks = await AllocateHandPksAsync(conn, tx, candidates.Count, ct);

        await CopyHandsAsync(conn, candidates, pks, tables, _roomId, ct);
        await CopyRawAsync(conn, candidates, pks, ct);
        await CopyPlayersAsync(conn, candidates, pks, players, ct);
        await CopyActionsAsync(conn, candidates, pks, ct);
        await CopyBoardsAsync(conn, candidates, pks, ct);
        await CopyDecisionsAsync(conn, candidates, pks, players, ct);

        var agg = new StatAggregator(_stats);
        var affected = new HashSet<long>();
        foreach (var (h, ctx) in candidates)
        {
            var ids = SeatIds(h, ctx, players);
            foreach (var id in ids) if (id != 0) affected.Add(id);
            agg.Add(ctx, ids);
        }
        await AggWriter.UpsertAsync(conn, tx, agg, ct);

        long maxPk = pks.Max();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO agg_watermark (stat_id, def_version, last_hand_pk)
            SELECT stat_id, version, $1 FROM stat_definition WHERE stat_id = ANY($2)
            ON CONFLICT (stat_id) DO UPDATE SET last_hand_pk = greatest(agg_watermark.last_hand_pk, EXCLUDED.last_hand_pk)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue(maxPk);
            cmd.Parameters.AddWithValue(_stats.Select(s => s.StatId).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new ImportBatchResult(candidates.Count, duplicates, errors.Count, affected, maxPk);
    }

    internal static long[] SeatIds(ParsedHand h, HandContext ctx, IReadOnlyDictionary<string, long> players)
    {
        int size = Math.Max(ctx.Positions?.BySeat.Length ?? 0, h.Seats.Count == 0 ? 0 : h.Seats.Max(s => s.Seat) + 1);
        var ids = new long[size];
        foreach (var s in h.Seats) ids[s.Seat] = players[s.Name];
        return ids;
    }

    // ── справочники ───────────────────────────────────────────────────────────

    private async Task<Dictionary<string, long>> ResolvePlayersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IEnumerable<ParsedHand> hands, CancellationToken ct)
    {
        var seen = new Dictionary<string, (DateTime First, DateTime Last)>(StringComparer.Ordinal);
        foreach (var h in hands)
            foreach (var s in h.Seats)
                seen[s.Name] = seen.TryGetValue(s.Name, out var fl)
                    ? (fl.First < h.PlayedAtUtc ? fl.First : h.PlayedAtUtc, fl.Last > h.PlayedAtUtc ? fl.Last : h.PlayedAtUtc)
                    : (h.PlayedAtUtc, h.PlayedAtUtc);

        var names = seen.Keys.ToArray();
        await using var cmd = new NpgsqlCommand("""
            WITH src AS (
                SELECT * FROM unnest($2::text[], $3::timestamptz[], $4::timestamptz[]) AS t(nickname, first_seen, last_seen)
            ), up AS (
                INSERT INTO player (room_id, nickname, first_seen, last_seen)
                SELECT $1, nickname, first_seen, last_seen FROM src
                ON CONFLICT (room_id, nickname) DO UPDATE
                   SET first_seen = least(player.first_seen, EXCLUDED.first_seen),
                       last_seen  = greatest(player.last_seen, EXCLUDED.last_seen)
                RETURNING player_id, nickname, canonical_id
            )
            SELECT nickname, coalesce(canonical_id, player_id) FROM up
            """, conn, tx);
        cmd.Parameters.AddWithValue(_roomId);
        cmd.Parameters.AddWithValue(names);
        cmd.Parameters.AddWithValue(names.Select(n => seen[n].First).ToArray());
        cmd.Parameters.AddWithValue(names.Select(n => seen[n].Last).ToArray());
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }

    private async Task<Dictionary<(string, string), long>> ResolveTablesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IEnumerable<ParsedHand> hands, CancellationToken ct)
    {
        var map = new Dictionary<(string, string), long>();
        foreach (var h in hands.GroupBy(h => (h.ExternalGameId, h.ExternalTableId)).Select(g => g.First()))
        {
            if (h.TournamentId is { } tid)
            {
                await using var tc = new NpgsqlCommand("""
                    INSERT INTO tournament (tournament_id, room_id, name, started_at, currency, format)
                    VALUES ($1, $2, $3, $4, $5, $6)
                    ON CONFLICT (tournament_id) DO UPDATE SET
                        started_at = least(tournament.started_at, EXCLUDED.started_at),
                        -- .ots не содержит названия, формата и валюты: турнир, заведённый итогами,
                        -- остаётся безымянным, пока не придут раздачи. Дописываем только пустое,
                        -- чтобы не затирать то, что уже известно из .ots.
                        name = coalesce(nullif(tournament.name, ''), EXCLUDED.name),
                        currency = coalesce(nullif(tournament.currency, ''), EXCLUDED.currency),
                        format = coalesce(tournament.format, EXCLUDED.format)
                    """, conn, tx);
                tc.Parameters.AddWithValue(tid);
                tc.Parameters.AddWithValue(_roomId);
                var name = h.TableName;
                int hash = name.LastIndexOf(" #", StringComparison.Ordinal);
                tc.Parameters.AddWithValue(hash > 0 ? name[..hash] : name);
                tc.Parameters.AddWithValue(h.PlayedAtUtc);
                tc.Parameters.AddWithValue(h.Currency);
                tc.Parameters.AddWithValue((short)h.GameKind);
                await tc.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = new NpgsqlCommand("""
                INSERT INTO table_instance (room_id, game_kind, external_game_id, external_table_id, table_name, tournament_id, max_seats, currency, scale)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                ON CONFLICT (room_id, external_game_id, external_table_id) DO UPDATE SET table_name = EXCLUDED.table_name
                RETURNING table_instance_id
                """, conn, tx);
            cmd.Parameters.AddWithValue(_roomId);
            cmd.Parameters.AddWithValue((short)h.GameKind);
            cmd.Parameters.AddWithValue(h.ExternalGameId);
            cmd.Parameters.AddWithValue(h.ExternalTableId);
            cmd.Parameters.AddWithValue(h.TableName);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, (object?)h.TournamentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((short)h.MaxSeats);
            cmd.Parameters.AddWithValue(h.Currency);
            cmd.Parameters.AddWithValue((short)h.Scale);
            map[(h.ExternalGameId, h.ExternalTableId)] = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return map;
    }

    private static async Task<long[]> AllocateHandPksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int n, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT nextval(pg_get_serial_sequence('hands', 'hand_pk')) FROM generate_series(1, $1)", conn, tx);
        cmd.Parameters.AddWithValue(n);
        var res = new long[n];
        int i = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) res[i++] = r.GetInt64(0);
        return res;
    }

    private static async Task WriteErrorsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<(HandParseResult R, string Message)> errors, CancellationToken ct)
    {
        foreach (var (r, msg) in errors)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO import_error (source_file, file_offset, message, body) VALUES ($1, $2, $3, $4)
                ON CONFLICT (source_file, file_offset) DO UPDATE SET message = EXCLUDED.message, body = EXCLUDED.body, seen_at = now()
                """, conn, tx);
            cmd.Parameters.AddWithValue(r.SourceFile);
            cmd.Parameters.AddWithValue(r.Offset);
            cmd.Parameters.AddWithValue(msg);
            cmd.Parameters.AddWithValue(r.RawText);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ── COPY BINARY ───────────────────────────────────────────────────────────

    private static async Task CopyHandsAsync(NpgsqlConnection conn, List<(ParsedHand Hand, HandContext Ctx)> c, long[] pks,
        Dictionary<(string, string), long> tables, short roomId, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync("""
            COPY hands (hand_pk, room_id, external_hand_id, table_instance_id, played_at, game_kind, max_seats, players_dealt,
                        button_seat, sb, bb, ante, tourney_level, total_pot, rake, jp_fee, board_count, hero_seat, flags, context_version)
            FROM STDIN (FORMAT BINARY)
            """, ct);
        for (int i = 0; i < c.Count; i++)
        {
            var (h, ctx) = c[i];
            var hero = h.Seats.FirstOrDefault(s => s.IsHero && s.Dealt);
            await w.StartRowAsync(ct);
            await w.WriteAsync(pks[i], NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(roomId, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync(h.ExternalHandId, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(tables[(h.ExternalGameId, h.ExternalTableId)], NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(h.PlayedAtUtc, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync((short)h.GameKind, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync((short)h.MaxSeats, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync((short)ctx.Hand.PlayersDealt, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync((short)h.ButtonSeat, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync(h.SmallBlind, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(h.BigBlind, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(h.Ante, NpgsqlDbType.Bigint, ct);
            if (h.TournamentLevel is { } lvl) await w.WriteAsync((short)lvl, NpgsqlDbType.Smallint, ct); else await w.WriteNullAsync(ct);
            await w.WriteAsync(h.TotalPot, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(h.Rake, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(h.TotalExtraFees, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync((short)Math.Max(1, h.Boards.Count), NpgsqlDbType.Smallint, ct);
            if (hero != null) await w.WriteAsync((short)hero.Seat, NpgsqlDbType.Smallint, ct); else await w.WriteNullAsync(ct);
            await w.WriteAsync((int)h.Flags, NpgsqlDbType.Integer, ct);
            await w.WriteAsync(HandContextBuilder.ContextVersion, NpgsqlDbType.Smallint, ct);
        }
        await w.CompleteAsync(ct);
    }

    private static async Task CopyRawAsync(NpgsqlConnection conn, List<(ParsedHand Hand, HandContext Ctx)> c, long[] pks, CancellationToken ct)
    {
        using var zstd = new Compressor(9);
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY hand_raw (hand_pk, body, codec, source_file, file_offset) FROM STDIN (FORMAT BINARY)", ct);
        for (int i = 0; i < c.Count; i++)
        {
            var h = c[i].Hand;
            await w.StartRowAsync(ct);
            await w.WriteAsync(pks[i], NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(zstd.Wrap(System.Text.Encoding.UTF8.GetBytes(h.RawText)).ToArray(), NpgsqlDbType.Bytea, ct);
            await w.WriteAsync((short)0, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync(h.SourceFile, NpgsqlDbType.Text, ct);
            await w.WriteAsync(h.FileOffset, NpgsqlDbType.Bigint, ct);
        }
        await w.CompleteAsync(ct);
    }

    internal static async Task CopyPlayersAsync(NpgsqlConnection conn, List<(ParsedHand Hand, HandContext Ctx)> c, long[] pks,
        IReadOnlyDictionary<string, long> players, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync("""
            COPY hand_players (hand_pk, seat, player_id, position, start_stack, stack_bb_x10, hole_card1, hole_card2, hole_class,
                               invested, collected, net_won, net_won_bb_x100, rake_share, fact_bits, ev_net_won, ev_net_bb_x100)
            FROM STDIN (FORMAT BINARY)
            """, ct);
        for (int i = 0; i < c.Count; i++)
        {
            foreach (var p in c[i].Ctx.Players)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(pks[i], NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((short)p.Seat, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(players[p.PlayerName], NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((short)p.Position, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(p.StartStack, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(p.StackBbX10, NpgsqlDbType.Smallint, ct);
                if (p.HoleCard1 is { } c1) await w.WriteAsync((short)c1.Value, NpgsqlDbType.Smallint, ct); else await w.WriteNullAsync(ct);
                if (p.HoleCard2 is { } c2) await w.WriteAsync((short)c2.Value, NpgsqlDbType.Smallint, ct); else await w.WriteNullAsync(ct);
                if (p.HoleClass is { } hc) await w.WriteAsync(hc, NpgsqlDbType.Smallint, ct); else await w.WriteNullAsync(ct);
                await w.WriteAsync(p.Invested, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(p.Collected, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(p.NetWon, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(p.NetWonBbX100, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(p.RakeShare, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((long)p.Bits, NpgsqlDbType.Bigint, ct);
                if (p.EvNetWon is { } ev)
                {
                    await w.WriteAsync(ev, NpgsqlDbType.Bigint, ct);
                    await w.WriteAsync(p.EvNetBbX100, NpgsqlDbType.Integer, ct);
                }
                else
                {
                    await w.WriteNullAsync(ct);
                    await w.WriteNullAsync(ct);
                }
            }
        }
        await w.CompleteAsync(ct);
    }

    internal static async Task CopyActionsAsync(NpgsqlConnection conn, List<(ParsedHand Hand, HandContext Ctx)> c, long[] pks, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY hand_actions (hand_pk, seq, street, seat, action, amount, to_amount, pot_before, is_all_in) FROM STDIN (FORMAT BINARY)", ct);
        for (int i = 0; i < c.Count; i++)
        {
            foreach (var a in c[i].Ctx.Actions)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(pks[i], NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(a.Seq, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)a.Street, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)a.Seat, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)a.Action, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(a.Amount, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(a.ToAmount, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(a.PotBefore, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(a.IsAllIn, NpgsqlDbType.Boolean, ct);
            }
        }
        await w.CompleteAsync(ct);
    }

    internal static async Task CopyBoardsAsync(NpgsqlConnection conn, List<(ParsedHand Hand, HandContext Ctx)> c, long[] pks, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY hand_boards (hand_pk, board_no, c1, c2, c3, c4, c5) FROM STDIN (FORMAT BINARY)", ct);
        for (int i = 0; i < c.Count; i++)
        {
            var boards = c[i].Hand.Boards;
            for (int b = 0; b < boards.Count; b++)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(pks[i], NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((short)(b + 1), NpgsqlDbType.Smallint, ct);
                for (int k = 0; k < 5; k++)
                {
                    if (k < boards[b].Count) await w.WriteAsync((short)boards[b][k].Value, NpgsqlDbType.Smallint, ct);
                    else await w.WriteNullAsync(ct);
                }
            }
        }
        await w.CompleteAsync(ct);
    }

    internal static async Task CopyDecisionsAsync(NpgsqlConnection conn, List<(ParsedHand Hand, HandContext Ctx)> c, long[] pks,
        IReadOnlyDictionary<string, long> players, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync("""
            COPY hand_decisions (hand_pk, seq, seat, player_id, street, position, facing, bet_level, my_last_level, to_call,
                                 pot_before, eff_stack, players_in, players_behind, num_limpers, num_callers, ctx_bits, chosen,
                                 chosen_amount, size_pct, is_all_in, hand_strength, board_texture, stack_bucket,
                                 vs_position, opener_position, to_amount, line_bits, facing_amount)
            FROM STDIN (FORMAT BINARY)
            """, ct);
        for (int i = 0; i < c.Count; i++)
        {
            foreach (var d in c[i].Ctx.Decisions)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(pks[i], NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(d.Seq, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.Seat, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(players[d.PlayerName], NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((short)d.Street, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.Position, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.Facing, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.BetLevel, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.MyLastLevel, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(d.ToCall, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(d.PotBefore, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(d.EffStack, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((short)d.PlayersIn, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.PlayersBehind, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.NumLimpers, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.NumCallers, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((long)d.Ctx, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync((short)d.Chosen, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(d.ChosenAmount, NpgsqlDbType.Bigint, ct);
                if (d.SizePct is { } sp) await w.WriteAsync(sp, NpgsqlDbType.Smallint, ct); else await w.WriteNullAsync(ct);
                await w.WriteAsync(d.IsAllIn, NpgsqlDbType.Boolean, ct);
                if (d.HandStrength is { } hs) await w.WriteAsync(hs, NpgsqlDbType.Smallint, ct); else await w.WriteNullAsync(ct);
                await w.WriteAsync(d.BoardTexture, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.StackBucket, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.VsPosition, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)d.OpenerPosition, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(d.ToAmount, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(d.LineBits, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(d.FacingAmount, NpgsqlDbType.Bigint, ct);
            }
        }
        await w.CompleteAsync(ct);
    }
}

/// <summary>Запись дельт агрегатов: COPY во временную таблицу + один INSERT … ON CONFLICT DO UPDATE (§4.4).</summary>
public static class AggWriter
{
    public static async Task UpsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StatAggregator agg, CancellationToken ct,
        string table = "player_stat_agg")
    {
        if (agg.Count == 0) return;
        await using (var cmd = new NpgsqlCommand("""
            CREATE TEMP TABLE IF NOT EXISTS agg_stage (
                player_id bigint, stat_id integer, game_kind smallint, dealt_bucket smallint, position smallint,
                stack_bucket smallint, cases integer, hits integer, sum_value bigint) ON COMMIT DELETE ROWS
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY agg_stage (player_id, stat_id, game_kind, dealt_bucket, position, stack_bucket, cases, hits, sum_value) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var (k, v) in agg.Values)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(k.PlayerId, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(k.StatId, NpgsqlDbType.Integer, ct);
                await w.WriteAsync((short)k.GameKind, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)k.DealtBucket, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)k.Position, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync((short)k.StackBucket, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(v.Cases, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(v.Hits, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(v.Sum, NpgsqlDbType.Bigint, ct);
            }
            await w.CompleteAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO {table} AS a (player_id, stat_id, game_kind, dealt_bucket, position, stack_bucket, cases, hits, sum_value)
            SELECT player_id, stat_id, game_kind, dealt_bucket, position, stack_bucket, cases, hits, sum_value FROM agg_stage
            ON CONFLICT (player_id, stat_id, game_kind, dealt_bucket, position, stack_bucket) DO UPDATE
               SET cases = a.cases + EXCLUDED.cases, hits = a.hits + EXCLUDED.hits, sum_value = a.sum_value + EXCLUDED.sum_value
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand("TRUNCATE agg_stage", conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);
    }
}
