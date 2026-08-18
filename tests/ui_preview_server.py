#!/usr/bin/env python3
"""
Тестовый двойник бэкенда для проверки фронтенда без .NET.

Реализует те же контракты, что Chat Service и TZ Generator, и ходит в ту же
базу через те же SQL-функции — то есть проверяет связку «SQL -> API -> UI»
целиком. Логика готовности и рисков здесь повторяет tz.template из БД
(тот же DSL), чтобы убедиться, что правила в базе корректны и фронт их рисует.

Это инструмент разработки и демонстрации, а не часть продакшена: в рабочем
контуре эти эндпоинты обслуживают сервисы на C#.

  python3 tests/ui_preview_server.py --dsn "host=... dbname=prostor" --root frontend/dist
"""
import argparse
import datetime as dt
import json
import math
import re
import sys
import uuid
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from urllib.parse import urlparse

import psycopg2
import psycopg2.extras

DIM = 1536
SESSIONS: dict[str, dict] = {}
ARGS = None


# ----------------------------------------------------------------- эмбеддинг
def fnv1a(value: str) -> int:
    h = 2166136261
    for b in value.encode("utf-8"):
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h


def embed(text: str):
    vector = [0.0] * DIM
    normalized = re.sub(r"[^0-9a-zа-я]+", " ", (text or "").lower().replace("ё", "е")).strip()
    for word in normalized.split():
        if len(word) < 2:
            continue
        feats = [("w:" + word, 1.0)]
        padded = "_" + word + "_"
        feats += [("t:" + padded[i:i + 3], 0.35) for i in range(len(padded) - 2)]
        for feat, weight in feats:
            h = fnv1a(feat)
            vector[h % DIM] += (-1.0 if (h >> 31) & 1 else 1.0) * weight
    norm = math.sqrt(sum(x * x for x in vector))
    return [x / norm for x in vector] if norm else vector


def vec_literal(vector):
    return "[" + ",".join(f"{x:.6f}" for x in vector) + "]"


def query(sql, params=()):
    with psycopg2.connect(ARGS.dsn) as conn:
        with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
            cur.execute(sql, params)
            return [dict(r) for r in cur.fetchall()] if cur.description else []


def jsonable(value):
    if isinstance(value, (dt.date, dt.datetime)):
        return value.isoformat()
    if isinstance(value, uuid.UUID):
        return str(value)
    from decimal import Decimal
    if isinstance(value, Decimal):
        return float(value)
    raise TypeError(str(type(value)))


# --------------------------------------------------------------- блоки чата
def product_blocks(text):
    rows = query(
        "SELECT * FROM search.find_products(%s::vector, %s, 5)", (vec_literal(embed(text)), text))
    items = []
    for r in rows:
        reasons = []
        if r["lexical"] and float(r["lexical"]) > 0:
            reasons.append("совпадение по составу работ")
        if r["similarity"] and float(r["similarity"]) > 0.3:
            reasons.append("смысловая близость запросу")
        if r["calcs_cnt"]:
            reasons.append(f"выполнено работ: {r['calcs_cnt']}")
        if r["companies_cnt"]:
            reasons.append(f"исполнителей с опытом: {r['companies_cnt']}")
        if r["typical_days"]:
            reasons.append(f"типовой срок {r['typical_days']} дн.")
        items.append({
            "rank": r["rank"], "id": r["product_id"], "title": r["name"],
            "category": r["category"], "snippet": r["snippet"],
            "score": float(r["score"]), "similarity": float(r["similarity"] or 0),
            "templateId": r["template_id"], "calcsCnt": r["calcs_cnt"],
            "companiesCnt": r["companies_cnt"], "operationsCnt": r["operations_cnt"],
            "typicalDays": r["typical_days"], "reasons": reasons,
        })
    return rows, [{"type": "product_list", "selectMode": "single", "items": items}]


def stages_of(product_id, top=12):
    return query("SELECT * FROM catalog.product_stages(%s, %s)", (product_id, top))


def draft(state):
    template_id = state.get("templateId") or "tpl-generic"
    rows = query("SELECT * FROM tz.template WHERE template_id = %s", (template_id,))
    if not rows:
        rows = query("SELECT * FROM tz.template WHERE template_id = 'tpl-generic'")
    tpl = rows[0]

    typical = None
    if state.get("productId"):
        stats = query(
            "SELECT typical_duration_days d FROM analytics.product_stats WHERE product_id = %s",
            (state["productId"],))
        typical = stats[0]["d"] if stats else None

    def filled(key):
        if key == "period":
            return bool(state.get("period", {}).get("from") and state.get("period", {}).get("to"))
        if key == "stages":
            return bool(state.get("stages"))
        if key == "operations":
            return bool(state.get("operationIds"))
        if key == "executors":
            return bool(state.get("executors"))
        if key == "source_data":
            return bool((state.get("sourceData") or "").strip())
        return bool((state.get(key) or "").strip())

    fields = [{
        "key": f["key"], "section": f["section"], "title": f["title"],
        "weight": f["weight"], "blocking": f.get("blocking", False),
        "filled": filled(f["key"]), "hint": f.get("hint"),
    } for f in tpl["required_fields"]]
    readiness = sum(f["weight"] for f in fields if f["filled"])

    def evaluate(cond):
        op = cond.get("op")
        arg = cond.get("arg")
        if op == "and":
            return all(evaluate(a) for a in cond.get("args", []))
        if op == "or":
            return any(evaluate(a) for a in cond.get("args", []))
        if op == "not":
            return not evaluate(cond["args"][0])
        if op == "empty":
            return not filled(arg)
        if op == "empty_list":
            return not filled(arg)
        if op == "flag":
            return bool((state.get("flags") or {}).get(arg))
        if op == "missing_stage":
            stages = state.get("stages") or []
            if not stages:
                return True
            return not any(arg.lower() in (s.get("name") or "").lower() for s in stages)
        if op == "duration_below_typical":
            if not typical:
                return False
            period = state.get("period") or {}
            if not (period.get("from") and period.get("to")):
                return False
            days = (dt.date.fromisoformat(period["to"]) - dt.date.fromisoformat(period["from"])).days + 1
            return days < typical * float(arg or 0.8)
        if op == "stages_without_docs":
            stages = state.get("stages") or []
            return bool(stages) and any(not s.get("documentation") for s in stages)
        return False

    order = {"blocking": 0, "warning": 1, "info": 2}
    risks = sorted(
        [{"code": r["code"], "severity": r["severity"], "title": r["title"],
          "recommendation": r["recommendation"]}
         for r in tpl["risk_rules"] if evaluate(r["when"])],
        key=lambda r: order.get(r["severity"], 3))

    blocking = [r for r in risks if r["severity"] == "blocking"]
    warnings = [r for r in risks if r["severity"] == "warning"]
    if not blocking and not warnings:
        recommendation = "ТЗ готово к согласованию: обязательные разделы заполнены, критичных рисков нет."
    else:
        parts = []
        if blocking:
            parts.append("Перед согласованием необходимо устранить: "
                         + "; ".join(r["title"].lower() for r in blocking) + ".")
        if warnings:
            parts.append("Рекомендуется дополнительно: "
                         + "; ".join(r["recommendation"].rstrip(".").lower() for r in warnings) + ".")
        recommendation = " ".join(parts)

    sections = []
    for s in tpl["sections"]:
        body = None
        key = s["key"]
        if key == "purpose":
            body = state.get("purpose") or f"Выполнение работ по услуге «{state.get('productName') or '—'}»."
        elif key == "perimeter" and state.get("object"):
            body = f"Объект работ: {state['object']}. {state.get('perimeter') or ''}".strip()
        elif key == "schedule" and filled("period"):
            p = state["period"]
            days = (dt.date.fromisoformat(p["to"]) - dt.date.fromisoformat(p["from"])).days + 1
            body = (f"Начало работ: {p['from']}. Окончание работ: {p['to']}. "
                    f"Общая продолжительность: {days} календарных дней.")
        elif key == "content" and state.get("stages"):
            body = "\n".join(
                f"Этап {i}. {s2['name']}" + (f" Продолжительность: {s2['days']} дн." if s2.get("days") else "")
                for i, s2 in enumerate(state["stages"], start=1))
        elif key == "documentation":
            body = state.get("documentation") or "Результаты работ передаются информационными отчётами."
        elif key == "subcontract" and state.get("executors"):
            body = "Исполнители работ: " + ", ".join(e.get("name", "") for e in state["executors"]) + "."
        elif key == "abbreviations":
            body = "ТЗ — техническое задание; ДО — дочернее общество; ГМ — геологическая модель."
        else:
            body = state.get(key)
        sections.append({"key": key, "title": s["title"], "required": s.get("required", False),
                         "body": body, "filled": bool(body and str(body).strip())})

    return {
        "templateId": tpl["template_id"], "templateName": tpl["name"],
        "readiness": max(0, min(readiness, 100)), "canGenerate": not blocking,
        "recommendation": recommendation, "typicalDays": typical,
        "fields": fields, "risks": risks, "sections": sections,
    }


# ------------------------------------------------------------------- сервер
class Handler(SimpleHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def _send(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False, default=jsonable).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _sse(self, events):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        for name, data in events:
            frame = f"event: {name}\ndata: {json.dumps(data, ensure_ascii=False, default=jsonable)}\n\n"
            self.wfile.write(frame.encode())
            self.wfile.flush()

    # ------------------------------------------------------------- GET
    def do_GET(self):
        path = urlparse(self.path).path
        if not path.startswith("/api/"):
            return super().do_GET()

        m = re.match(r"^/api/v1/chat/sessions/([0-9a-f-]+)/state$", path)
        if m:
            return self._send(SESSIONS.get(m.group(1), {}))

        m = re.match(r"^/api/v1/catalog/products/([^/]+)/stages$", path)
        if m:
            return self._send({"items": [
                {"key": s["stage_key"], "name": s["name"], "usedCount": s["used_cnt"],
                 "medianDays": s["median_days"], "documentation": s["documentation"]}
                for s in stages_of(m.group(1), 20)]})

        if path == "/api/v1/tz/templates":
            return self._send({"items": [
                {"id": t["template_id"], "name": t["name"], "typeCode": t["type_code"]}
                for t in query("SELECT * FROM tz.template ORDER BY name")]})

        if path == "/api/v1/tz/documents":
            return self._send({"items": []})

        if path == "/api/v1/analytics/overview":
            return self._send(analytics())

        return self._send({"error": "not_found"}, 404)

    # ------------------------------------------------------------- POST
    def do_POST(self):
        path = urlparse(self.path).path
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length) or b"{}")

        if path == "/api/v1/chat/sessions":
            sid = str(uuid.uuid4())
            SESSIONS[sid] = {"step": "Idle", "flags": {}, "stages": [], "executors": [], "period": {}}
            return self._send({"sessionId": sid, "state": snapshot(SESSIONS[sid])}, 201)

        m = re.match(r"^/api/v1/chat/sessions/([0-9a-f-]+)/turns$", path)
        if m:
            return self._sse(turn(SESSIONS.setdefault(m.group(1), {}), body))

        if path == "/api/v1/catalog/products/search":
            rows, _ = product_blocks(body.get("query", ""))
            return self._send({"items": rows})

        if path in ("/api/v1/tz/drafts", "/internal/tz/draft"):
            return self._send(draft(body.get("state") or {}))

        if path == "/api/v1/tz/documents":
            return self._send({"tzId": str(uuid.uuid4()), "readiness": draft(body.get("state") or {})["readiness"],
                               "downloadUrl": "#", "stored": False}, 201)

        return self._send({"error": "not_found"}, 404)


def snapshot(state):
    missing = []
    if not state.get("productId"): missing.append("productId")
    if not (state.get("period") or {}).get("from"): missing.append("period")
    if not state.get("executors"): missing.append("executors")
    if not state.get("stages"): missing.append("stages")
    if not state.get("object"): missing.append("object")
    return {
        "step": state.get("step", "Idle"), "missing": missing,
        "productId": state.get("productId"), "productName": state.get("productName"),
        "templateId": state.get("templateId"), "period": state.get("period", {}),
        "stages": len(state.get("stages", [])), "executors": len(state.get("executors", [])),
    }


def turn(state, body):
    events = [("meta", {"turnId": str(uuid.uuid4())})]
    action = body.get("action")

    if not action:
        text = body.get("text", "")
        state["step"] = "ProductSearch"
        rows, blocks = product_blocks(text)
        top = rows[0] if rows else None
        message = (f"Нашёл {len(rows)} подходящих услуг. Наиболее близкая — «{top['name']}»"
                   f"{', по ней в системе ' + str(top['calcs_cnt']) + ' выполненных работ' if top and top['calcs_cnt'] else ''}"
                   ". Выберите подходящий вариант — дальше уточню сроки и подберу исполнителей."
                   ) if top else "По этому запросу ничего не нашлось."
        events.append(("delta", {"text": message}))
        for b in blocks:
            events.append(("block", {"block": b}))

    elif action["type"] == "select_product":
        pid = action["id"]
        card = query("""
            SELECT p.product_id, p.name, s.typical_duration_days d,
                   coalesce((SELECT t.template_id FROM tz.template t
                              WHERE p.product_id = ANY (t.product_ids) LIMIT 1), 'tpl-generic') tpl
            FROM catalog.product p LEFT JOIN analytics.product_stats s ON s.product_id = p.product_id
            WHERE p.product_id = %s""", (pid,))[0]
        state.update({"productId": pid, "productName": card["name"], "templateId": card["tpl"],
                      "typicalDays": card["d"], "step": "ProductPicked", "stages": [], "executors": []})

        similar = query("SELECT * FROM ops.similar_calcs(%s, 4)", (pid,))
        related = query("SELECT * FROM analytics.related_products(%s, 4)", (pid,))
        stages = stages_of(pid)
        ops_rows = query("SELECT * FROM catalog.operation WHERE product_id = %s ORDER BY order_num", (pid,))

        today = dt.date.today() + dt.timedelta(days=7)
        events.append(("delta", {"text": f"Выбрана услуга «{card['name']}». "
                                         f"По ней в системе {len(similar)}+ выполненных работ — ниже похожие. "
                                         "Укажите желаемые сроки — подберу исполнителей."}))
        events.append(("block", {"block": {
            "type": "period_request", "text": "Укажите сроки выполнения работ",
            "meta": {"suggestedFrom": today.isoformat(),
                     "suggestedTo": (today + dt.timedelta(days=card["d"] or 90)).isoformat(),
                     "typicalDays": card["d"]}}}))
        events.append(("block", {"block": {
            "type": "similar_calcs", "text": "Аналогичные выполненные работы",
            "items": [{"id": s["calc_id"], "title": s["calc_name"], "company": s["company_name"],
                       "days": s["duration_days"], "stages": s["stages_cnt"]} for s in similar]}}))
        events.append(("block", {"block": {
            "type": "related_products", "text": "Часто заказывают вместе",
            "items": [{"id": r["product_id"], "title": r["name"], "cnt": r["cnt"]} for r in related]}}))
        events.append(("block", {"block": {
            "type": "recommendations", "text": "Что понадобится для заявки",
            "items": [{"text": f"В истории устойчиво повторяются {len(stages)} этапов — "
                               "конструктор подставит их автоматически"},
                      {"text": f"В составе услуги {len(ops_rows)} операций; обязательные отмечены заранее"},
                      {"text": "Подготовьте название объекта работ — без него ТЗ не пройдёт проверку"}]}}))

    elif action["type"] == "set_period":
        state["period"] = {"from": action["from"], "to": action["to"]}
        state["step"] = "PeriodSet"
        rows = query("SELECT * FROM ops.find_executors(%s, %s, %s, true, 6)",
                     (state["productId"], action["from"], action["to"]))
        events.append(("delta", {"text": f"Нашёл {len(rows)} исполнителей на период "
                                         f"{action['from']} — {action['to']}. "
                                         "Список отсортирован по опыту, доступности и рейтингу."}))
        events.append(("block", {"block": {
            "type": "executor_list", "selectMode": "multi",
            "items": [{"rank": r["rank"], "id": r["company_id"], "name": r["name"],
                       "score": float(r["score"]), "rating": r["rating"], "experience": r["experience"],
                       "availability": r["availability"], "loadPct": r["load_pct"],
                       "subcontract": r["subcontract"], "reasons": r["reasons"]} for r in rows]}}))

    elif action["type"] == "select_executors":
        names = {r["company_id"]: r["name"] for r in query(
            "SELECT company_id, name FROM catalog.company WHERE company_id = ANY(%s)", (action["ids"],))}
        state["executors"] = [{"id": i, "name": names.get(i, i), "subcontract": False} for i in action["ids"]]
        state["step"] = "ExecutorsPicked"
        stages = stages_of(state["productId"])
        events.append(("delta", {"text": f"Исполнителей выбрано: {len(action['ids'])}. "
                                         "Теперь отметьте этапы работ — они попадут в раздел "
                                         "«Содержание работ» технического задания."}))
        events.append(("block", {"block": {
            "type": "stage_list", "selectMode": "multi", "text": "Этапы работ",
            "items": [{"id": s["stage_key"], "title": s["name"], "usedCnt": s["used_cnt"],
                       "medianDays": s["median_days"], "documentation": s["documentation"],
                       "preselected": s["used_cnt"] > 1} for s in stages]}}))
        events.append(("block", {"block": {
            "type": "conditions", "text": "Условия выполнения работ",
            "items": [{"key": "model3d", "title": "Требуется построение 3D геологической модели", "value": False},
                      {"key": "subcontract", "title": "Допускается привлечение субподряда", "value": False},
                      {"key": "urgent", "title": "Срочное выполнение", "value": False}]}}))

    elif action["type"] == "select_stages":
        chosen = set(action["ids"])
        state["stages"] = [{"key": s["stage_key"], "name": s["name"], "days": s["median_days"],
                            "documentation": s["documentation"]}
                           for s in stages_of(state["productId"]) if s["stage_key"] in chosen]
        state["step"] = "Review"
        d = draft(state)
        events.append(("delta", {"text": f"Этапов выбрано: {len(state['stages'])}."}))
        events.append(("block", {"block": {
            "type": "tz_gaps", "text": f"Готовность ТЗ: {d['readiness']}%", "items": d["risks"],
            "meta": {"readiness": d["readiness"], "canGenerate": d["canGenerate"],
                     "recommendation": d["recommendation"]}}}))
        events.append(("block", {"block": {
            "type": "actions",
            "items": [{"action": "open_constructor", "title": "Сформировать ТЗ в конструкторе"}]}}))

    elif action["type"] == "set_flag":
        state.setdefault("flags", {})[action["key"]] = action.get("flag", True)
        d = draft(state)
        events.append(("delta", {"text": "Условие учтено."}))
        events.append(("block", {"block": {
            "type": "tz_gaps", "text": f"Готовность ТЗ: {d['readiness']}%", "items": d["risks"],
            "meta": {"readiness": d["readiness"], "canGenerate": d["canGenerate"],
                     "recommendation": d["recommendation"]}}}))

    events.append(("state", snapshot(state)))
    events.append(("done", {}))
    return events


def analytics():
    rows = query("""
        SELECT json_build_object(
          'topSearchedProducts', (SELECT coalesce(json_agg(x),'[]'::json) FROM (
             SELECT p.name, count(*)::int AS cnt FROM analytics.search_log l
             JOIN catalog.product p ON p.product_id = l.top_product_id
             GROUP BY p.name ORDER BY cnt DESC LIMIT 8) x),
          'unrecognizedQueries', (SELECT coalesce(json_agg(x),'[]'::json) FROM (
             SELECT query, created_at FROM analytics.search_log WHERE NOT recognized
             ORDER BY created_at DESC LIMIT 10) x),
          'topPairs', (SELECT coalesce(json_agg(x),'[]'::json) FROM (
             SELECT a.name AS product, b.name AS related, c.cnt
             FROM analytics.product_cooccurrence c
             JOIN catalog.product a ON a.product_id = c.product_id
             JOIN catalog.product b ON b.product_id = c.related_product_id
             WHERE a.product_id < b.product_id ORDER BY c.cnt DESC LIMIT 6) x),
          'topExecutors', (SELECT coalesce(json_agg(x),'[]'::json) FROM (
             SELECT co.name, sum(s.calcs_cnt)::int AS works, count(DISTINCT s.product_id)::int AS products
             FROM analytics.company_product_stats s JOIN catalog.company co ON co.company_id = s.company_id
             GROUP BY co.name ORDER BY works DESC LIMIT 8) x),
          'tzCreated', (SELECT count(*)::int FROM tz.document),
          'tzAvgReadiness', (SELECT coalesce(round(avg(readiness))::int, 0) FROM tz.document),
          'tzByTemplate', '[]'::json,
          'topRisks', '[]'::json,
          'topStages', '[]'::json,
          'productizationCandidates', (SELECT coalesce(json_agg(x),'[]'::json) FROM (
             SELECT p.name, s.calcs_cnt, s.companies_cnt, s.typical_duration_days
             FROM analytics.product_stats s JOIN catalog.product p ON p.product_id = s.product_id
             WHERE s.calcs_cnt >= 10 ORDER BY s.calcs_cnt DESC LIMIT 8) x)
        ) AS data""")
    return rows[0]["data"]


def main():
    global ARGS
    parser = argparse.ArgumentParser()
    parser.add_argument("--dsn", required=True)
    parser.add_argument("--root", default="frontend/dist")
    parser.add_argument("--port", type=int, default=8090)
    ARGS = parser.parse_args()

    import functools
    handler = functools.partial(Handler, directory=ARGS.root)
    print(f"превью на http://127.0.0.1:{ARGS.port} (корень {ARGS.root})", file=sys.stderr)
    ThreadingHTTPServer(("127.0.0.1", ARGS.port), handler).serve_forever()


if __name__ == "__main__":
    main()
