using PKTracker.Engine.Stats;

namespace PKTracker.Engine.Hud;

/// <summary>
/// Встроенные раскладки HUD (§5.4). Префлоп-вкладки.
///
/// Одно отличие от оригинала намеренное: колонки названы позициями PKTracker (EP, MP, CO, BTN, SB, BB),
/// а не LG и HJ, как принято в некоторых трекерах: там «HJ» означает MP, и при живом HJ это путало бы.
/// </summary>
public static partial class DefaultProfiles
{
    /// <summary>Полный стол: так режется почти каждая вкладка, чтобы HU и 3-max не смешивались с 9-max.</summary>
    private static readonly CellFilter Ring = PageBuilder.F(playersFrom: 4, playersTo: 10);

    private static CellFilter Ring2(string[]? pos = null, string[]? vs = null, double? from = null, double? to = null,
        bool? allIn = null, bool? ante = null, string? where = null) =>
        PageBuilder.Merge(Ring, PageBuilder.F(pos: pos, vs: vs, stackFrom: from, stackTo: to, allIn: allIn, ante: ante, where: where));

    private static readonly string[] PosCols = { "TOT", "EP", "MP", "CO", "BTN", "SB" };
    private static readonly CellFilter?[] PosFilters =
    {
        Ring, Ring2(P.Ep), Ring2(P.Mp), Ring2(P.Co), Ring2(P.Btn), Ring2(P.Sb),
    };

    // ── Панель на столе ──────────────────────────────────────────────────────

    /// <summary>
    /// Панель: три строки по шесть значений, как в оригинале. Клик по любой ячейке открывает попап;
    /// у каждой строки своя вкладка, чтобы попасть сразу в нужный раздел.
    /// </summary>
    private static HudPage BuildPanel(bool mtt)
    {
        var b = new PageBuilder("Панель", colWidth: 26, rowHeight: 15);
        b.Stat(0, 0, "hands", Ring, fg: "#EFE6DA", bold: true, popup: "GENERAL");
        b.Stat(0, 1, "vpip", Ring, popup: "GENERAL");
        b.Stat(0, 2, "pfr", Ring, popup: "GENERAL");
        b.Stat(0, 3, "three_bet", Ring, popup: "3 BET");
        b.Stat(0, 4, "fold_to_3bet", Ring, popup: "3 BET");
        b.Stat(0, 5, "agg_pct", Ring, popup: "RAISER IP");

        b.Stat(1, 0, "rfi", Ring2(P.Ep), popup: "GENERAL");
        b.Stat(1, 1, "rfi", Ring2(P.Mp), popup: "GENERAL");
        b.Stat(1, 2, "rfi", Ring2(P.Co), popup: "GENERAL");
        b.Stat(1, 3, "rfi", Ring2(P.Btn), popup: "GENERAL");
        b.Stat(1, 4, "rfi", Ring2(P.Sb), popup: "GENERAL");
        b.Stat(1, 5, "fold_bb_vs_steal", Ring, popup: "STEALS");

        b.Stat(2, 0, "cbet_flop", Ring, popup: "RAISER IP");
        b.Stat(2, 1, "fold_to_cbet_flop", Ring, popup: "CALLER IP");
        b.Stat(2, 2, "cbet_turn", Ring, popup: "RAISER IP");
        b.Stat(2, 3, "wtsd", Ring, popup: "GENERAL");
        b.Stat(2, 4, "wsd", Ring, popup: "GENERAL");
        // В кеше пуш-фолда нет, и вкладки R3 в кеш-профиле тоже: вместо неё — холодный колл.
        if (mtt) b.Stat(2, 5, "open_shove", Ring, popup: "R3");
        else b.Stat(2, 5, "cold_call", Ring, popup: "GENERAL");
        return b.Build();
    }

    // ── GENERAL ──────────────────────────────────────────────────────────────

    private static HudPage BuildGeneral()
    {
        var b = new PageBuilder("GENERAL", colWidth: 34);
        b.Width(0, 110);
        int r = 0;

        b.Label(r, 0, "Никнейм", Theme.Name, "#FF8A50", bold: true);
        b.Heads(r, 1, PosCols);
        r++;
        b.Row(r++, 0, "Raise 1st", "rfi", PosFilters, Theme.Open, Theme.Open);
        b.Row(r++, 0, "Fold to 3Bet", "fold_to_3bet", PosFilters, Theme.Open, Theme.Open);
        r++;

        b.Label(r, 0, "", Theme.Sep);
        b.Heads(r, 1, PosCols);
        r++;
        b.Row(r++, 0, "Limp", "limp_any", PosFilters, Theme.Limp, Theme.Limp);
        b.Row(r++, 0, "Limp - Fold", "limp_fold", PosFilters, Theme.Limp, Theme.Limp);
        b.Row(r++, 0, "Limp - Call", "limp_call", PosFilters, Theme.Limp, Theme.Limp);
        b.Row(r++, 0, "Limp - Raise", "limp_raise", PosFilters, Theme.Limp, Theme.Limp);
        r++;

        // Холодные коллы: BB отдельно, потому что там колл считается против всего стола,
        // а BB vs SB — единственный спот, где до BB никто не имел слова.
        var ccCols = new[] { "TOT", "EP", "MP", "CO", "BTN", "SB", "BB", "BBvSB" };
        var ccFilters = new CellFilter?[]
        {
            Ring, Ring2(P.Ep), Ring2(P.Mp), Ring2(P.Co), Ring2(P.Btn), Ring2(P.Sb), Ring2(P.Bb),
            Ring2(P.Bb, vs: P.Sb),
        };
        b.Label(r, 0, "", Theme.Sep);
        b.Heads(r, 1, ccCols);
        r++;
        b.Row(r++, 0, "Cold Call 25+", "cold_call_any", Stacked(ccFilters, 25, null), Theme.Cold, Theme.Cold);
        b.Row(r++, 0, "Cold Call <25", "cold_call_any", Stacked(ccFilters, null, 25), Theme.Cold, Theme.Cold);
        r++;

        var isoCols = new[] { "TOT", "EP", "MP", "CO", "BTN", "SB", "BB" };
        var isoFilters = new CellFilter?[]
        {
            Ring, Ring2(P.Ep), Ring2(P.Mp), Ring2(P.Co), Ring2(P.Btn), Ring2(P.Sb), Ring2(P.Bb),
        };
        b.Label(r, 0, "", Theme.Sep);
        b.Heads(r, 1, isoCols);
        r++;
        b.Row(r++, 0, "Iso Raise", "iso_raise", isoFilters, Theme.Iso, Theme.Iso);
        b.Row(r++, 0, "Iso PUSH >10", "iso_shove", Stacked(isoFilters, 10, null), Theme.Iso, Theme.Iso);
        b.Row(r++, 0, "Iso PUSH <10", "iso_shove", Stacked(isoFilters, null, 10), Theme.Iso, Theme.Iso);
        r++;

        // Постфлоп-сводка: слева по улицам своя ставка, справа — реакция на чужую.
        b.Heads(r, 1, new[] { "F", "T", "R" });
        b.Heads(r, 5, new[] { "F", "T", "R" });
        r++;
        b.Label(r, 0, "Bet", Theme.Post, bold: true);
        b.Stat(r, 1, "bet_flop", Ring, Theme.Post); b.Stat(r, 2, "bet_turn", Ring, Theme.Post); b.Stat(r, 3, "bet_river", Ring, Theme.Post);
        b.Label(r, 4, "FvsB", Theme.Post, bold: true);
        b.Stat(r, 5, "fold_vs_bet_flop", Ring, Theme.Post); b.Stat(r, 6, "fold_vs_bet_turn", Ring, Theme.Post); b.Stat(r, 7, "fold_vs_bet_river", Ring, Theme.Post);
        r++;
        b.Label(r, 0, "Bet AL", Theme.Post, bold: true);
        b.Stat(r, 1, "bet_allin_flop", Ring, Theme.Post);
        b.Label(r, 4, "FvsB-AL", Theme.Post, bold: true);
        b.Stat(r, 5, "fold_vs_allin_flop", Ring, Theme.Post);
        r++;
        b.Label(r, 0, "FvsR", Theme.Post, bold: true);
        b.Stat(r, 1, "fold_vs_raise_flop", Ring, Theme.Post); b.Stat(r, 2, "fold_vs_raise_turn", Ring, Theme.Post); b.Stat(r, 3, "fold_vs_raise_river", Ring, Theme.Post);
        b.Label(r, 4, "Raise", Theme.Post, bold: true);
        b.Stat(r, 5, "raise_flop", Ring, Theme.Post); b.Stat(r, 6, "raise_turn", Ring, Theme.Post); b.Stat(r, 7, "raise_river", Ring, Theme.Post);
        r++;
        b.Label(r, 0, "3b", Theme.Post, bold: true);
        b.Stat(r, 1, "three_bet_flop", Ring, Theme.Post); b.Stat(r, 2, "three_bet_turn", Ring, Theme.Post);
        b.Label(r, 4, "Fto3b", Theme.Post, bold: true);
        b.Stat(r, 5, "fold_to_three_bet_flop", Ring, Theme.Post);
        r += 2;

        b.Label(r, 0, "Went to SD", bold: true); b.Stat(r++, 1, "wtsd", Ring);
        b.Label(r, 0, "Won When Saw Flop", bold: true); b.Stat(r++, 1, "wwsf", Ring);
        b.Label(r, 0, "Won at SD", bold: true); b.Stat(r++, 1, "wsd", Ring);
        r++;

        b.Label(r, 0, "Min3b", bold: true); b.Stat(r++, 1, "min_three_bet", Ring, showSample: true, colSpan: 2);
        b.Label(r, 0, "R1 <10bb", bold: true); b.Stat(r++, 1, "rfi", Ring2(to: 10), showSample: true, colSpan: 2);
        b.Label(r, 0, "3-5x Raise", bold: true); b.Stat(r++, 1, "open_3_5x", Ring, showSample: true, colSpan: 2);
        b.Label(r, 0, "Push-I [без SB]", bold: true);
        b.Stat(r++, 1, "open_shove", Ring2(new[] { "UTG", "UTG1", "MP", "LJ", "HJ", "CO", "BTN" }), showSample: true, colSpan: 2);
        b.Label(r, 0, "Push-I [SB]", bold: true); b.Stat(r++, 1, "open_shove", Ring2(P.Sb), showSample: true, colSpan: 2);
        return b.Build();
    }

    /// <summary>Тот же набор фильтров, но с дополнительной полосой стека.</summary>
    private static CellFilter?[] Stacked(CellFilter?[] src, double? from, double? to)
    {
        var res = new CellFilter?[src.Length];
        for (int i = 0; i < src.Length; i++)
            res[i] = PageBuilder.Merge(src[i], PageBuilder.F(stackFrom: from, stackTo: to));
        return res;
    }

    // ── R3: пуш-фолд по полосам стека ────────────────────────────────────────

    /// <summary>Полосы стека вкладки R3 — те же границы, что в оригинале.</summary>
    private static readonly (string Label, double From, double To)[] R3Bands =
    {
        ("0-7", 0, 7), ("7-11", 7, 11), ("11-15", 11, 15), ("15-20", 15, 20), ("20-30", 20, 30),
    };

    private static readonly (string Label, string[] Pos)[] R3Rows =
    {
        ("EP", P.Ep), ("MP", P.Mp), ("CO", P.Co), ("BTN", P.Btn), ("SB", P.Sb),
    };

    private static HudPage BuildR3()
    {
        var b = new PageBuilder("R3", colWidth: 32);
        b.Width(0, 64);
        b.Width(1, 40);
        b.Width(5, 16);
        b.Width(6, 64);
        b.Width(7, 40);
        int r = 0;
        b.Label(r, 0, "БЕЗ АНТЕ", Theme.Head, "#FFD54F", bold: true, colSpan: 5, align: "center");
        b.Label(r, 6, "АНТЕ", Theme.Head, "#FFD54F", bold: true, colSpan: 5, align: "center");
        r++;

        // Слева — раздачи без анте, справа — с анте: в турнирах это разные игры,
        // и смешивать их в одном числе бессмысленно.
        foreach (var (label, from, to) in R3Bands)
        {
            b.Heads(r, 2, new[] { "PUSH", "R1", "FtoRES" });
            b.Heads(r, 8, new[] { "PUSH", "R1", "FtoRES" });
            r++;
            b.Label(r, 0, label, Theme.Push, bold: true, rowSpan: R3Rows.Length);
            b.Label(r, 6, label, Theme.Push, bold: true, rowSpan: R3Rows.Length);
            foreach (var (posLabel, pos) in R3Rows)
            {
                b.Label(r, 1, posLabel, Theme.Head, Theme.HeadFg, bold: true, align: "center");
                b.Stat(r, 2, "open_shove", Ring2(pos, from: from, to: to, ante: false));
                b.Stat(r, 3, "rfi", Ring2(pos, from: from, to: to, ante: false));
                b.Stat(r, 4, "fold_to_3bet", Ring2(pos, from: from, to: to, ante: false));

                b.Label(r, 7, posLabel, Theme.Head, Theme.HeadFg, bold: true, align: "center");
                b.Stat(r, 8, "open_shove", Ring2(pos, from: from, to: to, ante: true));
                b.Stat(r, 9, "rfi", Ring2(pos, from: from, to: to, ante: true));
                b.Stat(r, 10, "fold_to_3bet", Ring2(pos, from: from, to: to, ante: true));
                r++;
            }
        }
        return b.Build();
    }

    // ── 3 BET: матрица «позиция × против кого» ───────────────────────────────

    private static readonly (string Label, string[] Pos)[] MatrixRows =
    {
        ("EP", P.Ep), ("MP", P.Mp), ("CO", P.Co), ("BTN", P.Btn), ("SB", P.Sb), ("BB", P.Bb),
    };

    /// <summary>
    /// Блок 3-бета: строка «всего по позициям» и под ней треугольник «из позиции против позиции».
    /// Треугольник, а не квадрат: против того, кто сидит позже, 3-бетить нельзя — он ещё не действовал.
    /// </summary>
    private static int ThreeBetBlock(PageBuilder b, int row, int col, string title, string titleBg, CellFilter baseFilter)
    {
        b.Label(row, col, title, titleBg, "#FFFFFF", bold: true, colSpan: 7, align: "center");
        row++;
        b.Label(row, col, "", Theme.Head);
        b.Heads(row, col + 1, new[] { "Total", "EP", "MP", "CO", "BTN", "SB" });
        row++;
        b.Label(row, col, "3 BET", Theme.Head, Theme.HeadFg, bold: true);
        b.Stat(row, col + 1, "three_bet", baseFilter, bold: true);
        for (int i = 0; i < MatrixRows.Length - 1; i++)
            b.Stat(row, col + 2 + i, "three_bet", PageBuilder.Merge(baseFilter, PageBuilder.F(pos: MatrixRows[i].Pos)));
        row += 2;

        for (int i = 0; i < MatrixRows.Length; i++)
        {
            var (label, pos) = MatrixRows[i];
            b.Label(row, col, label + " vs", Theme.Head, Theme.HeadFg, bold: true);
            // Против всех, кто действовал раньше: колонок ровно столько, сколько таких позиций.
            for (int j = 0; j < i; j++)
                b.Stat(row, col + 2 + j, "three_bet",
                    PageBuilder.Merge(baseFilter, PageBuilder.F(pos: pos, vs: MatrixRows[j].Pos)));
            row++;
        }
        return row;
    }

    private static HudPage BuildThreeBet()
    {
        var b = new PageBuilder("3 BET", colWidth: 32);
        b.Width(0, 56);
        b.Width(8, 16);
        b.Width(9, 56);

        b.Label(0, 10, "Fold to 4b", Theme.Head, Theme.HeadFg, bold: true, colSpan: 2);
        b.Stat(0, 12, "fold_to_4bet", Ring);
        b.Label(0, 13, "4bet", Theme.Head, Theme.HeadFg, bold: true);
        b.Stat(0, 14, "four_bet", Ring);

        int left = 1, right = 1;
        // Слева «не олл-ин», справа «олл-ин»: 3-бет фишками и 3-бет пушем — разные решения.
        left = ThreeBetBlock(b, left, 0, "NO ALL-IN", "#8C3A2E", Ring2(allIn: false)) + 1;
        right = ThreeBetBlock(b, right, 9, "ALL-IN", "#8C7A1F", Ring2(allIn: true)) + 1;
        left = ThreeBetBlock(b, left, 0, "25+", "#8C3A2E", Ring2(from: 25)) + 1;
        right = ThreeBetBlock(b, right, 9, "25+", "#8C7A1F", Ring2(from: 25, allIn: true)) + 1;
        ThreeBetBlock(b, left, 0, "15-25", "#8C3A2E", Ring2(from: 15, to: 25));
        ThreeBetBlock(b, right, 9, "15-25", "#8C7A1F", Ring2(from: 15, to: 25, allIn: true));
        return b.Build();
    }

    // ── SQZ ──────────────────────────────────────────────────────────────────

    private static HudPage BuildSqueeze()
    {
        var b = new PageBuilder("SQZ", colWidth: 34);
        b.Width(0, 52);
        b.Width(1, 52);
        b.Width(9, 16);
        int r = 0;
        var blocks = new (string Title, double? From, double? To, bool? AllIn)[]
        {
            ("SQZ ALL 30+", 30, null, true),
            ("SQZ NO-ALL 30+", 30, null, false),
            ("SQZ ALL 15-30", 15, 30, true),
            ("SQZ NO-ALL 15-30", 15, 30, false),
        };
        foreach (var (title, from, to, allIn) in blocks)
        {
            b.Label(r, 0, title, Theme.Head, Theme.HeadFg, bold: true, colSpan: 2);
            b.Heads(r, 2, new[] { "TOT", "EP", "MP", "CO", "BTN", "SB", "BB" });
            b.Heads(r, 10, new[] { "EP-BTN", "SB-BB" });
            r++;
            var f = Ring2(from: from, to: to, allIn: allIn);
            b.Stat(r, 2, "squeeze", f);
            b.Stat(r, 3, "squeeze", PageBuilder.Merge(f, PageBuilder.F(pos: P.Ep)));
            b.Stat(r, 4, "squeeze", PageBuilder.Merge(f, PageBuilder.F(pos: P.Mp)));
            b.Stat(r, 5, "squeeze", PageBuilder.Merge(f, PageBuilder.F(pos: P.Co)));
            b.Stat(r, 6, "squeeze", PageBuilder.Merge(f, PageBuilder.F(pos: P.Btn)));
            b.Stat(r, 7, "squeeze", PageBuilder.Merge(f, PageBuilder.F(pos: P.Sb)));
            b.Stat(r, 8, "squeeze", PageBuilder.Merge(f, PageBuilder.F(pos: P.Bb)));
            // Справа — против кого сквиз: опен с ранних мест и опен с блайндов давят по-разному.
            b.Stat(r, 10, "squeeze", PageBuilder.Merge(f, PageBuilder.F(open: P.EpBtn)));
            b.Stat(r, 11, "squeeze", PageBuilder.Merge(f, PageBuilder.F(open: P.SbBb)));
            r++;
            b.Label(r, 2, "F-SQZ", Theme.Head, Theme.HeadFg, bold: true);
            b.Stat(r, 3, "fold_to_squeeze", f, Theme.Fold);
            r += 2;
        }
        return b.Build();
    }

    // ── Dynamic: защита блайндов по полосам стека ────────────────────────────

    private static HudPage BuildDynamic()
    {
        var b = new PageBuilder("DYNAMIC", colWidth: 34);
        var spots = new (string Title, string[] Pos, string[] Vs)[]
        {
            ("BB vs BTN", P.Bb, P.Btn), ("SB vs BTN", P.Sb, P.Btn), ("BB vs SB", P.Bb, P.Sb),
        };
        var bands = new (string Label, double? From, double? To)[]
        {
            ("<10", null, 10), ("10-15", 10, 15), ("15-20", 15, 20), ("20+", 20, null),
        };
        for (int s = 0; s < spots.Length; s++)
        {
            var (title, pos, vs) = spots[s];
            int col = s * 6;
            b.Width(col, 62);
            int r = 0;
            b.Label(r, col, title, Theme.Head, "#FFD54F", bold: true);
            r++;
            foreach (var (label, from, to) in bands)
            {
                var f = Ring2(pos, vs: vs, from: from, to: to);
                b.Label(r, col, label, Theme.Head, "#FF8A80", bold: true);
                b.Heads(r, col + 1, new[] { "Fold", "Call", "3bet", "PUSH" });
                r++;
                b.Stat(r, col + 1, "fold_vs_open", f, Theme.Fold);
                b.Stat(r, col + 2, "call_vs_open", f, Theme.Call);
                b.Stat(r, col + 3, "three_bet", f, Theme.Bet);
                b.Stat(r, col + 4, "resteal_shove", f, Theme.Push);
                r++;
            }
        }
        return b.Build();
    }
}
