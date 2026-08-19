// Общий с ConstructorView ключ localStorage и таблица переименования полей
// между wire-форматом действия set_field (snake_case, как в
// Pipeline.ApplyField на бэкенде) и именами в state конструктора
// (camelCase, единственное расхождение сегодня — source_data → sourceData).
export const STATE_STORAGE_KEY = 'prostor.constructor.state'

export const STATE_KEY_OVERRIDES: Record<string, string> = { source_data: 'sourceData' }

// Принятые в чате LLM-подсказки не пишутся в STATE_STORAGE_KEY напрямую:
// ConstructorView решает, грузить ли состояние из чата (productId, шаблон,
// этапы) или из localStorage, по ОДНОМУ булеву флагу — «есть что-то в
// STATE_STORAGE_KEY» (hasStored). Если записать туда поле до первого
// открытия конструктора, флаг взведётся преждевременно и конструктор при
// первом открытии решит, что пользователь уже редактировал форму, — и
// пропустит загрузку из чата, потеряв услугу, шаблон и этапы, оставив
// только эти три текстовых поля.
//
// Поэтому подсказки складываются в отдельную очередь и подмешиваются
// конструктором ПОВЕРХ уже загруженного состояния (из чата, из БД при
// editTzId, или из localStorage) — на каждом монтировании, независимо от
// того, какой из трёх источников сработал.
const PENDING_FIELDS_KEY = 'prostor.constructor.pendingFields'

export function queueSuggestedField(key: string, value: string) {
  const stateKey = STATE_KEY_OVERRIDES[key] ?? key
  try {
    const stored = localStorage.getItem(PENDING_FIELDS_KEY)
    const pending = stored ? JSON.parse(stored) : {}
    localStorage.setItem(PENDING_FIELDS_KEY, JSON.stringify({ ...pending, [stateKey]: value }))
  } catch {
    /* повреждённый JSON в localStorage или переполнение — не критично */
  }
}

/** Забирает и очищает очередь — вызывать ровно один раз на монтирование конструктора. */
export function consumePendingFields(): Record<string, string> {
  try {
    const stored = localStorage.getItem(PENDING_FIELDS_KEY)
    if (!stored) return {}
    localStorage.removeItem(PENDING_FIELDS_KEY)
    return JSON.parse(stored)
  } catch {
    return {}
  }
}
