using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;

namespace PKTracker.Tests;

/// <summary>
/// Содержимое попапа стата. Считается оно по раздачам из базы, но арифметика исходов и
/// раскладка диапазонов по действиям — здесь, и ошибиться в них можно молча: попап просто
/// покажет не то число.
/// </summary>
public class StatPopupDataTests
{
    /// <summary>
    /// Исход раздачи учитывается один раз, сколько бы решений в ней ни сработало. Иначе рука,
    /// где стат сработал на флопе и на тёрне, попала бы в профит дважды.
    /// </summary>
    [Fact]
    public void OutcomeOfOneHandCountsOnce()
    {
        var d = new StatPopupData();
        d.AddOutcome(FactBits.SawFlop | FactBits.WonHand, 250);
        d.AddOutcome(FactBits.SawFlop, -100);

        Assert.Equal(2, d.HitHands);
        Assert.Equal(150, d.HitNetBbX100);
        Assert.Equal(0.75, d.ActionProfitBb!.Value, 6);
        Assert.Equal(50, d.WonHandPct!.Value, 6);
    }

    [Fact]
    public void ShowdownMetricsUseTheirOwnDenominators()
    {
        var d = new StatPopupData();
        d.AddOutcome(FactBits.None, 0);                                                        // выиграл префлопом
        d.AddOutcome(FactBits.SawFlop, 0);                                                     // флоп без шоудауна
        d.AddOutcome(FactBits.SawFlop | FactBits.SawShowdown, 0);                              // шоудаун проигран
        d.AddOutcome(FactBits.SawFlop | FactBits.SawShowdown | FactBits.WonAtShowdown, 0);     // шоудаун выигран

        Assert.Equal(4, d.HitHands);
        Assert.Equal(3, d.HitSawFlop);
        // WTSD — от раздач с флопом, а не от всех: рука, выигранная префлопом, до шоудауна дойти не могла.
        Assert.Equal(2 * 100.0 / 3, d.WtsdPct!.Value, 6);
        Assert.Equal(50, d.WsdPct!.Value, 6);
    }

    [Fact]
    public void NoHandsMeansNoNumbers()
    {
        var d = new StatPopupData();
        Assert.Null(d.ActionProfitBb);
        Assert.Null(d.WonHandPct);
        Assert.Null(d.WtsdPct);
        Assert.Null(d.WsdPct);
    }

    /// <summary>Проигранный спот должен давать минус, а не ноль: по этому числу и решают, стоит ли так играть.</summary>
    [Fact]
    public void ProfitKeepsItsSign()
    {
        var d = new StatPopupData();
        d.AddOutcome(FactBits.SawFlop, -1200);
        d.AddOutcome(FactBits.SawFlop, 200);
        Assert.Equal(-5.0, d.ActionProfitBb!.Value, 6);
    }

    /// <summary>Диапазон заводится под действие лениво, но один и тот же массив — чтобы счётчики не терялись.</summary>
    [Fact]
    public void RangeOfReturnsTheSameArrayPerAction()
    {
        var d = new StatPopupData();
        d.RangeOf(Choice.Raise)[0]++;
        d.RangeOf(Choice.Raise)[0]++;
        d.RangeOf(Choice.Call)[0]++;

        Assert.Equal(2, d.RangeOf(Choice.Raise)[0]);
        Assert.Equal(1, d.RangeOf(Choice.Call)[0]);
        Assert.Equal(2, d.RangeByAction.Count);
        Assert.Equal(169, d.RangeOf(Choice.Fold).Length);
    }

    [Fact]
    public void UntouchedActionHasNoRange()
    {
        var d = new StatPopupData();
        d.RangeOf(Choice.Raise)[5]++;
        Assert.False(d.RangeByAction.ContainsKey(Choice.Fold));
    }
}

/// <summary>Настройки попапов: режим по умолчанию и то, что он переживает запись в файл.</summary>
public class PopupSettingsTests
{
    /// <summary>По умолчанию попап открывается наведением через полсекунды — это выбранное поведение.</summary>
    [Fact]
    public void DefaultsToHoverAfterHalfASecond()
    {
        var s = new TrackerSettings();
        Assert.Equal(PopupTrigger.Hover, s.PopupTrigger);
        Assert.Equal(500, s.HoverDelayMs);
    }

    [Theory]
    [InlineData(PopupTrigger.Hover)]
    [InlineData(PopupTrigger.Click)]
    public void TriggerSurvivesJsonRoundTrip(PopupTrigger trigger)
    {
        var json = new TrackerSettings { PopupTrigger = trigger, HoverDelayMs = 700 }.ToJson();
        // Режим пишется словом: settings.json правят руками, и «Click» понятнее единицы.
        Assert.Contains($"\"{trigger}\"", json);
        var back = TrackerSettings.FromJson(json);
        Assert.Equal(trigger, back.PopupTrigger);
        Assert.Equal(700, back.HoverDelayMs);
    }

    /// <summary>Старый settings.json поля не содержит — и должен читаться, а не падать.</summary>
    [Fact]
    public void SettingsWithoutTheFieldFallBackToHover()
    {
        var s = TrackerSettings.FromJson("""{ "HudEnabled": true, "PanelOpacity": 0.5 }""");
        Assert.Equal(PopupTrigger.Hover, s.PopupTrigger);
        Assert.Equal(0.5, s.PanelOpacity);
    }
}

/// <summary>
/// Вложенность попапов. Уровень решает, что закрывать при открытии нового, и именно на нём
/// сломалось: попап стата, открытый из вкладки профиля, закрывал собой эту вкладку.
/// </summary>
public class PopupTargetTests
{
    private static PopupTarget Page(string page = "GENERAL", long player = 1) => new()
    {
        PlayerId = player, PlayerName = "v", Kind = GameKind.Mtt,
        Profile = DefaultProfiles.Mtt, Page = page,
    };

    private static PopupTarget Stat(string code = "vpip", long player = 1) => new()
    {
        PlayerId = player, PlayerName = "v", Kind = GameKind.Mtt, StatCode = code,
    };

    /// <summary>Вкладка профиля — нижний уровень, разбивка стата открывается поверх неё.</summary>
    [Fact]
    public void StatPopupSitsAboveThePagePopup()
    {
        Assert.Equal(0, Page().Level);
        Assert.Equal(1, Stat().Level);
    }

    /// <summary>
    /// Ячейка с привязанной вкладкой открывает вкладку, а не стат, даже если код стата у неё
    /// тоже есть: иначе клик по панели показывал бы не то, что настроено в редакторе.
    /// </summary>
    [Fact]
    public void PageWinsOverStatCode()
    {
        var both = Page() with { StatCode = "vpip" };
        Assert.Equal(0, both.Level);
    }

    [Fact]
    public void SameTargetHasSameKey() => Assert.Equal(Stat().Key, Stat().Key);

    [Theory]
    [InlineData("vpip", 2)]
    [InlineData("pfr", 1)]
    public void KeyDistinguishesStatAndPlayer(string code, long player) =>
        Assert.NotEqual(Stat().Key, Stat(code, player).Key);

    [Fact]
    public void KeyDistinguishesPages() => Assert.NotEqual(Page().Key, Page("3 BET").Key);

    /// <summary>Вкладка и стат не должны совпасть ключами: они живут в разных слотах.</summary>
    [Fact]
    public void PageAndStatNeverShareAKey() => Assert.NotEqual(Page().Key, Stat().Key);
}
