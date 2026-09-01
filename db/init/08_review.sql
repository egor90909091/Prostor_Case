-- =====================================================================
--  Согласование ТЗ подрядчиком.
--
--  До этого приложение целиком жило на стороне заказчика: ТЗ можно было
--  собрать и выгрузить, но не направить исполнителю и не получить ответ.
--  Компании из catalog.company фигурировали только в выдаче ранжирования.
--
--  Здесь появляются две сущности процесса согласования:
--
--  tz.assignment — направление конкретной ВЕРСИИ ТЗ конкретной компании.
--      Одно ТЗ можно направить нескольким подрядчикам, и решение у каждого
--      своё, поэтому это отдельная строка, а не поле в tz.document.
--      tz.document.status (draft|final) остаётся про готовность документа
--      и здесь не участвует: направить можно только final.
--
--  tz.comment — замечания и решения. Тред привязан к КОРНЮ цепочки версий
--      (root_tz_id = coalesce(parent_tz_id, tz_id)), а не к конкретной
--      строке tz.document: заказчик правит ТЗ после «на доработку», версия
--      меняется, а обсуждение должно продолжаться, а не начинаться заново.
--      section_key — ключ раздела шаблона (tz.base_sections): замечание
--      можно повесить на конкретный раздел, и заказчик из него попадает
--      сразу в нужные поля конструктора. NULL — замечание про ТЗ целиком.
--
--  Как накатить на уже поднятую БД (init-скрипты не перевыполняются на
--  существующем volume — см. комментарий в 04_functions.sql):
--    docker compose exec -T db psql -U prostor -d prostor -v ON_ERROR_STOP=1 \
--      -f /docker-entrypoint-initdb.d/08_review.sql
-- =====================================================================

CREATE TABLE IF NOT EXISTS tz.assignment (
    assignment_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tz_id         uuid NOT NULL REFERENCES tz.document(tz_id) ON DELETE CASCADE,
    -- Корень цепочки версий на момент направления: по нему собирается тред
    -- и история согласования по всем версиям одного ТЗ.
    root_tz_id    uuid NOT NULL,
    company_id    text NOT NULL REFERENCES catalog.company(company_id) ON DELETE CASCADE,
    status        text NOT NULL DEFAULT 'sent',
    -- Сопроводительная записка заказчика к направлению
    note          text,
    created_at    timestamptz NOT NULL DEFAULT now(),
    viewed_at     timestamptz,
    decided_at    timestamptz,
    -- Одна версия ТЗ направляется компании один раз; повторное направление
    -- после правок — это уже новая версия, то есть другой tz_id.
    UNIQUE (tz_id, company_id)
);

-- ALTER TABLE ... ADD CONSTRAINT не поддерживает IF NOT EXISTS в Postgres,
-- поэтому идемпотентность обеспечиваем явным DROP перед ADD.
ALTER TABLE tz.assignment DROP CONSTRAINT IF EXISTS tz_assignment_status_check;
ALTER TABLE tz.assignment
    ADD CONSTRAINT tz_assignment_status_check
    CHECK (status IN ('sent', 'viewed', 'approved', 'revision', 'rejected'));

CREATE INDEX IF NOT EXISTS tz_assignment_company_idx ON tz.assignment (company_id, status);
CREATE INDEX IF NOT EXISTS tz_assignment_root_idx    ON tz.assignment (root_tz_id);

CREATE TABLE IF NOT EXISTS tz.comment (
    comment_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    root_tz_id    uuid NOT NULL,
    tz_id         uuid NOT NULL REFERENCES tz.document(tz_id) ON DELETE CASCADE,
    assignment_id uuid REFERENCES tz.assignment(assignment_id) ON DELETE CASCADE,
    author_kind   text NOT NULL,
    -- 'ntc' для заказчика, company_id для подрядчика
    author_id     text NOT NULL,
    section_key   text,
    kind          text NOT NULL DEFAULT 'comment',
    -- Заполняется только для kind='decision': approved | revision | rejected
    decision      text,
    text          text NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE tz.comment DROP CONSTRAINT IF EXISTS tz_comment_author_kind_check;
ALTER TABLE tz.comment
    ADD CONSTRAINT tz_comment_author_kind_check
    CHECK (author_kind IN ('customer', 'contractor'));

ALTER TABLE tz.comment DROP CONSTRAINT IF EXISTS tz_comment_kind_check;
ALTER TABLE tz.comment
    ADD CONSTRAINT tz_comment_kind_check CHECK (kind IN ('comment', 'decision'));

ALTER TABLE tz.comment DROP CONSTRAINT IF EXISTS tz_comment_decision_check;
ALTER TABLE tz.comment
    ADD CONSTRAINT tz_comment_decision_check
    CHECK (decision IS NULL OR decision IN ('approved', 'revision', 'rejected'));

CREATE INDEX IF NOT EXISTS tz_comment_root_idx ON tz.comment (root_tz_id, created_at);
