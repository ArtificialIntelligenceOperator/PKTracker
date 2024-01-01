using PKTracker.Core.Model;

namespace PKTracker.Engine;

/// <summary>Денежный инвариант (§15.2) и инварианты позиций (§15.3).</summary>
public static class Invariants
{
    public static List<string> Check(ParsedHand h, HandContext ctx)
    {
        var errors = new List<string>();

        long fees = h.Rake + h.TotalExtraFees;
        if (ctx.TotalInvested != h.TotalPot + fees)
            errors.Add($"money: invested {ctx.TotalInvested} != total_pot {h.TotalPot} + fees {fees}");
        if (ctx.TotalCollected != h.TotalPot)
            errors.Add($"money: collected {ctx.TotalCollected} != total_pot {h.TotalPot}");

        var dealt = ctx.Players.Where(p => p.Dealt).ToList();
        if (dealt.Count < 2) errors.Add($"dealt: only {dealt.Count} players");

        if (!h.Has(HandFlags.BombPot))
        {
            int bbCount = dealt.Count(p => p.Position == Position.BB);
            if (bbCount != 1) errors.Add($"positions: BB count {bbCount}");
            if (dealt.Count(p => p.Position == Position.SB) > 1) errors.Add("positions: >1 SB");
            if (dealt.Count(p => p.Position == Position.BTN) > 1) errors.Add("positions: >1 BTN");
            if (dealt.Any(p => p.Position == Position.None)) errors.Add("positions: unassigned");
            var dup = dealt.GroupBy(p => p.Position).FirstOrDefault(g => g.Count() > 1 && g.Key != Position.UTG);
            if (dup != null) errors.Add($"positions: duplicate {dup.Key}");
        }

        foreach (var p in ctx.Players)
            if (p.Invested < 0) errors.Add($"money: negative invested seat {p.Seat}");

        return errors;
    }
}
