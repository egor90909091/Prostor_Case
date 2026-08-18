-- =====================================================================
--  Шаблоны ТЗ.
--  Структура секций взята из реальных docx-шаблонов датасета
--  («Выгрузка из системы/Шаблоны ТЗ»): все они сводятся к одному
--  каноническому набору разделов, различаясь набором обязательных.
-- =====================================================================

-- Базовый набор секций, общий для всех шаблонов
CREATE OR REPLACE FUNCTION tz.base_sections() RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
SELECT $json$[
  {"key":"purpose",       "title":"Цели и задачи работ",                                   "required":true,  "source":"field"},
  {"key":"abbreviations", "title":"Принятые сокращения",                                   "required":false, "source":"static"},
  {"key":"perimeter",     "title":"Периметр выполнения работ",                             "required":true,  "source":"field"},
  {"key":"schedule",      "title":"Сроки выполнения работ",                                "required":true,  "source":"period"},
  {"key":"kpi",           "title":"КПЭ по SMART",                                          "required":false, "source":"field"},
  {"key":"content",       "title":"Содержание работ, особенности их выполнения и результаты","required":true,"source":"stages"},
  {"key":"conditions",    "title":"Условия выполнения работы",                             "required":true,  "source":"field"},
  {"key":"documentation", "title":"Требования к документации",                             "required":true,  "source":"documentation"},
  {"key":"quality",       "title":"Контроль качества",                                     "required":false, "source":"field"},
  {"key":"subcontract",   "title":"Условия привлечения субподрядчиков",                    "required":false, "source":"executors"},
  {"key":"other",         "title":"Иные условия выполнения работ",                         "required":false, "source":"field"}
]$json$::jsonb;
$$;

-- Веса полей складываются в 100 — это и есть шкала «процент готовности ТЗ»
CREATE OR REPLACE FUNCTION tz.base_fields() RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
SELECT $json$[
  {"key":"customer",      "section":"purpose",       "title":"Заказчик",                      "weight":6,  "blocking":true,  "hint":"Полное наименование ДО-заказчика"},
  {"key":"object",        "section":"perimeter",     "title":"Объект работ",                  "weight":12, "blocking":true,  "hint":"Месторождение, лицензионный участок или площадь"},
  {"key":"purpose",       "section":"purpose",       "title":"Цели и задачи",                 "weight":12, "blocking":true,  "hint":"Что именно нужно получить по итогам работ"},
  {"key":"perimeter",     "section":"perimeter",     "title":"Периметр работ",                "weight":8,  "blocking":false, "hint":"Границы: пласты, залежи, скважины, объём выборки"},
  {"key":"period",        "section":"schedule",      "title":"Сроки выполнения",              "weight":10, "blocking":true,  "hint":"Дата начала и дата окончания работ"},
  {"key":"stages",        "section":"content",       "title":"Этапы работ",                   "weight":15, "blocking":true,  "hint":"Минимум один этап из состава продукта"},
  {"key":"operations",    "section":"content",       "title":"Операции",                      "weight":6,  "blocking":false, "hint":"Состав операций внутри этапов"},
  {"key":"executors",     "section":"subcontract",   "title":"Исполнители",                   "weight":10, "blocking":true,  "hint":"Одна или несколько компаний-исполнителей"},
  {"key":"source_data",   "section":"conditions",    "title":"Требования к исходным данным",  "weight":9,  "blocking":false, "hint":"Какие данные предоставляет заказчик и в каком виде"},
  {"key":"documentation", "section":"documentation", "title":"Требования к документации",     "weight":7,  "blocking":false, "hint":"Формат и состав отчётных материалов"},
  {"key":"acceptance",    "section":"quality",       "title":"Порядок приёмки",               "weight":5,  "blocking":false, "hint":"Как принимаются результаты и кем"}
]$json$::jsonb;
$$;

-- Правила рисков. `when` — маленький декларативный DSL, который разбирает TZ Generator:
--   empty(field)              — поле не заполнено
--   empty_list(field)         — список пуст
--   flag(name)               — установлен флаг условий работ
--   missing_stage(substr)     — среди выбранных этапов нет содержащего подстроку
--   duration_below_typical(k) — срок меньше типового по истории, умноженного на k
--   stages_without_docs       — есть этапы без требований к документации
CREATE OR REPLACE FUNCTION tz.base_risks() RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
SELECT $json$[
  {"code":"no_object",        "severity":"blocking","title":"Не указан объект работ",
   "recommendation":"Укажите месторождение или лицензионный участок — без него ТЗ не проходит согласование",
   "when":{"op":"empty","arg":"object"}},

  {"code":"no_purpose",       "severity":"blocking","title":"Не сформулированы цели и задачи",
   "recommendation":"Опишите ожидаемый результат работ в измеримой форме",
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

  {"code":"model3d_no_source","severity":"warning", "title":"Выбрана 3D-модель без этапа подготовки исходных данных",
   "recommendation":"Добавьте этап подготовки и контроля качества исходных данных перед построением 3D ГМ",
   "when":{"op":"and","args":[{"op":"flag","arg":"model3d"},{"op":"missing_stage","arg":"исходн"}]}},

  {"code":"no_source_data",   "severity":"warning", "title":"Не указаны требования к исходным материалам",
   "recommendation":"Перечислите данные, которые предоставляет заказчик, и требования к их формату",
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
   "recommendation":"Опишите условия привлечения субподрядчиков и границы их ответственности",
   "when":{"op":"and","args":[{"op":"flag","arg":"subcontract"},{"op":"empty","arg":"other"}]}}
]$json$::jsonb;
$$;

INSERT INTO tz.template (template_id, name, type_code, product_ids, sections, required_fields, risk_rules) VALUES
('tpl-ptd',     'ТЗ на проектно-технический документ (ПТД)',            'PTD',     '{}', tz.base_sections(), tz.base_fields(), tz.base_risks()),
('tpl-pz',      'ТЗ на подсчёт запасов / оперативное изменение запасов','PZ',      '{}', tz.base_sections(), tz.base_fields(), tz.base_risks()),
('tpl-concept', 'ТЗ на интегрированный концепт',                        'CONCEPT', '{}', tz.base_sections(), tz.base_fields(), tz.base_risks()),
('tpl-support', 'ТЗ на сопровождение инженерных работ',                 'SUPPORT', '{}', tz.base_sections(), tz.base_fields(), tz.base_risks()),
('tpl-generic', 'Универсальное ТЗ на оказание услуг',                   'GENERIC', '{}', tz.base_sections(), tz.base_fields(), tz.base_risks());
