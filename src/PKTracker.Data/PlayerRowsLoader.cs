using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Engine.Hud;

namespace PKTracker.Data;

/// <summary>
/// Читает «срез» игрока из normalized-таблиц для попапов (§5.4).
///
/// Агрегаты для попапов не годятся: там всего три измерения, а ячейки раскладки режут данные по позиции
/// оппонента, точным полосам стека и числу игроков в раздаче. Зато срез читается один раз на игрока
/// и обслуживает все вкладки сразу.
/// </summary>
public sealed class PlayerRowsLoader(TrackerDb db)
{
    /// <summary>Потолок строк решений: у регуляра с сотней тысяч рук иначе уедет и память, и время.</summary>
    public const int MaxDecisions = 200_000;
    public const int MaxHands = 200_000;

    public async Task<PlayerRows> LoadAsync(long playerId, GameKind kind, CancellationToken ct = default)
    {
        short[] kinds = kind == GameKind.Cash ? new short[] { 0 } : new short[] { 1, 2 };
        await using var conn = await db.Reports.OpenConnectionAsync(ct);
        var infos = new Dictionary<long, HandInfo>();
        var facts = await LoadFactsAsync(conn, playerId, kinds, infos, ct);
        var decisions = await LoadDecisionsAsync(conn, playerId, kinds, infos, ct);
        return new PlayerRows
        {
            Decisions = decisions,
            Facts = facts,
            ScannedDecisions = decisions.Count,
            Truncated = decisions.Count >= MaxDecisions || facts.Count >= MaxHands,
        };
    }

    private static HandInfo Info(NpgsqlDataReader r, int o) => new()
    {
        GameKind = (GameKind)r.GetInt16(o), Flags = (HandFlags)r.GetInt32(o + 1), PlayersDealt = r.GetInt16(o + 2),
        MaxSeats = r.GetInt16(o + 3), SmallBlind = r.GetInt64(o + 4), BigBlind = r.GetInt64(o + 5), Ante = r.GetInt64(o + 6),
    };

    private static async Task<List<HandPlayerFacts>> LoadFactsAsync(
        NpgsqlConnection conn, long playerId, short[] kinds, Dictionary<long, HandInfo> infos, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT hp.hand_pk, hp.seat, hp.position, hp.start_stack, hp.stack_bb_x10, hp.hole_card1, hp.hole_card2, hp.hole_class,
                   hp.invested, hp.collected, hp.rake_share, hp.fact_bits, hp.ev_net_won,
                   h.game_kind, h.flags, h.players_dealt, h.max_seats, h.sb, h.bb, h.ante
              FROM hand_players hp JOIN hands h ON h.hand_pk = hp.hand_pk
             WHERE hp.player_id = $1 AND h.game_kind = ANY($2)
             ORDER BY hp.hand_pk DESC
             LIMIT $3
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue(kinds);
        cmd.Parameters.AddWithValue(MaxHands);
        var list = new List<HandPlayerFacts>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            long pk = r.GetInt64(0);
            if (!infos.TryGetValue(pk, out var info)) infos[pk] = info = Info(r, 13);
            list.Add(new HandPlayerFacts
            {
                Hand = info, Seat = (byte)r.GetInt16(1), PlayerName = "", Position = (Position)r.GetInt16(2),
                StartStack = r.GetInt64(3), StackBbX10 = r.GetInt16(4),
                StackBucket = HandContextBuilder.StackBucket(r.GetInt64(3), info.BigBlind),
                HoleCard1 = r.IsDBNull(5) ? null : new Card((byte)r.GetInt16(5)),
                HoleCard2 = r.IsDBNull(6) ? null : new Card((byte)r.GetInt16(6)),
                HoleClass = r.IsDBNull(7) ? null : r.GetInt16(7), Invested = r.GetInt64(8), Collected = r.GetInt64(9),
                RakeShare = r.GetInt64(10), Bits = (FactBits)r.GetInt64(11),
                EvNetWon = r.IsDBNull(12) ? null : r.GetInt64(12),
            });
        }
        return list;
    }

    private static async Task<List<DecisionPoint>> LoadDecisionsAsync(
        NpgsqlConnection conn, long playerId, short[] kinds, Dictionary<long, HandInfo> infos, CancellationToken ct)
    {
        // fact_bits игрока — исход раздачи (DecisionPoint.Outcome): отдельной колонки у решения нет.
        await using var cmd = new NpgsqlCommand($"""
            SELECT d.hand_pk, d.seq, d.seat, d.street, d.position, d.facing, d.bet_level, d.my_last_level, d.to_call, d.pot_before,
                   d.eff_stack, d.players_in, d.players_behind, d.num_limpers, d.num_callers, d.ctx_bits, d.chosen, d.chosen_amount,
                   d.size_pct, d.is_all_in, d.hand_strength, d.board_texture, d.stack_bucket,
                   d.vs_position, d.opener_position, d.to_amount, d.line_bits, d.facing_amount,
                   h.game_kind, h.flags, h.players_dealt, h.max_seats, h.sb, h.bb, h.ante,
                   {DecisionColumns.Select("d")}, hp.fact_bits
              FROM hand_decisions d JOIN hands h ON h.hand_pk = d.hand_pk
              LEFT JOIN hand_players hp ON hp.hand_pk = d.hand_pk AND hp.seat = d.seat
             WHERE d.player_id = $1 AND h.game_kind = ANY($2)
             ORDER BY d.hand_pk DESC, d.seq
             LIMIT $3
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue(kinds);
        cmd.Parameters.AddWithValue(MaxDecisions);
        var list = new List<DecisionPoint>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            long pk = r.GetInt64(0);
            if (!infos.TryGetValue(pk, out var info)) infos[pk] = info = Info(r, 28);
            var x = DecisionColumns.Read(r, 35);
            list.Add(new DecisionPoint
            {
                Hand = info, Seq = r.GetInt16(1), Seat = (byte)r.GetInt16(2), PlayerName = "",
                Street = (Street)r.GetInt16(3), Position = (Position)r.GetInt16(4), Facing = (Facing)r.GetInt16(5),
                BetLevel = r.GetInt16(6), MyLastLevel = r.GetInt16(7), ToCall = r.GetInt64(8), PotBefore = r.GetInt64(9),
                EffStack = r.GetInt64(10), PlayersIn = r.GetInt16(11), PlayersBehind = r.GetInt16(12),
                NumLimpers = r.GetInt16(13), NumCallers = r.GetInt16(14), Ctx = (CtxBits)r.GetInt64(15),
                Chosen = (Choice)r.GetInt16(16), ChosenAmount = r.GetInt64(17),
                SizePct = r.IsDBNull(18) ? null : r.GetInt16(18), IsAllIn = r.GetBoolean(19),
                HandStrength = r.IsDBNull(20) ? null : r.GetInt16(20),
                BoardTexture = r.IsDBNull(21) ? (short)0 : r.GetInt16(21), StackBucket = (byte)r.GetInt16(22),
                VsPosition = (Position)r.GetInt16(23), OpenerPosition = (Position)r.GetInt16(24), ToAmount = r.GetInt64(25),
                LineBits = r.GetInt16(26), FacingAmount = r.GetInt64(27),
                PfaLineBits = x.PfaLine, OppLineBits = x.OppLine, PotRaises = x.PotRaises, PfLimpers = x.PfLimpers,
                PfrPosition = x.PfrPos, FlopPlayers = x.FlopPlayers, FlopMask = x.FlopMask, VsStack = x.VsStack,
                StartEffStack = x.StartEff, CallersBefore3b = x.CallersBefore3b, MadeHandBits = x.MadeHand,
                Outcome = r.IsDBNull(35 + DecisionColumns.Count) ? 0 : (FactBits)r.GetInt64(35 + DecisionColumns.Count),
            });
        }
        return list;
    }
}
