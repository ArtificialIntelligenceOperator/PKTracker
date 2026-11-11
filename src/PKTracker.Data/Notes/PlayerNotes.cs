using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;

namespace PKTracker.Data.Notes;

/// <summary>Цветовая метка и подпись на игрока. Note — последняя заметка из player_note (для подсказки).</summary>
public sealed record PlayerNote(long PlayerId, string? Marker, string? Caption, string? Note, DateTime UpdatedUtc);

/// <summary>
/// Метки на игроков (player_marker). HUD читает их на каждой перерисовке панели, поэтому
/// держим кеш в памяти и ходим в базу пулом HUD — короткими запросами.
/// </summary>
public sealed class PlayerNotesRepository(TrackerDb db)
{
    private readonly ConcurrentDictionary<long, PlayerNote?> _cache = new();

    public async Task<PlayerNote?> GetAsync(long playerId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(playerId, out var hit)) return hit;
        await using var conn = await db.Hud.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT marker, caption, (SELECT body FROM player_note n WHERE n.player_id = m.player_id ORDER BY created_at DESC LIMIT 1), updated_at FROM player_marker m WHERE player_id = $1", conn);
        cmd.Parameters.AddWithValue(playerId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        PlayerNote? note = null;
        if (await r.ReadAsync(ct))
            note = new PlayerNote(playerId, r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetDateTime(3));
        _cache[playerId] = note;
        return note;
    }

    public async Task<Dictionary<long, PlayerNote>> GetAllAsync(CancellationToken ct = default)
    {
        var res = new Dictionary<long, PlayerNote>();
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT player_id, marker, caption, NULL::text, updated_at FROM player_marker", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var n = new PlayerNote(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetDateTime(4));
            // В кэш не кладём: здесь нет текста заметки, и GetAsync потом отдавал бы запись без неё.
            res[n.PlayerId] = n;
        }
        return res;
    }

    /// <summary>Записать метку и подпись. Обе пустые — запись удаляется. Заметка пишется отдельно (<see cref="AddNoteAsync"/>).</summary>
    public async Task SetAsync(long playerId, string? marker, string? caption, CancellationToken ct = default)
    {
        marker = string.IsNullOrWhiteSpace(marker) ? null : marker.Trim();
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        if (marker == null && caption == null)
        {
            await using var del = new NpgsqlCommand("DELETE FROM player_marker WHERE player_id = $1", conn);
            del.Parameters.AddWithValue(playerId);
            await del.ExecuteNonQueryAsync(ct);
            _cache.TryRemove(playerId, out _);
            return;
        }
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO player_marker (player_id, marker, caption, updated_at) VALUES ($1, $2, $3, now())
            ON CONFLICT (player_id) DO UPDATE SET marker = EXCLUDED.marker, caption = EXCLUDED.caption, updated_at = now()
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue((object?)marker ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)caption ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.TryRemove(playerId, out _);
    }

    /// <summary>Заметка с датой — в общий список заметок игрока (player_note), как во вкладке «Игроки».</summary>
    public async Task AddNoteAsync(long playerId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await new ReportRepository(db).AddNoteAsync(playerId, text.Trim(), null, ct);
        _cache.TryRemove(playerId, out _);
    }

    public void Invalidate() => _cache.Clear();
}

/// <summary>Тип оппонента для цветовой метки: название, цвет и признак «регуляр» (для фильтров).</summary>
public sealed class MarkerDef
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public string Color { get; set; } = "#9E9E9E";
    public bool IsReg { get; set; }
    /// <summary>Как распознать тип по статам — подсказка в меню, а не автоматическое правило.</summary>
    public string? Hint { get; set; }
}

/// <summary>
/// Каталог меток пользователя: %APPDATA%\PKTracker\markers.json. Хранится у пользователя, а не в
/// репозитории: классификация оппонентов — его личный материал. Пустой каталог — меток нет.
/// </summary>
public sealed class MarkerCatalog
{
    public List<MarkerDef> Markers { get; set; } = new();

    public static string FilePath => Path.Combine(TrackerSettings.Folder, "markers.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MarkerDef? Find(string id) => Markers.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static MarkerCatalog Load(string? path = null)
    {
        try
        {
            var p = path ?? FilePath;
            if (File.Exists(p)) return JsonSerializer.Deserialize<MarkerCatalog>(File.ReadAllText(p), Json) ?? new MarkerCatalog();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new MarkerCatalog();
    }

    public void Save(string? path = null)
    {
        Directory.CreateDirectory(TrackerSettings.Folder);
        File.WriteAllText(path ?? FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Добавить метки из другого файла (готовый набор), не трогая уже заведённые с тем же Id.</summary>
    public int Merge(MarkerCatalog other)
    {
        int added = 0;
        foreach (var m in other.Markers)
            if (Find(m.Id) == null) { Markers.Add(m); added++; }
        return added;
    }
}
