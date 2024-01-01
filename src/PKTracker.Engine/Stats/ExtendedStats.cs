namespace PKTracker.Engine.Stats;

/// <summary>
/// Статы, которых требуют многоячеечные раскладки HUD (§5.4).
///
/// Здесь только те, что нельзя получить фильтром ячейки. Разрезы по позиции, полосе стека, числу
/// игроков и олл-ину живут в <c>CellFilter</c>: заводить «3bet из CO против BTN при 15-25bb»
/// отдельным статом — это сотни определений и сотни пересчётов вместо одного.
///
/// Каждое определение — гипотеза о принятом в индустрии счёте; где формулировка спорная, это сказано
/// в комментарии. Знаменатель важнее числителя: если <c>cases = 0</c>, ошибка почти всегда в нём.
/// </summary>
public static class ExtendedStats
{
    private static readonly List<StatColor> Loose = new()
    {
        new(0, 15, "#4FC3F7"), new(15, 30, "#9CCC65"), new(30, 101, "#EF5350"),
    };
    private static readonly List<StatColor> Aggro = new()
    {
        new(0, 5, "#4FC3F7"), new(5, 12, "#9CCC65"), new(12, 101, "#EF5350"),
    };
    private static readonly List<StatColor> Fold = new()
    {
        new(0, 40, "#EF5350"), new(40, 65, "#9CCC65"), new(65, 101, "#4FC3F7"),
    };
    private static readonly List<StatColor> Bet = new()
    {
        new(0, 45, "#4FC3F7"), new(45, 70, "#9CCC65"), new(70, 101, "#EF5350"),
    };

    private static StatDefinition D(string code, string title, string opp, string hit, List<StatColor>? colors = null, int minSample = 25) => new()
    {
        Code = code, Title = title, Scope = StatScope.Decision, Opportunity = opp, Hit = hit,
        Colors = colors ?? new(), MinSample = minSample, IsBuiltin = true,
    };

    private static StatDefinition E(string code, string title, string formula, int decimals = 1, List<StatColor>? colors = null) => new()
    {
        Code = code, Title = title, Scope = StatScope.Expression, Hit = formula,
        Format = new StatFormat { Decimals = decimals, Percent = false },
        Colors = colors ?? new(), IsBuiltin = true,
    };

    // Префлоп: игрок уже добровольно вложил на первом круге и не рейзил — то есть лимпнул,
    // а теперь за него подняли. MyLastLevel==0 отсекает того, кто сам открывал.
    private const string LimpedFacingRaise =
        "Street==\"Preflop\" && BetLevel==2 && Facing==\"Raise\" && MyLastLevel==0 && VoluntarilyInvested";

    // Знаменатель всех «против опена»: перед игроком ровно один рейз, сам он ещё не поднимал.
    private const string FacingOpen = "Street==\"Preflop\" && BetLevel==2 && Facing==\"Raise\" && MyLastLevel==0";

    public static IReadOnlyList<StatDefinition> All { get; } = new List<StatDefinition>
    {
        // ── Префлоп: лимпы ──────────────────────────────────────────────────
        // Встроенный limp считает только не-блайнды. Для строки «Limp» с колонкой SB нужен
        // вариант, где доставка малого блайнда тоже лимп: иначе колонка SB всегда пуста.
        D("limp_any", "Limp", "Street==\"Preflop\" && BetLevel==1 && Position!=\"BB\"", "Chosen==\"Call\""),
        D("sb_complete", "SB Compl", "Street==\"Preflop\" && BetLevel==1 && Position==\"SB\"", "Chosen==\"Call\""),
        D("limp_fold", "L-F", LimpedFacingRaise, "Chosen==\"Fold\"", Fold),
        D("limp_call", "L-C", LimpedFacingRaise, "Chosen==\"Call\""),
        D("limp_raise", "L-R", LimpedFacingRaise, "Chosen==\"Raise\"", Aggro),
        // Изоляция лимперов: банк ещё не поднимали, но в нём уже есть лимперы.
        D("iso_raise", "ISO", "Street==\"Preflop\" && BetLevel==1 && NumLimpers>=1", "Chosen==\"Raise\"", Aggro),
        D("iso_shove", "ISO-P", "Street==\"Preflop\" && BetLevel==1 && NumLimpers>=1", "Chosen==\"Raise\" && IsAllIn", Aggro),
        D("fold_to_iso", "F-ISO", "Street==\"Preflop\" && BetLevel==2 && Facing==\"Raise\" && MyLastLevel==0 && NumLimpers>=1", "Chosen==\"Fold\"", Fold),

        // ── Префлоп: против опенрейза ───────────────────────────────────────
        // Встроенный cold_call исключает блайнды, а в раскладке у строки Cold Call есть колонки
        // BB и BB vs SB. Здесь колл против опена считается со всех мест, включая блайнды.
        D("cold_call_any", "CC", "Street==\"Preflop\" && BetLevel==2 && Facing==\"Raise\" && MyLastLevel==0 && NumLimpers==0", "Chosen==\"Call\""),
        D("fold_vs_open", "F-OP", FacingOpen, "Chosen==\"Fold\"", Fold),
        D("call_vs_open", "C-OP", FacingOpen, "Chosen==\"Call\""),
        // Рестил: 3-бет против опена, который сам же и олл-ин.
        D("resteal_shove", "RES", FacingOpen, "Chosen==\"Raise\" && IsAllIn", Aggro),
        // Минимальный 3-бет: рейз меньше чем в 2.3 раза перекрывает опен (минрейз — ровно 2).
        D("min_three_bet", "Min3b", "Street==\"Preflop\" && BetLevel==2 && Facing==\"Raise\"", "Chosen==\"Raise\" && RaiseRatio <= 2.3", Aggro),
        D("open_3_5x", "3-5x", "Street==\"Preflop\" && BetLevel==1 && NumLimpers==0 && Position!=\"BB\"", "Chosen==\"Raise\" && ToAmountBb >= 3 && ToAmountBb <= 5"),
        // Пятибет: перед игроком четыре уровня ставок, последний собственный рейз был 3-бетом.
        D("five_bet", "5B", "Street==\"Preflop\" && BetLevel==4 && MyLastLevel==3", "Chosen==\"Raise\"", Aggro),
        D("fold_to_squeeze", "F-SQZ", "Street==\"Preflop\" && BetLevel==3 && MyLastLevel==2 && NumCallers>=1", "Chosen==\"Fold\"", Fold),
        // Холодный 4-бет: игрок входит в банк сразу 4-бетом, до этого не поднимал.
        D("cold_four_bet", "C4B", "Street==\"Preflop\" && BetLevel==3 && MyLastLevel==0", "Chosen==\"Raise\"", Aggro),
        D("bb_defend", "Def BB", "Street==\"Preflop\" && Position==\"BB\" && BetLevel==2 && Facing==\"Raise\" && MyLastLevel==0", "Chosen!=\"Fold\""),
        // Оверрейз в ответ на олл-ин (реджем): доступен, только если стек позволяет поднять.
        D("over_shove", "OVER-R", "Street==\"Preflop\" && FacingAllIn && ToCall>0 && EffStack>ToCall", "Chosen==\"Raise\"", Aggro),

        // ── Постфлоп: продолженные ставки ───────────────────────────────────
        D("fold_to_cbet_river", "FCB R", "Street==\"River\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Fold\"", Fold),
        D("call_cbet_flop", "CCB F", "Street==\"Flop\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Call\""),
        D("call_cbet_turn", "CCB T", "Street==\"Turn\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Call\""),
        D("raise_cbet_turn", "RCB T", "Street==\"Turn\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Raise\"", Aggro),
        D("raise_cbet_river", "RCB R", "Street==\"River\" && Facing==\"Bet\" && FacingCbet", "Chosen==\"Raise\"", Aggro),
        // Отложенный сибет: агрессор пропустил свою улицу чеком и ставит следующую.
        D("delay_cbet_turn", "DCB T", "Street==\"Turn\" && PfAggressor && Facing==\"None\" && FirstActionOnStreet && MyLine==\"X\"", "Chosen==\"Bet\"", Bet),
        D("delay_cbet_river", "DCB R", "Street==\"River\" && PfAggressor && Facing==\"None\" && FirstActionOnStreet && MyLine==\"XX\"", "Chosen==\"Bet\"", Bet),
        // Против отложенного сибета: банк вёл не мы, на флопе ставки не было (иначе это сибет),
        // и на тёрне инициативу забирает префлоп-агрессор.
        D("fold_to_delay_cbet_turn", "F-DCB T", "Street==\"Turn\" && Facing==\"Bet\" && !PfAggressor && !FacingCbet && MyLine==\"X\"", "Chosen==\"Fold\"", Fold),
        // Рейз на собственный сибет: MyLastLevel==1 — ставку сделал сам игрок, и её подняли.
        D("fold_to_raise_cbet_flop", "F-R F", "Street==\"Flop\" && Facing==\"Raise\" && MyLastLevel==1 && PfAggressor", "Chosen==\"Fold\"", Fold),
        D("fold_to_raise_cbet_turn", "F-R T", "Street==\"Turn\" && Facing==\"Raise\" && MyLastLevel==1 && PrevStreetAggressor", "Chosen==\"Fold\"", Fold),
        D("check_behind_flop", "X-B", "Street==\"Flop\" && Facing==\"None\" && IsIp && PlayersBehind==0", "Chosen==\"Check\""),

        // ── Постфлоп: донки и пробы ─────────────────────────────────────────
        D("donk_turn", "Donk T", "Street==\"Turn\" && Facing==\"None\" && !PrevStreetAggressor && !IsIp && AggressorBehind", "Chosen==\"Bet\""),
        D("donk_river", "Donk R", "Street==\"River\" && Facing==\"None\" && !PrevStreetAggressor && !IsIp && AggressorBehind", "Chosen==\"Bet\""),
        // Против донка: ставит не агрессор прошлой улицы, поэтому сибетом она не считается.
        D("fold_to_donk_flop", "F-Donk F", "Street==\"Flop\" && Facing==\"Bet\" && PfAggressor && !FacingCbet", "Chosen==\"Fold\"", Fold),
        D("fold_to_donk_turn", "F-Donk T", "Street==\"Turn\" && Facing==\"Bet\" && PrevStreetAggressor && !FacingCbet", "Chosen==\"Fold\"", Fold),
        D("raise_donk_flop", "R-Donk F", "Street==\"Flop\" && Facing==\"Bet\" && PfAggressor && !FacingCbet", "Chosen==\"Raise\"", Aggro),
        // Проба: агрессор прошлой улицы не поставил, и ставку делает не он, вне позиции.
        D("probe_turn", "Prb T", "Street==\"Turn\" && Facing==\"None\" && !PrevStreetAggressor && FirstActionOnStreet && !IsIp", "Chosen==\"Bet\""),
        D("probe_river", "Prb R", "Street==\"River\" && Facing==\"None\" && !PrevStreetAggressor && FirstActionOnStreet && !IsIp", "Chosen==\"Bet\""),
        // Пробу получает префлоп-агрессор, пропустивший флоп чеком. Брать здесь PrevStreetAggressor
        // нельзя: на тёрне это «агрессор флопа», а он по условию как раз не ставил.
        D("fold_to_probe_turn", "F-Prb T", "Street==\"Turn\" && Facing==\"Bet\" && PfAggressor && MyLine==\"X\"", "Chosen==\"Fold\"", Fold),
        D("fold_to_probe_river", "F-Prb R", "Street==\"River\" && Facing==\"Bet\" && PfAggressor && MyLine==\"XX\"", "Chosen==\"Fold\"", Fold),
        D("call_probe_turn", "C-Prb T", "Street==\"Turn\" && Facing==\"Bet\" && PfAggressor && MyLine==\"X\"", "Chosen==\"Call\""),

        // ── Постфлоп: флоат ─────────────────────────────────────────────────
        D("float_flop", "Flt F", "Street==\"Flop\" && Facing==\"Bet\" && FacingCbet && IsIp", "Chosen==\"Call\""),
        // Коллировал флоп в позиции, на тёрне до него чекнули — ставит.
        D("float_turn", "Flt T", "Street==\"Turn\" && MyLine==\"C\" && Facing==\"None\" && IsIp", "Chosen==\"Bet\"", Bet),
        D("float_river", "Flt R", "Street==\"River\" && MyLine==\"CC\" && Facing==\"None\" && IsIp", "Chosen==\"Bet\"", Bet),
        D("fold_to_float_turn", "F-Flt T", "Street==\"Turn\" && Facing==\"Bet\" && PrevStreetAggressor && MyLine==\"B\"", "Chosen==\"Fold\"", Fold),

        // ── Постфлоп: чек-рейзы и рейзы ─────────────────────────────────────
        D("check_raise_turn", "XR T", "Street==\"Turn\" && Facing==\"Bet\" && CheckedThisStreet", "Chosen==\"Raise\"", Aggro),
        D("check_raise_river", "XR R", "Street==\"River\" && Facing==\"Bet\" && CheckedThisStreet", "Chosen==\"Raise\"", Aggro),
        D("fold_to_check_raise_flop", "F-XR F", "Street==\"Flop\" && Facing==\"Raise\" && MyLastLevel==1", "Chosen==\"Fold\"", Fold),
        // 3-бет постфлоп: игрок поставил, его подняли, он поднимает снова.
        D("three_bet_flop", "3b F", "Street==\"Flop\" && Facing==\"Raise\" && MyLastLevel==1", "Chosen==\"Raise\"", Aggro),
        D("three_bet_turn", "3b T", "Street==\"Turn\" && Facing==\"Raise\" && MyLastLevel==1", "Chosen==\"Raise\"", Aggro),
        D("fold_to_three_bet_flop", "F3b F", "Street==\"Flop\" && Facing==\"Raise\" && MyLastLevel==2", "Chosen==\"Fold\"", Fold),
        D("fold_to_four_bet_flop", "F4b F", "Street==\"Flop\" && Facing==\"Raise\" && MyLastLevel==3", "Chosen==\"Fold\"", Fold),

        // ── Постфлоп: ставки и защита по улицам ─────────────────────────────
        D("bet_flop", "Bet F", "Street==\"Flop\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("bet_turn", "Bet T", "Street==\"Turn\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("bet_river", "Bet R", "Street==\"River\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("bet_allin_flop", "Bet AL F", "Street==\"Flop\" && Facing==\"None\"", "Chosen==\"Bet\" && IsAllIn"),
        D("fold_vs_bet_flop", "FvsB F", "Street==\"Flop\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("fold_vs_bet_turn", "FvsB T", "Street==\"Turn\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("fold_vs_bet_river", "FvsB R", "Street==\"River\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("fold_vs_allin_flop", "FvsB-AL F", "Street==\"Flop\" && Facing==\"Bet\" && FacingAllIn", "Chosen==\"Fold\"", Fold),
        D("raise_flop", "Raise F", "Street==\"Flop\" && Facing==\"Bet\"", "Chosen==\"Raise\"", Aggro),
        D("raise_turn", "Raise T", "Street==\"Turn\" && Facing==\"Bet\"", "Chosen==\"Raise\"", Aggro),
        D("raise_river", "Raise R", "Street==\"River\" && Facing==\"Bet\"", "Chosen==\"Raise\"", Aggro),
        D("fold_vs_raise_flop", "FvsR F", "Street==\"Flop\" && Facing==\"Raise\"", "Chosen==\"Fold\"", Fold),
        D("fold_vs_raise_turn", "FvsR T", "Street==\"Turn\" && Facing==\"Raise\"", "Chosen==\"Fold\"", Fold),
        D("fold_vs_raise_river", "FvsR R", "Street==\"River\" && Facing==\"Raise\"", "Chosen==\"Fold\"", Fold),

        // ── Линии (MyLine — собственные действия на прошлых улицах) ─────────
        // Вторая буква — что игрок сделал на тёрне, третья — решение, которое и считается.
        D("line_bxb", "BXB", "Street==\"River\" && MyLine==\"BX\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("line_bxf", "BXF", "Street==\"River\" && MyLine==\"BX\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("line_xxb", "XXB", "Street==\"River\" && MyLine==\"XX\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("line_xxf", "XXF", "Street==\"River\" && MyLine==\"XX\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("line_xcb", "XCB", "Street==\"River\" && MyLine==\"XC\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("line_xcf", "XCF", "Street==\"River\" && MyLine==\"XC\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("line_cxb", "CXB", "Street==\"River\" && MyLine==\"CX\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("line_cxf", "CXF", "Street==\"River\" && MyLine==\"CX\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("line_xbb", "XBB", "Street==\"River\" && MyLine==\"XB\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("line_cbb", "CBB", "Street==\"River\" && MyLine==\"CB\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("line_xb", "XB", "Street==\"Turn\" && MyLine==\"X\" && Facing==\"None\"", "Chosen==\"Bet\"", Bet),
        D("line_xf", "XF", "Street==\"Turn\" && MyLine==\"X\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),
        D("line_bf", "BF", "Street==\"Turn\" && MyLine==\"B\" && Facing==\"Bet\"", "Chosen==\"Fold\"", Fold),

        // ── Агрессия по улицам (знаменатели для expression ниже) ────────────
        D("f_bets", "F bets", "Street==\"Flop\"", "Chosen==\"Bet\"", minSample: 0),
        D("f_raises", "F raises", "Street==\"Flop\"", "Chosen==\"Raise\"", minSample: 0),
        D("f_calls", "F calls", "Street==\"Flop\"", "Chosen==\"Call\"", minSample: 0),
        D("f_folds", "F folds", "Street==\"Flop\"", "Chosen==\"Fold\"", minSample: 0),
        D("t_bets", "T bets", "Street==\"Turn\"", "Chosen==\"Bet\"", minSample: 0),
        D("t_raises", "T raises", "Street==\"Turn\"", "Chosen==\"Raise\"", minSample: 0),
        D("t_calls", "T calls", "Street==\"Turn\"", "Chosen==\"Call\"", minSample: 0),
        D("t_folds", "T folds", "Street==\"Turn\"", "Chosen==\"Fold\"", minSample: 0),
        D("r_bets", "R bets", "Street==\"River\"", "Chosen==\"Bet\"", minSample: 0),
        D("r_raises", "R raises", "Street==\"River\"", "Chosen==\"Raise\"", minSample: 0),
        D("r_calls", "R calls", "Street==\"River\"", "Chosen==\"Call\"", minSample: 0),
        D("r_folds", "R folds", "Street==\"River\"", "Chosen==\"Fold\"", minSample: 0),

        // AGG% = (ставки + рейзы) / (ставки + рейзы + коллы + фолды); чеки в знаменатель не входят (§5.3).
        E("agg_flop", "AGG F", "(f_bets.hits + f_raises.hits) * 100 / max(f_bets.hits + f_raises.hits + f_calls.hits + f_folds.hits, 1)", 0),
        E("agg_turn", "AGG T", "(t_bets.hits + t_raises.hits) * 100 / max(t_bets.hits + t_raises.hits + t_calls.hits + t_folds.hits, 1)", 0),
        E("agg_river", "AGG R", "(r_bets.hits + r_raises.hits) * 100 / max(r_bets.hits + r_raises.hits + r_calls.hits + r_folds.hits, 1)", 0),
    };
}
