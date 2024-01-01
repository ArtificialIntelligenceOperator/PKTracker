using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace PKTracker.Data;

/// <summary>
/// Анонимизация ников оппонентов для ответов внешнему агенту (MCP): данные уходят в облако модели, а в раздачах —
/// чужие ники. Псевдоним стабилен для одной установки (локальная соль), поэтому агент может следить за игроком
/// между запросами, но не узнаёт настоящий ник. Ники Hero не скрываются.
/// </summary>
public sealed class Anonymizer
{
    private readonly byte[] _salt;
    private readonly HashSet<string> _heroes;
    private Dictionary<string, string>? _aliasToNick;
    private HashSet<string>? _opponents;

    public bool Enabled { get; }

    public Anonymizer(bool enabled, IEnumerable<string> heroes, string saltFile)
    {
        Enabled = enabled;
        _heroes = new HashSet<string>(heroes, StringComparer.Ordinal);
        if (File.Exists(saltFile)) _salt = File.ReadAllBytes(saltFile);
        else
        {
            _salt = RandomNumberGenerator.GetBytes(16);
            Directory.CreateDirectory(Path.GetDirectoryName(saltFile)!);
            File.WriteAllBytes(saltFile, _salt);
        }
    }

    public bool IsHero(string nick) => _heroes.Contains(nick);

    public string Alias(string nick)
    {
        if (!Enabled || _heroes.Contains(nick)) return nick;
        var hash = HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(nick));
        return "Villain_" + Convert.ToHexString(hash, 0, 3);
    }

    /// <summary>Загружает всех игроков рума (для обратного поиска псевдонима и замены ников в тексте).</summary>
    public async Task LoadPlayersAsync(TrackerDb db, CancellationToken ct = default)
    {
        if (!Enabled || _aliasToNick != null) return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nicks = new List<string>();
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT nickname FROM player WHERE room_id = $1", conn);
        cmd.Parameters.AddWithValue(db.RoomId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var n = r.GetString(0);
            nicks.Add(n);
            map[Alias(n)] = n;
        }
        _aliasToNick = map;
        _opponents = new HashSet<string>(nicks.Where(n => !_heroes.Contains(n)), StringComparer.Ordinal);
    }

    /// <summary>Псевдоним или настоящий ник → настоящий ник для запроса к базе.</summary>
    public string Resolve(string nickOrAlias) =>
        Enabled && _aliasToNick != null && _aliasToNick.TryGetValue(nickOrAlias, out var real) ? real : nickOrAlias;

    /// <summary>Заменяет ники игроков раздачи в тексте (длинные — первыми: ники бывают префиксами друг друга).</summary>
    public string ScrubText(string text, IEnumerable<string> names)
    {
        if (!Enabled) return text;
        foreach (var n in names.Where(n => !_heroes.Contains(n)).Distinct().OrderByDescending(n => n.Length))
            text = text.Replace(n, Alias(n), StringComparison.Ordinal);
        return text;
    }

    /// <summary>Значение ячейки произвольного SQL: если это ник оппонента — псевдоним.</summary>
    public object? ScrubCell(object? value)
    {
        if (!Enabled || value is not string s || _opponents == null) return value;
        return _opponents.Contains(s) ? Alias(s) : value;
    }
}
