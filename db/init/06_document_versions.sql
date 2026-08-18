-- =====================================================================
--  Версионирование ТЗ без привязки к session_id.
--
--  tz.document.version сегодня инкрементируется по session_id
--  (Data.SaveDocumentAsync): max(version)+1 WHERE session_id = @s.
--  Это работает только для ТЗ, собранных из чата: ручные ТЗ из
--  конструктора имеют session_id = NULL, и все такие документы делят
--  один общий счётчик версий, что неверно.
--
--  Решение: parent_tz_id указывает на исходный документ, от которого
--  пошли правки. Версии одного ТЗ — это все строки с совпадающим
--  parent_tz_id плюс сам корневой документ (tz_id = parent_tz_id).
--  Для документов из чата поле остаётся NULL — им продолжает
--  управлять session_id, обратная совместимость сохраняется.
--
--  Как накатить на уже поднятую БД (init-скрипты не перевыполняются на
--  существующем volume — см. комментарий в 04_functions.sql):
--    docker compose exec -T db psql -U prostor -d prostor -v ON_ERROR_STOP=1 \
--      -f /docker-entrypoint-initdb.d/06_document_versions.sql
-- =====================================================================

ALTER TABLE tz.document
    ADD COLUMN IF NOT EXISTS parent_tz_id uuid REFERENCES tz.document(tz_id);

CREATE INDEX IF NOT EXISTS tz_document_parent_tz_id_idx
    ON tz.document (parent_tz_id);
