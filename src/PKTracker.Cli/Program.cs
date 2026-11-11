using System.Diagnostics;
using System.Text;
using PKTracker.Core.Model;
using PKTracker.Engine;
using PKTracker.Parsers;
using PKTracker.Parsers.Wpn;

namespace PKTracker.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "verify" when args.Length >= 2 => Verify(args[1], args.Length > 2 ? args[2] : null),
                _ => await Commands.RunAsync(args),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    internal static int Usage()
    {
        Console.WriteLine("""
            PKTracker CLI
              verify <dir> [hero]                      парсинг + инварианты денег/позиций на корпусе
              db init  --conn <cs>                     применить миграции
              db list | db create <имя> | db drop <имя> --yes   базы на сервере: список, новая, удаление
              import   --conn <cs> [--hero <ник|РУМ:ник>,…] [--observed] [--gg] <папка|файл|архив>…
                                                       ручной импорт: папки, файлы и архивы .zip/.rar/.7z
              stats    --conn <cs> <nick> [cash|mtt]   статы игрока из агрегатов (как HUD)
              rebuild  --conn <cs> [--full]            ребилд агрегатов (селективный / полный из hand_raw)
              check-agg --conn <cs>                    сверка агрегатов с SQL по hand_decisions (§15.7)
              report   --conn <cs> [cash|mtt]          отчёт со срезами: итог, все разбивки, сверка сумм
                       [--drill <формат>]
              hud-check --conn <cs> [cash|mtt]         прогон всех вкладок профиля HUD по игроку
                        [--player <nick>] [--profile <имя>] [--verbose]
              watch    --conn <cs> --hero <nick> <dir> авто-импорт (дозор файлов)
              data <file.data>                         декодировать рассадку из .data
            """);
        return 2;
    }

    private static int Verify(string dir, string? hero)
    {
        int hands = 0, errors = 0, invFails = 0, cash = 0, tourney = 0;
        int rit = 0, bomb = 0, dead = 0, noSb = 0, hu = 0, known = 0, decisions = 0, evHands = 0;
        var sw = Stopwatch.StartNew();
        var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(ParserRegistry.LooksLikeHistoryFile).ToList();
        var formats = new Dictionary<string, int>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f, Encoding.UTF8);
            // verify не привязан к руму: формат каждого файла определяется по его началу
            var detected = ParserRegistry.Detect(text.AsSpan(0, Math.Min(text.Length, 4096)), "POKERKING");
            if (detected == null) continue;
            formats[detected.Value.Room.Network] = formats.GetValueOrDefault(detected.Value.Room.Network) + 1;
            foreach (var r in detected.Value.Parser.Parse(text, ParserRegistry.SourceFor(f, hero)))
            {
                hands++;
                if (!r.Ok)
                {
                    errors++;
                    if (errors <= 20) Console.WriteLine($"PARSE ERROR {Path.GetFileName(f)} @{r.Offset}: {r.Error}");
                    continue;
                }
                var h = r.Hand!;
                if (h.GameKind == GameKind.Cash) cash++; else tourney++;
                if (h.Has(HandFlags.RunItTwice)) rit++;
                if (h.Has(HandFlags.BombPot)) bomb++;
                if (h.Has(HandFlags.DeadButton)) dead++;
                if (h.Has(HandFlags.NoSmallBlind)) noSb++;
                if (h.Has(HandFlags.HeadsUp)) hu++;
                known += h.Seats.Count(s => s.CardsKnown && !s.IsHero);
                HandContext ctx;
                try { ctx = HandContextBuilder.Build(h, computeEv: true); if (ctx.Players.Any(p => p.EvNetWon != null)) evHands++; }
                catch (Exception ex)
                {
                    invFails++;
                    Console.WriteLine($"CONTEXT ERROR #{h.ExternalHandId}: {ex.Message}");
                    continue;
                }
                decisions += ctx.Decisions.Count;
                var inv = Invariants.Check(h, ctx);
                if (inv.Count > 0)
                {
                    invFails++;
                    if (invFails <= 20) Console.WriteLine($"INVARIANT #{h.ExternalHandId} ({Path.GetFileName(f)}): {string.Join("; ", inv)}");
                }
            }
        }
        sw.Stop();
        Console.WriteLine($"""
            Файлов:          {files.Count}  ({string.Join(", ", formats.Select(kv => $"{kv.Key} {kv.Value}"))})
            Раздач:          {hands}  (кеш {cash} / турниры {tourney})
            Ошибок парсера:  {errors}
            Нарушений инв.:  {invFails}
            Точек решения:   {decisions}
            All-in EV:       {evHands} раздач
            Run It Twice:    {rit}   Bomb Pot: {bomb}   Мёртвая кнопка: {dead}   Без SB: {noSb}   Хедз-ап: {hu}
            Вскрытых карт оппонентов: {known}
            Время:           {sw.ElapsedMilliseconds} мс ({(hands / Math.Max(sw.Elapsed.TotalSeconds, 0.001)):F0} рук/с, вкл. чтение и контекст)
            """);
        return errors == 0 && invFails == 0 ? 0 : 1;
    }
}
