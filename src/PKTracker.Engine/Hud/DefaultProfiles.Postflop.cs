namespace PKTracker.Engine.Hud;

/// <summary>Встроенные раскладки: постфлоп-вкладки (агрессор и коллер, в позиции и вне её).</summary>
public static partial class DefaultProfiles
{
    /// <summary>В позиции на постфлопе. Фильтр, а не отдельные статы: условие одно на всю вкладку.</summary>
    private static readonly CellFilter Ip = PageBuilder.Merge(Ring, PageBuilder.F(where: "IsIp"));
    private static readonly CellFilter Oop = PageBuilder.Merge(Ring, PageBuilder.F(where: "!IsIp"));

    /// <summary>Ряд «подпись | флоп | тёрн | ривер» из трёх разных статов — их на улицу по одному.</summary>
    private static void Streets(PageBuilder b, int row, int col, string label, string? flop, string? turn, string? river,
        CellFilter f, string bg, string? labelFg = "#FFFFFF")
    {
        b.Label(row, col, label, bg, labelFg, bold: true);
        if (flop != null) b.Stat(row, col + 1, flop, f, bg);
        if (turn != null) b.Stat(row, col + 2, turn, f, bg);
        if (river != null) b.Stat(row, col + 3, river, f, bg);
    }

    private static HudPage BuildRaiser(bool ip)
    {
        string name = ip ? "RAISER IP" : "RAISER OOP";
        var f = ip ? Ip : Oop;
        var b = new PageBuilder(name, colWidth: 40);
        b.Width(0, 170);
        b.Width(4, 14);
        b.Width(5, 150);
        int r = 0;

        b.Label(r, 0, name, Theme.Line, "#4FC3F7", bold: true);
        b.Heads(r, 1, new[] { "FLOP", "TURN", "RIVER" });
        b.Heads(r, 6, new[] { "FLOP", "TURN", "RIVER" });
        r++;

        Streets(b, r, 0, ip ? "CB IP" : "CB OOP", "cbet_flop", "cbet_turn", "cbet_river", f, Theme.Line, "#FFD54F");
        Streets(b, r, 5, ip ? "Fold vs DONK" : "Fold vs FLOAT", "fold_to_donk_flop", "fold_to_donk_turn", null, f, Theme.Bet);
        r++;
        Streets(b, r, 0, "Fold vs RAISE", "fold_to_raise_cbet_flop", "fold_to_raise_cbet_turn", null, f, Theme.Fold);
        Streets(b, r, 5, ip ? "Raise DONK" : "Fold vs FLOAT", "raise_donk_flop", null, null, f, Theme.Fold);
        r++;
        Streets(b, r, 0, "Delay CB", null, "delay_cbet_turn", "delay_cbet_river", f, Theme.Line, "#FFD54F");
        Streets(b, r, 5, "Fold vs DONK", null, "fold_to_donk_turn", null, f, Theme.Bet);
        r++;
        Streets(b, r, 0, "Fold vs RAISE Delay CB", null, "fold_to_raise_cbet_turn", null, f, Theme.Fold);
        Streets(b, r, 5, "Fold vs DONK", null, null, "fold_vs_bet_river", f, Theme.Bet);
        r++;
        // Сибет в банк с двумя оппонентами: в мультипоте он совсем другой, поэтому отдельной строкой.
        b.Label(r, 0, "CB vs 2 players", Theme.Line, "#FFD54F", bold: true);
        b.Stat(r, 1, "cbet_flop", PageBuilder.Merge(f, PageBuilder.F(inHandFrom: 3)), Theme.Line);
        r++;
        Streets(b, r, 0, "Agression", "agg_flop", "agg_turn", "agg_river", f, Theme.Fold);
        Streets(b, r, 5, "CALL vs PROBA", null, "call_probe_turn", null, f, Theme.Donk);
        r++;
        if (ip)
        {
            b.Label(r, 0, "X-Behind", Theme.Donk, "#FFFFFF", bold: true);
            b.Stat(r, 1, "check_behind_flop", f, Theme.Donk);
        }
        Streets(b, r, 5, "Fold vs PROBA", null, "fold_to_probe_turn", "fold_to_probe_river", f, Theme.Line);
        r += 2;

        // Линии собственных действий: значение стоит на той улице, где принимается решение.
        b.Label(r, 0, ip ? "IP" : "OOP", Theme.Line, "#4FC3F7", bold: true);
        r++;
        var lines = ip
            ? new[] { ("BXB", "line_bxb", 3), ("BXF", "line_bxf", 3), ("XXB", "line_xxb", 3), ("XXF", "line_xxf", 3), ("XCB", "line_xcb", 3) }
            : new[] { ("XB", "line_xb", 2), ("XF", "line_xf", 2), ("XBB", "line_xbb", 3), ("CBB", "line_cbb", 3), ("BXB", "line_bxb", 3),
                      ("BXF", "line_bxf", 3), ("XXB", "line_xxb", 3), ("XXF", "line_xxf", 3), ("CXB", "line_cxb", 3), ("CXF", "line_cxf", 3) };
        foreach (var (label, code, street) in lines)
        {
            b.Label(r, 0, label, Theme.Line, "#FFD54F", bold: true);
            b.Stat(r, street, code, f, Theme.Line);
            r++;
        }
        r++;

        // Внизу — префлоп-контекст: с какими руками игрок вообще попадает в эту вкладку.
        b.Heads(r, 1, new[] { "ToT", "EP", "MP", "CO", "BTN", "SB", "BB" });
        r++;
        b.Row(r++, 0, "ISO", "iso_raise", PosFiltersWide, Theme.Iso, Theme.Iso);
        b.Row(r++, 0, "R1st", "rfi", PosFiltersWide, Theme.Open, Theme.Open);
        r++;

        b.Label(r, 0, ip ? "R IP" : "R OOP", Theme.Head, Theme.HeadFg, bold: true);
        b.Heads(r, 1, new[] { "F", "T", "R" });
        b.Heads(r, 5, new[] { "F", "T", "R" });
        r++;
        Streets(b, r, 0, "Bet", "bet_flop", "bet_turn", "bet_river", f, Theme.Post);
        Streets(b, r, 4, "FvsB", "fold_vs_bet_flop", "fold_vs_bet_turn", "fold_vs_bet_river", f, Theme.Post);
        r++;
        Streets(b, r, 0, "FvsR", "fold_vs_raise_flop", "fold_vs_raise_turn", "fold_vs_raise_river", f, Theme.Post);
        Streets(b, r, 4, "Raise", "raise_flop", "raise_turn", "raise_river", f, Theme.Post);
        r++;
        Streets(b, r, 0, "3b", "three_bet_flop", "three_bet_turn", null, f, Theme.Post);
        Streets(b, r, 4, "Fto3b", "fold_to_three_bet_flop", null, null, f, Theme.Post);
        return b.Build();
    }

    private static readonly CellFilter?[] PosFiltersWide =
    {
        Ring, Ring2(P.Ep), Ring2(P.Mp), Ring2(P.Co), Ring2(P.Btn), Ring2(P.Sb), Ring2(P.Bb),
    };

    private static HudPage BuildCaller(bool ip)
    {
        string name = ip ? "CALLER IP" : "CALLER OOP";
        var f = ip ? Ip : Oop;
        var b = new PageBuilder(name, colWidth: 40);
        b.Width(0, 150);
        b.Width(4, 14);
        b.Width(5, 150);
        int r = 0;

        b.Label(r, 0, name, Theme.Line, "#4FC3F7", bold: true);
        b.Heads(r, 1, new[] { "FLOP", "TURN", "RIVER" });
        b.Heads(r, 6, new[] { "FLOP", "TURN", "RIVER" });
        r++;

        if (ip)
        {
            Streets(b, r, 0, "FLOAT FLOP", "float_flop", null, null, f, Theme.Bet, "#FFD54F");
            Streets(b, r, 5, "Fold to CB", "fold_to_cbet_flop", "fold_to_cbet_turn", "fold_to_cbet_river", f, Theme.Fold);
            r++;
            Streets(b, r, 0, "FLOAT TURN", null, "float_turn", null, f, Theme.Bet, "#FFD54F");
            Streets(b, r, 5, "RAISE CB", "raise_cbet_flop", "raise_cbet_turn", null, f, Theme.Fold);
            r++;
            Streets(b, r, 0, "FLOAT RIVER", null, null, "float_river", f, Theme.Bet, "#FFD54F");
            Streets(b, r, 5, "Fold vs Delay", null, "fold_to_delay_cbet_turn", null, f, Theme.Fold);
            r++;
            Streets(b, r, 0, "Agression", "agg_flop", "agg_turn", "agg_river", f, Theme.Fold);
            Streets(b, r, 5, "F to 3bet Flop", "fold_to_three_bet_flop", null, null, f, Theme.Bet);
            r += 2;
        }
        else
        {
            Streets(b, r, 0, "DONK", "donk_flop", "donk_turn", "donk_river", f, Theme.Donk);
            Streets(b, r, 5, "Fold to CB", "fold_to_cbet_flop", "fold_to_cbet_turn", "fold_to_cbet_river", f, Theme.Fold);
            r++;
            Streets(b, r, 0, "Fold vs R DONK", "fold_vs_raise_flop", "fold_vs_raise_turn", null, f, Theme.Fold);
            Streets(b, r, 5, "Raise vs CB", "raise_cbet_flop", "raise_cbet_turn", "raise_cbet_river", f, Theme.Fold);
            r++;
            Streets(b, r, 0, "PROBA", null, "probe_turn", "probe_river", f, Theme.Line, "#4FC3F7");
            Streets(b, r, 5, "Fold vs DEL", null, "fold_to_delay_cbet_turn", null, f, Theme.Call);
            r++;
            Streets(b, r, 0, "Agression", "agg_flop", "agg_turn", "agg_river", f, Theme.Fold);
            Streets(b, r, 5, "F to 4bet FLOP", "fold_to_four_bet_flop", null, null, f, Theme.Bet);
            r += 2;
        }

        b.Label(r, 0, ip ? "IP" : "OOP", Theme.Line, "#4FC3F7", bold: true);
        r++;
        var lines = ip
            ? new[] { ("BXB", "line_bxb", 3), ("BXF", "line_bxf", 3), ("XXB", "line_xxb", 3), ("XXF", "line_xxf", 3),
                      ("XCB", "line_xcb", 3), ("XCF", "line_xcf", 3) }
            : new[] { ("BF", "line_bf", 2), ("BXB", "line_bxb", 3), ("BXF", "line_bxf", 3), ("XXB", "line_xxb", 3),
                      ("XXF", "line_xxf", 3), ("XCF", "line_xcf", 3), ("CXB", "line_cxb", 3) };
        foreach (var (label, code, street) in lines)
        {
            b.Label(r, 0, label, Theme.Line, "#FFD54F", bold: true);
            b.Stat(r, street, code, f, Theme.Line);
            r++;
        }
        r++;

        b.Heads(r, 1, new[] { "ToT", "EP", "MP", "CO", "BTN", "SB", "BB" });
        r++;
        b.Row(r++, 0, "Limp", "limp_any", PosFiltersWide, Theme.Limp, Theme.Limp);
        b.Row(r++, 0, "CC", "cold_call_any", PosFiltersWide, Theme.Call, Theme.Call);
        r++;

        b.Label(r, 0, ip ? "C IP" : "C OOP", Theme.Head, Theme.HeadFg, bold: true);
        b.Heads(r, 1, new[] { "F", "T", "R" });
        b.Heads(r, 5, new[] { "F", "T", "R" });
        r++;
        Streets(b, r, 0, "Bet", "bet_flop", "bet_turn", "bet_river", f, Theme.Post);
        Streets(b, r, 4, "FvsB", "fold_vs_bet_flop", "fold_vs_bet_turn", "fold_vs_bet_river", f, Theme.Post);
        r++;
        Streets(b, r, 0, "FvsR", "fold_vs_raise_flop", "fold_vs_raise_turn", "fold_vs_raise_river", f, Theme.Post);
        Streets(b, r, 4, "Raise", "raise_flop", "raise_turn", "raise_river", f, Theme.Post);
        r++;
        Streets(b, r, 0, "3b", "three_bet_flop", "three_bet_turn", null, f, Theme.Post);
        Streets(b, r, 4, "Fto3b", "fold_to_three_bet_flop", null, null, f, Theme.Post);
        return b.Build();
    }

    /// <summary>
    /// LIT — лид в следующую улицу: ставка не агрессора туда, где инициатива была чужой.
    /// В PKTracker это донк и проба, отсюда и коды статов.
    /// </summary>
    private static HudPage BuildLit()
    {
        var b = new PageBuilder("LIT", colWidth: 44);
        b.Width(0, 130);
        b.Width(4, 14);
        b.Width(5, 150);
        const string Bg = "#2E6B6B";
        int r = 0;
        b.Heads(r, 1, new[] { "FLOP", "TURN", "RIVER" });
        b.Heads(r, 6, new[] { "FLOP", "TURN", "RIVER" });
        r++;
        Streets(b, r, 0, "LIT SB", "donk_flop", "donk_turn", "donk_river", Ring2(P.Sb), Bg);
        Streets(b, r, 5, "RAISE LIT", "raise_flop", "raise_turn", null, Ring, Bg);
        r++;
        Streets(b, r, 0, "Fold vs LIT", "fold_to_donk_flop", "fold_to_donk_turn", null, Ring, Bg);
        r++;
        Streets(b, r, 0, "Fold vs R IP", "fold_vs_raise_flop", null, null, Ip, Bg);
        Streets(b, r, 5, "BET vs Missed LIT", "bet_flop", "bet_turn", "bet_river", Ring, Bg);
        r++;
        Streets(b, r, 0, "Skip LIT and F", "fold_to_cbet_flop", null, null, Oop, Bg);
        Streets(b, r, 5, "Fold vs Check-Raise", "fold_to_check_raise_flop", null, null, Ring, Bg);
        r += 2;

        b.Heads(r, 1, new[] { "FLOP", "TURN", "RIVER" });
        r++;
        Streets(b, r, 0, "DELAY LIT", null, "probe_turn", "probe_river", Oop, Bg);
        r++;
        Streets(b, r, 0, "F vs DEL LIT", null, "fold_to_probe_turn", "fold_to_probe_river", Ring, Bg);
        r++;
        Streets(b, r, 0, "FvsPROBA", null, "fold_to_probe_turn", "fold_to_probe_river", Ip, Bg);
        return b.Build();
    }
}
