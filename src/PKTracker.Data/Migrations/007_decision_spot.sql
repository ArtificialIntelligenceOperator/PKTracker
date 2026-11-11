-- Обстановка постфлоп-спота в точке решения (ContextVersion 4).
--
-- Постфлоп-статы принято описывать спотами («рейзер против BB», «лимп-пот блайндов», «3-бет-пот») и линиями
-- соперника («ставка-чек», «чек на флопе - ставка на тёрне»). Точка решения видит только свою
-- улицу, поэтому снимок конца префлопа и линии соперников приходится нести с собой:
--   pfa_line_bits / opp_line_bits - линия префлоп-агрессора и единственного оппонента (хедз-ап),
--                                   кодировка как у line_bits;
--   pot_raises / pf_limpers       - рейзов и лимперов префлоп (тип банка);
--   pfr_position                  - позиция префлоп-агрессора;
--   flop_players / flop_mask      - сколько и какие позиции увидели флоп;
--   vs_stack                      - стартовый стек последнего агрессора («3-бет против 12-16bb»);
--   start_eff_stack               - эффективный стек по стартовым стекам;
--   callers_before_3b             - коллеров опена до 3-бета (3-бет был сквизом).
-- Значения появляются только после полного ребилда (rebuild --full): они восстанавливаются из текста раздачи.

ALTER TABLE hand_decisions
    ADD COLUMN IF NOT EXISTS pfa_line_bits   smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS opp_line_bits   smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS pot_raises      smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS pf_limpers      smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS pfr_position    smallint NOT NULL DEFAULT 9,
    ADD COLUMN IF NOT EXISTS flop_players    smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS flop_mask       smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS vs_stack        bigint   NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS start_eff_stack bigint   NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS callers_before_3b smallint NOT NULL DEFAULT 0;
