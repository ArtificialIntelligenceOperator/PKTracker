using System.Text;
using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Data.Import;
using PKTracker.Engine;
using PKTracker.Engine.Stats;
using PKTracker.Parsers;
using ZstdSharp;

namespace PKTracker.Data;

public sealed record RebuildProgress(string Stage, long Done, long Total);

/// <summary>
/// Три режима пересчёта (§4.5): инкрементальный — в HandImporter; селективный — здесь, по hand_decisions/hand_players
/// без прохода по сырым раздачам; полный — из hand_raw после изменения парсера/HandContextBuilder.
/// </summary>
public sealed class StatRebuilder(TrackerDb db)
{
    private const int Batch = 20_000;

    // ── Восстановление контекстов из normalized ─────────────────────────────

    private static async Task<Dictionary<long, HandInfo>> LoadHandInfosAsync(NpgsqlConnection conn, long fromPk, long toPk, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT hand_pk, game_kind, flags, players_dealt, max_seats, sb, bb, ante
              FROM hands WHERE hand_pk > $1 AND hand_pk <= $2
            """, conn);
        cmd.Parameters.AddWithValue(fromPk);
        cmd.Parameters.AddWithValue(toPk);
        var map = new Dictionary<long, HandInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            map[r.GetInt64(0)] = new HandInfo
            {
                GameKind = (GameKind)r.GetInt16(1), Flags = (HandFlags)r.GetInt32(2), PlayersDealt = r.GetInt16(3),
                MaxSeats = r.GetInt16(4), SmallBlind = r.GetInt64(5), BigBlind = r.GetInt64(6), Ante = r.GetInt64(7),
            };
        }
        return map;
    }

    /// <summary>
    /// Прогоняет статы по normalized-данным в диапазоне hand_pk и отдаёт аккумулятор.
    /// </summary>
    private static async Task AggregateRangeAsync(NpgsqlConnection conn, StatAggregator agg, long fromPk, long toPk,
        bool decisions, bool players, CancellationToken ct)
    {
        var infos = await LoadHandInfosAsync(conn, fromPk, toPk, ct);
        var contexts = new Dictionary<long, (HandContext Ctx, Dictionary<byte, long> Ids)>();
        (HandContext, Dictionary<byte, long>) Ctx(long pk)
        {
            if (!contexts.TryGetValue(pk, out var c))
            {
                c = (new HandContext { Hand = infos[pk], Players = new(), Decisions = new() }, new Dictionary<byte, long>());
                contexts[pk] = c;
            }
            return c;
        }

        if (players)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT hand_pk, seat, player_id, position, start_stack, stack_bb_x10, hole_card1, hole_card2, hole_class,
                       invested, collected, rake_share, fact_bits, ev_net_won
                  FROM hand_players WHERE hand_pk > $1 AND hand_pk <= $2
                """, conn);
            cmd.Parameters.AddWithValue(fromPk);
            cmd.Parameters.AddWithValue(toPk);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long pk = r.GetInt64(0);
                var (ctx, ids) = Ctx(pk);
                byte seat = (byte)r.GetInt16(1);
                ids[seat] = r.GetInt64(2);
                ctx.Players.Add(new HandPlayerFacts
                {
                    Hand = ctx.Hand, Seat = seat, PlayerName = "", Position = (Position)r.GetInt16(3),
                    StartStack = r.GetInt64(4), StackBbX10 = r.GetInt16(5),
                    StackBucket = HandContextBuilder.StackBucket(r.GetInt64(4), ctx.Hand.BigBlind),
                    HoleCard1 = r.IsDBNull(6) ? null : new Card((byte)r.GetInt16(6)),
                    HoleCard2 = r.IsDBNull(7) ? null : new Card((byte)r.GetInt16(7)),
                    HoleClass = r.IsDBNull(8) ? null : r.GetInt16(8),
                    Invested = r.GetInt64(9), Collected = r.GetInt64(10), RakeShare = r.GetInt64(11),
                    Bits = (FactBits)r.GetInt64(12),
                    EvNetWon = r.IsDBNull(13) ? null : r.GetInt64(13),
                });
            }
        }

        if (decisions)
        {
            // fact_bits игрока — исход раздачи для статов вида «выиграл без вскрытия в таком-то споте».
            await using var cmd = new NpgsqlCommand($"""
                SELECT d.hand_pk, d.seq, d.seat, d.player_id, d.street, d.position, d.facing, d.bet_level, d.my_last_level, d.to_call, d.pot_before,
                       d.eff_stack, d.players_in, d.players_behind, d.num_limpers, d.num_callers, d.ctx_bits, d.chosen, d.chosen_amount,
                       d.size_pct, d.is_all_in, d.hand_strength, d.board_texture, d.stack_bucket,
                       d.vs_position, d.opener_position, d.to_amount, d.line_bits, d.facing_amount,
                       {DecisionColumns.Select("d")}, hp.fact_bits
                  FROM hand_decisions d
                  LEFT JOIN hand_players hp ON hp.hand_pk = d.hand_pk AND hp.seat = d.seat
                 WHERE d.hand_pk > $1 AND d.hand_pk <= $2
                """, conn);
            cmd.Parameters.AddWithValue(fromPk);
            cmd.Parameters.AddWithValue(toPk);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long pk = r.GetInt64(0);
                var (ctx, ids) = Ctx(pk);
                byte seat = (byte)r.GetInt16(2);
                ids[seat] = r.GetInt64(3);
                var x = DecisionColumns.Read(r, 29);
                ctx.Decisions.Add(new DecisionPoint
                {
                    Hand = ctx.Hand, Seq = r.GetInt16(1), Seat = seat, PlayerName = "",
                    Street = (Street)r.GetInt16(4), Position = (Position)r.GetInt16(5), Facing = (Facing)r.GetInt16(6),
                    BetLevel = r.GetInt16(7), MyLastLevel = r.GetInt16(8), ToCall = r.GetInt64(9), PotBefore = r.GetInt64(10),
                    EffStack = r.GetInt64(11), PlayersIn = r.GetInt16(12), PlayersBehind = r.GetInt16(13),
                    NumLimpers = r.GetInt16(14), NumCallers = r.GetInt16(15), Ctx = (CtxBits)r.GetInt64(16),
                    Chosen = (Choice)r.GetInt16(17), ChosenAmount = r.GetInt64(18),
                    SizePct = r.IsDBNull(19) ? null : r.GetInt16(19), IsAllIn = r.GetBoolean(20),
                    HandStrength = r.IsDBNull(21) ? null : r.GetInt16(21), BoardTexture = r.IsDBNull(22) ? (short)0 : r.GetInt16(22),
                    StackBucket = (byte)r.GetInt16(23),
                    VsPosition = (Position)r.GetInt16(24), OpenerPosition = (Position)r.GetInt16(25), ToAmount = r.GetInt64(26),
                    LineBits = r.GetInt16(27), FacingAmount = r.GetInt64(28),
                    PfaLineBits = x.PfaLine, OppLineBits = x.OppLine, PotRaises = x.PotRaises, PfLimpers = x.PfLimpers,
                    PfrPosition = x.PfrPos, FlopPlayers = x.FlopPlayers, FlopMask = x.FlopMask, VsStack = x.VsStack,
                    StartEffStack = x.StartEff, CallersBefore3b = x.CallersBefore3b, MadeHandBits = x.MadeHand,
                    Outcome = r.IsDBNull(29 + DecisionColumns.Count) ? 0 : (FactBits)r.GetInt64(29 + DecisionColumns.Count),
                });
            }
        }

        foreach (var (ctx, ids) in contexts.Values)
        {
            var arr = new long[256];
            foreach (var (seat, id) in ids) arr[seat] = id;
            agg.Add(ctx, arr);
        }
    }

    private static async Task<long> MaxHandPkAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT coalesce(max(hand_pk), 0) FROM hands", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ── Селективный ребилд ───────────────────────────────────────────────────

    /// <summary>Пересчёт только указанных статов по normalized-таблицам. HUD в это время видит остальные статы.</summary>
    public async Task RebuildStatsAsync(IReadOnlyList<CompiledStat> stats, IProgress<RebuildProgress>? progress = null, CancellationToken ct = default)
    {
        var plain = stats.Where(s => s.Scope != StatScope.Expression && s.StatId > 0).ToList();
        if (plain.Count == 0) return;
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        long max = await MaxHandPkAsync(conn, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = plain.Select(s => s.StatId).ToArray();
        await using (var del = new NpgsqlCommand("DELETE FROM player_stat_agg WHERE stat_id = ANY($1)", conn, tx))
        {
            del.Parameters.AddWithValue(ids);
            await del.ExecuteNonQueryAsync(ct);
        }
        bool needDecisions = plain.Any(s => s.Scope == StatScope.Decision);
        bool needPlayers = plain.Any(s => s.Scope == StatScope.Hand);
        for (long from = 0; from < max; from += Batch)
        {
            var agg = new StatAggregator(plain);
            await AggregateRangeAsync(conn, agg, from, Math.Min(from + Batch, max), needDecisions, needPlayers, ct);
            await AggWriter.UpsertAsync(conn, tx, agg, ct);
            progress?.Report(new RebuildProgress("stats", Math.Min(from + Batch, max), max));
        }
        await using (var wm = new NpgsqlCommand("""
            INSERT INTO agg_watermark (stat_id, def_version, last_hand_pk)
            SELECT stat_id, version, $1 FROM stat_definition WHERE stat_id = ANY($2)
            ON CONFLICT (stat_id) DO UPDATE SET def_version = EXCLUDED.def_version, last_hand_pk = EXCLUDED.last_hand_pk
            """, conn, tx))
        {
            wm.Parameters.AddWithValue(max);
            wm.Parameters.AddWithValue(ids);
            await wm.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // ── Пробный прогон стата из редактора (§4.3) ─────────────────────────────

    /// <summary>Прогоняет стат по последним <paramref name="lastHands"/> раздачам; cases == 0 — почти наверняка ошибка в условии.</summary>
    public async Task<(long Cases, long Hits)> TrialRunAsync(CompiledStat stat, int lastHands = 1000, CancellationToken ct = default)
    {
        if (stat.Scope == StatScope.Expression) return (0, 0);
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        long max = await MaxHandPkAsync(conn, ct);
        var trial = new CompiledStat
        {
            Definition = new StatDefinition
            {
                StatId = 1, Code = stat.Code, Title = stat.Definition.Title, Scope = stat.Scope,
                Opportunity = stat.Definition.Opportunity, Hit = stat.Definition.Hit, Value = stat.Definition.Value,
            },
            DecisionOpportunity = stat.DecisionOpportunity, DecisionHit = stat.DecisionHit,
            HandOpportunity = stat.HandOpportunity, HandHit = stat.HandHit, HandValue = stat.HandValue,
        };
        var agg = new StatAggregator(new[] { trial });
        await AggregateRangeAsync(conn, agg, Math.Max(0, max - lastHands), max,
            stat.Scope == StatScope.Decision, stat.Scope == StatScope.Hand, ct);
        long cases = 0, hits = 0;
        foreach (var v in agg.Values.Values) { cases += v.Cases; hits += v.Hits; }
        return (cases, hits);
    }

    // ── Сверка агрегатов с normalized (§15.7) ─────────────────────────────────

    public async Task<List<string>> VerifyAggregatesAsync(IReadOnlyList<CompiledStat> stats, CancellationToken ct = default)
    {
        var plain = stats.Where(s => s.Scope != StatScope.Expression && s.StatId > 0).ToList();
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        long max = await MaxHandPkAsync(conn, ct);
        var agg = new StatAggregator(plain);
        for (long from = 0; from < max; from += Batch)
            await AggregateRangeAsync(conn, agg, from, Math.Min(from + Batch, max), true, true, ct);

        var stored = new Dictionary<AggKey, AggValue>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT player_id, stat_id, game_kind, dealt_bucket, position, stack_bucket, cases, hits, sum_value FROM player_stat_agg", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var k = new AggKey(r.GetInt64(0), r.GetInt32(1), (byte)r.GetInt16(2), (byte)r.GetInt16(3), (byte)r.GetInt16(4), (byte)r.GetInt16(5));
                stored[k] = new AggValue { Cases = r.GetInt32(6), Hits = r.GetInt32(7), Sum = r.GetInt64(8) };
            }
        }

        var diffs = new List<string>();
        var byId = plain.ToDictionary(s => s.StatId, s => s.Code);
        foreach (var (k, v) in agg.Values)
        {
            if (!stored.Remove(k, out var s) || s.Cases != v.Cases || s.Hits != v.Hits || s.Sum != v.Sum)
                diffs.Add($"{byId[k.StatId]} player={k.PlayerId} pos={k.Position}: expected {v.Cases}/{v.Hits}/{v.Sum}, stored {s.Cases}/{s.Hits}/{s.Sum}");
        }
        foreach (var (k, s) in stored)
            if (byId.ContainsKey(k.StatId))
                diffs.Add($"{byId[k.StatId]} player={k.PlayerId}: unexpected stored {s.Cases}/{s.Hits}");
        return diffs;
    }

    // ── Полный ребилд из hand_raw ────────────────────────────────────────────

    /// <summary>
    /// Перестраивает normalized из hand_raw и агрегаты в теневой таблице; в конце — атомарный swap (§10.3, §15.7).
    /// HUD всё время читает старую player_stat_agg.
    /// </summary>
    public async Task FullRebuildAsync(IReadOnlyList<CompiledStat> stats, IEnumerable<string> heroes, IProgress<RebuildProgress>? progress = null, CancellationToken ct = default)
    {
        var plain = stats.Where(s => s.Scope != StatScope.Expression && s.StatId > 0).ToList();
        var heroSet = new HashSet<string>(heroes, StringComparer.Ordinal);
        // В базе могут лежать раздачи нескольких румов (рум в настройках меняли): текст каждой разбирает
        // парсер её рума, а не активного — иначе чужой формат обрывал бы ребилд на середине.
        var parsers = new Dictionary<string, PKTracker.Core.Abstractions.IHandHistoryParser>(StringComparer.OrdinalIgnoreCase);
        PKTracker.Core.Abstractions.IHandHistoryParser ParserOf(string room)
        {
            if (!parsers.TryGetValue(room, out var p)) parsers[room] = p = ParserRegistry.ForRoom(room);
            return p;
        }
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        long max = await MaxHandPkAsync(conn, ct);

        await Exec(conn, null, """
            DROP TABLE IF EXISTS player_stat_agg_shadow;
            CREATE TABLE player_stat_agg_shadow (LIKE player_stat_agg INCLUDING ALL);
            """, ct);

        using var dec = new Decompressor();
        for (long from = 0; from < max; from += Batch)
        {
            long to = Math.Min(from + Batch, max);
            var batch = new List<(long Pk, ParsedHand Hand, HandContext Ctx)>();
            await using (var cmd = new NpgsqlCommand("SELECT r.hand_pk, r.body, r.source_file, r.file_offset, rm.code FROM hand_raw r JOIN hands h ON h.hand_pk = r.hand_pk JOIN room rm ON rm.room_id = h.room_id WHERE r.hand_pk > $1 AND r.hand_pk <= $2 ORDER BY r.hand_pk", conn))
            {
                cmd.Parameters.AddWithValue(from);
                cmd.Parameters.AddWithValue(to);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var text = Encoding.UTF8.GetString(dec.Unwrap((byte[])r[1]));
                    var file = r.GetString(2);
                    var h = ParseOne(ParserOf(r.GetString(4)), text, file);
                    h.FileOffset = r.GetInt64(3);
                    foreach (var s in h.Seats) if (heroSet.Contains(s.Name)) s.IsHero = true;
                    batch.Add((r.GetInt64(0), h, HandContextBuilder.Build(h, computeEv: true)));
                }
            }
            if (batch.Count == 0) continue;

            await using var tx = await conn.BeginTransactionAsync(ct);
            var pks = batch.Select(b => b.Pk).ToArray();
            foreach (var table in new[] { "hand_players", "hand_actions", "hand_boards", "hand_decisions" })
            {
                await using var del = new NpgsqlCommand($"DELETE FROM {table} WHERE hand_pk = ANY($1)", conn, tx);
                del.Parameters.AddWithValue(pks);
                await del.ExecuteNonQueryAsync(ct);
            }

            var names = batch.SelectMany(b => b.Hand.Seats.Select(s => s.Name)).Distinct().ToArray();
            var players = new Dictionary<string, long>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT nickname, coalesce(canonical_id, player_id) FROM player WHERE room_id = $1 AND nickname = ANY($2)", conn, tx))
            {
                cmd.Parameters.AddWithValue(db.RoomId);
                cmd.Parameters.AddWithValue(names);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) players[r.GetString(0)] = r.GetInt64(1);
            }

            var list = batch.Select(b => (b.Hand, b.Ctx)).ToList();
            await HandImporter.CopyPlayersAsync(conn, list, pks, players, ct);
            await HandImporter.CopyActionsAsync(conn, list, pks, ct);
            await HandImporter.CopyBoardsAsync(conn, list, pks, ct);
            await HandImporter.CopyDecisionsAsync(conn, list, pks, players, ct);

            await using (var upd = new NpgsqlCommand("UPDATE hands SET context_version = $1 WHERE hand_pk = ANY($2)", conn, tx))
            {
                upd.Parameters.AddWithValue(HandContextBuilder.ContextVersion);
                upd.Parameters.AddWithValue(pks);
                await upd.ExecuteNonQueryAsync(ct);
            }

            var agg = new StatAggregator(plain);
            foreach (var (_, h, ctx) in batch) agg.Add(ctx, HandImporter.SeatIds(h, ctx, players));
            await AggWriter.UpsertAsync(conn, tx, agg, ct, "player_stat_agg_shadow");
            await tx.CommitAsync(ct);
            progress?.Report(new RebuildProgress("full", to, max));
        }

        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await Exec(conn, tx, """
                LOCK TABLE player_stat_agg IN ACCESS EXCLUSIVE MODE;
                DROP TABLE player_stat_agg;
                ALTER TABLE player_stat_agg_shadow RENAME TO player_stat_agg;
                ALTER INDEX IF EXISTS player_stat_agg_shadow_pkey RENAME TO player_stat_agg_pkey;
                """, ct);
            await using (var wm = new NpgsqlCommand("""
                INSERT INTO agg_watermark (stat_id, def_version, last_hand_pk)
                SELECT stat_id, version, $1 FROM stat_definition WHERE stat_id = ANY($2)
                ON CONFLICT (stat_id) DO UPDATE SET def_version = EXCLUDED.def_version, last_hand_pk = EXCLUDED.last_hand_pk
                """, conn, tx))
            {
                wm.Parameters.AddWithValue(max);
                wm.Parameters.AddWithValue(plain.Select(s => s.StatId).ToArray());
                await wm.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ребилд разбирает сохранённый текст одной раздачи тем же парсером, что и импорт.</summary>
    private static PKTracker.Core.Model.ParsedHand ParseOne(PKTracker.Core.Abstractions.IHandHistoryParser parser, string text, string file)
    {
        var r = parser.Parse(text, ParserRegistry.SourceFor(file)).FirstOrDefault()
                ?? throw new FormatException("Пустой текст раздачи");
        return r.Hand ?? throw new FormatException(r.Error);
    }
}
