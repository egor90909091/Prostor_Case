-- =====================================================================
--  Классификация шаблонов ТЗ по видам работ.
--
--  Источник: 11 реальных .docx из «Шаблоны ТЗ» (Приложения к Заказам/
--  Договорам) + эталонная бланк-форма «Приложение № 2.1 Форма
--  Технического задания» — она и есть канонический список разделов,
--  который уже лежит в tz.base_sections() (db/init/02_templates.sql).
--
--  Проблема, которую чинит этот файл: все 5 строк tz.template были
--  созданы с ОДИНАКОВЫМ tz.base_fields()/base_risks() и с product_ids='{}'.
--  Разные названия шаблонов не были подкреплены ни разными полями, ни
--  связью с catalog.product — конструктор показывал одну и ту же форму
--  независимо от выбранной услуги, а search.find_products не мог
--  подставить template_id ни для одной услуги и всегда откатывался на
--  'tpl-generic' (см. coalesce(template_id, 'tpl-generic') в 04_functions.sql).
--
--  Что реально отличается между документами (см. анализ разделов ниже):
--    * ПТД/ПЗ/ОПЗ — самый тяжёлый шаблон: обязательные «Принятые
--      сокращения», регуляторные контрольные сроки (НТС/ГКЗ/ЦКР —
--      раздел «Ключевые показатели» встречается в примерах Приложения
--      №2.1 и Приложения 1 ПЗ), детальные форматы сдачи (DOC/DOCX+PDF,
--      XLS/XLSX, векторный PDF, реестры для гос. фондов).
--    * Концепт геологии/сейсморазведки — периметр = целевые горизонты/
--      интервалы, содержание = этапы обработки и интерпретации,
--      исходные данные = объём сейсмики и скважин, документация =
--      SEG-Y/ASCII + Petrel/tNavigator.
--    * Интегрированные концепты и концепты обустройства — обязательные
--      «Термины и сокращения», периметр = границы систем (от устья
--      скважин до точки сдачи), субподряд — с явным ограничением доли
--      (см. «Концепт обустройства»: не более 50%), приёмка — через
--      коллегиальные органы (УК/ИК/НТС).
--    * Сопровождение инженерных работ — самый лёгкий шаблон: НЕТ
--      разделов «Периметр работ» и «Иные условия» ни в одном из
--      просмотренных файлов, объект/месторождение не всегда есть
--      (методические/процессные работы), субподряд типично не
--      планируется, исходные данные не выделены отдельным разделом.
--
--  Как накатить на уже поднятую БД (init-скрипты не перевыполняются на
--  существующем volume — см. комментарий в 04_functions.sql):
--    docker compose exec -T db psql -U prostor -d prostor -v ON_ERROR_STOP=1 \
--      -f /docker-entrypoint-initdb.d/05_template_classification.sql
-- =====================================================================

-- --------------------------------------------------------------- ПТД/ПЗ/ОПЗ
-- Проектно-технический документ и подсчёт/пересчёт запасов в этом
-- датасете — по сути один документ (см. «Приложение 3. ТЗ ПТД_ОПЗ»,
-- где тема сформулирована как «Проектно-технический документ /
-- Оперативный подсчет запасов УВС» одной строкой), поэтому оба шаблона
-- используют один и тот же набор полей и рисков.
UPDATE tz.template SET
    sections = tz.base_sections(),
    required_fields = $json$[
      {"key":"customer",      "section":"purpose",     "title":"Заказчик",                     "weight":6,  "blocking":true,  "hint":"Полное наименование ДО-заказчика"},
      {"key":"object",        "section":"perimeter",   "title":"Объект работ",                 "weight":12, "blocking":true,  "hint":"Месторождение или лицензионный участок, по которому готовится ПТД/ПЗ"},
      {"key":"purpose",       "section":"purpose",     "title":"Цели и задачи",                "weight":12, "blocking":true,  "hint":"Реализация лицензионных обязательств, актуализация запасов, защита в ГКЗ/ЦКР и т.п."},
      {"key":"perimeter",     "section":"perimeter",   "title":"Периметр работ",                "weight":6,  "blocking":false, "hint":"Пласты, залежи, разбуриваемые участки, объём выборки"},
      {"key":"period",        "section":"schedule",    "title":"Сроки выполнения",             "weight":10, "blocking":true,  "hint":"Дата начала и дата окончания работ"},
      {"key":"kpi",           "section":"kpi",         "title":"Контрольные сроки (КПЭ)",       "weight":4,  "blocking":false, "hint":"Прохождение НТС ДО/ОРД, экспертизы ГКЗ/ЦКР Роснедр по УВС, постановка на баланс — с датами"},
      {"key":"stages",        "section":"content",     "title":"Этапы работ",                  "weight":15, "blocking":true,  "hint":"Минимум один этап из состава продукта"},
      {"key":"operations",    "section":"content",     "title":"Операции",                     "weight":5,  "blocking":false, "hint":"Состав операций внутри этапов"},
      {"key":"executors",     "section":"subcontract", "title":"Исполнители",                  "weight":10, "blocking":true,  "hint":"Одна или несколько компаний-исполнителей"},
      {"key":"source_data",   "section":"conditions",  "title":"Требования к исходным данным",  "weight":8,  "blocking":false, "hint":"Данные ГИС, керна, сейсморазведки, действующая геологическая модель и т.п., предоставляемые заказчиком"},
      {"key":"documentation", "section":"documentation","title":"Требования к документации",    "weight":8,  "blocking":false, "hint":"Текст — DOC/DOCX и скан с подписями (PDF), таблицы — XLS/XLSX, графика — векторный PDF, состав комплекта для гос. фондов"},
      {"key":"acceptance",    "section":"quality",     "title":"Порядок приёмки",               "weight":4,  "blocking":false, "hint":"Акт сдачи-приёмки по каждому этапу"}
    ]$json$::jsonb,
    risk_rules = (
        tz.base_risks() || $json$[
          {"code":"no_kpi_dates","severity":"info","title":"Не указаны контрольные сроки согласования",
           "recommendation":"Укажите сроки прохождения НТС, ГКЗ/ЦКР Роснедр по УВС — без них раздел «КПЭ» останется пустым",
           "when":{"op":"empty","arg":"kpi"}}
        ]$json$::jsonb
    ),
    product_ids = ARRAY(
        SELECT product_id FROM catalog.product
        WHERE name IN ('Проектно-технический документ', 'Проектные технические решения')
          AND is_active
    )
WHERE template_id = 'tpl-ptd';

-- tpl-pz: то же содержимое, что и tpl-ptd (совместный документ по факту),
-- но БЕЗ product_ids — в каталоге нет отдельной услуги «подсчёт запасов»,
-- она всегда идёт в связке с «Проектно-технический документ». Строка
-- остаётся в списке шаблонов для ручного выбора в конструкторе, когда
-- заказ оформляется именно на ПЗ/ОПЗ вне каталожной услуги.
UPDATE tz.template SET
    sections         = src.sections,
    required_fields  = src.required_fields,
    risk_rules       = src.risk_rules,
    product_ids      = '{}'
FROM tz.template src
WHERE src.template_id = 'tpl-ptd' AND tz.template.template_id = 'tpl-pz';

-- --------------------------------------------------------------- Концепт геологии
-- Периметр = целевые горизонты/интервалы, содержание = этапы обработки
-- и интерпретации сейсморазведки, исходные данные = объём сейсмики и
-- скважинного материала, документация = SEG-Y/ASCII + Petrel/tNavigator.
-- Разделы — канонические (тот же base_sections), риски — тоже базовые:
-- правило model3d_no_source точно попадает в этот тип работ.
INSERT INTO tz.template (template_id, name, type_code, product_ids, sections, required_fields, risk_rules)
VALUES (
    'tpl-geology',
    'ТЗ на концепт геологии / сейсморазведочные работы',
    'GEOLOGY',
    ARRAY(SELECT product_id FROM catalog.product WHERE name = 'Концепт геологии' AND is_active),
    tz.base_sections(),
    $json$[
      {"key":"customer",      "section":"purpose",     "title":"Заказчик",                     "weight":6,  "blocking":true,  "hint":"Полное наименование ДО-заказчика"},
      {"key":"object",        "section":"perimeter",   "title":"Объект работ",                 "weight":10, "blocking":true,  "hint":"Месторождение/лицензионный участок и целевые горизонты"},
      {"key":"purpose",       "section":"purpose",     "title":"Цели и задачи",                "weight":12, "blocking":true,  "hint":"Геологические/геофизические задачи: уточнение строения, корреляции, коллекторских свойств, прогноз трещиноватости и т.п."},
      {"key":"perimeter",     "section":"perimeter",   "title":"Периметр работ",                "weight":10, "blocking":false, "hint":"Целевые интервалы, отражающие горизонты, границы площади сейсморазведки/интерпретации"},
      {"key":"period",        "section":"schedule",    "title":"Сроки выполнения",             "weight":10, "blocking":true,  "hint":"Дата начала и дата окончания работ"},
      {"key":"stages",        "section":"content",     "title":"Этапы работ",                  "weight":16, "blocking":true,  "hint":"Препроцессинг, сигнальная обработка, миграция, структурная и динамическая интерпретация и т.п."},
      {"key":"operations",    "section":"content",     "title":"Операции",                     "weight":5,  "blocking":false, "hint":"Состав операций внутри этапов"},
      {"key":"executors",     "section":"subcontract", "title":"Исполнители",                  "weight":10, "blocking":true,  "hint":"Одна или несколько компаний-исполнителей"},
      {"key":"source_data",   "section":"conditions",  "title":"Требования к исходным данным",  "weight":12, "blocking":false, "hint":"Объём и формат исходных данных: сейсморазведка (км²/кв. км), скважинные данные (ГИС, керн, ВСП), действующие модели"},
      {"key":"documentation", "section":"documentation","title":"Требования к документации",    "weight":6,  "blocking":false, "hint":"Кубы и атрибуты — SEG-Y/ASCII, отчёт — MS Word, модели — Petrel/tNavigator"},
      {"key":"acceptance",    "section":"quality",     "title":"Порядок приёмки",               "weight":3,  "blocking":false, "hint":"Акт сдачи-приёмки по каждому этапу"}
    ]$json$::jsonb,
    tz.base_risks()
)
ON CONFLICT (template_id) DO UPDATE SET
    name = EXCLUDED.name, type_code = EXCLUDED.type_code, product_ids = EXCLUDED.product_ids,
    sections = EXCLUDED.sections, required_fields = EXCLUDED.required_fields, risk_rules = EXCLUDED.risk_rules;

-- --------------------------------------------------------------- Концепты и обустройство
-- Интегрированные концепты (развитие/заканчивание) и концепты
-- обустройства/реинжиниринг: обязательные «Термины и сокращения»,
-- периметр = границы систем, приёмка через коллегиальные органы,
-- субподряд — с ограничением доли. Правило model3d_no_source сюда не
-- переносится: 3D-модель — специфика геологии, а не инженерного концепта.
UPDATE tz.template SET
    sections = tz.base_sections(),
    required_fields = $json$[
      {"key":"customer",      "section":"purpose",     "title":"Заказчик",                     "weight":6,  "blocking":true,  "hint":"Полное наименование ДО-заказчика"},
      {"key":"object",        "section":"perimeter",   "title":"Объект работ",                 "weight":10, "blocking":true,  "hint":"Месторождение/актив, по которому разрабатывается концепт"},
      {"key":"purpose",       "section":"purpose",     "title":"Цели и задачи",                "weight":14, "blocking":true,  "hint":"Что актуализируется/прорабатывается: сценарии, стоимость, схема обустройства, заканчивание и т.п."},
      {"key":"perimeter",     "section":"perimeter",   "title":"Периметр работ",                "weight":10, "blocking":false, "hint":"Границы рассматриваемых систем (например «от устья скважин до точки сдачи»), перечень направлений и систем"},
      {"key":"period",        "section":"schedule",    "title":"Сроки выполнения",             "weight":10, "blocking":true,  "hint":"Дата начала и дата окончания работ"},
      {"key":"stages",        "section":"content",     "title":"Этапы работ",                  "weight":16, "blocking":true,  "hint":"Актуализация технических решений, стоимостной инжиниринг, сопровождение защиты в коллегиальных органах"},
      {"key":"operations",    "section":"content",     "title":"Операции",                     "weight":4,  "blocking":false, "hint":"Состав операций внутри этапов"},
      {"key":"executors",     "section":"subcontract", "title":"Исполнители",                  "weight":10, "blocking":true,  "hint":"Одна или несколько компаний-исполнителей"},
      {"key":"source_data",   "section":"conditions",  "title":"Требования к исходным данным",  "weight":6,  "blocking":false, "hint":"Действующая концепция/модели, объекты-аналоги, применяемые методические документы (НМД)"},
      {"key":"documentation", "section":"documentation","title":"Требования к документации",    "weight":6,  "blocking":false, "hint":"Информационный отчёт и презентация по каждому этапу — PDF/PPTX"},
      {"key":"acceptance",    "section":"quality",     "title":"Порядок приёмки",               "weight":8,  "blocking":false, "hint":"Защита на коллегиальных органах (УК, ИК, НТС), согласование с Проектным офисом"}
    ]$json$::jsonb,
    risk_rules = $json$[
      {"code":"no_object",        "severity":"blocking","title":"Не указан объект работ",
       "recommendation":"Укажите месторождение или актив — без него ТЗ не проходит согласование",
       "when":{"op":"empty","arg":"object"}},
      {"code":"no_purpose",       "severity":"blocking","title":"Не сформулированы цели и задачи",
       "recommendation":"Опишите ожидаемый результат концепта в измеримой форме",
       "when":{"op":"empty","arg":"purpose"}},
      {"code":"no_period",        "severity":"blocking","title":"Не указаны сроки выполнения",
       "recommendation":"Задайте дату начала и дату окончания работ",
       "when":{"op":"empty","arg":"period"}},
      {"code":"no_stages",        "severity":"blocking","title":"Не выбран ни один этап работ",
       "recommendation":"Выберите этапы из состава продукта — из них собирается раздел «Содержание работ»",
       "when":{"op":"empty_list","arg":"stages"}},
      {"code":"no_executors",     "severity":"blocking","title":"Не выбран исполнитель",
       "recommendation":"Подберите исполнителя с подтверждённым опытом по данному продукту",
       "when":{"op":"empty_list","arg":"executors"}},
      {"code":"no_source_data",   "severity":"warning", "title":"Не указаны требования к исходным материалам",
       "recommendation":"Перечислите действующие материалы и НМД, на основании которых готовится концепт",
       "when":{"op":"empty","arg":"source_data"}},
      {"code":"short_duration",   "severity":"warning", "title":"Заявленный срок ниже типового",
       "recommendation":"Сравните срок с историей: по этому продукту типовая длительность больше заявленной",
       "when":{"op":"duration_below_typical","arg":"0.8"}},
      {"code":"stages_no_docs",   "severity":"warning", "title":"Есть этапы без требований к документации",
       "recommendation":"Для каждого этапа укажите состав отчётных материалов — иначе приёмка будет спорной",
       "when":{"op":"stages_without_docs"}},
      {"code":"no_acceptance",    "severity":"info",    "title":"Не описан порядок приёмки",
       "recommendation":"Добавьте раздел контроля качества: кто и по каким критериям принимает результат",
       "when":{"op":"empty","arg":"acceptance"}},
      {"code":"subcontract_terms","severity":"warning", "title":"Привлекается субподряд без описания условий",
       "recommendation":"Опишите условия привлечения субподрядчиков, включая предельную долю их участия (типично до 50% для концептов обустройства)",
       "when":{"op":"and","args":[{"op":"flag","arg":"subcontract"},{"op":"empty","arg":"other"}]}}
    ]$json$::jsonb,
    product_ids = ARRAY(
        SELECT product_id FROM catalog.product
        WHERE name IN (
            'Интегрированный концепт развития',
            'Интегрированный концепт заканчивания',
            'Концепт обустройства',
            'Реинжиниринг обустройства',
            'Сопровождение технических решений наземного обустройства'
        )
          AND is_active
    )
WHERE template_id = 'tpl-concept';

-- --------------------------------------------------------------- Сопровождение инженерных работ
-- Самый лёгкий шаблон датасета: ни в одном из просмотренных файлов нет
-- разделов «Периметр работ» и «Иные условия», исходные данные не
-- выделены отдельным разделом (услуги оказываются без выезда, привязки
-- к конкретному месторождению часто нет), субподряд типично не
-- планируется. Раздел «Условия оказания услуг» тоже помечен
-- необязательным — заполнить его нечем без источника данных, показывать
-- его как обязательный и вечно пустой не нужно.
UPDATE tz.template SET
    sections = $json$[
      {"key":"purpose",       "title":"Цели и задачи работ",                                    "required":true,  "source":"field"},
      {"key":"abbreviations", "title":"Принятые сокращения",                                    "required":false, "source":"static"},
      {"key":"perimeter",     "title":"Периметр выполнения работ",                              "required":false, "source":"field"},
      {"key":"schedule",      "title":"Сроки выполнения работ",                                 "required":true,  "source":"period"},
      {"key":"kpi",           "title":"КПЭ по SMART",                                           "required":false, "source":"field"},
      {"key":"content",       "title":"Содержание работ, особенности их выполнения и результаты","required":true,"source":"stages"},
      {"key":"conditions",    "title":"Условия выполнения работы",                              "required":false, "source":"field"},
      {"key":"documentation", "title":"Требования к документации",                              "required":true,  "source":"documentation"},
      {"key":"quality",       "title":"Контроль качества",                                      "required":false, "source":"field"},
      {"key":"subcontract",   "title":"Условия привлечения субподрядчиков",                     "required":false, "source":"executors"},
      {"key":"other",         "title":"Иные условия выполнения работ",                          "required":false, "source":"field"}
    ]$json$::jsonb,
    required_fields = $json$[
      {"key":"customer",      "section":"purpose",     "title":"Заказчик",                     "weight":8,  "blocking":true,  "hint":"Полное наименование ДО-заказчика"},
      {"key":"object",        "section":"perimeter",   "title":"Объект/актив работ",            "weight":6,  "blocking":false, "hint":"Месторождение или актив, если работы привязаны к нему — необязательно для методических/процессных работ"},
      {"key":"purpose",       "section":"purpose",     "title":"Цели и задачи",                 "weight":18, "blocking":true,  "hint":"Что нужно повысить, разработать или внедрить по итогам сопровождения"},
      {"key":"period",        "section":"schedule",    "title":"Сроки выполнения",              "weight":12, "blocking":true,  "hint":"Дата начала и дата окончания работ"},
      {"key":"stages",        "section":"content",     "title":"Этапы работ",                   "weight":20, "blocking":true,  "hint":"Этапы сопровождения и ожидаемые результаты по каждому"},
      {"key":"operations",    "section":"content",     "title":"Операции",                      "weight":6,  "blocking":false, "hint":"Состав операций внутри этапов"},
      {"key":"executors",     "section":"subcontract", "title":"Исполнители",                   "weight":12, "blocking":true,  "hint":"Одна или несколько компаний-исполнителей"},
      {"key":"documentation", "section":"documentation","title":"Требования к документации",    "weight":10, "blocking":false, "hint":"Формат информационного отчёта по этапу — обычно PDF, количество экземпляров"},
      {"key":"acceptance",    "section":"quality",     "title":"Порядок приёмки",               "weight":8,  "blocking":false, "hint":"Акт сдачи-приёмки, промежуточные формы контроля по согласованию сторон"}
    ]$json$::jsonb,
    risk_rules = $json$[
      {"code":"no_object",        "severity":"warning", "title":"Не указан объект/актив работ",
       "recommendation":"Если работы привязаны к конкретному месторождению или активу — укажите его",
       "when":{"op":"empty","arg":"object"}},
      {"code":"no_purpose",       "severity":"blocking","title":"Не сформулированы цели и задачи",
       "recommendation":"Опишите ожидаемый результат сопровождения в измеримой форме",
       "when":{"op":"empty","arg":"purpose"}},
      {"code":"no_period",        "severity":"blocking","title":"Не указаны сроки выполнения",
       "recommendation":"Задайте дату начала и дату окончания работ",
       "when":{"op":"empty","arg":"period"}},
      {"code":"no_stages",        "severity":"blocking","title":"Не выбран ни один этап работ",
       "recommendation":"Выберите этапы из состава продукта — из них собирается раздел «Содержание работ»",
       "when":{"op":"empty_list","arg":"stages"}},
      {"code":"no_executors",     "severity":"blocking","title":"Не выбран исполнитель",
       "recommendation":"Подберите исполнителя с подтверждённым опытом по данному продукту",
       "when":{"op":"empty_list","arg":"executors"}},
      {"code":"short_duration",   "severity":"warning", "title":"Заявленный срок ниже типового",
       "recommendation":"Сравните срок с историей: по этому продукту типовая длительность больше заявленной",
       "when":{"op":"duration_below_typical","arg":"0.8"}},
      {"code":"stages_no_docs",   "severity":"warning", "title":"Есть этапы без требований к документации",
       "recommendation":"Для каждого этапа укажите состав отчётных материалов — иначе приёмка будет спорной",
       "when":{"op":"stages_without_docs"}},
      {"code":"no_acceptance",    "severity":"info",    "title":"Не описан порядок приёмки",
       "recommendation":"Добавьте раздел контроля качества: кто и по каким критериям принимает результат",
       "when":{"op":"empty","arg":"acceptance"}},
      {"code":"subcontract_terms","severity":"warning", "title":"Привлекается субподряд без описания условий",
       "recommendation":"Опишите условия привлечения субподрядчиков и границы их ответственности",
       "when":{"op":"and","args":[{"op":"flag","arg":"subcontract"},{"op":"empty","arg":"other"}]}}
    ]$json$::jsonb,
    product_ids = ARRAY(
        SELECT product_id FROM catalog.product
        WHERE name IN (
            'Сопровождение инженерных работ и высокорисковых операций',
            'Проектирование и сопровождение строительства скважин',
            'Оперативная поддержка Активов',
            'Экспертная поддержка и сопровождение процесса перехода на принципы НДТ ДО БРД'
        )
          AND is_active
    )
WHERE template_id = 'tpl-support';

-- --------------------------------------------------------------- Универсальный (fallback)
-- tpl-generic намеренно остаётся на tz.base_sections()/base_fields()/
-- base_risks(): это калька с пустой бланк-формы «Приложение № 2.1» и
-- единственный шаблон, к которому search.find_products откатывается
-- через coalesce(template_id, 'tpl-generic') для ~2/3 каталога («Прочие
-- услуги», «Данные и ИТ», «Экономика и стоимость», «Экспертиза и
-- компетенции» и часть «Геология и запасы»), где в датасете нет ни
-- одного примера реального ТЗ — придумывать для них специфичные поля
-- значило бы гадать, а не анализировать паттерн.
--
-- product_ids для tpl-generic уже были заданы в 03_seed.sql (31 услуга).
-- Переразметка tpl-ptd/tpl-pz/tpl-concept/tpl-support выше на точные
-- совпадения по названию услуги забрала часть услуг из их старых
-- (местами случайных — например, «Техническая политика» была в
-- tpl-ptd, а «Развитие компетенций и кадрового резерва» — в
-- tpl-concept) списков product_ids. Явно возвращаем их в tpl-generic,
-- чтобы каждая активная услуга была явно закреплена хотя бы за одним
-- шаблоном, а не только неявно через coalence-фолбэк в поиске.
UPDATE tz.template SET product_ids = ARRAY(
    SELECT DISTINCT unnest(product_ids || ARRAY[
        '01160b31325f0', -- Геолого-экономическая оценка
        '063aa50f9cd4a', -- Экспертиза данных для целей аудита запасов SEC/PRMS
        '0dfd6e32bf994', -- Развитие внешнего инновационного окружения (ВУЗы)
        '0ea8b16fd71ff', -- Развитие системы ПИР
        '09f64ab2ac6fe', -- Развитие компетенций и кадрового резерва
        '094421a84b8aa'  -- Техническая политика
    ]::text[])
) WHERE template_id = 'tpl-generic';
