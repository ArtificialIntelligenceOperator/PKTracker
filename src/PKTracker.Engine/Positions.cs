using PKTracker.Core.Model;

namespace PKTracker.Engine;

/// <summary>
/// Позиции по порядку действий, а не арифметикой от кнопки (§15.3).
/// BTN — тот, кто действует последним префлоп перед блайндами.
/// </summary>
public static class Positions
{
    private static readonly Position[][] Names =
    {
        Array.Empty<Position>(),
        new[] { Position.BTN },
        new[] { Position.CO, Position.BTN },
        new[] { Position.HJ, Position.CO, Position.BTN },
        new[] { Position.UTG, Position.HJ, Position.CO, Position.BTN },
        new[] { Position.UTG, Position.LJ, Position.HJ, Position.CO, Position.BTN },
        new[] { Position.UTG, Position.UTG1, Position.LJ, Position.HJ, Position.CO, Position.BTN },
        new[] { Position.UTG, Position.UTG1, Position.MP, Position.LJ, Position.HJ, Position.CO, Position.BTN },
    };

    public sealed class Result
    {
        /// <summary>Индекс = номер места (0..maxSeats); Position.None для нераздававшихся.</summary>
        public required Position[] BySeat { get; init; }
        /// <summary>Порядок действий постфлоп (0 — первый); -1 для нераздававшихся.</summary>
        public required int[] PostflopOrder { get; init; }
        public int SbSeat { get; init; }
        public int BbSeat { get; init; }
        public int BtnSeat { get; init; }
    }

    public static Result Compute(ParsedHand h)
    {
        int size = Math.Max(h.MaxSeats, h.Seats.Count == 0 ? 0 : h.Seats.Max(s => s.Seat)) + 1;
        var bySeat = new Position[size];
        var order = new int[size];
        Array.Fill(bySeat, Position.None);
        Array.Fill(order, -1);

        var dealt = h.Seats.Where(s => s.Dealt).Select(s => s.Seat).OrderBy(s => s).ToList();
        int sb = 0, bb = 0;
        foreach (var a in h.Actions)
        {
            if (a.Type == ActionType.PostSb && sb == 0 && dealt.Contains(a.Seat)) sb = a.Seat;
            else if (a.Type == ActionType.PostBb && bb == 0 && dealt.Contains(a.Seat)) bb = a.Seat;
        }

        if (bb == 0 || dealt.Count < 2)
            return new Result { BySeat = bySeat, PostflopOrder = order, SbSeat = sb, BbSeat = bb };

        if (dealt.Count == 2)
        {
            int other = dealt[0] == bb ? dealt[1] : dealt[0];
            // Хедз-ап: кнопка = SB; действует первой префлоп и последней постфлоп.
            bySeat[other] = Position.BTN;
            bySeat[bb] = Position.BB;
            order[bb] = 0;
            order[other] = 1;
            return new Result { BySeat = bySeat, PostflopOrder = order, SbSeat = sb, BbSeat = bb, BtnSeat = other };
        }

        // Места по часовой стрелке, начиная после BB, до места перед SB (или перед BB, если SB нет).
        int stop = sb != 0 ? sb : bb;
        int startIdx = dealt.IndexOf(bb);
        var ring = new List<int>();
        for (int i = 1; i < dealt.Count; i++)
        {
            int s = dealt[(startIdx + i) % dealt.Count];
            if (s == stop) break;
            ring.Add(s);
        }

        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            Position p;
            if (n < Names.Length) p = Names[n][i];
            else
            {
                // >9 игроков: лишние ранние места — UTG
                int fromEnd = n - 1 - i;
                var full = Names[^1];
                p = fromEnd < full.Length ? full[full.Length - 1 - fromEnd] : Position.UTG;
            }
            bySeat[ring[i]] = p;
        }
        if (sb != 0) bySeat[sb] = Position.SB;
        bySeat[bb] = Position.BB;

        int k = 0;
        if (sb != 0) order[sb] = k++;
        order[bb] = k++;
        foreach (var s in ring) order[s] = k++;

        return new Result
        {
            BySeat = bySeat, PostflopOrder = order, SbSeat = sb, BbSeat = bb,
            BtnSeat = n > 0 ? ring[^1] : 0,
        };
    }
}

/// <summary>
/// Перевод позиций PKTracker в обозначения Hand2Note. Считаем по расстоянию от кнопки:
/// так одинаково ведут себя столы 7, 8 и 9 игроков, а в 6-max первая позиция называется «MP».
/// </summary>
public static class HandPositions
{
    /// <summary>Расстояние от кнопки для не-блайндов: 0 — BTN, 1 — CO, 2 — HJ, … ; -1 — блайнд или нет позиции.</summary>
    public static int DistanceFromButton(Position p, int playersDealt)
    {
        int nonBlind = Math.Max(0, playersDealt - 2);
        return p switch
        {
            Position.BTN => 0,
            Position.CO => 1,
            Position.HJ => 2,
            Position.LJ => 3,
            Position.MP => 4,
            Position.UTG1 => nonBlind >= 7 ? 5 : 4,
            Position.UTG => nonBlind - 1,
            _ => -1,
        };
    }

    public static string H2n(Position p, int playersDealt)
    {
        if (p == Position.SB) return "SB";
        if (p == Position.BB) return "BB";
        if (p == Position.None) return "";
        if (p == Position.BTN) return "BTN";
        if (p == Position.CO) return "CO";
        if (p == Position.HJ) return "HJ";
        // В full ring «UTG» — только первое место; второе и дальше до HJ — «MP» (сверено с RFI по позициям
        // в попапах Hand2Note: так сходятся и UTG, и MP одновременно). Первое место ринга — всегда Position.UTG,
        // поэтому число сданных здесь не нужно: за столом без малого блайнда оно на единицу врёт.
        return playersDealt >= 7 && p == Position.UTG ? "UTG" : "MP";
    }
}
