namespace PKTracker.Engine.Hud;

/// <summary>
/// Встроенные профили HUD. Собираются в коде и не пишутся на диск: пока пользователь не сохранил
/// свой профиль в редакторе, он получает актуальный набор после каждого обновления программы.
/// Редактор всегда правит копию — «Сохранить как» создаёт файл в %APPDATA%\PKTracker\hud.
/// </summary>
public static partial class DefaultProfiles
{
    private static HudProfile? _mtt;
    private static HudProfile? _cash;

    /// <summary>Турнирный профиль: 16 вкладок со всеми разрезами, которые нужны в турнирах.</summary>
    public static HudProfile Mtt => _mtt ??= new HudProfile
    {
        Name = "MTT",
        GameKind = "mtt",
        Panel = BuildPanel(mtt: true),
        Pages = new List<HudPage>
        {
            BuildGeneral(),
            BuildR3(),
            BuildThreeBet(),
            BuildDynamic(),
            BuildSqueeze(),
            BuildRaiser(ip: true),
            BuildRaiser(ip: false),
            BuildCaller(ip: true),
            BuildCaller(ip: false),
            BuildHeadsUp(),
            BuildLit(),
            BuildSteals(),
            BuildStealsShort(),
            BuildOpHu(),
            BuildThreeMax(),
        },
    };

    /// <summary>
    /// Кеш-профиль: те же вкладки, но без пуш-фолдовых. В кеше стек почти всегда около 100bb,
    /// и разрезы по полосам стека там показывают одно и то же число в пяти местах.
    /// </summary>
    public static HudProfile Cash => _cash ??= new HudProfile
    {
        Name = "Cash",
        GameKind = "cash",
        Panel = BuildPanel(mtt: false),
        Pages = new List<HudPage>
        {
            BuildGeneral(),
            BuildThreeBet(),
            BuildSqueeze(),
            BuildRaiser(ip: true),
            BuildRaiser(ip: false),
            BuildCaller(ip: true),
            BuildCaller(ip: false),
            BuildHeadsUp(mtt: false),
            BuildLit(),
            BuildSteals(mtt: false),
        },
    };

    public static IReadOnlyList<HudProfile> All => new[] { Mtt, Cash };
}
