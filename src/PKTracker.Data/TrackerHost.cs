using System.Text.Json;
using PKTracker.Data.Import;
using PKTracker.Engine.Stats;

namespace PKTracker.Data;

/// <summary>Чем открывается попап стата или вкладки.</summary>
public enum PopupTrigger
{
    /// <summary>Навести и подождать; уход курсора закрывает.</summary>
    Hover,
    /// <summary>Только клик; закрывается вручную.</summary>
    Click,
}

/// <summary>Настройки приложения: %APPDATA%\PKTracker\settings.json.</summary>
/// <summary>
/// Что делает трекер, пока запущен клиент сети GG. GG с 01.12.2026 запрещает сторонний покерный софт рядом со своим
/// клиентом — программы, которые «обращаются к игровым данным, взаимодействуют со столами или автоматизируют действия»,
/// включая HUD, — и проверяет запущенные процессы. Играют при этом на нескольких румах сразу, поэтому выбор за игроком.
/// </summary>
public enum GgCoexistence
{
    /// <summary>Работать как обычно: HUD и всё остальное для других румов продолжает работать — на риск игрока.</summary>
    Continue,
    /// <summary>
    /// Выключить всё, что GG запрещает: оверлей и попапы, слежение за окнами столов, .data-канал, виджет сессии.
    /// Остаётся только чтение файлов истории других румов с диска, чтобы статистика не отставала. Клиент GG закрыли —
    /// всё включается обратно.
    /// </summary>
    Passive,
    /// <summary>Закрыть PKTracker (и его MCP-сервер): рядом с клиентом GG не остаётся ни одного процесса трекера.</summary>
    Exit,
}

public sealed class TrackerSettings
{
    public string ConnectionString { get; set; } = "Host=localhost;Port=5433;Username=pktracker;Password=pktracker;Database=pktracker";
    /// <summary>Рум сети WPN: POKERKING, ACR, BLACKCHIP, TRUEPOKER — формат HH у всех скинов общий.</summary>
    public string RoomCode { get; set; } = "POKERKING";
    /// <summary>Папка HH клиента (E:\PokerKing\handHistory\&lt;ник&gt;).</summary>
    public string? HandHistoryFolder { get; set; }
    /// <summary>Папка .data-файлов клиента (E:\PokerKing\handHistory).</summary>
    public string? LiveDataFolder { get; set; }
    public string? TournamentSummaryFolder { get; set; }
    public bool AutoImport { get; set; } = true;
    public bool HudEnabled { get; set; } = true;
    /// <summary>Имя процесса клиента для поиска окон столов (без .exe).</summary>
    public string ClientProcessName { get; set; } = "PokerKing";
    /// <summary>Класс окна стола (Electron: Chrome_WidgetWin_1) — результат спайка Фазы 0, настраивается.</summary>
    public string TableWindowClass { get; set; } = "Chrome_WidgetWin_1";
    /// <summary>Сдвиг нумерации мест .data → HH (§0.4, гипотеза: +1).</summary>
    public int LiveSeatOffset { get; set; } = 1;
    /// <summary>Имя профиля HUD из %APPDATA%\PKTracker\hud; пусто — встроенный.</summary>
    public string? CashProfile { get; set; }
    public string? MttProfile { get; set; }
    /// <summary>Показывать панели HUD в реплеере.</summary>
    public bool ReplayerHud { get; set; } = true;
    /// <summary>Реплеер показывает стеки, ставки и банк в больших блайндах, а не в фишках (переключатель «BB» в реплеере).</summary>
    public bool ReplayerInBb { get; set; }
    /// <summary>
    /// Ручной импорт принимает и раздачи без вас за столом (наблюдаемые, купленные). Выключено по умолчанию:
    /// правила большинства румов это запрещают, решение и риск — на игроке.
    /// </summary>
    public bool ImportObservedHands { get; set; }
    /// <summary>
    /// Ручной импорт принимает раздачи сети GG (GGPoker, PokerOK). Выключено по умолчанию: политика GG запрещает
    /// сторонним программам импортировать и конвертировать её истории раздач, в том числе выгруженные из PokerCraft.
    /// Решение и риск — на игроке; при запущенном клиенте GG импорт не идёт и с этим флажком.
    /// </summary>
    public bool ImportGgHands { get; set; }
    /// <summary>
    /// Режим совместной игры с GG: что делать, пока запущен её клиент. По умолчанию трекер продолжает работать
    /// для других румов; «Passive» и «Exit» — для тех, кто хочет соблюдать правило GG о стороннем софте.
    /// </summary>
    public GgCoexistence WhenGgRunning { get; set; } = GgCoexistence.Continue;
    /// <summary>Раскладка, выбранная значком в реплеере: null — встроенная, особое значение — скрыт.</summary>
    public string? ReplayerProfile { get; set; }
    /// <summary>
    /// Сдвиг панели HUD в реплеере правой кнопкой мыши, DIP; ключ — «мест за столом:место».
    /// По месту, а не по игроку: панель двигают, когда она лезет на карты, и это свойство стола.
    /// </summary>
    public Dictionary<string, double[]> ReplayerPanelOffsets { get; set; } = new();
    /// <summary>Положение значка выбора раскладки в реплеере, DIP от левого верхнего угла холста.</summary>
    public double ReplayerBadgeX { get; set; } = 8;
    public double ReplayerBadgeY { get; set; } = 8;
    /// <summary>Коды статов панели HUD по порядку; "|" — перенос строки. Используется, только пока нет профиля.</summary>
    public string CashStats { get; set; } = "vpip,pfr,agg_pct,hands,|,three_bet,fold_to_3bet,four_bet,fold_to_4bet,|,cbet_flop,fold_to_cbet_flop,wtsd,cold_call,limp";
    public string MttStats { get; set; } = "vpip,pfr,agg_pct,hands,|,three_bet,fold_to_3bet,steal,fold_bb_vs_steal,|,open_shove,call_shove,fold_to_shove,cbet_flop";
    /// <summary>Глобальная горячая клавиша показа/скрытия HUD.</summary>
    public string HudHotkey { get; set; } = "Ctrl+Alt+H";
    public double PanelOpacity { get; set; } = 0.85;
    /// <summary>Стол считается активным, если по нему были раздачи за это число часов.</summary>
    public double HudActiveTableHours { get; set; } = 3;
    /// <summary>
    /// Чем открывается попап. По наведению он и закрывается сам, когда курсор ушёл; по клику —
    /// висит, пока его не закроют. Закреплённый булавкой попап не закрывается ни в одном режиме.
    /// </summary>
    public PopupTrigger PopupTrigger { get; set; } = PopupTrigger.Hover;
    /// <summary>Сколько курсор должен постоять на стате, прежде чем откроется попап.</summary>
    public int HoverDelayMs { get; set; } = 500;
    /// <summary>Через сколько попап закрывается после ухода курсора со стата и с самого попапа.</summary>
    public int HoverHideMs { get; set; } = 400;
    public int SessionGapMinutes { get; set; } = 30;
    /// <summary>Виджет текущей сессии поверх столов: раздачи, итог в bb, график. Только по завершённым раздачам.</summary>
    public bool SessionWidget { get; set; }
    /// <summary>Положение виджета сессии на экране (левый верхний угол), если его двигали.</summary>
    public double[]? SessionWidgetPos { get; set; }
    /// <summary>
    /// Подбирать парную раскладку по размеру стола: выбрана «…9max…» — на столах до 6 мест берётся «…6max…»
    /// с тем же именем (и наоборот), если такая сохранена. Размер стола за раздачу не меняется, так что HUD остаётся статичным.
    /// </summary>
    public bool AutoProfileBySeats { get; set; } = true;
    /// <summary>Выборку меньше этого числа писать мелкой цифрой у значения (как в Hand2Note); 0 — не писать.</summary>
    public int SampleSubscriptBelow { get; set; } = 10;
    /// <summary>Показывать 99 вместо 100 — трёхзначное число не влезает в узкую ячейку.</summary>
    public bool Cap100To99 { get; set; } = true;
    /// <summary>Автобэкап pg_dump раз в N дней (0 — выключен) и сколько последних копий хранить (§13.11).</summary>
    public int AutoBackupDays { get; set; } = 1;
    /// <summary>MCP: заменять ники оппонентов псевдонимами в ответах агенту (данные уходят в облако модели).</summary>
    public bool McpAnonymizeOpponents { get; set; } = true;
    /// <summary>MCP: разрешить агенту изменения (статы, профиль HUD, импорт). Только чтение — false.</summary>
    public bool McpAllowWrites { get; set; } = true;
    public int KeepBackups { get; set; } = 7;
    /// <summary>
    /// Папка автобэкапов базы (pg_dump). Пусто — «Документы\PKTracker\backups». Дамп — десятки мегабайт в день,
    /// поэтому при тесном системном диске папку стоит перенести на другой.
    /// </summary>
    public string? BackupFolder { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PKTracker");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    // Режим попапа пишется словом: settings.json правят руками, и «Hover» понятнее единицы.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Разбор настроек отдельно от файла — чтобы формат можно было проверить тестом.</summary>
    public static TrackerSettings FromJson(string json) => JsonSerializer.Deserialize<TrackerSettings>(json, Json) ?? new TrackerSettings();

    public static TrackerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return FromJson(File.ReadAllText(FilePath));
        }
        catch (JsonException) { }
        return new TrackerSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, ToJson());
    }
}

/// <summary>Сборка сервисов поверх БД: миграции, статы, импорт, HUD-запросы.</summary>
public sealed class TrackerHost : IAsyncDisposable
{
    public TrackerDb Db { get; }
    public StatRepository StatsRepo { get; }
    public HeroRepository Heroes { get; }
    /// <summary>Цветовые метки, подписи и заметки на игроков.</summary>
    public PKTracker.Data.Notes.PlayerNotesRepository Notes { get; }
    public List<CompiledStat> Stats { get; private set; } = new();
    public HudStatsService Hud { get; private set; } = null!;
    public List<string> HeroNames { get; private set; } = new();

    private TrackerHost(string cs)
    {
        Db = new TrackerDb(cs);
        StatsRepo = new StatRepository(Db);
        Heroes = new HeroRepository(Db);
        Notes = new PKTracker.Data.Notes.PlayerNotesRepository(Db);
    }

    public static async Task<TrackerHost> StartAsync(string connectionString, string roomCode = "POKERKING", CancellationToken ct = default)
    {
        var host = new TrackerHost(connectionString);
        await Migrator.MigrateAsync(host.Db.Import, ct);
        await using (var conn = await host.Db.Import.OpenConnectionAsync(ct))
        await using (var cmd = new Npgsql.NpgsqlCommand("SELECT room_id FROM room WHERE code = $1", conn))
        {
            cmd.Parameters.AddWithValue(roomCode.ToUpperInvariant());
            host.Db.RoomId = (short?)await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException($"Неизвестный рум: {roomCode}");
            host.Db.RoomCode = roomCode.ToUpperInvariant();
        }
        await host.StatsRepo.SeedDefaultsAsync(ct);
        await host.ReloadAsync(ct);
        return host;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        Stats = StatCompiler.CompileAll(await StatsRepo.LoadActiveAsync(ct: ct));
        HeroNames = await Heroes.ListAsync(ct: ct);
        if (Hud == null) Hud = new HudStatsService(Db, Stats);
        else Hud.ReplaceStats(Stats);
    }

    public HandImporter CreateHandImporter() => new(Db, Stats, HeroNames);

    /// <param name="allowObserved">Принимать раздачи без Hero за столом (<see cref="HandImporter.AllowObserved"/>).</param>
    /// <param name="allowRestrictedRooms">Принимать раздачи румов, запрещающих сторонний импорт (<see cref="FileImporter.AllowRestrictedRooms"/>).</param>
    public FileImporter CreateFileImporter(bool allowObserved = false, bool allowRestrictedRooms = false) =>
        new(Db, new HandImporter(Db, Stats, HeroNames) { AllowObserved = allowObserved }, (room, ct) => ImporterForRoomAsync(room, allowObserved, ct))
        {
            AllowRestrictedRooms = allowRestrictedRooms,
        };

    /// <summary>
    /// Импортёр для рума, отличного от активного: в выгрузке вперемешку лежат раздачи разных румов,
    /// и каждая должна попасть под свой рум со своими никами Hero.
    /// </summary>
    private async Task<HandImporter?> ImporterForRoomAsync(PKTracker.Parsers.RoomInfo room, bool allowObserved, CancellationToken ct)
    {
        short? roomId = null;
        await using (var conn = await Db.Import.OpenConnectionAsync(ct))
        {
            if (room.Network == "WPN")
            {
                // Скины WPN по тексту раздачи не различить. Рум однозначен, только если ники Hero заведены
                // ровно для одного из них; иначе файлы пропускаются с подсказкой, а не приписываются наугад.
                await using var cmd = new Npgsql.NpgsqlCommand("""
                    SELECT r.room_id FROM room r
                     WHERE r.network = 'WPN' AND EXISTS (SELECT 1 FROM hero_account h WHERE h.room_id = r.room_id)
                    """, conn);
                var ids = new List<short>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ids.Add(r.GetInt16(0));
                if (ids.Count == 1) roomId = ids[0];
            }
            else
            {
                await using var cmd = new Npgsql.NpgsqlCommand("SELECT room_id FROM room WHERE code = $1", conn);
                cmd.Parameters.AddWithValue(room.Code);
                roomId = (short?)await cmd.ExecuteScalarAsync(ct);
            }
        }
        if (roomId == null) return null;
        return new HandImporter(Db, Stats, await Heroes.ListAsync(roomId, ct), roomId) { AllowObserved = allowObserved };
    }

    public HandHistoryWatcher CreateWatcher(string folder) => new(Db, CreateHandImporter(), folder);

    /// <summary>Досчитывает статы, у которых версия определения или watermark отстали (после правки стата / падения).</summary>
    /// <summary>
    /// Раздач со старым контекстом. Ненулевое значение означает, что часть статов сейчас считается
    /// по пустым колонкам: нужен полный ребилд (`rebuild --full` или кнопка в настройках).
    /// </summary>
    public Task<long> StaleContextHandsAsync(CancellationToken ct = default) =>
        StatsRepo.StaleContextHandsAsync(PKTracker.Engine.HandContextBuilder.ContextVersion, ct);

    public async Task<int> CatchUpStaleStatsAsync(IProgress<RebuildProgress>? progress = null, CancellationToken ct = default)
    {
        var stale = await StatsRepo.StaleStatIdsAsync(ct);
        if (stale.Count == 0) return 0;
        await new StatRebuilder(Db).RebuildStatsAsync(Stats.Where(s => stale.Contains(s.StatId)).ToList(), progress, ct);
        Hud.InvalidateAll();
        return stale.Count;
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
