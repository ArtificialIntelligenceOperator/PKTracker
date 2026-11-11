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
    /// <summary>Каталог цветовых меток пользователя (%APPDATA%\PKTracker\markers.json).</summary>
    public PKTracker.Data.Notes.MarkerCatalog Markers { get; set; } = PKTracker.Data.Notes.MarkerCatalog.Load();
    public HudManager Hud { get; }
    /// <summary>Виджет текущей сессии поверх столов; null — выключен.</summary>
    public SessionWidget? Session { get; private set; }
    public ObservableCollection<string> LogLines { get; } = new();
    public string DbStatus { get; private set; } = "не подключено";
    /// <summary>Раздач со старым ContextVersion; > 0 — нужен полный ребилд.</summary>
    public long StaleContextHands { get; private set; }

    /// <summary>Перечитать число раздач старой версии контекста (после импорта и после пересчёта базы).</summary>
    public async Task<long> RefreshStaleContextAsync()
    {
        if (Host is not { } host) return 0;
        return StaleContextHands = await Task.Run(() => host.StaleContextHandsAsync());
    }
    public string WatcherStatus { get; private set; } = "выключен";

    /// <summary>Имя процесса запущенного клиента GG; null — клиент GG не запущен.</summary>
    public string? GgClient { get; private set; }
    private DispatcherTimer? _coexistenceTimer;

    public event Action? StatusChanged;
    public event Action<ImportBatchResult>? HandsImported;

    public AppServices(Dispatcher ui)
    {
        ApplyDisplayOptions(Settings);
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
        StartCoexistenceGuard();
        StartSettingsWatcher();
        _backupTimer?.Dispose();
        _backupTimer = new System.Threading.Timer(_ => _ = AutoBackupAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
        Hud.Enabled = Settings.HudEnabled;
        Hud.Start();
        ApplySessionWidget();
        RaiseStatus();
    }

    /// <summary>Показать или убрать виджет сессии по настройке. Только из UI-потока.</summary>
    public void ApplySessionWidget()
    {
        if (Settings.SessionWidget && Host != null)
        {
            if (Session != null) { _ = Session.RefreshAsync(); return; }
            Session = new SessionWidget(this);
            Session.Dismissed += () =>
            {
                Settings.SessionWidget = false;
                try { Settings.Save(); }
                catch (IOException ex) { Log("App", "Настройки не сохранены: " + ex.Message); }
                ApplySessionWidget();
                RaiseStatus();
            };
            Session.Show();
        }
        else
        {
            Session?.Close();
            Session = null;
        }
    }

    /// <summary>
    /// Игроки часто держат открытыми несколько румов сразу, и GG — один из них, поэтому рядом с клиентом GG трекер
    /// продолжает работать для остальных румов: HUD, авто-импорт и .data-канал не останавливаются. С самой сетью GG
    /// он при этом не взаимодействует никак: оверлея над её столами нет, её окна не читаются, а импорт её раздач,
    /// пока клиент запущен, закрыт (<see cref="FileImporter"/>). От проверки процессов клиентом GG трекер не прячется —
    /// и не должен: по правилу GG от 15.10.2026 любой сторонний покерный софт рядом с клиентом остаётся на риске игрока,
    /// о чём и напоминает строка в журнале при каждом запуске клиента GG.
    /// </summary>
    private void StartCoexistenceGuard()
    {
        if (_coexistenceTimer != null) return;
        _coexistenceTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) =>
        {
            // Перебор процессов — в фоне: он стоит десятки миллисекунд, а таймер тикает в UI-потоке.
            var now = await Task.Run(RoomClientProbe.ForbiddenCoexistence);
            if (now == GgClient) return;
            GgClient = now;
            if (now != null)
                Log("App", $"Запущен клиент GG ({now}). PKTracker продолжает работать для остальных румов; с сетью GG он не взаимодействует " +
                           "(нет HUD над её столами, импорт её раздач закрыт, пока клиент открыт). Учтите: с 01.12.2026 GG запрещает " +
                           "любой сторонний покерный софт рядом со своим клиентом и проверяет запущенные программы — это ваш риск.");
            else Log("App", "Клиент GG закрыт.");
        }, _ui);
        _coexistenceTimer.Start();
    }

    private void StartWatcher()
    {
        if (Host != null && RoomHudPolicy.For(Settings.RoomCode).ImportNeedsOptIn)
        {
            // У GG нет локальной истории, а сторонний импорт её раздач запрещён политикой сети: автоматически
            // их не берём никогда — только ручной импорт после явного включения игроком.
            WatcherStatus = "выключен: правила GG запрещают сторонний импорт раздач (ручной импорт — на вкладке «Импорт», на свой риск)";
            return;
        }
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
                _ = Session?.RefreshAsync();
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
            ApplyDisplayOptions(fresh);
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
        ApplyDisplayOptions(s);
    }

    /// <summary>Вид значений в ячейках HUD — общий для панели, попапов и редактора.</summary>
    public static void ApplyDisplayOptions(TrackerSettings s) =>
        PKTracker.Engine.Hud.CellRender.Options = new PKTracker.Engine.Hud.CellRenderOptions
        {
            SampleSubscriptBelow = s.SampleSubscriptBelow, Cap100To99 = s.Cap100To99,
        };

    public static string DefaultBackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PKTracker", "backups");
    /// <summary>Папка бэкапов: из настроек, иначе «Документы». Системный диск бывает тесным — путь должен переноситься.</summary>
    public string BackupFolder => string.IsNullOrWhiteSpace(Settings.BackupFolder) ? DefaultBackupFolder : Settings.BackupFolder!;

    /// <summary>
    /// Бэкапы активной базы, новые первыми. Имя файла начинается с имени базы: баз может быть несколько,
    /// и чистка старых копий одной не должна задевать копии другой («pktracker» и «pktracker_test»).
    /// </summary>
    private List<FileInfo> BackupFiles()
    {
        if (!Directory.Exists(BackupFolder)) return new List<FileInfo>();
        var db = System.Text.RegularExpressions.Regex.Escape(DatabaseCatalog.Current(Settings.ConnectionString));
        var rule = new System.Text.RegularExpressions.Regex($@"^{db}_\d{{8}}_\d{{6}}\.dump$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new DirectoryInfo(BackupFolder).GetFiles("*.dump").Where(f => rule.IsMatch(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
    }

    private int _backupRunning;

    /// <summary>Проверяется раз в час: если последний бэкап старше AutoBackupDays — делаем новый и удаляем лишние.</summary>
    private async Task AutoBackupAsync()
    {
        if (Settings.AutoBackupDays <= 0 || Host == null) return;
        if (Interlocked.Exchange(ref _backupRunning, 1) == 1) return;
        try
        {
            var files = BackupFiles();
            if (files.Count > 0 && DateTime.UtcNow - files[0].LastWriteTimeUtc < TimeSpan.FromDays(Settings.AutoBackupDays)) return;
            await BackupAsync();
            files = BackupFiles();
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
        var file = Path.Combine(dir, $"{b.Database}_{DateTime.Now:yyyyMMdd_HHmmss}.dump");
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
        _coexistenceTimer?.Stop();
        Hud.Dispose();
        await StopBackgroundAsync();
    }
}
