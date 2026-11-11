using System.Text.RegularExpressions;
using Npgsql;

namespace PKTracker.Data;

/// <summary>База на сервере PostgreSQL, к которому подключён трекер.</summary>
/// <param name="IsTracker">В базе есть схема PKTracker (или она пуста и создана трекером) — только такие можно удалять из интерфейса.</param>
/// <param name="Hands">Число раздач; null — база не трекера или недоступна.</param>
public sealed record DatabaseInfo(string Name, long SizeBytes, bool IsTracker, long? Hands)
{
    public string SizeText => SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):0.0} ГБ" : $"{SizeBytes / (double)(1L << 20):0} МБ";

    public override string ToString() =>
        Hands is { } h ? $"{Name}  ·  {h:N0} раздач  ·  {SizeText}" : $"{Name}  ·  {SizeText}";
}

/// <summary>
/// Несколько именованных баз на одном сервере: отдельная база под тест, под другой аккаунт или под
/// присланную выгрузку — чтобы не смешивать её со своей рабочей статистикой.
///
/// Трекер переключается между ними сменой имени базы в строке подключения; схема в новой базе
/// создаётся миграциями при первом подключении. Удалять разрешено только базы самого трекера:
/// сервер может быть общим, и чужую базу отсюда снести нельзя.
/// </summary>
public static class DatabaseCatalog
{
    /// <summary>Метка в комментарии базы: по ней пустая, только что созданная база опознаётся как своя.</summary>
    private const string Marker = "PKTracker";

    /// <summary>Латинские буквы, цифры и подчёркивание; начинается с буквы. Так имя не требует кавычек нигде — ни в pg_dump, ни в строке подключения.</summary>
    private static readonly Regex NameRule = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

    private static readonly string[] Reserved = { "postgres", "template0", "template1" };

    /// <summary>Имя базы из строки подключения.</summary>
    public static string Current(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Database ?? "";

    /// <summary>Та же строка подключения, но к другой базе.</summary>
    public static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    /// <summary>Приводит введённое имя к допустимому виду или объясняет, что не так. null в <paramref name="error"/> — имя годится.</summary>
    public static string Normalize(string input, out string? error)
    {
        var name = input.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        error = name.Length == 0 ? "Введите имя базы."
            : !NameRule.IsMatch(name) ? "Имя базы — латинские буквы, цифры и «_», начинается с буквы, не длиннее 63 символов."
            : Reserved.Contains(name) ? "Это служебное имя PostgreSQL."
            : null;
        return name;
    }

    /// <summary>Базы сервера (без служебных). Соединения — короткие, вне пулов трекера: список не должен удерживать базу открытой.</summary>
    public static async Task<List<DatabaseInfo>> ListAsync(string connectionString, CancellationToken ct = default)
    {
        var names = new List<(string Name, long Size, bool Marked)>();
        await using (var conn = await OpenAsync(connectionString, "postgres", ct))
        await using (var cmd = new NpgsqlCommand("""
            SELECT d.datname, pg_database_size(d.oid), coalesce(shobj_description(d.oid, 'pg_database'), '') = $1
              FROM pg_database d
             WHERE NOT d.datistemplate AND d.datallowconn AND d.datname <> 'postgres'
             ORDER BY d.datname
            """, conn))
        {
            cmd.Parameters.AddWithValue(Marker);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) names.Add((r.GetString(0), r.GetInt64(1), r.GetBoolean(2)));
        }

        var list = new List<DatabaseInfo>();
        foreach (var (name, size, marked) in names)
        {
            bool tracker = marked;
            long? hands = null;
            try
            {
                await using var conn = await OpenAsync(connectionString, name, ct);
                await using var cmd = new NpgsqlCommand(
                    "SELECT to_regclass('public.schema_version') IS NOT NULL AND to_regclass('public.hands') IS NOT NULL", conn);
                if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
                {
                    tracker = true;
                    // Оценка планировщика, а не count(*): список открывается мгновенно и на большой базе.
                    await using var cnt = new NpgsqlCommand(
                        "SELECT CASE WHEN reltuples < 0 THEN 0 ELSE reltuples::bigint END FROM pg_class WHERE oid = 'public.hands'::regclass", conn);
                    hands = (long)(await cnt.ExecuteScalarAsync(ct))!;
                    if (hands < 200_000)
                    {
                        await using var exact = new NpgsqlCommand("SELECT count(*) FROM hands", conn);
                        hands = (long)(await exact.ExecuteScalarAsync(ct))!;
                    }
                }
            }
            catch (NpgsqlException) { } // нет прав на чужую базу — показываем её без подробностей
            list.Add(new DatabaseInfo(name, size, tracker, hands));
        }
        return list;
    }

    /// <summary>Создаёт пустую базу. Схему в ней создаст трекер при первом подключении.</summary>
    public static async Task CreateAsync(string connectionString, string name, CancellationToken ct = default)
    {
        name = Normalize(name, out var error);
        if (error != null) throw new ArgumentException(error);
        await using var conn = await OpenAsync(connectionString, "postgres", ct);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", conn))
        {
            exists.Parameters.AddWithValue(name);
            if (await exists.ExecuteScalarAsync(ct) != null) throw new InvalidOperationException($"База «{name}» уже существует.");
        }
        // Имя проверено правилом NameRule, поэтому подстановка в текст команды безопасна (параметром имя базы не передать).
        // template0 и явная кодировка: на русской Windows template1 бывает в WIN1251, и ники не в ASCII в ней не хранятся.
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\" ENCODING 'UTF8' TEMPLATE template0", conn))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand($"COMMENT ON DATABASE \"{name}\" IS '{Marker}'", conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Удаляет базу трекера безвозвратно. Активную базу удалить нельзя — сначала переключитесь на другую:
    /// иначе приложение осталось бы без базы посреди работы.
    /// </summary>
    public static async Task DropAsync(string connectionString, string name, CancellationToken ct = default)
    {
        name = Normalize(name, out var error);
        if (error != null) throw new ArgumentException(error);
        if (string.Equals(name, Current(connectionString), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Это активная база. Переключитесь на другую и повторите.");
        var info = (await ListAsync(connectionString, ct)).FirstOrDefault(d => d.Name == name)
                   ?? throw new InvalidOperationException($"Базы «{name}» нет на сервере.");
        if (!info.IsTracker) throw new InvalidOperationException($"«{name}» — не база PKTracker; удалять её отсюда нельзя.");

        await using var conn = await OpenAsync(connectionString, "postgres", ct);
        try
        {
            await using var cmd = new NpgsqlCommand($"DROP DATABASE \"{name}\"", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse)
        {
            throw new InvalidOperationException(
                $"База «{name}» сейчас открыта другой программой (консоль PKTracker.Cli, MCP-сервер, pgAdmin). Закройте её и повторите.", ex);
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, string database, CancellationToken ct)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString) { Database = database, Pooling = false, Timeout = 5, CommandTimeout = 60 };
        var conn = new NpgsqlConnection(b.ConnectionString);
        try { await conn.OpenAsync(ct); }
        catch { await conn.DisposeAsync(); throw; }
        return conn;
    }
}
