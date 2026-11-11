using System.Diagnostics;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Data.Import;
using PKTracker.Engine.Hud;
using PKTracker.Live;

namespace PKTracker.Cli;

internal static class Commands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = Options.Parse(args.Skip(1));
        var settings = TrackerSettings.Load();
        var cs = opts.Get("conn") ?? settings.ConnectionString;

        switch (args[0])
        {
            case "db" when opts.Positional.FirstOrDefault() == "init":
            {
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                Console.WriteLine($"Схема: версия {Migrator.LatestVersion}. Статов активно: {host.Stats.Count}.");
                return 0;
            }
            case "db" when opts.Positional.FirstOrDefault() == "list":
            {
                var current = DatabaseCatalog.Current(cs);
                foreach (var d in await DatabaseCatalog.ListAsync(cs))
                    Console.WriteLine($"{(d.Name == current ? "*" : " ")} {d}{(d.IsTracker ? "" : "  (не база PKTracker)")}");
                return 0;
            }
            case "db" when opts.Positional.FirstOrDefault() == "create":
            {
                var name = opts.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("Не указано имя базы");
                await DatabaseCatalog.CreateAsync(cs, name);
                // Схему создаём сразу: пустая база без таблиц выглядела бы в списке как чужая.
                await using var host = await TrackerHost.StartAsync(DatabaseCatalog.WithDatabase(cs, DatabaseCatalog.Normalize(name, out _)),
                    opts.Get("room") ?? settings.RoomCode);
                Console.WriteLine($"База «{DatabaseCatalog.Normalize(name, out _)}» создана, схема v{Migrator.LatestVersion}.");
                return 0;
            }
            case "db" when opts.Positional.FirstOrDefault() == "drop":
            {
                var name = opts.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("Не указано имя базы");
                // Консоль не переспрашивает, поэтому имя нужно повторить ключом: одна опечатка не должна стоить базы.
                if (!opts.Has("yes")) throw new ArgumentException($"Удаление необратимо. Подтвердите ключом: db drop {name} --yes");
                await DatabaseCatalog.DropAsync(cs, name);
                Console.WriteLine($"База «{name}» удалена.");
                return 0;
            }
            case "import":
            {
                var paths = opts.Positional.Count > 0 ? opts.Positional
                    : new List<string> { settings.HandHistoryFolder ?? throw new ArgumentException("Не указано, что импортировать: папка, файлы или архив") };
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                // «--hero ник» — аккаунт активного рума; «--hero РУМ:ник» — аккаунт другого рума (для выгрузок с несколькими румами).
                foreach (var hero in (opts.Get("hero") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int colon = hero.IndexOf(':');
                    if (colon > 0 && PKTracker.Parsers.ParserRegistry.Room(hero[..colon]) != null)
                        await host.Heroes.AddForRoomAsync(hero[(colon + 1)..], hero[..colon]);
                    else await host.Heroes.AddAsync(hero);
                }
                await host.ReloadAsync();
                var progress = new Progress<ImportProgress>(p =>
                    Console.Write($"\r  файлов {p.FilesDone}/{p.FilesTotal}  рук {p.Hands}  импорт {p.Imported}  дубли {p.Duplicates}  ошибки {p.Errors}  {p.HandsPerSecond:F0} рук/с   "));
                // «--observed» — принимать и раздачи без Hero за столом (на свой риск: правила румов это запрещают).
                // «--gg» — принимать раздачи сети GG (на свой риск: её политика запрещает сторонний импорт истории).
                var rep = await host.CreateFileImporter(opts.Has("observed"), opts.Has("gg")).ImportPathsAsync(paths, progress);
                Console.WriteLine();
                Console.WriteLine($"""
                    Файлов: {rep.Files}  Раздач: {rep.Hands}
                    Импортировано: {rep.Imported}  Дубликатов: {rep.Duplicates}  Ошибок: {rep.Errors}  Отклонено файлов: {rep.RejectedFiles}
                    Турниров (.ots): {rep.Tournaments}  Время: {rep.Elapsed.TotalSeconds:F1} c
                    """);
                foreach (var m in rep.Messages.Take(20)) Console.WriteLine("  " + m);
                if (rep.UnknownHeroes.Count > 0)
                    Console.WriteLine("  Свои аккаунты добавьте ключом: --hero " +
                                      string.Join(",", rep.UnknownHeroes.Take(4).Select(u => $"{u.RoomCode}:{u.Nickname}")));
                return rep.Errors == 0 ? 0 : 1;
            }
            case "stats":
            {
                var nick = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("Не указан ник");
                var kind = opts.Positional.Skip(1).FirstOrDefault() == "mtt" ? GameKind.Mtt : GameKind.Cash;
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var p = await host.Hud.FindPlayerAsync(nick) ?? throw new ArgumentException($"Игрок '{nick}' не найден");
                var sw = Stopwatch.StartNew();
                var set = await host.Hud.GetAsync(p.PlayerId, kind);
                var ms = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"{p.Nickname} (id {p.PlayerId}), {kind}, запрос {ms:F1} мс");
                foreach (var st in host.Stats)
                {
                    var (v, n) = set.Evaluate(st.Code);
                    if (v == null) continue;
                    var dec = st.Definition.Format.Decimals;
                    Console.WriteLine($"  {st.Definition.Title,-10} {v.Value.ToString("F" + dec),8}   n={n}");
                }
                return 0;
            }
            case "popup":
            {
                var nick = opts.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("Не указан ник");
                var code = opts.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("Не указан код стата");
                var kind = opts.Positional.ElementAtOrDefault(2) == "mtt" ? GameKind.Mtt : GameKind.Cash;
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var p = await host.Hud.FindPlayerAsync(nick) ?? throw new ArgumentException($"Игрок '{nick}' не найден");
                var stat = host.Stats.Single(s => s.Code == code);
                var sw = Stopwatch.StartNew();
                var d = await new StatPopupService(host.Db).LoadAsync(p.PlayerId, stat, kind);
                Console.WriteLine($"{stat.Definition.Title}: {d.Hits}/{d.Cases} (строк {d.Scanned}, {sw.ElapsedMilliseconds} мс)");
                Console.WriteLine($"  исходы спота по раздачам ({d.HitHands}): профит {d.ActionProfitBb:F2} bb/рука, "
                    + $"выиграл руку {d.WonHandPct:F0}%, WTSD {d.WtsdPct:F0}%, W$SD {d.WsdPct:F0}%");
                if (d.HitHands > d.Hits) Console.WriteLine($"  ВНИМАНИЕ: раздач {d.HitHands} больше срабатываний {d.Hits}");
                foreach (var (choice, range) in d.RangeByAction.OrderByDescending(kv => kv.Value.Sum()))
                    Console.WriteLine($"  диапазон «{choice}»: рук {range.Sum()}, разных комбинаций {range.Count(v => v > 0)}");
                void Print(string title, IEnumerable<string> lines) { Console.WriteLine($"  [{title}]"); foreach (var l in lines) Console.WriteLine("    " + l); }
                Print("позиции", d.ByPosition.Select(r => $"{r.Key,-6} {r.Pct,5:F0}%  {r.Hits}/{r.Cases}"));
                Print("next actions", d.NextActions.Select(r => $"{r.Key,-40} {r.Pct,5:F0}%  ({r.Count})"));
                Print("сайзинги", d.BetSizing.Select(r => $"{r.Key,-10} {r.Pct,5:F0}%  ({r.Count})"));
                Print("сила руки", d.HandStrength.Select(r => $"{r.Key,-14} {r.Pct,5:F0}%  ({r.Count})"));
                Print("борды", d.Boards.Select(r => $"{r.Key,-16} {r.Pct,5:F0}%  ({r.Count})"));
                Print("диапазон (топ)", Enumerable.Range(0, 169).Where(i => d.RangeHits[i] > 0).OrderByDescending(i => d.RangeHits[i]).Take(10)
                    .Select(i => $"{PKTracker.Core.Model.Card.HoleClassName(i)}: {d.RangeHits[i]}"));
                return 0;
            }
            case "merge":
            {
                var canonNick = opts.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("merge <основной ник> <старый ник>");
                var aliasNick = opts.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("merge <основной ник> <старый ник>");
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var canon = await host.Hud.FindPlayerAsync(canonNick) ?? throw new ArgumentException($"Игрок '{canonNick}' не найден");
                var alias = await host.Hud.FindPlayerAsync(aliasNick) ?? throw new ArgumentException($"Игрок '{aliasNick}' не найден");
                var r = await new PlayerIdentityService(host.Db).MergeAsync(canon.PlayerId, alias.PlayerId);
                Console.WriteLine($"Склеено: {r.AliasNickname} (id {r.AliasId}) → id {r.CanonicalId}");
                return 0;
            }
            case "identity":
            {
                var folder = opts.Positional.FirstOrDefault() ?? settings.LiveDataFolder ?? throw new ArgumentException("Не указана папка .data");
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var svc = new PlayerIdentityService(host.Db);
                int files = 0, merges = 0;
                foreach (var f in Directory.EnumerateFiles(folder, "*.data"))
                {
                    var decoder = new PokerKingDataDecoder(settings.LiveSeatOffset);
                    var seats = new Dictionary<Guid, PKTracker.Core.Abstractions.LiveSeat>();
                    foreach (var line in File.ReadLines(f))
                        if (decoder.Feed(line) is { } s)
                            foreach (var seat in s.Seats) if (seat.ExternalId is { } id) seats[id] = seat;
                    var res = await svc.ObserveAsync(seats.Values);
                    merges += res.Count;
                    foreach (var m in res) Console.WriteLine($"  {Path.GetFileName(f)}: {m.AliasNickname} → id {m.CanonicalId}");
                    files++;
                }
                Console.WriteLine($"Файлов .data: {files}, склеек: {merges}");
                return 0;
            }
            case "hands":
            {
                var expr = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("hands \"<выражение>\" [cash|mtt]");
                GameKind? kind = opts.Positional.ElementAtOrDefault(1) switch { "cash" => GameKind.Cash, "mtt" => GameKind.Mtt, _ => null };
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var filter = PKTracker.Engine.Stats.StatCompiler.CompileHandFilter(expr);
                var sw = Stopwatch.StartNew();
                var (rows, scanned) = await new ReportRepository(host.Db).HeroHandsFilteredAsync(filter, kind, 50, 0);
                Console.WriteLine($"Найдено {rows.Count} (показано до 50), просмотрено {scanned}, {sw.ElapsedMilliseconds} мс");
                foreach (var r in rows)
                    Console.WriteLine($"  {r.PlayedAt:dd.MM HH:mm}  #{r.ExternalId}  {r.Table,-24} {r.Position,-4} {r.Cards,-6} {r.NetBbX100 / 100.0,8:F1} bb");
                return 0;
            }
            case "rebuild":
            {
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var rb = new StatRebuilder(host.Db);
                var progress = new Progress<RebuildProgress>(p => Console.Write($"\r  {p.Stage}: {p.Done}/{p.Total}   "));
                var sw = Stopwatch.StartNew();
                if (opts.Has("full")) await rb.FullRebuildAsync(host.Stats, host.HeroNames, progress);
                else if (opts.Get("stat") is { } code) await rb.RebuildStatsAsync(host.Stats.Where(s => s.Code == code).ToList(), progress);
                else Console.WriteLine($"Устаревших статов пересчитано: {await host.CatchUpStaleStatsAsync(progress)}");
                Console.WriteLine($"\nГотово за {sw.Elapsed.TotalSeconds:F1} c");
                return 0;
            }
            case "hud-check":
            {
                // Раскладка ломается тихо: ячейка с недостижимым условием просто показывает прочерк.
                // Поэтому прогоняем все вкладки по настоящему игроку и показываем, что осталось пустым.
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var kind = opts.Positional.Contains("cash") ? GameKind.Cash : GameKind.Mtt;
                string? nick = opts.Get("player") ?? host.HeroNames.FirstOrDefault();
                if (nick == null) { Console.Error.WriteLine("Не указан игрок и не найден Hero"); return 1; }
                var found = await host.Hud.FindPlayerAsync(nick);
                if (found == null) { Console.Error.WriteLine($"Игрок «{nick}» не найден"); return 1; }

                var store = new HudProfileStore();
                var profile = store.Resolve(opts.Get("profile"), kind);
                var rows = await host.Hud.GetRowsAsync(found.PlayerId, kind);
                Console.WriteLine($"Профиль «{profile.Name}», игрок {nick} ({kind}): "
                    + $"раздач {rows.Facts.Count}, решений {rows.Decisions.Count}");

                var evaluator = new PageEvaluator(host.Hud.Stats);
                int totalEmpty = 0, totalStats = 0;
                var unknown = profile.StatCodes().Where(c => !host.Hud.Stats.ContainsKey(c)).ToList();
                foreach (var code in unknown) Console.WriteLine($"  НЕТ ТАКОГО СТАТА: {code}");

                if (opts.Has("print"))
                {
                    // Панель над столом считается по агрегатам — печатаем именно её, чтобы сверить с эталоном.
                    var agg = await host.Hud.GetAggAsync(found.PlayerId, kind);
                    foreach (var pp in profile.AllPanelPages()) LabCommand.Print(pp, agg.EvaluatePage(pp));
                }
                foreach (var page in profile.AllPanelPages().Concat(profile.Pages))
                {
                    var sw = Stopwatch.StartNew();
                    var values = evaluator.Evaluate(page, rows);
                    int stats = 0, empty = 0;
                    for (int i = 0; i < page.Cells.Count; i++)
                    {
                        if (page.Cells[i].Stat == null) continue;
                        stats++;
                        if (values[i].Cases == 0) empty++;
                    }
                    totalStats += stats;
                    totalEmpty += empty;
                    Console.WriteLine($"  {page.Name,-14} ячеек со статом {stats,4}, без выборки {empty,4}  ({sw.ElapsedMilliseconds} мс)");
                    if (opts.Has("verbose"))
                        for (int i = 0; i < page.Cells.Count; i++)
                            if (page.Cells[i].Stat != null && values[i].Cases == 0)
                                Console.WriteLine($"      пусто: {page.Cells[i].Stat} [{page.Cells[i].Filter}]");
                }
                Console.WriteLine($"Итого: {totalStats} ячеек со статом, {totalEmpty} без выборки.");
                return unknown.Count == 0 ? 0 : 1;
            }
            // Прогон всех запросов вкладки «Отчёты» — ошибка в срезе иначе видна только в окне.
            case "report":
            {
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var kind = opts.Positional.FirstOrDefault() == "cash" ? GameKind.Cash : GameKind.Mtt;
                var repo = new SliceReportRepository(host.Db);
                var slice = new ReportSlice { Kind = kind };
                var tournaments = await new ReportRepository(host.Db).TournamentsAsync();
                var formatOf = tournaments.ToDictionary(t => t.TournamentId, t => t.FormatLabel);

                // --drill <формат> повторяет клик по строке разбивки: тот же отчёт, но по одному формату.
                if (opts.Get("drill") is { } fmt)
                {
                    var ids = tournaments.Where(t => t.FormatLabel.Contains(fmt, StringComparison.OrdinalIgnoreCase))
                        .Select(t => t.TournamentId).ToHashSet();
                    if (ids.Count == 0) { Console.Error.WriteLine($"Формат «{fmt}» не найден"); return 1; }
                    slice = slice with { Tournaments = ids, FormatLabel = fmt };
                }

                var sw = Stopwatch.StartNew();
                var sum = await repo.SummaryAsync(slice);
                var act = await repo.ActionsAsync(slice);
                Console.WriteLine($"Срез: {string.Join(" + ", slice.Chips().Select(c => c.Label).DefaultIfEmpty("всё"))} ({sw.ElapsedMilliseconds} мс)");
                Console.WriteLine($"  раздач {sum.Hands}, турниров {sum.Tournaments}, VPIP {sum.VpipPct:F0} PFR {sum.PfrPct:F0} "
                    + $"3Bet {act.ThreeBetPct:F0} ({act.ThreeBetHits}/{act.ThreeBetCases}) AF {act.Af:F1} AGG {act.AggPct:F0}");
                Console.WriteLine($"  WTSD {sum.WtsdPct:F0} W$SD {sum.WsdPct:F0} WWSF {sum.WwsfPct:F0} "
                    + $"bb/100 {sum.Bb100:F1} EV {sum.EvBb100:F1} удача {sum.LuckBb100:F1} σ {sum.StdDevBb100:F0}");

                var dims = new[] { kind == GameKind.Cash ? Breakdown.Limit : Breakdown.Tournament, Breakdown.Stack, Breakdown.Position, Breakdown.Month };
                foreach (var by in dims)
                {
                    sw.Restart();
                    var groups = await repo.BreakdownAsync(slice, by);
                    Console.WriteLine($"  [{by}] групп {groups.Count} ({sw.ElapsedMilliseconds} мс)");
                    // Разбивка обязана сходиться с итогом: расхождение значит, что WHERE и GROUP BY разошлись.
                    var folded = groups.Aggregate(SliceStats.Empty, (a, g) => a + g.Stats);
                    if (folded.Hands != sum.Hands)
                        Console.WriteLine($"    РАСХОЖДЕНИЕ: сумма групп {folded.Hands} != итог {sum.Hands}");
                    foreach (var g in groups.OrderByDescending(g => g.Stats.Hands).Take(8))
                    {
                        var label = by == Breakdown.Tournament
                            ? formatOf.GetValueOrDefault(long.Parse(g.Key), g.Key)
                            : SliceReportRepository.Label(by, g.Key, slice.IsTournament);
                        Console.WriteLine($"    {label,-28} рук {g.Stats.Hands,6}  VPIP {g.Stats.VpipPct,5:F0}  PFR {g.Stats.PfrPct,5:F0}"
                            + $"  3Bet {g.Actions.ThreeBetPct,5:F0}  bb/100 {g.Stats.Bb100,7:F1}");
                    }
                }

                var next = await repo.NextActionAsync(slice);
                Console.WriteLine($"  [NextAction] строк {next.Count}");
                foreach (var r in next.Where(r => r.Street == Street.Preflop))
                    Console.WriteLine($"    {r.Label,-34} решений {r.Decisions,6}  рук {r.Hands,6}  bb/100 {r.Bb100,7:F1}");

                // Клик по строке разбивки обязан дать ровно её числа: это единственная проверка,
                // которая ловит расхождение условия среза с ключом группировки.
                int bad = 0;
                foreach (var g in await repo.BreakdownAsync(slice, Breakdown.Position))
                {
                    var (pos, hu) = SliceReportRepository.PositionKey(g.Key);
                    var drilled = hu
                        ? slice with { Position = pos, MinPlayers = 2, MaxPlayers = 2, PlayersLabel = "хедз-ап" }
                        : slice with { Position = pos, MinPlayers = 3, MaxPlayers = null, PlayersLabel = "3+ игроков" };
                    var got = await repo.SummaryAsync(drilled);
                    if (got.Hands != g.Stats.Hands || got.Vpip != g.Stats.Vpip)
                    {
                        bad++;
                        Console.WriteLine($"    РАСХОЖДЕНИЕ позиция {SliceReportRepository.Label(Breakdown.Position, g.Key, slice.IsTournament)}: "
                            + $"в разбивке {g.Stats.Hands}/{g.Stats.Vpip}, после клика {got.Hands}/{got.Vpip}");
                    }
                }
                foreach (var g in await repo.BreakdownAsync(slice, Breakdown.Stack))
                {
                    var band = ReportSlice.StackBands(slice.IsTournament)[int.Parse(g.Key)];
                    var got = await repo.SummaryAsync(slice with { MinStackBb = band.From, MaxStackBb = band.To, StackLabel = band.Label });
                    if (got.Hands != g.Stats.Hands)
                    {
                        bad++;
                        Console.WriteLine($"    РАСХОЖДЕНИЕ стек {band.Label}: в разбивке {g.Stats.Hands}, после клика {got.Hands}");
                    }
                }
                foreach (var r in next)
                {
                    var got = await repo.SummaryAsync(slice with { Action = r.Slice });
                    // Срез по действию отбирает раздачи, где оно было хотя бы раз, — это и есть «рук» строки.
                    if (got.Hands != r.Hands)
                    {
                        bad++;
                        Console.WriteLine($"    РАСХОЖДЕНИЕ действие «{r.Label}»: в строке рук {r.Hands}, после клика {got.Hands}");
                    }
                }
                Console.WriteLine(bad == 0 ? "  клик по строке == её строка: расхождений нет" : $"  РАСХОЖДЕНИЙ ПРИ КЛИКЕ: {bad}");

                var graph = await repo.GraphAsync(slice);
                var hands = await repo.HandsAsync(slice, 5, 0);
                Console.WriteLine($"  график {graph.Count} точек, список рук {hands.Count} строк");
                if (graph.Count != sum.Hands) Console.WriteLine($"    РАСХОЖДЕНИЕ: график {graph.Count} != итог {sum.Hands}");
                return bad == 0 ? 0 : 1;
            }
            case "check-agg":
            {
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var diffs = await new StatRebuilder(host.Db).VerifyAggregatesAsync(host.Stats);
                Console.WriteLine(diffs.Count == 0 ? "Агрегаты == пересчёт по hand_decisions/hand_players: расхождений нет." : $"Расхождений: {diffs.Count}");
                foreach (var d in diffs.Take(20)) Console.WriteLine("  " + d);
                return diffs.Count == 0 ? 0 : 1;
            }
            case "watch":
            {
                var dir = opts.Positional.FirstOrDefault() ?? settings.HandHistoryFolder ?? throw new ArgumentException("Не указана папка");
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                if (opts.Get("hero") is { } hero) { await host.Heroes.AddAsync(hero); await host.ReloadAsync(); }
                await using var w = host.CreateWatcher(dir);
                w.HandsImported += e => Console.WriteLine($"{DateTime.Now:T} +{e.Result.Imported} рук ({Path.GetFileName(e.File)})");
                w.Error += (f, ex) => Console.WriteLine($"{DateTime.Now:T} ОШИБКА {f}: {ex.Message}");
                w.Start();
                Console.WriteLine($"Дозор {dir}. Ctrl+C — выход.");
                var done = new TaskCompletionSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                await done.Task;
                return 0;
            }
            case "data":
            {
                var file = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("Не указан .data файл");
                var decoder = new PokerKingDataDecoder(settings.LiveSeatOffset);
                PKTracker.Core.Abstractions.LiveTableSeating? last = null;
                int events = 0;
                foreach (var line in File.ReadLines(file))
                {
                    if (decoder.Feed(line) is { } seating) { last = seating; events++; }
                }
                if (last == null) { Console.WriteLine("Рассадка не найдена"); return 1; }
                Console.WriteLine($"Игра {last.GameNumber} стол {last.TableNumber}, {last.MaxSeats}-max, Hero место {last.MySeat} (HH), обновлений: {events}");
                foreach (var s in last.Seats) Console.WriteLine($"  Seat {s.Seat}: {s.Name} {s.Country} {s.ExternalId}");
                return 0;
            }
            case "lab":
                return LabCommand.Run(opts);
            case "hud-import":
            {
                // Пакет HUD: статы в базу, профиль в %APPDATA%\PKTracker\hud, досчёт агрегатов новых статов.
                var file = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("Не указан файл пакета");
                await using var host = await TrackerHost.StartAsync(cs, opts.Get("room") ?? settings.RoomCode);
                var progress = new Progress<RebuildProgress>(p => Console.Write($"\r  {p.Stage}: {p.Done}/{p.Total}      "));
                var res = await new HudPackageService(host).ImportAsync(file, progress);
                Console.WriteLine();
                Console.WriteLine($"Профиль «{res.ProfileName}»: статов в пакете {res.Stats}, новых/изменённых {res.ChangedStats.Count}, пересчитано {res.RebuiltStats}");
                return 0;
            }
            default:
                return Program.Usage();
        }
    }

    internal sealed class Options
    {
        private readonly Dictionary<string, string?> _named = new();
        public List<string> Positional { get; } = new();

        public static Options Parse(IEnumerable<string> args)
        {
            var o = new Options();
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].StartsWith("--", StringComparison.Ordinal))
                {
                    var key = list[i][2..];
                    string? val = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal) && key is "conn" or "hero" or "stat" or "room" or "profile" or "player" or "drill" or "pack" or "page" or "opp" or "hit" or "scope" or "where" or "group" or "sum"
                        ? list[++i] : null;
                    o._named[key] = val;
                }
                else o.Positional.Add(list[i]);
            }
            return o;
        }

        public string? Get(string key) => _named.GetValueOrDefault(key);
        public bool Has(string key) => _named.ContainsKey(key);
    }
}
