// Клиент API. Главная особенность — приём хода диалога: он идёт по
// WebSocket (/api/v1/chat/sessions/{id}/ws), а не обычным запрос-ответом —
// сервер стримит события хода (delta/block/state/done) кадрами по мере
// готовности, а не одним ответом в конце.

import { actorHeaders } from './identity'

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
  productCategory?: string
  templateId?: string
  period?: { from?: string; to?: string }
  stages: number
  executors: number
  executorNames?: string[]
  tzId?: string
  // Поля ТЗ, собранные из разговора: приходят в каждом снапшоте состояния,
  // чтобы панель заявки показывала их сразу, а не после открытия конструктора.
  fields?: Record<string, string>
  flags?: string[]
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

function wsUrl(path: string): string {
  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${proto}//${window.location.host}${path}`
}

/**
 * Один ход диалога по WebSocket. Соединение открывается заново на каждый
 * ход и закрывается сразу после события done — так проще всего совместить
 * с уже существующей идемпотентностью по clientMessageId на сервере, не
 * городя поверх неё отдельный протокол переподключения на клиенте.
 */
export function sendTurn(
  sessionId: string,
  body: { text?: string; action?: TurnAction },
  handlers: StreamHandlers,
  signal?: AbortSignal,
): Promise<void> {
  const clientMessageId = `c-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`

  return new Promise((resolve) => {
    if (signal?.aborted) {
      resolve()
      return
    }

    const socket = new WebSocket(wsUrl(`/api/v1/chat/sessions/${sessionId}/ws`))
    let settled = false
    let opened = false

    const finish = () => {
      if (settled) return
      settled = true
      signal?.removeEventListener('abort', onAbort)
      handlers.onDone?.()
      resolve()
    }

    const onAbort = () => socket.close()
    signal?.addEventListener('abort', onAbort)

    socket.onopen = () => {
      opened = true
      socket.send(JSON.stringify({ clientMessageId, ...body }))
    }

    socket.onmessage = (ev) => {
      let frame: { event?: string; data?: any }
      try {
        frame = JSON.parse(ev.data)
      } catch {
        return
      }
      const event = frame.event ?? 'message'
      const payload = frame.data ?? {}

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
          socket.close()
          break
      }
    }

    socket.onerror = () => {
      if (!opened) handlers.onError?.('Ошибка соединения')
    }

    socket.onclose = () => finish()
  })
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
  asDraft = false,
) {
  const response = await fetch('/api/v1/tz/documents', {
    method: 'POST',
    headers: json,
    body: JSON.stringify({
      sessionId, templateId, state, force, parentTzId: parentTzId ?? null, asDraft,
    }),
  })
  const body = await response.json()
  return { ok: response.ok, status: response.status, body }
}

export async function listTzDocuments() {
  // Не 20: «Мои заявки» раскладывают документы по категориям и показывают
  // счётчики, а счётчик по обрезанному списку врёт — согласованное ТЗ месячной
  // давности просто не попало бы в свою категорию. Для прототипа с десятками
  // документов один запрос дешевле пагинации.
  const response = await fetch('/api/v1/tz/documents?limit=200')
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
  status: 'draft' | 'final'
  downloadUrl: string
  pdfUrl: string
}

/**
 * Ссылка на файл документа. PDF — то же самое ТЗ, собранное на лету из
 * сохранённого payload (в хранилище лежит только .docx), поэтому это один
 * эндпойнт с параметром формата, а не два разных адреса.
 */
export function documentFileUrl(tzId: string, format: 'docx' | 'pdf' = 'docx'): string {
  return `/api/v1/tz/documents/${tzId}/file${format === 'pdf' ? '?format=pdf' : ''}`
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

export interface ReviewIssue {
  title: string
  detail: string
  severity: string
}

// НЕ /api/v1/tz/... — этот путь физически живёт в Prostor.Chat (там есть
// LLM), а nginx/vite проксируют префикс /api/v1/tz строковым совпадением
// на Prostor.Tz (см. frontend/nginx.conf).
export async function reviewDraft(
  templateId: string,
  state: any,
): Promise<{ issues: ReviewIssue[]; available: boolean }> {
  const response = await fetch('/api/v1/review/draft', {
    method: 'POST',
    headers: json,
    body: JSON.stringify({ templateId, state }),
  })
  if (!response.ok) return { issues: [], available: false }
  return response.json()
}

// ------------------------------------------------------------ справочники
export async function getStages(productId: string) {
  const response = await fetch(`/api/v1/catalog/products/${productId}/stages?top=20`)
  return response.json()
}

export interface ProductRisk {
  title: string
  severity: string
  count: number
}

export async function getProductRisks(productId: string): Promise<{ items: ProductRisk[] }> {
  const response = await fetch(`/api/v1/catalog/products/${productId}/risks?top=5`)
  return response.json()
}

export async function getAnalytics() {
  const response = await fetch('/api/v1/analytics/overview')
  return response.json()
}

// ------------------------------------------------------------ согласование
// Роль уходит заголовком X-Prostor-Actor. Это демо-контекст, а не
// авторизация: бэкенд принимает заголовок на веру (см. identity.ts).

export interface CompanyRef {
  companyId: string
  code: string
  name: string
  rating: number
}

export async function getCompanies(): Promise<{ items: CompanyRef[] }> {
  const response = await fetch('/api/v1/catalog/companies')
  if (!response.ok) return { items: [] }
  return response.json()
}

export type ReviewStatus = 'sent' | 'viewed' | 'approved' | 'revision' | 'rejected'

export interface Assignment {
  assignmentId: string
  tzId: string
  version: number
  companyId: string
  companyName: string
  companyCode: string
  status: ReviewStatus
  note?: string | null
  createdAt: string
  viewedAt?: string | null
  decidedAt?: string | null
}

export interface InboxItem {
  assignmentId: string
  tzId: string
  rootTzId: string
  status: ReviewStatus
  note?: string | null
  createdAt: string
  viewedAt?: string | null
  decidedAt?: string | null
  version: number
  readiness: number
  templateName: string
  productName: string
  objectName: string
  customerName: string
  commentsCount: number
}

export interface ReviewComment {
  commentId: string
  tzId: string
  assignmentId?: string | null
  authorKind: 'customer' | 'contractor'
  authorId: string
  authorName: string
  sectionKey?: string | null
  kind: 'comment' | 'decision'
  decision?: ReviewStatus | null
  text: string
  createdAt: string
}

export async function sendToContractors(
  tzId: string,
  companyIds: string[],
  note?: string,
): Promise<{ ok: boolean; body: any }> {
  const response = await fetch(`/api/v1/tz/documents/${tzId}/assignments`, {
    method: 'POST',
    headers: { ...json, ...actorHeaders() },
    body: JSON.stringify({ companyIds, note: note ?? null }),
  })
  return { ok: response.ok, body: await response.json() }
}

export async function getAssignments(tzId: string): Promise<{ items: Assignment[] }> {
  const response = await fetch(`/api/v1/tz/documents/${tzId}/assignments`, {
    headers: actorHeaders(),
  })
  if (!response.ok) return { items: [] }
  return response.json()
}

export async function getInbox(): Promise<{ items: InboxItem[] }> {
  const response = await fetch('/api/v1/tz/inbox', { headers: actorHeaders() })
  if (!response.ok) return { items: [] }
  return response.json()
}

export async function markAssignmentViewed(assignmentId: string): Promise<void> {
  await fetch(`/api/v1/tz/assignments/${assignmentId}/view`, {
    method: 'POST',
    headers: actorHeaders(),
  })
}

export async function decideAssignment(
  assignmentId: string,
  decision: 'approved' | 'revision' | 'rejected',
  text: string,
): Promise<{ ok: boolean; body: any }> {
  const response = await fetch(`/api/v1/tz/assignments/${assignmentId}/decision`, {
    method: 'POST',
    headers: { ...json, ...actorHeaders() },
    body: JSON.stringify({ decision, text }),
  })
  return { ok: response.ok, body: await response.json() }
}

export async function getComments(tzId: string): Promise<{ items: ReviewComment[] }> {
  const response = await fetch(`/api/v1/tz/documents/${tzId}/comments`, {
    headers: actorHeaders(),
  })
  if (!response.ok) return { items: [] }
  return response.json()
}

export async function addComment(
  tzId: string,
  text: string,
  sectionKey?: string | null,
  assignmentId?: string | null,
): Promise<{ items: ReviewComment[] }> {
  const response = await fetch(`/api/v1/tz/documents/${tzId}/comments`, {
    method: 'POST',
    headers: { ...json, ...actorHeaders() },
    body: JSON.stringify({
      text,
      sectionKey: sectionKey ?? null,
      assignmentId: assignmentId ?? null,
    }),
  })
  if (!response.ok) return { items: [] }
  return response.json()
}
