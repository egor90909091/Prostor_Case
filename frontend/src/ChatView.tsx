import { useEffect, useRef, useState } from 'react'
import { createSession, getSession, sendTurn } from './api'
import type { Block, ChatStateSnapshot, TurnAction } from './api'
import { BlockView } from './Blocks'

interface Message {
  role: 'user' | 'assistant'
  blocks: Block[]
}

const EXAMPLES = [
  'Нужно оценить запасы по объекту',
  'Требуется концепт обустройства месторождения',
  'Сопровождение высокорисковых операций при бурении',
  'Исследование керна и пластовых флюидов',
]

// В localStorage хранится только указатель на сессию, а не сама история —
// история целиком лежит в chat.session/chat.message в Postgres (см.
// GET /api/v1/chat/sessions/{id}). Дублировать её в localStorage означало бы
// второй источник истины, который может разойтись с базой.
const SESSION_STORAGE_KEY = 'prostor.chat.sessionId'

const WELCOME_MESSAGE: Message = {
  role: 'assistant',
  blocks: [
    {
      type: 'text',
      text:
        'Здравствуйте. Опишите своими словами, какие работы нужны — ' +
        'подберу услугу из каталога ПРОСТОР, покажу похожие выполненные заявки, ' +
        'предложу исполнителей и соберу черновик ТЗ.',
    },
  ],
}

export function ChatView({ onOpenConstructor }: { onOpenConstructor: (sessionId: string) => void }) {
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [state, setState] = useState<ChatStateSnapshot | null>(null)
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const bottom = useRef<HTMLDivElement>(null)

  function startNewSession() {
    return createSession('Демо-заказчик')
      .then((s) => {
        localStorage.setItem(SESSION_STORAGE_KEY, s.sessionId)
        setSessionId(s.sessionId)
        setState(s.state)
        setMessages([WELCOME_MESSAGE])
      })
      .catch(() => setError('Бэкенд недоступен. Проверьте, что сервисы запущены.'))
  }

  useEffect(() => {
    const stored = localStorage.getItem(SESSION_STORAGE_KEY)
    if (!stored) {
      void startNewSession()
      return
    }

    // Указатель на сессию есть — восстанавливаем историю из БД, а не заводим
    // новую сессию. Так и переключение вкладок, и перезагрузка страницы, и
    // возврат в браузер позже не теряют диалог.
    getSession(stored)
      .then((detail) => {
        setSessionId(detail.sessionId)
        setState(detail.state)
        setMessages(
          detail.messages.length > 0
            ? detail.messages.map((m) => ({ role: m.role, blocks: m.blocks }))
            : [WELCOME_MESSAGE],
        )
      })
      .catch(() => {
        // Сессии в базе больше нет (например, после docker compose down -v) —
        // localStorage хранил только указатель, реальные данные были в Postgres,
        // так что просто заводим новую сессию и перезаписываем указатель.
        localStorage.removeItem(SESSION_STORAGE_KEY)
        void startNewSession()
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])

  async function run(body: { text?: string; action?: TurnAction }, userLabel: string) {
    if (!sessionId || busy) return
    setBusy(true)
    setError(null)
    setMessages((prev) => [...prev, { role: 'user', blocks: [{ type: 'text', text: userLabel }] }])

    let assistant: Message = { role: 'assistant', blocks: [] }
    setMessages((prev) => [...prev, assistant])

    const patch = (mutate: (m: Message) => void) =>
      setMessages((prev) => {
        const copy = [...prev]
        const last = { ...copy[copy.length - 1], blocks: [...copy[copy.length - 1].blocks] }
        mutate(last)
        copy[copy.length - 1] = last
        return copy
      })

    await sendTurn(sessionId, body, {
      onDelta: (text) =>
        patch((m) => {
          const first = m.blocks[0]
          if (first && first.type === 'text' && !first.meta?.fixed) {
            m.blocks[0] = { ...first, text: (first.text ?? '') + text }
          } else {
            m.blocks.unshift({ type: 'text', text })
          }
        }),
      onBlock: (block) => patch((m) => m.blocks.push(block)),
      onState: (s) => setState(s),
      onError: (message) => setError(message),
    })

    setBusy(false)
  }

  const submit = () => {
    const text = input.trim()
    if (!text) return
    setInput('')
    void run({ text }, text)
  }

  const last = messages[messages.length - 1]
  const streaming = busy && last?.role === 'assistant' && last.blocks.length > 0
  const thinking = busy && (!last || last.role !== 'assistant' || last.blocks.length === 0)

  return (
    <div className="chat">
      <div className="chat-main">
        {error && <div className="banner error">{error}</div>}

        <div className="stream">
          {messages.map((message, index) => {
            const isLast = index === messages.length - 1
            const cls = `msg ${message.role}${isLast && streaming ? ' streaming' : ''}`
            return (
              <div className={cls} key={index}>
                {message.blocks.map((block, i) => (
                  <BlockView
                    key={i}
                    block={block}
                    disabled={busy}
                    onAction={(action) => void run({ action }, actionLabel(action))}
                    onOpenConstructor={() => sessionId && onOpenConstructor(sessionId)}
                  />
                ))}
              </div>
            )
          })}
          {thinking && (
            <div className="msg assistant">
              <div className="thinking" role="status" aria-live="polite">
                <span className="dots"><span /><span /><span /></span>
                <span className="thinking-label">агент анализирует запрос…</span>
              </div>
            </div>
          )}
          <div ref={bottom} />
        </div>

        <div className="composer">
          <input
            value={input}
            placeholder="Опишите нужные работы своими словами"
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && submit()}
            disabled={busy || !sessionId}
          />
          <button className="btn primary" onClick={submit} disabled={busy || !input.trim()}>
            Отправить
          </button>
        </div>

        <div className="examples">
          {EXAMPLES.map((example) => (
            <button key={example} className="chip" disabled={busy} onClick={() => void run({ text: example }, example)}>
              {example}
            </button>
          ))}
        </div>
      </div>

      <aside className="side">
        <h3>Состояние заявки</h3>
        <Slot label="Услуга" value={state?.productName} />
        <Slot
          label="Сроки"
          value={state?.period?.from ? `${state.period.from} — ${state.period.to}` : undefined}
        />
        <Slot label="Исполнители" value={state?.executors ? `${state.executors}` : undefined} />
        <Slot label="Этапы" value={state?.stages ? `${state.stages}` : undefined} />
        <div className="step">Шаг: {state?.step ? stepLabel(state.step) : '—'}</div>
        {state?.missing?.length ? (
          <div className="missing">
            <div className="muted small">не хватает:</div>
            {state.missing.map((m) => (
              <span className="tag warn" key={m}>{fieldLabel(m)}</span>
            ))}
          </div>
        ) : null}

        <button
          className="btn primary wide"
          disabled={!sessionId || !state?.productId}
          onClick={() => sessionId && onOpenConstructor(sessionId)}
        >
          Сформировать ТЗ
        </button>
        <p className="muted small">
          Данные диалога переносятся в конструктор: тип ТЗ, этапы, сроки и исполнители
          будут предзаполнены.
        </p>
      </aside>
    </div>
  )
}

function Slot({ label, value }: { label: string; value?: string }) {
  return (
    <div className={`slot ${value ? 'filled' : ''}`}>
      <span className="slot-label">{label}</span>
      <span className="slot-value">{value ?? '—'}</span>
    </div>
  )
}

// Соответствует backend/src/Prostor.Chat/Domain.cs — static class Step.
const STEP_LABELS: Record<string, string> = {
  Idle: 'Ожидание запроса',
  ProductSearch: 'Подбор услуги',
  ProductPicked: 'Услуга выбрана',
  PeriodSet: 'Сроки согласованы',
  ExecutorsPicked: 'Исполнители выбраны',
  StagesPicked: 'Этапы подтверждены',
  Review: 'Проверка данных',
  TzReady: 'ТЗ готово',
}

function stepLabel(step: string): string {
  return STEP_LABELS[step] ?? step
}

function fieldLabel(key: string): string {
  const map: Record<string, string> = {
    productId: 'услуга',
    period: 'сроки',
    executors: 'исполнители',
    stages: 'этапы',
    object: 'объект работ',
  }
  return map[key] ?? key
}

function actionLabel(action: TurnAction): string {
  switch (action.type) {
    case 'select_product':
      return 'Выбираю эту услугу'
    case 'set_period':
      return `Сроки: ${action.from} — ${action.to}`
    case 'select_executors':
      return `Выбираю исполнителей: ${action.ids?.length}`
    case 'select_stages':
      return `Подтверждаю этапы: ${action.ids?.length}`
    case 'select_operations':
      return `Операции: ${action.ids?.length}`
    case 'set_flag':
      return action.flag ? 'Добавляю условие работ' : 'Снимаю условие работ'
    default:
      return action.type
  }
}
