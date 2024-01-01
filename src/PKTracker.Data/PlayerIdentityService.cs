using Npgsql;
using PKTracker.Core.Abstractions;

namespace PKTracker.Data;

public sealed record MergeResult(long CanonicalId, long AliasId, string AliasNickname);

/// <summary>
/// Идентичность игрока (§0.4, §3.2): ник меняется, UUID из .data — нет. Смена ника склеивает игроков:
/// hand_players / hand_decisions / заметки / итоги турниров перезаписываются на канон, агрегаты переносятся.
/// Разрешение алиасов «на лету» в HUD-запросах не делается — это удвоило бы стоимость самого горячего запроса.
/// </summary>
public sealed class PlayerIdentityService(TrackerDb db)
{
    /// <summary>Сопоставляет UUID с никами рассадки. Игроки без раздач в базе не создаются.</summary>
    public async Task<List<MergeResult>> ObserveAsync(IEnumerable<LiveSeat> seats, short? room = null, CancellationToken ct = default)
    {
        short roomId = room ?? db.RoomId;
        var merges = new List<MergeResult>();
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        foreach (var seat in seats)
        {
            if (seat.ExternalId is not { } uuid || string.IsNullOrWhiteSpace(seat.Name)) continue;

            (long Id, string Nick, long Canon)? owner = null;
            await using (var cmd = new NpgsqlCommand(
                "SELECT player_id, nickname, coalesce(canonical_id, player_id) FROM player WHERE room_id = $1 AND external_id = $2", conn))
            {
                cmd.Parameters.AddWithValue(roomId);
                cmd.Parameters.AddWithValue(uuid);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) owner = (r.GetInt64(0), r.GetString(1), r.GetInt64(2));
            }

            (long Id, long Canon)? byName = null;
            await using (var cmd = new NpgsqlCommand(
                "SELECT player_id, coalesce(canonical_id, player_id) FROM player WHERE room_id = $1 AND nickname = $2", conn))
            {
                cmd.Parameters.AddWithValue(roomId);
                cmd.Parameters.AddWithValue(seat.Name);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) byName = (r.GetInt64(0), r.GetInt64(1));
            }

            if (owner == null)
            {
                if (byName == null) continue; // нет раздач с этим игроком — запоминать нечего
                await using var cmd = new NpgsqlCommand(
                    "UPDATE player SET external_id = $1, country = coalesce($2, country) WHERE player_id = $3 AND external_id IS NULL", conn);
                cmd.Parameters.AddWithValue(uuid);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Char, (object?)Country(seat.Country) ?? DBNull.Value);
                cmd.Parameters.AddWithValue(byName.Value.Id);
                await cmd.ExecuteNonQueryAsync(ct);
                continue;
            }

            if (owner.Value.Nick == seat.Name) continue;

            // Ник сменился
            if (byName == null)
            {
                // Будущие раздачи под новым ником сразу лягут на канон
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO player (room_id, nickname, canonical_id, country) VALUES ($1, $2, $3, $4)
                    ON CONFLICT (room_id, nickname) DO NOTHING
                    """, conn);
                cmd.Parameters.AddWithValue(roomId);
                cmd.Parameters.AddWithValue(seat.Name);
                cmd.Parameters.AddWithValue(owner.Value.Canon);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Char, (object?)Country(seat.Country) ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else if (byName.Value.Canon != owner.Value.Canon)
            {
                merges.Add(await MergeAsync(conn, owner.Value.Canon, byName.Value.Canon, ct));
            }
        }
        return merges;
    }

    private static string? Country(string? c) => c is { Length: 3 } ? c : null;

    /// <summary>Ручная склейка ников (вкладка «Игроки»): <paramref name="aliasId"/> становится алиасом <paramref name="canonicalId"/>.</summary>
    public async Task<MergeResult> MergeAsync(long canonicalId, long aliasId, CancellationToken ct = default)
    {
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        return await MergeAsync(conn, canonicalId, aliasId, ct);
    }

    private static async Task<MergeResult> MergeAsync(NpgsqlConnection conn, long canon, long alias, CancellationToken ct)
    {
        if (canon == alias) throw new ArgumentException("Нельзя склеить игрока с самим собой");
        await using var tx = await conn.BeginTransactionAsync(ct);

        string aliasNick;
        await using (var cmd = new NpgsqlCommand("SELECT nickname FROM player WHERE player_id = $1", conn, tx))
        {
            cmd.Parameters.AddWithValue(alias);
            aliasNick = (string?)await cmd.ExecuteScalarAsync(ct) ?? throw new ArgumentException($"Игрок {alias} не найден");
        }

        string[] statements =
        {
            "UPDATE player SET canonical_id = $1 WHERE player_id = $2 OR canonical_id = $2",
            "UPDATE hand_players SET player_id = $1 WHERE player_id = $2",
            "UPDATE hand_decisions SET player_id = $1 WHERE player_id = $2",
            "UPDATE player_note SET player_id = $1 WHERE player_id = $2",
            """
            UPDATE tournament_result t SET player_id = $1
             WHERE t.player_id = $2
               AND NOT EXISTS (SELECT 1 FROM tournament_result x
                                WHERE x.tournament_id = t.tournament_id AND x.player_id = $1 AND x.entry_no = t.entry_no)
            """,
            "DELETE FROM tournament_result WHERE player_id = $2",
            // Агрегаты: переносим дельтой, без пересчёта по раздачам
            """
            INSERT INTO player_stat_agg AS a (player_id, stat_id, game_kind, dealt_bucket, position, stack_bucket, cases, hits, sum_value)
            SELECT $1, stat_id, game_kind, dealt_bucket, position, stack_bucket, cases, hits, sum_value
              FROM player_stat_agg WHERE player_id = $2
            ON CONFLICT (player_id, stat_id, game_kind, dealt_bucket, position, stack_bucket) DO UPDATE
               SET cases = a.cases + EXCLUDED.cases, hits = a.hits + EXCLUDED.hits, sum_value = a.sum_value + EXCLUDED.sum_value
            """,
            "DELETE FROM player_stat_agg WHERE player_id = $2",
        };
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue(canon);
            cmd.Parameters.AddWithValue(alias);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new MergeResult(canon, alias, aliasNick);
    }
}
