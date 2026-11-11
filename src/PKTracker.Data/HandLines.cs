using Npgsql;
using PKTracker.Core.Model;

namespace PKTracker.Data;

/// <summary>Одно действие в «линии» раздачи: «R3», «C2», «X», «B3.4», «F».</summary>
/// <param name="Focus">Действие игрока, по которому открыт список (выделяется подчёркиванием).</param>
/// <param name="Actor">Номер игрока в раздаче по порядку появления — для цвета: один игрок — один цвет на всю раздачу.</param>
public sealed record LineAction(string Text, bool Focus, int Actor);

/// <summary>Улица раздачи: карты борда, банк к её началу (в bb) и действия.</summary>
public sealed record LineStreet(IReadOnlyList<Card> Cards, double PotBb, IReadOnlyList<LineAction> Actions);

/// <summary>
/// Раздача одной строкой, как в списках рук Hand2Note: карты игрока, затем по улицам — борд, банк и
/// действия буквами (X чек, B ставка, C колл, R рейз, F фолд) с размером в больших блайндах.
/// Так линию розыгрыша видно сразу, без открытия реплеера.
/// </summary>
public sealed record HandLine(long HandPk, DateTime PlayedAt, Card? Hole1, Card? Hole2, IReadOnlyList<LineStreet> Streets,
    double NetBb, string Stakes);

public static class HandLineLoader
{
    /// <summary>Линии раздач по списку hand_pk для игрока <paramref name="playerId"/>. Порядок — как в <paramref name="handPks"/>.</summary>
    public static async Task<Dictionary<long, HandLine>> LoadAsync(NpgsqlConnection conn, IReadOnlyCollection<long> handPks, long playerId,
        CancellationToken ct = default)
    {
        var res = new Dictionary<long, HandLine>();
        if (handPks.Count == 0) return res;
        var pks = handPks.Distinct().ToArray();

        var head = new Dictionary<long, (DateTime At, long Bb, long Sb, GameKind Kind, int Seat, Card? C1, Card? C2, int NetBb)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT h.hand_pk, h.played_at, h.bb, h.sb, h.game_kind, hp.seat, hp.hole_card1, hp.hole_card2, hp.net_won_bb_x100
              FROM hands h JOIN hand_players hp ON hp.hand_pk = h.hand_pk AND hp.player_id = $2
             WHERE h.hand_pk = ANY($1)
            """, conn))
        {
            cmd.Parameters.AddWithValue(pks);
            cmd.Parameters.AddWithValue(playerId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                head[r.GetInt64(0)] = (r.GetDateTime(1), r.GetInt64(2), r.GetInt64(3), (GameKind)r.GetInt16(4), r.GetInt16(5),
                    r.IsDBNull(6) ? null : new Card((byte)r.GetInt16(6)), r.IsDBNull(7) ? null : new Card((byte)r.GetInt16(7)), r.GetInt32(8));
        }

        var boards = new Dictionary<long, List<Card>>();
        await using (var cmd = new NpgsqlCommand("SELECT hand_pk, c1, c2, c3, c4, c5 FROM hand_boards WHERE hand_pk = ANY($1) AND board_no = 1", conn))
        {
            cmd.Parameters.AddWithValue(pks);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var cards = new List<Card>(5);
                for (int i = 1; i <= 5; i++) if (!r.IsDBNull(i)) cards.Add(new Card((byte)r.GetInt16(i)));
                boards[r.GetInt64(0)] = cards;
            }
        }

        var actions = new Dictionary<long, List<(Street Street, int Seat, ActionType Type, long Amount, long To, long Pot)>>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT hand_pk, street, seat, action, amount, to_amount, pot_before
              FROM hand_actions WHERE hand_pk = ANY($1) ORDER BY hand_pk, seq
            """, conn))
        {
            cmd.Parameters.AddWithValue(pks);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long pk = r.GetInt64(0);
                if (!actions.TryGetValue(pk, out var list)) actions[pk] = list = new();
                list.Add(((Street)r.GetInt16(1), r.GetInt16(2), (ActionType)r.GetInt16(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
            }
        }

        foreach (var pk in pks)
        {
            if (!head.TryGetValue(pk, out var h)) continue;
            res[pk] = Build(pk, h.At, h.Bb, h.Sb, h.Kind, h.Seat, h.C1, h.C2, h.NetBb,
                boards.GetValueOrDefault(pk) ?? new List<Card>(), actions.GetValueOrDefault(pk) ?? new());
        }
        return res;
    }

    /// <summary>Сборка линии из действий раздачи — отдельно от запросов, чтобы формат можно было проверить тестом.</summary>
    public static HandLine Build(long pk, DateTime at, long bb, long sb, GameKind kind, int focusSeat, Card? c1, Card? c2, int netBbX100,
        IReadOnlyList<Card> board, IReadOnlyList<(Street Street, int Seat, ActionType Type, long Amount, long To, long Pot)> actions)
    {
        var actors = new Dictionary<int, int>();
        var streets = new List<LineStreet>();
        foreach (var street in new[] { Street.Preflop, Street.Flop, Street.Turn, Street.River })
        {
            var list = new List<LineAction>();
            long pot = -1;
            foreach (var a in actions)
            {
                if (a.Street != street) continue;
                string? text = a.Type switch
                {
                    ActionType.Fold => "F",
                    ActionType.Check => "X",
                    ActionType.Call => "C" + Bb(a.Amount, bb),
                    ActionType.Bet => "B" + Bb(a.Amount, bb),
                    ActionType.Raise => "R" + Bb(a.To, bb),      // рейз — «до скольких», как принято записывать
                    _ => null,                                   // блайнды, анте, возвраты и сбор банка в линию не входят
                };
                if (text == null) continue;
                // Префлоп-фолды в линию не идут: за полным столом это пять-шесть «F» подряд, которые ничего не сообщают.
                if (street == Street.Preflop && a.Type == ActionType.Fold) continue;
                if (pot < 0) pot = a.Pot;
                if (!actors.TryGetValue(a.Seat, out int actor)) actors[a.Seat] = actor = actors.Count;
                list.Add(new LineAction(text, a.Seat == focusSeat, actor));
            }
            var cards = street switch
            {
                Street.Flop => board.Take(3).ToList(), Street.Turn => board.Skip(3).Take(1).ToList(),
                Street.River => board.Skip(4).Take(1).ToList(), _ => new List<Card>(),
            };
            if (street != Street.Preflop && cards.Count == 0) break;
            streets.Add(new LineStreet(cards, pot < 0 || bb <= 0 ? 0 : (double)pot / bb, list));
        }
        string stakes = kind == GameKind.Cash ? $"{Money(sb)}/{Money(bb)}" : $"{sb / 100}/{bb / 100}";
        return new HandLine(pk, at, c1, c2, streets, netBbX100 / 100.0, stakes);
    }

    /// <summary>Сумма в bb: целые без дроби («3»), иначе один знак («2.5»).</summary>
    private static string Bb(long amount, long bb)
    {
        if (bb <= 0) return "";
        double v = (double)amount / bb;
        return (Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Money(long cents) => (cents / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
