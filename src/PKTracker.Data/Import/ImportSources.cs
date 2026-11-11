using PKTracker.Parsers;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace PKTracker.Data.Import;

/// <summary>Чем является файл для импорта.</summary>
public enum ImportKind { None, History, Summary, Archive }

/// <summary>Путь на диске, который нужно импортировать: файл истории, итог турнира или архив.</summary>
public sealed record ImportUnit(string Path, ImportKind Kind);

/// <summary>Файл из архива, уже прочитанный в память. <see cref="Name"/> — «архив!/путь/внутри».</summary>
public sealed record ArchiveEntry(string Name, ImportKind Kind, byte[] Bytes);

/// <summary>
/// Разворачивает то, что указал пользователь (папки, отдельные файлы, архивы), в список файлов для импорта.
///
/// Архивы читаются потоком прямо в память, без распаковки на диск: историю присылают и хранят
/// именно архивами, а временная распаковка сотен мегабайт на системный диск — лишний шаг, который
/// к тому же оставляет чужие ники в папке TEMP.
/// </summary>
public static class ImportSources
{
    /// <summary>Архивы, которые умеем читать.</summary>
    public static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z" };

    /// <summary>Файл истории крупнее этого внутри архива пропускается: это не история одной сессии, а ошибка или «бомба».</summary>
    public const long MaxEntryBytes = 512L << 20;

    /// <summary>Архив в архиве разбирается, но не глубже: в выгрузках трекеров вложенность — один уровень.</summary>
    public const int MaxDepth = 3;

    public static ImportKind Classify(string name)
    {
        var ext = System.IO.Path.GetExtension(name);
        if (ext.Equals(".ots", StringComparison.OrdinalIgnoreCase)) return ImportKind.Summary;
        if (ParserRegistry.LooksLikeHistoryFile(name)) return ImportKind.History;
        foreach (var a in ArchiveExtensions)
            if (ext.Equals(a, StringComparison.OrdinalIgnoreCase)) return ImportKind.Archive;
        return ImportKind.None;
    }

    /// <summary>Фильтр диалога выбора файлов.</summary>
    public const string DialogFilter =
        "История и архивы (*.txt;*.xml;*.ots;*.zip;*.rar;*.7z)|*.txt;*.xml;*.ots;*.zip;*.rar;*.7z|Все файлы (*.*)|*.*";

    /// <summary>
    /// Папки обходятся рекурсивно; файл, указанный явно, берётся даже с незнакомым расширением
    /// (формат всё равно определяется по содержимому). Несуществующие пути попадают в <paramref name="messages"/>.
    /// </summary>
    public static List<ImportUnit> Expand(IEnumerable<string> paths, List<string> messages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var units = new List<ImportUnit>();
        foreach (var raw in paths)
        {
            var path = raw.Trim().Trim('"');
            if (path.Length == 0) continue;
            if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    messages.Add($"{path}: {ex.Message}");
                    continue;
                }
                foreach (var f in files)
                    if (Classify(f) is var k and not ImportKind.None && seen.Add(f)) units.Add(new ImportUnit(f, k));
            }
            else if (File.Exists(path))
            {
                var k = Classify(path);
                if (seen.Add(path)) units.Add(new ImportUnit(path, k == ImportKind.None ? ImportKind.History : k));
            }
            else messages.Add($"Не найдено: {path}");
        }
        return units;
    }

    /// <summary>
    /// Файлы архива по одному, в порядке хранения. Вложенные архивы раскрываются на месте.
    /// <paramref name="grow"/> получает число найденных файлов — чтобы прогресс знал, сколько всего.
    /// Повреждённый, запароленный или многотомный архив не роняет импорт: причина уходит в <paramref name="messages"/>.
    /// </summary>
    public static IEnumerable<ArchiveEntry> ReadArchive(string archivePath, List<string> messages, Action<int>? grow = null)
    {
        FileStream fs;
        try { fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add($"{System.IO.Path.GetFileName(archivePath)}: {ex.Message}");
            yield break;
        }
        using (fs)
            foreach (var e in ReadArchive(fs, archivePath, 0, messages, grow)) yield return e;
    }

    private static IEnumerable<ArchiveEntry> ReadArchive(Stream stream, string name, int depth, List<string> messages, Action<int>? grow)
    {
        string shortName = System.IO.Path.GetFileName(name);
        IArchive archive;
        IEnumerator<ArchiveItem> items;
        try
        {
            archive = ArchiveFactory.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
            grow?.Invoke(archive.Entries.Count(e => !e.IsDirectory && e.Key != null && Classify(e.Key) != ImportKind.None));
            items = Items(archive).GetEnumerator();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            messages.Add($"Архив {shortName} не открыт: {Reason(ex)}");
            yield break;
        }

        using (archive)
        using (items)
        {
            while (true)
            {
                ArchiveEntry? entry = null;
                bool stop = false;
                try
                {
                    if (!items.MoveNext()) break;
                    var e = items.Current;
                    if (e.IsDirectory || e.Key == null) continue;
                    var kind = Classify(e.Key);
                    if (kind == ImportKind.None) continue;
                    if (e.Size > MaxEntryBytes)
                    {
                        messages.Add($"Пропущен слишком большой файл в архиве {shortName}: {e.Key}");
                        continue;
                    }
                    if (kind == ImportKind.Archive && depth + 1 >= MaxDepth)
                    {
                        messages.Add($"Пропущен архив слишком глубокой вложенности: {e.Key}");
                        continue;
                    }
                    using var ms = new MemoryStream(e.Size > 0 ? (int)Math.Min(e.Size, int.MaxValue / 2) : 0);
                    e.WriteTo(ms);
                    entry = new ArchiveEntry(name + "!/" + e.Key.Replace('\\', '/'), kind, ms.ToArray());
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // После ошибки чтения положение в потоке архива неизвестно — продолжать по нему нельзя.
                    messages.Add($"Архив {shortName} прочитан не полностью: {Reason(ex)}");
                    stop = true;
                }
                if (stop) yield break;
                if (entry == null) continue;
                if (entry.Kind != ImportKind.Archive) { yield return entry; continue; }
                using var inner = new MemoryStream(entry.Bytes, writable: false);
                foreach (var nested in ReadArchive(inner, entry.Name, depth + 1, messages, grow)) yield return nested;
            }
        }
    }

    private readonly record struct ArchiveItem(string? Key, bool IsDirectory, long Size, Action<Stream> WriteTo);

    /// <summary>
    /// Файлы архива одним способом для всех форматов. «Непрерывные» RAR и 7z читаются только подряд
    /// (каждый файл сжат с опорой на предыдущие), остальные — по записям оглавления.
    /// </summary>
    private static IEnumerable<ArchiveItem> Items(IArchive archive)
    {
        if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
                yield return new ArchiveItem(reader.Entry.Key, reader.Entry.IsDirectory, reader.Entry.Size, s => reader.WriteEntryTo(s));
        }
        else
        {
            foreach (var e in archive.Entries)
                yield return new ArchiveItem(e.Key, e.IsDirectory, e.Size, s =>
                {
                    using var es = e.OpenEntryStream();
                    es.CopyTo(s);
                });
        }
    }

    private static string Reason(Exception ex)
    {
        var m = ex.Message;
        if (m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
            return "архив защищён паролем — распакуйте его и укажите папку";
        if (m.Contains("volume", StringComparison.OrdinalIgnoreCase) || m.Contains("multi", StringComparison.OrdinalIgnoreCase))
            return "многотомный архив — распакуйте его и укажите папку";
        return m;
    }
}
