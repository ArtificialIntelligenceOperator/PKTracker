using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using PKTracker.App.Hud;
using PKTracker.Data;
using PKTracker.Data.Import;
using PKTracker.Live;

namespace PKTracker.App;

/// <summary>Оркестрация фоновых подсистем единого процесса (§1.1, §10.3).</summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly Dispatcher _ui;
    private HandHistoryWatcher? _watcher;
    private PokerKingDataFeed? _feed;
    private System.Threading.Timer? _backupTimer;
    private FileSystemWatcher? _settingsWatcher;
    private System.Threading.Timer? _reloadDebounce;

    public TrackerSettings Settings { get; private set; } = TrackerSettings.Load();
    public TrackerHost? Host { get; private set; }
    public HudManager Hud { get; }
    public ObservableCollection<string> LogLines { get; } = new();
    public string DbStatus { get; private set; } = "не подключено";
    /// <summary>Раздач со старым ContextVersion; > 0 — нужен полный ребилд.</summary>
    public long StaleContextHands { get; private set; }
    public string WatcherStatus { get; private set; } = "выключен";

    public event Action? StatusChanged;
    public event Action<ImportBatchResult>? HandsImported;

    public AppServices(Dispatcher ui)
    {
        _ui = ui;
        Hud = new HudManager(this, ui);
    }

    public static string LogFolder => Path.Combine(TrackerSettings.Folder, "logs");

    public void Log(string subsystem, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{subsystem}] {message}";
        try
        {
            Directory.CreateDirectory(LogFolder);
            File.AppendAllText(Path.Combine(LogFolder, $"{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch (IOException) { }
        _ui.InvokeAsync(() =>
        {
            LogLines.Insert(0, line);
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
        });
    }

    /// <summary>Подключение к БД, миграции, запуск авто-импорта, .data и HUD. Безопасно вызывать повторно после смены настроек.</summary>
    public async Task StartAsync()
    {
        await StopBackgroundAsync();
        var pg = await Task.Run(() => LocalPostgres.EnsureRunning());
        if (pg != null) Log("Db", pg);
        try
        {
            var cs = Settings.ConnectionString;
            var room = Settings.RoomCode;
            Host = await Task.Run(() => TrackerHost.StartAsync(cs, room));
            DbStatus = $"{room}: подключено, схема v{Migrator.LatestVersion}, статов: {Host.Stats.Count}, Hero: {string.Join(", ", Host.HeroNames)}";
            Log("Db", DbStatus);
            var host = Host;
            // Порядок важен: сначала предупреждение о старом контексте, потом досчёт статов.
            // Иначе новые статы тихо посчитаются по пустым колонкам, и число в HUD будет
            // выглядеть достоверным (§15.6).
            long staleCtx = await Task.Run(() => host.StaleContextHandsAsync());
            StaleContextHands = staleCtx;
            if (staleCtx > 0)
                Log("Db", $"ВНИМАНИЕ: {staleCtx} раздач разобрано старой версией контекста. "
                    + "Статы про позицию оппонента, линии и размеры ставок будут неверны, "
                    + "пока не выполнен полный ребилд: PKTracker.Cli rebuild --full");
            int stale = await Task.Run(() => host.CatchUpStaleStatsAsync());
            if (stale > 0) Log("Db", $"Досчитаны устаревшие статы: {stale}");
        }
        catch (Exception ex)
        {
            Host = null;
            DbStatus = "ошибка: " + ex.Message;
            Log("Db", DbStatus);
            RaiseStatus();
            return;
        }

        StartWatcher();
        StartFeed();
        StartSettingsWatcher();
        _backupTimer?.Dispose();
        _backupTimer = new System.Threading.Timer(_ => _ = AutoBackupAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
        Hud.Enabled = Settings.HudEnabled;
        Hud.Start();
        RaiseStatus();
    }

    private void StartWatcher()
    {
        if (Host == null || !Settings.AutoImport || string.IsNullOrWhiteSpace(Settings.HandHistoryFolder) || !Directory.Exists(Settings.HandHistoryFolder))
        {
            WatcherStatus = Settings.AutoImport ? "папка HH не задана или не существует" : "выключен";
            return;
        }
        if (Host.HeroNames.Count == 0)
        {
            WatcherStatus = "не задан Hero — импорт запрещён (правила рума)";
            return;
        }
        _watcher = Host.CreateWatcher(Settings.HandHistoryFolder);
        _watcher.HandsImported += e =>
        {
            Log("Import", $"+{e.Result.Imported} рук ({Path.GetFileName(e.File)})");
            _ui.InvokeAsync(() =>
            {
                Hud.OnHandsImported(e.Result.AffectedPlayers);
                HandsImported?.Invoke(e.Result);
            });
        };
        _watcher.Error += (f, ex) => Log("Import", $"{Path.GetFileName(f)}: {ex.Message}");
        _watcher.Start();
        WatcherStatus = "дозор: " + Settings.HandHistoryFolder;
        Log("Import", WatcherStatus);
    }

    private void StartFeed()
    {
        var folder = Settings.LiveDataFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        _feed = new PokerKingDataFeed(folder, Settings.LiveSeatOffset);
        _feed.SeatingChanged += Hud.OnLiveSeating;
        _feed.Start();
        Log("Live", ".data-канал: " + folder);
    }

    /// <summary>
    /// Агент (MCP-сервер) меняет статы и профиль HUD в отдельном процессе. Он трогает settings.json и пишет reload.signal —
    /// приложение подхватывает изменения без перезапуска.
    /// </summary>
    private void StartSettingsWatcher()
    {
        _settingsWatcher?.Dispose();
        try
        {
            Directory.CreateDirectory(TrackerSettings.Folder);
            _settingsWatcher = new FileSystemWatcher(TrackerSettings.Folder)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler handler = (_, e) =>
            {
                var name = Path.GetFileName(e.FullPath);
                if (name is not ("settings.json" or "reload.signal")) return;
                // Файл могут дописывать — небольшая задержка и склейка частых событий
                _reloadDebounce?.Dispose();
                _reloadDebounce = new System.Threading.Timer(_ => _ = ReloadFromDiskAsync(name), null, 400, System.Threading.Timeout.Infinite);
            };
            _settingsWatcher.Changed += handler;
            _settingsWatcher.Created += handler;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Log("App", "Слежение за настройками не запущено: " + ex.Message);
        }
    }

    private async Task ReloadFromDiskAsync(string trigger)
    {
        try
        {
            var fresh = TrackerSettings.Load();
            bool hudChanged = fresh.CashStats != Settings.CashStats || fresh.MttStats != Settings.MttStats
                              || fresh.PopupTrigger != Settings.PopupTrigger || Math.Abs(fresh.PanelOpacity - Settings.PanelOpacity) > 0.001;
            Settings = fresh;
            if (Host != null && trigger == "reload.signal")
            {
                await Host.ReloadAsync();
                Host.Hud.InvalidateAll();
            }
            await _ui.InvokeAsync(() =>
            {
                if (hudChanged || trigger == "reload.signal") Hud.ReloadProfile();
                StatusChanged?.Invoke();
            });
            Log("App", $"Настройки перечитаны ({trigger})");
        }
        catch (Exception ex)
        {
            Log("App", "Не удалось перечитать настройки: " + ex.Message);
        }
    }

    private async Task StopBackgroundAsync()
    {
        if (_watcher != null) { await _watcher.DisposeAsync(); _watcher = null; }
        _feed?.Dispose();
        _feed = null;
        if (Host != null) { await Host.DisposeAsync(); Host = null; }
    }

    public void SaveSettings(TrackerSettings s)
    {
        Settings = s;
        s.Save();
    }

    public static string BackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PKTracker", "backups");

    private int _backupRunning;

    /// <summary>Проверяется раз в час: если последний бэкап старше AutoBackupDays — делаем новый и удаляем лишние.</summary>
    private async Task AutoBackupAsync()
    {
        if (Settings.AutoBackupDays <= 0 || Host == null) return;
        if (Interlocked.Exchange(ref _backupRunning, 1) == 1) return;
        try
        {
            var files = Directory.Exists(BackupFolder)
                ? new DirectoryInfo(BackupFolder).GetFiles("pktracker_*.dump").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
                : new List<FileInfo>();
            if (files.Count > 0 && DateTime.UtcNow - files[0].LastWriteTimeUtc < TimeSpan.FromDays(Settings.AutoBackupDays)) return;
            await BackupAsync();
            files = new DirectoryInfo(BackupFolder).GetFiles("pktracker_*.dump").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in files.Skip(Math.Max(1, Settings.KeepBackups)))
            {
                old.Delete();
                Log("Db", "Удалён старый бэкап: " + old.Name);
            }
        }
        catch (Exception ex)
        {
            Log("Db", "Автобэкап не удался: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _backupRunning, 0);
        }
    }

    /// <summary>Бэкап pg_dump (§13.11). Потеря базы = потеря месяцев статистики.</summary>
    public async Task<string> BackupAsync()
    {
        var pgDump = Path.Combine(LocalPostgres.BinFolder, "pg_dump.exe");
        if (!File.Exists(pgDump)) pgDump = "pg_dump.exe";
        var b = new Npgsql.NpgsqlConnectionStringBuilder(Settings.ConnectionString);
        var dir = BackupFolder;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"pktracker_{DateTime.Now:yyyyMMdd_HHmmss}.dump");
        var psi = new ProcessStartInfo(pgDump, $"-h {b.Host} -p {b.Port} -U {b.Username} -Fc -f \"{file}\" {b.Database}")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true,
        };
        psi.Environment["PGPASSWORD"] = b.Password ?? "";
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException(err);
        Log("Db", "Бэкап: " + file);
        return file;
    }

    private void RaiseStatus() => _ui.InvokeAsync(() => StatusChanged?.Invoke());

    public async ValueTask DisposeAsync()
    {
        _backupTimer?.Dispose();
        Hud.Dispose();
        await StopBackgroundAsync();
    }
}
