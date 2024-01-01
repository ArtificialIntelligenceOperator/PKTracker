using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PKTracker.Core.Abstractions;

namespace PKTracker.Live;

/// <summary>
/// Декодер live-протокола клиента из .data (§0.4): построчный base64 → JSON-событие.
/// Читает ТОЛЬКО события рассадки (TableState, Seat, EmptySeat). HoleCards, HandEquities и всё, что относится
/// к текущей руке, не разбирается вовсе — прямой запрет правил рума (§0.2).
/// </summary>
public sealed class PokerKingDataDecoder
{
    private readonly int _seatOffset;
    private long _game;
    private int _table;
    private int _maxSeats;
    private int? _mySeat;
    private readonly SortedDictionary<int, LiveSeat> _seats = new();

    public PokerKingDataDecoder(int seatOffset = 1) => _seatOffset = seatOffset;

    /// <summary>Обрабатывает одну строку; возвращает новую рассадку, если она изменилась.</summary>
    public LiveTableSeating? Feed(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(line); }
        catch (FormatException) { return null; } // ошибка одной строки не останавливает поток (§15.8)

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("header", out var header) || !header.TryGetProperty("type", out var typeEl)) return null;
            if (!root.TryGetProperty("payload", out var p)) return null;

            switch (typeEl.GetString())
            {
                case "TableState":
                    _game = p.TryGetProperty("gameNumber", out var g) ? g.GetInt64() : _game;
                    _table = p.TryGetProperty("tableNumber", out var t) ? t.GetInt32() : _table;
                    _maxSeats = p.TryGetProperty("maxSeatCount", out var m) ? m.GetInt32() : _maxSeats;
                    _mySeat = p.TryGetProperty("mySeat", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.GetInt32() >= 0
                        ? ms.GetInt32() + _seatOffset : null;
                    _seats.Clear();
                    if (p.TryGetProperty("sitters", out var sitters) && sitters.ValueKind == JsonValueKind.Array)
                        foreach (var s in sitters.EnumerateArray()) AddSeat(s);
                    return Snapshot();
                case "Seat":
                    AddSeat(p);
                    return Snapshot();
                case "EmptySeat":
                    if (p.TryGetProperty("seat", out var es) && _seats.Remove(es.GetInt32() + _seatOffset)) return Snapshot();
                    return null;
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private void AddSeat(JsonElement s)
    {
        if (!s.TryGetProperty("seat", out var seatEl) || !s.TryGetProperty("name", out var nameEl)) return;
        int seat = seatEl.GetInt32() + _seatOffset;
        Guid? id = s.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var gid) ? gid : null;
        string? country = s.TryGetProperty("country", out var c) ? c.GetString() : null;
        bool sitIn = !s.TryGetProperty("sitIn", out var si) || si.ValueKind != JsonValueKind.False;
        _seats[seat] = new LiveSeat(seat, nameEl.GetString() ?? "", id, country, sitIn);
    }

    private LiveTableSeating? Snapshot() =>
        _game == 0 ? null : new LiveTableSeating(_game, _table, _maxSeats, _mySeat, _seats.Values.ToList(), _seatOffset, DateTime.UtcNow);
}

/// <summary>
/// Хвостит все *.data в папке клиента и публикует рассадку. Архитектурно опционален (§0.4): HUD работает и без него.
/// </summary>
public sealed class PokerKingDataFeed : ILiveTableFeed, IDisposable
{
    private readonly string _folder;
    private readonly int _seatOffset;
    private readonly ConcurrentDictionary<string, (long Offset, PokerKingDataDecoder Decoder)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(long, int), LiveTableSeating> _latest = new();
    private Timer? _timer;
    private int _busy;

    public event Action<LiveTableSeating>? SeatingChanged;

    public PokerKingDataFeed(string folder, int seatOffset = 1)
    {
        _folder = folder;
        _seatOffset = seatOffset;
    }

    public IReadOnlyCollection<LiveTableSeating> Tables => _latest.Values.ToList();

    /// <summary>Файлы читаются с начала: TableState с полной рассадкой пишется один раз при открытии стола.</summary>
    public void Start()
    {
        if (!Directory.Exists(_folder)) return;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_folder, "*.data"))
            {
                var st = _files.GetOrAdd(f, _ => (0, new PokerKingDataDecoder(_seatOffset)));
                long size;
                try { size = new FileInfo(f).Length; } catch (IOException) { continue; }
                if (size < st.Offset) st = (0, new PokerKingDataDecoder(_seatOffset));
                if (size == st.Offset) continue;

                byte[] buf;
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    fs.Seek(st.Offset, SeekOrigin.Begin);
                    buf = new byte[size - st.Offset];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    Array.Resize(ref buf, read);
                }
                catch (IOException) { continue; }

                // Только строки, оканчивающиеся \n (§15.8)
                int lastNl = Array.LastIndexOf(buf, (byte)'\n');
                if (lastNl < 0) continue;
                var text = Encoding.ASCII.GetString(buf, 0, lastNl + 1);
                LiveTableSeating? seating = null;
                foreach (var line in text.Split('\n'))
                    if (st.Decoder.Feed(line) is { } s) seating = s;
                _files[f] = (st.Offset + lastNl + 1, st.Decoder);
                if (seating != null)
                {
                    _latest[(seating.GameNumber, seating.TableNumber)] = seating;
                    SeatingChanged?.Invoke(seating);
                }
            }
        }
        catch (IOException) { }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
