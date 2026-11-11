using System.Collections.Concurrent;
using System.Text;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Parsers;

namespace PKTracker.Data.Import;

/// <summary>Отбор завершённых раздач из дописываемого файла (§6.2, §15.8).</summary>
public static class CompleteHands
{
    private static readonly HandBoundary Wpn = new(["Hand #", "Game Hand #"], "*** SUMMARY ***");

    /// <summary>Формат WPN — исходный и самый частый; остальным границы задаёт их парсер.</summary>
    public static int CompleteLength(ReadOnlySpan<byte> buf, bool fileQuiet) => CompleteLength(buf, fileQuiet, Wpn);

    /// <summary>
    /// Раздача завершена, если после неё начинается следующая, или файл не менялся ≥ 1 с и блок содержит метку итогов.
    /// Возвращает число байт (от начала буфера), которые можно разобрать и зафиксировать.
    /// </summary>
    /// <remarks>
    /// Признаки свои у каждого формата (<see cref="HandBoundary"/>): с метками одного WPN файлы 888 и iPoker
    /// всегда давали 0 и молча не импортировались, а у PokerStars «следующая раздача» не находилась вовсе.
    /// </remarks>
    /// <param name="settled">
    /// Файл заведомо дописан (лежит в архиве или давно не менялся). Тогда последняя строка без перевода
    /// строки — тоже часть раздачи: у Winamax и выгрузок других трекеров файл на ней и заканчивается, и
    /// обрезка по последнему переводу строки теряла строку итогов, а с ней и всю раздачу.
    /// </param>
    public static int CompleteLength(ReadOnlySpan<byte> buf, bool fileQuiet, HandBoundary boundary, bool settled = false)
    {
        var end = Encoding.UTF8.GetBytes(boundary.EndMarker);
        if (boundary.ClosedByMarker)
        {
            int close = buf.LastIndexOf(end);
            return close < 0 ? 0 : close + end.Length;
        }
        // Последнее начало раздачи в буфере (заголовок всегда с начала строки)
        int last = -1;
        foreach (var start in boundary.Starts)
            last = Math.Max(last, buf.LastIndexOf(Encoding.UTF8.GetBytes("\n" + start)));
        if (fileQuiet)
        {
            int tailStart = last >= 0 ? last : 0;
            var tail = buf[tailStart..];
            if (tail.IndexOf(end) >= 0)
            {
                if (settled) return buf.Length;
                // Файл, который клиент ещё может дописывать: хвост надёжен только до последнего перевода строки
                int nl = buf.LastIndexOf((byte)'\n');
                return nl >= 0 ? nl + 1 : 0;
            }
        }
        return last >= 0 ? last + 1 : 0;
    }
}

public sealed record WatcherEvent(string File, ImportBatchResult Result);

/// <summary>
/// Авто-импорт: FileSystemWatcher как ускоритель + опрос каждые 2 с как источник истины (§15.8).
/// Читает с запомненного байтового смещения; незавершённый хвост оставляет до следующего тика.
/// </summary>
public sealed class HandHistoryWatcher : IAsyncDisposable
{
    private readonly TrackerDb _db;
    private readonly HandImporter _importer;
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Offset, long Size, DateTime MTime)> _state = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Файлы, которые читаются только целиком (iPoker): повторно — лишь когда изменились размер или время.</summary>
    private readonly HashSet<string> _wholeFiles = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _fsw;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public event Action<WatcherEvent>? HandsImported;
    public event Action<string, Exception>? Error;

    public HandHistoryWatcher(TrackerDb db, HandImporter importer, string folder)
    {
        _db = db;
        _importer = importer;
        _folder = folder;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        try
        {
            _fsw = new FileSystemWatcher(_folder, "*.*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Changed += (_, e) => { if (ParserRegistry.LooksLikeHistoryFile(e.FullPath)) _dirty[e.FullPath] = 0; };
            _fsw.Created += (_, e) => { if (ParserRegistry.LooksLikeHistoryFile(e.FullPath)) _dirty[e.FullPath] = 0; };
            _fsw.Renamed += (_, e) => { if (ParserRegistry.LooksLikeHistoryFile(e.FullPath)) _dirty[e.FullPath] = 0; };
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Error?.Invoke(_folder, ex); // опрос продолжит работать
        }
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await LoadStateAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var nextPoll = DateTime.MinValue;
        while (await timer.WaitForNextTickAsync(ct))
        {
            var files = new List<string>();
            if (DateTime.UtcNow >= nextPoll)
            {
                nextPoll = DateTime.UtcNow + PollInterval;
                try { files.AddRange(Directory.EnumerateFiles(_folder, "*.*", SearchOption.AllDirectories).Where(ParserRegistry.LooksLikeHistoryFile)); }
                catch (IOException ex) { Error?.Invoke(_folder, ex); }
            }
            foreach (var k in _dirty.Keys) { _dirty.TryRemove(k, out _); files.Add(k); }

            foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { await ProcessFileAsync(f, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Error?.Invoke(f, ex); }
            }
        }
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        await using var conn = await _db.Import.OpenConnectionAsync(ct);
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT source_file, byte_offset, size, mtime FROM import_state", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) _state[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetDateTime(3));
    }

    public async Task ProcessFileAsync(string file, CancellationToken ct)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) return;
        long size = fi.Length;
        var mtime = fi.LastWriteTimeUtc;
        _state.TryGetValue(file, out var st);
        long offset = st.Offset;
        if (size < offset) offset = 0;                 // файл пересоздан
        if (size == offset) return;
        if (_wholeFiles.Contains(file) && st.Size == size && st.MTime == mtime) return;   // уже разобран в этом виде

        // Формат — по началу файла (а не по хвосту, который сейчас читаем): дописанный кусок начинается с середины.
        var detected = ParserRegistry.Detect(HeadOf(file), _db.RoomCode);
        if (detected == null || !ParserRegistry.SameNetwork(detected.Value.Room, _db.RoomCode))
        {
            _state[file] = (size, size, mtime);   // чужой или не-HH файл: больше не перечитываем
            return;
        }
        var boundary = detected.Value.Parser.Boundary;
        // iPoker: ник и турнир — в заголовке сессии в начале файла, поэтому хвост отдельно не разобрать.
        // Читаем файл целиком; уже записанные раздачи импорт отсеет как дубликаты.
        if (boundary.WholeFile) { offset = 0; _wholeFiles.Add(file); }

        var bytes = FileImporter.ReadShared(file, offset);
        bool quiet = DateTime.UtcNow - mtime >= TimeSpan.FromSeconds(1);
        int len = CompleteHands.CompleteLength(bytes, quiet, boundary);
        if (len == 0)
        {
            if (boundary.WholeFile) _state[file] = (0, size, mtime);
            return;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, len);
        var results = detected.Value.Parser.Parse(text, ParserRegistry.SourceFor(file), offset).ToList();
        var result = results.Count > 0 ? await _importer.ImportAsync(results, ct) : ImportBatchResult.Empty;

        long newOffset = offset + len;
        await using (var conn = await _db.Import.OpenConnectionAsync(ct))
            await ImportStateStore.SaveAsync(conn, file, newOffset, size, mtime, ct);
        _state[file] = (newOffset, size, mtime);

        if (result.Imported > 0) HandsImported?.Invoke(new WatcherEvent(file, result));
    }

    public async ValueTask DisposeAsync()
    {
        _fsw?.Dispose();
        if (_cts != null)
        {
            await _cts.CancelAsync();
            if (_loop != null)
            {
                try { await _loop; } catch (OperationCanceledException) { }
            }
            _cts.Dispose();
        }
    }

    private static string HeadOf(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[4096];
        int n = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, n);
    }
}
