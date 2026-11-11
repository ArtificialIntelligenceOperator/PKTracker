using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Npgsql;

namespace PKTracker.Mcp.Tools;

/// <summary>Произвольная аналитика SQL-запросами — строго только чтение.</summary>
[McpServerToolType]
public sealed class SqlTools(TrackerContext ctx)
{
    [McpServerTool(Name = "get_database_schema", ReadOnly = true, Title = "Схема базы")]
    [Description("Описание схемы PostgreSQL (единицы, битовые маски fact_bits/ctx_bits, кодировка карт и позиций) плюс фактический список таблиц и колонок.")]
    public async Task<string> GetDatabaseSchema(CancellationToken ct)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.HostAsync(ct);   // схема — не данные об игроках: доступна и во время игры
            var sb = new System.Text.StringBuilder(Resources.ReadDoc("DATABASE.md"));
            sb.AppendLine().AppendLine("## Фактические колонки").AppendLine();
            await using var conn = await host.Db.Reports.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT table_name, string_agg(column_name || ' ' || data_type, ', ' ORDER BY ordinal_position)
                  FROM information_schema.columns WHERE table_schema = 'public'
                 GROUP BY table_name ORDER BY table_name
                """, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) sb.Append("- **").Append(r.GetString(0)).Append("**: ").AppendLine(r.GetString(1));
            return sb.ToString();
        });
    }

    [McpServerTool(Name = "run_sql", ReadOnly = true, Title = "SQL (только чтение)")]
    [Description("Выполняет один SELECT/WITH в транзакции READ ONLY с таймаутом 20 с. Возвращает до max_rows строк. " +
                 "Деньги — в сотых (кеш: центы, турниры: фишки×100). Hero: player_id IN (SELECT coalesce(p.canonical_id, p.player_id) " +
                 "FROM player p JOIN hero_account h USING (room_id, nickname)). Ники оппонентов при анонимизации заменяются псевдонимами.")]
    public async Task<string> RunSql(
        [Description("SQL-запрос (SELECT или WITH ... SELECT)")] string query,
        [Description("Максимум строк (до 2000)")] int max_rows = 200,
        CancellationToken ct = default) =>
        await TrackerContext.Guard(() => RunSqlCore(query, max_rows, ct));

    private async Task<string> RunSqlCore(string query, int max_rows, CancellationToken ct)
    {
        var host = await ctx.AnalysisHostAsync(ct);
        var trimmed = query.Trim().TrimEnd(';');
        var head = trimmed.TrimStart('(').TrimStart();
        if (!(head.StartsWith("select", StringComparison.OrdinalIgnoreCase) || head.StartsWith("with", StringComparison.OrdinalIgnoreCase)
              || head.StartsWith("explain", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Разрешены только SELECT / WITH / EXPLAIN");
        if (trimmed.Contains(';')) throw new ArgumentException("Только один запрос за вызов");

        max_rows = Math.Clamp(max_rows, 1, 2000);
        await using var conn = await host.Db.Reports.OpenConnectionAsync(ct);
        // Даже если проверка выше пропустит что-то пишущее, транзакция READ ONLY это отклонит на стороне PostgreSQL
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var setup = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '20s'", conn, tx))
            await setup.ExecuteNonQueryAsync(ct);

        var columns = new List<string>();
        var rows = new List<object?[]>();
        bool truncated = false;
        await using (var cmd = new NpgsqlCommand(trimmed, conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            for (int i = 0; i < r.FieldCount; i++) columns.Add(r.GetName(i));
            while (await r.ReadAsync(ct))
            {
                if (rows.Count >= max_rows) { truncated = true; break; }
                var row = new object?[r.FieldCount];
                for (int i = 0; i < r.FieldCount; i++)
                {
                    object? v = r.IsDBNull(i) ? null : r.GetValue(i);
                    row[i] = v switch
                    {
                        DateTime d => TrackerContext.Iso(d),
                        byte[] => "<bytea>",
                        Guid g => g.ToString(),
                        _ => ctx.Anonymizer.ScrubCell(v),
                    };
                }
                rows.Add(row);
            }
        }
        await tx.RollbackAsync(ct);
        return TrackerContext.ToJson(new { columns, rows, row_count = rows.Count, truncated });
    }
}

/// <summary>Документация для агента: язык статов и схема базы.</summary>
[McpServerResourceType]
public static class Resources
{
    public static string ReadDoc(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [McpServerResource(UriTemplate = "pktracker://docs/stats", Name = "stats-language", Title = "Язык статов PKTracker", MimeType = "text/markdown")]
    [Description("Как устроены статы: DecisionPoint и HandPlayerFacts, все поля, синтаксис условий, примеры, expression-статы.")]
    public static string StatsDoc() => ReadDoc("STATS.md");

    [McpServerResource(UriTemplate = "pktracker://docs/database", Name = "database-schema", Title = "Схема базы PKTracker", MimeType = "text/markdown")]
    [Description("Таблицы, единицы измерения, битовые маски, кодировка карт и позиций.")]
    public static string DatabaseDoc() => ReadDoc("DATABASE.md");
}
