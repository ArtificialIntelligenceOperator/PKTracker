using PKTracker.Engine.Hud;

namespace PKTracker.Data;

public sealed record HudPackageResult(string ProfileName, int Stats, IReadOnlyList<string> ChangedStats, int RebuiltStats);

/// <summary>
/// Импорт пакета HUD (профиль + статы одним файлом): статы — в базу, профиль — в папку раскладок
/// пользователя. После импорта досчитываются агрегаты новых статов, иначе панель была бы пустой.
/// Сам пакет — личный материал и в репозиторий не кладётся.
/// </summary>
public sealed class HudPackageService(TrackerHost host)
{
    public async Task<HudPackageResult> ImportAsync(string path, IProgress<RebuildProgress>? progress = null, CancellationToken ct = default)
    {
        var pkg = HudPackage.FromJson(await File.ReadAllTextAsync(path, ct));
        var changed = await host.StatsRepo.SaveManyAsync(pkg.Stats, ct);
        new HudProfileStore().Save(pkg.Profile);
        await host.ReloadAsync(ct);
        int rebuilt = await host.CatchUpStaleStatsAsync(progress, ct);
        return new HudPackageResult(pkg.Profile.Name, pkg.Stats.Count, changed, rebuilt);
    }
}
