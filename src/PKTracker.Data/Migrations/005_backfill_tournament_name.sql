-- Названия турниров, заведённых только итогами .ots.
--
-- .ots не содержит ни названия турнира, ни его формата - только номер, бай-ин и места. Пока
-- раздач не было, турнир оставался безымянным, а когда они приходили, upsert в HandImporter
-- обновлял лишь started_at. В лобби это выглядело как десятки строк без названия.
--
-- Сам upsert исправлен, но на уже импортированные раздачи он не сработает: они дубликаты,
-- и ветка создания турнира для них не выполняется. Поэтому разовый ремонт здесь.
--
-- Имя берём из названия стола ("$5 Spinz Holdem #1"), отрезая номер стола, - так же,
-- как это делает импортёр.

UPDATE tournament t
   SET name = src.name
  FROM (
        SELECT DISTINCT ON (ti.tournament_id)
               ti.tournament_id,
               regexp_replace(ti.table_name, ' #[0-9]+$', '') AS name
          FROM table_instance ti
         WHERE ti.tournament_id IS NOT NULL
           AND ti.table_name IS NOT NULL
           AND ti.table_name <> ''
         ORDER BY ti.tournament_id, ti.table_instance_id
       ) src
 WHERE t.tournament_id = src.tournament_id
   AND coalesce(t.name, '') = '';

-- Формат (кеш/MTT/SNG) известен из раздач; у турниров из .ots он пустой.
UPDATE tournament t
   SET format = src.game_kind
  FROM (
        SELECT DISTINCT ON (ti.tournament_id) ti.tournament_id, h.game_kind
          FROM table_instance ti
          JOIN hands h ON h.table_instance_id = ti.table_instance_id
         WHERE ti.tournament_id IS NOT NULL
         ORDER BY ti.tournament_id, h.hand_pk
       ) src
 WHERE t.tournament_id = src.tournament_id
   AND t.format IS NULL;
