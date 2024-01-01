using System.Collections.Concurrent;
using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.Data;

public sealed record PlayerRef(long PlayerId, string Nickname);

/// <summary>
/// HUD читает только агрегаты (§3.1, §15.9). Кеш по (player, game_kind) инвалидируется после импорта.
/// Вызывается только из фоновых потоков — никогда из UI-потока (§10.2).
/// </summary>
public sealed class HudStatsService
{
    private readonly TrackerDb _db;
    private readonly ConcurrentDictionary<(long, GameKind), PlayerStatSet> _cache = new();
    private readonly ConcurrentDictionary<(long, GameKind), PlayerAggSet> _aggCache = new();
    private readonly ConcurrentDictionary<(long, GameKind), Task<PlayerRows>> _rowsCache = new();
    private IReadOnlyDictionary<string, CompiledStat> _stats;

    public HudStatsService(TrackerDb db, IEnumerable<CompiledStat> stats)
    {
        _db = db;
        _stats = stats.ToDictionary(s => s.Code);
    }

    public IReadOnlyDictionary<string, CompiledStat> Stats => _stats;

    public void ReplaceStats(IEnumerable<CompiledStat> stats)
    {
        _stats = stats.ToDictionary(s => s.Code);
        InvalidateAll();
    }

    public void Invalidate(IEnumerable<long> playerIds)
    {
        foreach (var id in playerIds)
            foreach (var k in Enum.GetValues<GameKind>())
            {
                _cache.TryRemove((id, k), out _);
                _aggCache.TryRemove((id, k), out _);
                _rowsCache.TryRemove((id, k), out _);
            }
    }

    public void InvalidateAll()
    {
        _cache.Clear();
        _aggCache.Clear();
        _rowsCache.Clear();
    }

    public async Task<PlayerStatSet> GetAsync(long playerId, GameKind kind, CancellationToken ct = default)
    {
        if (_cache.TryGetValue((playerId, kind), out var cached)) return cached;
        await using var conn = await _db.Hud.OpenConnectionAsync(ct);
        var set = await LoadAsync(conn, playerId, kind, ct);
        _cache[(playerId, kind)] = set;
        return set;
    }

    private async Task<PlayerStatSet> LoadAsync(NpgsqlConnection conn, long playerId, GameKind kind, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT stat_id, SUM(cases)::bigint, SUM(hits)::bigint, SUM(sum_value)::bigint
              FROM player_stat_agg
             WHERE player_id = $1 AND game_kind = ANY($2)
             GROUP BY stat_id
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        // SNG считаем вместе с MTT: турнирные статы копятся через все турниры (§7.1)
        short[] kinds = kind == GameKind.Cash ? new short[] { 0 } : new short[] { 1, 2 };
        cmd.Parameters.AddWithValue(kinds);
        var set = new PlayerStatSet(_stats);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        return set;
    }

    /// <summary>
    /// Агрегаты игрока с сохранёнными измерениями — для панели HUD, где ячейки фильтруются
    /// по позиции, числу игроков и корзине стека.
    /// </summary>
    public async Task<PlayerAggSet> GetAggAsync(long playerId, GameKind kind, CancellationToken ct = default)
    {
        if (_aggCache.TryGetValue((playerId, kind), out var cached)) return cached;
        await using var conn = await _db.Hud.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT stat_id, dealt_bucket, position, stack_bucket, cases, hits, sum_value
              FROM player_stat_agg
             WHERE player_id = $1 AND game_kind = ANY($2)
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue(Kinds(kind));
        var rows = new List<AggRow>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add(new AggRow(r.GetInt32(0), (byte)r.GetInt16(1), (byte)r.GetInt16(2), (byte)r.GetInt16(3),
                    r.GetInt32(4), r.GetInt32(5), r.GetInt64(6)));
        var set = new PlayerAggSet(_stats, rows);
        _aggCache[(playerId, kind)] = set;
        return set;
    }

    /// <summary>
    /// «Срез» игрока для попапов. Кешируется задачей, а не результатом: попап открывают кликом,
    /// и два быстрых клика не должны запускать два одинаковых чтения на 200 000 строк.
    /// </summary>
    public Task<PlayerRows> GetRowsAsync(long playerId, GameKind kind, CancellationToken ct = default) =>
        _rowsCache.GetOrAdd((playerId, kind), key => new PlayerRowsLoader(_db).LoadAsync(key.Item1, key.Item2, ct));

    // SNG считаем вместе с MTT: турнирные статы копятся через все турниры (§7.1)
    private static short[] Kinds(GameKind kind) => kind == GameKind.Cash ? new short[] { 0 } : new short[] { 1, 2 };

    public async Task<PlayerRef?> FindPlayerAsync(string nickname, CancellationToken ct = default)
    {
        await using var conn = await _db.Hud.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT coalesce(canonical_id, player_id), nickname FROM player WHERE room_id = $1 AND nickname = $2", conn);
        cmd.Parameters.AddWithValue(_db.RoomId);
        cmd.Parameters.AddWithValue(nickname);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PlayerRef(r.GetInt64(0), r.GetString(1)) : null;
    }

    public async Task<Dictionary<string, long>> FindPlayersAsync(IEnumerable<string> nicknames, CancellationToken ct = default)
    {
        await using var conn = await _db.Hud.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT nickname, coalesce(canonical_id, player_id) FROM player WHERE room_id = $1 AND nickname = ANY($2)", conn);
        cmd.Parameters.AddWithValue(_db.RoomId);
        cmd.Parameters.AddWithValue(nicknames.Distinct().ToArray());
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }
}
