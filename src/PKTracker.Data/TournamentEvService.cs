using Npgsql;
using PKTracker.Engine;

namespace PKTracker.Data;

/// <summary>EV турнира: Chip EV всегда; $EV — только если известны выплаты (.ots).</summary>
public sealed record TournamentEv(long TournamentId, long ChipNet, long ChipEvNet, int AllInHands, double? DollarEvAdjustment, string? DollarMethod)
{
    /// <summary>Сколько фишек «подарила/отняла» удача в олл-инах (факт − EV).</summary>
    public long ChipLuck => ChipNet - ChipEvNet;
}

/// <summary>
/// $EV (§7.1, Фаза 12). Для одностоловых турниров (SNG, Spins) — ICM по стекам стола и выплатам из .ots:
/// поправка = ICM(стеки при EV-исходе) − ICM(фактические стеки) для Hero, по каждой олл-ин раздаче.
/// Для MTT ICM по одному столу неприменим — линейная оценка: фишка = призовой / (входов × стартовый стек).
/// </summary>
public sealed class TournamentEvService(TrackerDb db)
{
    private sealed class HandPlayers
    {
        public int HeroSeat;
        public bool HasEv;
        public int Level;
        public readonly List<(int Seat, long Start, long Net, long Ev)> Players = new();
    }

    public async Task<Dictionary<long, TournamentEv>> ComputeAsync(CancellationToken ct = default)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);

        // Все раздачи турниров с участием Hero: стеки и выигрыши всех игроков стола
        var byTournament = new Dictionary<long, (short Kind, Dictionary<long, HandPlayers> Hands)>();
        await using (var cmd = new NpgsqlCommand("""
            WITH heroes AS (
                SELECT coalesce(p.canonical_id, p.player_id) AS id
                  FROM player p JOIN hero_account h ON h.room_id = p.room_id AND h.nickname = p.nickname
            ), hh AS (
                SELECT DISTINCT hp.hand_pk FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
                 WHERE hp.player_id IN (SELECT id FROM heroes) AND h.game_kind <> 0
            )
            SELECT ti.tournament_id, h.game_kind, h.hand_pk, coalesce(h.tourney_level, 0), hp.seat,
                   hp.player_id IN (SELECT id FROM heroes), hp.start_stack, hp.net_won, hp.ev_net_won
              FROM hh JOIN hands h ON h.hand_pk = hh.hand_pk
              JOIN table_instance ti ON ti.table_instance_id = h.table_instance_id
              JOIN hand_players hp ON hp.hand_pk = h.hand_pk
             WHERE ti.tournament_id IS NOT NULL
             ORDER BY h.played_at, h.hand_pk
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                long tid = r.GetInt64(0);
                if (!byTournament.TryGetValue(tid, out var t))
                    byTournament[tid] = t = (r.GetInt16(1), new Dictionary<long, HandPlayers>());
                long pk = r.GetInt64(2);
                if (!t.Hands.TryGetValue(pk, out var hand)) t.Hands[pk] = hand = new HandPlayers { Level = r.GetInt32(3) };
                int seat = r.GetInt16(4);
                long net = r.GetInt64(7);
                long? ev = r.IsDBNull(8) ? null : r.GetInt64(8);
                if (r.GetBoolean(5)) hand.HeroSeat = seat;
                if (ev != null) hand.HasEv = true;
                hand.Players.Add((seat, r.GetInt64(6), net, ev ?? net));
            }
        }

        // Выплаты по местам и параметры турнира
        var payouts = new Dictionary<long, List<double>>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT tournament_id, finish_position, prize + ticket_value FROM tournament_result
             WHERE finish_position IS NOT NULL AND prize + ticket_value > 0 ORDER BY tournament_id, finish_position
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                long tid = r.GetInt64(0);
                if (!payouts.TryGetValue(tid, out var list)) payouts[tid] = list = new List<double>();
                int place = r.GetInt32(1);
                while (list.Count < place) list.Add(0);
                list[place - 1] += r.GetInt64(2);
            }
        }
        var meta = new Dictionary<long, (long? PrizePool, int? Players)>();
        await using (var cmd = new NpgsqlCommand("SELECT tournament_id, prize_pool, player_count FROM tournament", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                meta[r.GetInt64(0)] = (r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt32(2));
        }

        var result = new Dictionary<long, TournamentEv>();
        foreach (var (tid, (kind, hands)) in byTournament)
        {
            long chipNet = 0, chipEv = 0;
            int allIns = 0;
            foreach (var h in hands.Values)
            {
                var hero = h.Players.FirstOrDefault(p => p.Seat == h.HeroSeat);
                chipNet += hero.Net;
                chipEv += hero.Ev;
                if (h.HasEv && hero.Ev != hero.Net) allIns++;
            }

            double? dollar = null;
            string? method = null;
            // Winner-take-all (Spin & Go на 3 игрока — 100% первому): ICM там линеен по стекам,
            // то есть $EV — это тот же Chip EV в других единицах. Отдаём только фишки (§7.1).
            if (payouts.TryGetValue(tid, out var pay) && pay.Count(v => v > 0) > 1)
            {
                if (kind == 2)
                {
                    // Одностоловый турнир: все живые игроки — за этим столом, ICM корректен
                    dollar = 0;
                    method = "ICM";
                    foreach (var h in hands.Values.Where(x => x.HasEv))
                    {
                        if (h.Players.Count > Icm.MaxPlayers) { dollar = null; method = null; break; }
                        int heroIdx = h.Players.FindIndex(p => p.Seat == h.HeroSeat);
                        if (heroIdx < 0) continue;
                        var actual = h.Players.Select(p => (double)(p.Start + p.Net)).ToArray();
                        var expected = h.Players.Select(p => (double)(p.Start + p.Ev)).ToArray();
                        dollar += Icm.Equity(expected, pay)[heroIdx] - Icm.Equity(actual, pay)[heroIdx];
                    }
                }
                else if (meta.TryGetValue(tid, out var m) && m.Players is > 0)
                {
                    // MTT: стартовый стек — наибольший стек в раздачах первого уровня
                    long startStack = hands.Values.Where(x => x.Level == 1).SelectMany(x => x.Players).Select(p => p.Start).DefaultIfEmpty(0).Max();
                    double pool = m.PrizePool ?? pay.Sum();
                    if (startStack > 0)
                    {
                        dollar = (chipEv - chipNet) * pool / ((double)m.Players.Value * startStack);
                        method = "линейно";
                    }
                }
            }
            result[tid] = new TournamentEv(tid, chipNet, chipEv, allIns, dollar, method);
        }
        return result;
    }
}
