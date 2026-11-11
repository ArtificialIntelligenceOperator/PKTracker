using PKTracker.Core.Model;

namespace PKTracker.Parsers.Common;

/// <summary>
/// Вклады игроков на текущей улице. Нужен румам, которые НЕ пишут «Uncalled bet … returned»
/// (Winamax, 888, iPoker): неуравненную часть ставки приходится вычислять самим, иначе
/// Σ вложено не сойдётся с банком. Правило покера одно для всех: возвращается превышение
/// самого большого вклада над вторым по величине (среди ВСЕХ, включая сбросивших, — их фишки уже в банке).
/// </summary>
public sealed class BetLedger
{
    private readonly Dictionary<int, long> _put = new();

    /// <summary>Самый большой вклад на улице — «уровень», до которого надо доплатить.</summary>
    public long Level { get; private set; }

    public long Put(int seat) => _put.GetValueOrDefault(seat);

    /// <summary>Игрок доложил <paramref name="chips"/> фишек на эту улицу (блайнд, колл, бет).</summary>
    public void Add(int seat, long chips)
    {
        _put[seat] = Put(seat) + chips;
        if (_put[seat] > Level) Level = _put[seat];
    }

    /// <summary>
    /// Возврат неуравненной части: (место, сумма) или null. Вызывается в конце каждой улицы и в конце раздачи;
    /// после вызова вклады улицы сбрасываются.
    /// </summary>
    public (int Seat, long Amount)? FlushStreet()
    {
        int topSeat = -1;
        long top = 0, second = 0;
        foreach (var (seat, put) in _put)
        {
            if (put > top) { second = top; top = put; topSeat = seat; }
            else if (put > second) second = put;
        }
        _put.Clear();
        Level = 0;
        // top == second — ставку кто-то уравнял, возвращать нечего.
        return topSeat >= 0 && top > second ? (topSeat, top - second) : null;
    }

    /// <summary>
    /// Сбросить улицу и, если есть неуравненная часть, записать её действием UncalledReturn (как «Uncalled bet» у PokerStars).
    /// </summary>
    public (int Seat, long Amount)? FlushInto(ParsedHand h, ref int seq, Street street)
    {
        var r = FlushStreet();
        if (r is { } u)
            h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = u.Seat, Type = ActionType.UncalledReturn, Amount = u.Amount });
        return r;
    }
}
