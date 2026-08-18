-- =====================================================================
--  Запросы поиска и подбора.
--  Вся релевантность и всё ранжирование живут здесь, а не в промпте:
--  один вызов = один запрос, результат детерминирован и воспроизводим.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Подбор услуг: гибрид «вектор + полнотекст + опечатки + история заказов».
-- Возвращает уже отсортированный список с rank, score, разбором вклада
-- каждого канала и matched_terms — словами запроса, которые реально
-- совпали. Обоснование подбора в чате строится ТОЛЬКО из matched_terms
-- и величин каналов, а не из порогов «на глазок»: если доказательств
-- совпадения нет, интерфейс обязан это показать, а не выдумать причину.
--
-- DROP перед CREATE: состав RETURNS TABLE изменился, а CREATE OR REPLACE
-- не умеет менять тип возврата. Благодаря этому файл накатывается на живую
-- базу целиком, без пересоздания volume:
--   docker compose exec -T db psql -U prostor -d prostor -v ON_ERROR_STOP=1 \
--     -f /docker-entrypoint-initdb.d/04_functions.sql
-- ---------------------------------------------------------------------
DROP FUNCTION IF EXISTS search.find_products(vector, text, int);
CREATE OR REPLACE FUNCTION search.find_products(
    -- Размерность обязана совпадать с vector(N) в db/init/01_schema.sql и с
    -- AppConfig.EmbeddingDimensions. Здесь было vector(1536) при схеме 1024:
    -- Postgres не проверяет typmod у параметров функций, поэтому расхождение
    -- не падало, а тихо ждало первой смены модели эмбеддингов.
    p_emb   vector(1024),
    p_query text,
    p_top   int DEFAULT 5
)
RETURNS TABLE (
    rank            bigint,
    product_id      text,
    name            text,
    category        text,
    snippet         text,
    similarity      numeric,
    lexical         numeric,
    fuzzy           numeric,
    popularity      numeric,
    score           numeric,
    matched_terms   text[],
    contracts_cnt   int,
    calcs_cnt       int,
    companies_cnt   int,
    typical_days    int,
    operations_cnt  int,
    template_id     text
)
LANGUAGE sql STABLE AS $$
WITH qraw AS (
    -- Все лексемы запроса, включая слова с опечатками, которых нет в каталоге:
    -- их всё равно должен попробовать триграммный канал.
    SELECT DISTINCT lexeme
    FROM unnest(to_tsvector('russian', coalesce(p_query, '')))
    WHERE length(lexeme) >= 3
),
qdf AS (
    -- Сколько услуг содержит лексему. Встроенный русский словарь стоп-слов
    -- снимает не всё: «который» переживает to_tsvector и как лексема «котор»
    -- совпадает с половиной каталога («...документов, КОТОРЫЕ отсутствуют»).
    -- Такое совпадение не является доказательством релевантности, поэтому
    -- частотность считается по фактическим данным, а не по словарю.
    SELECT r.lexeme, c.df
    FROM qraw r
    CROSS JOIN LATERAL (
        SELECT count(DISTINCT p.product_id)::numeric AS df
        FROM catalog.product p
        WHERE p.is_active
          AND ( p.search_tsv @@ (quote_literal(r.lexeme))::tsquery
             OR EXISTS (SELECT 1 FROM search.product_chunk ch
                         WHERE ch.product_id = p.product_id
                           AND ch.chunk_tsv @@ (quote_literal(r.lexeme))::tsquery) )
    ) c
),
qlex AS (
    -- Информативные лексемы: встречаются в каталоге, но не в каждой третьей
    -- услуге. Из них строятся и полнотекстовый запрос, и доказательства
    -- совпадения (matched_terms).
    SELECT d.lexeme
    FROM qdf d
    WHERE d.df > 0
      AND d.df <= greatest((SELECT count(*) FROM catalog.product WHERE is_active) * 0.3, 1)
),
q AS (
    SELECT p_emb AS emb,
           -- OR-запрос: релевантность растёт с числом совпавших слов,
           -- а не требует совпадения всех сразу — иначе длинные формулировки
           -- пользователя не находят ничего
           (SELECT string_agg(quote_literal(lexeme), ' | ') FROM qlex)::tsquery AS tsq,
           websearch_to_tsquery('russian', coalesce(p_query, ''))               AS tsq_all,
           upper(btrim(coalesce(p_query, '')))                                  AS raw
),
chunk_df AS (
    -- Насколько чанк вообще различает услуги. В выгрузке десятки услуг делят
    -- дословно одинаковый текст операций («Помощь в формировании/получении
    -- необходимых документов»), и такой чанк одинаково «близок» к любому
    -- запросу. Без этого штрафа группа дубликатов стабильно занимает всю
    -- выдачу на шумовом уровне, вытесняя единственное точное совпадение.
    SELECT chunk_text, count(DISTINCT product_id)::numeric AS df
    FROM search.product_chunk
    GROUP BY chunk_text
),
ann AS (
    -- ANN отдельным CTE: HNSW работает только на простом ORDER BY + LIMIT
    SELECT c.product_id, c.chunk_text, c.chunk_type,
           c.embedding <=> (SELECT emb FROM q) AS dist
    FROM search.product_chunk c
    WHERE c.embedding IS NOT NULL AND (SELECT emb FROM q) IS NOT NULL
    ORDER BY c.embedding <=> (SELECT emb FROM q)
    LIMIT 300
),
vec AS (
    SELECT DISTINCT ON (a.product_id)
           a.product_id, a.chunk_text, a.chunk_type, a.dist,
           (1.0 / (1 + ln(d.df)))::numeric AS disc
    FROM ann a
    JOIN chunk_df d ON d.chunk_text = a.chunk_text
    ORDER BY a.product_id, a.dist
),
fts AS (
    SELECT p.product_id, ts_rank_cd(p.search_tsv, q.tsq) AS lex
    FROM catalog.product p
    CROSS JOIN q
    WHERE q.tsq IS NOT NULL AND p.search_tsv @@ q.tsq
    LIMIT 200
),
cfts AS (
    -- лексика по содержимому чанков: этапы и операции из реальной истории.
    -- Именно этот канал ловит «запасы», «керн», «3D-модель» — слова,
    -- которых нет в названии продукта, но которые есть в составе работ.
    -- DISTINCT ON вместо max(): нужен не только вес, но и сам совпавший
    -- чанк — он идёт в сниппет и в штраф за неинформативность.
    SELECT DISTINCT ON (c.product_id)
           c.product_id,
           ts_rank_cd(c.chunk_tsv, q.tsq)  AS clex,
           c.chunk_text,
           (1.0 / (1 + ln(d.df)))::numeric AS disc
    FROM search.product_chunk c
    JOIN chunk_df d ON d.chunk_text = c.chunk_text
    CROSS JOIN q
    WHERE q.tsq IS NOT NULL AND c.chunk_tsv @@ q.tsq
    ORDER BY c.product_id, ts_rank_cd(c.chunk_tsv, q.tsq) DESC
),
trg AS (
    -- Страховка от опечаток — пословно. Прежний вариант сравнивал ВЕСЬ запрос
    -- с названием услуги (p.name %> p_query) и на разговорной фразе из шести
    -- слов не срабатывал никогда. word_similarity(слово, название) ищет лучший
    -- фрагмент названия под каждое слово запроса по отдельности.
    -- Предел канала честный: перестановка соседних букв («скважину» ->
    -- «скажвину») рушит сразу три триграммы подряд и сюда не ловится — такой
    -- запрос вытягивает только семантический канал либо уточняющий вопрос.
    -- Здесь именно qraw, а не qlex: слово с опечаткой в каталоге не встречается
    -- (df = 0) и в информативные не попадает, а ловить его должен как раз этот
    -- канал.
    SELECT p.product_id, max(word_similarity(l.lexeme, p.name))::numeric AS sim
    FROM catalog.product p
    CROSS JOIN qraw l
    WHERE p.is_active
      AND length(l.lexeme) >= 4
      AND word_similarity(l.lexeme, p.name) >= 0.4
    GROUP BY p.product_id
),
matched AS (
    -- Доказательства: какие именно слова запроса нашлись у услуги — в названии,
    -- категории, описании или в составе работ. Пустой массив означает, что
    -- лексических доказательств нет вообще и единственным сигналом был вектор.
    SELECT p.product_id, array_agg(DISTINCT l.lexeme) AS terms
    FROM catalog.product p
    CROSS JOIN qlex l
    WHERE p.is_active
      AND ( p.search_tsv @@ (quote_literal(l.lexeme))::tsquery
         OR EXISTS (SELECT 1 FROM search.product_chunk ch
                     WHERE ch.product_id = p.product_id
                       AND ch.chunk_tsv @@ (quote_literal(l.lexeme))::tsquery) )
    GROUP BY p.product_id
),
cand AS (
    SELECT product_id FROM vec
    UNION SELECT product_id FROM fts
    UNION SELECT product_id FROM cfts
    UNION SELECT product_id FROM trg
),
scored AS (
    SELECT p.product_id,
           p.name,
           p.category,
           left(coalesce(cf.chunk_text, v.chunk_text, p.description, p.name), 220) AS snippet,
           round((1 - coalesce(v.dist, 1))::numeric, 4)             AS similarity,
           round(greatest(coalesce(f.lex, 0), coalesce(cf.clex, 0))::numeric, 4) AS lexical,
           round(coalesce(t.sim, 0), 4)                             AS fuzzy,
           round(coalesce(s.popularity_norm, 0), 4)                 AS popularity,
           round((
                 -- вклад вектора гасится, если ближайший чанк не различает услуги
                 0.40 * (1 - coalesce(v.dist, 1)) * coalesce(v.disc, 1)
               + 0.20 * least(coalesce(f.lex, 0)  / 0.4, 1)
               + 0.25 * least(coalesce(cf.clex, 0) / 0.4, 1) * coalesce(cf.disc, 1)
               -- опечатки: полноценный канал вместо прежнего округления 0.05
               + 0.10 * least(coalesce(t.sim, 0), 1)
               -- популярность понижена с 0.10: это тай-брейк при прочих равных,
               -- она не должна вытягивать нерелевантное наверх
               + 0.05 * coalesce(s.popularity_norm, 0)
               + CASE WHEN (SELECT tsq_all FROM q) IS NOT NULL
                       AND p.search_tsv @@ (SELECT tsq_all FROM q) THEN 0.15 ELSE 0 END
               + CASE WHEN upper(p.name) = (SELECT raw FROM q) THEN 1.0 ELSE 0 END
           )::numeric, 4)                                           AS score,
           coalesce(m.terms, ARRAY[]::text[])                       AS matched_terms,
           coalesce(s.contracts_cnt, 0)                             AS contracts_cnt,
           coalesce(s.calcs_cnt, 0)                                 AS calcs_cnt,
           coalesce(s.companies_cnt, 0)                             AS companies_cnt,
           s.typical_duration_days                                  AS typical_days,
           (SELECT count(*)::int FROM catalog.operation o WHERE o.product_id = p.product_id) AS operations_cnt,
           (SELECT t2.template_id FROM tz.template t2
             WHERE p.product_id = ANY (t2.product_ids) LIMIT 1)     AS template_id
    FROM cand c
    JOIN catalog.product p ON p.product_id = c.product_id AND p.is_active
    LEFT JOIN vec     v  ON v.product_id  = c.product_id
    LEFT JOIN fts     f  ON f.product_id  = c.product_id
    LEFT JOIN cfts    cf ON cf.product_id = c.product_id
    LEFT JOIN trg     t  ON t.product_id  = c.product_id
    LEFT JOIN matched m  ON m.product_id  = c.product_id
    LEFT JOIN analytics.product_stats s ON s.product_id = c.product_id
)
SELECT row_number() OVER (ORDER BY score DESC, name) AS rank,
       product_id, name, category, snippet,
       similarity, lexical, fuzzy, popularity, score, matched_terms,
       contracts_cnt, calcs_cnt, companies_cnt, typical_days, operations_cnt,
       coalesce(template_id, 'tpl-generic')
FROM scored
-- Порог намеренно низкий и больше не играет роль «фильтра качества»: score —
-- сумма разнородных величин, и «0.34» сам по себе ничего не значит, поэтому
-- отсекать по нему шум бессмысленно (ровно на этом система уверенно выдавала
-- пять юридических услуг на запрос про бурение). Решение «уверенно / похоже /
-- не нашёл» принимает сервис по разрыву в выдаче и наличию matched_terms —
-- см. TurnPipeline.Assess.
WHERE score >= 0.10
ORDER BY score DESC, name
LIMIT p_top;
$$;

-- ---------------------------------------------------------------------
-- Подбор ИСПОЛНИТЕЛЕЙ по способностям — без выбранной услуги и без периода.
--
-- Зачем отдельно от ops.find_executors: та функция отвечает на вопрос
-- «кто из умеющих делать ЭТУ услугу свободен в ЭТОТ период» и требует
-- product_id + даты. Вопрос «кто вообще может пробурить скважину» до выбора
-- услуги ею не покрывался вообще — пользователю приходилось сначала угадать
-- услугу. Здесь ранжирование по способностям: что компания умеет
-- (company_chunk + services_text) и что реально делала (история расчётов).
-- Занятость сознательно НЕ считается: без периода она не определена, и
-- сервис обязан сказать это прямо, а не показать выдуманную доступность.
-- ---------------------------------------------------------------------
DROP FUNCTION IF EXISTS search.find_companies(vector, text, int);
CREATE OR REPLACE FUNCTION search.find_companies(
    p_emb   vector(1024),
    p_query text,
    p_top   int DEFAULT 6
)
RETURNS TABLE (
    rank          bigint,
    company_id    text,
    name          text,
    rating        int,
    similarity    numeric,
    lexical       numeric,
    score         numeric,
    matched_terms text[],
    calcs_cnt     int,
    products_cnt  int,
    last_end_date date,
    top_products  text[],
    snippet       text
)
LANGUAGE sql STABLE AS $$
WITH q AS (
    SELECT p_emb AS emb,
           (SELECT string_agg(quote_literal(lexeme), ' | ')
              FROM unnest(to_tsvector('russian', coalesce(p_query, ''))))::tsquery AS tsq
),
qlex AS (
    SELECT DISTINCT lexeme
    FROM unnest(to_tsvector('russian', coalesce(p_query, '')))
    WHERE length(lexeme) >= 3
),
-- Профиль компании одной строкой: чем занимается по справочнику + названия
-- услуг, которые она реально выполняла по истории расчётов. Компаний в
-- выгрузке 13, поэтому tsvector считается на лету, отдельный индекс не нужен.
profile AS (
    SELECT co.company_id,
           co.name,
           co.rating,
           co.name || ' ' || coalesce(co.info, '') || ' ' || coalesce(co.services_text, '') || ' '
             || coalesce((SELECT string_agg(DISTINCT pr.name, ' ')
                            FROM analytics.company_product_stats cps
                            JOIN catalog.product pr ON pr.product_id = cps.product_id
                           WHERE cps.company_id = co.company_id), '') AS profile_text
    FROM catalog.company co
    WHERE co.is_active
),
prof AS (
    SELECT p.*, to_tsvector('russian', p.profile_text) AS tsv
    FROM profile p
),
ann AS (
    SELECT c.company_id, c.chunk_text,
           c.embedding <=> (SELECT emb FROM q) AS dist
    FROM search.company_chunk c
    WHERE c.embedding IS NOT NULL AND (SELECT emb FROM q) IS NOT NULL
    ORDER BY c.embedding <=> (SELECT emb FROM q)
    LIMIT 100
),
vec AS (
    SELECT DISTINCT ON (company_id) company_id, chunk_text, dist
    FROM ann ORDER BY company_id, dist
),
fts AS (
    SELECT pr.company_id, ts_rank_cd(pr.tsv, q.tsq) AS lex
    FROM prof pr
    CROSS JOIN q
    WHERE q.tsq IS NOT NULL AND pr.tsv @@ q.tsq
),
trg AS (
    SELECT pr.company_id, max(word_similarity(l.lexeme, pr.profile_text))::numeric AS sim
    FROM prof pr
    CROSS JOIN qlex l
    WHERE length(l.lexeme) >= 4
      AND word_similarity(l.lexeme, pr.profile_text) >= 0.5
    GROUP BY pr.company_id
),
matched AS (
    SELECT pr.company_id, array_agg(DISTINCT l.lexeme) AS terms
    FROM prof pr
    CROSS JOIN qlex l
    WHERE pr.tsv @@ (quote_literal(l.lexeme))::tsquery
    GROUP BY pr.company_id
),
hist AS (
    SELECT cps.company_id,
           sum(cps.calcs_cnt)::int          AS calcs_cnt,
           count(DISTINCT cps.product_id)::int AS products_cnt,
           max(cps.last_end_date)           AS last_end_date,
           (array_agg(DISTINCT p2.name))[1:3] AS top_products
    FROM analytics.company_product_stats cps
    JOIN catalog.product p2 ON p2.product_id = cps.product_id
    GROUP BY cps.company_id
),
cand AS (
    SELECT company_id FROM vec
    UNION SELECT company_id FROM fts
    UNION SELECT company_id FROM trg
),
scored AS (
    SELECT pr.company_id,
           pr.name,
           pr.rating,
           round((1 - coalesce(v.dist, 1))::numeric, 4)                  AS similarity,
           round(coalesce(f.lex, 0)::numeric, 4)                         AS lexical,
           round((
                 0.35 * (1 - coalesce(v.dist, 1))
               + 0.30 * least(coalesce(f.lex, 0) / 0.4, 1)
               + 0.10 * least(coalesce(t.sim, 0), 1)
               -- подтверждённый опыт: логарифм, чтобы один крупный подрядчик
               -- не забивал выдачу только объёмом истории
               + 0.15 * least(ln(1 + coalesce(h.calcs_cnt, 0)) / ln(30), 1)
               + 0.10 * (pr.rating::numeric / 5)
           )::numeric, 4)                                                AS score,
           coalesce(m.terms, ARRAY[]::text[])                            AS matched_terms,
           coalesce(h.calcs_cnt, 0)                                      AS calcs_cnt,
           coalesce(h.products_cnt, 0)                                   AS products_cnt,
           h.last_end_date,
           coalesce(h.top_products, ARRAY[]::text[])                     AS top_products,
           left(coalesce(v.chunk_text, pr.profile_text), 220)            AS snippet
    FROM cand c
    JOIN prof pr ON pr.company_id = c.company_id
    LEFT JOIN vec     v ON v.company_id = c.company_id
    LEFT JOIN fts     f ON f.company_id = c.company_id
    LEFT JOIN trg     t ON t.company_id = c.company_id
    LEFT JOIN matched m ON m.company_id = c.company_id
    LEFT JOIN hist    h ON h.company_id = c.company_id
)
SELECT row_number() OVER (ORDER BY score DESC, name) AS rank,
       company_id, name, rating, similarity, lexical, score, matched_terms,
       calcs_cnt, products_cnt, last_end_date, top_products, snippet
FROM scored
WHERE score >= 0.10
ORDER BY score DESC, name
LIMIT p_top;
$$;

-- ---------------------------------------------------------------------
-- Типовые этапы продукта: агрегируются из реальных расчётов.
-- Именно из них конструктор собирает раздел «Содержание работ».
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION catalog.product_stages(p_product_id text, p_top int DEFAULT 12)
RETURNS TABLE (
    stage_key      text,
    name           text,
    used_cnt       int,
    median_days    int,
    documentation  text,
    order_hint     numeric
)
LANGUAGE sql STABLE AS $$
SELECT md5(lower(btrim(s.name)))                                          AS stage_key,
       min(s.name)                                                        AS name,
       count(*)::int                                                      AS used_cnt,
       percentile_cont(0.5) WITHIN GROUP (
           ORDER BY (s.end_date - s.start_date))::int                     AS median_days,
       (array_agg(s.documentation_list ORDER BY s.documentation_list)
            FILTER (WHERE s.documentation_list IS NOT NULL))[1]           AS documentation,
       avg(s.order_num)                                                   AS order_hint
FROM ops.stage s
JOIN ops.calc  c ON c.calc_id = s.calc_id
WHERE c.product_id = p_product_id
GROUP BY md5(lower(btrim(s.name)))
ORDER BY count(*) DESC, avg(s.order_num)
LIMIT p_top;
$$;

-- ---------------------------------------------------------------------
-- Подбор исполнителей на период с обоснованием.
--
-- Важное наблюдение по данным: этапы работ у компаний идут внахлёст и
-- покрывают почти весь год, поэтому «занят / свободен» как бинарный признак
-- бессмысленен — свободных не будет ни одного. Загрузку считаем
-- ОТНОСИТЕЛЬНОЙ: число параллельных работ компании в заданном периоде против
-- самого загруженного кандидата. Это сравнимая величина, она не требует
-- выдуманной константы «пропускная способность» и честно отвечает на вопрос
-- «кто из подходящих исполнителей загружен меньше».
-- Календарные дни показываем справочно — объединением интервалов, а не
-- суммой: перекрывающиеся этапы иначе дают дни сверх длины периода.
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION ops.find_executors(
    p_product_id text,
    p_from       date,
    p_to         date,
    p_allow_sub  boolean DEFAULT true,
    p_top        int     DEFAULT 8
)
RETURNS TABLE (
    rank          bigint,
    company_id    text,
    name          text,
    rating        int,
    experience    int,
    last_end_date date,
    busy_days     int,
    period_days   int,
    availability  text,
    load_pct      int,
    active_works  int,
    subcontract   boolean,
    score         numeric,
    reasons       text[]
)
LANGUAGE sql STABLE AS $$
WITH p AS (
    SELECT daterange(p_from, p_to, '[]') AS period,
           greatest((p_to - p_from) + 1, 1) AS days,
           (SELECT category FROM catalog.product WHERE product_id = p_product_id) AS cat
),
direct AS (                       -- прямой опыт по продукту или действующий договор
    SELECT s.company_id, s.calcs_cnt::int AS experience, s.last_end_date, false AS is_sub
    FROM analytics.company_product_stats s
    WHERE s.product_id = p_product_id
    UNION
    SELECT cp.company_id, 0, NULL::date, false
    FROM ops.contract_product cp
    WHERE cp.product_id = p_product_id AND cp.company_id IS NOT NULL
),
sub AS (                          -- субподряд: опыт в той же категории работ
    SELECT s.company_id,
           sum(s.calcs_cnt)::int AS experience,
           max(s.last_end_date)  AS last_end_date,
           true                  AS is_sub
    FROM analytics.company_product_stats s
    JOIN catalog.product pr ON pr.product_id = s.product_id
    WHERE p_allow_sub
      AND pr.category = (SELECT cat FROM p)
      AND s.product_id <> p_product_id
      AND s.company_id NOT IN (SELECT company_id FROM direct)
    GROUP BY s.company_id
),
cand AS (
    SELECT company_id,
           max(experience)    AS experience,
           max(last_end_date) AS last_end_date,
           bool_and(is_sub)   AS is_sub
    FROM (SELECT * FROM direct UNION ALL SELECT * FROM sub) u
    WHERE company_id IS NOT NULL
    GROUP BY company_id
),
work AS (                         -- сколько работ идёт параллельно в этом периоде
    SELECT b.company_id, count(DISTINCT b.calc_id)::int AS active_works
    FROM ops.booking b, p
    WHERE b.period && p.period
    GROUP BY b.company_id
),
days AS (                         -- календарные дни занятости: объединение, не сумма
    SELECT b.company_id, count(DISTINCT g.d)::int AS busy_days
    FROM ops.booking b, p,
         LATERAL generate_series(
             greatest(lower(b.period), p_from),
             least(upper(b.period) - 1, p_to),
             interval '1 day') AS g(d)
    WHERE b.period && p.period
    GROUP BY b.company_id
),
base AS (
    SELECT co.company_id, co.name, co.rating,
           coalesce(c.experience, 0)                       AS experience,
           c.last_end_date,
           c.is_sub,
           coalesce(d.busy_days, 0)                        AS busy_days,
           (SELECT days FROM p)                            AS period_days,
           coalesce(w.active_works, 0)                     AS active_works,
           coalesce(w.active_works, 0)::numeric
             / nullif((SELECT max(coalesce(w2.active_works, 0))
                       FROM cand c2
                       LEFT JOIN work w2 ON w2.company_id = c2.company_id), 0) AS load,
           coalesce(c.experience, 0)::numeric
             / nullif((SELECT max(experience) FROM cand), 0) AS exp_norm
    FROM cand c
    JOIN catalog.company co ON co.company_id = c.company_id AND co.is_active
    LEFT JOIN work w ON w.company_id = c.company_id
    LEFT JOIN days d ON d.company_id = c.company_id
),
scored AS (
    SELECT *,
           1 - coalesce(load, 0) AS avail,
           round((
               (  0.35 * coalesce(exp_norm, 0)
                + 0.30 * (1 - coalesce(load, 0))
                + 0.25 * (rating::numeric / 5)
                + 0.10 * CASE WHEN last_end_date IS NOT NULL THEN 1 ELSE 0 END
               ) * CASE WHEN is_sub THEN 0.85 ELSE 1 END
           )::numeric, 4) AS score
    FROM base
)
SELECT row_number() OVER (ORDER BY score DESC, name) AS rank,
       company_id, name, rating, experience, last_end_date,
       busy_days, period_days,
       CASE WHEN coalesce(load, 0) = 0     THEN 'free'
            WHEN coalesce(load, 0) < 0.35   THEN 'moderate'
            WHEN coalesce(load, 0) < 0.75   THEN 'busy'
            ELSE 'overloaded' END AS availability,
       round(100 * coalesce(load, 0))::int AS load_pct,
       active_works,
       is_sub,
       score,
       array_remove(ARRAY[
           CASE WHEN experience > 0
                THEN 'выполнено работ по этой услуге: ' || experience END,
           CASE WHEN last_end_date IS NOT NULL
                THEN 'последняя работа завершена ' || to_char(last_end_date, 'DD.MM.YYYY') END,
           CASE WHEN active_works = 0 THEN 'в заявленный период работ не запланировано'
                ELSE 'в этот период уже ведёт работ: ' || active_works
                     || ' (относительная загрузка '
                     || round(100 * coalesce(load, 0))::int || '%)' END,
           CASE WHEN busy_days > 0
                THEN 'занятые календарные дни: ' || busy_days || ' из ' || period_days END,
           'рейтинг ' || rating || ' из 5',
           CASE WHEN is_sub THEN 'предлагается как субподряд: опыт в смежных услугах категории' END
       ], NULL) AS reasons
FROM scored
-- Перегруженных не выбрасываем: в этих данных полностью свободных
-- исполнителей не бывает. Загрузка влияет на место в списке и видна
-- пользователю в обосновании — решение остаётся за ним.
ORDER BY score DESC, name
LIMIT p_top;
$$;

-- ---------------------------------------------------------------------
-- Связанные услуги: что заказывали вместе в рамках одного договора
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION analytics.related_products(p_product_id text, p_top int DEFAULT 4)
RETURNS TABLE (product_id text, name text, category text, cnt int, confidence numeric)
LANGUAGE sql STABLE AS $$
SELECT r.related_product_id, p.name, p.category, r.cnt::int, r.confidence
FROM analytics.product_cooccurrence r
JOIN catalog.product p ON p.product_id = r.related_product_id
WHERE r.product_id = p_product_id
ORDER BY r.cnt DESC, p.name
LIMIT p_top;
$$;

-- ---------------------------------------------------------------------
-- Аналогичные выполненные работы
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION ops.similar_calcs(p_product_id text, p_top int DEFAULT 5)
RETURNS TABLE (
    calc_id text, calc_name text, company_id text, company_name text,
    contract_number text, start_date date, end_date date, duration_days int, stages_cnt int
)
LANGUAGE sql STABLE AS $$
SELECT c.calc_id, c.name, c.company_id, co.name, ct.contract_number,
       c.start_date, c.end_date,
       (c.end_date - c.start_date)::int,
       (SELECT count(*)::int FROM ops.stage s WHERE s.calc_id = c.calc_id)
FROM ops.calc c
LEFT JOIN catalog.company co ON co.company_id = c.company_id
LEFT JOIN ops.contract    ct ON ct.contract_id = c.contract_id
WHERE c.product_id = p_product_id AND c.start_date IS NOT NULL
ORDER BY c.end_date DESC NULLS LAST
LIMIT p_top;
$$;

-- ---------------------------------------------------------------------
-- Ориентировочная структура стоимости: роли и единицы измерения
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION catalog.product_pricing(p_product_id text)
RETURNS TABLE (measurement_type text, measurement_name text, roles_cnt int, roles text[])
LANGUAGE sql STABLE AS $$
SELECT coalesce(pr.measurement_type, 'н/д'),
       coalesce(pr.measurement_name, 'н/д'),
       count(*)::int,
       (array_agg(DISTINCT pr.price_name))[1:6]
FROM catalog.price pr
WHERE pr.product_id = p_product_id
GROUP BY 1, 2
ORDER BY 3 DESC;
$$;
