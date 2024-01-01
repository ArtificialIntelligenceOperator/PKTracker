# Статы PKTracker: как они устроены и как написать свой

Любой частотный стат — это дробь **hits / cases**:

- **cases** (opportunity) — сколько раз у игрока была *возможность* совершить действие;
- **hits** — сколько раз он его *совершил*.

Главная ошибка при создании статов — неверный знаменатель. «CBet флопа» считается не от всех флопов, а от флопов,
где игрок был префлоп-агрессором и до него никто не ставил.

Определения хранятся в таблице `stat_definition` (JSON), компилируются в делегаты (System.Linq.Dynamic.Core)
и считаются за один проход по раздаче. После сохранения пересчитывается только изменённый стат.

## Три вида статов

| scope | Считается по | Когда использовать |
|---|---|---|
| `decision` | каждой **точке решения** (`DecisionPoint`): игрок фолдит / чекает / коллирует / ставит / рейзит | 3-бет, сибет, фолд на сибет, чек-рейз, стил, сайзинги |
| `hand` | одной записи на **(раздача, игрок)** (`HandPlayerFacts`) | VPIP, PFR, WTSD, W$SD, выигрыш, EV |
| `expression` | формуле над агрегатами других статов | AF, AGG%, bb/100 |

## Формат определения

```json
{
  "code": "xr_turn",
  "title": "XR T",
  "scope": "decision",
  "opportunity": "Street == \"Turn\" && Facing == \"Bet\" && CheckedThisStreet",
  "hit": "Chosen == \"Raise\"",
  "format": { "decimals": 0, "suffix": "", "percent": true },
  "colors": [ { "from": 0, "to": 10, "color": "#4FC3F7" }, { "from": 10, "to": 101, "color": "#EF5350" } ],
  "minSample": 30
}
```

- `code` — уникальный, snake_case. Встроенные коды занимать нельзя.
- `title` — подпись в HUD, коротко (до ~8 символов).
- `opportunity` / `hit` — логические выражения (синтаксис ниже).
- `value` — только для `hand`: числовое выражение, суммируется в `sum_value` (например `NetWonBbX100`).
- `colors` — пороги: `from` включительно, `to` исключительно.
- `minSample` — при меньшей выборке стат в HUD приглушается.

## Синтаксис выражений

C#-подобный: `&&`, `||`, `!`, `==`, `!=`, `<`, `<=`, `>`, `>=`, `+ - * /`, скобки.
Перечисления сравниваются со строками: `Street == "Flop"`, `Position == "BTN"`, `Chosen == "Raise"`.
Nullable-поля сравниваются с `null`: `EvNetWon != null`, `HandStrength >= 2`.

## Поля DecisionPoint (scope = decision)

| Поле | Тип | Смысл |
|---|---|---|
| `Street` | `Preflop` `Flop` `Turn` `River` | улица |
| `Position` | `BTN` `SB` `BB` `UTG` `UTG1` `MP` `LJ` `HJ` `CO` `None` | позиция (по порядку действий, а не от кнопки; в хедз-апе BTN = SB) |
| `Facing` | `None` `Bet` `Raise` | что перед игроком. Префлоп: `None`, пока никто не рейзил |
| `BetLevel` | int | префлоп: 1 — никто не рейзил, 2 — был опен, 3 — был 3-бет… Постфлоп: число ставок и рейзов на улице |
| `MyLastLevel` | int | уровень последнего рейза игрока на этой улице (0 — не рейзил). F3B = `BetLevel==3 && MyLastLevel==2` |
| `NumLimpers` | int | префлоп: коллов до первого рейза |
| `NumCallers` | int | коллов текущей ставки/рейза до игрока (squeeze: `BetLevel==2 && NumCallers>=1`) |
| `ToCall` | long | сколько доплатить (минимальные единицы) |
| `PotBefore` | long | банк до решения |
| `EffStack` / `EffStackBb` | long / double | эффективный стек |
| `PotOdds` | double | `ToCall / (PotBefore + ToCall)` |
| `PlayersIn` | int | игроков в раздаче |
| `PlayersBehind` | int | ещё не действовали на улице |
| `Chosen` | `Fold` `Check` `Call` `Bet` `Raise` | выбранное действие |
| `ChosenAmount` | long | доплата (для рейза — вычисленная) |
| `SizePct` | short? | ставка/рейз в % банка; `null` для fold/check/call |
| `IsAllIn` | bool | действие олл-ин |
| `HandStrength` | short? | категория руки на улице: 0 старшая карта, 1 пара, 2 две пары, 3 сет, 4 стрит, 5 флеш, 6 фулл-хаус, 7 каре, 8 стрит-флеш; `null` — карты неизвестны |
| `BoardTexture` | short | битовая маска: 1 спаренный, 2 трипс, 4 монотонный, 8 две масти, 16 радуга, 32 стритовый, 64 старший (Q+), 128 3+ одной масти |
| `StackBucket` | byte | 0 <10bb, 1 10–20, 2 20–40, 3 40–100, 4 100+ |
| `PfAggressor` | bool | игрок сделал последний префлоп-рейз |
| `PrevStreetAggressor` | bool | агрессор прошлой улицы (на флопе = префлоп-агрессор) |
| `IsIp` | bool | в позиции (постфлоп) |
| `FirstActionOnStreet` | bool | первое действие игрока на улице |
| `InBlinds` | bool | SB или BB |
| `CheckedThisStreet` | bool | игрок уже чекнул на этой улице |
| `FacingCbet` | bool | ставка перед игроком — продолженная ставка агрессора прошлой улицы |
| `StealSpot` | bool | префлоп: перед игроком опен с CO/BTN/SB без лимперов и коллов |
| `AggressorBehind` | bool | агрессор прошлой улицы в раздаче и действует после игрока |
| `VoluntarilyInvested` | bool | игрок уже добровольно вкладывал префлоп |
| `FacingAllIn` | bool | последняя агрессия — олл-ин |
| `VsPosition` | Position | позиция последнего агрессора на улице (префлоп — последнего рейзера); `None`, если ставки не было |
| `OpenerPosition` | Position | позиция открывшего банк рейзом на префлопе; `None`, если опена не было |
| `ToAmount` / `ToAmountBb` | long / double | собственная ставка «до» после действия: сайзинги опенов задаются в bb, а не в % банка |
| `FacingAmount` / `FacingBb` | long / double | ставка, которую нужно перекрыть (у BB часть уже стоит блайндом, поэтому это не `ToCall`) |
| `RaiseRatio` | double | во сколько раз собственный рейз перекрывает ставку оппонента: 2 — минрейз |
| `ToCallBb`, `PotBb` | double | доплата и банк в bb |
| `MyLine` | string | собственная линия на прошлых улицах постфлопа: `""`, `"B"`, `"BX"`, `"XC"`… (X чек, B ставка, C колл, R рейз) |
| `Hand.*` | | сведения о раздаче, см. ниже |

## Поля HandPlayerFacts (scope = hand, фильтр поиска рук)

| Поле | Смысл |
|---|---|
| `Dealt` | игроку сданы карты |
| `Vpip`, `Pfr`, `ThreeBet` | добровольно вложил / рейзил / 3-бетил префлоп |
| `SawFlop`, `SawTurn`, `SawRiver`, `SawShowdown` | дошёл до улицы / шоудауна |
| `WonHand`, `WonAtShowdown` | выиграл банк (часть банка) / выиграл на шоудауне |
| `AllIn`, `IsHero`, `PostedOutOfTurn`, `CardsKnown` | флаги |
| `Position`, `Seat` | позиция и место |
| `StartStack`, `StackBbX10`, `StackBb`, `StackBucket` | стек в начале раздачи |
| `HoleCard1`, `HoleCard2`, `HoleClass` | карты (если известны); `HoleClass` 0..168 — клетка сетки 13×13: пары на диагонали `r*13+r` (AA=0, KK=14, …, 22=168), одномастные выше диагонали, разномастные ниже |
| `Invested`, `Collected`, `NetWon`, `NetWonBbX100` | вложено, получено, итог (в сотых bb для `NetWonBbX100`) |
| `EvNetWon`, `EvOrNetWon`, `EvNetBbX100` | All-in EV: `null`, если олл-ина с известными картами не было |
| `RakeShare` | доля рейка |

## Hand.* (в обоих видах)

`Hand.GameKind` (`Cash` / `Mtt` / `Sng`), `Hand.IsTournament`, `Hand.BombPot`, `Hand.RunItTwice`, `Hand.DeadButton`,
`Hand.HeadsUp`, `Hand.PlayersDealt`, `Hand.MaxSeats`, `Hand.BigBlind`, `Hand.SmallBlind`, `Hand.Ante`, `Hand.HasAnte`.

## Expression-статы

Формула над агрегатами **plain**-статов: `code.cases`, `code.hits`, `code.sum`; функции `max(a, b)`, `min(a, b)`.
Ссылаться на другие expression нельзя.

```json
{ "code": "af", "title": "AF", "scope": "expression",
  "hit": "(pf_bets.hits + pf_raises.hits) / max(pf_calls.hits, 1)",
  "format": { "decimals": 1, "percent": false } }
```

## Примеры

| Стат | opportunity | hit |
|---|---|---|
| Фолд на 3-бет в позиции | `Street=="Preflop" && BetLevel==3 && MyLastLevel==2 && (Position=="BTN" \|\| Position=="CO")` | `Chosen=="Fold"` |
| Проба тёрна | `Street=="Turn" && Facing=="None" && !PrevStreetAggressor && FirstActionOnStreet && !IsIp` | `Chosen=="Bet"` |
| Овербет ривера | `Street=="River" && Facing=="None"` | `Chosen=="Bet" && SizePct >= 100` |
| Пуш с ≤12bb | `Street=="Preflop" && BetLevel==1 && NumLimpers==0 && EffStackBb <= 12` | `Chosen=="Raise" && IsAllIn` |
| 3-бет против опена с BTN | `Street=="Preflop" && BetLevel==2 && Facing=="Raise" && VsPosition=="BTN"` | `Chosen=="Raise"` |
| Минрейз-3-бет | `Street=="Preflop" && BetLevel==2 && Facing=="Raise"` | `Chosen=="Raise" && RaiseRatio <= 2.3` |
| Ставка ривера после «ставка-чек» | `Street=="River" && MyLine=="BX" && Facing=="None"` | `Chosen=="Bet"` |
| Отложенный сибет тёрна | `Street=="Turn" && PfAggressor && Facing=="None" && FirstActionOnStreet && MyLine=="X"` | `Chosen=="Bet"` |
| Колл 3-бета OOP | `Street=="Preflop" && BetLevel==3 && MyLastLevel==2 && InBlinds` | `Chosen=="Call"` |
| Выигрыш в EV (hand, sum) | `Dealt` | `EvNetWon != null` · `value`: `EvNetBbX100` |

## Порядок работы

1. Посмотреть похожие встроенные статы (`list_stat_definitions` в MCP или вкладка «Статы»).
2. Проверить: `test_stat` (MCP) или «Проверить и прогнать по 1000 рукам». Если `cases = 0` — в условии ошибка.
3. Сохранить: `save_stat` или «Сохранить и пересчитать». Пересчитывается только этот стат.
4. Добавить в HUD: вкладка HUD → «Редактор HUD и попапов» (см. `docs/HUD_LAYOUTS.md`).

## Стат или фильтр ячейки?

Разрезы по позиции, полосе стека, числу игроков и олл-ину задаются **фильтром ячейки** раскладки,
а не новым статом: «3bet из CO против BTN при 15-25bb» — это стат `three_bet` плюс фильтр.
Новый стат нужен только тогда, когда меняется само условие спота (другой знаменатель), а не
обстановка вокруг него. Подробности — `docs/HUD_LAYOUTS.md`.
