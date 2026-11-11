using System.Diagnostics;
using System.Text;
using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Parsers;
using PKTracker.Tournaments;

namespace PKTracker.Data.Import;

public sealed record ImportProgress(int FilesDone, int FilesTotal, int Hands, int Imported, int Duplicates, int Errors,
    int RejectedFiles, double HandsPerSecond, TimeSpan Elapsed, TimeSpan? Eta, string? CurrentFile);

public sealed record ImportReport(int Files, int Hands, int Imported, int Duplicates, int Errors, int RejectedFiles,
    int Tournaments, TimeSpan Elapsed, IReadOnlyCollection<long> AffectedPlayers, IReadOnlyList<string> Messages)
{
    /// <summary>Ники, чьи карты розданы в отклонённых файлах, но которых нет среди аккаунтов Hero.</summary>
    public IReadOnlyList<UnknownHero> UnknownHeroes { get; init; } = Array.Empty<UnknownHero>();

    /// <summary>Файлы без карманных карт какого-либо игрока — наблюдаемые или купленные раздачи.</summary>
    public int ObservedFiles { get; init; }

    /// <summary>
    /// Файлы сети WPN, которые не удалось отнести к руму: скины сети по тексту неразличимы, а ник владельца
    /// файлов не заведён ни для одного из них. Рум здесь не определён — его выбирает пользователь.
    /// </summary>
    public UnknownHero? UnknownWpn { get; init; }

    /// <summary>Раздачи из принятых файлов, где Hero не сидел за столом (не загружены и ошибкой не считаются).</summary>
    public int NotOwnHands { get; init; }
}

/// <summary>
/// Игрок, которому в файлах розданы карты (то есть файлы записаны его клиентом), но не заведённый как Hero.
/// Интерфейс по этому списку спрашивает, не аккаунт ли это пользователя, — вместо молчаливого отказа.
/// </summary>
public sealed record UnknownHero(string RoomCode, string RoomTitle, string Nickname, int Files, int Hands);

/// <summary>
/// Ручной импорт (§6.1): папки, отдельные файлы и архивы; пачками, с прогрессом и отменой.
///
/// Рум раздачи определяется по содержимому файла, поэтому выгрузку с несколькими румами можно загрузить
/// за один раз: у каждого рума свой <see cref="HandImporter"/> со своими никами Hero. Исключение — скины
/// сети WPN: их текст неразличим, и такие файлы принимаются только когда активным выбран скин WPN.
/// </summary>
public sealed class FileImporter
{
    private readonly TrackerDb _db;
    private readonly HandImporter _hands;
    private readonly Func<RoomInfo, CancellationToken, Task<HandImporter?>>? _forRoom;
    public int BatchSize { get; init; } = 5000;

    /// <summary>
    /// Принимать раздачи румов, чьи правила запрещают сторонний импорт истории (<see cref="RoomHudPolicy.ImportNeedsOptIn"/> —
    /// сеть GG). Выключено по умолчанию, включает сам игрок на свой риск: <see cref="TrackerSettings.ImportGgHands"/>,
    /// в консоли <c>import --gg</c>. При запущенном клиенте такого рума импорт не идёт в любом случае.
    /// </summary>
    public bool AllowRestrictedRooms { get; init; }

    /// <param name="forRoom">Импортёр для рума, отличного от активного (его id и ники Hero); null — другие румы пропускаются.</param>
    public FileImporter(TrackerDb db, HandImporter hands, Func<RoomInfo, CancellationToken, Task<HandImporter?>>? forRoom = null)
    {
        _db = db;
        _hands = hands;
        _forRoom = forRoom;
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

    public Task<ImportReport> ImportFolderAsync(string folder, IProgress<ImportProgress>? progress = null, CancellationToken ct = default) =>
        ImportPathsAsync(new[] { folder }, progress, ct);

    /// <summary>Только итоги турниров (*.ots) — без раздач: место, призы, баунти, размер поля (§0.5).</summary>
    public Task<ImportReport> ImportTournamentsFolderAsync(string folder, IProgress<ImportProgress>? progress = null, CancellationToken ct = default) =>
        ImportUnitsAsync(ImportSources.Expand(new[] { folder }, new List<string>()).Where(u => u.Kind == ImportKind.Summary).ToList(),
            new List<string>(), progress, ct);

    public Task<ImportReport> ImportFilesAsync(IReadOnlyList<string> hhFiles, IReadOnlyList<string> otsFiles,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default) =>
        ImportUnitsAsync(hhFiles.Select(f => new ImportUnit(f, ImportKind.History))
            .Concat(otsFiles.Select(f => new ImportUnit(f, ImportKind.Summary))).ToList(), new List<string>(), progress, ct);

    /// <summary>
    /// Импорт всего, что указал пользователь: папки (рекурсивно), файлы истории, итоги турниров и архивы
    /// .zip / .rar / .7z — в любом сочетании. Формат каждого файла определяется по содержимому, а не по имени.
    /// </summary>
    public Task<ImportReport> ImportPathsAsync(IEnumerable<string> paths, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var messages = new List<string>();
        return ImportUnitsAsync(ImportSources.Expand(paths, messages), messages, progress, ct);
    }

    private async Task<ImportReport> ImportUnitsAsync(IReadOnlyList<ImportUnit> units, List<string> messages,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var affected = new HashSet<long>();
        int filesDone = 0, hands = 0, imported = 0, dups = 0, errors = 0, rejected = 0, notOwn = 0;
        // Архив считается одним файлом, пока не открыт; затем к общему числу добавляется его содержимое.
        int total = units.Count;
        var pending = new Dictionary<HandImporter, List<HandParseResult>>();
        int pendingCount = 0;
        var pendingFiles = new List<(string File, long Size)>();
        var importers = new Dictionary<string, HandImporter?>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<(string Name, byte[] Bytes)>();

        // Причины отказа копятся счётчиками: тысяча одинаковых строк «отклонён» ничего не объясняет.
        var unknown = new Dictionary<(string Room, string Nick), (int Files, int Hands)>();
        var otherRoom = new Dictionary<string, int>();
        int observedFiles = 0, observedHands = 0, unparsed = 0;
        string? unparsedSample = null;
        string? wpnNick = null;
        int wpnProbes = 0;
        var restricted = new Dictionary<string, int>();
        int restrictedWhileRunning = 0;

        async Task Flush()
        {
            foreach (var (importer, list) in pending)
            {
                if (list.Count == 0) continue;
                var r = await importer.ImportAsync(list, ct);
                imported += r.Imported; dups += r.Duplicates; errors += r.Errors; notOwn += r.NotOwn;
                affected.UnionWith(r.AffectedPlayers);
                list.Clear();
            }
            pendingCount = 0;
            // Позиция дозора сохраняется только после успешной записи раздач файла.
            foreach (var (f, size) in pendingFiles) await SaveImportStateAsync(f, size, ct);
            pendingFiles.Clear();
        }

        void Report(string? file)
        {
            var el = sw.Elapsed;
            double rate = hands / Math.Max(el.TotalSeconds, 0.001);
            TimeSpan? eta = filesDone > 0 && total > filesDone ? TimeSpan.FromTicks(el.Ticks * (total - filesDone) / filesDone) : null;
            progress?.Report(new ImportProgress(filesDone, total, hands, imported, dups, errors, rejected, rate, el, eta, file));
        }

        async Task<HandImporter?> ImporterFor(RoomInfo room)
        {
            if (ParserRegistry.SameNetwork(room, _db.RoomCode)) return _hands;
            if (_forRoom == null) return null;
            // Скины WPN по тексту неразличимы, поэтому для всей сети импортёр один: какой именно скин —
            // решает тот, кто создаёт импортёр (по никам Hero), а не первый попавшийся парсер.
            string key = room.Network == "WPN" ? "WPN" : room.Code;
            if (!importers.TryGetValue(key, out var importer))
                importers[key] = importer = await _forRoom(room, ct);
            return importer;
        }

        async Task History(string name, byte[] bytes, string? diskPath)
        {
            // Файл на диске может дописываться клиентом прямо сейчас — берём только завершённые раздачи (§15.8).
            bool quiet = diskPath == null || DateTime.UtcNow - File.GetLastWriteTimeUtc(diskPath) >= TimeSpan.FromSeconds(1);
            // Формат — по началу файла: от него зависит, как искать конец последней раздачи.
            var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var detected = ParserRegistry.Detect(head, _db.RoomCode);
            if (detected == null) return; // не история раздач (итоги турниров, логи клиента)
            var room = detected.Value.Room;
            // GG запрещает сторонним программам импортировать свои раздачи, а с 01.12.2026 её клиент ещё и ищет
            // сторонний покерный софт среди процессов. Поэтому без явного согласия игрока файл GG не принимается,
            // а при запущенном клиенте GG — не принимается вовсе: трекер работает рядом с ним для других румов
            // именно потому, что в это время не делает ничего, что относится к сети GG.
            if (RoomHudPolicy.For(room.Code).ImportNeedsOptIn)
            {
                if (RoomClientProbe.ForbiddenCoexistence() != null) { rejected++; restrictedWhileRunning++; return; }
                if (!AllowRestrictedRooms)
                {
                    rejected++;
                    restricted[room.Title] = restricted.GetValueOrDefault(room.Title) + 1;
                    return;
                }
            }
            var importer = await ImporterFor(room);
            if (importer == null)
            {
                rejected++;
                string key = room.Network == "WPN" ? "WPN" : room.Title;
                otherRoom[key] = otherRoom.GetValueOrDefault(key) + 1;
                // Ник владельца файлов нужен для подсказки; ради него достаточно разобрать несколько файлов, а не все.
                if (key == "WPN" && wpnNick == null && wpnProbes++ < 5)
                    wpnNick = detected.Value.Parser.Parse(Encoding.UTF8.GetString(bytes), ParserRegistry.SourceFor(name))
                        .Where(r => r.Ok).Select(r => r.Hand!.HeroName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
                return;
            }
            // Архив и файл, не менявшийся полминуты, уже никто не дописывает — берём их до последнего байта.
            bool settled = diskPath == null || DateTime.UtcNow - File.GetLastWriteTimeUtc(diskPath) >= TimeSpan.FromSeconds(30);
            int complete = CompleteHands.CompleteLength(bytes, quiet, detected.Value.Parser.Boundary, settled);
            var text = Encoding.UTF8.GetString(bytes, 0, complete);
            var results = detected.Value.Parser.Parse(text, ParserRegistry.SourceFor(name)).ToList();
            hands += results.Count;
            if (results.Count == 0) return;

            var ok = results.Where(r => r.Ok).ToList();
            if (ok.Count == 0)
            {
                rejected++;
                unparsed++;
                unparsedSample ??= $"{Path.GetFileName(name)}: {results[0].Error}";
                return;
            }
            // Защита от импорта чужого архива: файл без Hero ни в одной раздаче отклоняется целиком (§6.1) —
            // если игрок сам не включил загрузку раздач без него (AllowObserved).
            if (!importer.AllowObserved && !ok.Any(r => importer.HeroSeated(r.Hand!)))
            {
                rejected++;
                // Клиент рума пишет карманные карты только своему игроку, так что ник из «Dealt to» —
                // владелец файла. Если карт нет ни у кого, раздачи записаны наблюдателем.
                var nick = ok.Select(r => r.Hand!.HeroName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
                if (nick == null) { observedFiles++; observedHands += ok.Count; }
                else
                {
                    var prev = unknown.GetValueOrDefault((room.Code, nick));
                    unknown[(room.Code, nick)] = (prev.Files + 1, prev.Hands + ok.Count);
                }
                return;
            }

            if (!pending.TryGetValue(importer, out var list)) pending[importer] = list = new List<HandParseResult>(BatchSize);
            list.AddRange(results);
            pendingCount += results.Count;
            if (diskPath != null) pendingFiles.Add((diskPath, complete));
            if (pendingCount >= BatchSize) await Flush();
        }

        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            Report(Path.GetFileName(unit.Path));
            if (unit.Kind == ImportKind.Archive)
            {
                foreach (var entry in ImportSources.ReadArchive(unit.Path, messages, n => total += n))
                {
                    ct.ThrowIfCancellationRequested();
                    Report(Path.GetFileName(entry.Name));
                    if (entry.Kind == ImportKind.Summary) summaries.Add((entry.Name, entry.Bytes));
                    else await History(entry.Name, entry.Bytes, null);
                    filesDone++;
                }
                filesDone++;
                continue;
            }
            byte[] bytes;
            try { bytes = ReadShared(unit.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"{unit.Path}: {ex.Message}");
                filesDone++;
                continue;
            }
            if (unit.Kind == ImportKind.Summary) summaries.Add((unit.Path, bytes));
            else await History(unit.Path, bytes, unit.Path);
            filesDone++;
        }
        await Flush();

        // Итоги турниров — после раздач, как и раньше: сами турниры к этому моменту уже заведены импортом рук.
        int tourneys = 0;
        foreach (var (name, bytes) in summaries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var summary = TournamentSummaryParser.Parse(Encoding.UTF8.GetString(bytes), name);
                await new TournamentImporter(_db).ImportAsync(summary, name, ct);
                tourneys++;
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
            {
                errors++;
                messages.Add($"{Path.GetFileName(name)}: {ex.Message}");
            }
        }
        Report(null);

        var unknownHeroes = unknown
            .Select(kv => new UnknownHero(kv.Key.Room, ParserRegistry.Room(kv.Key.Room)?.Title ?? kv.Key.Room, kv.Key.Nick, kv.Value.Files, kv.Value.Hands))
            .OrderByDescending(u => u.Hands).ToList();
        var summary2 = new List<string>();
        foreach (var u in unknownHeroes)
            summary2.Add($"Не импортировано: {u.Files} файл(ов), {u.Hands:N0} раздач игрока «{u.Nickname}» ({u.RoomTitle}) — этого ника нет в списке «Мои ники». " +
                         "Если это ваш аккаунт, добавьте его и повторите импорт.");
        if (observedFiles > 0)
            summary2.Add($"Отклонено: {observedFiles} файл(ов), {observedHands:N0} раздач без карманных карт игрока — это чужие раздачи (наблюдаемые или купленные). " +
                         "По умолчанию трекер принимает только собственные раздачи: так требуют правила румов. " +
                         "Загрузку таких раздач можно включить на вкладке «Импорт» — на свой риск.");
        foreach (var (room, n) in otherRoom)
            summary2.Add(room == "WPN"
                ? $"Пропущено: {n} файл(ов) сети WPN{(wpnNick != null ? $" (игрок «{wpnNick}»)" : "")} — по файлу не определить, какой это рум сети. " +
                  "Добавьте свой ник в «Мои ники», выбрав рум WPN (PokerKing, ACR, Black Chip или True Poker), и повторите импорт."
                : $"Пропущено: {n} файл(ов) формата {room} — этот рум не заведён в базе.");
        foreach (var (room, n) in restricted)
            summary2.Add($"Не импортировано: {n} файл(ов) {room}. Правила этой сети запрещают сторонним программам импортировать её раздачи " +
                         "(в том числе выгруженные из PokerCraft). Импорт можно включить на вкладке «Импорт» — на свой риск.");
        if (restrictedWhileRunning > 0)
            summary2.Add($"Не импортировано: {restrictedWhileRunning} файл(ов) сети GG — запущен клиент GG. Пока он открыт, трекер не работает " +
                         "с раздачами GG; повторите импорт после сессии, когда клиент GG закрыт. Раздачи других румов импортируются как обычно.");
        if (notOwn > 0)
            summary2.Add($"Пропущено: {notOwn:N0} раздач, где вас не было за столом (в принятых файлах).");
        if (unparsed > 0)
            summary2.Add($"Не разобрано: {unparsed} файл(ов), ни одной раздачи не прочитано. Первый: {unparsedSample}");
        summary2.AddRange(messages);

        return new ImportReport(total, hands, imported, dups, errors, rejected, tourneys, sw.Elapsed, affected, summary2)
        {
            UnknownHeroes = unknownHeroes,
            UnknownWpn = wpnNick != null && otherRoom.TryGetValue("WPN", out int wpnFiles) ? new UnknownHero("WPN", "сеть WPN", wpnNick, wpnFiles, 0) : null,
            ObservedFiles = observedFiles,
            NotOwnHands = notOwn,
        };
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
