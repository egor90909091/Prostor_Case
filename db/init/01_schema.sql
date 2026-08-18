-- =====================================================================
--  ПРОСТОР MVP · схема данных
--  Кейс 1 «Умный конструктор ТЗ» + Кейс 2 «ИИ-агент умного поиска»
-- =====================================================================
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE SCHEMA IF NOT EXISTS catalog;    -- справочники: продукты, операции, компании
CREATE SCHEMA IF NOT EXISTS ops;        -- история: договоры, расчёты, этапы, занятость
CREATE SCHEMA IF NOT EXISTS search;     -- эмбеддинги и кэш
CREATE SCHEMA IF NOT EXISTS chat;       -- сессии агента
CREATE SCHEMA IF NOT EXISTS tz;         -- шаблоны и документы ТЗ
CREATE SCHEMA IF NOT EXISTS analytics;  -- витрины и логи

-- ---------------------------------------------------------------- catalog
CREATE TABLE catalog.company (
    company_id    text PRIMARY KEY,
    code          text NOT NULL,
    name          text NOT NULL,
    info          text,
    services_text text,
    rating        int  NOT NULL DEFAULT 3,
    is_active     boolean NOT NULL DEFAULT true
);

CREATE TABLE catalog.product (
    product_id  text PRIMARY KEY,
    name        text NOT NULL,
    description text,
    category    text,
    is_active   boolean NOT NULL DEFAULT true,
    -- лексический канал поиска: имя важнее категории, категория важнее описания
    search_tsv  tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('russian', coalesce(name, '')),        'A') ||
        setweight(to_tsvector('russian', coalesce(category, '')),    'B') ||
        setweight(to_tsvector('russian', coalesce(description, '')), 'C')
    ) STORED
);
CREATE INDEX product_tsv_idx  ON catalog.product USING gin (search_tsv);
CREATE INDEX product_trgm_idx ON catalog.product USING gin (name gin_trgm_ops);

CREATE TABLE catalog.operation (
    operation_id text PRIMARY KEY,
    product_id   text NOT NULL REFERENCES catalog.product ON DELETE CASCADE,
    name         text NOT NULL,
    order_num    int  NOT NULL DEFAULT 0,
    is_required  boolean NOT NULL DEFAULT false
);
CREATE INDEX ON catalog.operation (product_id);

CREATE TABLE catalog.price (
    price_id         text PRIMARY KEY,
    product_id       text NOT NULL REFERENCES catalog.product ON DELETE CASCADE,
    price_name       text,
    measurement_name text,
    measurement_type text
);
CREATE INDEX ON catalog.price (product_id);

-- ---------------------------------------------------------------- ops
CREATE TABLE ops.contract (
    contract_id     text PRIMARY KEY,
    contract_number text,
    company_id      text
);

CREATE TABLE ops.contract_product (
    contract_id text NOT NULL,
    product_id  text NOT NULL REFERENCES catalog.product ON DELETE CASCADE,
    company_id  text,
    PRIMARY KEY (contract_id, product_id)
);
CREATE INDEX ON ops.contract_product (product_id);
CREATE INDEX ON ops.contract_product (company_id);

CREATE TABLE ops.calc (
    calc_id     text PRIMARY KEY,
    contract_id text,
    company_id  text,
    product_id  text REFERENCES catalog.product ON DELETE CASCADE,
    name        text,
    start_date  date,
    end_date    date
);
CREATE INDEX ON ops.calc (product_id);
CREATE INDEX ON ops.calc (company_id);

CREATE TABLE ops.stage (
    stage_id           text PRIMARY KEY,
    calc_id            text REFERENCES ops.calc ON DELETE CASCADE,
    parent_stage_id    text,
    name               text NOT NULL,
    start_date         date,
    end_date           date,
    order_num          int,
    documentation_list text
);
CREATE INDEX ON ops.stage (calc_id);

-- Занятость исполнителей — не отдельная таблица, а проекция реальных этапов.
-- Обновляется вместе с историей: REFRESH MATERIALIZED VIEW ops.booking.
CREATE MATERIALIZED VIEW ops.booking AS
SELECT c.company_id,
       c.calc_id,
       c.product_id,
       s.stage_id,
       daterange(s.start_date, s.end_date, '[]') AS period
FROM ops.stage s
JOIN ops.calc  c ON c.calc_id = s.calc_id
WHERE s.start_date IS NOT NULL
  AND s.end_date  IS NOT NULL
  AND s.end_date >= s.start_date
  AND c.company_id IS NOT NULL;
CREATE INDEX booking_period_idx  ON ops.booking USING gist (period);
CREATE INDEX booking_company_idx ON ops.booking (company_id);

-- ---------------------------------------------------------------- search
-- 1024 измерения = нативная размерность bge-m3 (см. EMBEDDING_MODEL / EMBEDDING_DIMENSIONS
-- в docker-compose.yml). При смене модели эмбеддингов на другую размерность нужно
-- поменять vector(...) во всех трёх местах ниже и пересоздать pgdata volume.
CREATE TABLE search.product_chunk (
    id         bigserial PRIMARY KEY,
    product_id text NOT NULL REFERENCES catalog.product ON DELETE CASCADE,
    chunk_type text NOT NULL,               -- name | operation | stage | doc
    chunk_text text NOT NULL,
    embedding  vector(1024),
    chunk_tsv  tsvector GENERATED ALWAYS AS (to_tsvector('russian', chunk_text)) STORED,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX product_chunk_hnsw ON search.product_chunk
    USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
CREATE INDEX product_chunk_tsv_idx ON search.product_chunk USING gin (chunk_tsv);
CREATE INDEX ON search.product_chunk (product_id);
CREATE INDEX ON search.product_chunk (chunk_type);

CREATE TABLE search.company_chunk (
    id         bigserial PRIMARY KEY,
    company_id text NOT NULL REFERENCES catalog.company ON DELETE CASCADE,
    chunk_text text NOT NULL,
    embedding  vector(1024),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX company_chunk_hnsw ON search.company_chunk
    USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);

-- Кэш эмбеддингов: повторяющиеся формулировки не уходят в модель второй раз
CREATE TABLE search.embedding_cache (
    text_hash  text PRIMARY KEY,
    embedding  vector(1024) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- chat
CREATE TABLE chat.session (
    session_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_name text,
    state         jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE chat.message (
    message_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id uuid NOT NULL REFERENCES chat.session ON DELETE CASCADE,
    seq        int  NOT NULL,
    role       text NOT NULL CHECK (role IN ('user', 'assistant')),
    blocks     jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (session_id, seq)
);

-- Ход диалога. Хранится целиком, чтобы клиент мог добрать события после обрыва
-- долгого HTTP-соединения: GET /turns/{turnId}/stream?fromSeq=N
CREATE TABLE chat.turn (
    turn_id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id      uuid NOT NULL REFERENCES chat.session ON DELETE CASCADE,
    idempotency_key text NOT NULL,
    status          text NOT NULL DEFAULT 'running',   -- running | done | failed | cancelled
    events          jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (session_id, idempotency_key)
);
CREATE INDEX ON chat.turn (session_id, status);

-- ---------------------------------------------------------------- tz
CREATE TABLE tz.template (
    template_id     text PRIMARY KEY,
    name            text NOT NULL,
    type_code       text NOT NULL,
    product_ids     text[] NOT NULL DEFAULT '{}',
    sections        jsonb  NOT NULL,   -- [{key,title,required,source}]
    required_fields jsonb  NOT NULL,   -- [{key,section,title,weight,hint}]
    risk_rules      jsonb  NOT NULL    -- [{code,severity,title,recommendation,when}]
);

CREATE TABLE tz.document (
    tz_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id  uuid,
    template_id text REFERENCES tz.template,
    product_id  text,
    company_ids text[] NOT NULL DEFAULT '{}',
    payload     jsonb  NOT NULL,
    readiness   int    NOT NULL DEFAULT 0,
    risks       jsonb  NOT NULL DEFAULT '[]'::jsonb,
    version     int    NOT NULL DEFAULT 1,
    storage_key text,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON tz.document (product_id);
CREATE INDEX ON tz.document (created_at);

-- ---------------------------------------------------------------- analytics
CREATE TABLE analytics.search_log (
    id             bigserial PRIMARY KEY,
    session_id     uuid,
    query          text NOT NULL,
    top_product_id text,
    top_score      numeric,
    hits           int NOT NULL DEFAULT 0,
    recognized     boolean NOT NULL DEFAULT false,
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE analytics.event (
    id         bigserial PRIMARY KEY,
    session_id uuid,
    kind       text NOT NULL,      -- product_selected | executors_selected | tz_created | gap_left | ...
    payload    jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON analytics.event (kind, created_at);

-- ---------------------------------------------------------------- витрины
CREATE MATERIALIZED VIEW analytics.product_stats AS
WITH dur AS (
    SELECT product_id,
           count(*)                                                            AS calcs_cnt,
           percentile_cont(0.5) WITHIN GROUP (ORDER BY (end_date - start_date)) AS median_days,
           min(end_date - start_date)                                          AS min_days,
           max(end_date - start_date)                                          AS max_days
    FROM ops.calc
    WHERE start_date IS NOT NULL AND end_date IS NOT NULL AND end_date >= start_date
    GROUP BY product_id
),
cp AS (
    SELECT product_id,
           count(DISTINCT contract_id) AS contracts_cnt,
           count(DISTINCT company_id)  AS companies_cnt
    FROM ops.contract_product
    GROUP BY product_id
),
st AS (
    SELECT c.product_id, avg(x.cnt) AS avg_stages
    FROM (SELECT calc_id, count(*) AS cnt FROM ops.stage GROUP BY calc_id) x
    JOIN ops.calc c ON c.calc_id = x.calc_id
    GROUP BY c.product_id
)
SELECT p.product_id,
       coalesce(cp.contracts_cnt, 0)                     AS contracts_cnt,
       coalesce(cp.companies_cnt, 0)                     AS companies_cnt,
       coalesce(dur.calcs_cnt, 0)                        AS calcs_cnt,
       dur.median_days::int                              AS typical_duration_days,
       dur.min_days::int                                 AS min_duration_days,
       dur.max_days::int                                 AS max_duration_days,
       round(coalesce(st.avg_stages, 0), 1)              AS avg_stages,
       round(( coalesce(dur.calcs_cnt, 0)::numeric
               / nullif(max(coalesce(dur.calcs_cnt, 0)) OVER (), 0) ), 4) AS popularity_norm
FROM catalog.product p
LEFT JOIN dur ON dur.product_id = p.product_id
LEFT JOIN cp  ON cp.product_id  = p.product_id
LEFT JOIN st  ON st.product_id  = p.product_id;
CREATE UNIQUE INDEX ON analytics.product_stats (product_id);

-- Связанные услуги: что чаще всего заказывают вместе в рамках одного договора
CREATE MATERIALIZED VIEW analytics.product_cooccurrence AS
WITH pair AS (
    SELECT a.product_id AS product_id,
           b.product_id AS related_product_id,
           count(DISTINCT a.contract_id) AS cnt
    FROM ops.contract_product a
    JOIN ops.contract_product b
      ON b.contract_id = a.contract_id AND b.product_id <> a.product_id
    GROUP BY 1, 2
),
tot AS (SELECT product_id, count(DISTINCT contract_id) AS n FROM ops.contract_product GROUP BY 1)
SELECT p.product_id,
       p.related_product_id,
       p.cnt,
       round(p.cnt::numeric / nullif(t.n, 0), 4) AS confidence
FROM pair p JOIN tot t ON t.product_id = p.product_id;
CREATE INDEX ON analytics.product_cooccurrence (product_id, cnt DESC);

-- Опыт исполнителя по продукту
CREATE MATERIALIZED VIEW analytics.company_product_stats AS
SELECT c.company_id,
       c.product_id,
       count(*)                                     AS calcs_cnt,
       max(c.end_date)                              AS last_end_date,
       sum(coalesce(c.end_date - c.start_date, 0))  AS total_days
FROM ops.calc c
WHERE c.company_id IS NOT NULL AND c.product_id IS NOT NULL
GROUP BY 1, 2;
CREATE UNIQUE INDEX ON analytics.company_product_stats (company_id, product_id);
