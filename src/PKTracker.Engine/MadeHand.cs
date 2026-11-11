using System.Numerics;
using PKTracker.Core.Model;

namespace PKTracker.Engine;

/// <summary>
/// Класс готовой руки относительно борда. Категории эвалюатора («пара», «две пары») для разбора игры
/// слишком грубые: оверпара и младшая пара — обе «пара», а играются противоположно.
/// </summary>
public enum MadeHandClass : byte
{
    /// <summary>Карты неизвестны или префлоп.</summary>
    Unknown = 0,
    NoPair = 1,
    /// <summary>Две карты старше борда, без пары.</summary>
    Overcards = 2,
    /// <summary>Карманная пара ниже старшей карты борда.</summary>
    Underpair = 3,
    BottomPair = 4,
    MiddlePair = 5,
    TopPair = 6,
    Overpair = 7,
    TwoPair = 8,
    /// <summary>Трипс: одна своя карта к паре на борде.</summary>
    Trips = 9,
    /// <summary>Сет: карманная пара и карта на борде.</summary>
    Set = 10,
    Straight = 11,
    Flush = 12,
    FullHouse = 13,
    Quads = 14,
    StraightFlush = 15,
}

/// <summary>
/// Классификатор «рука игрока на этой улице»: младший байт — <see cref="MadeHandClass"/>, старшие биты — дро.
/// Пара на борде, которая есть у всех, силой руки не считается: A♠K♠ на борде 7-7-2 — это «оверкарты»,
/// а не «пара», иначе отчёт по силе рук показывал бы блеф как ставку с готовой рукой.
/// </summary>
public static class MadeHands
{
    public const short FlushDrawBit = 0x100;
    /// <summary>Двусторонний стрит-дро или двойной гатшот — 8 аутов.</summary>
    public const short OesdBit = 0x200;
    public const short GutshotBit = 0x400;
    /// <summary>Топ-пара с хорошим кикером (валет и старше) — «TPGK» против «TPWK».</summary>
    public const short GoodKickerBit = 0x800;

    /// <summary>
    /// Категории «постфлоп-диаграммы» в привычном по Hand2Note порядке — от воздуха к сильным рукам.
    /// Индекс категории возвращает <see cref="StrengthCategory"/>.
    /// </summary>
    public static readonly string[] StrengthCategories =
    {
        "KQ, KJ, QJ", "Trash", "AK, AQ, AJ", "A-high", "Gutshot", "Draw", "Draw+Pair", "Low Pockets", "Low Pair",
        "Middle Pair", "TPWK", "TPGK", "Overpair", "Set", "2pair+",
    };

    /// <summary>
    /// Категория руки для диаграммы силы. Руки без пары и без дро делятся по своим картам (туз с картинкой,
    /// две картинки, туз, мусор) — на борде они одинаково «ничего», но играются по-разному.
    /// </summary>
    public static int StrengthCategory(short bits, Card? c1, Card? c2)
    {
        var cls = Class(bits);
        bool strongDraw = (bits & (FlushDrawBit | OesdBit)) != 0;
        switch (cls)
        {
            case MadeHandClass.Trips or MadeHandClass.Set: return 13;
            case >= MadeHandClass.TwoPair: return 14;
            case MadeHandClass.Overpair: return 12;
            case MadeHandClass.TopPair: return (bits & GoodKickerBit) != 0 ? 11 : 10;
            case MadeHandClass.MiddlePair: return strongDraw ? 6 : 9;
            case MadeHandClass.BottomPair: return strongDraw ? 6 : 8;
            case MadeHandClass.Underpair: return strongDraw ? 6 : 7;
        }
        if (strongDraw) return 5;
        if ((bits & GutshotBit) != 0) return 4;
        if (c1 is not { } a || c2 is not { } b) return 1;
        int hi = Math.Max(a.Rank, b.Rank), lo = Math.Min(a.Rank, b.Rank);   // ранги: 12 — туз, 11 — король, 10 — дама, 9 — валет
        if (hi == 12) return lo >= 9 ? 2 : 3;
        return lo >= 9 && hi != lo ? 0 : 1;
    }

    public static MadeHandClass Class(short bits) => (MadeHandClass)(bits & 0xFF);

    public static short Classify(Card h1, Card h2, ReadOnlySpan<Card> board)
    {
        if (board.Length < 3) return 0;
        long boardPacked = 0;
        int boardMask = 0;
        Span<int> boardRanks = stackalloc int[13];
        Span<int> suits = stackalloc int[4];
        foreach (var c in board)
        {
            boardPacked |= HandEvaluator.Pack(c);
            boardMask |= 1 << c.Rank;
            boardRanks[c.Rank]++;
            suits[c.Suit]++;
        }
        long allPacked = boardPacked | HandEvaluator.Pack(h1) | HandEvaluator.Pack(h2);
        int allValue = HandEvaluator.EvaluatePacked(allPacked);
        int boardValue = HandEvaluator.EvaluatePacked(boardPacked);
        var cat = HandEvaluator.Category(allValue);
        var boardCat = HandEvaluator.Category(boardValue);

        int topRank = 31 - BitOperations.LeadingZeroCount((uint)boardMask);
        bool pocket = h1.Rank == h2.Rank;
        // Своя карта попала в неспаренную карту борда (в спаренную — это уже трипс).
        bool h1Single = boardRanks[h1.Rank] == 1, h2Single = boardRanks[h2.Rank] == 1;

        MadeHandClass Nothing() => h1.Rank > topRank && h2.Rank > topRank ? MadeHandClass.Overcards : MadeHandClass.NoPair;

        // Пара своей картой: какая по старшинству среди разных рангов борда.
        MadeHandClass PairWith(int rank)
        {
            int higher = BitOperations.PopCount((uint)(boardMask >> (rank + 1)));
            return higher switch { 0 => MadeHandClass.TopPair, 1 => MadeHandClass.MiddlePair, _ => MadeHandClass.BottomPair };
        }

        MadeHandClass OwnPair()
        {
            if (pocket) return h1.Rank > topRank ? MadeHandClass.Overpair : MadeHandClass.Underpair;
            if (h1Single && (!h2Single || h1.Rank > h2.Rank)) return PairWith(h1.Rank);
            if (h2Single) return PairWith(h2.Rank);
            return Nothing();
        }

        MadeHandClass made;
        if (board.Length == 5 && allValue == boardValue) made = Nothing();   // играет борд
        else
            made = cat switch
            {
                HandCategory.StraightFlush => MadeHandClass.StraightFlush,
                HandCategory.Quads => boardCat == HandCategory.Quads ? Nothing() : MadeHandClass.Quads,
                HandCategory.FullHouse => MadeHandClass.FullHouse,
                HandCategory.Flush => MadeHandClass.Flush,
                HandCategory.Straight => MadeHandClass.Straight,
                HandCategory.Trips => boardCat == HandCategory.Trips ? Nothing()
                    : pocket ? MadeHandClass.Set : MadeHandClass.Trips,
                HandCategory.TwoPair => boardCat switch
                {
                    // Борд без пар: обе свои карты попали — настоящие две пары.
                    HandCategory.HighCard => MadeHandClass.TwoPair,
                    // Борд спарен: вторую пару дала одна своя карта или карманка — по силе это «пара».
                    HandCategory.Pair => !pocket && h1Single && h2Single
                        ? MadeHandClass.TwoPair : OwnPair(),
                    _ => OwnPair(),
                },
                HandCategory.Pair => boardCat == HandCategory.Pair ? Nothing() : OwnPair(),
                _ => Nothing(),
            };

        short bits = (short)made;
        if (made == MadeHandClass.TopPair)
        {
            // Топ-пара собрана одной своей картой; вторая — кикер. «Хороший» — от валета.
            var kicker = h1.Rank == topRank ? h2 : h1;
            if (kicker.Rank >= 9) bits |= GoodKickerBit;
        }
        if (board.Length < 5)
        {
            if (cat < HandCategory.Flush)
            {
                suits[h1.Suit]++; suits[h2.Suit]++;
                if (suits[h1.Suit] == 4 || suits[h2.Suit] == 4) bits |= FlushDrawBit;
            }
            if (cat < HandCategory.Straight)
            {
                int allMask = boardMask | (1 << h1.Rank) | (1 << h2.Rank);
                // Ауты на стрит, которых нет у голого борда: дро «на борде» есть у всех и рукой не считается.
                int mine = StraightOuts(allMask) & ~StraightOuts(boardMask);
                int n = BitOperations.PopCount((uint)mine);
                if (n >= 2) bits |= OesdBit;
                else if (n == 1) bits |= GutshotBit;
            }
        }
        return bits;
    }

    /// <summary>Маска рангов, каждый из которых достраивает стрит.</summary>
    private static int StraightOuts(int mask)
    {
        if (HasStraight(mask)) return 0;
        int outs = 0;
        for (int r = 0; r < 13; r++)
            if ((mask & (1 << r)) == 0 && HasStraight(mask | (1 << r))) outs |= 1 << r;
        return outs;
    }

    private static bool HasStraight(int mask)
    {
        int ext = (mask << 1) | ((mask >> 12) & 1);   // туз ещё и как единица — колесо
        return (ext & (ext >> 1) & (ext >> 2) & (ext >> 3) & (ext >> 4)) != 0;
    }

    public static string Ru(MadeHandClass c) => c switch
    {
        MadeHandClass.NoPair => "Ничего",
        MadeHandClass.Overcards => "Оверкарты",
        MadeHandClass.Underpair => "Карманка ниже борда",
        MadeHandClass.BottomPair => "Младшая пара",
        MadeHandClass.MiddlePair => "Средняя пара",
        MadeHandClass.TopPair => "Топ-пара",
        MadeHandClass.Overpair => "Оверпара",
        MadeHandClass.TwoPair => "Две пары",
        MadeHandClass.Trips => "Трипс",
        MadeHandClass.Set => "Сет",
        MadeHandClass.Straight => "Стрит",
        MadeHandClass.Flush => "Флеш",
        MadeHandClass.FullHouse => "Фулл-хаус",
        MadeHandClass.Quads => "Каре",
        MadeHandClass.StraightFlush => "Стрит-флеш",
        _ => "Неизвестно",
    };
}
