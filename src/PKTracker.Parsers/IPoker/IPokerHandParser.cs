using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PKTracker.Core.Abstractions;
using PKTracker.Core.Model;
using PKTracker.Parsers.Common;

namespace PKTracker.Parsers.IPoker;

/// <summary>
/// Парсер iPoker (XML-истории сети iPoker/Playtech: Winamax-соседи, Betsson, Unibet и др.).
/// Холдем (Holdem NL/PL/FL), турниры и кеш. Строгий: неизвестный тег, тип действия или игрок отклоняет раздачу.
/// Ловушки формата: (1) в файле НЕСКОЛЬКО корней («&lt;root&gt;&lt;general&gt;…&lt;/general&gt;&lt;game&gt;…&lt;/game&gt;&lt;/root&gt;» на каждую
/// раздачу, подряд) или один &lt;session&gt; с общим &lt;general&gt; и многими &lt;game&gt; — файл целиком не валидный XML,
/// поэтому режем по тегам сами; (2) последняя раздача файла бывает оборвана — это ошибка, а не молчаливый пропуск;
/// (3) sum действия — сколько игрок доложил, но у рейза (type 23) это уровень «до» на улице, а у олл-ина (type 7)
/// — доложенное, вид (колл/бет/рейз) определяем по контексту; (4) раунд 0 (блайнды, анте) и раунд 1 (префлоп) —
/// одна улица; (5) неуравненная часть ставки не возвращается строкой, а <c>win</c> игрока уже без неё —
/// возврат вычисляем по вкладам (BetLedger); (6) карты: «DK», «S2», «H10» — масть первой буквой, «X» — скрыто;
/// (7) суммы с разделителем тысяч («24,785»), в кеше — с символом валюты; (8) время — по часам компьютера игрока,
/// зона в файле не указана (см. <see cref="ClientTimeZone"/>).
/// </summary>
public sealed class IPokerHandParser : IHandHistoryParser
{
    public string RoomCode => "IPOKER";
    public HandBoundary Boundary { get; } = new(["<game "], "</game>", ClosedByMarker: true, WholeFile: true);

    /// <summary>Пояс компьютера игрока, в котором iPoker пишет startdate (по файлам корпуса — локальное время). Не задан — UTC.</summary>
    public TimeZoneInfo? ClientTimeZone { get; init; }

    public bool CanParse(ReadOnlySpan<char> fileHead)
    {
        var s = fileHead.TrimStart('﻿').TrimStart();
        if (!(s.StartsWith("<root") || s.StartsWith("<session") || s.StartsWith("<?xml"))) return false;
        // Само по себе «<root>» ничего не доказывает — проверяем признаки iPoker.
        return s.Contains("<gametype>", StringComparison.Ordinal) || s.Contains("gamecode=", StringComparison.Ordinal);
    }

    public IEnumerable<HandParseResult> Parse(string text, HandSourceInfo source, long baseOffset = 0)
    {
        int pos = 0;
        string? meta = null;
        while (pos < text.Length)
        {
            int g = text.IndexOf("<general>", pos, StringComparison.Ordinal);
            int game = text.IndexOf("<game ", pos, StringComparison.Ordinal);
            if (g >= 0 && (game < 0 || g < game))
            {
                // Общий <general> сессии (до <game>); вложенный в <game> сюда не попадает — он поглощается вместе с игрой.
                int ge = text.IndexOf("</general>", g, StringComparison.Ordinal);
                if (ge < 0)
                {
                    yield return Fail(text[g..], g, "Truncated session <general>");
                    yield break;
                }
                ge += "</general>".Length;
                meta = text[g..ge];
                pos = ge;
                continue;
            }
            if (game < 0) break;   // дальше только закрывающие теги
            int end = text.IndexOf("</game>", game, StringComparison.Ordinal);
            if (end < 0)
            {
                yield return Fail(text[game..], game, "Truncated game (no </game>): file is cut off");
                yield break;
            }
            end += "</game>".Length;
            var gameXml = text[game..end];
            pos = end;
            if (meta == null) { yield return Fail(gameXml, game, "Game without session <general> (no nickname/tournament data)"); continue; }
            var raw = "<root>" + meta + gameXml + "</root>";
            HandParseResult r;
            try
            {
                var h = ParseHand(raw, source);
                h.FileOffset = baseOffset + game;
                r = new HandParseResult { Hand = h, RawText = raw, Offset = baseOffset + game, SourceFile = source.FilePath };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or IndexOutOfRangeException or XmlException)
            {
                r = new HandParseResult { Error = ex.Message, RawText = raw, Offset = baseOffset + game, SourceFile = source.FilePath };
            }
            yield return r;
        }

        HandParseResult Fail(string rawText, int at, string msg) =>
            new() { Error = msg, RawText = rawText, Offset = baseOffset + at, SourceFile = source.FilePath };
    }

    public ParsedHand ParseHand(string raw, HandSourceInfo source)
    {
        var root = XElement.Parse(raw);
        var meta = root.Element("general") ?? throw new HandFormatError("No session <general>");
        var game = root.Element("game") ?? throw new HandFormatError("No <game>");
        var gen = game.Element("general") ?? throw new HandFormatError("No <general> inside <game>");

        var h = new ParsedHand { RoomCode = RoomCode, SourceFile = source.FilePath, RawText = raw, Scale = 2 };
        h.ExternalHandId = long.Parse(Attr(game, "gamecode"), NumberStyles.None, CultureInfo.InvariantCulture);

        var mode = Text(meta, "mode");
        if (mode != "real") throw new HandFormatError($"Unsupported mode '{mode}' (play money)");
        h.GameVariant = Variant(Text(meta, "gametype"));

        var tcode = Text(meta, "tournamentcode");
        var tablename = Text(meta, "tablename");
        if (tcode.Length > 0)
        {
            h.TournamentId = long.Parse(tcode, NumberStyles.None, CultureInfo.InvariantCulture);
            h.Currency = "CHP";
            h.GameKind = source.GameKind is GameKind.Sng ? GameKind.Sng : GameKind.Mtt;
            h.TableName = $"{Text(meta, "tournamentname")} #{h.TournamentId} {tablename}";
            h.ExternalGameId = source.ExternalGameId ?? "G" + h.TournamentId;
        }
        else
        {
            var cur = Text(meta, "currency");
            if (cur.Length != 3) throw new HandFormatError($"Cash game without currency code: '{cur}'");
            h.Currency = cur;
            h.GameKind = GameKind.Cash;
            h.TableName = tablename;
            h.ExternalGameId = source.ExternalGameId ?? "N" + tablename;
        }
        h.ExternalTableId = source.ExternalTableId ?? "T" + tablename;
        var tsize = Text(meta, "tablesize");
        if (tsize.Length > 0) h.MaxSeats = int.Parse(tsize, NumberStyles.None, CultureInfo.InvariantCulture);

        h.PlayedAtUtc = HhText.ToUtc(Text(gen, "startdate"), ClientTimeZone);
        h.SmallBlind = Money(Text(gen, "smallblind"));
        h.BigBlind = Money(Text(gen, "bigblind"));
        h.Ante = Money(Text(gen, "ante"));

        var playersEl = gen.Element("players") ?? throw new HandFormatError("No <players>");
        int dealerSeat = 0;
        foreach (var p in playersEl.Elements())
        {
            if (p.Name != "player") throw new HandFormatError($"Unknown element <{p.Name}> in <players>");
            int no = int.Parse(Attr(p, "seat"), NumberStyles.None, CultureInfo.InvariantCulture);
            var name = Attr(p, "name");
            if (name.Length == 0) throw new HandFormatError("Player without name");
            if (h.SeatByNumber(no) != null || h.SeatByName(name) != null) throw new HandFormatError($"Duplicate seat/name: {no} {name}");
            h.Seats.Add(new ParsedSeat { Seat = no, Name = name, Stack = Money(Attr(p, "chips")) });
            if (p.Attribute("dealer")?.Value == "1")
            {
                if (dealerSeat != 0) throw new HandFormatError("Two dealers");
                dealerSeat = no;
            }
        }
        if (h.Seats.Count < 2) throw new HandFormatError("Less than two players");
        // Мёртвая кнопка: игрок на кнопке вылетел, место пусто, и ни у кого dealer="1" нет. Номера места кнопки
        // тогда не знаем — ставим 0 (нет такого места), FinishFlags отметит DeadButton; позиции считает движок по порядку действий.
        h.ButtonSeat = dealerSeat;
        if (h.MaxSeats < h.Seats.Max(s => s.Seat)) h.MaxSeats = h.Seats.Max(s => s.Seat) <= 6 ? 6 : h.Seats.Max(s => s.Seat) <= 9 ? 9 : 10;

        var ledger = new BetLedger();
        var totalPut = new Dictionary<int, long>();
        var uncalled = new List<(int Seat, long Amount)>();
        var dealt = new HashSet<int>();
        int seq = 0;
        int curStreet = -1;
        var street = Street.Preflop;

        void Flush()
        {
            if (ledger.FlushInto(h, ref seq, street) is { } u) uncalled.Add(u);
        }

        foreach (var rnd in game.Elements())
        {
            if (rnd.Name == "general") continue;
            if (rnd.Name != "round") throw new HandFormatError($"Unknown element <{rnd.Name}> in <game>");
            int no = int.Parse(Attr(rnd, "no"), NumberStyles.None, CultureInfo.InvariantCulture);
            // Раунд 0 (анте/блайнды) и 1 (префлоп) — одна улица: вклады блайндов нужны уровню первого рейза.
            int st = no switch { 0 or 1 => 0, 2 => 1, 3 => 2, 4 => 3, _ => throw new HandFormatError($"Unknown round no={no}") };
            if (st != curStreet)
            {
                if (st < curStreet) throw new HandFormatError("Rounds go backwards");
                if (curStreet >= 0) Flush();
                curStreet = st;
                street = (Street)st;
            }

            foreach (var el in rnd.Elements())
            {
                if (el.Name == "cards") { Cards(h, el, dealt); continue; }
                if (el.Name != "action") throw new HandFormatError($"Unknown element <{el.Name}> in <round>");

                var seat = h.SeatByName(Attr(el, "player")) ?? throw new HandFormatError($"Action by unknown player '{Attr(el, "player")}'");
                int type = int.Parse(Attr(el, "type"), NumberStyles.None, CultureInfo.InvariantCulture);
                long sum = Money(Attr(el, "sum"));
                dealt.Add(seat.Seat);
                long Put() => totalPut.GetValueOrDefault(seat.Seat);
                bool AllInNow() => Put() >= seat.Stack;

                switch (type)
                {
                    case 15: // ante
                        totalPut[seat.Seat] = Put() + sum;
                        if (sum > h.Ante) h.Ante = sum;
                        h.Actions.Add(new ParsedAction { Seq = seq++, Street = Street.Preflop, Seat = seat.Seat, Type = ActionType.PostAnte, Amount = sum, IsAllIn = AllInNow() });
                        break;
                    case 1: // small blind
                    case 2: // big blind
                    {
                        var t = type == 1 ? ActionType.PostSb : ActionType.PostBb;
                        if (t == ActionType.PostBb && h.Actions.Any(a => a.Type == ActionType.PostBb)) t = ActionType.PostBbOot;
                        if (t == ActionType.PostSb && h.Actions.Any(a => a.Type == ActionType.PostSb)) t = ActionType.PostDead;
                        ledger.Add(seat.Seat, sum);
                        totalPut[seat.Seat] = Put() + sum;
                        h.Actions.Add(new ParsedAction { Seq = seq++, Street = Street.Preflop, Seat = seat.Seat, Type = t, Amount = sum, IsAllIn = AllInNow() });
                        break;
                    }
                    case 0:
                        h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = ActionType.Fold });
                        break;
                    case 4:
                        h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = ActionType.Check });
                        break;
                    case 3: // call
                    case 5: // bet
                        ledger.Add(seat.Seat, sum);
                        totalPut[seat.Seat] = Put() + sum;
                        h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = type == 3 ? ActionType.Call : ActionType.Bet, Amount = sum, IsAllIn = AllInNow() });
                        break;
                    case 23: // raise: sum — уровень «до» на улице
                    {
                        long add = sum - ledger.Put(seat.Seat);
                        if (add < 0) throw new HandFormatError($"Raise to {sum} is below already put {ledger.Put(seat.Seat)} by {seat.Name}");
                        ledger.Add(seat.Seat, add);
                        totalPut[seat.Seat] = Put() + add;
                        h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = ActionType.Raise, Amount = sum, IsAllIn = AllInNow() });
                        break;
                    }
                    case 7: // all-in: sum — доложенное; вид действия клиент не пишет
                    {
                        long after = ledger.Put(seat.Seat) + sum;
                        var t = ledger.Level == 0 ? ActionType.Bet : after <= ledger.Level ? ActionType.Call : ActionType.Raise;
                        ledger.Add(seat.Seat, sum);
                        totalPut[seat.Seat] = Put() + sum;
                        h.Actions.Add(new ParsedAction { Seq = seq++, Street = street, Seat = seat.Seat, Type = t, Amount = t == ActionType.Raise ? after : sum, IsAllIn = true });
                        break;
                    }
                    default:
                        throw new HandFormatError($"Unknown action type {type}: {el}");
                }
            }
        }
        if (curStreet < 0) throw new HandFormatError("No rounds");
        Flush();

        // Выигрыш и «кто раздан»: win игрока уже без неуравненной части ставки, банк = Σ win.
        long invested = totalPut.Values.Sum() - uncalled.Sum(u => u.Amount);
        foreach (var p in playersEl.Elements())
        {
            var seat = h.SeatByName(Attr(p, "name"))!;
            seat.Won = Money(p.Attribute("win")?.Value ?? "0");
            h.TotalPot += seat.Won;
            if (dealt.Contains(seat.Seat)) seat.Dealt = true;
            else if (seat.Won > 0 || Money(p.Attribute("bet")?.Value ?? "0") > 0) throw new HandFormatError($"Player {seat.Name} has bet/win but no cards or actions");
            else seat.SittingOut = true;
        }
        // Кеш: разница между вложенным и выигранным — рейк (iPoker отдельной строкой его не пишет).
        if (h.GameKind == GameKind.Cash)
        {
            long rake = invested - h.TotalPot;
            if (rake < 0) throw new HandFormatError($"Negative rake {rake}");
            h.Rake = rake;
        }

        // Hero — владелец сессии (nickname) с видимыми картами; чужие карманные карты скрыты «X X».
        var nick = Text(meta, "nickname");
        if (h.SeatByName(nick) is { CardsKnown: true } hero)
        {
            hero.IsHero = true;
            h.HeroName = hero.Name;
        }

        HhText.FinishFlags(h);
        HhText.ApplyHeroHint(h, source);
        return h;
    }

    private static void Cards(ParsedHand h, XElement el, HashSet<int> dealt)
    {
        var type = Attr(el, "type");
        var cards = ParseCards(el.Value);
        switch (type)
        {
            case "Pocket":
            {
                var seat = h.SeatByName(Attr(el, "player")) ?? throw new HandFormatError($"Cards for unknown player '{Attr(el, "player")}'");
                if (cards.Length != 2) throw new HandFormatError($"Expected 2 pocket cards (Omaha is not supported): {el}");
                for (int i = 0; i < 2; i++) if (cards[i].HasValue) seat.HoleCards[i] = cards[i];
                dealt.Add(seat.Seat);
                break;
            }
            case "Flop": AddBoard(h, cards, 0, 3); break;
            case "Turn": AddBoard(h, cards, 3, 1); break;
            case "River": AddBoard(h, cards, 4, 1); break;
            default: throw new HandFormatError($"Unknown cards type '{type}'");
        }
    }

    private static void AddBoard(ParsedHand h, Card?[] cards, int before, int count)
    {
        if (cards.Length != count || cards.Any(c => !c.HasValue)) throw new HandFormatError("Bad board cards");
        if (h.Boards.Count == 0) h.Boards.Add(new List<Card>());
        var b = h.Boards[0];
        if (b.Count != before) throw new HandFormatError($"Board has {b.Count} cards before street, expected {before}");
        b.AddRange(cards.Select(c => c!.Value));
    }

    /// <summary>«DK H5 C7» → Kd 5h 7c; «H10» → Th; «X» — скрыто (null).</summary>
    private static Card?[] ParseCards(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var res = new Card?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Equals("X", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Length < 2 || "CDHS".IndexOf(char.ToUpperInvariant(p[0])) < 0) throw new HandFormatError($"Bad card '{p}'");
            var rank = p[1..];
            if (rank == "10") rank = "T";
            if (!Card.TryParse(rank + char.ToLowerInvariant(p[0]), out var c)) throw new HandFormatError($"Bad card '{p}'");
            res[i] = c;
        }
        return res;
    }

    private static string Variant(string s) => s switch
    {
        "Holdem NL" => "NLHE",
        "Holdem PL" => "PLHE",
        "Holdem FL" => "FLHE",
        _ => throw new HandFormatError($"Unsupported game variant: {s}"),
    };

    private static string Attr(XElement e, string name) =>
        e.Attribute(name)?.Value ?? throw new HandFormatError($"<{e.Name}> without '{name}' attribute");

    private static string Text(XElement e, string name) => e.Element(name)?.Value.Trim() ?? "";

    /// <summary>Пусто = 0 (ante в турнирах без анте бывает пустым).</summary>
    private static long Money(string s) => s.Trim().Length == 0 ? 0 : HhText.Amount(s);
}
