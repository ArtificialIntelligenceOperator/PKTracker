# База PKTracker (PostgreSQL)

Миграции — `src/PKTracker.Data/Migrations/NNN_*.sql`, применяются при старте. Схема — три уровня:
**raw** (`hand_raw`: исходный текст, сжатый zstd) → **normalized** (раздачи, игроки, действия, точки решения) →
**aggregated** (`player_stat_agg`, питает HUD).

## Единицы и кодировки

- **Деньги** — `bigint` в сотых: кеш — центы, турниры — фишки×100. Турнирные фишки и доллары не смешивать.
- `net_won_bb_x100` — выигрыш в сотых долях bb (сравним между лимитами).
- **Время** — `timestamptz`, UTC.
- **Карты** — `smallint` 0..51 = `rank*4 + suit`; rank 0..12 = 2..A, suit 0..3 = c d h s.
- **hole_class** — 0..168, клетка сетки 13×13 (AA=0, KK=14, 22=168).
- **position**: 0 BTN, 1 SB, 2 BB, 3 UTG, 4 UTG1, 5 MP, 6 LJ, 7 HJ, 8 CO, 9 нет.
- **street**: 0 preflop, 1 flop, 2 turn, 3 river. **game_kind**: 0 cash, 1 mtt, 2 sng.
- **hand_actions.action**: 0 post SB, 1 post BB, 2 ante, 3 dead, 4 BB вне очереди, 5 fold, 6 check, 7 call, 8 bet, 9 raise,
  10 возврат uncalled, 11 collect. `amount` — доплата (для рейза вычисленная), `to_amount` — уровень ставки игрока на улице.
- **hand_decisions.chosen**: 0 fold, 1 check, 2 call, 3 bet, 4 raise. **facing**: 0 нет, 1 ставка, 2 рейз.
- **hands.flags** (биты): 1 bomb pot, 2 run it twice, 4 мёртвая кнопка, 8 без SB, 16 хедз-ап.

### hand_players.fact_bits

| Бит | Значение | Бит | Значение |
|---|---|---|---|
| 1 | Dealt | 512 | PostedOutOfTurn |
| 2 | Vpip | 1024 | PostedSb |
| 4 | Pfr | 2048 | PostedBb |
| 8 | SawFlop | 4096 | PostedDead |
| 16 | SawTurn | 8192 | AllIn |
| 32 | SawRiver | 16384 | IsHero |
| 64 | SawShowdown | 32768 | ThreeBet |
| 128 | WonAtShowdown | 65536 | PfAggressor |
| 256 | WonHand | 131072 | CardsKnown |

### hand_decisions.ctx_bits

1 PfAggressor, 2 PrevStreetAggressor, 4 IsIp, 8 FirstActionOnStreet, 16 InBlinds, 32 CheckedThisStreet,
64 FacingCbet, 128 StealSpot, 256 AggressorBehind, 512 VoluntarilyInvested, 1024 FacingAllIn.

### hand_decisions: поля ContextVersion 2 (миграции 003, 004)

Добавлены ради многоячеечных раскладок HUD — матриц «против позиции», сайзингов в bb и линий.
Заполняются только полным ребилдом (`rebuild --full`): восстанавливаются из текста раздачи.

- **vs_position** — позиция последнего агрессора на улице (префлоп — последнего рейзера); 9 = None.
- **opener_position** — позиция открывшего банк рейзом на префлопе; 9 = None.
- **to_amount** — собственная ставка «до» после действия, минимальные единицы.
- **facing_amount** — ставка на улице, которую игрок перекрывает. Не равна `to_call`: у BB часть
  уже стоит блайндом.
- **line_bits** — собственная линия на постфлопе, по 3 бита на улицу (флоп — младшие):
  0 не действовал, 1 чек, 2 ставка, 3 колл, 4 рейз. Так `2 | (1 << 3)` = «поставил флоп, чекнул тёрн».

### hand_decisions: поля ContextVersion 4–6 (миграции 007, 009)

- **pfa_line_bits, opp_line_bits, pot_raises, pf_limpers, pfr_position, flop_players, flop_mask, vs_stack,
  start_eff_stack, callers_before_3b** (007) — обстановка постфлоп-спота: линии соперников, тип банка, кто дошёл до флопа.
- **made_hand** (009) — рука игрока на улице: младший байт — класс относительно борда (`MadeHandClass`),
  биты `0x100` флеш-дро, `0x200` стрит-дро на 8 аутов, `0x400` гатшот, `0x800` топ-пара с кикером от валета;
  0 — карты неизвестны. По ней строятся отчёт «Сила руки» и диаграмма в попапе стата.

Исход раздачи в точке решения (`HandWon`, `WentToShowdown`) отдельной колонкой не хранится: при чтении
к решению подклеивается `hand_players.fact_bits` того же игрока.

Раздачи со старым контекстом видно запросом:

```sql
SELECT count(*) FROM hands WHERE context_version < 2;
```

## Основные таблицы

| Таблица | Что хранит |
|---|---|
| `room`, `hero_account` | румы; ники пользователя по румам |
| `player` | игроки; `canonical_id` — склейка ников, `external_id` — UUID из .data |
| `table_instance` | стол-день кеша или стол турнира (`tournament_id`) |
| `hands` | раздача: время, блайнды, банк, рейк, флаги, `hero_seat` |
| `hand_raw` | исходный текст (zstd) |
| `hand_players` | игрок в раздаче: позиция, стек, карты, вложено, выигрыш, `ev_net_won`, `fact_bits` |
| `hand_actions` | действия по порядку |
| `hand_boards` | доски (при Run It Twice — две) |
| `hand_decisions` | точки решения — основа статов и попапов |
| `player_stat_agg` | агрегаты статов по (игрок, стат, тип игры, число игроков, позиция, стек) |
| `stat_definition`, `agg_watermark` | определения статов и версия их пересчёта |
| `tournament`, `tournament_result` | турниры и итоги из .ots (`entry_no` — ре-энтри) |
| `player_note`, `import_state`, `import_error` | заметки, позиции дозора файлов, ошибки импорта |

## Типовые запросы

Hero:

```sql
WITH heroes AS (
  SELECT coalesce(p.canonical_id, p.player_id) AS id
    FROM player p JOIN hero_account h ON h.room_id = p.room_id AND h.nickname = p.nickname)
SELECT h.played_at, hp.position, hp.net_won_bb_x100 / 100.0 AS net_bb
  FROM hand_players hp JOIN hands h USING (hand_pk)
 WHERE hp.player_id IN (SELECT id FROM heroes) AND h.game_kind = 0
 ORDER BY h.played_at DESC LIMIT 20;
```

Winrate по позициям:

```sql
... SELECT hp.position, count(*), sum(hp.net_won_bb_x100) / 100.0 / count(*) * 100 AS bb100
      FROM hand_players hp JOIN hands h USING (hand_pk)
     WHERE hp.player_id IN (SELECT id FROM heroes) AND (hp.fact_bits & 1) <> 0 GROUP BY 1;
```

Частота действия в спотах напрямую по точкам решения (например, сибет флопа Hero):

```sql
... SELECT count(*) FILTER (WHERE d.chosen = 3) * 100.0 / count(*)
      FROM hand_decisions d
     WHERE d.player_id IN (SELECT id FROM heroes) AND d.street = 1 AND d.facing = 0
       AND (d.ctx_bits & 1) <> 0 AND (d.ctx_bits & 8) <> 0;
```
