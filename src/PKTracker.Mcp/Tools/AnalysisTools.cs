using System.ComponentModel;
using ModelContextProtocol.Server;
using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Stats;

namespace PKTracker.Mcp.Tools;

/// <summary>Чтение и анализ сыгранных раздач.</summary>
[McpServerToolType]
public sealed class AnalysisTools(TrackerContext ctx)
{
    [McpServerTool(Name = "tracker_status", ReadOnly = true, Title = "Состояние трекера")]
    [Description("Рум, Hero-аккаунты, число раздач, период, число статов, включена ли анонимизация и разрешена ли запись. Вызывай первым.")]
    public async Task<string> TrackerStatus(CancellationToken ct)
    {
        return await TrackerContext.Guard(async () =>
        {
            // Состояние трекера — не анализ: отвечает и при запущенном клиенте рума.
            var host = await ctx.HostAsync(ct);
            await using var conn = await host.Db.Reports.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*), count(*) FILTER (WHERE game_kind = 0), count(*) FILTER (WHERE game_kind <> 0), min(played_at), max(played_at),
                       (SELECT count(*) FROM player WHERE room_id = $1), (SELECT count(*) FROM tournament)
                  FROM hands WHERE room_id = $1
                """, conn);
            cmd.Parameters.AddWithValue(host.Db.RoomId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return TrackerContext.ToJson(new
            {
                room = host.Db.RoomCode,
                heroes = host.HeroNames,
                hands = r.GetInt64(0),
                cash_hands = r.GetInt64(1),
                tournament_hands = r.GetInt64(2),
                first_hand = r.IsDBNull(3) ? null : TrackerContext.Iso(r.GetDateTime(3)),
                last_hand = r.IsDBNull(4) ? null : TrackerContext.Iso(r.GetDateTime(4)),
                players = r.GetInt64(5),
                tournaments = r.GetInt64(6),
                stats = host.Stats.Count,
                anonymize_opponents = ctx.Anonymizer.Enabled,
                writes_allowed = ctx.WritesAllowed,
                money_units = "cash: доллары; турниры: фишки",
            });
        });
    }

    [McpServerTool(Name = "get_overview", ReadOnly = true, Title = "Обзор результатов Hero")]
    [Description("Сводка Hero: раздачи, выигрыш, bb/100, All-in EV (Chip EV для турниров), рейк, VPIP/PFR, часы, сессии, разбивки по позициям, лимитам и числу игроков.")]
    public async Task<string> GetOverview([Description("cash или mtt")] string game = "cash", CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.AnalysisHostAsync(ct);
            var kind = TrackerContext.ParseGame(game);
            var rep = new ReportRepository(host.Db);
            var o = await rep.OverviewAsync(kind, ctx.Settings.SessionGapMinutes, ct);
            object Group(GroupRow g) => new { key = g.Key, hands = g.Hands, bb100 = Math.Round(g.Bb100, 2), vpip = Math.Round(g.VpipPct, 1), pfr = Math.Round(g.PfrPct, 1) };
            return TrackerContext.ToJson(new
            {
                game = kind == GameKind.Cash ? "cash" : "mtt",
                hands = o.Hands,
                net_won = TrackerContext.Amount(o.NetWon),
                bb100 = Math.Round(o.Bb100, 2),
                allin_ev_won = TrackerContext.Amount(o.EvNetWon),
                allin_ev_bb100 = Math.Round(o.EvBb100, 2),
                luck_in_allins = TrackerContext.Amount(o.NetWon - o.EvNetWon),
                allin_hands = o.AllInHands,
                won_at_showdown = TrackerContext.Amount(o.WonAtSd),
                won_without_showdown = TrackerContext.Amount(o.WonNonSd),
                rake = kind == GameKind.Cash ? TrackerContext.Amount(o.Rake) : (double?)null,
                vpip = Math.Round(o.VpipPct, 1),
                pfr = Math.Round(o.PfrPct, 1),
                hours = Math.Round(o.Hours, 1),
                sessions = o.Sessions,
                by_position = (await rep.ByPositionAsync(kind, ct)).Select(Group),
                by_limit = kind == GameKind.Cash ? (await rep.ByLimitAsync(ct)).Select(Group) : null,
                by_players_dealt = (await rep.ByTableSizeAsync(kind, ct)).Select(Group),
            });
        });
    }

    [McpServerTool(Name = "list_sessions", ReadOnly = true, Title = "Кеш-сессии")]
    [Description("Кеш-сессии Hero (разрыв между руками больше настройки SessionGapMinutes — новая сессия), новые первыми.")]
    public async Task<string> ListSessions([Description("Сколько сессий вернуть")] int limit = 20, CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.AnalysisHostAsync(ct);
            var rows = await new ReportRepository(host.Db).SessionsAsync(ctx.Settings.SessionGapMinutes, ct: ct);
            return TrackerContext.ToJson(rows.Take(Math.Clamp(limit, 1, 500)).Select(s => new
            {
                start = TrackerContext.Iso(s.Start),
                end = TrackerContext.Iso(s.End),
                minutes = (int)s.Duration.TotalMinutes,
                hands = s.Hands,
                tables = s.Tables,
                net_won = TrackerContext.Amount(s.NetWon),
                bb100 = Math.Round(s.Bb100, 2),
            }));
        });
    }

    [McpServerTool(Name = "list_tournaments", ReadOnly = true, Title = "Турниры")]
    [Description("Турниры Hero: бай-ин, место, призы (из .ots), результат по фишкам, Chip EV, число олл-инов и поправка $EV (ICM для SNG/Spins, линейная для MTT).")]
    public async Task<string> ListTournaments([Description("Сколько турниров вернуть")] int limit = 50, CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.AnalysisHostAsync(ct);
            var rows = await new ReportRepository(host.Db).TournamentsAsync(ct);
            var evs = await new TournamentEvService(host.Db).ComputeAsync(ct);
            return TrackerContext.ToJson(rows.Take(Math.Clamp(limit, 1, 1000)).Select(t =>
            {
                evs.TryGetValue(t.TournamentId, out var ev);
                return new
                {
                    tournament_id = t.TournamentId,
                    name = t.Name,
                    started = t.Started is { } d ? TrackerContext.Iso(d) : null,
                    hands = t.Hands,
                    currency = t.Currency,
                    buy_in = t.BuyIn is { } b ? TrackerContext.Amount(b) : (double?)null,
                    fee = t.Fee is { } f ? TrackerContext.Amount(f) : (double?)null,
                    entries = t.Entries,
                    finish = t.BestFinish,
                    field = t.PlayerCount,
                    won = t.HasResult ? TrackerContext.Amount(t.Won) : (double?)null,
                    profit = t.HasResult && t.BuyIn != null ? TrackerContext.Amount(t.Won - t.Cost) : (double?)null,
                    chips_net = ev != null ? ev.ChipNet / 100 : (long?)null,
                    chip_ev_net = ev != null ? ev.ChipEvNet / 100 : (long?)null,
                    allin_hands = ev?.AllInHands,
                    dollar_ev_adjustment = ev?.DollarEvAdjustment is { } adj ? Math.Round(adj / 100, 2) : (double?)null,
                    dollar_ev_method = ev?.DollarMethod,
                };
            }));
        });
    }

    [McpServerTool(Name = "search_hands", ReadOnly = true, Title = "Поиск рук Hero")]
    [Description("Поиск рук Hero, новые первыми. filter — выражение над HandPlayerFacts на языке статов (см. pktracker://docs/stats), например " +
                 "'Pfr && SawShowdown && !WonAtShowdown', 'NetWonBbX100 <= -5000', 'HoleClass == 0 && Position == \"BB\"', " +
                 "'EvNetWon != null && NetWon < EvNetWon'. Пустой filter — все руки.")]
    public async Task<string> SearchHands(
        [Description("Выражение-фильтр или пустая строка")] string filter = "",
        [Description("cash, mtt или all")] string game = "all",
        [Description("Максимум рук (до 500)")] int limit = 50,
        [Description("Пропустить столько подходящих рук (пагинация)")] int offset = 0,
        CancellationToken ct = default)
    {
        var host = await ctx.AnalysisHostAsync(ct);
        GameKind? kind = game.Trim().ToLowerInvariant() == "all" ? null : TrackerContext.ParseGame(game);
        var expr = string.IsNullOrWhiteSpace(filter) ? "true" : filter;
        var compiled = StatCompiler.CompileHandFilter(expr);
        var (rows, scanned) = await new ReportRepository(host.Db).HeroHandsFilteredAsync(compiled, kind, Math.Clamp(limit, 1, 500), Math.Max(0, offset), ct);
        return TrackerContext.ToJson(new
        {
            scanned,
            found = rows.Count,
            hands = rows.Select(h => new
            {
                hand_id = h.ExternalId,
                played_at = TrackerContext.Iso(h.PlayedAt),
                table = h.Table,
                game = h.Kind.ToString().ToLowerInvariant(),
                stakes = h.Stakes,
                position = h.Position.ToString(),
                cards = h.Cards,
                net_won = TrackerContext.Amount(h.NetWon),
                net_bb = TrackerContext.Bb(h.NetBbX100),
                allin_ev_won = h.EvNetWon is { } ev ? TrackerContext.Amount(ev) : (double?)null,
                pot = TrackerContext.Amount(h.TotalPot),
            }),
        });
    }

    [McpServerTool(Name = "get_hand", ReadOnly = true, Title = "Раздача целиком")]
    [Description("Полная раздача: исходный текст hand history, игроки с позициями, стеками в bb, картами (если известны) и результатом, доски, действия с суммами в bb.")]
    public async Task<string> GetHand([Description("Номер раздачи рума (hand_id из search_hands)")] long hand_id, CancellationToken ct = default)
    {
        var host = await ctx.AnalysisHostAsync(ct);
        var rep = new ReportRepository(host.Db);
        var pk = await rep.ResolveHandPkAsync(hand_id, ct) ?? throw new ArgumentException($"Раздача {hand_id} не найдена");
        var hand = await rep.LoadReplayAsync(pk, ct) ?? throw new ArgumentException($"Раздача {hand_id} не найдена");
        var raw = await rep.RawHandTextAsync(pk, ct);
        var an = ctx.Anonymizer;
        double Bb(long v) => hand.Bb > 0 ? Math.Round((double)v / hand.Bb, 2) : 0;
        var names = hand.Seats.ToDictionary(s => s.Seat, s => an.Alias(s.Name));
        return TrackerContext.ToJson(new
        {
            hand_id = hand.ExternalId,
            played_at = TrackerContext.Iso(hand.PlayedAt),
            table = hand.Table,
            game = hand.Kind.ToString().ToLowerInvariant(),
            sb = TrackerContext.Amount(hand.Sb),
            bb = TrackerContext.Amount(hand.Bb),
            ante = TrackerContext.Amount(hand.Ante),
            max_seats = hand.MaxSeats,
            flags = hand.Flags.ToString(),
            boards = hand.Boards.Select(b => string.Join(" ", b)),
            total_pot_bb = Bb(hand.TotalPot),
            rake = TrackerContext.Amount(hand.Rake),
            players = hand.Seats.Select(s => new
            {
                seat = s.Seat,
                name = names[s.Seat],
                hero = s.IsHero,
                position = s.Position.ToString(),
                stack_bb = Bb(s.StartStack),
                cards = s.C1 is { } a && s.C2 is { } b ? $"{a} {b}" : null,
                net_bb = Bb(s.NetWon),
            }),
            actions = hand.Actions.Select(a => new
            {
                street = a.Street.ToString(),
                player = names.GetValueOrDefault(a.Seat, $"seat {a.Seat}"),
                action = a.Action.ToString(),
                amount_bb = a.Amount != 0 ? Bb(a.Amount) : (double?)null,
                to_bb = a.Action == ActionType.Raise ? Bb(a.ToAmount) : (double?)null,
                pot_before_bb = Bb(a.PotBefore),
                all_in = a.AllIn ? true : (bool?)null,
            }),
            raw_text = raw == null ? null : an.ScrubText(raw, hand.Seats.Select(s => s.Name)),
        });
    }

    [McpServerTool(Name = "find_players", ReadOnly = true, Title = "Игроки")]
    [Description("Игроки из базы, отсортированные по числу рук с Hero. prefix — начало ника (или пусто для топа оппонентов). При анонимизации ники возвращаются псевдонимами.")]
    public async Task<string> FindPlayers([Description("Начало ника или пусто")] string prefix = "", [Description("Максимум")] int limit = 30, CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.AnalysisHostAsync(ct);
            var rows = await new ReportRepository(host.Db).SearchPlayersAsync(prefix, Math.Clamp(limit, 1, 500), ct);
            return TrackerContext.ToJson(rows.Select(p => new
            {
                player = ctx.Anonymizer.Alias(p.Nickname),
                hero = ctx.Anonymizer.IsHero(p.Nickname),
                hands = p.Hands,
                last_seen = p.LastSeen is { } d ? TrackerContext.Iso(d) : null,
            }));
        });
    }

    [McpServerTool(Name = "get_player_stats", ReadOnly = true, Title = "Статы игрока")]
    [Description("Все статы игрока (как в HUD) с размером выборки. player — ник Hero, ник оппонента или псевдоним Villain_XXXXXX.")]
    public async Task<string> GetPlayerStats([Description("Ник или псевдоним")] string player, [Description("cash или mtt")] string game = "cash", CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.AnalysisHostAsync(ct);
            var kind = TrackerContext.ParseGame(game);
            var p = await host.Hud.FindPlayerAsync(ctx.Anonymizer.Resolve(player), ct) ?? throw new ArgumentException($"Игрок '{player}' не найден");
            var set = await host.Hud.GetAsync(p.PlayerId, kind, ct);
            return TrackerContext.ToJson(new
            {
                player = ctx.Anonymizer.Alias(p.Nickname),
                game = kind == GameKind.Cash ? "cash" : "mtt",
                stats = host.Stats.Select(st =>
                {
                    var (v, n) = set.Evaluate(st.Code);
                    return new { code = st.Code, title = st.Definition.Title, value = v is { } x ? Math.Round(x, 2) : (double?)null, sample = n };
                }).Where(s => s.value != null),
            });
        });
    }

    [McpServerTool(Name = "get_stat_details", ReadOnly = true, Title = "Разбивка стата (попап)")]
    [Description("Попап стата: разбивка по позициям и глубине стека, что игрок делает дальше (Next Actions), сайзинги, сила руки, текстуры бордов, " +
                 "показанные руки в споте (сетка 13×13) и последние раздачи. Считается по последним 50 000 точкам решения.")]
    public async Task<string> GetStatDetails(
        [Description("Ник или псевдоним")] string player,
        [Description("Код стата, см. list_stat_definitions")] string stat_code,
        [Description("cash или mtt")] string game = "cash",
        CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.AnalysisHostAsync(ct);
            var kind = TrackerContext.ParseGame(game);
            var stat = host.Stats.FirstOrDefault(s => s.Code == stat_code) ?? throw new ArgumentException($"Стат '{stat_code}' не найден");
            var p = await host.Hud.FindPlayerAsync(ctx.Anonymizer.Resolve(player), ct) ?? throw new ArgumentException($"Игрок '{player}' не найден");
            var d = await new StatPopupService(host.Db).LoadAsync(p.PlayerId, stat, kind, ct);
            object Rows(IEnumerable<CountRow> rows) => rows.Select(r => new { key = r.Key, count = r.Count, pct = Math.Round(r.Pct, 1) });
            return TrackerContext.ToJson(new
            {
                player = ctx.Anonymizer.Alias(p.Nickname),
                stat = stat.Code,
                opportunity = stat.Definition.Opportunity,
                hit = stat.Definition.Hit,
                cases = d.Cases,
                hits = d.Hits,
                pct = d.Cases > 0 ? Math.Round(d.Hits * 100.0 / d.Cases, 1) : (double?)null,
                by_position = d.ByPosition.Select(r => new { key = r.Key, cases = r.Cases, hits = r.Hits, pct = Math.Round(r.Pct, 1) }),
                by_stack = d.ByStack.Select(r => new { key = r.Key, cases = r.Cases, hits = r.Hits, pct = Math.Round(r.Pct, 1) }),
                next_actions = Rows(d.NextActions),
                bet_sizing = Rows(d.BetSizing),
                hand_strength = Rows(d.HandStrength),
                boards = Rows(d.Boards),
                shown_hands = Enumerable.Range(0, 169).Where(i => d.RangeHits[i] > 0).OrderByDescending(i => d.RangeHits[i])
                    .Select(i => new { hand = Card.HoleClassName(i), count = d.RangeHits[i] }),
                recent = d.Hands.Take(30).Select(h => new
                {
                    hand_id_internal = h.HandPk, played_at = TrackerContext.Iso(h.PlayedAt), street = h.Street, position = h.Position.ToString(),
                    action = h.Action, cards = h.Cards, size_pct = h.SizePct,
                }),
            });
        });
    }

    [McpServerTool(Name = "find_leaks", ReadOnly = true, Title = "Ликфайндер")]
    [Description("Статы Hero против пула (все остальные игроки из базы пользователя), отсортированные по значимости отклонения (score ≈ z-оценка).")]
    public async Task<string> FindLeaks([Description("cash или mtt")] string game = "cash", CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.AnalysisHostAsync(ct);
            var rows = await LeakFinder.ComputeAsync(host, TrackerContext.ParseGame(game), ct);
            return TrackerContext.ToJson(rows.Select(r => new
            {
                code = r.Code, title = r.Title, hero = Math.Round(r.Hero, 2), hero_sample = r.HeroSample, pool = Math.Round(r.Pool, 2),
                pool_sample = r.PoolSample, diff = Math.Round(r.Diff, 2), score = Math.Round(r.Score, 1), verdict = r.Verdict,
            }));
        });
    }
}
