using System.Text.Json;
using PKTracker.Core.Model;
using PKTracker.Engine.Hud;

namespace PKTracker.Data;

/// <summary>
/// Профили HUD на диске: %APPDATA%\PKTracker\hud\&lt;имя&gt;.json.
///
/// Файлами, а не таблицей в базе: профиль — это конфигурация, а не данные. Его правят, возят
/// между машинами, откатывают из бэкапа и держат в системе контроля версий, и ради всего этого
/// не нужна работающая PostgreSQL.
/// </summary>
public sealed class HudProfileStore
{
    public static string Folder => Path.Combine(TrackerSettings.Folder, "hud");

    private static string PathFor(string name) => Path.Combine(Folder, Sanitize(name) + ".json");

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<string>();
        return Directory.EnumerateFiles(Folder, "*.json").Select(System.IO.Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).OrderBy(n => n).ToList();
    }

    public HudProfile? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try { return HudProfile.FromJson(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    public void Save(HudProfile profile)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathFor(profile.Name), profile.ToJson());
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Профиль для вида игры: сохранённый пользователем либо встроенный. Встроенный никогда не пишется
    /// на диск сам — пока пользователь не сохранил свой, он всегда актуален после обновления программы.
    /// </summary>
    public HudProfile Resolve(string? name, GameKind kind)
    {
        if (!string.IsNullOrWhiteSpace(name) && Load(name) is { } saved) return saved;
        return kind == GameKind.Cash ? DefaultProfiles.Cash : DefaultProfiles.Mtt;
    }
}
