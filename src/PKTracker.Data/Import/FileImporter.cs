using System.Diagnostics;
using System.Text;
using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Parsers.Wpn;
using PKTracker.Tournaments;

namespace PKTracker.Data.Import;

public sealed record ImportProgress(int FilesDone, int FilesTotal, int Hands, int Imported, int Duplicates, int Errors,
    int RejectedFiles, double HandsPerSecond, TimeSpan Elapsed, TimeSpan? Eta, string? CurrentFile);

public sealed record ImportReport(int Files, int Hands, int Imported, int Duplicates, int Errors, int RejectedFiles,
    int Tournaments, TimeSpan Elapsed, IReadOnlyCollection<long> AffectedPlayers, IReadOnlyList<string> Messages);

/// <summary>Ручной импорт папки (§6.1): HH*.txt и *.ots, пачками, с прогрессом и отменой.</summary>
public sealed class FileImporter
{
    private readonly TrackerDb _db;
    private readonly HandImporter _hands;
    private readonly WpnHandParser _parser;
    public int BatchSize { get; init; } = 5000;

    public FileImporter(TrackerDb db, HandImporter hands)
    {
        _db = db;
        _hands = hands;
        _parser = new WpnHandParser(db.RoomCode);
    }

    /// <summary>Читает файл, который клиент может держать открытым (§15.1).</summary>
    public static byte[] ReadShared(string path, long offset = 0)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public async Task<ImportReport> ImportFolderAsync(string folder, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var hh = Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).StartsWith("HH", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
        var ots = Directory.EnumerateFiles(folder, "*.ots", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        return await ImportFilesAsync(hh, ots, progress, ct);
    }

    /// <summary>Только итоги турниров (*.ots) — без раздач: место, призы, баунти, размер поля (§0.5).</summary>
    public async Task<ImportReport> ImportTournamentsFolderAsync(string folder, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var ots = Directory.EnumerateFiles(folder, "*.ots", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        return await ImportFilesAsync(Array.Empty<string>(), ots, progress, ct);
    }

    public async Task<ImportReport> ImportFilesAsync(IReadOnlyList<string> hhFiles, IReadOnlyList<string> otsFiles,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<string>();
        var affected = new HashSet<long>();
        int filesDone = 0, hands = 0, imported = 0, dups = 0, errors = 0, rejected = 0;
        int total = hhFiles.Count + otsFiles.Count;
        var pending = new List<HandParseResult>(BatchSize);
        var pendingFiles = new List<(string File, long Size)>();

        async Task Flush()
        {
            if (pending.Count > 0)
            {
                var r = await _hands.ImportAsync(pending, ct);
                imported += r.Imported; dups += r.Duplicates; errors += r.Errors;
                affected.UnionWith(r.AffectedPlayers);
                pending.Clear();
            }
            // Позиция дозора сохраняется только после успешной записи раздач файла.
            foreach (var (f, size) in pendingFiles) await SaveImportStateAsync(f, size, ct);
            pendingFiles.Clear();
        }

        void Report(string? file)
        {
            var el = sw.Elapsed;
            double rate = hands / Math.Max(el.TotalSeconds, 0.001);
            TimeSpan? eta = filesDone > 0 ? TimeSpan.FromTicks(el.Ticks * (total - filesDone) / filesDone) : null;
            progress?.Report(new ImportProgress(filesDone, total, hands, imported, dups, errors, rejected, rate, el, eta, file));
        }

        foreach (var file in hhFiles)
        {
            ct.ThrowIfCancellationRequested();
            Report(Path.GetFileName(file));
            byte[] bytes;
            try { bytes = ReadShared(file); }
            catch (IOException ex) { messages.Add($"{file}: {ex.Message}"); filesDone++; continue; }

            // Файл может дописываться клиентом прямо сейчас — берём только завершённые раздачи (§15.8).
            bool quiet = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= TimeSpan.FromSeconds(1);
            int complete = CompleteHands.CompleteLength(bytes, quiet);
            var text = Encoding.UTF8.GetString(bytes, 0, complete);
            var results = _parser.Parse(text, WpnFileNameParser.ToSource(file)).ToList();
            hands += results.Count;

            // Защита от импорта чужого архива: файл без Hero ни в одной раздаче отклоняется целиком (§6.1).
            if (results.Count > 0 && !results.Any(r => r.Ok && _hands.HeroSeated(r.Hand!)))
            {
                rejected++;
                messages.Add($"Отклонён (нет своих раздач — правила рума): {Path.GetFileName(file)}");
                filesDone++;
                continue;
            }

            pending.AddRange(results);
            pendingFiles.Add((file, complete));
            if (pending.Count >= BatchSize) await Flush();
            filesDone++;
        }
        await Flush();

        int tourneys = 0;
        foreach (var file in otsFiles)
        {
            ct.ThrowIfCancellationRequested();
            Report(Path.GetFileName(file));
            try
            {
                var summary = TournamentSummaryParser.Parse(Encoding.UTF8.GetString(ReadShared(file)), file);
                await new TournamentImporter(_db).ImportAsync(summary, file, ct);
                tourneys++;
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
            {
                errors++;
                messages.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
            filesDone++;
        }
        Report(null);

        return new ImportReport(total, hands, imported, dups, errors, rejected, tourneys, sw.Elapsed, affected, messages);
    }

    private async Task SaveImportStateAsync(string file, long offset, CancellationToken ct)
    {
        await using var conn = await _db.Import.OpenConnectionAsync(ct);
        await ImportStateStore.SaveAsync(conn, file, offset, new FileInfo(file).Length, File.GetLastWriteTimeUtc(file), ct);
    }
}

public static class ImportStateStore
{
    public static async Task<(long Offset, long Size, DateTime MTime)?> GetAsync(NpgsqlConnection conn, string file, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT byte_offset, size, mtime FROM import_state WHERE source_file = $1", conn);
        cmd.Parameters.AddWithValue(file);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetInt64(0), r.GetInt64(1), r.GetDateTime(2));
    }

    public static async Task SaveAsync(NpgsqlConnection conn, string file, long offset, long size, DateTime mtimeUtc, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO import_state (source_file, byte_offset, size, mtime) VALUES ($1, $2, $3, $4)
            ON CONFLICT (source_file) DO UPDATE SET byte_offset = EXCLUDED.byte_offset, size = EXCLUDED.size, mtime = EXCLUDED.mtime
            """, conn);
        cmd.Parameters.AddWithValue(file);
        cmd.Parameters.AddWithValue(offset);
        cmd.Parameters.AddWithValue(size);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(mtimeUtc, DateTimeKind.Utc));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class TournamentImporter(TrackerDb db)
{
    public async Task ImportAsync(TournamentSummary s, string sourceFile, CancellationToken ct = default)
    {
        await using var conn = await db.Import.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO tournament (tournament_id, room_id, name, started_at, finished_at, buy_in, fee, currency, prize_pool,
                                    player_count, is_ko, ko_kind, source_file)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            ON CONFLICT (tournament_id) DO UPDATE SET
                name = coalesce(EXCLUDED.name, tournament.name),
                started_at = coalesce(EXCLUDED.started_at, tournament.started_at),
                finished_at = coalesce(EXCLUDED.finished_at, tournament.finished_at),
                buy_in = coalesce(EXCLUDED.buy_in, tournament.buy_in),
                fee = coalesce(EXCLUDED.fee, tournament.fee),
                currency = coalesce(EXCLUDED.currency, tournament.currency),
                prize_pool = coalesce(EXCLUDED.prize_pool, tournament.prize_pool),
                player_count = coalesce(EXCLUDED.player_count, tournament.player_count),
                is_ko = EXCLUDED.is_ko, ko_kind = EXCLUDED.ko_kind, source_file = EXCLUDED.source_file
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue(s.TournamentId);
            cmd.Parameters.AddWithValue(db.RoomId);
            cmd.Parameters.AddWithValue((object?)s.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)s.StartedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)s.FinishedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)s.BuyIn ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)s.Fee ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)(s.Currency?.Length > 3 ? s.Currency[..3] : s.Currency) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)s.PrizePool ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)s.PlayerCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue(s.IsKnockout);
            cmd.Parameters.AddWithValue(s.IsMystery ? (short)2 : s.IsKnockout ? (short)1 : (short)0);
            cmd.Parameters.AddWithValue(sourceFile);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var del = new NpgsqlCommand("DELETE FROM tournament_result WHERE tournament_id = $1", conn, tx))
        {
            del.Parameters.AddWithValue(s.TournamentId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var f in s.Finishes)
        {
            await using var cmd = new NpgsqlCommand("""
                WITH p AS (
                    INSERT INTO player (room_id, nickname) VALUES ($1, $2)
                    ON CONFLICT (room_id, nickname) DO UPDATE SET nickname = EXCLUDED.nickname
                    RETURNING coalesce(canonical_id, player_id) AS id
                )
                INSERT INTO tournament_result (tournament_id, player_id, finish_position, prize, ticket_value, bounty_won, entry_no)
                SELECT $3, id, $4, $5, $6, $7, $8 FROM p
                ON CONFLICT (tournament_id, player_id, entry_no) DO UPDATE
                   SET finish_position = EXCLUDED.finish_position, prize = EXCLUDED.prize,
                       ticket_value = EXCLUDED.ticket_value, bounty_won = EXCLUDED.bounty_won
                """, conn, tx);
            cmd.Parameters.AddWithValue(db.RoomId);
            cmd.Parameters.AddWithValue(f.PlayerName);
            cmd.Parameters.AddWithValue(s.TournamentId);
            cmd.Parameters.AddWithValue((object?)f.FinishPosition ?? DBNull.Value);
            cmd.Parameters.AddWithValue(f.Prize);
            cmd.Parameters.AddWithValue(f.TicketValue);
            cmd.Parameters.AddWithValue(f.BountyWon);
            cmd.Parameters.AddWithValue((short)f.EntryNo);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
