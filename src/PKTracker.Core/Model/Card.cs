namespace PKTracker.Core.Model;

/// <summary>Карта: rank*4 + suit. rank 0..12 = 2..A, suit 0..3 = c d h s (§15.5).</summary>
public readonly record struct Card(byte Value)
{
    public const string Ranks = "23456789TJQKA";
    public const string Suits = "cdhs";

    public int Rank => Value >> 2;
    public int Suit => Value & 3;

    public static Card Of(int rank, int suit) => new((byte)(rank * 4 + suit));

    public static bool TryParse(ReadOnlySpan<char> s, out Card card)
    {
        card = default;
        if (s.Length != 2) return false;
        int r = Ranks.IndexOf(char.ToUpperInvariant(s[0]));
        int u = Suits.IndexOf(char.ToLowerInvariant(s[1]));
        if (r < 0 || u < 0) return false;
        card = Of(r, u);
        return true;
    }

    public static Card Parse(string s) =>
        TryParse(s, out var c) ? c : throw new FormatException($"Bad card '{s}'");

    public override string ToString() => $"{Ranks[Rank]}{Suits[Suit]}";

    /// <summary>Индекс в сетке 13×13 (0..168): hi,lo — индексы рангов от A=0 до 2=12.</summary>
    public static short HoleClass(Card a, Card b)
    {
        int ia = 12 - a.Rank, ib = 12 - b.Rank;
        int hi = Math.Min(ia, ib), lo = Math.Max(ia, ib);
        if (hi == lo) return (short)(hi * 13 + hi);
        return a.Suit == b.Suit ? (short)(hi * 13 + lo) : (short)(lo * 13 + hi);
    }

    public static string HoleClassName(int cls)
    {
        int row = cls / 13, col = cls % 13;
        char r1 = Ranks[12 - Math.Min(row, col)], r2 = Ranks[12 - Math.Max(row, col)];
        if (row == col) return $"{r1}{r2}";
        return row < col ? $"{r1}{r2}s" : $"{r1}{r2}o";
    }
}
