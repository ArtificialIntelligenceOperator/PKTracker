using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKTracker.Core.Model;
using PKTracker.Data;

namespace PKTracker.Mcp;

/// <summary>Параметры запуска: аргументы командной строки и переменные окружения переопределяют settings.json.</summary>
public sealed record McpOptions(string? ConnectionString, string? Room, bool? Anonymize, bool? ReadOnly)
{
    public static McpOptions FromArgs(string[] args)
    {
        string? Get(string name)
        {
            int i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool? Flag(string on, string off) => args.Contains("--" + on) ? true : args.Contains("--" + off) ? false : null;
        return new McpOptions(
            Get("conn") ?? Environment.GetEnvironmentVariable("PKTRACKER_CONNECTION"),
            Get("room") ?? Environment.GetEnvironmentVariable("PKTRACKER_ROOM"),
            Flag("anonymize", "no-anonymize"),
            Flag("read-only", "allow-writes"));
    }
}

/// <summary>Ленивое подключение к базе трекера и общие помощники для инструментов MCP.</summary>
public sealed class TrackerContext : IAsyncDisposable
{
    public const string Instructions = """
        PKTracker — локальный покерный трекер (PostgreSQL) для сети WPN (PokerKing, ACR, Black Chip, True Poker).
        Все данные — раздачи самого пользователя (Hero). Суммы: кеш — в долларах, турниры — в фишках.
        С чего начать: tracker_status → get_overview / find_leaks → search_hands (выражение как в статах) → get_hand.
        Статы — дроби hits/cases над точками решения (DecisionPoint) или фактами руки (HandPlayerFacts); язык условий —
        в ресурсе pktracker://docs/stats. Перед save_stat всегда вызывай test_stat. Для произвольной аналитики — run_sql
        (только чтение); схема — get_database_schema. При включённой анонимизации оппоненты видны как Villain_XXXXXX —
        эти псевдонимы можно передавать в инструменты вместо ников. Анализируй только сыгранные раздачи: советы по
        текущей раздаче в реальном времени (RTA) запрещены правилами румов.
        """;

    private readonly McpOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TrackerHost? _host;

    public TrackerSettings Settings { get; private set; } = TrackerSettings.Load();
    public Anonymizer Anonymizer { get; private set; } = null!;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public TrackerContext(McpOptions options) => _options = options;

    public bool WritesAllowed => _options.ReadOnly is { } ro ? !ro : Settings.McpAllowWrites;

    public async Task<TrackerHost> HostAsync(CancellationToken ct = default)
    {
        if (_host != null) return _host;
        await _gate.WaitAsync(ct);
        try
        {
            if (_host != null) return _host;
            Settings = TrackerSettings.Load();
            // Агент может работать и без запущенного приложения: поднимаем локальный PostgreSQL сами
            var pg = LocalPostgres.EnsureRunning();
            if (pg != null) Console.Error.WriteLine("[pktracker] " + pg);
            var host = await TrackerHost.StartAsync(_options.ConnectionString ?? Settings.ConnectionString, _options.Room ?? Settings.RoomCode, ct);
            Anonymizer = new Anonymizer(_options.Anonymize ?? Settings.McpAnonymizeOpponents, host.HeroNames,
                Path.Combine(TrackerSettings.Folder, "mcp_salt.bin"));
            await Anonymizer.LoadPlayersAsync(host.Db, ct);
            _host = host;
            return host;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>
    /// Ожидаемые ошибки (неверные аргументы, ошибка в выражении, ошибка PostgreSQL) превращаются в McpException —
    /// только её текст SDK передаёт агенту, и тот может исправить запрос. Прочие исключения остаются скрытыми.
    /// </summary>
    public static async Task<string> Guard(Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or JsonException
                                       or PKTracker.Engine.Stats.StatCompileException or Npgsql.PostgresException)
        {
            var message = ex is PKTracker.Engine.Stats.StatCompileException { InnerException: { } inner } ? inner.Message : ex.Message;
            throw new ModelContextProtocol.McpException(message, ex);
        }
    }

    public static GameKind ParseGame(string? game) => game?.Trim().ToLowerInvariant() switch
    {
        null or "" or "cash" => GameKind.Cash,
        "mtt" or "tournament" or "tournaments" or "sng" => GameKind.Mtt,
        _ => throw new ArgumentException($"game: ожидается cash или mtt, получено '{game}'"),
    };

    public void RequireWrites()
    {
        if (!WritesAllowed)
            throw new InvalidOperationException("Сервер запущен только для чтения (--read-only или McpAllowWrites=false в настройках).");
    }

    /// <summary>Сигнал запущенному приложению PKTracker перечитать статы и профиль HUD.</summary>
    public static void SignalAppReload()
    {
        try
        {
            Directory.CreateDirectory(TrackerSettings.Folder);
            File.WriteAllText(Path.Combine(TrackerSettings.Folder, "reload.signal"), DateTime.UtcNow.ToString("O"));
        }
        catch (IOException) { }
    }

    /// <summary>Кеш — доллары с центами, турниры — фишки.</summary>
    public static double Amount(long units) => units / 100.0;

    public static double Bb(long bbX100) => Math.Round(bbX100 / 100.0, 2);

    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
    }
}
