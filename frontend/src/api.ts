// Клиент API. Главная особенность — приём хода диалога:
// это обычный POST, ответ на который сервер отдаёт долгим потоком.
// EventSource здесь не годится (умеет только GET), поэтому читаем
// ReadableStream и сами разбираем SSE-кадры.

export type BlockItem = Record<string, any>

export interface Block {
  type: string
  text?: string
  selectMode?: string
  items?: BlockItem[]
  meta?: Record<string, any>
}

export interface ChatStateSnapshot {
  step: string
  missing: string[]
  productId?: string
  productName?: string
  templateId?: string
  period?: { from?: string; to?: string }
  stages: number
  executors: number
  tzId?: string
}

export interface TurnAction {
  type: string
  id?: string
  ids?: string[]
  from?: string
  to?: string
  key?: string
  value?: string
  flag?: boolean
  subcontract?: string[]
}

export interface ChatMessage {
  seq: number
  role: 'user' | 'assistant'
  blocks: Block[]
  createdAt: string
}

export interface SessionDetail {
  sessionId: string
  state: ChatStateSnapshot
  fields: Record<string, string | undefined>
  messages: ChatMessage[]
}

export interface StreamHandlers {
  onDelta?: (text: string) => void
  onBlock?: (block: Block) => void
  onState?: (state: ChatStateSnapshot) => void
  onDone?: () => void
  onError?: (message: string) => void
}

const json = { 'Content-Type': 'application/json' }

export async function createSession(customerName?: string): Promise<{ sessionId: string; state: ChatStateSnapshot }> {
  const response = await fetch('/api/v1/chat/sessions', {
    method: 'POST',
    headers: json,
    body: JSON.stringify({ customerName: customerName ?? null }),
  })
  if (!response.ok) throw new Error('не удалось создать сессию')
  return response.json()
}

export async function getSession(sessionId: string): Promise<SessionDetail> {
  const response = await fetch(`/api/v1/chat/sessions/${sessionId}`)
  if (!response.ok) throw new Error('сессия не найдена')
  return response.json()
}

/**
 * Один ход диалога. Соединение живёт ровно до события done.
 */
export async function sendTurn(
  sessionId: string,
  body: { text?: string; action?: TurnAction },
  handlers: StreamHandlers,
  signal?: AbortSignal,
): Promise<void> {
  const clientMessageId = `c-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`

  const response = await fetch(`/api/v1/chat/sessions/${sessionId}/turns`, {
    method: 'POST',
    headers: { ...json, Accept: 'text/event-stream', 'Idempotency-Key': clientMessageId },
    body: JSON.stringify({ clientMessageId, ...body }),
    signal,
  })

  if (response.status === 409) {
    handlers.onError?.('Предыдущий запрос ещё выполняется')
    return
  }
  if (!response.ok || !response.body) {
    handlers.onError?.(`Ошибка соединения (${response.status})`)
    return
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    // Кадры SSE разделены пустой строкой
    let split: number
    while ((split = buffer.indexOf('\n\n')) >= 0) {
      const frame = buffer.slice(0, split)
      buffer = buffer.slice(split + 2)
      dispatch(frame, handlers)
    }
  }
  handlers.onDone?.()
}

function dispatch(frame: string, handlers: StreamHandlers) {
  let event = 'message'
  const dataLines: string[] = []

  for (const line of frame.split('\n')) {
    if (line.startsWith(':')) return           // heartbeat
    if (line.startsWith('event:')) event = line.slice(6).trim()
    else if (line.startsWith('data:')) dataLines.push(line.slice(5).trim())
  }
  if (dataLines.length === 0) return

  let payload: any
  try {
    payload = JSON.parse(dataLines.join('\n'))
  } catch {
    return
  }

  switch (event) {
    case 'delta':
      handlers.onDelta?.(payload.text ?? '')
      break
    case 'block':
      if (payload.block) handlers.onBlock?.(payload.block)
      break
    case 'state':
      handlers.onState?.(payload)
      break
    case 'error':
      handlers.onError?.(payload.message ?? 'ошибка')
      break
    case 'done':
      break
  }
}

// ------------------------------------------------------------------ ТЗ
export async function draftTz(sessionId: string | null, templateId: string, state: any) {
  const response = await fetch('/api/v1/tz/drafts', {
    method: 'POST',
    headers: json,
    body: JSON.stringify({ sessionId, templateId, state }),
  })
  if (!response.ok) throw new Error('не удалось собрать черновик')
  return response.json()
}

export async function createTzDocument(
  sessionId: string | null,
  templateId: string,
  state: any,
  force = false,
  parentTzId?: string | null,
) {
  const response = await fetch('/api/v1/tz/documents', {
    method: 'POST',
    headers: json,
    body: JSON.stringify({ sessionId, templateId, state, force, parentTzId: parentTzId ?? null }),
  })
  const body = await response.json()
  return { ok: response.ok, status: response.status, body }
}

export async function listTzDocuments() {
  const response = await fetch('/api/v1/tz/documents?limit=20')
  return response.json()
}

export async function getTzDocument(tzId: string) {
  const response = await fetch(`/api/v1/tz/documents/${tzId}`)
  if (!response.ok) throw new Error('ТЗ не найдено')
  return response.json()
}

export interface TzVersionItem {
  tzId: string
  version: number
  readiness: number
  createdAt: string
  productName: string
  objectName: string
  downloadUrl: string
}

export async function getTzDocumentVersions(tzId: string): Promise<{ rootTzId: string; items: TzVersionItem[] }> {
  const response = await fetch(`/api/v1/tz/documents/${tzId}/versions`)
  if (!response.ok) throw new Error('не удалось загрузить версии')
  return response.json()
}

export async function getTemplates() {
  const response = await fetch('/api/v1/tz/templates')
  return response.json()
}

// ------------------------------------------------------------ справочники
export async function getStages(productId: string) {
  const response = await fetch(`/api/v1/catalog/products/${productId}/stages?top=20`)
  return response.json()
}

export async function getAnalytics() {
  const response = await fetch('/api/v1/analytics/overview')
  return response.json()
}
