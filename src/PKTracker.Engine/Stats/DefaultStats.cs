namespace PKTracker.Engine.Stats;

/// <summary>
/// Дефолтный набор статов (§4.3, §5.3, §7.1). Опечатка здесь = неверный HUD у всех — каждое определение
/// покрыто оракул-тестом в PKTracker.Tests.
/// </summary>
public static class DefaultStats
{
    private static readonly List<StatColor> PreflopLooseness = new()
    {
        new(0, 15, "#4FC3F7"), new(15, 30, "#9CCC65"), new(30, 101, "#EF5350"),
    };
    private static readonly List<StatColor> Aggression = new()
    {
        new(0, 5, "#4FC3F7"), new(5, 10, "#9CCC65"), new(10, 101, "#EF5350"),
    };
    private static readonly List<StatColor> Folding = new()
    {
        new(0, 40, "#EF5350"), new(40, 65, "#9CCC65"), new(65, 101, "#4FC3F7"),
    };
    private static readonly List<StatColor> Cbet = new()
    {
        new(0, 45, "#4FC3F7"), new(45, 70, "#9CCC65"), new(70, 101, "#EF5350"),
    };

    private static StatDefinition D(string code, string title, string opp, string hit, List<StatColor>? colors = null, int minSample = 30) => new()
    {
        Code = code, Title = title, Scope = StatScope.Decision, Opportunity = opp, Hit = hit,
        Colors = colors ?? new(), MinSample = minSample, IsBuiltin = true,
    };

    private static StatDefinition H(string code, string title, string opp, string hit, List<StatColor>? colors = null, int minSample = 30) => new()
    {
        Code = code, Title = title, Scope = StatScope.Hand, Opportunity = opp, Hit = hit,
        Colors = colors ?? new(), MinSample = minSample, IsBuiltin = true,
    };

    private static StatDefinition E(string code, string title, string formula, int decimals, string suffix = "", List<StatColor>? colors = null) => new()
    {
        Code = code, Title = title, Scope = StatScope.Expression, Hit = formula,
        Format = new StatFormat { Decimals = decimals, Suffix = suffix, Percent = false },
        Colors = colors ?? new(), IsBuiltin = true,
    };

    /// <summary>Базовый набор; полный (с <see cref="ExtendedStats"/>) — <see cref="All"/>.</summary>
    private static readonly List<StatDefinition> Base = new()
    {
        // ── hand-level ──────────────────────────────────────────────────────
        new StatDefinition
        {
            Code = "hands", Title = "Hands", Scope = StatScope.Hand, Opportunity = "Dealt", Hit = "true",
            Format = new StatFormat { Percent = false }, IsBuiltin = true, MinSample = 0,
        },
        H("vpip", "VPIP", "Dealt && !Hand.BombPot", "Vpip", PreflopLooseness),
        H("pfr", "PFR", "Dealt && !Hand.BombPot", "Pfr", PreflopLooseness),
        H("wtsd", "WTSD", "SawFlop", "SawShowdown"),
        H("wsd", "W$SD", "SawShowdown", "WonAtShowdown"),
        H("wwsf", "WWSF", "SawFlop", "WonHand"),
        new StatDefinition
        {
            Code = "net_bb", Title = "Net bb", Scope = StatScope.Hand, Opportunity = "Dealt", Hit = "true",
            Value = "NetWonBbX100", Format = new StatFormat { Percent = false }, IsBuiltin = true, MinSample = 0,
        },

        // Деньги/фишки: sum_value в минимальных единицах (кеш — центы, турниры — фишки×100); game_kind разделяет валюты
        new StatDefinition
        {
            Code = "net_won", Title = "Net won", Scope = StatScope.Hand, Opportunity = "Dealt", Hit = "true",
            Value = "NetWon", Format = new StatFormat { Percent = false }, IsBuiltin = true, MinSample = 0,
        },
        // All-in EV: выигрыш с заменой олл-инов на математическое ожидание (Chip EV в турнирах, EV $ в кеше)
        new StatDefinition
        {
            Code = "ev_won", Title = "EV won", Scope = StatScope.Hand, Opportunity = "Dealt", Hit = "EvNetWon != null",
            Value = "EvOrNetWon", Format = new StatFormat { Percent = false }, IsBuiltin = true, MinSample = 0,
        },
        new StatDefinition
        {
            Code = "ev_bb", Title = "EV bb", Scope = StatScope.Hand, Opportunity = "Dealt", Hit = "EvNetWon != null",
            Value = "EvNetBbX100", Format = new StatFormat { Percent = false }, IsBuiltin = true, MinSample = 0,
        },

        // ── preflop decisions ───────────────────────────────────────────────
        D("rfi", "RFI", "Street==\"Preflop\" && BetLevel==1 && NumLimpers==0 && Position!=\"BB\"", "Chosen==\"Raise\""),
        D("limp", "Limp", "Street==\"Preflop\" && BetLevel==1 && !InBlinds", "Chosen==\"Call\""),
        D("three_bet", "3B", "Street==\"Preflop\" && BetLevel==2 && Facing==\"Raise\"", "Chosen==\"Raise\"", Aggression),
        D("fold_to_3bet", "F3B", "Street==\"Preflop\" && BetLevel==3 && MyLastLevel==2", "Chosen==\"Fold\"", Folding),
        D("four_bet", "4B", "Street==\"Preflop\" && BetLevel==3 && MyLastLevel==2", "Chosen==\"Raise\"", Aggression),
        D("fold_to_4bet", "F4B", "Street==\"Preflop\" && BetLevel==4 && MyLastLevel==3", "Chosen==\"Fold\"", Folding),
        D("cold_call", "CC", "Street==\"Preflop\" && BetLevel==2 && !InBlinds && MyLastLevel==0 && NumLimpers==0", "Chosen==\"Call\""),
        D("squeeze", "SQZ", "Street==\"Preflop\" && BetLevel==2 && NumCallers>=1", "Chosen==\"Raise\"", Aggression),
        D("steal", "Steal", "Street==\"Preflop\" && BetLevel==1 && NumLimpers==0 && (Position==\"CO\" || Position==\"BTN\" || Position==\"SB\")", "Chosen==\"Raise\""),
        D("fold_bb_vs_steal", "FBBvS", "Street==\"Preflop\" && Position==\"BB\" && BetLevel==2 && StealSpot", "Chosen==\"Fold\"", Folding),

        // ── postflop decisions ──────────────────────────────────────────────
        D("cbet_flop", "CBet F", "Street==\"Flop\" && PfAggressor && Facing==\"None\" && FirstActionOnStreet", "Chosen==\"Bet\"", Cbet),
        D("fold_to_cbet_flop", "FCBet F", "Street==\"Flop\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Fold\"", Folding),
        D("raise_cbet_flop", "RCBet F", "Street==\"Flop\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Raise\"", Aggression),
        D("cbet_turn", "CBet T", "Street==\"Turn\" && PrevStreetAggressor && Facing==\"None\" && FirstActionOnStreet", "Chosen==\"Bet\"", Cbet),
        D("fold_to_cbet_turn", "FCBet T", "Street==\"Turn\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Fold\"", Folding),
        D("cbet_river", "CBet R", "Street==\"River\" && PrevStreetAggressor && Facing==\"None\" && FirstActionOnStreet", "Chosen==\"Bet\"", Cbet),
        D("check_raise_flop", "XR F", "Street==\"Flop\" && Facing==\"Bet\" && CheckedThisStreet", "Chosen==\"Raise\"", Aggression),
        D("donk_flop", "Donk F", "Street==\"Flop\" && Facing==\"None\" && !PfAggressor && !IsIp && AggressorBehind", "Chosen==\"Bet\""),
        D("pf_bets", "PF Bets", "Street!=\"Preflop\"", "Chosen==\"Bet\"", minSample: 0),
        D("pf_raises", "PF Raises", "Street!=\"Preflop\"", "Chosen==\"Raise\"", minSample: 0),
        D("pf_calls", "PF Calls", "Street!=\"Preflop\"", "Chosen==\"Call\"", minSample: 0),
        D("pf_folds", "PF Folds", "Street!=\"Preflop\"", "Chosen==\"Fold\"", minSample: 0),

        // ── MTT push/fold (§7.1) ────────────────────────────────────────────
        D("open_shove", "OShove", "Street==\"Preflop\" && BetLevel==1 && NumLimpers==0 && Position!=\"BB\"", "Chosen==\"Raise\" && IsAllIn"),
        D("call_shove", "CShove", "Street==\"Preflop\" && FacingAllIn && ToCall>0", "Chosen==\"Call\""),
        D("fold_to_shove", "FShove", "Street==\"Preflop\" && FacingAllIn && ToCall>0", "Chosen==\"Fold\"", Folding),

        // ── expression ──────────────────────────────────────────────────────
        E("af", "AF", "(pf_bets.hits + pf_raises.hits) / max(pf_calls.hits, 1)", 1),
        // §5.3: AGG% = (bet+raise) / (bet+raise+call+fold) постфлоп — чеки в знаменатель не входят.
        E("agg_pct", "AGG", "(pf_bets.hits + pf_raises.hits) * 100 / max(pf_bets.hits + pf_raises.hits + pf_calls.hits + pf_folds.hits, 1)", 0),
        E("bb100", "bb/100", "net_bb.sum / 100 / max(hands.cases, 1) * 100", 1),
        E("ev_bb100", "EV bb/100", "ev_bb.sum / 100 / max(hands.cases, 1) * 100", 1),
        // Удача в олл-инах: факт минус EV, в bb/100 (плюс — везёт)
        E("luck_bb100", "Luck bb/100", "(net_bb.sum - ev_bb.sum) / 100 / max(hands.cases, 1) * 100", 1),
    };

    /// <summary>Все встроенные статы: базовые плюс те, что нужны многоячеечным раскладкам.</summary>
    public static IReadOnlyList<StatDefinition> All { get; } = Base.Concat(ExtendedStats.All).ToList();
}
