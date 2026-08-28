-- Проверки схемы и поисковых функций на реальных данных выгрузки.
--   psql "$DSN" -v ON_ERROR_STOP=1 -f tests/sql_checks.sql
-- Любая непройденная проверка роняет скрипт через RAISE EXCEPTION.

\set ON_ERROR_STOP on
\timing off

DO $$
DECLARE
    v_products int;
    v_companies int;
    v_stages int;
    v_chunks int;
    v_indexed int;
BEGIN
    SELECT count(*) INTO v_products  FROM catalog.product;
    SELECT count(*) INTO v_companies FROM catalog.company;
    SELECT count(*) INTO v_stages    FROM ops.stage;
    SELECT count(*) INTO v_chunks    FROM search.product_chunk;
    SELECT count(*) INTO v_indexed   FROM search.product_chunk WHERE embedding IS NOT NULL;

    IF v_products < 40 THEN RAISE EXCEPTION 'мало продуктов: %', v_products; END IF;
    IF v_companies < 10 THEN RAISE EXCEPTION 'мало компаний: %', v_companies; END IF;
    IF v_stages < 1000 THEN RAISE EXCEPTION 'мало этапов: %', v_stages; END IF;
    IF v_chunks < 300 THEN RAISE EXCEPTION 'мало чанков: %', v_chunks; END IF;

    RAISE NOTICE 'данные: продуктов %, компаний %, этапов %, чанков % (проиндексировано %)',
        v_products, v_companies, v_stages, v_chunks, v_indexed;
END $$;

-- Дубли продуктов по названию должны быть схлопнуты на этапе ETL
DO $$
DECLARE v_dupes int;
BEGIN
    SELECT count(*) INTO v_dupes FROM (
        SELECT lower(btrim(name)) FROM catalog.product GROUP BY 1 HAVING count(*) > 1) x;
    IF v_dupes > 0 THEN RAISE EXCEPTION 'в каталоге % дублирующихся названий', v_dupes; END IF;
    RAISE NOTICE 'дублей названий продуктов нет';
END $$;

-- Занятость: интервалы корректны и не выходят за границы
DO $$
DECLARE v_bad int;
BEGIN
    SELECT count(*) INTO v_bad FROM ops.booking WHERE isempty(period) OR lower(period) IS NULL;
    IF v_bad > 0 THEN RAISE EXCEPTION 'некорректных интервалов занятости: %', v_bad; END IF;
    RAISE NOTICE 'интервалы занятости валидны';
END $$;

-- Поиск: полнотекстовый канал работает даже без эмбеддинга
DO $$
DECLARE v_hits int;
BEGIN
    SELECT count(*) INTO v_hits FROM search.find_products(NULL, 'оценка запасов месторождения', 5);
    IF v_hits = 0 THEN RAISE EXCEPTION 'деградированный поиск ничего не нашёл'; END IF;
    RAISE NOTICE 'поиск без эмбеддинга: % результатов', v_hits;
END $$;

-- Поиск: результаты строго упорядочены по убыванию score
DO $$
DECLARE v_bad int;
BEGIN
    SELECT count(*) INTO v_bad FROM (
        SELECT score, lag(score) OVER (ORDER BY rank) AS prev
        FROM search.find_products(NULL, 'концепт обустройства', 5)) x
    WHERE prev IS NOT NULL AND score > prev;
    IF v_bad > 0 THEN RAISE EXCEPTION 'нарушен порядок ранжирования'; END IF;
    RAISE NOTICE 'порядок ранжирования корректен';
END $$;

-- Неактуальные позиции каталога не попадают в выдачу
DO $$
DECLARE v_bad int;
BEGIN
    SELECT count(*) INTO v_bad FROM search.find_products(NULL, 'сопровождение технических решений', 10)
    WHERE name ILIKE 'НЕАКТУАЛЬНО%';
    IF v_bad > 0 THEN RAISE EXCEPTION 'в выдачу попали неактуальные услуги'; END IF;
    RAISE NOTICE 'неактуальные услуги отфильтрованы';
END $$;

-- Этапы продукта: агрегация из истории отдаёт непустой результат
DO $$
DECLARE v_pid text; v_stages int;
BEGIN
    SELECT product_id INTO v_pid FROM analytics.product_stats ORDER BY calcs_cnt DESC LIMIT 1;
    SELECT count(*) INTO v_stages FROM catalog.product_stages(v_pid, 12);
    IF v_stages = 0 THEN RAISE EXCEPTION 'у самого частого продукта нет этапов'; END IF;
    RAISE NOTICE 'типовых этапов у топового продукта: %', v_stages;
END $$;

-- Подбор исполнителей: непустой, отсортированный, загрузка в пределах 0..100
DO $$
DECLARE v_pid text; v_cnt int; v_bad int;
BEGIN
    SELECT product_id INTO v_pid FROM analytics.product_stats ORDER BY calcs_cnt DESC LIMIT 1;

    SELECT count(*) INTO v_cnt FROM ops.find_executors(v_pid, '2026-09-01', '2026-11-30', true, 8);
    IF v_cnt = 0 THEN RAISE EXCEPTION 'исполнители не подобраны'; END IF;

    SELECT count(*) INTO v_bad FROM ops.find_executors(v_pid, '2026-09-01', '2026-11-30', true, 8)
    WHERE load_pct < 0 OR load_pct > 100 OR busy_days > period_days;
    IF v_bad > 0 THEN RAISE EXCEPTION 'некорректные метрики загрузки у % исполнителей', v_bad; END IF;

    RAISE NOTICE 'подобрано исполнителей: %, метрики в допустимых границах', v_cnt;
END $$;

-- Связанные услуги и аналогичные работы
DO $$
DECLARE v_pid text; v_related int; v_similar int;
BEGIN
    SELECT product_id INTO v_pid FROM analytics.product_stats ORDER BY calcs_cnt DESC LIMIT 1;
    SELECT count(*) INTO v_related FROM analytics.related_products(v_pid, 5);
    SELECT count(*) INTO v_similar FROM ops.similar_calcs(v_pid, 5);
    IF v_related = 0 THEN RAISE EXCEPTION 'нет связанных услуг'; END IF;
    IF v_similar = 0 THEN RAISE EXCEPTION 'нет аналогичных работ'; END IF;
    RAISE NOTICE 'связанных услуг %, аналогичных работ %', v_related, v_similar;
END $$;

-- Шаблоны ТЗ загружены и привязаны к продуктам
DO $$
DECLARE v_templates int; v_bound int;
BEGIN
    SELECT count(*) INTO v_templates FROM tz.template;
    SELECT count(*) INTO v_bound FROM tz.template WHERE array_length(product_ids, 1) > 0;
    IF v_templates < 5 THEN RAISE EXCEPTION 'шаблоны ТЗ не загружены'; END IF;
    IF v_bound = 0 THEN RAISE EXCEPTION 'ни один шаблон не привязан к продуктам'; END IF;
    RAISE NOTICE 'шаблонов ТЗ %, из них привязано к продуктам %', v_templates, v_bound;
END $$;

-- Веса полей готовности складываются в 100
DO $$
DECLARE v_sum int;
BEGIN
    SELECT sum((f->>'weight')::int) INTO v_sum
    FROM tz.template t, jsonb_array_elements(t.required_fields) f
    WHERE t.template_id = 'tpl-ptd';
    IF v_sum <> 100 THEN RAISE EXCEPTION 'сумма весов полей = %, ожидалось 100', v_sum; END IF;
    RAISE NOTICE 'веса полей готовности дают ровно 100%%';
END $$;

-- Согласование ТЗ: тред и направления держатся на одном корне цепочки версий
DO $$
DECLARE v_bad int; v_orphan int;
BEGIN
    -- root_tz_id направления обязан совпадать с корнем версии документа:
    -- иначе после правок по замечаниям обсуждение разъедется по версиям.
    SELECT count(*) INTO v_bad
    FROM tz.assignment a
    JOIN tz.document d ON d.tz_id = a.tz_id
    WHERE a.root_tz_id <> coalesce(d.parent_tz_id, d.tz_id);
    IF v_bad > 0 THEN
        RAISE EXCEPTION 'направлений с чужим корнем цепочки версий: %', v_bad;
    END IF;

    -- Замечание всегда лежит в том же треде, что и направление, по которому
    -- оно оставлено.
    SELECT count(*) INTO v_orphan
    FROM tz.comment c
    JOIN tz.assignment a ON a.assignment_id = c.assignment_id
    WHERE c.root_tz_id <> a.root_tz_id;
    IF v_orphan > 0 THEN
        RAISE EXCEPTION 'замечаний вне треда своего направления: %', v_orphan;
    END IF;

    RAISE NOTICE 'согласование: % направлений, % замечаний, треды сходятся',
        (SELECT count(*) FROM tz.assignment), (SELECT count(*) FROM tz.comment);
END $$;

-- Вердикт подрядчика всегда с причиной и всегда с решением
DO $$
DECLARE v_bad int;
BEGIN
    SELECT count(*) INTO v_bad FROM tz.comment
    WHERE kind = 'decision' AND (decision IS NULL OR btrim(text) = '');
    IF v_bad > 0 THEN RAISE EXCEPTION 'вердиктов без решения или без текста: %', v_bad; END IF;
    RAISE NOTICE 'все вердикты подрядчиков содержат решение и текст';
END $$;

\echo 'Все SQL-проверки пройдены'
