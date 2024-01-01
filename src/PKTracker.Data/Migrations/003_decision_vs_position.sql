-- Против кого принято решение и каким размером (ContextVersion 2).
--
-- Матрицы popup-ов «BB vs BTN», «EP vs MP», «Fold vs DONK» фильтруют по позиции оппонента, а её
-- в hand_decisions не было: позиция хранилась только своя. Восстановить её задним числом из
-- hand_actions нельзя без повторного разбора раздачи, поэтому — колонка и полный ребилд.
--
-- to_amount: размер собственной ставки «до» в минимальных единицах. Префлоп-сайзинги («опен ≤ 4bb»)
-- задаются в bb, а size_pct считает проценты банка и для них бесполезен.

ALTER TABLE hand_decisions
    ADD COLUMN IF NOT EXISTS vs_position     smallint NOT NULL DEFAULT 9,   -- Position.None
    ADD COLUMN IF NOT EXISTS opener_position smallint NOT NULL DEFAULT 9,
    ADD COLUMN IF NOT EXISTS to_amount       bigint   NOT NULL DEFAULT 0;

-- Раздачи со старым контекстом пересчитываются полным ребилдом (rebuild --full): значения берутся
-- из исходного текста раздачи, а не из уже разобранных строк.
