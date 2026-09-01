import { useSyncExternalStore } from 'react'
import { clearConstructorState } from './constructorStorage'

/**
 * Текущая заявка на фронте — то, что пользователь считает «своим ТЗ прямо
 * сейчас»: диалог, форма конструктора и сформированный документ.
 *
 * Документ раньше жил только в React-состоянии ConstructorView, а конструктор
 * размонтируется при переключении вкладки — поэтому зона готового документа
 * исчезала, стоило уйти в чат и вернуться, и ни чат, ни «Мои заявки» не знали,
 * что документ вообще существует. Здесь он лежит в одном месте: localStorage
 * переживает и вкладки, и F5, а useSyncExternalStore держит все экраны на
 * одном снимке — включая соседние вкладки браузера (событие storage).
 */
export interface WorkspaceDocument {
  tzId: string
  version: number
  status: 'draft' | 'final'
  readiness: number
  /** Название для карточки: объект работ, иначе услуга. */
  title: string
  templateId?: string | null
  /** Корень цепочки версий — с ним следующее сохранение станет версией, а не новым ТЗ. */
  parentTzId?: string | null
  createdAt: string
}

const DOCUMENT_KEY = 'prostor.workspace.document'

/**
 * Указатель на сессию чата. Сама история диалога лежит в Postgres, здесь —
 * только идентификатор, зато доступный всем экранам: после F5 состояние App
 * пустое, и без общего указателя конструктор терял связь с диалогом, из
 * которого он открыт.
 */
export const CHAT_SESSION_KEY = 'prostor.chat.sessionId'

export function getChatSessionId(): string | null {
  try {
    return localStorage.getItem(CHAT_SESSION_KEY)
  } catch {
    return null
  }
}

const listeners = new Set<() => void>()

// undefined — «ещё не читали из localStorage». Кэш нужен, чтобы getSnapshot
// возвращал стабильную ссылку: иначе useSyncExternalStore на каждом рендере
// видел бы новый объект после JSON.parse и уходил в бесконечный цикл.
let cache: WorkspaceDocument | null | undefined

function read(): WorkspaceDocument | null {
  try {
    const raw = localStorage.getItem(DOCUMENT_KEY)
    return raw ? (JSON.parse(raw) as WorkspaceDocument) : null
  } catch {
    return null
  }
}

function snapshot(): WorkspaceDocument | null {
  if (cache === undefined) cache = read()
  return cache
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  const onStorage = (event: StorageEvent) => {
    if (event.key !== DOCUMENT_KEY) return
    cache = undefined
    listener()
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    window.removeEventListener('storage', onStorage)
  }
}

export function getCurrentDocument(): WorkspaceDocument | null {
  return snapshot()
}

export function setCurrentDocument(document: WorkspaceDocument | null) {
  cache = document
  try {
    if (document) localStorage.setItem(DOCUMENT_KEY, JSON.stringify(document))
    else localStorage.removeItem(DOCUMENT_KEY)
  } catch {
    /* переполнение storage — карточка просто не переживёт перезагрузку */
  }
  listeners.forEach((listener) => listener())
}

/** Подписка на текущий документ: одна и та же карточка в чате, конструкторе и заявках. */
export function useCurrentDocument(): WorkspaceDocument | null {
  // Третий аргумент — снимок для серверного рендера; его здесь нет, но
  // сигнатура требует функцию, а localStorage вне браузера недоступен.
  return useSyncExternalStore(subscribe, snapshot, () => null)
}

/**
 * «Начать заново» в чате обещает сбросить и диалог, и черновик ТЗ — значит,
 * форма и документ должны уходить вместе с сессией. Указатель на саму сессию
 * чата чистит ChatView: он им и владеет.
 */
export function resetWorkspace() {
  clearConstructorState()
  setCurrentDocument(null)
}
