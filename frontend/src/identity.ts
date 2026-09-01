import { useSyncExternalStore } from 'react'

/**
 * Роль, под которой сейчас работают с системой: заказчик (НТЦ) или одна из
 * компаний-подрядчиков. Ролей ровно две, заказчик один, подрядчиков много —
 * это те же компании, что лежат в catalog.company и участвуют в подборе
 * исполнителей.
 *
 * ВАЖНО: это переключатель демо-контекста, а не авторизация. Логинов и
 * паролей в прототипе нет; роль лежит в localStorage и уходит на бэкенд
 * заголовком X-Prostor-Actor, который там принимается на веру
 * (см. backend/src/Prostor.Tz/Actor.cs и docs/architecture.md). Разграничения
 * доступа за этим нет и обещать его в интерфейсе нельзя.
 *
 * Хранилище устроено так же, как workspace.ts: localStorage переживает F5 и
 * соседние вкладки браузера, useSyncExternalStore держит все экраны на одном
 * снимке.
 */
export interface CustomerActor {
  kind: 'customer'
  id: 'ntc'
  name: string
  code: string
}

export interface ContractorActor {
  kind: 'contractor'
  id: string
  name: string
  code: string
}

export type Actor = CustomerActor | ContractorActor

export const CUSTOMER: CustomerActor = { kind: 'customer', id: 'ntc', name: 'НТЦ', code: 'НТЦ' }

const ACTOR_KEY = 'prostor.actor'

const listeners = new Set<() => void>()

// undefined — «ещё не читали из localStorage». Кэш нужен ради стабильной
// ссылки в getSnapshot: без него useSyncExternalStore на каждом рендере
// видел бы новый объект после JSON.parse и уходил в бесконечный цикл.
let cache: Actor | undefined

function read(): Actor {
  try {
    const raw = localStorage.getItem(ACTOR_KEY)
    if (!raw) return CUSTOMER
    const parsed = JSON.parse(raw) as Actor
    if (parsed?.kind === 'contractor' && parsed.id) return parsed
    return CUSTOMER
  } catch {
    return CUSTOMER
  }
}

function snapshot(): Actor {
  if (cache === undefined) cache = read()
  return cache
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  const onStorage = (event: StorageEvent) => {
    if (event.key !== ACTOR_KEY) return
    cache = undefined
    listener()
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    window.removeEventListener('storage', onStorage)
  }
}

export function getActor(): Actor {
  return snapshot()
}

export function setActor(actor: Actor) {
  cache = actor
  try {
    if (actor.kind === 'customer') localStorage.removeItem(ACTOR_KEY)
    else localStorage.setItem(ACTOR_KEY, JSON.stringify(actor))
  } catch {
    /* приватный режим/переполнение — роль просто не переживёт перезагрузку */
  }
  listeners.forEach((listener) => listener())
}

export function useActor(): Actor {
  // Третий аргумент — снимок для серверного рендера; его здесь нет, но
  // сигнатура требует функцию, а localStorage вне браузера недоступен.
  return useSyncExternalStore(subscribe, snapshot, () => CUSTOMER)
}

/** Заголовок роли для запросов согласования. */
export function actorHeaders(): Record<string, string> {
  const actor = getActor()
  return { 'X-Prostor-Actor': `${actor.kind}:${actor.id}` }
}
