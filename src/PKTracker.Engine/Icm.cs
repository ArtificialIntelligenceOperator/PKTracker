namespace PKTracker.Engine;

/// <summary>
/// ICM по модели Malmuth-Harville (§7.1, Фаза 12): вероятность занять место пропорциональна стеку среди оставшихся.
/// Точный перебор порядков финиша — годится для финальных столов и одностоловых турниров (≤ 10 игроков).
/// </summary>
public static class Icm
{
    public const int MaxPlayers = 10;

    /// <param name="stacks">Стеки игроков (выбывшие — 0).</param>
    /// <param name="payouts">Выплаты по местам: [0] — первое место.</param>
    /// <returns>Денежное эквити каждого игрока.</returns>
    public static double[] Equity(IReadOnlyList<double> stacks, IReadOnlyList<double> payouts)
    {
        int n = stacks.Count;
        var eq = new double[n];
        if (n == 0 || payouts.Count == 0) return eq;
        if (n > MaxPlayers) throw new ArgumentException($"ICM поддерживает не более {MaxPlayers} игроков");

        // Игроки с нулевым стеком уже выбыли: призы за места ниже живых им не достаются в этой модели
        int alive = 0;
        foreach (var s in stacks) if (s > 0) alive++;
        int places = Math.Min(payouts.Count, alive);
        var stack = stacks.Select(s => Math.Max(0, s)).ToArray();

        void Recurse(int mask, double remaining, int place, double prob)
        {
            if (place >= places || prob < 1e-15) return;
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) != 0 || stack[i] <= 0) continue;
                double p = prob * stack[i] / remaining;
                eq[i] += p * payouts[place];
                Recurse(mask | (1 << i), remaining - stack[i], place + 1, p);
            }
        }

        double total = stack.Sum();
        if (total <= 0) return eq;
        Recurse(0, total, 0, 1.0);
        return eq;
    }
}
