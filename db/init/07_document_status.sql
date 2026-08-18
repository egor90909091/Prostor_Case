-- =====================================================================
--  Статус ТЗ: черновик vs готовый документ.
--
--  Раньше «Сформировать документ» был единственным способом сохранить
--  состояние конструктора — версия в tz.document создавалась только
--  вместе с готовым .docx и требовала прохождения гейта по рискам
--  (canGenerate). Пользователь не мог сохранить промежуточный вариант
--  ТЗ, если форма ещё не готова к выгрузке.
--
--  status='draft'  — сохранено кнопкой «Сохранить черновик», гейт по
--                     рискам не применяется, документ помечается в
--                     «Мои заявки» бейджем «Черновик».
--  status='final'  — обычная генерация («Сформировать документ»),
--                     поведение не меняется, значение по умолчанию
--                     сохраняет обратную совместимость со старыми
--                     строками.
--
--  Как накатить на уже поднятую БД (init-скрипты не перевыполняются на
--  существующем volume — см. комментарий в 04_functions.sql):
--    docker compose exec -T db psql -U prostor -d prostor -v ON_ERROR_STOP=1 \
--      -f /docker-entrypoint-initdb.d/07_document_status.sql
-- =====================================================================

ALTER TABLE tz.document
    ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'final';

-- ALTER TABLE ... ADD CONSTRAINT не поддерживает IF NOT EXISTS в Postgres,
-- поэтому идемпотентность обеспечиваем явным DROP перед ADD.
ALTER TABLE tz.document DROP CONSTRAINT IF EXISTS tz_document_status_check;
ALTER TABLE tz.document
    ADD CONSTRAINT tz_document_status_check CHECK (status IN ('draft', 'final'));

CREATE INDEX IF NOT EXISTS tz_document_status_idx ON tz.document (status);
