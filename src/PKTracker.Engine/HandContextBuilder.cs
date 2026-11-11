using PKTracker.Core.Model;

namespace PKTracker.Engine;

/// <summary>
/// ParsedHand → { DecisionPoint[], HandPlayerFacts[], ActionRecord[] } за один проход (§4.2, §15.2).
/// </summary>
public static class HandContextBuilder
{
    /// <summary>Версия логики построения контекста. Любое изменение семантики → ++ и полный ребилд (§15.6).</summary>
    public const short ContextVersion = 6;

    private const byte NotFolded = 255;

    public static byte StackBucket(long stack, long bb)
    {
        if (bb <= 0) return 0;
        long x = stack / bb;
        return x switch { < 10 => 0, < 20 => 1, < 40 => 2, < 100 => 3, _ => 4 };
    }

    /// <param name="computeEv">Посчитать All-in EV (дорого для префлоп-олл-инов; импорт и ребилд — да, быстрые проверки — нет).</param>
    public static HandContext Build(ParsedHand h, bool computeEv)
    {
        var ctx = Build(h);
        if (computeEv) AllInEv.Apply(h, ctx);
        return ctx;
    }

    public static HandContext Build(ParsedHand h)
    {
        var pos = Positions.Compute(h);
        int size = pos.BySeat.Length;
        foreach (var a in h.Actions) if (a.Seat >= size) throw new InvalidOperationException($"Seat {a.Seat} out of range");

        var dealtSeats = h.Seats.Where(s => s.Dealt).Select(s => s.Seat).OrderBy(s => s).ToArray();
        var info = HandInfo.From(h, dealtSeats.Length);
        long bb = h.BigBlind;

        // Порядок постфлоп; для раздач без позиций (бомб-пот) — по часовой от кнопки.
        var order = (int[])pos.PostflopOrder.Clone();
        if (dealtSeats.Any(s => order[s] < 0))
        {
            var ring = dealtSeats.OrderBy(s => s > h.ButtonSeat ? s - h.ButtonSeat : s + size - h.ButtonSeat).ToArray();
            for (int i = 0; i < ring.Length; i++) order[ring[i]] = i;
        }

        var dealt = new bool[size];
        foreach (var s in dealtSeats) dealt[s] = true;
        var startStack = new long[size];
        var names = new string[size];
        var collected = new long[size];
        foreach (var s in h.Seats) { startStack[s.Seat] = s.Stack; names[s.Seat] = s.Name; collected[s.Seat] = s.Won; }

        var invested = new long[size];
        var streetPut = new long[size];
        var foldStreet = new byte[size];
        Array.Fill(foldStreet, NotFolded);
        var allIn = new bool[size];
        var acted = new bool[size];
        var checkedSt = new bool[size];
        var myLastLevel = new int[size];
        var voluntary = new bool[size];
        var bits = new FactBits[size];
        // Последнее постфлоп-действие игрока на каждой улице, по 3 бита (флоп — младшие): линии BXB, XCF и т. п.
        var lineBits = new short[size];

        long level = 0;
        int aggr = 0;              // ставок+рейзов на текущей улице
        int pfRaises = 0;
        int pfLimpers = 0;
        int numCallers = 0;
        int openRaiser = -1, openLimpers = 0;   // -1 — опена ещё не было (место 0 валидно)
        int pfAggressor = 0, streetAggressor = 0, prevStreetAggressor = 0;
        bool streetBetIsCbet = false;
        var street = Street.Preflop;
        int pfRaisesTotal = 0, pfLimpersTotal = 0, flopPlayers = 0;
        int callersBefore3b = 0;   // коллеров опена до 3-бета: отличает сквиз от обычного 3-бета
        short flopMask = 0;
        var pfrPos = Position.None;

        var decisions = new List<DecisionPoint>(h.Actions.Count);
        var records = new List<ActionRecord>(h.Actions.Count);

        long Pot() { long p = 0; for (int i = 0; i < size; i++) p += invested[i]; return p; }

        foreach (var a in h.Actions)
        {
            int s = a.Seat;
            long potBefore = Pot();
            bool isDecision = a.Type is ActionType.Fold or ActionType.Check or ActionType.Call or ActionType.Bet or ActionType.Raise;

            if (isDecision && a.Street != street)
            {
                if (a.Street < street) throw new InvalidOperationException("Street went backwards");
                prevStreetAggressor = street == Street.Preflop ? pfAggressor : streetAggressor;
                if (street == Street.Preflop)
                {
                    // Снимок конца префлопа: тип банка, кто его открыл и кто дошёл до флопа. Постфлоп-споты
                    // («рейзер против BB», «лимп-пот блайндов», 3-бет-пот) определяются только по нему.
                    pfRaisesTotal = pfRaises; pfLimpersTotal = pfLimpers;
                    pfrPos = pfRaises > 0 ? pos.BySeat[pfAggressor] : Position.None;
                    for (int i = 0; i < size; i++)
                        if (dealt[i] && foldStreet[i] == NotFolded && pos.BySeat[i] != Position.None)
                        {
                            flopPlayers++;
                            flopMask |= (short)(1 << (int)pos.BySeat[i]);
                        }
                }
                street = a.Street;
                Array.Clear(streetPut);
                Array.Clear(acted);
                Array.Clear(checkedSt);
                Array.Clear(myLastLevel);
                level = 0; aggr = 0; numCallers = 0; streetAggressor = 0; streetBetIsCbet = false;
            }

            long amount = a.Amount;
            switch (a.Type)
            {
                case ActionType.PostSb:
                case ActionType.PostBb:
                case ActionType.PostBbOot:
                    invested[s] += amount; streetPut[s] += amount; level = Math.Max(level, streetPut[s]);
                    bits[s] |= a.Type switch
                    {
                        ActionType.PostSb => FactBits.PostedSb,
                        ActionType.PostBb => FactBits.PostedBb,
                        _ => FactBits.PostedOutOfTurn,
                    };
                    break;
                case ActionType.PostAnte:
                    invested[s] += amount;
                    break;
                case ActionType.PostDead:
                {
                    invested[s] += amount;
                    // "posts dead" = мёртвый SB + живой BB (A >= sb+bb) либо только мёртвый SB (A < sb+bb).
                    long live = amount >= h.SmallBlind + bb ? bb : 0;
                    streetPut[s] += live; level = Math.Max(level, streetPut[s]);
                    bits[s] |= FactBits.PostedDead | FactBits.PostedOutOfTurn;
                    break;
                }
                case ActionType.UncalledReturn:
                {
                    // В 3-max спинах WPN иногда пишет в «Uncalled bet» не только непокрытую ставку,
                    // но и мёртвый сайд-пот, который игрок забирает без вскрытия (когда короткий
                    // стек в олл-ине за неполный блайнд, а обладатель большего блайнда сбросил).
                    // Тогда возврат оказывается больше, чем игрок вообще поставил, и invested уходит
                    // в минус. Настоящий возврат — превышение над самым большим вкладом тех,
                    // кто ещё в раздаче; берём минимум, чтобы не трогать нормальные раздачи.
                    long maxLive = 0;
                    for (int i = 0; i < size; i++)
                        if (i != s && dealt[i] && foldStreet[i] == NotFolded) maxLive = Math.Max(maxLive, streetPut[i]);
                    amount = Math.Min(amount, Math.Max(0, streetPut[s] - maxLive));
                    invested[s] -= amount; streetPut[s] -= amount;
                    break;
                }
                case ActionType.Collect:
                    break; // выигрыш берётся из ParsedSeat.Won

                default:
                    amount = EmitDecision(a, potBefore);
                    break;
            }
            if (a.IsAllIn) { allIn[s] = true; bits[s] |= FactBits.AllIn; }
            records.Add(new ActionRecord((short)a.Seq, a.Street, (byte)s, a.Type, amount, streetPut[s], potBefore, a.IsAllIn));
        }

        long EmitDecision(ParsedAction a, long potBefore)
        {
            int s = a.Seat;
            bool pf = street == Street.Preflop;
            int betLevel = pf ? 1 + pfRaises : aggr;
            int myLastBefore = myLastLevel[s];
            long toCall = Math.Max(0, level - streetPut[s]);
            var facing = pf ? (pfRaises == 0 ? Facing.None : Facing.Raise)
                            : aggr switch { 0 => Facing.None, 1 => Facing.Bet, _ => Facing.Raise };

            int playersIn = 0, playersBehind = 0;
            long myStack = startStack[s] - invested[s] + streetPut[s];
            long maxOther = 0;
            bool isIp = true;
            for (int i = 0; i < size; i++)
            {
                if (!dealt[i] || foldStreet[i] != NotFolded) continue;
                playersIn++;
                if (i == s) continue;
                if (!allIn[i] && !acted[i]) playersBehind++;
                maxOther = Math.Max(maxOther, startStack[i] - invested[i] + streetPut[i]);
                if (order[i] > order[s]) isIp = false;
            }

            var ctx = CtxBits.None;
            if (pfAggressor == s && pfAggressor != 0) ctx |= CtxBits.PfAggressor;
            if (!pf && prevStreetAggressor == s && s != 0) ctx |= CtxBits.PrevStreetAggressor;
            if (!pf && isIp) ctx |= CtxBits.IsIp;
            if (!acted[s]) ctx |= CtxBits.FirstActionOnStreet;
            var myPos = pos.BySeat[s];
            if (myPos is Position.SB or Position.BB) ctx |= CtxBits.InBlinds;
            if (checkedSt[s]) ctx |= CtxBits.CheckedThisStreet;
            if (!pf && aggr == 1 && streetBetIsCbet && streetAggressor != s) ctx |= CtxBits.FacingCbet;
            if (pf && pfRaises == 1 && openLimpers == 0 && numCallers == 0 && openRaiser != s &&
                pos.BySeat[openRaiser] is Position.CO or Position.BTN or Position.SB)
                ctx |= CtxBits.StealSpot;
            int aggressorRef = pf ? 0 : prevStreetAggressor;
            if (aggressorRef != 0 && aggressorRef != s && dealt[aggressorRef] && foldStreet[aggressorRef] == NotFolded && order[aggressorRef] > order[s])
                ctx |= CtxBits.AggressorBehind;
            if (voluntary[s]) ctx |= CtxBits.VoluntarilyInvested;
            int lastAgg = pf ? pfAggressor : streetAggressor;
            if (toCall > 0 && lastAgg != 0 && allIn[lastAgg]) ctx |= CtxBits.FacingAllIn;
            // Место 0 служит признаком «агрессора не было» — но оно же валидное место, поэтому
            // отдельно проверяем, что агрессия на улице действительно была.
            bool hasAgg = pf ? pfRaises > 0 : aggr > 0;
            var vsPos = hasAgg && lastAgg != s ? pos.BySeat[lastAgg] : Position.None;
            var openPos = openRaiser >= 0 ? pos.BySeat[openRaiser] : Position.None;
            short myLine = lineBits[s];
            long facingAmount = level;

            // До применения действия: собственный рейз не должен попасть в «тип банка» своей же точки решения.
            var pfrPosBefore = pf && pfRaises > 0 ? pos.BySeat[pfAggressor] : Position.None;

            Choice chosen;
            long chosenAmount = 0;
            short? sizePct = null;
            int limpersSnapshot = pf ? pfLimpers : 0;
            int callersSnapshot = numCallers;

            switch (a.Type)
            {
                case ActionType.Fold:
                    chosen = Choice.Fold;
                    foldStreet[s] = (byte)street;
                    break;
                case ActionType.Check:
                    chosen = Choice.Check;
                    checkedSt[s] = true;
                    break;
                case ActionType.Call:
                    chosen = Choice.Call;
                    chosenAmount = a.Amount;
                    invested[s] += a.Amount; streetPut[s] += a.Amount; level = Math.Max(level, streetPut[s]);
                    numCallers++;
                    if (pf)
                    {
                        if (pfRaises == 0) pfLimpers++;
                        voluntary[s] = true;
                        bits[s] |= FactBits.Vpip;
                    }
                    break;
                case ActionType.Bet:
                    chosen = Choice.Bet;
                    chosenAmount = a.Amount;
                    sizePct = Pct(a.Amount, potBefore);
                    invested[s] += a.Amount; streetPut[s] += a.Amount; level = Math.Max(level, streetPut[s]);
                    aggr++;
                    if (!pf && aggr == 1 && s == prevStreetAggressor) streetBetIsCbet = true;
                    streetAggressor = s; numCallers = 0;
                    myLastLevel[s] = pf ? 1 + pfRaises : aggr;
                    if (pf) { voluntary[s] = true; bits[s] |= FactBits.Vpip; }
                    break;
                case ActionType.Raise:
                {
                    chosen = Choice.Raise;
                    long inc = a.Amount - streetPut[s];
                    if (inc < 0) throw new InvalidOperationException($"Raise to {a.Amount} below already put {streetPut[s]}");
                    chosenAmount = inc;
                    sizePct = Pct(a.Amount - level, potBefore + toCall);
                    invested[s] += inc; streetPut[s] = a.Amount; level = Math.Max(level, a.Amount);
                    aggr++;
                    streetAggressor = s; numCallers = 0;
                    if (pf)
                    {
                        pfRaises++;
                        if (pfRaises == 1) { openRaiser = s; openLimpers = pfLimpers; }
                        if (pfRaises == 2) { bits[s] |= FactBits.ThreeBet; callersBefore3b = callersSnapshot; }
                        pfAggressor = s;
                        voluntary[s] = true;
                        bits[s] |= FactBits.Vpip | FactBits.Pfr;
                    }
                    myLastLevel[s] = pf ? 1 + pfRaises : aggr;
                    break;
                }
                default:
                    throw new InvalidOperationException();
            }

            // Линии соперников: префлоп-агрессора и (в хедз-апе) единственного оппонента.
            short pfaLine = !pf && pfRaisesTotal > 0 && pfAggressor != s ? lineBits[pfAggressor] : (short)0;
            short oppLine = 0;
            long tableMaxStart = 0;
            for (int i = 0; i < size; i++)
                if (i != s && dealt[i]) tableMaxStart = Math.Max(tableMaxStart, startStack[i]);
            {
                int other = -1, cnt = 0;
                for (int i = 0; i < size; i++)
                {
                    if (i == s || !dealt[i] || foldStreet[i] != NotFolded) continue;
                    cnt++; other = i;
                }
                if (!pf && cnt == 1) oppLine = lineBits[other];
            }
            long vsStart = hasAgg && lastAgg != s ? startStack[lastAgg] : 0;
            // Как в Hand2Note: эффективный стек раздачи — против наибольшего стартового стека среди всех,
            // кому сданы карты (а не только оставшихся): сверено с его фильтрами «20bb+ eff».
            long startEff = Math.Min(startStack[s], tableMaxStart);

            short? strength = null;
            short texture = 0;
            if (!pf && h.Boards.Count > 0)
            {
                int n = street switch { Street.Flop => 3, Street.Turn => 4, _ => 5 };
                texture = (short)HandEvaluator.BoardTexture(h.Boards[0], n);
            }

            decisions.Add(new DecisionPoint
            {
                Hand = info,
                Seq = (short)a.Seq,
                Seat = (byte)s,
                PlayerName = names[s] ?? "",
                Street = street,
                Position = myPos,
                Facing = facing,
                BetLevel = betLevel,
                MyLastLevel = myLastBefore,
                ToCall = toCall,
                PotBefore = potBefore,
                EffStack = Math.Min(myStack, maxOther),
                PlayersIn = playersIn,
                PlayersBehind = playersBehind,
                NumLimpers = limpersSnapshot,
                NumCallers = callersSnapshot,
                Ctx = ctx,
                Chosen = chosen,
                ChosenAmount = chosenAmount,
                SizePct = sizePct,
                IsAllIn = a.IsAllIn,
                HandStrength = strength,
                BoardTexture = texture,
                StackBucket = StackBucket(startStack[s], bb),
                VsPosition = vsPos,
                OpenerPosition = openPos,
                ToAmount = streetPut[s],
                FacingAmount = facingAmount,
                LineBits = myLine,
                PfaLineBits = pfaLine,
                OppLineBits = oppLine,
                PotRaises = pf ? betLevel - 1 : pfRaisesTotal,
                PfLimpers = pf ? limpersSnapshot : pfLimpersTotal,
                PfrPosition = pf ? pfrPosBefore : pfrPos,
                FlopPlayers = pf ? 0 : flopPlayers,
                FlopMask = pf ? (short)0 : flopMask,
                VsStack = vsStart,
                CallersBefore3b = callersBefore3b,
                StartEffStack = startEff,
            });
            acted[s] = true;
            if (!pf)
            {
                // Пишем последнее действие улицы: чек-колл пишется как C, чек-рейз как R — линия
                // описывает, чем игрок улицу закончил, а не сколько раз брал слово.
                int code = chosen switch
                {
                    Choice.Check => 1, Choice.Bet => 2, Choice.Call => 3, Choice.Raise => 4, _ => 0,
                };
                if (code != 0)
                {
                    int shift = ((int)street - 1) * 3;
                    lineBits[s] = (short)((lineBits[s] & ~(7 << shift)) | (code << shift));
                }
            }
            return chosenAmount;
        }

        return Finish();

        HandContext Finish()
        {
            // SawFlop/Turn/River/Showdown
            int boardCards = h.Boards.Count > 0 ? h.Boards.Max(b => b.Count) : 0;
            int remaining = 0;
            for (int i = 0; i < size; i++) if (dealt[i] && foldStreet[i] == NotFolded) remaining++;
            bool showdown = remaining >= 2;

            long totalInvested = 0, totalCollected = 0;
            for (int i = 0; i < size; i++) { totalInvested += invested[i]; totalCollected += collected[i]; }

            var players = new List<HandPlayerFacts>(dealtSeats.Length);
            var finalBits = new FactBits[size];
            foreach (var seat in h.Seats.OrderBy(x => x.Seat))
            {
                int i = seat.Seat;
                if (!dealt[i] && invested[i] == 0 && collected[i] == 0) continue;
                var b = bits[i];
                if (dealt[i]) b |= FactBits.Dealt;
                byte fs = foldStreet[i];
                if (dealt[i])
                {
                    if (boardCards >= 3 && fs > (byte)Street.Preflop) b |= FactBits.SawFlop;
                    if (boardCards >= 4 && fs > (byte)Street.Flop) b |= FactBits.SawTurn;
                    if (boardCards >= 5 && fs > (byte)Street.Turn) b |= FactBits.SawRiver;
                    if (showdown && fs == NotFolded) b |= FactBits.SawShowdown;
                }
                if (collected[i] > 0)
                {
                    b |= FactBits.WonHand;
                    if ((b & FactBits.SawShowdown) != 0) b |= FactBits.WonAtShowdown;
                }
                if (i == pfAggressor) b |= FactBits.PfAggressor;
                if (seat.IsHero) b |= FactBits.IsHero;
                if (seat.CardsKnown) b |= FactBits.CardsKnown;
                if (h.Has(HandFlags.BombPot)) b &= ~(FactBits.Vpip | FactBits.Pfr | FactBits.ThreeBet);
                finalBits[i] = b;

                players.Add(new HandPlayerFacts
                {
                    Hand = info,
                    Seat = (byte)i,
                    PlayerName = seat.Name,
                    Position = pos.BySeat[i],
                    StartStack = seat.Stack,
                    StackBbX10 = bb > 0 ? (short)Math.Min(short.MaxValue, seat.Stack * 10 / bb) : (short)0,
                    StackBucket = StackBucket(seat.Stack, bb),
                    HoleCard1 = seat.HoleCards[0],
                    HoleCard2 = seat.HoleCards[1],
                    HoleClass = seat.CardsKnown ? Card.HoleClass(seat.HoleCards[0]!.Value, seat.HoleCards[1]!.Value) : null,
                    Invested = invested[i],
                    Collected = collected[i],
                    RakeShare = totalInvested > 0 ? h.Rake * invested[i] / totalInvested : 0,
                    Bits = b,
                });
            }

            // Сила руки — ретроспективно по известным картам (§15.5)
            if (h.Boards.Count > 0)
            {
                Span<Card> buf = stackalloc Card[7];
                foreach (var d in decisions)
                {
                    if (d.Street == Street.Preflop) continue;
                    var seat = h.SeatByNumber(d.Seat);
                    if (seat is not { CardsKnown: true }) continue;
                    int n = d.Street switch { Street.Flop => 3, Street.Turn => 4, _ => 5 };
                    var board = h.Boards[0];
                    if (board.Count < n) continue;
                    buf[0] = seat.HoleCards[0]!.Value; buf[1] = seat.HoleCards[1]!.Value;
                    for (int k = 0; k < n; k++) buf[2 + k] = board[k];
                    d.HandStrength = (short)HandEvaluator.Category(HandEvaluator.Evaluate(buf[..(2 + n)]));
                    d.MadeHandBits = MadeHands.Classify(buf[0], buf[1], buf.Slice(2, n));
                }
            }
            // Исход раздачи виден из каждой точки решения: «выиграл без вскрытия» в конкретном споте.
            foreach (var d in decisions) d.Outcome = finalBits[d.Seat];

            return new HandContext
            {
                Hand = info, Positions = pos, Players = players, Decisions = decisions, Actions = records,
                TotalInvested = totalInvested, TotalCollected = totalCollected,
            };
        }

        static short Pct(long amount, long pot) =>
            pot <= 0 ? (short)0 : (short)Math.Clamp(amount * 100 / pot, 0, short.MaxValue);
    }
}
