namespace PKTracker.Tests;

internal static class SampleHands
{
    /// <summary>Open HJ, 3-бет BTN, фолд опенера. Ник "Vik" — префикс ника "Vik Storm" (ловушка 1).</summary>
    public const string OpenThreeBetFold = """
        Hand #1001 - Holdem (No Limit) - $0.10/$0.25 - 2024/01/05 13:29:01 UTC
        Demorest 6-max Seat #5 is the button
        Seat 1: Nord player ($25.00)
        Seat 2: Vik Storm ($26.62)
        Seat 3: Vik ($25.00)
        Seat 4: LMason07 ($25.00)
        Seat 5: Sundialler ($72.91)
        Seat 6: Kestrel14 ($36.59)
        Kestrel14 posts the small blind $0.10
        Nord player posts the big blind $0.25
        *** HOLE CARDS ***
        Dealt to Vik [Ks Kh]
        Vik Storm folds
        Vik raises $0.75 to $0.75
        LMason07 folds
        Sundialler raises $2.50 to $2.50
        Kestrel14 folds
        Nord player folds
        Vik folds
        Uncalled bet ($1.75) returned to Sundialler
        Sundialler does not show
        *** SUMMARY ***
        Total pot $1.85
        Seat 1: Nord player (big blind) folded on the Pre-Flop
        Seat 2: Vik Storm folded on the Pre-Flop and did not bet
        Seat 3: Vik folded on the Pre-Flop and did not bet
        Seat 4: LMason07 folded on the Pre-Flop and did not bet
        Seat 5: Sundialler (button) did not show and won $1.85
        Seat 6: Kestrel14 (small blind) folded on the Pre-Flop
        """;

    /// <summary>Стил с BTN, колл BB, чек-рейз против сибета. Рейк в SUMMARY.</summary>
    public const string StealCbetCheckRaise = """
        Hand #1002 - Holdem (No Limit) - $0.05/$0.10 - 2024/01/02 17:02:16 UTC
        Axtell 6-max Seat #3 is the button
        Seat 1: Alpha ($10.00)
        Seat 2: Beta ($10.00)
        Seat 3: Gamma ($10.00)
        Seat 4: Delta ($10.00)
        Seat 5: Eps ($10.00)
        Delta posts the small blind $0.05
        Eps posts the big blind $0.10
        *** HOLE CARDS ***
        Dealt to Alpha [2c 7d]
        Alpha folds
        Beta folds
        Gamma raises $0.25 to $0.25
        Delta folds
        Eps calls $0.15
        *** FLOP *** [Kh Qc 8d]
        Main pot $0.55 | Rake $0.00
        Eps checks
        Gamma bets $0.30
        Eps raises $1.20 to $1.20
        Gamma folds
        Uncalled bet ($0.90) returned to Eps
        Eps does not show
        *** SUMMARY ***
        Total pot $1.10 | Rake $0.05
        Board [Kh Qc 8d]
        Seat 1: Alpha folded on the Pre-Flop and did not bet
        Seat 2: Beta folded on the Pre-Flop and did not bet
        Seat 3: Gamma (button) folded on the Flop
        Seat 4: Delta (small blind) folded on the Pre-Flop
        Seat 5: Eps (big blind) did not show and won $1.10
        """;

    /// <summary>Хедз-ап: лимп BTN, шоудаун.</summary>
    public const string HeadsUpShowdown = """
        Hand #1003 - Holdem (No Limit) - $0.05/$0.10 - 2024/01/02 18:00:00 UTC
        Axtell 6-max Seat #2 is the button
        Seat 2: Btn ($10.00)
        Seat 5: Bb ($10.00)
        Btn posts the small blind $0.05
        Bb posts the big blind $0.10
        *** HOLE CARDS ***
        Dealt to Btn [Qh Jh]
        Btn calls $0.05
        Bb checks
        *** FLOP *** [2c 7d Kh]
        Bb bets $0.10
        Btn calls $0.10
        *** TURN *** [2c 7d Kh] [As]
        Bb checks
        Btn checks
        *** RIVER *** [2c 7d Kh As] [3s]
        Bb checks
        Btn checks
        *** SHOW DOWN ***
        Bb shows [Ac Ad] (three of a kind, Aces [Ac Ad As Kh 7d])
        Btn shows [Qh Jh] (a high card, Ace high [As Kh Qh Jh 7d])
        Bb collected $0.40 from main pot
        *** SUMMARY ***
        Total pot $0.40
        Board [2c 7d Kh As 3s]
        Seat 2: Btn (button) showed [Qh Jh] and lost with a high card, Ace high [As Kh Qh Jh 7d]
        Seat 5: Bb (big blind) showed [Ac Ad] and won $0.40 with three of a kind, Aces [Ac Ad As Kh 7d]
        """;

    /// <summary>Турнир: анте, мёртвая кнопка (место 8 не раздавалось), ник-число, сайд-пот.</summary>
    /// <summary>
    /// 3-max спин: короткий стек в олл-ине за неполный блайнд, обладатель большего блайнда сбросил.
    /// WPN пишет «Uncalled bet (620.00)», хотя рейзер поставил всего 600: в возврат попал ещё и
    /// мёртвый сайд-пот 25, который он забирает без вскрытия.
    /// </summary>
    public const string SpinOverstatedUncalledBet = """
        Game Hand #2827266390 - Tournament #35990244 - Holdem (No Limit) - Level 4 (30.00/60.00) - 2026/09/19 14:57:49 UTC
        Table '1' 3-max Seat #1 is the button
        Seat 1: MabryUK88 (600.00)
        Seat 2: Hero (295.00)
        Seat 3: vesperon (5.00)
        Hero posts the small blind 30.00
        vesperon posts the big blind 5.00 and is all-in
        *** HOLE CARDS ***
        Dealt to Hero [Qd 3h]
        MabryUK88 raises 600.00 to 600.00 and is all-in
        Hero folds
        Uncalled bet (620.00) returned to MabryUK88
        *** FLOP *** [7c 4s 5d]
        Main pot 15.00
        *** TURN *** [7c 4s 5d] [2c]
        Main pot 15.00
        *** RIVER *** [7c 4s 5d 2c] [7d]
        Main pot 15.00
        *** SHOW DOWN ***
        Main pot 15.00
        MabryUK88 shows [Ah 2d] (two pair, Sevens and Deuces [7d 7c 2d 2c Ah])
        vesperon shows [Ts Th] (two pair, Tens and Sevens [Ts Th 7d 7c 5d])
        vesperon collected 15.00 from main pot
        *** SUMMARY ***
        Total pot 40.00
        Board [7c 4s 5d 2c 7d]
        Seat 1: MabryUK88 (button) showed [Ah 2d] and won 25.00 with two pair, Sevens and Deuces [7d 7c 2d 2c Ah]
        Seat 2: Hero (small blind) folded on the Pre-Flop
        Seat 3: vesperon (big blind) showed [Ts Th] and won 15.00 with two pair, Tens and Sevens [Ts Th 7d 7c 5d]
        """;

    public const string TourneyDeadButton = """
        Game Hand #2001 - Tournament #40000001 - Holdem (No Limit) - Level 2 (100.00/200.00) - 2024/01/03 16:20:49 UTC
        Table '7' 8-max Seat #8 is the button
        Seat 1: 12345 (1000.00)
        Seat 2: Nina Wolf (5000.00)
        Seat 3: Hero (5000.00)
        Seat 8: Gone (3000.00) is sitting out
        12345 posts ante 25.00
        Nina Wolf posts ante 25.00
        Hero posts ante 25.00
        12345 posts the small blind 100.00
        Nina Wolf posts the big blind 200.00
        *** HOLE CARDS ***
        Main pot 75.00
        Dealt to Hero [As Ks]
        Hero raises 600.00 to 600.00
        12345 calls 875.00 and is all-in
        Nina Wolf raises 4775.00 to 4975.00 and is all-in
        Hero calls 4375.00 and is all-in
        *** FLOP *** [2c 3c 4c]
        Main pot 3000.00
        Side pot(1) 8000.00
        *** TURN *** [2c 3c 4c] [Td]
        *** RIVER *** [2c 3c 4c Td] [Jd]
        *** SHOW DOWN ***
        12345 shows [5c 6d] (a straight flush, Six high [6d 5c 4c 3c 2c])
        Nina Wolf shows [Qh Qd] (a pair of Queens [Qh Qd As Jd Td])
        Hero shows [As Ks] (a high card, Ace high [As Ks Jd Td 4c])
        12345 collected 3000.00 from main pot
        Nina Wolf collected 8000.00 from side pot-1
        *** SUMMARY ***
        Total pot 11000.00
        Board [2c 3c 4c Td Jd]
        Seat 1: 12345 (small blind) showed [5c 6d] and won 3000.00 with a straight flush, Six high [6d 5c 4c 3c 2c]
        Seat 2: Nina Wolf (big blind) showed [Qh Qd] and won 8000.00 with a pair of Queens [Qh Qd As Jd Td]
        Seat 3: Hero showed [As Ks] and lost with a high card, Ace high [As Ks Jd Td 4c]
        """;
}
