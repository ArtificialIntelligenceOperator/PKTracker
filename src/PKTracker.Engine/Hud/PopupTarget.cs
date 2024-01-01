using PKTracker.Core.Model;

namespace PKTracker.Engine.Hud;

/// <summary>Что открыть по наведению или клику: вкладку попапа профиля или разбивку одного стата.</summary>
public sealed record PopupTarget
{
    public required long PlayerId { get; init; }
    public required string PlayerName { get; init; }
    public required GameKind Kind { get; init; }

    /// <summary>Код стата — откроется его разбивка.</summary>
    public string? StatCode { get; init; }

    /// <summary>Профиль и вкладка — откроется попап профиля. Имеет приоритет над <see cref="StatCode"/>.</summary>
    public HudProfile? Profile { get; init; }
    public string? Page { get; init; }

    /// <summary>
    /// Вложенность: вкладка профиля открывается с панели (0), разбивка стата — из этой вкладки (1).
    /// У каждого уровня свой слот, иначе попап стата закрывал бы тот попап, из которого открыт.
    /// </summary>
    public int Level => Profile != null ? 0 : 1;

    /// <summary>Повторное наведение на тот же стат не должно переоткрывать уже показанный попап.</summary>
    public object Key => (PlayerId, StatCode, Profile?.Name, Page);
}
