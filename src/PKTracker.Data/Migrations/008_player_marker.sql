-- Цветовая метка и короткая подпись на игрока (заметки списком — уже существующая player_note).
--
-- Метка — ссылка на тип из каталога пользователя (%APPDATA%\PKTracker\markers.json): сами типы
-- и их цвета — личная классификация, поэтому живут не в базе и не в репозитории, а у пользователя.
-- Ставятся только вручную: автоматические метки по статам запрещены правилами части румов (iPoker).
CREATE TABLE IF NOT EXISTS player_marker (
    player_id  bigint PRIMARY KEY REFERENCES player ON DELETE CASCADE,
    marker     text,
    caption    text,
    updated_at timestamptz NOT NULL DEFAULT now()
);
