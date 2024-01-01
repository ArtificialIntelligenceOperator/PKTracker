namespace PKTracker.Engine.Hud;

/// <summary>Встроенные раскладки: стилы, короткие стеки, хедз-ап и 3-max.</summary>
public static partial class DefaultProfiles
{
    // ── STEALS: защита блайндов против конкретного стилера ───────────────────

    private static readonly (string Row, string[] Pos, (string Label, string[] Vs)[] Vs)[] StealSpots =
    {
        ("BB vs", P.Bb, new[] { ("BTN", P.Btn), ("SB", P.Sb), ("CO", P.Co) }),
        ("SB vs", P.Sb, new[] { ("BTN", P.Btn), ("CO", P.Co) }),
        ("BTN vs", P.Btn, new[] { ("CO", P.Co) }),
    };

    /// <summary>
    /// Полосы стека вкладки STEALS. В турнире стил решает стек в блайндах, в кеше стек почти всегда
    /// около сотни — те же полосы там дали бы четыре колонки, три из которых всегда пусты.
    /// </summary>
    private static (string Label, double? From, double? To)[] StealBands(bool mtt) => mtt
        ? new (string, double?, double?)[] { ("12-20", 12, 20), ("20-30", 20, 30), ("30-50", 30, 50), ("51+", 51, null) }
        : new (string, double?, double?)[] { ("Все", null, null), ("<40", null, 40), ("40-100", 40, 100), ("100+", 100, null) };

    private static HudPage BuildSteals(bool mtt = true)
    {
        var b = new PageBuilder("STEALS", colWidth: 38);
        var bands = StealBands(mtt);
        int row = 0;
        foreach (var (rowLabel, pos, opponents) in StealSpots)
        {
            for (int bi = 0; bi < bands.Length; bi++)
            {
                var (bandLabel, from, to) = bands[bi];
                int col = bi * (opponents.Length + 2);
                b.Width(col, 52);
                int r = row;
                b.Label(r, col, bi == 0 ? rowLabel : "", Theme.Head, "#FFD54F", bold: true);
                b.Heads(r, col + 1, opponents.Select(o => (string?)o.Label).ToArray());
                r++;
                // Три реакции на стил подряд: фолд, колл, 3-бет. Сумма близка к 100 —
                // если сильно меньше, часть рук ушла в олл-ин-рестил, он на вкладке DYNAMIC.
                var actions = new (string Label, string Code, string Color)[]
                {
                    ("Fold", "fold_vs_open", "#FFFFFF"), ("Call", "call_vs_open", "#81C784"), ("3bet", "three_bet", "#EF5350"),
                };
                foreach (var (actLabel, code, color) in actions)
                {
                    b.Label(r, col, bi == 0 ? bandLabel : "", null, "#FFD54F", bold: true);
                    b.Label(r, col + 1, actLabel, null, color, bold: true, colSpan: opponents.Length);
                    r++;
                    for (int oi = 0; oi < opponents.Length; oi++)
                        b.Stat(r, col + 1 + oi, code, Ring2(pos, vs: opponents[oi].Vs, from: from, to: to), fg: color);
                    r++;
                }
            }
            row += 8;
        }
        return b.Build();
    }

    private static HudPage BuildStealsShort()
    {
        var b = new PageBuilder("STEALS SHORT", colWidth: 44);
        b.Width(0, 52);
        var bands = new (string Label, double From, double To)[] { ("11-15", 11, 15), ("15-20", 15, 20) };
        var cols = new (string Label, string[] Pos, string[]? Vs)[]
        {
            ("CO", P.Co, null), ("BTN", P.Btn, null), ("SB", P.Sb, null),
            ("BBvsBTN", P.Bb, P.Btn), ("BBvsSB", P.Bb, P.Sb),
        };
        b.Heads(0, 1, cols.Select(c => (string?)c.Label).ToArray());
        int r = 1;
        foreach (var (label, from, to) in bands)
        {
            b.Label(r, 0, label, Theme.Head, Theme.HeadFg, bold: true, rowSpan: 2);
            for (int i = 0; i < cols.Length; i++)
            {
                var (_, pos, vs) = cols[i];
                var f = Ring2(pos, vs: vs, from: from, to: to);
                // Первая строка — агрессия (стил или 3-бет), вторая — пас.
                bool defend = vs != null;
                b.Stat(r, 1 + i, defend ? "three_bet" : "rfi", f, fg: defend ? "#4FC3F7" : "#FFD54F");
                b.Stat(r + 1, 1 + i, defend ? "fold_vs_open" : "open_shove", f, fg: defend ? "#EF5350" : "#B388FF");
            }
            r += 3;
        }
        return b.Build();
    }

    // ── HU: хедз-ап ──────────────────────────────────────────────────────────

    private static CellFilter Hu(string[]? pos = null, string[]? vs = null, double? from = null, double? to = null) =>
        PageBuilder.F(pos: pos, vs: vs, stackFrom: from, stackTo: to, players: 2);

    private static HudPage BuildHeadsUp(bool mtt = true)
    {
        var b = new PageBuilder("HU", colWidth: 42);
        b.Width(0, 74);
        b.Width(5, 16);
        b.Width(6, 96);
        // Турнирный хедз-ап — это короткие стеки, кешевый — глубокие; полосы разные по той же причине.
        var bands1 = mtt
            ? new (string Label, double? From, double? To)[] { ("0-5", 0, 5), ("5-8", 5, 8), ("9-15", 9, 15), ("16+", 16, null) }
            : new (string Label, double? From, double? To)[] { ("<40", null, 40), ("40-75", 40, 75), ("75-150", 75, 150), ("150+", 150, null) };
        var bands2 = mtt
            ? new (string Label, double? From, double? To)[] { ("0-8", 0, 8), ("9-15", 9, 15), ("16+", 16, null) }
            : new (string Label, double? From, double? To)[] { ("<50", null, 50), ("50-120", 50, 120), ("120+", 120, null) };

        // Верх слева: что делает игрок на кнопке (в HU кнопка — это SB).
        int r = 0;
        b.Label(r, 0, "", Theme.Head);
        b.Label(r, 1, "ToT", Theme.Head, Theme.HeadFg, bold: true, align: "center");
        b.Heads(r, 2, bands1.Select(x => (string?)x.Label).ToArray(), Theme.Head, "#FFD54F");
        r++;
        var btnRows = new (string Label, string Code, string Color)[]
        {
            // В хедз-апе кнопка — это малый блайнд, поэтому лимп здесь только limp_any:
            // встроенный limp блайнды не считает и колонка была бы пустой всегда.
            ("OP", "rfi", "#FFD54F"), ("L", "limp_any", "#81C784"), ("R", "three_bet", "#EF5350"), ("FtoRES", "fold_to_3bet", "#4FC3F7"),
        };
        foreach (var (label, code, color) in btnRows)
        {
            b.Label(r, 0, label, null, color, bold: true);
            b.Stat(r, 1, code, Hu(P.Btn), bold: true);
            for (int i = 0; i < bands1.Length; i++)
                b.Stat(r, 2 + i, code, Hu(P.Btn, from: bands1[i].From, to: bands1[i].To));
            r += 2;
        }

        // Верх справа: постфлоп-сводка — в HU она решает не меньше, чем префлоп.
        int rr = 0;
        b.Heads(rr, 7, new[] { "F", "T", "R" });
        rr++;
        var huPost = new (string Label, string F, string T, string R)[]
        {
            ("B", "bet_flop", "bet_turn", "bet_river"),
            ("B-F", "fold_vs_raise_flop", "fold_vs_raise_turn", "fold_vs_raise_river"),
            ("FtoB", "fold_vs_bet_flop", "fold_vs_bet_turn", "fold_vs_bet_river"),
            ("R", "raise_flop", "raise_turn", "raise_river"),
        };
        foreach (var (label, fl, tu, ri) in huPost)
        {
            b.Label(rr, 6, label, null, "#FFD54F", bold: true);
            b.Stat(rr, 7, fl, Hu()); b.Stat(rr, 8, tu, Hu()); b.Stat(rr, 9, ri, Hu());
            rr++;
        }

        // Низ слева: что делает BB против кнопки.
        r += 1;
        int bottom = r;
        b.Label(r, 0, "", Theme.Head);
        b.Label(r, 1, "ToT", Theme.Head, Theme.HeadFg, bold: true, align: "center");
        b.Heads(r, 2, bands2.Select(x => (string?)x.Label).ToArray(), Theme.Head, "#FFD54F");
        r++;
        var bbRows = new (string Label, string Code, string Color)[]
        {
            ("Fold", "fold_vs_open", "#FFD54F"), ("G3bet", "three_bet", "#EF5350"),
            ("RES", "resteal_shove", "#FFD54F"), ("ftOP", "fold_to_shove", "#4FC3F7"),
        };
        foreach (var (label, code, color) in bbRows)
        {
            b.Label(r, 0, label, null, color, bold: true);
            b.Stat(r, 1, code, Hu(P.Bb), bold: true);
            for (int i = 0; i < bands2.Length; i++)
                b.Stat(r, 2 + i, code, Hu(P.Bb, from: bands2[i].From, to: bands2[i].To));
            r += 2;
        }

        // Низ справа: изоляция лимпа и реакция на неё — в HU лимп с кнопки идёт постоянно.
        int r2 = bottom;
        b.Label(r2, 6, "", Theme.Head);
        b.Label(r2, 7, "ToT", Theme.Head, Theme.HeadFg, bold: true, align: "center");
        b.Heads(r2, 8, bands2.Select(x => (string?)x.Label).ToArray(), Theme.Head, "#FFD54F");
        r2++;
        var isoRows = new (string Label, string Code, string Color)[]
        {
            ("ISO-R", "iso_raise", "#FF8A65"), ("ISO-P", "iso_shove", "#FFD54F"),
            ("F vs ISO-R", "fold_to_iso", "#FF8A65"), ("F vs ISO-P", "fold_to_shove", "#FFD54F"),
        };
        foreach (var (label, code, color) in isoRows)
        {
            b.Label(r2, 6, label, null, color, bold: true);
            b.Stat(r2, 7, code, Hu(), bold: true);
            for (int i = 0; i < bands2.Length; i++)
                b.Stat(r2, 8 + i, code, Hu(from: bands2[i].From, to: bands2[i].To));
            r2 += 2;
        }
        return b.Build();
    }

    // ── OP(HU): пуш/колл по точному числу bb ─────────────────────────────────

    private static HudPage BuildOpHu()
    {
        var b = new PageBuilder("OP(HU)", colWidth: 62);
        b.Width(0, 46);
        b.Width(3, 16);
        b.Width(4, 46);
        const string Bg = "#D8CBA8";
        const string Fg = "#1A1A1A";
        b.Label(0, 0, "SB", "#2E6B6B", "#FFFFFF", bold: true);
        b.Heads(0, 1, new[] { "Open push", "OVER-R" });
        b.Label(0, 4, "BB vs SB", "#2E6B6B", "#FFFFFF", bold: true);
        b.Heads(0, 5, new[] { "CALL OP", "OVER-R" });

        // Целые bb, а не полосы: на этих стеках разница в один блайнд меняет диапазон целиком.
        var sbBands = new (string Label, double From, double To)[]
        {
            ("3", 2.5, 3.5), ("4", 3.5, 4.5), ("5", 4.5, 5.5), ("6", 5.5, 6.5), ("7", 6.5, 7.5), ("8", 7.5, 8.5),
            ("9", 8.5, 9.5), ("10-11", 9.5, 11.5), ("12-13", 11.5, 13.5), ("14-15", 13.5, 15.5),
        };
        var bbBands = new (string Label, double From, double To)[]
        {
            ("3", 2.5, 3.5), ("4", 3.5, 4.5), ("5", 4.5, 5.5), ("6-7", 5.5, 7.5), ("8", 7.5, 8.5),
            ("9", 8.5, 9.5), ("10-13", 9.5, 13.5), ("14-16", 13.5, 16.5),
        };
        for (int i = 0; i < sbBands.Length; i++)
        {
            var (label, from, to) = sbBands[i];
            b.Label(i + 1, 0, label, "#3D7A7A", "#FFFFFF", bold: true);
            b.Stat(i + 1, 1, "open_shove", Hu(P.Btn, from: from, to: to), Bg, Fg);
            b.Stat(i + 1, 2, "over_shove", Hu(P.Btn, from: from, to: to), Bg, Fg);
        }
        for (int i = 0; i < bbBands.Length; i++)
        {
            var (label, from, to) = bbBands[i];
            b.Label(i + 1, 4, label, "#3D7A7A", "#FFFFFF", bold: true);
            b.Stat(i + 1, 5, "call_shove", Hu(P.Bb, from: from, to: to), Bg, Fg);
            b.Stat(i + 1, 6, "over_shove", Hu(P.Bb, from: from, to: to), Bg, Fg);
        }
        return b.Build();
    }

    // ── 3max ─────────────────────────────────────────────────────────────────

    private static HudPage BuildThreeMax()
    {
        var b = new PageBuilder("3MAX", colWidth: 46);
        b.Width(0, 64);
        b.Width(1, 40);
        const string Bg = "#D8CBA8";
        const string Fg = "#1A1A1A";
        var bands = new (string Label, double? From, double? To)[]
        {
            ("0-5", 0, 5), ("5-9", 5, 9), ("9-13", 9, 13), ("13+", 13, null),
        };
        var seats = new (string Label, string[] Pos)[] { ("BTN", P.Btn), ("SB", P.Sb) };
        int r = 0;
        b.Label(r, 0, "Никнейм", Theme.Name, "#FF8A50", bold: true, colSpan: 2);
        b.Heads(r, 2, new[] { "PUSH", "R1", "SB", "L", "L-F" });
        r++;
        foreach (var (bandLabel, from, to) in bands)
        {
            b.Label(r, 0, bandLabel, "#3D7A7A", "#FFFFFF", bold: true, rowSpan: seats.Length);
            foreach (var (seatLabel, pos) in seats)
            {
                var f = PageBuilder.F(pos: pos, stackFrom: from, stackTo: to, players: 3);
                b.Label(r, 1, seatLabel, "#2E6B6B", "#FFFFFF", bold: true);
                b.Stat(r, 2, "open_shove", f, Bg, "#B8860B", bold: true);
                b.Stat(r, 3, "rfi", f, Bg, Fg);
                b.Stat(r, 4, "fold_to_3bet", f, Bg, Fg);
                b.Stat(r, 5, "limp_any", f, Bg, Fg);
                b.Stat(r, 6, "limp_fold", f, Bg, Fg);
                r++;
            }
        }
        r++;
        // Сводная строка «открыл / открыл рейзом / сбросил на 3-бет» по местам.
        b.Label(r, 0, "OP/R1/Ft3b", Bg, Fg, bold: true, colSpan: 2);
        b.Label(r - 1, 2, "BTN", "#2E6B6B", "#FFFFFF", bold: true, colSpan: 3, align: "center");
        b.Label(r - 1, 5, "SB", "#2E6B6B", "#FFFFFF", bold: true, colSpan: 3, align: "center");
        var three = PageBuilder.F(players: 3);
        b.Stat(r, 2, "open_shove", PageBuilder.Merge(three, PageBuilder.F(pos: P.Btn)), Bg, "#B8860B", bold: true);
        b.Stat(r, 3, "rfi", PageBuilder.Merge(three, PageBuilder.F(pos: P.Btn)), Bg, "#C62828");
        b.Stat(r, 4, "fold_to_3bet", PageBuilder.Merge(three, PageBuilder.F(pos: P.Btn)), Bg, "#0277BD");
        b.Stat(r, 5, "open_shove", PageBuilder.Merge(three, PageBuilder.F(pos: P.Sb)), Bg, "#B8860B", bold: true);
        b.Stat(r, 6, "rfi", PageBuilder.Merge(three, PageBuilder.F(pos: P.Sb)), Bg, "#C62828");
        b.Stat(r, 7, "fold_to_3bet", PageBuilder.Merge(three, PageBuilder.F(pos: P.Sb)), Bg, "#0277BD");
        return b.Build();
    }
}
