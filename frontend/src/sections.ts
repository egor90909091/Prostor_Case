/**
 * Заголовки разделов ТЗ по ключу. Канонический набор один для всех шаблонов
 * и живёт в БД (tz.base_sections() в db/init/02_templates.sql); здесь —
 * только подписи для интерфейса: замечание подрядчика хранит section_key, и
 * его надо показать словами в треде, где самого документа рядом нет.
 *
 * Экран подрядчика берёт разделы из ответа /api/v1/tz/drafts (там ключ,
 * заголовок и текст раздела приходят из шаблона), эта таблица — подпись для
 * ленты замечаний и запасной вариант для незнакомого ключа.
 */
export const SECTION_TITLES: Record<string, string> = {
  purpose: 'Цели и задачи работ',
  abbreviations: 'Принятые сокращения',
  perimeter: 'Периметр выполнения работ',
  schedule: 'Сроки выполнения работ',
  kpi: 'КПЭ по SMART',
  content: 'Содержание работ',
  conditions: 'Условия выполнения работы',
  documentation: 'Требования к документации',
  quality: 'Контроль качества',
  subcontract: 'Условия привлечения субподрядчиков',
  other: 'Иные условия выполнения работ',
}

export function sectionTitle(key: string | null | undefined): string | null {
  if (!key) return null
  return SECTION_TITLES[key] ?? key
}

/** Русские подписи статусов согласования — одни и те же у обеих ролей. */
export const REVIEW_STATUS_LABELS: Record<string, string> = {
  sent: 'Направлено',
  viewed: 'Просмотрено',
  approved: 'Согласовано',
  revision: 'На доработке',
  rejected: 'Отклонено',
}

/** Тон бейджа статуса — те же классы, что у бейджей готовности в Blocks.tsx. */
export function reviewTone(status: string | null | undefined): string {
  switch (status) {
    case 'approved':
      return 'ok'
    case 'revision':
      return 'warn'
    case 'rejected':
      return 'low'
    case 'sent':
    case 'viewed':
      return 'neutral'
    default:
      return 'neutral'
  }
}
