using System.Numerics;
using PKTracker.Core.Model;

namespace PKTracker.Engine;

public enum HandCategory : byte
{
    HighCard = 0, Pair = 1, TwoPair = 2, Trips = 3, Straight = 4, Flush = 5, FullHouse = 6, Quads = 7, StraightFlush = 8,
}

/// <summary>
/// Эвалюатор 5–7 карт на битовых масках рангов (без таблиц). Результат — int:
/// category &lt;&lt; 24 | тай-брейкеры; больше = сильнее. Тест-обязательство: §15.5.
/// </summary>
public static class HandEvaluator
{
    public static HandCategory Category(int value) => (HandCategory)(value >> 24);

    /// <summary>Карта в упакованной маске: 13 бит на масть.</summary>
    public static long Pack(Card c) => 1L << ((c.Value >> 2) + 13 * (c.Value & 3));

    public static int Evaluate(ReadOnlySpan<Card> cards)
    {
        long packed = 0;
        foreach (var c in cards) packed |= Pack(c); // без ветвлений
        return EvaluatePacked(packed);
    }

    /// <summary>Оценка набора карт, заданного упакованной маской (карты не повторяются).</summary>
    public static int EvaluatePacked(long packed)
    {
        int m0 = (int)(packed & 0x1FFF), m1 = (int)((packed >> 13) & 0x1FFF);
        int m2 = (int)((packed >> 26) & 0x1FFF), m3 = (int)((packed >> 39) & 0x1FFF);

        // Флеш / стрит-флеш (при ≤ 7 картах флеш и фулл-хаус/каре одновременно невозможны)
        int flush = BitOperations.PopCount((uint)m0) >= 5 ? m0
                  : BitOperations.PopCount((uint)m1) >= 5 ? m1
                  : BitOperations.PopCount((uint)m2) >= 5 ? m2
                  : BitOperations.PopCount((uint)m3) >= 5 ? m3 : 0;
        if (flush != 0)
        {
            int sf = StraightHigh(flush);
            if (sf >= 0) return (8 << 24) | sf;
            return (5 << 24) | TopBits(flush, 5);
        }

        int all = m0 | m1 | m2 | m3;
        int quads = m0 & m1 & m2 & m3;
        int atLeast2 = (m0 & m1) | (m0 & m2) | (m0 & m3) | (m1 & m2) | (m1 & m3) | (m2 & m3);
        int atLeast3 = (m0 & m1 & m2) | (m0 & m1 & m3) | (m0 & m2 & m3) | (m1 & m2 & m3);
        int trips = atLeast3 & ~quads;
        int pairs = atLeast2 & ~atLeast3;

        if (quads != 0)
        {
            int q = HighestRank(quads);
            return (7 << 24) | (q << 16) | TopBits(all & ~(1 << q), 1);
        }
        if (trips != 0)
        {
            int t = HighestRank(trips);
            int restPairs = (pairs | trips) & ~(1 << t);
            if (restPairs != 0)
                return (6 << 24) | (t << 16) | (HighestRank(restPairs) << 12);
        }

        int st = StraightHigh(all);
        if (st >= 0) return (4 << 24) | st;

        if (trips != 0)
        {
            int t = HighestRank(trips);
            return (3 << 24) | (t << 16) | TopBits(all & ~(1 << t), 2);
        }
        if (BitOperations.PopCount((uint)pairs) >= 2)
        {
            int p1 = HighestRank(pairs);
            int p2 = HighestRank(pairs & ~(1 << p1));
            return (2 << 24) | (p1 << 16) | (p2 << 12) | TopBits(all & ~(1 << p1) & ~(1 << p2), 1);
        }
        if (pairs != 0)
        {
            int p = HighestRank(pairs);
            return (1 << 24) | (p << 16) | TopBits(all & ~(1 << p), 3);
        }
        return TopBits(all, 5);
    }

    private static int HighestRank(int mask) => 31 - BitOperations.LeadingZeroCount((uint)mask);

    /// <summary>Оставляет n старших битов маски (кикеры).</summary>
    private static int TopBits(int mask, int n)
    {
        int res = 0;
        while (n-- > 0 && mask != 0)
        {
            int r = HighestRank(mask);
            res |= 1 << r;
            mask &= ~(1 << r);
        }
        return res;
    }

    /// <summary>Старшая карта стрита или -1. Колесо A-5 → 3 (пятёрка).</summary>
    private static int StraightHigh(int mask)
    {
        // Бит A дублируется как «единица» (позиция -1 → сдвиг всей маски на 1)
        int ext = (mask << 1) | ((mask >> 12) & 1);
        int run = ext & (ext >> 1) & (ext >> 2) & (ext >> 3) & (ext >> 4);
        return run == 0 ? -1 : HighestRank(run) + 3; // бит j в run: карты ext[j..j+4]; старшая ext[j+4] = ранг j+3
    }

    /// <summary>
    /// Текстура борда (битовая маска), считается по первым <paramref name="count"/> картам доски.
    /// </summary>
    public static int BoardTexture(IReadOnlyList<Card> board, int count)
    {
        count = Math.Min(count, board.Count);
        if (count < 3) return 0;
        Span<int> suits = stackalloc int[4];
        Span<int> ranks = stackalloc int[13];
        int mask = 0;
        for (int i = 0; i < count; i++)
        {
            suits[board[i].Suit]++;
            ranks[board[i].Rank]++;
            mask |= 1 << board[i].Rank;
        }
        int tex = 0;
        int maxSuit = 0, maxRank = 0;
        foreach (var s in suits) maxSuit = Math.Max(maxSuit, s);
        foreach (var r in ranks) maxRank = Math.Max(maxRank, r);
        if (maxRank >= 2) tex |= (int)Texture.Paired;
        if (maxRank >= 3) tex |= (int)Texture.Trips;
        if (maxSuit == count) tex |= (int)Texture.Monotone;
        else if (maxSuit >= 3) tex |= (int)Texture.FlushPossible;
        else if (maxSuit == 2) tex |= (int)Texture.TwoTone;
        else tex |= (int)Texture.Rainbow;
        // Стрит возможен: 3 разных ранга в окне из 5 (с учётом колеса)
        int ext = (mask << 1) | ((mask >> 12) & 1);
        for (int lo = 0; lo <= 9; lo++)
            if (BitOperations.PopCount((uint)((ext >> lo) & 0x1F)) >= 3) { tex |= (int)Texture.StraightPossible; break; }
        int high = HighestRank(mask);
        if (high >= 10) tex |= (int)Texture.HighBroadway; // Q, K, A
        return tex;
    }

    [Flags]
    public enum Texture
    {
        Paired = 1, Trips = 2, Monotone = 4, TwoTone = 8, Rainbow = 16, StraightPossible = 32, HighBroadway = 64, FlushPossible = 128,
    }
}
