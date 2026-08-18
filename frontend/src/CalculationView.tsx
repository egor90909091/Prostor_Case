import { Fragment, useEffect, useMemo, useState } from 'react'
import { getStages } from './api'

// ------------------------------------------------------------------ типы
interface StageRow {
  key: string
  name: string
  documentation?: string | null
  medianDays?: number | null
}

interface CalcLine {
  role: string
  unit: string
  qty: number
  rate: number
}

interface ChatMsg {
  id: string
  author: string
  role: 'author' | 'other' | 'self'
  text?: string
  attachment?: boolean
  time?: string
}

const VAT = 0.2

// Ставки — иллюстративные (в проекте пока нет справочника расценок по ролям),
// нужны только чтобы экран не пустовал. Заменить на реальный расчёт, когда
// появится тарификатор в бэкенде.
const ROLES: { role: string; unit: string; rate: number; share: number }[] = [
  { role: 'Геология и разработка L3', unit: 'чел/день', rate: 62300, share: 0.4 },
  { role: 'Лицензирование и недропользование L3', unit: 'чел/день', rate: 62600, share: 0.6 },
]

function buildLines(days: number): CalcLine[] {
  return ROLES.map((r) => ({
    role: r.role,
    unit: r.unit,
    qty: Math.max(1, Math.round(days * r.share * 10) / 10),
    rate: r.rate,
  }))
}

function money(n: number): string {
  return n.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

function addDays(base: Date, days: number): Date {
  const d = new Date(base)
  d.setDate(d.getDate() + days)
  return d
}

function fmtDate(d: Date): string {
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' })
}

const DEMO_MESSAGES: ChatMsg[] = [
  { id: 'm1', author: 'Кондрашова В.С., МП', role: 'author', text: 'Проверьте, пожалуйста, состав этапов 2 и 3 — там менялся объём.' },
  { id: 'm2', author: 'Кондрашова В.С., МП', role: 'author', text: 'Добавила комментарий по субподрядным работам.' },
  { id: 'm3', author: 'Манькова Я.В., Заказчик ГТМ-Актюба', role: 'other', text: 'Согласовано, можно закрывать расчёт.' },
  { id: 'm4', author: 'Кондрашова В.С., МП', role: 'self', attachment: true, time: '16.06.2026 в 09:02' },
]

// ------------------------------------------------------------------ экран
export function CalculationView({ sessionId }: { sessionId: string | null }) {
  const [productName, setProductName] = useState<string | null>(null)
  const [stages, setStages] = useState<StageRow[]>([])
  const [expanded, setExpanded] = useState<Record<string, boolean>>({})
  const [chatTab, setChatTab] = useState<'chat' | 'notes' | 'history'>('chat')
  const [draft, setDraft] = useState('')

  useEffect(() => {
    if (!sessionId) return
    fetch(`/api/v1/chat/sessions/${sessionId}/state`)
      .then((r) => (r.ok ? r.json() : null))
      .then((s) => {
        if (!s) return
        setProductName(s.productName ?? null)
        if (s.productId) {
          getStages(s.productId)
            .then((r) => setStages(r.items ?? []))
            .catch(() => undefined)
        }
      })
      .catch(() => undefined)
  }, [sessionId])

  // Без открытой сессии показываем демонстрационный состав, чтобы можно
  // было оценить оформление экрана без прохождения диалога.
  const rows = stages.length > 0
    ? stages
    : [
        { key: 'demo-1', name: 'Формирование базы данных. Обработка и интерпретация данных ГИС вновь пробуренных скважин', medianDays: 33 },
        { key: 'demo-2', name: 'Оперативный подсчёт геологических запасов. Оформление материалов оперативного изменения', medianDays: 46 },
        { key: 'demo-3', name: 'Согласование ОПЗ на НТС Заказчика. Подготовка пакета документов и передача отчёта на экспертизу', medianDays: 27 },
      ]

  useEffect(() => {
    setExpanded(Object.fromEntries(rows.map((r) => [r.key, true])))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stages.length])

  const groups = useMemo(() => {
    let cursor = new Date()
    return rows.map((stage) => {
      const days = stage.medianDays ?? 14
      const from = addDays(cursor, 3)
      const to = addDays(from, days)
      cursor = to
      const lines = buildLines(days)
      const subtotal = lines.reduce((sum, l) => sum + l.qty * l.rate, 0)
      return { stage, from, to, days, lines, subtotal }
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows])

  const grandTotal = groups.reduce((sum, g) => sum + g.subtotal, 0)
  const grandTotalVat = grandTotal * (1 + VAT)

  const toggle = (key: string) => setExpanded((prev) => ({ ...prev, [key]: !prev[key] }))

  return (
    <div className="calc-screen">
      <div className="calc-subheader">
        <div className="calc-crumbs">
          <span className="calc-brand">ПРОСТОР</span>
          <span className="calc-sep">›</span>
          <span>Управление проектами</span>
          <span className="calc-sep">›</span>
          <span>Расчёты</span>
        </div>
        <div className="calc-badge">{productName ?? 'Черновик расчёта'}</div>
      </div>

      <div className="calc-tabsbar">
        <div className="calc-tabs">
          <button className="calc-tab" disabled>Общая информация</button>
          <button className="calc-tab on">Расчёт стоимости</button>
          <button className="calc-tab" disabled>Наряд-заказ</button>
        </div>
        <div className="calc-tools">
          <button className="calc-btn ghost">Экспорт ▾</button>
          <button className="calc-btn ghost">Отклонить</button>
          <button className="calc-btn solid">Закрыть</button>
        </div>
      </div>

      <div className="calc-body">
        <div className="calc-table-wrap">
          <table className="calc-table">
            <thead>
              <tr>
                <th className="col-name">Наименование</th>
                <th>Перечень результатов / документация</th>
                <th>Период работ</th>
                <th className="num">Тр-сть, дн.</th>
                <th>Ед. изм.</th>
                <th className="num">Кол-во</th>
                <th className="num">Стоимость за ед.</th>
              </tr>
            </thead>
            <tbody>
              <tr className="calc-group-root">
                <td colSpan={7}>Проектно-технический документ</td>
              </tr>
              {groups.map((g, i) => (
                <Fragment key={g.stage.key}>
                  <tr className="calc-row-group">
                    <td className="col-name">
                      <button className="calc-caret" onClick={() => toggle(g.stage.key)}>
                        {expanded[g.stage.key] ? '⌄' : '›'}
                      </button>
                      {i + 1}. {g.stage.name}
                    </td>
                    <td className="muted">Информационный отчёт</td>
                    <td className="nowrap">{fmtDate(g.from)} – {fmtDate(g.to)}</td>
                    <td className="num">{g.days}</td>
                    <td />
                    <td />
                    <td className="num calc-subtotal">
                      {money(g.subtotal)}
                      <span className="calc-subtotal-vat">с НДС {money(g.subtotal * (1 + VAT))}</span>
                    </td>
                  </tr>
                  {expanded[g.stage.key] &&
                    g.lines.map((line, j) => (
                      <tr className="calc-row-line" key={`${g.stage.key}-${j}`}>
                        <td className="col-name indent">{line.role}</td>
                        <td />
                        <td />
                        <td />
                        <td>{line.unit}</td>
                        <td className="num">{line.qty.toFixed(2)}</td>
                        <td className="num">{money(line.rate)}</td>
                      </tr>
                    ))}
                  {expanded[g.stage.key] && (
                    <tr className="calc-row-line muted-row">
                      <td className="col-name indent">Субподрядные работы</td>
                      <td /><td /><td /><td /><td /><td />
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <td colSpan={4} className="calc-total-label">
                  ИТОГО ПО РАСЧЁТУ&nbsp;
                  <span className="muted">
                    Гарантированный объём без НДС: {money(grandTotal)} р, с НДС: {money(grandTotalVat)} р ·
                    Негарантированный объём без НДС: 0,00 р, с НДС: 0,00 р
                  </span>
                </td>
                <td colSpan={3} className="num calc-total-value">
                  {money(grandTotal)} / {money(grandTotalVat)}
                </td>
              </tr>
            </tfoot>
          </table>
        </div>

        <aside className="calc-chat">
          <div className="calc-chat-tabs">
            <button className={chatTab === 'chat' ? 'on' : ''} onClick={() => setChatTab('chat')}>Чат расчёта</button>
            <button className={chatTab === 'notes' ? 'on' : ''} onClick={() => setChatTab('notes')}>Замечания</button>
            <button className={chatTab === 'history' ? 'on' : ''} onClick={() => setChatTab('history')}>История</button>
          </div>

          {chatTab === 'chat' && (
            <>
              <div className="calc-chat-hint">Обсуждение расчёта — видно всем участникам заявки</div>
              <div className="calc-chat-stream">
                {DEMO_MESSAGES.map((m) => (
                  <div className={`calc-msg ${m.role}`} key={m.id}>
                    <span className={`calc-avatar ${m.role}`}>{m.author.slice(0, 1)}</span>
                    <div className="calc-msg-body">
                      <div className="calc-msg-author">{m.author}</div>
                      {m.attachment ? (
                        <div className="calc-msg-attachment">
                          <span>Файл расчёта.xlsx</span>
                          {m.time && <span className="calc-msg-time">{m.time}</span>}
                        </div>
                      ) : (
                        <div className="calc-msg-text">{m.text}</div>
                      )}
                    </div>
                  </div>
                ))}
              </div>
              <div className="calc-composer">
                <input
                  placeholder="Введите сообщение"
                  value={draft}
                  onChange={(e) => setDraft(e.target.value)}
                />
                <button className="calc-send" disabled={!draft.trim()} onClick={() => setDraft('')}>➤</button>
              </div>
            </>
          )}
          {chatTab === 'notes' && <div className="calc-chat-hint">Замечаний по расчёту пока нет.</div>}
          {chatTab === 'history' && <div className="calc-chat-hint">История изменений появится после первого согласования.</div>}
        </aside>
      </div>
    </div>
  )
}
