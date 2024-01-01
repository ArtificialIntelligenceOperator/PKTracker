"""
Дымовой тест MCP-сервера PKTracker по stdio (JSON-RPC 2.0), без внешних зависимостей.

    python tools/mcp_smoke_test.py [путь к pktracker-mcp.exe] [--write]

Проверяет: initialize, список инструментов/ресурсов/промптов, основные инструменты чтения.
С --write дополнительно проверяет test_stat (без сохранения).
"""
import json
import subprocess
import sys

exe = next((a for a in sys.argv[1:] if not a.startswith("--")), r"src\PKTracker.Mcp\bin\Debug\net10.0\pktracker-mcp.exe")
proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
next_id = 0


def call(method, params=None, notify=False):
    global next_id
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None:
        msg["params"] = params
    if not notify:
        next_id += 1
        msg["id"] = next_id
    proc.stdin.write(json.dumps(msg) + "\n")
    proc.stdin.flush()
    if notify:
        return None
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("сервер закрыл stdout: " + proc.stderr.read())
        resp = json.loads(line)
        if resp.get("id") == next_id:
            if "error" in resp:
                raise RuntimeError(f"{method}: {resp['error']}")
            return resp["result"]


def tool(name, **args):
    res = call("tools/call", {"name": name, "arguments": args})
    text = res["content"][0]["text"]
    if res.get("isError"):
        raise RuntimeError(f"{name}: {text}")
    return json.loads(text) if text[:1] in "[{" else text


init = call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
print("server:", init["serverInfo"], "| instructions:", len(init.get("instructions", "")), "символов")
call("notifications/initialized", notify=True)

tools = call("tools/list")["tools"]
print("tools:", ", ".join(t["name"] for t in tools))
print("resources:", ", ".join(r.get("uri", r.get("uriTemplate", "?")) for r in call("resources/list").get("resources", [])))
print("prompts:", ", ".join(p["name"] for p in call("prompts/list")["prompts"]))

status = tool("tracker_status")
print("status:", status)
overview = tool("get_overview", game="cash")
print("overview: hands", overview["hands"], "bb100", overview["bb100"], "ev_bb100", overview["allin_ev_bb100"])
hands = tool("search_hands", filter="Pfr && SawShowdown && !WonAtShowdown", game="cash", limit=3)
print("search_hands:", hands["found"], "из", hands["scanned"])
if hands["hands"]:
    h = tool("get_hand", hand_id=hands["hands"][0]["hand_id"])
    print("get_hand:", h["hand_id"], [p["name"] for p in h["players"]], "raw", len(h["raw_text"] or ""), "символов")
hero = status["heroes"][0]
stats = tool("get_player_stats", player=hero, game="cash")
print("stats:", {s["code"]: s["value"] for s in stats["stats"] if s["code"] in ("vpip", "pfr", "three_bet", "cbet_flop")})
opp = tool("find_players", prefix="", limit=3)
print("find_players:", opp)
villain = next(p["player"] for p in opp if not p["hero"])
print("villain stats (по псевдониму):", len(tool("get_player_stats", player=villain)["stats"]), "статов")
details = tool("get_stat_details", player=hero, stat_code="cbet_flop", game="cash")
print("details: cbet", details["hits"], "/", details["cases"], "next:", details["next_actions"][:2])
leaks = tool("find_leaks", game="cash")
print("leaks:", [(l["title"], l["verdict"]) for l in leaks[:3]])
sql = tool("run_sql", query="SELECT p.nickname, count(*) AS hands FROM hand_players hp JOIN player p USING (player_id) GROUP BY 1 ORDER BY 2 DESC LIMIT 3")
print("run_sql:", sql["rows"])
try:
    tool("run_sql", query="DELETE FROM hands")
    print("ОШИБКА: запись через run_sql прошла")
except RuntimeError as e:
    print("run_sql запись отклонена: OK")
try:
    # Проходит текстовую проверку (начинается с WITH), но PostgreSQL обязан отклонить её в транзакции READ ONLY.
    # LIMIT 0: даже при сбое защиты ничего не будет записано.
    tool("run_sql", query="WITH x AS (INSERT INTO player_note (player_id, body) SELECT player_id, 'smoke' FROM player LIMIT 0 RETURNING 1) SELECT * FROM x")
    print("ОШИБКА: запись внутри WITH прошла")
except RuntimeError as e:
    print("run_sql запись внутри WITH отклонена базой: OK" if "read-only" in str(e) or "только чтение" in str(e) else f"отклонено: {e}")
print("hud profile:", tool("get_hud_profile")["cash"])
print("prompt:", call("prompts/get", {"name": "find_my_leaks", "arguments": {"game": "cash"}})["messages"][0]["content"]["text"][:60], "…")
print("resource stats doc:", len(call("resources/read", {"uri": "pktracker://docs/stats"})["contents"][0]["text"]), "символов")

if "--write" in sys.argv:
    ok = tool("test_stat", definition_json=json.dumps({
        "code": "smoke_xr_turn", "title": "XR T", "scope": "decision",
        "opportunity": "Street==\"Turn\" && Facing==\"Bet\" && CheckedThisStreet", "hit": "Chosen==\"Raise\""}))
    print("test_stat:", ok)
    bad = tool("test_stat", definition_json=json.dumps({"code": "bad", "title": "b", "scope": "decision", "opportunity": "Street ==", "hit": "true"}))
    print("test_stat (ошибка):", bad)

proc.stdin.close()
proc.wait(timeout=10)
print("OK")
