-- All-in EV (Фаза 12) и остальные скины сети WPN (Фаза 13).
-- ev_net_won NULL — олл-ина с известными картами не было, EV совпадает с фактом (net_won).
ALTER TABLE hand_players
    ADD COLUMN ev_net_won      bigint,
    ADD COLUMN ev_net_bb_x100  integer;

INSERT INTO room (room_id, code, network) VALUES
    (2, 'ACR', 'WPN'),
    (3, 'BLACKCHIP', 'WPN'),
    (4, 'TRUEPOKER', 'WPN')
ON CONFLICT DO NOTHING;
