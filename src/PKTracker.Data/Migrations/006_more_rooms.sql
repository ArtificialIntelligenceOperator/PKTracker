-- Румы других сетей: PokerStars, Winamax, GG (PokerOK), iPoker, 888.
-- У каждого свой формат истории; парсер выбирает ParserRegistry по содержимому файла,
-- а раздачи, игроки и Hero привязываются к активному руму так же, как у скинов WPN.
INSERT INTO room (room_id, code, network) VALUES
    (10, 'POKERSTARS', 'POKERSTARS'),
    (11, 'WINAMAX', 'WINAMAX'),
    (12, 'GGPOKER', 'GG'),
    (13, 'IPOKER', 'IPOKER'),
    (14, '888POKER', '888')
ON CONFLICT DO NOTHING;
