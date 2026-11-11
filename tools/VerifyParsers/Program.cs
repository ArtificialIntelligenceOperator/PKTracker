using System.Text;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Parsers;

// Использование: VerifyParsers <папка|файл> [--room CODE] [--show N] [--dump ID]
// Проверка парсеров на корпусе: формат по ParserRegistry.Detect, разбор, контекст, денежные инварианты.
if (args.Contains("--selftest")) return SelfTest.Run();
string? path = null; string active = "POKERKING"; int show = 20; string? only = null; long? dump = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--room") active = args[++i];
    else if (args[i] == "--show") show = int.Parse(args[++i]);
    else if (args[i] == "--only") only = args[++i];
    else if (args[i] == "--dump") dump = long.Parse(args[++i]);
    else path = args[i];
}
if (path == null) { Console.WriteLine("usage: VerifyParsers <dir> [--room CODE] [--show N] [--only ROOM]"); return 1; }

Console.OutputEncoding = Encoding.UTF8;
var files = Directory.Exists(path)
    ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Where(f => ParserRegistry.LooksLikeHistoryFile(f)).OrderBy(f => f, StringComparer.Ordinal).ToList()
    : new List<string> { path };

var stat = new Dictionary<string, Stat>();
int undetected = 0; var undetectedFiles = new List<string>();
foreach (var f in files)
{
    string text;
    try { text = File.ReadAllText(f); } catch (Exception ex) { Console.WriteLine($"READ FAIL {f}: {ex.Message}"); continue; }
    var head = text.Length > 4096 ? text.AsSpan(0, 4096) : text.AsSpan();
    var det = ParserRegistry.Detect(head, active);
    if (det == null) { undetected++; if (undetectedFiles.Count < 10) undetectedFiles.Add(f); continue; }
    var (parser, room) = det.Value;
    if (only != null && room.Code != only) continue;
    if (!stat.TryGetValue(room.Code, out var st)) stat[room.Code] = st = new Stat();
    st.Files++;
    var src = ParserRegistry.SourceFor(f);
    foreach (var r in parser.Parse(text, src))
    {
        if (!r.Ok)
        {
            st.ParseErrors++;
            if (st.ParseErrorMsgs.Count < show) st.ParseErrorMsgs.Add($"{Path.GetFileName(f)} @{r.Offset}: {r.Error}");
            st.ErrKinds[Kind(r.Error!)] = st.ErrKinds.GetValueOrDefault(Kind(r.Error!)) + 1;
            continue;
        }
        var h = r.Hand!;
        st.Hands++;
        if (dump == h.ExternalHandId) Dumper.Dump(h);
        if (h.Seats.Any(x => !x.Dealt)) st.WithUndealt++;
        if (h.HeroName != null) st.WithHero++;
        if (h.Has(HandFlags.DeadButton)) st.DeadBtn++;
        try
        {
            var ctx = HandContextBuilder.Build(h, computeEv: false);
            var errs = Invariants.Check(h, ctx);
            if (errs.Count > 0)
            {
                st.Violations++;
                if (st.ViolationMsgs.Count < show) st.ViolationMsgs.Add($"{Path.GetFileName(f)} hand {h.ExternalHandId}: {string.Join("; ", errs)}");
            }
        }
        catch (Exception ex)
        {
            st.CtxExceptions++;
            if (st.CtxMsgs.Count < show) st.CtxMsgs.Add($"{Path.GetFileName(f)} hand {h.ExternalHandId}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

foreach (var (code, st) in stat.OrderBy(k => k.Key))
{
    Console.WriteLine($"=== {code}: files {st.Files}, hands {st.Hands}, parse errors {st.ParseErrors}, invariant violations {st.Violations}, ctx exceptions {st.CtxExceptions}");
    Console.WriteLine($"  info: hero found in {st.WithHero}, hands with non-dealt seats {st.WithUndealt}, dead button {st.DeadBtn}");
    if (st.ErrKinds.Count > 0)
    {
        Console.WriteLine("  error kinds:");
        foreach (var (k, v) in st.ErrKinds.OrderByDescending(x => x.Value).Take(show)) Console.WriteLine($"    {v,6}  {k}");
    }
    foreach (var m in st.ParseErrorMsgs) Console.WriteLine("  PARSE: " + m);
    foreach (var m in st.ViolationMsgs) Console.WriteLine("  INV:   " + m);
    foreach (var m in st.CtxMsgs) Console.WriteLine("  CTX:   " + m);
}
Console.WriteLine($"undetected files: {undetected}");
foreach (var f in undetectedFiles) Console.WriteLine("  " + f);
return 0;

static string Kind(string msg)
{
    // Группировка ошибок: цифры и суммы не различаем, чтобы видеть виды строк.
    var s = msg.Length > 70 ? msg[..70] : msg;
    return System.Text.RegularExpressions.Regex.Replace(s, @"\d+", "#");
}

sealed class Stat
{
    public int Files, Hands, ParseErrors, Violations, CtxExceptions, WithUndealt, WithHero, DeadBtn;
    public List<string> ParseErrorMsgs = new(), ViolationMsgs = new(), CtxMsgs = new();
    public Dictionary<string, int> ErrKinds = new();
}

static class Dumper
{
public static void Dump(ParsedHand h)
{
    Console.WriteLine($"--- {h.RoomCode} hand {h.ExternalHandId} {h.GameKind} {h.Currency} t={h.TournamentId} lvl={h.TournamentLevel} {h.PlayedAtUtc:u} blinds {h.SmallBlind}/{h.BigBlind} ante {h.Ante} btn {h.ButtonSeat} max {h.MaxSeats} flags {h.Flags} hero {h.HeroName}");
    Console.WriteLine($"    table '{h.TableName}' game {h.ExternalGameId} tbl {h.ExternalTableId} pot {h.TotalPot} rake {h.Rake} fees {h.TotalExtraFees} boards {string.Join("|", h.Boards.Select(b => string.Join(" ", b)))}");
    foreach (var s in h.Seats) Console.WriteLine($"    seat {s.Seat} {s.Name} stack {s.Stack} dealt {s.Dealt} won {s.Won} hero {s.IsHero} cards {s.HoleCards[0]}{s.HoleCards[1]} out {s.SittingOut}");
    foreach (var a in h.Actions) Console.WriteLine($"    {a}");
}
}
