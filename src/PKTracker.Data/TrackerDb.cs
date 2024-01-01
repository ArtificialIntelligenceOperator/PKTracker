using System.Reflection;
using Npgsql;

namespace PKTracker.Data;

/// <summary>
/// Раздельные пулы соединений (§15.10): тяжёлый отчёт физически не может занять соединения HUD.
/// </summary>
public sealed class TrackerDb : IAsyncDisposable
{
    public const short PokerKingRoomId = 1;

    /// <summary>Активный рум (скин WPN): все раздачи, ники Hero и игроки привязываются к нему (§9, Фаза 13).</summary>
    public short RoomId { get; set; } = PokerKingRoomId;
    public string RoomCode { get; set; } = "POKERKING";

    public NpgsqlDataSource Hud { get; }
    public NpgsqlDataSource Reports { get; }
    public NpgsqlDataSource Import { get; }
    public string ConnectionString { get; }

    public TrackerDb(string connectionString)
    {
        ConnectionString = connectionString;
        Hud = Build(connectionString, maxPool: 4, timeout: 2);
        Reports = Build(connectionString, maxPool: 8, timeout: 120);
        Import = Build(connectionString, maxPool: 2, timeout: 0);
    }

    private static NpgsqlDataSource Build(string cs, int maxPool, int timeout)
    {
        var b = new NpgsqlConnectionStringBuilder(cs)
        {
            MaxPoolSize = maxPool,
            CommandTimeout = timeout,
            MaxAutoPrepare = 20,
            AutoPrepareMinUsages = 2,
        };
        return new NpgsqlDataSourceBuilder(b.ConnectionString).Build();
    }

    public async ValueTask DisposeAsync()
    {
        await Hud.DisposeAsync();
        await Reports.DisposeAsync();
        await Import.DisposeAsync();
    }
}

/// <summary>Нумерованные .sql в ресурсах сборки, применяются на старте в транзакции (§3.3, §15.10).</summary>
public static class Migrator
{
    public static IReadOnlyList<(int Version, string Name, string Sql)> Scripts()
    {
        var asm = typeof(Migrator).Assembly;
        var list = new List<(int, string, string)>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            // PKTracker.Data.Migrations.001_init.sql
            var file = res.Split('.')[^2];
            int us = file.IndexOf('_');
            var digits = us > 0 ? file[..us] : file;
            if (!int.TryParse(digits.TrimStart('_'), out int version)) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            list.Add((version, file, r.ReadToEnd()));
        }
        return list.OrderBy(x => x.Item1).ToList();
    }

    public static int LatestVersion => Scripts().Max(s => s.Version);

    /// <returns>Число применённых миграций.</returns>
    public static async Task<int> MigrateAsync(NpgsqlDataSource ds, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_version (version integer PRIMARY KEY, applied_at timestamptz DEFAULT now())", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        // Сериализация миграций между процессами
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(726354)", conn))
            await cmd.ExecuteNonQueryAsync(ct);
        try
        {
            int current;
            await using (var cmd = new NpgsqlCommand("SELECT coalesce(max(version), 0) FROM schema_version", conn))
                current = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

            var scripts = Scripts();
            int latest = scripts.Count == 0 ? 0 : scripts[^1].Version;
            if (current > latest)
                throw new InvalidOperationException($"Версия БД {current} новее, чем знает приложение ({latest}). Обновите PKTracker.");

            int applied = 0;
            foreach (var (version, _, sql) in scripts.Where(s => s.Version > current))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand("INSERT INTO schema_version (version) VALUES ($1)", conn, tx))
                {
                    cmd.Parameters.AddWithValue(version);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                applied++;
            }
            return applied;
        }
        finally
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(726354)", conn);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
