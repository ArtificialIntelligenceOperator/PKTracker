using Npgsql;
using PKTracker.Core.Model;

namespace PKTracker.Data;

/// <summary>
/// Колонки hand_decisions, добавленные миграциями 007 и 009 (made_hand). Они дописаны в КОНЕЦ каждого SELECT,
/// чтобы индексы старых колонок в читателях не сдвигались. Порядок: десять колонок 007, затем made_hand.
/// </summary>
internal static class DecisionColumns
{

    public readonly record struct Extras(short PfaLine, short OppLine, int PotRaises, int PfLimpers, Position PfrPos,
        int FlopPlayers, short FlopMask, long VsStack, long StartEff, int CallersBefore3b, short MadeHand);

    /// <summary>Список колонок для SELECT с псевдонимом таблицы <paramref name="a"/> — один на все читатели.</summary>
    public static string Select(string a) =>
        $"{a}.pfa_line_bits, {a}.opp_line_bits, {a}.pot_raises, {a}.pf_limpers, {a}.pfr_position, {a}.flop_players, " +
        $"{a}.flop_mask, {a}.vs_stack, {a}.start_eff_stack, {a}.callers_before_3b, {a}.made_hand";

    /// <summary>Сколько колонок читает <see cref="Read"/>: следующая за ними — по смещению o + Count.</summary>
    public const int Count = 11;

    public static Extras Read(NpgsqlDataReader r, int o) => new(
        r.GetInt16(o), r.GetInt16(o + 1), r.GetInt16(o + 2), r.GetInt16(o + 3), (Position)r.GetInt16(o + 4),
        r.GetInt16(o + 5), r.GetInt16(o + 6), r.GetInt64(o + 7), r.GetInt64(o + 8), r.GetInt16(o + 9), r.GetInt16(o + 10));
}
