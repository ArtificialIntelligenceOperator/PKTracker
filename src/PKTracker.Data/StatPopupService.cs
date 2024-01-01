using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Engine.Stats;

namespace PKTracker.Data;

public sealed record CountRow(string Key, long Count, double Pct);

public sealed record PopupHand(long HandPk, DateTime PlayedAt, string Street, Position Position, string Action, string? Cards, short? SizePct);

/// <summary>Содержимое попапа стата (§5.4).</summary>
public sealed class StatPopupData
{
    public long Cases { get; set; }
    public long Hits { get; set; }
    public List<BreakdownRow> ByPosition { get; } = new();
    public List<BreakdownRow> ByStack { get; } = new();
    /// <summary>Что игрок делает на следующем решении после срабатывания стата.</summary>
    public List<CountRow> NextActions { get; } = new();
    public List<CountRow> BetSizing { get; } = new();
    public List<CountRow> HandStrength { get; } = new();
    public List<CountRow> Boards { get; } = new();
    /// <summary>13×13: сколько раз в спот (hits) показана каждая рука — только вскрытые карты (§7 Range Research).</summary>
    public long[] RangeHits { get; } = new long[169];
    public long KnownHands { get; set; }
    public List<PopupHand> Hands { get; } = new();
    public int Scanned { get; set; }

    /// <summary>
    /// 13×13 по каждому выбранному действию в этом споте — вкладки «Рейз», «Колл», «Фолд».
    /// Считается по всем возможностям, а не только по срабатываниям: смысл вкладок в том,
    /// чтобы рядом лежали диапазон рейза и диапазон колла в одной и той же ситуации.
    /// </summary>
    public Dictionary<Choice, long[]> RangeByAction { get; } = new();

    public long[] RangeOf(Choice c) => RangeByAction.TryGetValue(c, out var a) ? a : RangeByAction[c] = new long[169];

    // ── исходы раздач, где стат сработал ───────────────────────────────────
    // Считаются по раздачам, а не по решениям: стат может сработать в одной раздаче дважды,
    // и тогда её результат попал бы в сумму два раза.

    public long HitHands { get; set; }
    public long HitSawFlop { get; set; }
    public long HitShowdown { get; set; }
    public long HitWonShowdown { get; set; }
    public long HitWonHand { get; set; }
    public long HitNetBbX100 { get; set; }

    /// <summary>Сколько bb в среднем приносит раздача, в которой стат сработал.</summary>
    public double? ActionProfitBb => HitHands == 0 ? null : HitNetBbX100 / 100.0 / HitHands;
    public double? WonHandPct => HitHands == 0 ? null : HitWonHand * 100.0 / HitHands;
    public double? WtsdPct => HitSawFlop == 0 ? null : HitShowdown * 100.0 / HitSawFlop;
    public double? WsdPct => HitShowdown == 0 ? null : HitWonShowdown * 100.0 / HitShowdown;

    /// <summary>Учесть исход раздачи один раз, каким бы числом решений стат в ней ни сработал.</summary>
    public void AddOutcome(FactBits bits, int netBbX100)
    {
        HitHands++;
        if ((bits & FactBits.SawFlop) != 0) HitSawFlop++;
        if ((bits & FactBits.SawShowdown) != 0) HitShowdown++;
        if ((bits & FactBits.WonAtShowdown) != 0) HitWonShowdown++;
        if ((bits & FactBits.WonHand) != 0) HitWonHand++;
        HitNetBbX100 += netBbX100;
    }
}

/// <summary>
/// Попапы ходят в normalized (hand_decisions / hand_players), а не в агрегаты — поэтому разбивки любые,
/// а player_stat_agg не раздувается измерениями (§5.4). Условия — те же скомпилированные делегаты, что и в агрегатах.
/// </summary>
public sealed class StatPopupService(TrackerDb db)
{
    public const int MaxRows = 50_000;

    private static readonly string[] StackNames = { "<10bb", "10–20bb", "20–40bb", "40–100bb", "100bb+" };

    public async Task<StatPopupData> LoadAsync(long playerId, CompiledStat stat, GameKind kind, CancellationToken ct = default)
    {
        short[] kinds = kind == GameKind.Cash ? new short[] { 0 } : new short[] { 1, 2 };
        return stat.Scope == StatScope.Hand
            ? await LoadHandStatAsync(playerId, stat, kinds, ct)
            : await LoadDecisionStatAsync(playerId, stat, kinds, ct);
    }

    private static HandInfo Info(NpgsqlDataReader r, int o) => new()
    {
        GameKind = (GameKind)r.GetInt16(o), Flags = (HandFlags)r.GetInt32(o + 1), PlayersDealt = r.GetInt16(o + 2),
        MaxSeats = r.GetInt16(o + 3), SmallBlind = r.GetInt64(o + 4), BigBlind = r.GetInt64(o + 5), Ante = r.GetInt64(o + 6),
    };

    private async Task<StatPopupData> LoadDecisionStatAsync(long playerId, CompiledStat stat, short[] kinds, CancellationToken ct)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT d.hand_pk, d.seq, d.seat, d.street, d.position, d.facing, d.bet_level, d.my_last_level, d.to_call, d.pot_before,
                   d.eff_stack, d.players_in, d.players_behind, d.num_limpers, d.num_callers, d.ctx_bits, d.chosen, d.chosen_amount,
                   d.size_pct, d.is_all_in, d.hand_strength, d.board_texture, d.stack_bucket,
                   d.vs_position, d.opener_position, d.to_amount, d.line_bits, d.facing_amount,
                   h.game_kind, h.flags, h.players_dealt, h.max_seats, h.sb, h.bb, h.ante,
                   h.played_at, hp.hole_card1, hp.hole_card2, hp.hole_class, hp.fact_bits, hp.net_won_bb_x100
              FROM hand_decisions d
              JOIN hands h ON h.hand_pk = d.hand_pk
              LEFT JOIN hand_players hp ON hp.hand_pk = d.hand_pk AND hp.seat = d.seat
             WHERE d.player_id = $1 AND h.game_kind = ANY($2)
             ORDER BY d.hand_pk DESC, d.seq
             LIMIT $3
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue(kinds);
        cmd.Parameters.AddWithValue(MaxRows);

        var rows = new List<(DecisionPoint Dp, long HandPk, DateTime At, short? C1, short? C2, short? Cls, FactBits Bits, int NetBb)>();
        var infos = new Dictionary<long, HandInfo>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                long pk = r.GetInt64(0);
                if (!infos.TryGetValue(pk, out var info)) infos[pk] = info = Info(r, 28);
                var dp = new DecisionPoint
                {
                    Hand = info, Seq = r.GetInt16(1), Seat = (byte)r.GetInt16(2), PlayerName = "",
                    Street = (Street)r.GetInt16(3), Position = (Position)r.GetInt16(4), Facing = (Facing)r.GetInt16(5),
                    BetLevel = r.GetInt16(6), MyLastLevel = r.GetInt16(7), ToCall = r.GetInt64(8), PotBefore = r.GetInt64(9),
                    EffStack = r.GetInt64(10), PlayersIn = r.GetInt16(11), PlayersBehind = r.GetInt16(12),
                    NumLimpers = r.GetInt16(13), NumCallers = r.GetInt16(14), Ctx = (CtxBits)r.GetInt64(15),
                    Chosen = (Choice)r.GetInt16(16), ChosenAmount = r.GetInt64(17),
                    SizePct = r.IsDBNull(18) ? null : r.GetInt16(18), IsAllIn = r.GetBoolean(19),
                    HandStrength = r.IsDBNull(20) ? null : r.GetInt16(20), BoardTexture = r.IsDBNull(21) ? (short)0 : r.GetInt16(21),
                    StackBucket = (byte)r.GetInt16(22),
                    VsPosition = (Position)r.GetInt16(23), OpenerPosition = (Position)r.GetInt16(24), ToAmount = r.GetInt64(25),
                    LineBits = r.GetInt16(26), FacingAmount = r.GetInt64(27),
                };
                rows.Add((dp, pk, r.GetDateTime(35), r.IsDBNull(36) ? null : r.GetInt16(36), r.IsDBNull(37) ? null : r.GetInt16(37),
                    r.IsDBNull(38) ? null : r.GetInt16(38), r.IsDBNull(39) ? 0 : (FactBits)r.GetInt64(39),
                    r.IsDBNull(40) ? 0 : r.GetInt32(40)));
            }
        }

        var byPos = new SortedDictionary<Position, (long C, long H)>();
        var byStack = new SortedDictionary<byte, (long C, long H)>();
        var next = new Dictionary<string, long>();
        var sizing = new Dictionary<string, long>();
        var strength = new Dictionary<string, long>();
        var boards = new Dictionary<string, long>();
        long cases = 0, hits = 0, known = 0;
        long lastOutcomeHand = -1;
        var data = new StatPopupData { Scanned = rows.Count };

        for (int i = 0; i < rows.Count; i++)
        {
            var (dp, pk, at, c1, c2, cls, bits, netBb) = rows[i];
            if (!stat.DecisionOpportunity!(dp)) continue;
            cases++;
            // Диапазон по действию — по всем возможностям: рядом должны лежать «чем рейзил»
            // и «чем коллировал» в одном и том же споте.
            if (cls is { } ck) data.RangeOf(dp.Chosen)[ck]++;
            bool hit = stat.DecisionHit!(dp);
            var pc = byPos.GetValueOrDefault(dp.Position);
            byPos[dp.Position] = (pc.C + 1, pc.H + (hit ? 1 : 0));
            var sc = byStack.GetValueOrDefault(dp.StackBucket);
            byStack[dp.StackBucket] = (sc.C + 1, sc.H + (hit ? 1 : 0));
            if (!hit) continue;
            hits++;
            if (pk != lastOutcomeHand) { data.AddOutcome(bits, netBb); lastOutcomeHand = pk; }

            // Следующее решение этого же игрока в этой же раздаче
            if (i + 1 < rows.Count && rows[i + 1].HandPk == pk)
            {
                var n = rows[i + 1].Dp;
                string facing = n.Facing switch { Facing.None => "без ставки", Facing.Bet => "против ставки", _ => "против рейза" };
                Inc(next, $"{n.Street}, {facing}: {n.Chosen}");
            }
            else Inc(next, "решений больше нет");

            if (dp.SizePct is { } size) Inc(sizing, SizeBucket(size));
            if (dp.HandStrength is { } hs) Inc(strength, ((HandCategory)hs).ToString());
            if (dp.Street != Street.Preflop) foreach (var t in TextureNames(dp.BoardTexture)) Inc(boards, t);
            if (cls is { } k) { data.RangeHits[k]++; known++; }

            if (data.Hands.Count < 100)
            {
                string? cards = c1 is { } a && c2 is { } b ? $"{new Card((byte)a)} {new Card((byte)b)}" : null;
                data.Hands.Add(new PopupHand(pk, at, dp.Street.ToString(), dp.Position, dp.Chosen.ToString(), cards, dp.SizePct));
            }
        }

        data.KnownHands = known;
        data.Cases = cases;
        data.Hits = hits;
        foreach (var (p, v) in byPos) data.ByPosition.Add(new BreakdownRow(p.ToString(), v.C, v.H));
        foreach (var (s, v) in byStack) data.ByStack.Add(new BreakdownRow(s < StackNames.Length ? StackNames[s] : s.ToString(), v.C, v.H));
        Fill(data.NextActions, next, hits);
        Fill(data.BetSizing, sizing, hits, ordered: true);
        Fill(data.HandStrength, strength, strength.Values.Sum());
        Fill(data.Boards, boards, hits);
        return data;
    }

    private async Task<StatPopupData> LoadHandStatAsync(long playerId, CompiledStat stat, short[] kinds, CancellationToken ct)
    {
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT hp.hand_pk, hp.seat, hp.position, hp.start_stack, hp.stack_bb_x10, hp.hole_card1, hp.hole_card2, hp.hole_class,
                   hp.invested, hp.collected, hp.rake_share, hp.fact_bits, hp.ev_net_won,
                   h.game_kind, h.flags, h.players_dealt, h.max_seats, h.sb, h.bb, h.ante, h.played_at
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE hp.player_id = $1 AND h.game_kind = ANY($2)
             ORDER BY hp.hand_pk DESC
             LIMIT $3
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue(kinds);
        cmd.Parameters.AddWithValue(MaxRows);
        var data = new StatPopupData();
        var byPos = new SortedDictionary<Position, (long C, long H)>();
        var byStack = new SortedDictionary<byte, (long C, long H)>();
        long cases = 0, hits = 0, known = 0;
        int scanned = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                scanned++;
                var info = Info(r, 13);
                var f = new HandPlayerFacts
                {
                    Hand = info, Seat = (byte)r.GetInt16(1), PlayerName = "", Position = (Position)r.GetInt16(2),
                    StartStack = r.GetInt64(3), StackBbX10 = r.GetInt16(4), StackBucket = HandContextBuilder.StackBucket(r.GetInt64(3), info.BigBlind),
                    HoleCard1 = r.IsDBNull(5) ? null : new Card((byte)r.GetInt16(5)), HoleCard2 = r.IsDBNull(6) ? null : new Card((byte)r.GetInt16(6)),
                    HoleClass = r.IsDBNull(7) ? null : r.GetInt16(7), Invested = r.GetInt64(8), Collected = r.GetInt64(9),
                    RakeShare = r.GetInt64(10), Bits = (FactBits)r.GetInt64(11), EvNetWon = r.IsDBNull(12) ? null : r.GetInt64(12),
                };
                if (!stat.HandOpportunity!(f)) continue;
                cases++;
                bool hit = stat.HandHit!(f);
                var pc = byPos.GetValueOrDefault(f.Position);
                byPos[f.Position] = (pc.C + 1, pc.H + (hit ? 1 : 0));
                var sc = byStack.GetValueOrDefault(f.StackBucket);
                byStack[f.StackBucket] = (sc.C + 1, sc.H + (hit ? 1 : 0));
                if (!hit) continue;
                hits++;
                data.AddOutcome(f.Bits, f.NetWonBbX100);
                if (f.HoleClass is { } k) { data.RangeHits[k]++; known++; }
                if (data.Hands.Count < 100)
                {
                    string? cards = f.HoleCard1 is { } a && f.HoleCard2 is { } b ? $"{a} {b}" : null;
                    data.Hands.Add(new PopupHand(r.GetInt64(0), r.GetDateTime(20), "", f.Position, f.WonHand ? "выиграл" : "", cards, null));
                }
            }
        }
        foreach (var (p, v) in byPos) data.ByPosition.Add(new BreakdownRow(p.ToString(), v.C, v.H));
        foreach (var (s, v) in byStack) data.ByStack.Add(new BreakdownRow(s < StackNames.Length ? StackNames[s] : s.ToString(), v.C, v.H));
        data.Cases = cases;
        data.Hits = hits;
        data.Scanned = scanned;
        data.KnownHands = known;
        return data;
    }

    private static void Inc(Dictionary<string, long> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

    private static readonly string[] SizeOrder = { "< 33%", "33–50%", "50–75%", "75–100%", "100–150%", "150%+" };

    private static string SizeBucket(short pct) => pct switch
    {
        < 33 => SizeOrder[0], < 50 => SizeOrder[1], < 75 => SizeOrder[2], < 100 => SizeOrder[3], < 150 => SizeOrder[4], _ => SizeOrder[5],
    };

    private static IEnumerable<string> TextureNames(short tex)
    {
        var t = (HandEvaluator.Texture)tex;
        if (t.HasFlag(HandEvaluator.Texture.Paired)) yield return "Спаренный";
        if (t.HasFlag(HandEvaluator.Texture.Monotone)) yield return "Монотонный";
        if (t.HasFlag(HandEvaluator.Texture.FlushPossible)) yield return "3+ одной масти";
        if (t.HasFlag(HandEvaluator.Texture.TwoTone)) yield return "Две масти";
        if (t.HasFlag(HandEvaluator.Texture.Rainbow)) yield return "Радуга";
        if (t.HasFlag(HandEvaluator.Texture.StraightPossible)) yield return "Стритовый";
        if (t.HasFlag(HandEvaluator.Texture.HighBroadway)) yield return "Старший (Q+)";
    }

    private static void Fill(List<CountRow> target, Dictionary<string, long> src, long total, bool ordered = false)
    {
        IEnumerable<KeyValuePair<string, long>> items = ordered
            ? src.OrderBy(kv => Array.IndexOf(SizeOrder, kv.Key))
            : src.OrderByDescending(kv => kv.Value);
        foreach (var (k, v) in items) target.Add(new CountRow(k, v, total > 0 ? v * 100.0 / total : 0));
    }
}
