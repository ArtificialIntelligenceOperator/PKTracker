using System.Collections.Concurrent;
using System.Text;
using PKTracker.Core.Model;
using PKTracker.Parsers.Wpn;

namespace PKTracker.Data.Import;

/// <summary>Отбор завершённых раздач из дописываемого файла (§6.2, §15.8).</summary>
public static class CompleteHands
{
    private static readonly byte[] NextHand1 = "\nHand #"u8.ToArray();
    private static readonly byte[] NextHand2 = "\nGame Hand #"u8.ToArray();

    /// <summary>
    /// Раздача завершена, если после неё начинается следующая, или файл не менялся ≥ 1 с и блок содержит SUMMARY.
    /// Возвращает число байт (от начала буфера), которые можно разобрать и зафиксировать.
    /// </summary>
    public static int CompleteLength(ReadOnlySpan<byte> buf, bool fileQuiet)
    {
        // Последнее начало раздачи в буфере
        int last = Math.Max(buf.LastIndexOf(NextHand1), buf.LastIndexOf(NextHand2));
        if (fileQuiet)
        {
            int tailStart = last >= 0 ? last : 0;
            var tail = buf[tailStart..];
            if (tail.IndexOf("*** SUMMARY ***"u8) >= 0)
            {
                // Хвост целиком дописан только до последнего перевода строки
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
    private readonly WpnHandParser _parser;
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Offset, long Size, DateTime MTime)> _state = new(StringComparer.OrdinalIgnoreCase);
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
        _parser = new WpnHandParser(db.RoomCode);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        try
        {
            _fsw = new FileSystemWatcher(_folder, "HH*.txt")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Changed += (_, e) => _dirty[e.FullPath] = 0;
            _fsw.Created += (_, e) => _dirty[e.FullPath] = 0;
            _fsw.Renamed += (_, e) => _dirty[e.FullPath] = 0;
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
                try { files.AddRange(Directory.EnumerateFiles(_folder, "HH*.txt", SearchOption.AllDirectories)); }
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

        var bytes = FileImporter.ReadShared(file, offset);
        bool quiet = DateTime.UtcNow - mtime >= TimeSpan.FromSeconds(1);
        int len = CompleteHands.CompleteLength(bytes, quiet);
        if (len == 0) return;

        var text = Encoding.UTF8.GetString(bytes, 0, len);
        var results = _parser.Parse(text, WpnFileNameParser.ToSource(file), offset).ToList();
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
}
