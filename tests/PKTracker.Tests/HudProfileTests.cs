using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.Tests;

/// <summary>
/// Встроенные профили — данные, а не код, и ошибка в них тихая: ячейка просто показывает прочерк.
/// Поэтому здесь проверяется ровно то, что молча ломается: опечатка в коде стата, неразбираемый
/// фильтр, налезающие друг на друга ячейки.
/// </summary>
public class HudProfileTests
{
    private static readonly Dictionary<string, CompiledStat> Stats = TestUtil.Stats.ToDictionary(s => s.Code);

    public static TheoryData<string> Profiles()
    {
        var d = new TheoryData<string>();
        foreach (var p in DefaultProfiles.All) d.Add(p.Name);
        return d;
    }

    private static HudProfile ByName(string name) => DefaultProfiles.All.First(p => p.Name == name);

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryCellReferencesKnownStat(string profileName)
    {
        var missing = ByName(profileName).StatCodes().Where(c => !Stats.ContainsKey(c)).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryFilterCompiles(string profileName)
    {
        foreach (var page in ByName(profileName).Pages.Prepend(ByName(profileName).Panel))
            foreach (var cell in page.Cells)
                if (cell.Filter is { IsEmpty: false } f)
                {
                    var compiled = new CompiledFilter(f);
                    Assert.NotNull(compiled);
                }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void CellsDoNotOverlap(string profileName)
    {
        var profile = ByName(profileName);
        foreach (var page in profile.Pages.Prepend(profile.Panel))
        {
            var taken = new Dictionary<(int, int), HudCell>();
            foreach (var cell in page.Cells)
                for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++)
                    for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++)
                    {
                        // Пустая подпись-заглушка перекрытием не считается: ею разделяют блоки.
                        if (taken.TryGetValue((r, c), out var other) && !IsFiller(other) && !IsFiller(cell))
                            Assert.Fail($"{profile.Name}/{page.Name}: ячейки налезают в ({r},{c}): "
                                + $"'{other.Text ?? other.Stat}' и '{cell.Text ?? cell.Stat}'");
                        taken[(r, c)] = cell;
                    }
        }
    }

    private static bool IsFiller(HudCell c) => c.IsLabel && string.IsNullOrEmpty(c.Text);

    [Theory]
    [MemberData(nameof(Profiles))]
    public void PagesHaveUniqueNames(string profileName)
    {
        var names = ByName(profileName).Pages.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PanelPopupTargetsExist()
    {
        foreach (var profile in DefaultProfiles.All)
            foreach (var cell in profile.Panel.Cells)
                if (cell.Popup is { } target)
                    Assert.True(profile.Page(target) != null, $"{profile.Name}: панель ссылается на вкладку '{target}', которой нет");
    }

    [Fact]
    public void ProfileSurvivesJsonRoundTrip()
    {
        var restored = HudProfile.FromJson(DefaultProfiles.Mtt.ToJson());
        Assert.Equal(DefaultProfiles.Mtt.Pages.Count, restored.Pages.Count);
        Assert.Equal(DefaultProfiles.Mtt.Panel.Cells.Count, restored.Panel.Cells.Count);
        var a = DefaultProfiles.Mtt.Pages[0].Cells.First(c => c.Filter is { IsEmpty: false });
        var b = restored.Pages[0].Cells[DefaultProfiles.Mtt.Pages[0].Cells.IndexOf(a)];
        Assert.Equal(a.Filter!.ToString(), b.Filter!.ToString());
    }

    /// <summary>
    /// Главная ловушка раскладок: ячейка с недостижимым условием. Считаем страницу на настоящих
    /// раздачах и убеждаемся, что знаменатель вообще набирается хотя бы у части ячеек.
    /// </summary>
    [Fact]
    public void GeneralPageProducesCasesOnSampleHands()
    {
        var ctx = TestUtil.Context(SampleHands.OpenThreeBetFold);
        var rows = new PlayerRows { Decisions = ctx.Decisions, Facts = ctx.Players };
        var page = DefaultProfiles.Mtt.Page("GENERAL")!;
        var values = new PageEvaluator(Stats).Evaluate(page, rows);
        Assert.Contains(values, v => v.Cases > 0);
        Assert.Equal(page.Cells.Count, values.Length);
    }

    /// <summary>Фильтр по позиции обязан менять выборку, иначе он не применяется.</summary>
    [Fact]
    public void PositionFilterNarrowsSample()
    {
        var ctx = TestUtil.Context(SampleHands.OpenThreeBetFold);
        var rows = new PlayerRows { Decisions = ctx.Decisions, Facts = ctx.Players };
        var all = Page("rfi", null);
        var btn = Page("rfi", PageBuilder.F(pos: P.Btn));
        var ev = new PageEvaluator(Stats);
        long total = ev.Evaluate(all, rows)[0].Cases;
        long onBtn = ev.Evaluate(btn, rows)[0].Cases;
        Assert.True(total > 0, "стат rfi не набрал выборки на тестовой раздаче");
        Assert.True(onBtn <= total);
    }

    private static HudPage Page(string code, CellFilter? filter) => new()
    {
        Name = "t", Cells = { new HudCell { Row = 0, Col = 0, Stat = code, Filter = filter } },
    };

    /// <summary>
    /// Старый плоский список кодов должен давать рабочую панель: на него переключается значок
    /// выбора раскладки, и если бы он превращался в пустую страницу, HUD просто исчезал бы.
    /// </summary>
    [Fact]
    public void LegacyCodeListBecomesPanel()
    {
        var profile = HudProfile.FromCodes("vpip,pfr,|,three_bet,fold_to_3bet", "cash");
        Assert.Empty(profile.Pages);
        Assert.Equal(4, profile.Panel.Cells.Count);
        Assert.All(profile.Panel.Cells, c => Assert.NotNull(c.Stat));
        // «|» — перенос строки, а не ячейка.
        Assert.Equal(2, profile.Panel.Rows);
        Assert.Equal(new[] { "vpip", "pfr", "three_bet", "fold_to_3bet" }, profile.Panel.Cells.Select(c => c.Stat));
        Assert.Equal(new[] { 0, 0, 1, 1 }, profile.Panel.Cells.Select(c => c.Row));
        Assert.Equal(new[] { 0, 1, 0, 1 }, profile.Panel.Cells.Select(c => c.Col));
    }

    [Fact]
    public void LegacyListIgnoresBlanksAndTrailingSeparator()
    {
        var profile = HudProfile.FromCodes(" vpip , , pfr ,|", "mtt");
        Assert.Equal(new[] { "vpip", "pfr" }, profile.Panel.Cells.Select(c => c.Stat));
    }

    /// <summary>Линия игрока читается с той улицы, на которой принимается решение.</summary>
    [Theory]
    [InlineData(0, Street.River, "")]
    [InlineData(2 | (1 << 3), Street.River, "BX")]
    [InlineData(2 | (1 << 3), Street.Turn, "B")]
    [InlineData(1 | (3 << 3), Street.River, "XC")]
    [InlineData(2, Street.Flop, "")]
    public void LineStringDecodesBits(int bits, Street street, string expected) =>
        Assert.Equal(expected, DecisionPoint.LineString((short)bits, street));
}
