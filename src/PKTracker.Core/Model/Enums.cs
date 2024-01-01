namespace PKTracker.Core.Model;

public enum GameKind : byte { Cash = 0, Mtt = 1, Sng = 2 }

public enum Street : byte { Preflop = 0, Flop = 1, Turn = 2, River = 3 }

/// <summary>Значения хранятся в БД (hand_actions.action) — не переназначать.</summary>
public enum ActionType : byte
{
    PostSb = 0,
    PostBb = 1,
    PostAnte = 2,
    /// <summary>Мёртвый SB + живой BB одной строкой.</summary>
    PostDead = 3,
    /// <summary>Живой BB вне очереди ("posts $0.10").</summary>
    PostBbOot = 4,
    Fold = 5,
    Check = 6,
    Call = 7,
    Bet = 8,
    Raise = 9,
    UncalledReturn = 10,
    Collect = 11,
}

/// <summary>§15.3. Значения хранятся в БД — не переназначать.</summary>
public enum Position : byte
{
    BTN = 0, SB = 1, BB = 2, UTG = 3, UTG1 = 4, MP = 5, LJ = 6, HJ = 7, CO = 8,
    /// <summary>Не назначена (не раздан / бомб-пот).</summary>
    None = 9,
}

public enum Facing : byte { None = 0, Bet = 1, Raise = 2 }

/// <summary>Выбранное действие в точке решения.</summary>
public enum Choice : byte { Fold = 0, Check = 1, Call = 2, Bet = 3, Raise = 4 }

[Flags]
public enum HandFlags : int
{
    None = 0,
    BombPot = 1 << 0,
    RunItTwice = 1 << 1,
    DeadButton = 1 << 2,
    NoSmallBlind = 1 << 3,
    HeadsUp = 1 << 4,
}

/// <summary>Факты уровня (рука, игрок). §15.6: биты только добавлять в конец, никогда не переиспользовать.</summary>
[Flags]
public enum FactBits : long
{
    None = 0,
    Dealt = 1L << 0,
    Vpip = 1L << 1,
    Pfr = 1L << 2,
    SawFlop = 1L << 3,
    SawTurn = 1L << 4,
    SawRiver = 1L << 5,
    SawShowdown = 1L << 6,
    WonAtShowdown = 1L << 7,
    WonHand = 1L << 8,
    PostedOutOfTurn = 1L << 9,
    PostedSb = 1L << 10,
    PostedBb = 1L << 11,
    PostedDead = 1L << 12,
    AllIn = 1L << 13,
    IsHero = 1L << 14,
    ThreeBet = 1L << 15,
    PfAggressor = 1L << 16,
    CardsKnown = 1L << 17,
    SittingOut = 1L << 18,
}

/// <summary>Контекст точки решения. §15.6: биты только добавлять в конец.</summary>
[Flags]
public enum CtxBits : long
{
    None = 0,
    PfAggressor = 1L << 0,
    PrevStreetAggressor = 1L << 1,
    IsIp = 1L << 2,
    FirstActionOnStreet = 1L << 3,
    InBlinds = 1L << 4,
    CheckedThisStreet = 1L << 5,
    FacingCbet = 1L << 6,
    StealSpot = 1L << 7,
    AggressorBehind = 1L << 8,
    VoluntarilyInvested = 1L << 9,
    FacingAllIn = 1L << 10,
}
