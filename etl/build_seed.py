#!/usr/bin/env python3
"""
ETL: выгрузка из ПРОСТОР (xlsx) -> db/init/03_seed.sql

Читает файлы датасета «Выгрузка из системы» и собирает один SQL-файл
с COPY-блоками. Ничего не додумывает: все идентификаторы, названия,
даты и тексты берутся из выгрузки как есть.

  python3 etl/build_seed.py --src "<путь к папке Выгрузка из системы>" \
                            --out db/init/03_seed.sql
"""
import argparse
import os
import re
import sys
from collections import Counter, defaultdict

try:
    import openpyxl
except ImportError:
    sys.exit("нужен openpyxl:  pip install openpyxl")

FILES = {
    "companies": "0. Компании.xlsx",
    "contracts": "1. Договоры.xlsx",
    "calcs":     "2. Договор + РС.xlsx",
    "cprod":     "3. Договор + продукты.xlsx",
    "prices":    "4. Продукты + расценки.xlsx",
    "ops":       "5. Продукты + Операции.xlsx",
}

NULLS = {None, "", "NULL", "null", "None"}


def load(path):
    wb = openpyxl.load_workbook(path, read_only=True, data_only=True)
    ws = wb.worksheets[0]
    it = ws.iter_rows(values_only=True)
    header = [h for h in next(it) if h is not None]
    rows = []
    for r in it:
        r = list(r) + [None] * (len(header) - len(r))
        row = dict(zip(header, r[: len(header)]))
        if all(v in NULLS for v in row.values()):
            continue
        rows.append(row)
    wb.close()
    return rows


def clean(v):
    """Значение ячейки -> строка или None."""
    if v in NULLS:
        return None
    s = str(v).strip()
    return s or None


def date(v):
    s = clean(v)
    if not s:
        return None
    s = s[:10]
    return s if re.fullmatch(r"\d{4}-\d{2}-\d{2}", s) else None


def esc(v):
    """Экранирование для текстового формата COPY."""
    if v is None:
        return r"\N"
    return (
        str(v)
        .replace("\\", "\\\\")
        .replace("\t", "\\t")
        .replace("\n", "\\n")
        .replace("\r", "")
    )


CATEGORIES = [
    ("Геология и запасы",        ("геолог", "запас", "сейсм", "керн", "пластов")),
    ("Проектная документация",   ("проектно-техническ", "проектные техническ", "птд", "документац")),
    ("Концепты и обустройство",  ("концепт", "обустройств", "реинжиниринг", "заканчиван")),
    ("Экономика и стоимость",    ("стоимост", "бизнес", "налог", "закуп", "ценообраз", "акселерац")),
    ("Инжиниринг и сопровождение", ("сопровожден", "инженерн", "высокориск", "оператив")),
    ("Данные и ИТ",              ("данн", "пир", "инновацион", "цифров")),
    ("Экспертиза и компетенции", ("экспертиз", "ашуранс", "компетенц", "кадров", "техническая политика", "ндт")),
]


def categorize(name):
    low = (name or "").lower()
    for cat, keys in CATEGORIES:
        if any(k in low for k in keys):
            return cat
    return "Прочие услуги"


TEMPLATE_RULES = [
    ("tpl-pz",      ("запас", "подсчет", "подсчёт", "геолог", "сейсм")),
    ("tpl-ptd",     ("проектно-техническ", "проектные техническ", "техническая политика", "документац")),
    ("tpl-concept", ("концепт", "обустройств", "реинжиниринг", "заканчиван", "развит")),
    ("tpl-support", ("сопровожден", "инженерн", "высокориск", "оператив")),
]


def template_for(name):
    low = (name or "").lower()
    for tpl, keys in TEMPLATE_RULES:
        if any(k in low for k in keys):
            return tpl
    return "tpl-generic"


def copy_block(out, table, columns, rows):
    out.append(f"COPY {table} ({', '.join(columns)}) FROM stdin;")
    for r in rows:
        out.append("\t".join(esc(c) for c in r))
    out.append("\\.")
    out.append("")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True, help="папка «Выгрузка из системы»")
    ap.add_argument("--out", default="db/init/03_seed.sql")
    args = ap.parse_args()

    src = args.src
    data = {k: load(os.path.join(src, f)) for k, f in FILES.items()}
    print("прочитано:", {k: len(v) for k, v in data.items()})

    # ---------------------------------------------------------- компании
    companies = {}
    for r in data["companies"]:
        cid = clean(r.get("company_id"))
        if not cid:
            continue
        rating = clean(r.get("rating"))
        companies[cid] = (
            cid,
            clean(r.get("name")) or cid,
            clean(r.get("name")) or cid,
            clean(r.get("info")),
            clean(r.get("services")),
            int(float(rating)) if rating else 3,
        )

    # ---------------------------------------------------------- продукты
    #
    # В выгрузке один и тот же продукт встречается под разными product_id
    # (в разных файлах). Схлопываем по нормализованному названию: канонический
    # id — тот, что встречается в расчётах (там больше всего смысловых связей),
    # остальные id остаются алиасами и переписываются во всех ссылках.
    raw_names = {}
    for key in ("calcs", "cprod", "ops", "prices"):
        for r in data[key]:
            pid = clean(r.get("product_id"))
            nm = clean(r.get("product_name"))
            if pid and nm and pid not in raw_names:
                raw_names[pid] = nm

    weight = Counter()
    for key, w in (("calcs", 1000), ("cprod", 100), ("ops", 10), ("prices", 1)):
        for r in data[key]:
            pid = clean(r.get("product_id"))
            if pid in raw_names:
                weight[pid] += w

    def norm_name(s):
        return re.sub(r"\s+", " ", (s or "").strip().lower().replace("ё", "е"))

    canon_by_name = {}
    for pid, nm in raw_names.items():
        k = norm_name(nm)
        cur = canon_by_name.get(k)
        if cur is None or weight[pid] > weight[cur]:
            canon_by_name[k] = pid
    alias = {pid: canon_by_name[norm_name(nm)] for pid, nm in raw_names.items()}
    names = {canon_by_name[k]: raw_names[canon_by_name[k]] for k in canon_by_name}
    print(f"продуктов в выгрузке {len(raw_names)}, после схлопывания дублей {len(names)}")

    for key in ("calcs", "cprod", "ops", "prices"):
        for r in data[key]:
            pid = clean(r.get("product_id"))
            if pid in alias:
                r["product_id"] = alias[pid]

    # операции продукта
    operations = []
    ops_by_product = defaultdict(list)
    order = Counter()
    for r in data["ops"]:
        oid, pid = clean(r.get("operation_id")), clean(r.get("product_id"))
        nm = clean(r.get("operation_name"))
        if not (oid and pid and nm) or pid not in names:
            continue
        order[pid] += 1
        # операции с номером «01)» в начале считаем обязательными: это установочные шаги
        required = bool(re.match(r"^0?1[.)]", nm))
        operations.append((oid, pid, nm, order[pid], required))
        ops_by_product[pid].append(nm)

    # расценки
    prices = []
    for r in data["prices"]:
        pid = clean(r.get("product_id"))
        prid = clean(r.get("price_id"))
        if not (pid and prid) or pid not in names:
            continue
        prices.append((prid, pid, clean(r.get("price_name")),
                       clean(r.get("measurement_name")), clean(r.get("measurement_type"))))
    prices = list({p[0]: p for p in prices}.values())

    # ---------------------------------------------------------- договоры
    contracts = {}
    for r in data["contracts"]:
        cid = clean(r.get("contract_id"))
        if not cid:
            continue
        contracts[cid] = (cid, clean(r.get("contract_number")), clean(r.get("company_id")))

    contract_products = {}
    for r in data["cprod"]:
        cid, pid = clean(r.get("contract_id")), clean(r.get("product_id"))
        if not (cid and pid) or pid not in names:
            continue
        contract_products[(cid, pid)] = (cid, pid, clean(r.get("company_id")))
        contracts.setdefault(cid, (cid, None, clean(r.get("company_id"))))

    # ------------------------------------------------- расчёты и этапы
    calcs, stages = {}, {}
    stage_docs = defaultdict(list)
    stage_names = defaultdict(list)
    for r in data["calcs"]:
        calc_id = clean(r.get("calc_id"))
        pid = clean(r.get("product_id"))
        if not calc_id or pid not in names:
            continue
        cid = clean(r.get("contract_id"))
        if cid:
            contracts.setdefault(cid, (cid, None, clean(r.get("company_id"))))
        calcs.setdefault(calc_id, (
            calc_id, cid, clean(r.get("company_id")), pid,
            clean(r.get("calc_name")),
            date(r.get("calc_start_date")), date(r.get("calc_end_date")),
        ))
        sid = clean(r.get("stage_id"))
        if not sid:
            continue
        nm = clean(r.get("stage_name"))
        if not nm:
            continue
        onum = clean(r.get("stage_order_num"))
        docs = clean(r.get("stage_documentation_list"))
        stages[sid] = (
            sid, calc_id, clean(r.get("parent_stage_id")), nm,
            date(r.get("stage_start_date")), date(r.get("stage_end_date")),
            int(float(onum)) if onum and onum.replace(".", "").isdigit() else 0,
            docs,
        )
        stage_names[pid].append(nm)
        if docs:
            stage_docs[pid].append(docs)

    # ------------------------------------------------------- описания
    products = []
    for pid, nm in sorted(names.items(), key=lambda kv: kv[1]):
        bits = []
        top_stages = [s for s, _ in Counter(stage_names[pid]).most_common(4)]
        if top_stages:
            bits.append("Типовые этапы: " + "; ".join(t[:180] for t in top_stages))
        top_ops = ops_by_product[pid][:6]
        if top_ops:
            bits.append("Операции: " + "; ".join(o[:120] for o in top_ops))
        top_docs = [d for d, _ in Counter(stage_docs[pid]).most_common(2)]
        if top_docs:
            bits.append("Результаты: " + "; ".join(top_docs))
        # в выгрузке встречаются позиции с пометкой НЕАКТУАЛЬНО — они остаются
        # в каталоге ради ссылочной целостности истории, но из поиска исключены
        active = "f" if nm.upper().startswith("НЕАКТУАЛЬНО") else "t"
        products.append((pid, nm, " ".join(bits) or None, categorize(nm), active))

    # ------------------------------------------- чанки для поиска
    chunks = []
    for pid, nm, descr, cat, _active in products:
        chunks.append((pid, "name", f"{nm}. {cat}"))
        for s, _ in Counter(stage_names[pid]).most_common(6):
            chunks.append((pid, "stage", s[:600]))
        for o in ops_by_product[pid][:10]:
            chunks.append((pid, "operation", o[:600]))
        for d, _ in Counter(stage_docs[pid]).most_common(3):
            chunks.append((pid, "doc", d[:400]))

    company_chunks = [
        (cid, " ".join(x for x in (c[1], c[4], (c[3] or "")[:600]) if x))
        for cid, c in companies.items()
    ]

    # ----------------------------------------------- сборка SQL
    out = [
        "-- сгенерировано etl/build_seed.py, вручную не править",
        "SET client_encoding = 'UTF8';",
        "BEGIN;",
        "",
    ]
    copy_block(out, "catalog.company",
               ["company_id", "code", "name", "info", "services_text", "rating"],
               companies.values())
    copy_block(out, "catalog.product",
               ["product_id", "name", "description", "category", "is_active"], products)
    copy_block(out, "catalog.operation",
               ["operation_id", "product_id", "name", "order_num", "is_required"],
               [(o[0], o[1], o[2], o[3], "t" if o[4] else "f") for o in operations])
    copy_block(out, "catalog.price",
               ["price_id", "product_id", "price_name", "measurement_name", "measurement_type"],
               prices)
    copy_block(out, "ops.contract",
               ["contract_id", "contract_number", "company_id"], contracts.values())
    copy_block(out, "ops.contract_product",
               ["contract_id", "product_id", "company_id"], contract_products.values())
    copy_block(out, "ops.calc",
               ["calc_id", "contract_id", "company_id", "product_id", "name", "start_date", "end_date"],
               calcs.values())
    copy_block(out, "ops.stage",
               ["stage_id", "calc_id", "parent_stage_id", "name", "start_date", "end_date",
                "order_num", "documentation_list"],
               stages.values())
    copy_block(out, "search.product_chunk", ["product_id", "chunk_type", "chunk_text"], chunks)
    copy_block(out, "search.company_chunk", ["company_id", "chunk_text"], company_chunks)

    # привязка шаблонов ТЗ к продуктам
    by_tpl = defaultdict(list)
    for pid, nm, _, _, _ in products:
        by_tpl[template_for(nm)].append(pid)
    out.append("-- привязка шаблонов ТЗ к продуктам по типу работ")
    for tpl, pids in by_tpl.items():
        arr = ",".join(f"'{p}'" for p in pids)
        out.append(f"UPDATE tz.template SET product_ids = ARRAY[{arr}]::text[] "
                   f"WHERE template_id = '{tpl}';")
    out.append("")
    out.append("REFRESH MATERIALIZED VIEW ops.booking;")
    out.append("REFRESH MATERIALIZED VIEW analytics.product_stats;")
    out.append("REFRESH MATERIALIZED VIEW analytics.product_cooccurrence;")
    out.append("REFRESH MATERIALIZED VIEW analytics.company_product_stats;")
    out.append("COMMIT;")
    out.append("")

    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        f.write("\n".join(out))

    print(f"записано {args.out}")
    print(f"  компаний {len(companies)}, продуктов {len(products)}, операций {len(operations)},")
    print(f"  расценок {len(prices)}, договоров {len(contracts)}, расчётов {len(calcs)},")
    print(f"  этапов {len(stages)}, чанков {len(chunks)}")


if __name__ == "__main__":
    main()
