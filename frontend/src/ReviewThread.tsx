import { useState } from 'react'
import type { ReviewComment } from './api'
import { REVIEW_STATUS_LABELS, reviewTone, sectionTitle } from './sections'

function fmt(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit',
  })
}

/**
 * Лента согласования: замечания подрядчика, ответы заказчика и вердикты.
 * Одна и та же для обеих ролей — различие только в том, чьи реплики
 * подсвечены как свои и кому доступна кнопка «Исправить».
 *
 * Тред грузится по корню цепочки версий, поэтому в нём видно и обсуждение
 * предыдущих версий ТЗ: после правок по замечаниям разговор продолжается,
 * а не начинается заново.
 */
export function ReviewThread({
  comments,
  myKind,
  sections,
  onSubmit,
  onFixSection,
  placeholder = 'Написать по документу…',
}: {
  comments: ReviewComment[]
  myKind: 'customer' | 'contractor'
  /** Разделы документа для привязки замечания. Пусто — только общий комментарий. */
  sections?: { key: string; title: string }[]
  onSubmit: (text: string, sectionKey: string | null) => Promise<void>
  /** Заказчик: перейти к правке раздела в конструкторе. */
  onFixSection?: (sectionKey: string) => void
  placeholder?: string
}) {
  const [text, setText] = useState('')
  const [sectionKey, setSectionKey] = useState('')
  const [sending, setSending] = useState(false)

  const submit = async () => {
    const value = text.trim()
    if (!value || sending) return
    setSending(true)
    try {
      await onSubmit(value, sectionKey || null)
      setText('')
      setSectionKey('')
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="review-thread">
      {comments.length === 0 ? (
        <p className="muted small">Замечаний пока нет.</p>
      ) : (
        <ul className="plain review-list">
          {comments.map((c) => {
            const section = sectionTitle(c.sectionKey)
            return (
              <li
                key={c.commentId}
                className={`review-item${c.authorKind === myKind ? ' own' : ''}${
                  c.kind === 'decision' ? ' decision' : ''
                }`}
              >
                <div className="review-item-head">
                  <strong>{c.authorName}</strong>
                  {c.kind === 'decision' && c.decision && (
                    <span className={`badge ${reviewTone(c.decision)}`}>
                      {REVIEW_STATUS_LABELS[c.decision] ?? c.decision}
                    </span>
                  )}
                  {section && <span className="review-section">{section}</span>}
                  <span className="muted small">{fmt(c.createdAt)}</span>
                </div>
                <p>{c.text}</p>
                {/* Замечание к разделу — единственный вид реплики, из которой
                    есть куда перейти: конструктор подведёт к нужным полям. */}
                {section && c.sectionKey && onFixSection && (
                  <button className="btn small ghost" onClick={() => onFixSection(c.sectionKey!)}>
                    Исправить раздел
                  </button>
                )}
              </li>
            )
          })}
        </ul>
      )}

      <div className="review-form">
        {sections && sections.length > 0 && (
          <select
            className="top-select review-section-select"
            value={sectionKey}
            onChange={(e) => setSectionKey(e.target.value)}
            aria-label="Раздел ТЗ"
          >
            <option value="">Ко всему документу</option>
            {sections.map((s) => (
              <option key={s.key} value={s.key}>{s.title}</option>
            ))}
          </select>
        )}
        <textarea
          rows={2}
          placeholder={placeholder}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <button className="btn small primary" disabled={!text.trim() || sending} onClick={submit}>
          {sending ? 'Отправляю…' : 'Отправить'}
        </button>
      </div>
    </div>
  )
}
