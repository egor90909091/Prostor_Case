#!/usr/bin/env bash
# Сквозной прогон сценария по API: от свободного запроса до готового ТЗ.
# Запускать после `docker compose up -d` и появления healthy у базы.
#
#   ./tests/smoke.sh            # против локального compose
#   CHAT=http://host:8080 TZ=http://host:8081 ./tests/smoke.sh
set -euo pipefail

CHAT="${CHAT:-http://localhost:8080}"
TZ="${TZ:-http://localhost:8081}"

say() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
fail() { printf '\033[31mПРОВАЛ: %s\033[0m\n' "$1"; exit 1; }

command -v jq >/dev/null || fail "нужен jq"

say "health"
HEALTH=$(curl -sf "$CHAT/health") || fail "chat недоступен"
echo "$HEALTH" | jq -e '.status == "ok"' >/dev/null || fail "chat нездоров: $(echo "$HEALTH" | jq -r '.db // "?"')"
curl -sf "$TZ/health"   | jq -e '.status == "ok"' >/dev/null || fail "tz недоступен"
echo "$HEALTH" | jq -r '"llm=\(.llm)  embeddings=\(.embeddings)  router=\(.router)"'

# Индексатор заполняет только embedding IS NULL, поэтому смена модели
# эмбеддингов сама индекс не пересчитывает: старые векторы остаются
# несовместимыми с новыми запросами и поиск тихо выдаёт ерунду.
# Ловим это здесь, а не по странной выдаче в чате.
echo "$HEALTH" | jq -r '"индекс: \(.index.chunks - .index.missing)/\(.index.chunks) чанков"'
if ! echo "$HEALTH" | jq -e '.index.ready' >/dev/null; then
  printf '\033[33mВНИМАНИЕ: %s\033[0m\n' "$(echo "$HEALTH" | jq -r '.index.hint')"
fi
echo "оба сервиса отвечают"

say "поиск услуг (1 эмбеддинг + 1 SQL)"
SEARCH=$(curl -sf -X POST "$CHAT/api/v1/catalog/products/search" \
  -H 'Content-Type: application/json' \
  -d '{"query":"нужно оценить запасы по объекту","topK":5}')
echo "$SEARCH" | jq -r '.items[] | "  \(.rank). \(.name)  score=\(.score)"'
COUNT=$(echo "$SEARCH" | jq '.items | length')
[ "$COUNT" -gt 0 ] || fail "поиск ничего не вернул"
PRODUCT_ID=$(echo "$SEARCH" | jq -r '.items[0].productId')
TEMPLATE_ID=$(echo "$SEARCH" | jq -r '.items[0].templateId')

say "сессия чата"
SESSION=$(curl -sf -X POST "$CHAT/api/v1/chat/sessions" \
  -H 'Content-Type: application/json' -d '{"customerName":"Smoke"}' | jq -r '.sessionId')
[ -n "$SESSION" ] || fail "сессия не создана"
echo "sessionId = $SESSION"

turn() {
  curl -sN -X POST "$CHAT/api/v1/chat/sessions/$SESSION/turns" \
    -H 'Content-Type: application/json' -H 'Accept: text/event-stream' \
    -H "Idempotency-Key: smoke-$1" -d "$2"
}

say "ход 1: свободный текст (стрим SSE)"
turn 1 '{"text":"нужно оценить запасы по объекту"}' | grep -c '^event:' \
  | xargs -I{} echo "получено событий: {}"

say "ход 2: выбор услуги"
turn 2 "{\"action\":{\"type\":\"select_product\",\"id\":\"$PRODUCT_ID\"}}" >/dev/null
echo "услуга выбрана"

say "ход 3: сроки"
turn 3 '{"action":{"type":"set_period","from":"2026-09-01","to":"2026-11-30"}}' \
  | grep -o '"type":"executor_list"' | head -1 | xargs -I{} echo "пришёл блок {}"

say "подбор исполнителей напрямую"
EXEC=$(curl -sf -X POST "$CHAT/api/v1/executors/search" -H 'Content-Type: application/json' \
  -d "{\"productId\":\"$PRODUCT_ID\",\"from\":\"2026-09-01\",\"to\":\"2026-11-30\"}")
echo "$EXEC" | jq -r '.items[] | "  \(.rank). \(.name) score=\(.score) загрузка=\(.loadPct)%"'
COMPANY=$(echo "$EXEC" | jq -r '.items[0].companyId')

say "этапы услуги"
STAGES=$(curl -sf "$CHAT/api/v1/catalog/products/$PRODUCT_ID/stages?top=5")
echo "$STAGES" | jq -r '.items[] | "  · \(.name[0:70]) (\(.medianDays // 0) дн.)"'

say "черновик ТЗ: пустое состояние -> должны быть критичные риски"
EMPTY=$(curl -sf -X POST "$TZ/api/v1/tz/drafts" -H 'Content-Type: application/json' \
  -d "{\"templateId\":\"$TEMPLATE_ID\",\"state\":{}}")
echo "$EMPTY" | jq -r '"готовность: \(.readiness)%  критичных: \([.risks[]|select(.severity=="blocking")]|length)"'
echo "$EMPTY" | jq -e '.canGenerate == false' >/dev/null || fail "пустое ТЗ не должно генерироваться"

say "черновик ТЗ: заполненное состояние"
STATE=$(jq -nc --arg p "$PRODUCT_ID" --arg t "$TEMPLATE_ID" --arg c "$COMPANY" \
  --argjson stages "$(echo "$STAGES" | jq '[.items[] | {key:.key, name:.name, days:.medianDays, documentation:.documentation}]')" '
{
  productId:$p, productName:"Услуга из смоука", templateId:$t,
  customer:"ООО «Демо-ДО»", object:"Восточно-Мыгинское месторождение",
  purpose:"Уточнение геологического строения и оценка запасов",
  perimeter:"Продуктивные пласты в границах лицензионного участка",
  sourceData:"Материалы ГИС, керн, результаты испытаний",
  acceptance:"Приёмка по этапам на основании информационных отчётов",
  period:{from:"2026-09-01", to:"2027-08-25"},
  stages:$stages, operationIds:[], executors:[{id:$c, name:"Исполнитель", subcontract:false}],
  flags:{model3d:false}
}')
FULL=$(curl -sf -X POST "$TZ/api/v1/tz/drafts" -H 'Content-Type: application/json' \
  -d "{\"templateId\":\"$TEMPLATE_ID\",\"state\":$STATE}")
echo "$FULL" | jq -r '"готовность: \(.readiness)%  можно выгружать: \(.canGenerate)"'
echo "$FULL" | jq -r '.risks[] | "  [\(.severity)] \(.title)"'

say "риск «3D-модель без подготовки исходных данных»"
STATE3D=$(echo "$STATE" | jq '.flags.model3d = true')
curl -sf -X POST "$TZ/api/v1/tz/drafts" -H 'Content-Type: application/json' \
  -d "{\"templateId\":\"$TEMPLATE_ID\",\"state\":$STATE3D}" \
  | jq -e '[.risks[].code] | index("model3d_no_source")' >/dev/null \
  && echo "риск корректно сработал" || fail "риск 3D не сработал"

say "выгрузка документа"
DOC=$(curl -sf -X POST "$TZ/api/v1/tz/documents" -H 'Content-Type: application/json' \
  -d "{\"sessionId\":\"$SESSION\",\"templateId\":\"$TEMPLATE_ID\",\"state\":$STATE}")
TZ_ID=$(echo "$DOC" | jq -r '.tzId')
echo "$DOC" | jq -r '"tzId=\(.tzId) готовность=\(.readiness)% сохранён в S3: \(.stored)"'

curl -sf "$TZ/api/v1/tz/documents/$TZ_ID/file" -o /tmp/smoke_tz.docx
SIZE=$(stat -c%s /tmp/smoke_tz.docx)
[ "$SIZE" -gt 1500 ] || fail "docx подозрительно мал ($SIZE байт)"
head -c 2 /tmp/smoke_tz.docx | grep -q 'PK' || fail "docx не является zip-архивом"
echo "документ скачан: $SIZE байт, /tmp/smoke_tz.docx"

say "аналитика"
curl -sf "$CHAT/api/v1/analytics/overview" | jq -r '"создано ТЗ: \(.tzCreated), средняя готовность: \(.tzAvgReadiness)%"'

say "роутер: свободный текст либо консультация, либо поиск услуг"
LLM_KIND=$(curl -sf "$CHAT/health" | jq -r '.llm')
echo "LLM: $LLM_KIND"

SESSION2=$(curl -sf -X POST "$CHAT/api/v1/chat/sessions" \
  -H 'Content-Type: application/json' -d '{"customerName":"SmokeRouter"}' | jq -r '.sessionId')
[ -n "$SESSION2" ] || fail "сессия для роутера не создана"

turn2() {
  curl -sN -X POST "$CHAT/api/v1/chat/sessions/$SESSION2/turns" \
    -H 'Content-Type: application/json' -H 'Accept: text/event-stream' \
    -H "Idempotency-Key: smoke-router-$1" -d "$2"
}

# Предметный запрос — это то же сообщение, что уже надёжно возвращает результаты
# в начале скрипта. Роутер (или его заглушка) должен передать управление поиску:
# ждём блок product_list. Это единственная жёсткая проверка в блоке — формулировка
# однозначна, и системный промпт роутера прямо требует вызвать search_services
# именно на таком сообщении.
SEARCH_RAW=$(turn2 search '{"text":"нужно оценить запасы по объекту"}')
echo "$SEARCH_RAW" | grep -q '"type":"product_list"' \
  || fail "предметная реплика не привела к поиску услуг (нет блока product_list)"
echo "предметная реплика -> получен product_list"

# Консультационную реплику мягко проверяем только против реальной модели: заглушка
# LLM не умеет вести диалог и намеренно всегда выбирает поиск (см. StubLlm.DecideAsync
# и docs/architecture.md §2.1) — на ней эта проверка ничего не говорит о роутере.
# Против настоящей модели решение — это её суждение, а не детерминированный код,
# поэтому расхождение здесь предупреждение, а не fail(): не хотим ронять CI из-за
# того, что модель в отдельном случае восприняла реплику иначе, чем ожидалось.
if [ "$LLM_KIND" != "stub" ]; then
  CONSULT_RAW=$(turn2 consult '{"text":"расскажи, как вообще устроен процесс подбора услуги — с чего лучше начать?"}')
  if echo "$CONSULT_RAW" | grep -q '"type":"product_list"'; then
    echo "предупреждение: консультационная реплика привела к поиску услуг — роутер решил иначе, чем ожидалось"
  else
    echo "консультационная реплика -> ответ текстом без поиска"
  fi
else
  echo "LLM — заглушка: проверку консультации пропускаем, заглушка всегда выбирает поиск"
fi

say "качество поиска: доказательства и уверенность"
# Регрессия на реальный провал: «я хочу найти закзачика который пробурит скажвину»
# возвращал пять юридических услуг с одинаковым баллом и обоснованием
# «смысловая близость запросу» у каждой. Требования к выдаче теперь такие:
#  1) точный запрос про бурение находит услугу про скважины и с доказательствами;
#  2) шумная выдача не выдаёт себя за находку.
DRILL=$(curl -sf -X POST "$CHAT/api/v1/catalog/products/search" \
  -H 'Content-Type: application/json' \
  -d '{"query":"строительство скважин","topK":5}')
echo "$DRILL" | jq -r '.items[] | "  \(.rank). \(.name[0:52])  score=\(.score)  слова=\(.matchedTerms|join(","))"'
echo "$DRILL" | jq -e '.items[0].matchedTerms | length > 0' >/dev/null \
  || fail "у лучшего результата нет ни одного совпавшего слова — доказательств нет"
echo "$DRILL" | jq -e '[.items[0].name] | .[0] | ascii_downcase | test("скважин")' >/dev/null \
  || fail "запрос про скважины не вывел наверх услугу про скважины"
echo "точный запрос: доказательства есть, наверху профильная услуга"

# Услуги-дубликаты не должны занимать выдачу одинаковым баллом: если у первых
# трёх результатов балл совпадает до третьего знака — ранжирование ничего не
# различило, и это ровно тот симптом, с которого начался разбор.
JUNK=$(curl -sf -X POST "$CHAT/api/v1/catalog/products/search" \
  -H 'Content-Type: application/json' \
  -d '{"query":"я хочу найти закзачика который пробурит скажвину","topK":5}')
echo "$JUNK" | jq -r '.items[] | "  \(.rank). \(.name[0:52])  score=\(.score)  слова=\(.matchedTerms|join(","))"'
FLAT=$(echo "$JUNK" | jq '[.items[0:3][].score | .*1000 | floor] | unique | length')
if [ "${FLAT:-0}" -le 1 ] && [ "$(echo "$JUNK" | jq '.items|length')" -ge 3 ]; then
  fail "первые три результата имеют одинаковый балл — выдача не различает услуги"
fi
echo "плоской выдачи из дубликатов нет"

say "поиск исполнителей по способностям (без выбранной услуги)"
DISC=$(curl -sf -X POST "$CHAT/api/v1/executors/discover" \
  -H 'Content-Type: application/json' \
  -d '{"query":"кто может пробурить скважину","topK":5}')
echo "$DISC" | jq -r '.items[] | "  \(.rank). \(.name)  score=\(.score)  работ=\(.calcsCnt)  слова=\(.matchedTerms|join(","))"'
echo "$DISC" | jq -e '.items | length > 0' >/dev/null || fail "исполнители по способностям не находятся"

printf '\n\033[32mВсе проверки пройдены\033[0m\n'
