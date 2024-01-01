using PKTracker.Core.Model;

namespace PKTracker.Engine;

/// <summary>
/// All-in EV (§15.5, Фаза 12): если после последнего олл-ина торговли больше нет, а карты всех оставшихся игроков
/// известны, выигрыш заменяется математическим ожиданием по всем раскладам оставшихся карт.
/// Два игрока или ≤ 200k раскладов — точный перебор; иначе Monte-Carlo с детерминированным seed (ребилд воспроизводим).
/// </summary>
public static class AllInEv
{
    public const int MonteCarloIterations = 50_000;
    private const long ExactLimit = 2_000_000;
    private const long ExactLimitMultiway = 200_000;

    /// <summary>Равенство (доля банка) каждого игрока при заданной известной части доски.</summary>
    public static double[] Equity(IReadOnlyList<(Card A, Card B)> hands, IReadOnlyList<Card> board, IEnumerable<Card>? dead = null, int? seed = null)
    {
        int n = hands.Count;
        var eligibleAll = new bool[1][];
        eligibleAll[0] = Enumerable.Repeat(true, n).ToArray();
        var shares = Shares(hands, board, dead ?? Array.Empty<Card>(), eligibleAll, seed ?? 12345);
        return shares[0];
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double[][]> PreflopCache = new();

    /// <summary>
    /// Для каждого банка (набор допущенных игроков) — средняя доля каждого игрока по всем раскладам.
    /// </summary>
    private static double[][] Shares(IReadOnlyList<(Card A, Card B)> hands, IReadOnlyList<Card> board, IEnumerable<Card> dead,
        bool[][] eligible, int seed)
    {
        var deadList = dead.ToList();
        // Префлоп без мёртвых карт: результат зависит только от матчапа с точностью до перестановки мастей — кешируем
        string? key = board.Count == 0 && deadList.Count == 0 ? CanonicalKey(hands, eligible) : null;
        if (key != null && PreflopCache.TryGetValue(key, out var cached)) return cached;

        int players = hands.Count;
        int need = 5 - board.Count;
        var used = new bool[52];
        foreach (var (a, b) in hands) { used[a.Value] = true; used[b.Value] = true; }
        foreach (var c in board) used[c.Value] = true;
        foreach (var c in deadList) used[c.Value] = true;
        var deck = new List<Card>(52);
        for (int i = 0; i < 52; i++) if (!used[i]) deck.Add(new Card((byte)i));

        long combos = Combinations(deck.Count, need);
        bool exact = need == 0 || combos <= (players == 2 ? ExactLimit : ExactLimitMultiway);
        double[][] result;
        if (exact && need == 5 && players == 2 && eligible.Length == 1 && eligible[0][0] && eligible[0][1])
            result = HeadsUpPreflop(hands, deck);
        else if (exact)
        {
            // Перебор параллелится по первой карте расклада
            int firsts = need == 0 ? 1 : deck.Count - need + 1;
            var partial = new (double[][] Sums, long Samples)[firsts];
            Parallel.For(0, firsts, first =>
            {
                var acc = new Accumulator(hands, board, eligible, need);
                if (need == 0) { acc.Score(); partial[first] = (acc.Sums, acc.Samples); return; }
                acc.Runout[0] = deck[first];
                int rest = need - 1;
                var idx = new int[rest];
                for (int i = 0; i < rest; i++) idx[i] = first + 1 + i;
                while (true)
                {
                    for (int i = 0; i < rest; i++) acc.Runout[1 + i] = deck[idx[i]];
                    acc.Score();
                    if (rest == 0) break;
                    int j = rest - 1;
                    while (j >= 0 && idx[j] == deck.Count - rest + j) j--;
                    if (j < 0) break;
                    idx[j]++;
                    for (int k = j + 1; k < rest; k++) idx[k] = idx[k - 1] + 1;
                }
                partial[first] = (acc.Sums, acc.Samples);
            });
            result = Merge(partial, eligible.Length, players);
        }
        else
        {
            var acc = new Accumulator(hands, board, eligible, need);
            var rng = new Random(seed);
            var pool = deck.ToArray();
            for (int it = 0; it < MonteCarloIterations; it++)
            {
                // частичная перетасовка Фишера–Йетса: первые need карт
                for (int k = 0; k < need; k++)
                {
                    int r = rng.Next(k, pool.Length);
                    (pool[k], pool[r]) = (pool[r], pool[k]);
                    acc.Runout[k] = pool[k];
                }
                acc.Score();
            }
            result = Merge(new[] { (acc.Sums, acc.Samples) }, eligible.Length, players);
        }
        if (key != null) PreflopCache[key] = result;
        return result;
    }

    /// <summary>
    /// Самый тяжёлый и самый частый случай — префлоп-олл-ин хедз-ап: 1,7 млн досок. Пять вложенных циклов
    /// с инкрементальной маской доски, параллельно по первой карте.
    /// </summary>
    private static double[][] HeadsUpPreflop(IReadOnlyList<(Card A, Card B)> hands, List<Card> deck)
    {
        long h0 = HandEvaluator.Pack(hands[0].A) | HandEvaluator.Pack(hands[0].B);
        long h1 = HandEvaluator.Pack(hands[1].A) | HandEvaluator.Pack(hands[1].B);
        var d = deck.Select(HandEvaluator.Pack).ToArray();
        int n = d.Length;
        var wins0 = new long[n];
        var wins1 = new long[n];
        var ties = new long[n];
        Parallel.For(0, n - 4, a =>
        {
            long w0 = 0, w1 = 0, t = 0;
            long pa = d[a];
            for (int b = a + 1; b < n - 3; b++)
            {
                long pb = pa | d[b];
                for (int c = b + 1; c < n - 2; c++)
                {
                    long pc = pb | d[c];
                    for (int e = c + 1; e < n - 1; e++)
                    {
                        long pe = pc | d[e];
                        for (int f = e + 1; f < n; f++)
                        {
                            long board = pe | d[f];
                            int v0 = HandEvaluator.EvaluatePacked(board | h0);
                            int v1 = HandEvaluator.EvaluatePacked(board | h1);
                            if (v0 > v1) w0++; else if (v1 > v0) w1++; else t++;
                        }
                    }
                }
            }
            wins0[a] = w0; wins1[a] = w1; ties[a] = t;
        });
        double total = wins0.Sum() + wins1.Sum() + ties.Sum();
        double tie = ties.Sum() / 2.0;
        return new[] { new[] { (wins0.Sum() + tie) / total, (wins1.Sum() + tie) / total } };
    }

    private static double[][] Merge((double[][] Sums, long Samples)[] parts, int pots, int players)
    {
        var res = new double[pots][];
        long samples = parts.Sum(p => p.Samples);
        for (int p = 0; p < pots; p++)
        {
            res[p] = new double[players];
            foreach (var part in parts)
                for (int i = 0; i < players; i++) res[p][i] += part.Sums[p][i];
            for (int i = 0; i < players; i++) res[p][i] /= samples;
        }
        return res;
    }

    /// <summary>Ключ матчапа, инвариантный к перестановке мастей.</summary>
    private static string CanonicalKey(IReadOnlyList<(Card A, Card B)> hands, bool[][] eligible)
    {
        var map = new int[] { -1, -1, -1, -1 };
        int next = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var (a, b) in hands)
        {
            var (x, y) = a.Rank > b.Rank || (a.Rank == b.Rank && a.Suit < b.Suit) ? (a, b) : (b, a);
            foreach (var c in new[] { x, y })
            {
                if (map[c.Suit] < 0) map[c.Suit] = next++;
                sb.Append(Card.Ranks[c.Rank]).Append(map[c.Suit]);
            }
            sb.Append('|');
        }
        foreach (var e in eligible) { foreach (var v in e) sb.Append(v ? '1' : '0'); sb.Append(';'); }
        return sb.ToString();
    }

    private sealed class Accumulator
    {
        private readonly long[] _holes;
        private readonly long _board;
        private readonly bool[][] _eligible;
        private readonly int _need;
        private readonly int[] _values;
        public readonly Card[] Runout;
        public readonly double[][] Sums;
        public long Samples;

        public Accumulator(IReadOnlyList<(Card A, Card B)> hands, IReadOnlyList<Card> board, bool[][] eligible, int need)
        {
            _holes = hands.Select(h => HandEvaluator.Pack(h.A) | HandEvaluator.Pack(h.B)).ToArray();
            foreach (var c in board) _board |= HandEvaluator.Pack(c);
            _eligible = eligible;
            _need = need;
            Runout = new Card[need];
            _values = new int[hands.Count];
            Sums = eligible.Select(_ => new double[hands.Count]).ToArray();
        }

        public void Score()
        {
            long b = _board;
            for (int k = 0; k < _need; k++) b |= HandEvaluator.Pack(Runout[k]);
            var holes = _holes;
            var values = _values;
            int players = holes.Length;
            for (int i = 0; i < players; i++) values[i] = HandEvaluator.EvaluatePacked(b | holes[i]);

            if (players == 2 && _eligible.Length == 1)
            {
                // частый случай: хедз-ап, один банк
                var s = Sums[0];
                if (values[0] > values[1]) s[0] += 1;
                else if (values[1] > values[0]) s[1] += 1;
                else { s[0] += 0.5; s[1] += 0.5; }
                Samples++;
                return;
            }
            for (int p = 0; p < _eligible.Length; p++)
            {
                int best = int.MinValue, winners = 0;
                var elig = _eligible[p];
                for (int i = 0; i < players; i++)
                {
                    if (!elig[i]) continue;
                    if (values[i] > best) { best = values[i]; winners = 1; }
                    else if (values[i] == best) winners++;
                }
                double share = 1.0 / winners;
                var sums = Sums[p];
                for (int i = 0; i < players; i++)
                    if (elig[i] && values[i] == best) sums[i] += share;
            }
            Samples++;
        }
    }

    private static long Combinations(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        long r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }

    /// <summary>
    /// Считает EV-выигрыш и записывает его в <see cref="HandPlayerFacts.EvNetWon"/>. Возвращает true, если EV применим.
    /// </summary>
    public static bool Apply(ParsedHand h, HandContext ctx)
    {
        if (ctx.Decisions.Count == 0) return false;
        var folded = new HashSet<byte>(ctx.Decisions.Where(d => d.Chosen == Choice.Fold).Select(d => d.Seat));
        var live = ctx.Players.Where(p => p.Dealt && !folded.Contains(p.Seat)).ToList();
        if (live.Count < 2) return false;
        if (live.Count(p => !p.AllIn) > 1) return false;          // торговля могла продолжиться
        if (!live.Any(p => p.AllIn)) return false;
        if (live.Any(p => !p.CardsKnown)) return false;

        var lastStreet = ctx.Decisions[^1].Street;
        int known = lastStreet switch { Street.Preflop => 0, Street.Flop => 3, Street.Turn => 4, _ => 5 };
        if (known == 5) return false;                              // олл-ин на ривере: EV == факт
        if (h.Boards.Count == 0 && known > 0) return false;
        var board = known == 0 ? new List<Card>() : h.Boards[0].Take(known).ToList();
        if (board.Count != known) return false;

        // Банки по уровням вложений оставшихся игроков; деньги сфолдивших — мёртвые в банках
        var levels = live.Select(p => p.Invested).Distinct().OrderBy(x => x).ToList();
        var pots = new List<(long Amount, bool[] Eligible)>();
        long prev = 0;
        foreach (var level in levels)
        {
            long amount = 0;
            foreach (var p in ctx.Players) amount += Math.Min(p.Invested, level) - Math.Min(p.Invested, prev);
            var elig = live.Select(p => p.Invested >= level).ToArray();
            if (amount > 0) pots.Add((amount, elig));
            prev = level;
        }
        // Остаток (деньги сфолдивших сверх максимального уровня) — в последний банк
        long totalInvested = ctx.Players.Sum(p => p.Invested);
        long assigned = pots.Sum(p => p.Amount);
        if (pots.Count == 0) return false;
        if (assigned < totalInvested) pots[^1] = (pots[^1].Amount + totalInvested - assigned, pots[^1].Eligible);

        var hands = live.Select(p => (p.HoleCard1!.Value, p.HoleCard2!.Value)).ToList();
        var dead = ctx.Players.Where(p => folded.Contains(p.Seat) && p.CardsKnown)
            .SelectMany(p => new[] { p.HoleCard1!.Value, p.HoleCard2!.Value });
        int seed = (int)(h.ExternalHandId ^ (h.ExternalHandId >> 32));
        var shares = Shares(hands, board, dead, pots.Select(p => p.Eligible).ToArray(), seed);

        // Рейк и сборы распределяются пропорционально: фактический банк / вложено
        double factor = totalInvested > 0 ? (double)h.TotalPot / totalInvested : 1;
        foreach (var p in ctx.Players) p.EvNetWon = p.NetWon;
        for (int i = 0; i < live.Count; i++)
        {
            double ev = 0;
            for (int k = 0; k < pots.Count; k++) ev += shares[k][i] * pots[k].Amount;
            live[i].EvNetWon = (long)Math.Round(ev * factor) - live[i].Invested;
        }
        return true;
    }
}
