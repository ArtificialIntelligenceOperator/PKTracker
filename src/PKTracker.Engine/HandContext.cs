using PKTracker.Core.Model;

namespace PKTracker.Engine;

/// <summary>Сведения о раздаче, доступные в условиях статов как <c>Hand.*</c>.</summary>
public sealed class HandInfo
{
    /// <summary>Исходная раздача; null, если контекст восстановлен из БД.</summary>
    public ParsedHand? Parsed { get; init; }
    public GameKind GameKind { get; init; }
    public HandFlags Flags { get; init; }
    public int PlayersDealt { get; init; }
    public int MaxSeats { get; init; }
    public long BigBlind { get; init; }
    public long SmallBlind { get; init; }
    public long Ante { get; init; }

    public bool BombPot => (Flags & HandFlags.BombPot) != 0;
    public bool RunItTwice => (Flags & HandFlags.RunItTwice) != 0;
    public bool DeadButton => (Flags & HandFlags.DeadButton) != 0;
    public bool HeadsUp => PlayersDealt == 2;
    public bool HasAnte => Ante > 0;
    public bool IsTournament => GameKind != GameKind.Cash;

    /// <summary>0 HU, 1 = 3-4, 2 = 5-6, 3 = 7+.</summary>
    public byte DealtBucket => PlayersDealt switch { <= 2 => 0, <= 4 => 1, <= 6 => 2, _ => 3 };

    public static HandInfo From(ParsedHand h, int playersDealt) => new()
    {
        Parsed = h, GameKind = h.GameKind, Flags = h.Flags, PlayersDealt = playersDealt, MaxSeats = h.MaxSeats,
        BigBlind = h.BigBlind, SmallBlind = h.SmallBlind, Ante = h.Ante,
    };
}

/// <summary>Нормализованное действие (hand_actions).</summary>
public readonly record struct ActionRecord(
    short Seq, Street Street, byte Seat, ActionType Action, long Amount, long ToAmount, long PotBefore, bool IsAllIn);

/// <summary>
/// Точка решения (§4.2): снимок ситуации ДО действия игрока + выбранное действие.
/// Свойства — это то, что доступно в условиях статов.
/// </summary>
public sealed class DecisionPoint
{
    public required HandInfo Hand { get; init; }
    public short Seq { get; init; }
    public byte Seat { get; init; }
    public required string PlayerName { get; init; }
    public Street Street { get; init; }
    public Position Position { get; init; }
    public Facing Facing { get; init; }
    public int BetLevel { get; init; }
    public int MyLastLevel { get; init; }
    public long ToCall { get; init; }
    public long PotBefore { get; init; }
    public long EffStack { get; init; }
    public int PlayersIn { get; init; }
    public int PlayersBehind { get; init; }
    public int NumLimpers { get; init; }
    public int NumCallers { get; init; }
    public CtxBits Ctx { get; init; }
    public Choice Chosen { get; init; }
    public long ChosenAmount { get; init; }
    public short? SizePct { get; init; }
    public bool IsAllIn { get; init; }
    public short? HandStrength { get; set; }
    public short BoardTexture { get; init; }
    public byte StackBucket { get; init; }

    /// <summary>
    /// Позиция последнего агрессора на этой улице (префлоп — последнего рейзера); <c>None</c>, если ставки не было.
    /// Нужна матрицам «BB vs BTN», «EP vs MP», «Fold vs DONK»: без неё «против кого» восстановить нечем.
    /// </summary>
    public Position VsPosition { get; init; } = Position.None;
    /// <summary>
    /// Позиция того, кто открыл банк рейзом на префлопе; <c>None</c>, если опена не было.
    /// Отличается от <see cref="VsPosition"/> на 4-бет-спотах: там против 3-беттора, а фильтр — по опенрейзеру.
    /// </summary>
    public Position OpenerPosition { get; init; } = Position.None;
    /// <summary>
    /// Сколько у игрока стоит на улице ПОСЛЕ действия (для рейза — размер «до»). Сайзинги опенов
    /// задаются в bb, а не в % банка, поэтому <see cref="SizePct"/> для них не годится.
    /// </summary>
    public long ToAmount { get; init; }

    /// <summary>
    /// Текущая ставка на улице, которую нужно перекрыть («до» последнего агрессора), в минимальных единицах.
    /// Из <see cref="ToCall"/> её не вычислить: у BB часть уже поставлена блайндом, у остальных — нет.
    /// </summary>
    public long FacingAmount { get; init; }

    /// <summary>
    /// Последнее действие игрока на каждой завершённой улице до текущей, по 3 бита на улицу
    /// (флоп — младшие). Значения: 0 — не действовал, 1 X, 2 B, 3 C, 4 R.
    /// </summary>
    public short LineBits { get; init; }

    // ── ContextVersion 4: обстановка постфлоп-спота (миграция 007) ─────────────────────────────
    // Постфлоп-статы принято описывать спотами — «рейзер против BB», «лимп-пот блайндов», «3-бет-пот» —
    // и линиями соперника («ставка-чек», «чек на флопе — ставка на тёрне»). Точка решения видит
    // только свою улицу, поэтому снимок конца префлопа и линии соперников несём с собой.

    /// <summary>Линия префлоп-агрессора на прошлых улицах постфлопа (как <see cref="LineBits"/>); 0 — агрессор я или его нет.</summary>
    public short PfaLineBits { get; init; }
    /// <summary>Линия единственного оппонента в хедз-ап банке на прошлых улицах; 0 — в банке больше двух игроков.</summary>
    public short OppLineBits { get; init; }
    /// <summary>Рейзов префлоп: на префлопе — до решения (как BetLevel−1), на постфлопе — всего (тип банка).</summary>
    public int PotRaises { get; init; }
    /// <summary>Лимперов префлоп: на префлопе — до решения, на постфлопе — всего до опена.</summary>
    public int PfLimpers { get; init; }
    /// <summary>Позиция префлоп-агрессора: на префлопе — последнего рейзера до решения, на постфлопе — итогового.</summary>
    public Position PfrPosition { get; init; } = Position.None;
    /// <summary>Игроков, увидевших флоп (на префлопе 0).</summary>
    public int FlopPlayers { get; init; }
    /// <summary>Битовая маска позиций, увидевших флоп: бит = (int)Position.</summary>
    public short FlopMask { get; init; }
    /// <summary>Стартовый стек последнего агрессора (против кого решение) — «3-бет против 12-16bb».</summary>
    public long VsStack { get; init; }
    /// <summary>Эффективный стек по стартовым стекам: мой против наибольшего среди всех, кому сданы карты (как в Hand2Note).</summary>
    public long StartEffStack { get; init; }
    /// <summary>Сколько игроков заколлировали опен до 3-бета: больше нуля — 3-бет был сквизом. До 3-бета — 0.</summary>
    public int CallersBefore3b { get; init; }
    /// <summary>3-бет в раздаче был сквизом (перед ним опен заколлировали).</summary>
    public bool WasSqueeze => CallersBefore3b > 0;

    // ── ContextVersion 5: готовая рука и исход раздачи ───────────────────────────────────────
    /// <summary>
    /// Рука игрока на этой улице: младший байт — <see cref="MadeHandClass"/>, старшие биты — дро
    /// (<see cref="MadeHands"/>). 0 — карты неизвестны или префлоп. Колонка made_hand (миграция 009).
    /// </summary>
    public short MadeHandBits { get; set; }
    public MadeHandClass Made => MadeHands.Class(MadeHandBits);
    public bool MadeKnown => MadeHandBits != 0;
    public bool FlushDraw => (MadeHandBits & MadeHands.FlushDrawBit) != 0;
    /// <summary>Двусторонний стрит-дро или двойной гатшот.</summary>
    public bool Oesd => (MadeHandBits & MadeHands.OesdBit) != 0;
    public bool Gutshot => (MadeHandBits & MadeHands.GutshotBit) != 0;
    public bool AnyDraw => (MadeHandBits & (MadeHands.FlushDrawBit | MadeHands.OesdBit | MadeHands.GutshotBit)) != 0;
    /// <summary>Топ-пара с кикером от валета (TPGK).</summary>
    public bool GoodKicker => (MadeHandBits & MadeHands.GoodKickerBit) != 0;
    /// <summary>Готовая рука от топ-пары и выше.</summary>
    public bool StrongMade => Made >= MadeHandClass.TopPair;

    /// <summary>
    /// Факты игрока по раздаче целиком (hand_players.fact_bits), видимые из точки решения. Нужны статам вида
    /// «выиграл без вскрытия в банке рейзер против BB»: спот известен только точке решения, исход — только
    /// фактам руки. Отдельной колонки нет — при чтении из базы подставляется JOIN-ом.
    /// </summary>
    public FactBits Outcome { get; set; }
    public bool HandWon => (Outcome & FactBits.WonHand) != 0;
    public bool WentToShowdown => (Outcome & FactBits.SawShowdown) != 0;
    public bool WonAtShowdown => (Outcome & FactBits.WonAtShowdown) != 0;

    public string PfaLine => LineString(PfaLineBits, Street);
    public string OppLine => LineString(OppLineBits, Street);
    public double VsStackBb => Hand.BigBlind > 0 ? (double)VsStack / Hand.BigBlind : 0;
    public double StartEffBb => Hand.BigBlind > 0 ? (double)StartEffStack / Hand.BigBlind : 0;
    /// <summary>Тип банка: Limp, SRP, 3BP, 4BP.</summary>
    public string PotType => PotRaises switch { 0 => "Limp", 1 => "SRP", 2 => "3BP", _ => "4BP" };
    public bool InPot(Position p) => (FlopMask & (1 << (int)p)) != 0;
    public bool BbInPot => InPot(Position.BB);
    public bool SbInPot => InPot(Position.SB);
    /// <summary>Префлоп-агрессор — не блайнд (UTG…BTN).</summary>
    public bool PfrNotBlind => PfrPosition is not (Position.SB or Position.BB or Position.None);

    /// <summary>
    /// Постфлоп-спот в привычных сокращениях. RvBB/RvSB/RvCC — рейзер (не блайнд) против одного коллера
    /// на BB / SB / в позиции; BvB_L — лимп SB и чек BB; BvB_R — рейз SB, колл BB; BvB_LC — лимп SB,
    /// изолейт BB, колл SB; RvCCBB — рейзер, колд-коллер и BB втроём; 3BP_IP / 3BP_OOP — хедз-ап 3-бет-пот,
    /// где 3-беттор в позиции (не блайнд против не-блайнда) или вне её (с блайндов); 3BP_BvB, 3BP, 4BP — прочие;
    /// 3way/MW — прочие мультипоты. На префлопе — пустая строка.
    /// </summary>
    public string Spot
    {
        get
        {
            if (Street == Street.Preflop || FlopPlayers < 2) return "";
            if (FlopPlayers == 2)
            {
                bool bvb = BbInPot && SbInPot;
                switch (PotRaises)
                {
                    case 0: return bvb ? "BvB_L" : "LimpHU";
                    case 1:
                        if (PfrNotBlind) return BbInPot ? "RvBB" : SbInPot ? "RvSB" : "RvCC";
                        if (bvb) return PfrPosition == Position.SB ? "BvB_R" : "BvB_LC";
                        return PfrPosition == Position.BB ? "BBvL" : "SBvL";
                    case 2:
                    {
                        // 3-бет-пот: важно, кто агрессор и в позиции ли он («3better IP / OOP»).
                        int other = FlopMask & ~(1 << (int)PfrPosition);
                        bool otherBlind = (other & ((1 << (int)Position.SB) | (1 << (int)Position.BB))) != 0;
                        if (PfrNotBlind) return otherBlind ? "3BP" : "3BP_IP";
                        return otherBlind ? "3BP_BvB" : "3BP_OOP";
                    }
                    default: return "4BP";
                }
            }
            if (FlopPlayers == 3 && PotRaises == 1 && PfrNotBlind && BbInPot && !SbInPot) return "RvCCBB";
            return FlopPlayers == 3 ? "3way" : "MW";
        }
    }

    /// <summary>
    /// Позиция в обозначениях Hand2Note: в 7–9-max — UTG / MP / HJ / CO / BTN, в 6-max и меньше —
    /// MP / HJ / CO / BTN (первая позиция 6-max у них «MP»). Блайнды — SB / BB.
    /// </summary>
    public string HPos => HandPositions.H2n(Position, Hand.PlayersDealt);
    public string HPfrPos => HandPositions.H2n(PfrPosition, Hand.PlayersDealt);
    public string HVsPos => HandPositions.H2n(VsPosition, Hand.PlayersDealt);
    public string HOpenPos => HandPositions.H2n(OpenerPosition, Hand.PlayersDealt);

    public bool PfAggressor => (Ctx & CtxBits.PfAggressor) != 0;
    public bool PrevStreetAggressor => (Ctx & CtxBits.PrevStreetAggressor) != 0;
    public bool IsIp => (Ctx & CtxBits.IsIp) != 0;
    public bool FirstActionOnStreet => (Ctx & CtxBits.FirstActionOnStreet) != 0;
    public bool InBlinds => (Ctx & CtxBits.InBlinds) != 0;
    public bool CheckedThisStreet => (Ctx & CtxBits.CheckedThisStreet) != 0;
    public bool FacingCbet => (Ctx & CtxBits.FacingCbet) != 0;
    public bool StealSpot => (Ctx & CtxBits.StealSpot) != 0;
    public bool AggressorBehind => (Ctx & CtxBits.AggressorBehind) != 0;
    public bool VoluntarilyInvested => (Ctx & CtxBits.VoluntarilyInvested) != 0;
    public bool FacingAllIn => (Ctx & CtxBits.FacingAllIn) != 0;

    /// <summary>Эффективный стек в bb.</summary>
    public double EffStackBb => Hand.BigBlind > 0 ? (double)EffStack / Hand.BigBlind : 0;
    public double PotOdds => ToCall > 0 ? (double)ToCall / (PotBefore + ToCall) : 0;
    /// <summary>Размер собственной ставки «до» в bb — «опен ≤ 4bb» и прочие префлоп-сайзинги.</summary>
    public double ToAmountBb => Hand.BigBlind > 0 ? (double)ToAmount / Hand.BigBlind : 0;
    /// <summary>Сколько доплатить, в bb.</summary>
    public double ToCallBb => Hand.BigBlind > 0 ? (double)ToCall / Hand.BigBlind : 0;
    /// <summary>Ставка оппонента, которую нужно перекрыть, в bb — «против опена 2.5bb».</summary>
    public double FacingBb => Hand.BigBlind > 0 ? (double)FacingAmount / Hand.BigBlind : 0;
    /// <summary>Во сколько раз собственный рейз перекрывает ставку оппонента: 2 — минрейз.</summary>
    public double RaiseRatio => FacingAmount > 0 ? (double)ToAmount / FacingAmount : 0;
    /// <summary>Банк до решения, в bb.</summary>
    public double PotBb => Hand.BigBlind > 0 ? (double)PotBefore / Hand.BigBlind : 0;

    /// <summary>
    /// Собственная линия игрока на постфлопе до текущей улицы: «B», «BX», «XC» и т. п.
    /// Ради неё и заведён <see cref="LineBits"/>: линии вроде BXB и XCF живут сразу на трёх улицах,
    /// а одна точка решения видит только свою.
    /// </summary>
    public string MyLine => LineString(LineBits, Street);

    /// <summary>Расшифровка <see cref="LineBits"/> в строку линии — отдельно от свойства, чтобы её можно было проверить.</summary>
    public static string LineString(short bits, Street upTo)
    {
        if (bits == 0) return "";
        int streets = upTo switch { Street.Flop => 0, Street.Turn => 1, Street.River => 2, _ => 0 };
        Span<char> buf = stackalloc char[3];
        int n = 0;
        for (int i = 0; i < streets; i++)
        {
            int code = (bits >> (i * 3)) & 7;
            if (code == 0) break;
            buf[n++] = code switch { 1 => 'X', 2 => 'B', 3 => 'C', 4 => 'R', _ => '?' };
        }
        return new string(buf[..n]);
    }
}

/// <summary>Факты уровня (рука, игрок) — hand_players.</summary>
public sealed class HandPlayerFacts
{
    public required HandInfo Hand { get; init; }
    public byte Seat { get; init; }
    public required string PlayerName { get; init; }
    public Position Position { get; init; }
    public long StartStack { get; init; }
    public short StackBbX10 { get; init; }
    public byte StackBucket { get; init; }
    public Card? HoleCard1 { get; init; }
    public Card? HoleCard2 { get; init; }
    public short? HoleClass { get; init; }
    public long Invested { get; set; }
    public long Collected { get; set; }
    public long NetWon => Collected - Invested;
    public int NetWonBbX100 => Hand.BigBlind > 0 ? (int)Math.Clamp(NetWon * 100 / Hand.BigBlind, int.MinValue, int.MaxValue) : 0;
    public long RakeShare { get; set; }
    /// <summary>All-in EV выигрыш (null — олл-ина с известными картами не было, EV = факт).</summary>
    public long? EvNetWon { get; set; }
    public long EvOrNetWon => EvNetWon ?? NetWon;
    public int EvNetBbX100 => Hand.BigBlind > 0 ? (int)Math.Clamp(EvOrNetWon * 100 / Hand.BigBlind, int.MinValue, int.MaxValue) : 0;
    public FactBits Bits { get; set; }

    public bool Dealt => (Bits & FactBits.Dealt) != 0;
    public bool Vpip => (Bits & FactBits.Vpip) != 0;
    public bool Pfr => (Bits & FactBits.Pfr) != 0;
    public bool ThreeBet => (Bits & FactBits.ThreeBet) != 0;
    public bool SawFlop => (Bits & FactBits.SawFlop) != 0;
    public bool SawTurn => (Bits & FactBits.SawTurn) != 0;
    public bool SawRiver => (Bits & FactBits.SawRiver) != 0;
    public bool SawShowdown => (Bits & FactBits.SawShowdown) != 0;
    public bool WonAtShowdown => (Bits & FactBits.WonAtShowdown) != 0;
    public bool WonHand => (Bits & FactBits.WonHand) != 0;
    public bool IsHero => (Bits & FactBits.IsHero) != 0;
    public bool AllIn => (Bits & FactBits.AllIn) != 0;
    public bool PostedOutOfTurn => (Bits & FactBits.PostedOutOfTurn) != 0;
    public bool CardsKnown => (Bits & FactBits.CardsKnown) != 0;
    public double StackBb => StackBbX10 / 10.0;
}

public sealed class HandContext
{
    public required HandInfo Hand { get; init; }
    /// <summary>null, если контекст восстановлен из БД.</summary>
    public Positions.Result? Positions { get; init; }
    public required List<HandPlayerFacts> Players { get; init; }
    public required List<DecisionPoint> Decisions { get; init; }
    public List<ActionRecord> Actions { get; init; } = new();
    public long TotalInvested { get; init; }
    public long TotalCollected { get; init; }
}
