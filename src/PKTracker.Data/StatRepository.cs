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

    /// <summary>
    /// Пакетное сохранение (импорт пакета HUD): одна компиляция на всех и одна транзакция. Определения,
    /// не изменившиеся по тексту, не трогаются — иначе версия выросла бы и стат пересчитывался бы зря.
    /// </summary>
    /// <returns>Коды добавленных и изменённых статов.</returns>
    public async Task<List<string>> SaveManyAsync(IReadOnlyList<StatDefinition> defs, CancellationToken ct = default)
    {
        var existing = (await LoadActiveAsync(ct: ct)).ToDictionary(d => d.Code);
        // Встроенные — все, включая выключенные: LoadActive их не вернёт, а ON CONFLICT молча перезаписал бы определение.
        var builtin = existing.Values.Where(e => e.IsBuiltin).Select(e => e.Code).ToHashSet();
        await using (var bc = await db.Import.OpenConnectionAsync(ct))
        await using (var cmd0 = new NpgsqlCommand("SELECT code FROM stat_definition WHERE is_builtin", bc))
        await using (var r0 = await cmd0.ExecuteReaderAsync(ct))
            while (await r0.ReadAsync(ct)) builtin.Add(r0.GetString(0));
        foreach (var d in defs)
            if (builtin.Contains(d.Code)) throw new StatCompileException(d.Code, "код занят встроенным статом");
        var known = new Dictionary<string, StatDefinition>(existing);
        foreach (var d in defs) known[d.Code] = d;
        foreach (var d in defs) StatCompiler.Compile(d, known); // бросит StatCompileException — пакет не применится

        var changed = defs.Where(d => !existing.TryGetValue(d.Code, out var e) || e.ToJson() != d.ToJson()).ToList();
        if (changed.Count == 0) return new List<string>();
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var def in changed)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO stat_definition (code, title, scope, definition, is_builtin)
                VALUES ($1, $2, $3, $4, false)
                ON CONFLICT (code) DO UPDATE
                   SET title = EXCLUDED.title, scope = EXCLUDED.scope, definition = EXCLUDED.definition,
                       version = stat_definition.version + 1, is_active = true
                """, conn, tx);
            cmd.Parameters.AddWithValue(def.Code);
            cmd.Parameters.AddWithValue(def.Title);
            cmd.Parameters.AddWithValue((short)def.Scope);
            cmd.Parameters.Add(new NpgsqlParameter { Value = def.ToJson(), NpgsqlDbType = NpgsqlDbType.Jsonb });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return changed.Select(d => d.Code).ToList();
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

/// <summary>Аккаунт пользователя в руме. В списке показывается как «ник — рум».</summary>
public sealed record HeroAccount(string RoomCode, string Nickname)
{
    public string RoomTitle => PKTracker.Parsers.ParserRegistry.Room(RoomCode)?.Title ?? RoomCode;
    public override string ToString() => $"{Nickname} — {RoomTitle}";
}

public sealed class HeroRepository(TrackerDb db)
{
    /// <summary>Аккаунты во всех румах: импорт раскладывает раздачи по румам сам, и ники нужны для каждого.</summary>
    public async Task<List<HeroAccount>> ListAllAsync(CancellationToken ct = default)
    {
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT r.code, h.nickname FROM hero_account h JOIN room r ON r.room_id = h.room_id ORDER BY r.code, h.nickname", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<HeroAccount>();
        while (await r.ReadAsync(ct)) list.Add(new HeroAccount(r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Добавить ник в рум по его коду. false — такого рума в базе нет.</summary>
    public async Task<bool> AddForRoomAsync(string nickname, string roomCode, CancellationToken ct = default)
    {
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH r AS (SELECT room_id FROM room WHERE code = $1),
                 i AS (INSERT INTO hero_account (room_id, nickname) SELECT room_id, $2 FROM r ON CONFLICT DO NOTHING)
            SELECT count(*) FROM r
            """, conn);
        cmd.Parameters.AddWithValue(roomCode.ToUpperInvariant());
        cmd.Parameters.AddWithValue(nickname);
        return (long)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    public async Task RemoveForRoomAsync(string nickname, string roomCode, CancellationToken ct = default)
    {
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM hero_account WHERE nickname = $2 AND room_id = (SELECT room_id FROM room WHERE code = $1)", conn);
        cmd.Parameters.AddWithValue(roomCode.ToUpperInvariant());
        cmd.Parameters.AddWithValue(nickname);
        await cmd.ExecuteNonQueryAsync(ct);
    }

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
