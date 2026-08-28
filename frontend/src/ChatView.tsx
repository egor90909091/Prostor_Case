import { useEffect, useRef, useState } from 'react'
import { createSession, getSession, sendTurn } from './api'
import type { Block, ChatStateSnapshot, TurnAction } from './api'
import { BlockView } from './Blocks'
import { queueSuggestedField } from './constructorStorage'
import { CHAT_SESSION_KEY as SESSION_STORAGE_KEY, resetWorkspace, useCurrentDocument } from './workspace'
import { documentFileUrl } from './api'

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
// второй источник истины, который может разойтись с базой. Сам ключ живёт в
// workspace.ts: указатель нужен и конструктору, чтобы после перезагрузки
// страницы не потерять связь заявки с диалогом.

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

export function ChatView({
  onOpenConstructor,
  onOpenDocuments,
  onResetSession,
  documentCreated,
}: {
  onOpenConstructor: (sessionId: string) => void
  onOpenDocuments?: (tzId?: string) => void
  onResetSession?: () => void
  // Документ, сформированный в конструкторе: чат отмечает его отдельным ходом,
  // чтобы состояние сессии на сервере и лента диалога знали о готовом ТЗ.
  // Иначе ассистент продолжал бы предлагать «собрать ТЗ», когда оно уже есть.
  documentCreated?: { tzId: string; at: number } | null
}) {
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [state, setState] = useState<ChatStateSnapshot | null>(null)
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [confirmingReset, setConfirmingReset] = useState(false)
  const bottom = useRef<HTMLDivElement>(null)
  // Автопрокрутка «прилипает» к низу, только пока пользователь сам находится
  // у последнего сообщения. Если он прокрутил вверх — читать историю, —
  // стрим больше не должен выдёргивать экран вниз при каждой новой реплике.
  const pinnedToBottom = useRef(true)
  const skipNextScroll = useRef(true)
  const scrollTimer = useRef<number | null>(null)
  // Один и тот же документ во всех разделах: чат читает его из общего
  // хранилища, а не заводит собственную копию.
  const currentDocument = useCurrentDocument()
  const notedDocument = useRef<string | null>(null)

  function startNewSession() {
    return createSession('Демо-заказчик')
      .then((s) => {
        localStorage.setItem(SESSION_STORAGE_KEY, s.sessionId)
        setSessionId(s.sessionId)
        setState(s.state)
        pinnedToBottom.current = true
        skipNextScroll.current = true
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
        pinnedToBottom.current = true
        skipNextScroll.current = true
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

  // Отслеживаем, видит ли пользователь последнее сообщение: сравнивать
  // scrollTop с высотой всей страницы нельзя — под лентой ещё есть композер
  // и примеры, так что «низ страницы» и «низ переписки» — разные точки.
  // Вместо этого на каждый scroll меряем положение сторожевого div через
  // getBoundingClientRect — синхронно и без зависимости от отдельного
  // цикла анимации/пересечений.
  useEffect(() => {
    const updatePinned = () => {
      const el = bottom.current
      if (!el) return
      const rect = el.getBoundingClientRect()
      pinnedToBottom.current = rect.top <= window.innerHeight + 120
    }
    updatePinned()
    window.addEventListener('scroll', updatePinned, { passive: true })
    window.addEventListener('resize', updatePinned)
    return () => {
      window.removeEventListener('scroll', updatePinned)
      window.removeEventListener('resize', updatePinned)
    }
  }, [])

  // Прокрутка к низу батчится через таймер, чтобы частые дельты стрима не
  // запускали смуз-анимацию заново на каждый чанк — иначе экран дёргается
  // вместо плавного «догоняющего» скролла. requestAnimationFrame здесь не
  // подходит: он замирает на фоновой/неактивной вкладке, а стрим должен
  // продолжать плавно доезжать до низа и в таком состоянии. При
  // восстановлении истории первый проход — мгновенный прыжок, а не проезд
  // через весь диалог.
  useEffect(() => {
    if (messages.length === 0 || !pinnedToBottom.current) return
    const behavior = skipNextScroll.current ? 'auto' : 'smooth'
    skipNextScroll.current = false
    if (scrollTimer.current !== null) window.clearTimeout(scrollTimer.current)
    scrollTimer.current = window.setTimeout(() => {
      bottom.current?.scrollIntoView({ behavior, block: 'end' })
    }, 0)
    return () => {
      if (scrollTimer.current !== null) window.clearTimeout(scrollTimer.current)
    }
  }, [messages])

  async function run(body: { text?: string; action?: TurnAction }, userLabel: string) {
    if (!sessionId || busy) return
    setBusy(true)
    setError(null)
    // Своя реплика — явное действие, ради которого стоит вернуться к низу,
    // даже если до этого пользователь листал историю вверх.
    pinnedToBottom.current = true
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

  // Ход-отметка о готовом ТЗ. Отправляется один раз на документ и только когда
  // чат свободен: если в этот момент идёт другой ход, эффект просто повторится
  // после его завершения (busy в зависимостях).
  useEffect(() => {
    if (!documentCreated || !sessionId || busy) return
    if (notedDocument.current === documentCreated.tzId) return
    notedDocument.current = documentCreated.tzId
    void run({ action: { type: 'tz_created', id: documentCreated.tzId } }, 'ТЗ сформировано')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [documentCreated, sessionId, busy])

  const submit = () => {
    const text = input.trim()
    if (!text) return
    setInput('')
    void run({ text }, text)
  }

  function resetSession() {
    if (busy) return
    if (messages.length > 1 && !confirmingReset) {
      setConfirmingReset(true)
      return
    }
    setConfirmingReset(false)
    // «Сбросить чат и черновик ТЗ» — значит и форму конструктора, и
    // сформированный документ, а не только диалог: иначе новая заявка
    // открывалась со старыми данными предыдущей.
    resetWorkspace()
    notedDocument.current = null
    onResetSession?.()
    void startNewSession()
  }

  const last = messages[messages.length - 1]
  const streaming = busy && last?.role === 'assistant' && last.blocks.length > 0
  const thinking = busy && (!last || last.role !== 'assistant' || last.blocks.length === 0)

  return (
    <div className="chat">
      <div className="chat-main">
        <div className="chat-toolbar">
          {confirmingReset && (
            <>
              <span className="muted small">Сбросить чат и черновик ТЗ?</span>
              <button className="btn ghost small" onClick={() => setConfirmingReset(false)}>
                Отмена
              </button>
            </>
          )}
          <button
            className={`btn small ${confirmingReset ? 'primary' : 'ghost'}`}
            disabled={busy || !sessionId}
            onClick={resetSession}
          >
            {confirmingReset ? 'Подтвердить' : 'Начать заново'}
          </button>
        </div>
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
                    selectedProductId={state?.productId}
                    onAction={(action) => {
                      // В очередь, а не сразу в основной state конструктора:
                      // прямая запись до первого открытия вкладки взвела бы
                      // guard hasStored и обрезала бы загрузку услуги/этапов
                      // из чата при первом визите (см. constructorStorage.ts).
                      if (action.type === 'set_field' && action.key && action.value !== undefined)
                        queueSuggestedField(action.key, action.value)
                      void run({ action }, actionLabel(action))
                    }}
                    onOpenConstructor={() => sessionId && onOpenConstructor(sessionId)}
                    onSuggest={(text) => void run({ text }, text)}
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
            placeholder={
              state?.productId
                ? 'Спросите что угодно, уточните детали или назовите сроки словами'
                : 'Опишите нужные работы или просто задайте вопрос'
            }
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
        <SelectedSummary state={state} />
        <CollectedFields state={state} />
        <div className="step">Шаг: {state?.step ? stepLabel(state.step) : '—'}</div>
        {state?.missing?.length ? (
          <div className="missing">
            <div className="muted small">не хватает:</div>
            {state.missing.map((m) => (
              <span className="tag warn" key={m}>{fieldLabel(m)}</span>
            ))}
          </div>
        ) : null}

        {currentDocument && (
          <div className="card summary doc-chip">
            <div className="summary-label">Документ ТЗ</div>
            <div className="summary-title">{currentDocument.title}</div>
            <div className="doc-chip-meta">
              <span className={`badge ${currentDocument.status === 'draft' ? 'draft' : 'ok'}`}>
                {currentDocument.status === 'draft' ? 'Черновик' : 'Готово'}
              </span>
              <span className="badge neutral">Версия {currentDocument.version}</span>
              <span className="badge neutral">Готовность {currentDocument.readiness}%</span>
            </div>
            <div className="doc-chip-actions">
              <a className="btn small" href={documentFileUrl(currentDocument.tzId)} download>.docx</a>
              <a className="btn small" href={documentFileUrl(currentDocument.tzId, 'pdf')} download>.pdf</a>
              {onOpenDocuments && (
                <button
                  className="btn small ghost"
                  onClick={() => onOpenDocuments(currentDocument.tzId)}
                >
                  Открыть
                </button>
              )}
            </div>
          </div>
        )}

        <button
          className="btn primary wide"
          disabled={!sessionId || !state?.productId}
          onClick={() => sessionId && onOpenConstructor(sessionId)}
        >
          {currentDocument ? 'Вернуться в конструктор' : 'Сформировать ТЗ'}
        </button>
        <p className="muted small">
          Данные диалога переносятся в конструктор: тип ТЗ, этапы, сроки и исполнители
          будут предзаполнены.
        </p>
      </aside>
    </div>
  )
}

// Сводка всего выбранного в диалоге: услуга всегда на виду, остальные слоты —
// по мере заполнения. До выбора услуги занимает место подсказкой, чтобы панель
// не «прыгала» после первого поиска.
function SelectedSummary({ state }: { state: ChatStateSnapshot | null }) {
  if (!state?.productId) {
    return (
      <div className="card summary empty">
        <div className="muted small">
          Услуга ещё не выбрана — опишите нужные работы в чате, и я предложу варианты.
        </div>
      </div>
    )
  }

  return (
    <div className="card summary">
      <div className="summary-label">Выбранная услуга</div>
      <div className="summary-title">{state.productName}</div>
      {state.productCategory && <div className="summary-sub">{state.productCategory}</div>}
      <div className="summary-rows">
        <div className="slot">
          <span className="slot-label">Сроки</span>
          <span className="slot-value">
            {state.period?.from ? `${state.period.from} — ${state.period.to}` : '—'}
          </span>
        </div>
        <div className="slot">
          <span className="slot-label">Исполнители</span>
          <span className="slot-value">
            {state.executorNames?.length ? state.executorNames.join(', ') : '—'}
          </span>
        </div>
        <div className="slot">
          <span className="slot-label">Этапы</span>
          <span className="slot-value">{state.stages ? `${state.stages}` : '—'}</span>
        </div>
      </div>
    </div>
  )
}

// Данные ТЗ, услышанные в разговоре. Панель показывает их сразу, как только
// ассистент их записал: человек видит, что именно понято, и может поправить
// репликой в чате, не открывая конструктор.
function CollectedFields({ state }: { state: ChatStateSnapshot | null }) {
  const entries = Object.entries(state?.fields ?? {}).filter(([, value]) => value)
  const flags = state?.flags ?? []
  if (entries.length === 0 && flags.length === 0) return null

  return (
    <div className="card summary">
      <div className="summary-label">Собрано из диалога</div>
      <div className="summary-rows">
        {entries.map(([key, value]) => (
          <div className="slot" key={key}>
            <span className="slot-label">{fieldLabel(key)}</span>
            <span className="slot-value">{value}</span>
          </div>
        ))}
      </div>
      {flags.length > 0 && (
        <div className="missing">
          {flags.map((flag) => (
            <span className="tag" key={flag}>{fieldLabel(flag)}</span>
          ))}
        </div>
      )}
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
    purpose: 'цель работ',
    customer: 'заказчик',
    perimeter: 'периметр работ',
    sourceData: 'исходные данные',
    documentation: 'документация',
    acceptance: 'приёмка',
    other: 'особые условия',
    model3d: '3D геологическая модель',
    subcontract: 'допускается субподряд',
    urgent: 'срочное выполнение',
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
    case 'set_field':
      return 'Принимаю предложенное поле'
    case 'extract_tz':
    case 'suggest_fields':
      return 'Собрать поля ТЗ из диалога'
    case 'tz_created':
      return 'ТЗ сформировано'
    default:
      return action.type
  }
}
