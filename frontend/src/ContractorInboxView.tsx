import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  addComment, decideAssignment, documentFileUrl, draftTz, getComments, getInbox,
  getTzDocument, markAssignmentViewed,
  type InboxItem, type ReviewComment,
} from './api'
import { DocxIcon, PdfIcon, readinessTone } from './Blocks'
import { DocxPreview } from './DocxPreview'
import { ReviewThread } from './ReviewThread'
import { REVIEW_STATUS_LABELS, reviewTone } from './sections'
import type { ContractorActor } from './identity'

type Filter = 'pending' | 'decided' | 'all'

const FILTERS: { key: Filter; label: string }[] = [
  { key: 'pending', label: 'Ждут ответа' },
  { key: 'decided', label: 'Отвечено' },
  { key: 'all', label: 'Все' },
]

function fmtDate(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit',
  })
}

function InboxIcon() {
  return (
    <svg viewBox="0 0 40 40" fill="none" aria-hidden="true">
      <path
        d="M6 22l4-12h20l4 12v8a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2v-8Zm0 0h8l2 4h8l2-4h8"
        stroke="currentColor" strokeWidth="1.6" strokeLinejoin="round"
      />
    </svg>
  )
}

/**
 * Экран подрядчика: ТЗ, направленные его компании.
 *
 * Роль — демо-контекст (см. identity.ts): компания берётся из актора в
 * заголовке запроса, отдельного входа с паролем в прототипе нет. Список
 * приходит уже отфильтрованным сервером, здесь фильтры только по статусу.
 */
export function ContractorInboxView({ actor }: { actor: ContractorActor }) {
  const [items, setItems] = useState<InboxItem[]>([])
  const [filter, setFilter] = useState<Filter>('pending')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [comments, setComments] = useState<ReviewComment[]>([])
  const [sections, setSections] = useState<{ key: string; title: string }[]>([])
  const [decisionText, setDecisionText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    const r = await getInbox()
    setItems(r.items ?? [])
  }, [])

  // Компания сменилась в переключателе роли — это другой список входящих
  // и другой выбранный документ; ничего от прошлой роли тянуть нельзя.
  useEffect(() => {
    setSelectedId(null)
    setComments([])
    setSections([])
    void refresh()
  }, [actor.id, refresh])

  const selected = useMemo(
    () => items.find((i) => i.assignmentId === selectedId) ?? null,
    [items, selectedId],
  )

  const visible = useMemo(
    () => items.filter((i) => {
      if (filter === 'all') return true
      const pending = i.status === 'sent' || i.status === 'viewed'
      return filter === 'pending' ? pending : !pending
    }),
    [items, filter],
  )

  const pendingCount = items.filter((i) => i.status === 'sent' || i.status === 'viewed').length

  // Открытие карточки: отмечаем просмотр, тянем тред и разделы документа.
  // Разделы берём из /tz/drafts по сохранённому payload — это тот же расчёт,
  // что видит заказчик в конструкторе, поэтому ключи разделов совпадают.
  const open = async (item: InboxItem) => {
    setSelectedId(item.assignmentId)
    setDecisionText('')
    setError(null)
    setComments([])
    setSections([])

    if (item.status === 'sent') {
      await markAssignmentViewed(item.assignmentId)
      void refresh()
    }

    const [thread, document] = await Promise.all([
      getComments(item.tzId),
      getTzDocument(item.tzId).catch(() => null),
    ])
    setComments(thread.items ?? [])

    if (document) {
      try {
        const draft = await draftTz(null, document.templateId, document.payload)
        setSections(
          (draft.sections ?? [])
            .filter((s: any) => s.key && s.title)
            .map((s: any) => ({ key: s.key as string, title: s.title as string })),
        )
      } catch {
        /* без разделов замечание всё равно можно оставить ко всему документу */
      }
    }
  }

  const comment = async (text: string, sectionKey: string | null) => {
    if (!selected) return
    const r = await addComment(selected.tzId, text, sectionKey, selected.assignmentId)
    if (r.items?.length) setComments(r.items)
  }

  const decide = async (decision: 'approved' | 'revision' | 'rejected') => {
    if (!selected || busy) return
    setBusy(true)
    setError(null)
    try {
      const r = await decideAssignment(selected.assignmentId, decision, decisionText.trim())
      if (!r.ok) {
        setError(r.body?.message ?? 'не удалось сохранить решение')
        return
      }
      setDecisionText('')
      const thread = await getComments(selected.tzId)
      setComments(thread.items ?? [])
      await refresh()
    } finally {
      setBusy(false)
    }
  }

  const decided = selected ? selected.status !== 'sent' && selected.status !== 'viewed' : false

  return (
    <div className="documents">
      <h2>
        Входящие ТЗ
        <span className="muted small inbox-actor"> · {actor.name}</span>
      </h2>

      <div className="chips list-filters">
        {FILTERS.map((f) => (
          <button
            key={f.key}
            className={`chip${filter === f.key ? ' on' : ''}`}
            onClick={() => setFilter(f.key)}
          >
            {f.label}
            {f.key === 'pending' && pendingCount > 0 && <span className="count">{pendingCount}</span>}
          </button>
        ))}
      </div>

      <div className="documents-layout">
        <section className="card documents-list">
          <div className="card-title">
            Направленные вам ТЗ
            {visible.length > 0 && <span className="count">{visible.length}</span>}
          </div>
          {visible.length === 0 ? (
            <div className="documents-list-empty">
              <InboxIcon />
              <strong>Входящих нет</strong>
              <p className="muted small">
                Здесь появятся технические задания, которые заказчик направит вашей компании
              </p>
            </div>
          ) : (
            <ul className="plain">
              {visible.map((item) => (
                <li
                  key={item.assignmentId}
                  className={`doc-row${selectedId === item.assignmentId ? ' on' : ''}`}
                  onClick={() => void open(item)}
                >
                  <div className="doc-row-icon"><DocxIcon /></div>
                  <div className="doc-row-body">
                    <div className="doc-row-head">
                      <strong>{item.productName !== '—' ? item.productName : item.templateName}</strong>
                      <span className="muted small">{fmtDate(item.createdAt)}</span>
                    </div>
                    <div className="muted small doc-row-sub">
                      {item.customerName}
                      {item.objectName && item.objectName !== '—' ? ` · ${item.objectName}` : ''}
                    </div>
                    <div className="doc-row-meta">
                      <span className={`badge ${reviewTone(item.status)}`}>
                        {REVIEW_STATUS_LABELS[item.status] ?? item.status}
                      </span>
                      <span className={`badge ${readinessTone(item.readiness)}`}>
                        Готовность {item.readiness}%
                      </span>
                      <span className="badge neutral">в. {item.version}</span>
                      {item.commentsCount > 0 && (
                        <span className="badge neutral">Замечаний: {item.commentsCount}</span>
                      )}
                    </div>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="card documents-preview">
          {!selected && (
            <div className="documents-empty">
              <DocxIcon />
              <strong>Выберите ТЗ слева</strong>
              <p className="muted small">
                Документ откроется здесь — с замечаниями и кнопками согласования
              </p>
            </div>
          )}

          {selected && (
            <>
              <div className="documents-toolbar">
                <a className="btn accent small" href={documentFileUrl(selected.tzId)} download>
                  <DocxIcon />
                  Скачать .docx
                </a>
                <a className="btn accent small" href={documentFileUrl(selected.tzId, 'pdf')} download>
                  <PdfIcon />
                  Скачать .pdf
                </a>
                <span className={`badge ${reviewTone(selected.status)}`}>
                  {REVIEW_STATUS_LABELS[selected.status] ?? selected.status}
                </span>
              </div>

              {selected.note && (
                <div className="review-note">
                  <strong>{selected.customerName}:</strong> {selected.note}
                </div>
              )}

              {/* Решение — над документом: подрядчик приходит сюда именно
                  за ним, а не за чтением с начала до конца. */}
              <div className="review-decision">
                <div className="card-title">Решение по ТЗ</div>
                {decided ? (
                  <p className="muted small">
                    Решение уже вынесено: {REVIEW_STATUS_LABELS[selected.status]?.toLowerCase()}.
                    Новое согласование возможно по следующей версии документа.
                  </p>
                ) : (
                  <>
                    <textarea
                      rows={2}
                      placeholder="Комментарий к решению — обязателен при доработке и отклонении"
                      value={decisionText}
                      onChange={(e) => setDecisionText(e.target.value)}
                    />
                    {error && <div className="banner error">{error}</div>}
                    <div className="review-decision-actions">
                      <button className="btn small primary" disabled={busy} onClick={() => void decide('approved')}>
                        Согласовать
                      </button>
                      <button className="btn small" disabled={busy} onClick={() => void decide('revision')}>
                        На доработку
                      </button>
                      <button className="btn small ghost" disabled={busy} onClick={() => void decide('rejected')}>
                        Отклонить
                      </button>
                    </div>
                  </>
                )}
              </div>

              <div className="review-block">
                <div className="card-title">Замечания</div>
                <ReviewThread
                  comments={comments}
                  myKind="contractor"
                  sections={sections}
                  onSubmit={comment}
                  placeholder="Замечание по разделу или по ТЗ целиком…"
                />
              </div>

              <DocxPreview tzId={selected.tzId} />
            </>
          )}
        </section>
      </div>
    </div>
  )
}
