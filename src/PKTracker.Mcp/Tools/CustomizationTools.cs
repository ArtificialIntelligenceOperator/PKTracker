using System.ComponentModel;
using ModelContextProtocol.Server;
using Npgsql;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Stats;

namespace PKTracker.Mcp.Tools;

/// <summary>Статы, профиль HUD, Hero-аккаунты и импорт: то, что агент может менять.</summary>
[McpServerToolType]
public sealed class CustomizationTools(TrackerContext ctx)
{
    [McpServerTool(Name = "list_stat_definitions", ReadOnly = true, Title = "Определения статов")]
    [Description("Все активные статы: код, заголовок, scope (decision / hand / expression), условия opportunity/hit, формат, цвета, версия.")]
    public async Task<string> ListStatDefinitions(CancellationToken ct)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.HostAsync(ct);
            return TrackerContext.ToJson(host.Stats.Select(s => new
            {
                code = s.Code,
                title = s.Definition.Title,
                scope = s.Scope.ToString().ToLowerInvariant(),
                opportunity = s.Definition.Opportunity,
                hit = s.Definition.Hit,
                value = s.Definition.Value,
                min_sample = s.Definition.MinSample,
                colors = s.Definition.Colors,
                builtin = s.Definition.IsBuiltin,
                version = s.Definition.Version,
            }));
        });
    }

    [McpServerTool(Name = "test_stat", ReadOnly = true, Title = "Проверить стат")]
    [Description("Компилирует определение стата и прогоняет его по последним раздачам базы, возвращая cases/hits. cases = 0 почти всегда " +
                 "означает ошибку в условии. definition_json: {\"code\":\"xr_turn\",\"title\":\"XR T\",\"scope\":\"decision\"," +
                 "\"opportunity\":\"Street==\\\"Turn\\\" && Facing==\\\"Bet\\\" && CheckedThisStreet\",\"hit\":\"Chosen==\\\"Raise\\\"\"}. " +
                 "Язык и поля — ресурс pktracker://docs/stats.")]
    public async Task<string> TestStat(
        [Description("JSON определения стата")] string definition_json,
        [Description("По скольким последним раздачам прогонять")] int last_hands = 2000,
        CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            var host = await ctx.HostAsync(ct);
            var def = StatDefinition.FromJson(definition_json);
            var known = host.Stats.Select(s => s.Definition).Where(d => d.Code != def.Code).Append(def).ToDictionary(d => d.Code);
            CompiledStat compiled;
            try { compiled = StatCompiler.Compile(def, known); }
            catch (StatCompileException ex) { return TrackerContext.ToJson(new { ok = false, error = ex.InnerException?.Message ?? ex.Message }); }
            if (compiled.Scope == StatScope.Expression)
                return TrackerContext.ToJson(new { ok = true, note = "expression компилируется; значение считается из агрегатов после сохранения", depends_on = compiled.Dependencies });
            var (cases, hits) = await new StatRebuilder(host.Db).TrialRunAsync(compiled, Math.Clamp(last_hands, 100, 100_000), ct);
            return TrackerContext.ToJson(new
            {
                ok = true,
                cases,
                hits,
                pct = cases > 0 ? Math.Round(hits * 100.0 / cases, 1) : (double?)null,
                warning = cases == 0 ? "cases = 0: условие opportunity ни разу не выполнилось — скорее всего в нём ошибка" : null,
            });
        });
    }

    [McpServerTool(Name = "save_stat", Destructive = false, Idempotent = true, Title = "Сохранить стат")]
    [Description("Создаёт или обновляет пользовательский стат (по code) и пересчитывает только его агрегаты по уже импортированным раздачам. " +
                 "Встроенные статы переопределять нельзя — используй новый code. Перед сохранением вызови test_stat.")]
    public async Task<string> SaveStat([Description("JSON определения стата")] string definition_json, CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            ctx.RequireWrites();
            var host = await ctx.HostAsync(ct);
            var def = StatDefinition.FromJson(definition_json);
            if (host.Stats.FirstOrDefault(s => s.Code == def.Code) is { Definition.IsBuiltin: true })
                throw new InvalidOperationException($"'{def.Code}' — встроенный стат; сохрани под другим code");
            var saved = await host.StatsRepo.SaveAsync(def, ct);
            await host.ReloadAsync(ct);
            int rebuilt = await host.CatchUpStaleStatsAsync(ct: ct);
            TrackerContext.SignalAppReload();
            return TrackerContext.ToJson(new { ok = true, stat_id = saved.StatId, version = saved.Version, rebuilt_stats = rebuilt });
        });
    }

    [McpServerTool(Name = "disable_stat", Destructive = true, Title = "Отключить стат")]
    [Description("Отключает пользовательский стат (агрегаты удаляются, определение остаётся в базе неактивным). Встроенные статы отключать нельзя.")]
    public async Task<string> DisableStat([Description("Код стата")] string code, CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            ctx.RequireWrites();
            var host = await ctx.HostAsync(ct);
            var st = host.Stats.FirstOrDefault(s => s.Code == code) ?? throw new ArgumentException($"Стат '{code}' не найден");
            if (st.Definition.IsBuiltin) throw new InvalidOperationException("Встроенные статы отключать нельзя");
            await using (var conn = await host.Db.Import.OpenConnectionAsync(ct))
            {
                await using var cmd = new NpgsqlCommand("UPDATE stat_definition SET is_active = false WHERE stat_id = $1", conn);
                cmd.Parameters.AddWithValue(st.StatId);
                await cmd.ExecuteNonQueryAsync(ct);
                await using var del = new NpgsqlCommand("DELETE FROM player_stat_agg WHERE stat_id = $1", conn);
                del.Parameters.AddWithValue(st.StatId);
                await del.ExecuteNonQueryAsync(ct);
            }
            await host.ReloadAsync(ct);
            TrackerContext.SignalAppReload();
            return TrackerContext.ToJson(new { ok = true, disabled = code });
        });
    }

    [McpServerTool(Name = "get_hud_profile", ReadOnly = true, Title = "Профиль HUD")]
    [Description("Статы панели HUD по порядку для кеша и турниров; '|' — перенос строки. Также настройки попапа по наведению.")]
    public async Task<string> GetHudProfile(CancellationToken ct)
    {
        return await TrackerContext.Guard(async () =>
        {
            await ctx.HostAsync(ct);
            var s = TrackerSettings.Load();
            return TrackerContext.ToJson(new
            {
                cash = s.CashStats.Split(','),
                mtt = s.MttStats.Split(','),
                popup_trigger = s.PopupTrigger.ToString().ToLowerInvariant(),
                hover_delay_ms = s.HoverDelayMs,
                hover_hide_ms = s.HoverHideMs,
                panel_opacity = s.PanelOpacity,
                hotkey = s.HudHotkey,
            });
        });
    }

    [McpServerTool(Name = "set_hud_profile", Destructive = false, Idempotent = true, Title = "Изменить профиль HUD")]
    [Description("Задаёт статы панели HUD для cash или mtt: список кодов по порядку, '|' — перенос строки. Запущенное приложение подхватит изменения само.")]
    public async Task<string> SetHudProfile(
        [Description("cash или mtt")] string game,
        [Description("Коды статов по порядку, например [\"vpip\",\"pfr\",\"agg_pct\",\"|\",\"three_bet\",\"fold_to_3bet\"]")] string[] stat_codes,
        CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            ctx.RequireWrites();
            var host = await ctx.HostAsync(ct);
            var unknown = stat_codes.Where(c => c != "|" && host.Stats.All(s => s.Code != c)).ToList();
            if (unknown.Count > 0) throw new ArgumentException("Неизвестные коды статов: " + string.Join(", ", unknown));
            var s = TrackerSettings.Load();
            var joined = string.Join(",", stat_codes);
            if (TrackerContext.ParseGame(game) == GameKind.Cash) s.CashStats = joined; else s.MttStats = joined;
            s.Save();
            TrackerContext.SignalAppReload();
            return TrackerContext.ToJson(new { ok = true, game, stats = stat_codes });
        });
    }

    [McpServerTool(Name = "add_hero_account", Destructive = false, Idempotent = true, Title = "Добавить свой ник")]
    [Description("Добавляет ник Hero для текущего рума. Импортируются только раздачи, где Hero сидит за столом (правила румов запрещают чужие руки).")]
    public async Task<string> AddHeroAccount([Description("Свой ник в руме")] string nickname, CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            ctx.RequireWrites();
            var host = await ctx.HostAsync(ct);
            await host.Heroes.AddAsync(nickname, ct: ct);
            await host.ReloadAsync(ct);
            return TrackerContext.ToJson(new { ok = true, heroes = host.HeroNames });
        });
    }

    [McpServerTool(Name = "import_hand_histories", Destructive = false, Idempotent = true, Title = "Импорт папки")]
    [Description("Импортирует HH*.txt и итоги турниров *.ots из локальной папки (рекурсивно). Повторный импорт не создаёт дублей. " +
                 "Файлы без раздач Hero отклоняются целиком.")]
    public async Task<string> ImportHandHistories([Description("Полный путь к папке")] string folder, CancellationToken ct = default)
    {
        return await TrackerContext.Guard(async () =>
        {
            ctx.RequireWrites();
            var host = await ctx.HostAsync(ct);
            if (!Directory.Exists(folder)) throw new ArgumentException($"Папка не найдена: {folder}");
            if (host.HeroNames.Count == 0) throw new InvalidOperationException("Сначала add_hero_account: без своего ника импорт запрещён");
            var rep = await host.CreateFileImporter().ImportFolderAsync(folder, ct: ct);
            host.Hud.InvalidateAll();
            TrackerContext.SignalAppReload();
            return TrackerContext.ToJson(new
            {
                files = rep.Files, hands = rep.Hands, imported = rep.Imported, duplicates = rep.Duplicates, errors = rep.Errors,
                rejected_files = rep.RejectedFiles, tournaments = rep.Tournaments, seconds = Math.Round(rep.Elapsed.TotalSeconds, 1),
                messages = rep.Messages.Take(20),
            });
        });
    }
}
