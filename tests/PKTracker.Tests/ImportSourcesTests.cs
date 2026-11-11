using System.IO.Compression;
using System.Text;
using PKTracker.Data;
using PKTracker.Data.Import;

namespace PKTracker.Tests;

/// <summary>
/// Что именно импорт считает файлом для загрузки: папки, отдельные файлы, архивы и архивы в архивах.
/// База не нужна — проверяется только разворачивание путей и чтение архива в память.
/// </summary>
public class ImportSourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pkt_import_" + Guid.NewGuid().ToString("N"));

    public ImportSourcesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static byte[] Zip(params (string Name, byte[] Body)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, body) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(body);
            }
        return ms.ToArray();
    }

    private static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData("HH20260101 T1.txt", ImportKind.History)]
    [InlineData("1150360454.XML", ImportKind.History)]
    [InlineData("summary.ots", ImportKind.Summary)]
    [InlineData("hands.ZIP", ImportKind.Archive)]
    [InlineData("hands.rar", ImportKind.Archive)]
    [InlineData("hands.7z", ImportKind.Archive)]
    [InlineData("readme.pdf", ImportKind.None)]
    public void ClassifyGoesByExtension(string name, ImportKind expected) => Assert.Equal(expected, ImportSources.Classify(name));

    [Fact]
    public void ExpandWalksFoldersAndKeepsExplicitFiles()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "2026", "1"));
        File.WriteAllText(Path.Combine(_dir, "2026", "1", "a.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "2026", "b.xml"), "x");
        File.WriteAllText(Path.Combine(_dir, "t.ots"), "x");
        File.WriteAllText(Path.Combine(_dir, "notes.pdf"), "x");
        File.WriteAllBytes(Path.Combine(_dir, "old.zip"), Zip(("c.txt", T("x"))));
        var odd = Path.Combine(_dir, "export.hh");
        File.WriteAllText(odd, "x");

        var messages = new List<string>();
        var units = ImportSources.Expand(new[] { _dir, odd, odd, Path.Combine(_dir, "missing") }, messages);

        // В папке — только знакомые расширения; посторонний pdf не берётся.
        Assert.Equal(new[] { "a.txt", "b.xml", "export.hh", "old.zip", "t.ots" }, units.Select(u => Path.GetFileName(u.Path)).OrderBy(n => n).ToArray());
        Assert.Equal(ImportKind.Archive, units.Single(u => u.Path.EndsWith("old.zip")).Kind);
        Assert.Equal(ImportKind.Summary, units.Single(u => u.Path.EndsWith("t.ots")).Kind);
        // Файл, указанный явно, берётся и с незнакомым расширением — формат всё равно определяется по содержимому.
        Assert.Equal(ImportKind.History, units.Single(u => u.Path == odd).Kind);
        Assert.Single(messages);
        Assert.Contains("missing", messages[0]);
    }

    [Fact]
    public void ArchiveIsReadWithNestedArchivesAndWithoutUnpacking()
    {
        var inner = Zip(("deep/d.txt", T("inner hand")), ("deep/skip.jpg", T("img")));
        var path = Path.Combine(_dir, "pack.zip");
        File.WriteAllBytes(path, Zip(
            ("pack/2026/a.txt", T("hand A")),
            ("pack/2026/b.xml", T("<session/>")),
            ("pack/t.ots", T("{}")),
            ("pack/photo.png", T("img")),
            ("pack/7/7.zip", inner)));

        var messages = new List<string>();
        int announced = 0;
        var entries = ImportSources.ReadArchive(path, messages, n => announced += n).ToList();

        Assert.Empty(messages);
        Assert.Equal(new[] { "a.txt", "b.xml", "d.txt", "t.ots" }, entries.Select(e => Path.GetFileName(e.Name)).OrderBy(n => n).ToArray());
        Assert.Equal("hand A", Encoding.UTF8.GetString(entries.Single(e => e.Name.EndsWith("a.txt")).Bytes));
        Assert.Equal("inner hand", Encoding.UTF8.GetString(entries.Single(e => e.Name.EndsWith("d.txt")).Bytes));
        Assert.Equal(ImportKind.Summary, entries.Single(e => e.Name.EndsWith("t.ots")).Kind);
        // Имя записи — путь архива и путь внутри него: по нему раздачу потом можно найти.
        Assert.EndsWith("pack.zip!/pack/7/7.zip!/deep/d.txt", entries.Single(e => e.Name.EndsWith("d.txt")).Name);
        // Прогрессу сообщается всё, что будет обработано: три файла и вложенный архив снаружи, один файл внутри.
        Assert.Equal(5, announced);
        // На диск ничего не распаковано.
        Assert.Equal(new[] { "pack.zip" }, Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void BrokenArchiveIsReportedNotThrown()
    {
        var path = Path.Combine(_dir, "broken.zip");
        File.WriteAllBytes(path, T("это не архив, а текст с расширением zip"));
        var messages = new List<string>();
        Assert.Empty(ImportSources.ReadArchive(path, messages).ToList());
        Assert.Single(messages);
        Assert.Contains("broken.zip", messages[0]);
    }

    [Fact]
    public void SettledFileIsTakenToTheLastByteEvenWithoutTrailingNewline()
    {
        // Так заканчиваются файлы Winamax: последняя строка итогов без перевода строки.
        var boundary = new PKTracker.Parsers.Winamax.WinamaxHandParser().Boundary;
        var buf = T("Winamax Poker - Tournament \"X\" ...\nSeat 1: Hero (20000)\n*** SUMMARY ***\nTotal pot 300 | No rake\nSeat 1: Hero won 300");
        int lastLine = Array.LastIndexOf(buf, (byte)'\n') + 1;
        // Файл, который клиент может дописывать, — только до последней целой строки…
        Assert.Equal(lastLine, CompleteHands.CompleteLength(buf, fileQuiet: true, boundary));
        // …а из архива или давно не менявшийся — целиком, иначе строка «won 300» терялась вместе с раздачей.
        Assert.Equal(buf.Length, CompleteHands.CompleteLength(buf, fileQuiet: true, boundary, settled: true));
        // Незавершённая раздача (нет метки итогов) не берётся и в этом случае.
        var open = T("Winamax Poker - Tournament \"X\" ...\nSeat 1: Hero (20000)\n*** PRE-FLOP ***\nHero folds");
        Assert.Equal(0, CompleteHands.CompleteLength(open, fileQuiet: true, boundary, settled: true));
    }

    [Theory]
    [InlineData("test", "test", true)]
    [InlineData("  Winamax 2026 ", "winamax_2026", true)]
    [InlineData("my-base", "my_base", true)]
    [InlineData("", "", false)]
    [InlineData("2026", "2026", false)]              // начинается с цифры
    [InlineData("база", "база", false)]              // только латиница: имя уходит в pg_dump и строку подключения
    [InlineData("a\"; DROP DATABASE x; --", "a\";_drop_database_x;___", false)]
    [InlineData("postgres", "postgres", false)]      // служебная
    public void DatabaseNameIsNormalizedAndChecked(string input, string expected, bool ok)
    {
        var name = DatabaseCatalog.Normalize(input, out var error);
        Assert.Equal(expected, name);
        Assert.Equal(ok, error == null);
    }

    [Fact]
    public void SwitchingDatabaseKeepsTheRestOfConnectionString()
    {
        const string cs = "Host=localhost;Port=5433;Username=pktracker;Password=secret;Database=pktracker";
        var switched = DatabaseCatalog.WithDatabase(cs, "test");
        Assert.Equal("test", DatabaseCatalog.Current(switched));
        var b = new Npgsql.NpgsqlConnectionStringBuilder(switched);
        Assert.Equal("localhost", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("pktracker", b.Username);
        Assert.Equal("secret", b.Password);
    }
}
