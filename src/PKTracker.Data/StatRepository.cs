using Npgsql;
using NpgsqlTypes;
using PKTracker.Engine.Stats;

namespace PKTracker.Data;

public sealed class StatRepository(TrackerDb db)
{
    /// <summary>
    /// Кладёт дефолтные статы в stat_definition. Встроенный стат с изменившимся определением получает version+1 —
    /// это сигнал селективного ребилда (§4.5).
    /// </summary>
    public async Task<List<int>> SeedDefaultsAsync(CancellationToken ct = default)
    {
        var changed = new List<int>();
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        foreach (var def in DefaultStats.All)
        {
            var json = def.ToJson();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO stat_definition (code, title, scope, definition, is_builtin)
                VALUES ($1, $2, $3, $4, true)
                ON CONFLICT (code) DO UPDATE
                   SET title = EXCLUDED.title, scope = EXCLUDED.scope, definition = EXCLUDED.definition,
                       version = stat_definition.version + 1
                 WHERE stat_definition.is_builtin
                   AND (stat_definition.definition - 'statId' - 'version' - 'isBuiltin')
                       IS DISTINCT FROM (EXCLUDED.definition - 'statId' - 'version' - 'isBuiltin')
                RETURNING stat_id
                """, conn);
            cmd.Parameters.AddWithValue(def.Code);
            cmd.Parameters.AddWithValue(def.Title);
            cmd.Parameters.AddWithValue((short)def.Scope);
            cmd.Parameters.Add(new NpgsqlParameter { Value = json, NpgsqlDbType = NpgsqlDbType.Jsonb });
            var id = await cmd.ExecuteScalarAsync(ct);
            if (id is int i) changed.Add(i);
        }
        return changed;
    }

    public async Task<List<StatDefinition>> LoadActiveAsync(NpgsqlDataSource? ds = null, CancellationToken ct = default)
    {
        await using var conn = await (ds ?? db.Import).OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT stat_id, definition::text, version, is_builtin FROM stat_definition WHERE is_active ORDER BY stat_id", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<StatDefinition>();
        while (await r.ReadAsync(ct))
        {
            var def = StatDefinition.FromJson(r.GetString(1));
            def.StatId = r.GetInt32(0);
            def.Version = r.GetInt32(2);
            def.IsBuiltin = r.GetBoolean(3);
            list.Add(def);
        }
        return list;
    }

    /// <summary>Сохранение пользовательского стата: компиляция обязательна, ошибка — отказ (§4.3).</summary>
    public async Task<StatDefinition> SaveAsync(StatDefinition def, CancellationToken ct = default)
    {
        var existing = await LoadActiveAsync(ct: ct);
        var known = existing.Where(e => e.Code != def.Code).Append(def).ToDictionary(d => d.Code);
        StatCompiler.Compile(def, known); // бросит StatCompileException

        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO stat_definition (code, title, scope, definition, is_builtin)
            VALUES ($1, $2, $3, $4, false)
            ON CONFLICT (code) DO UPDATE
               SET title = EXCLUDED.title, scope = EXCLUDED.scope, definition = EXCLUDED.definition,
                   version = stat_definition.version + 1
            RETURNING stat_id, version
            """, conn);
        cmd.Parameters.AddWithValue(def.Code);
        cmd.Parameters.AddWithValue(def.Title);
        cmd.Parameters.AddWithValue((short)def.Scope);
        cmd.Parameters.Add(new NpgsqlParameter { Value = def.ToJson(), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        def.StatId = r.GetInt32(0);
        def.Version = r.GetInt32(1);
        return def;
    }

    /// <summary>Статы, чьи агрегаты посчитаны не той версией определения или отсутствуют.</summary>
    /// <summary>
    /// Сколько раздач разобрано старой версией построителя контекста.
    ///
    /// Пока их больше нуля, статы, опирающиеся на новые поля (позиция оппонента, линия, размер ставки),
    /// считаются по умолчаниям колонок и молча врут. Селективный ребилд это не чинит — он читает
    /// hand_decisions, а не исходный текст; нужен полный (`rebuild --full`).
    /// </summary>
    public async Task<long> StaleContextHandsAsync(short currentVersion, CancellationToken ct = default)
    {
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM hands WHERE context_version < $1", conn);
        cmd.Parameters.AddWithValue(currentVersion);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<List<int>> StaleStatIdsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT d.stat_id
              FROM stat_definition d
              LEFT JOIN agg_watermark w ON w.stat_id = d.stat_id
             WHERE d.is_active AND d.scope <> 2
               AND (w.stat_id IS NULL OR w.def_version <> d.version
                    OR w.last_hand_pk < (SELECT coalesce(max(hand_pk), 0) FROM hands))
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<int>();
        while (await r.ReadAsync(ct)) list.Add(r.GetInt32(0));
        return list;
    }
}

public sealed class HeroRepository(TrackerDb db)
{
    public async Task<List<string>> ListAsync(short? room = null, CancellationToken ct = default)
    {
        short roomId = room ?? db.RoomId;
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT nickname FROM hero_account WHERE room_id = $1 ORDER BY nickname", conn);
        cmd.Parameters.AddWithValue(roomId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task AddAsync(string nickname, short? room = null, CancellationToken ct = default)
    {
        short roomId = room ?? db.RoomId;
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO hero_account (room_id, nickname) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(roomId);
        cmd.Parameters.AddWithValue(nickname);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(string nickname, short? room = null, CancellationToken ct = default)
    {
        short roomId = room ?? db.RoomId;
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM hero_account WHERE room_id = $1 AND nickname = $2", conn);
        cmd.Parameters.AddWithValue(roomId);
        cmd.Parameters.AddWithValue(nickname);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
