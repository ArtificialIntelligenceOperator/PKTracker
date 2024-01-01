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
