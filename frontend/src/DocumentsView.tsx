import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  addComment, documentFileUrl, getAssignments, getComments, getCompanies, getTzDocument,
  getTzDocumentVersions, listTzDocuments, sendToContractors,
  type Assignment, type CompanyRef, type ReviewComment, type TzVersionItem,
} from './api'
import { DocxIcon, PdfIcon, readinessTone } from './Blocks'
import { DocxPreview } from './DocxPreview'
import { ReviewThread } from './ReviewThread'
import { REVIEW_STATUS_LABELS, reviewTone } from './sections'
import { useCurrentDocument } from './workspace'

interface DocRow {
  tzId: string
  createdAt: string
  readiness: number
  templateName: string
  productName: string
  objectName: string
  risksCount: number
  status: 'draft' | 'final'
  // Сводный статус согласования по всем направлениям этой версии ТЗ.
  // null — документ никому не направляли.
  reviewStatus: string | null
  commentsCount: number
}

/**
 * Категории заявок — состояния, в которых ТЗ бывает у заказчика. Они не
 * пересекаются, поэтому счётчик на чипсе отвечает на вопрос «сколько сейчас
 * вот таких», а сумма чипсов равна списку целиком.
 *
 * `status` (черновик/готово) и `reviewStatus` (согласование) — разные оси:
 * первая про готовность документа, вторая про процесс. Черновик по
 * определению никому не направлен, поэтому категории и получаются
 * непересекающимися: сначала отсекаем черновики, дальше смотрим согласование.
 */
type Category = 'all' | 'draft' | 'ready' | 'review' | 'returned' | 'approved'

const CATEGORIES: { key: Category; label: string; match: (d: DocRow) => boolean }[] = [
  { key: 'all', label: 'Все', match: () => true },
  { key: 'draft', label: 'Черновики', match: (d) => d.status === 'draft' },
  {
    key: 'ready',
    label: 'Готовы к отправке',
    match: (d) => d.status !== 'draft' && !d.reviewStatus,
  },
  {
    key: 'review',
    label: 'На согласовании',
    match: (d) => d.reviewStatus === 'sent' || d.reviewStatus === 'viewed',
  },
  {
    // «Вернули» — и доработка, и отказ: в обоих случаях подрядчик обязан
    // написать причину, и в обоих случаях мяч на стороне заказчика.
    key: 'returned',
    label: 'Вернули с замечаниями',
    match: (d) => d.reviewStatus === 'revision' || d.reviewStatus === 'rejected',
  },
  { key: 'approved', label: 'Согласованы', match: (d) => d.reviewStatus === 'approved' },
]

const EMPTY_HINTS: Record<Category, string> = {
  all: 'Соберите техническое задание в чате или конструкторе — оно появится здесь',
  draft: 'Незавершённые ТЗ, сохранённые кнопкой «Сохранить черновик», появятся здесь',
  ready: 'Сюда попадают готовые документы, которые ещё никому не направлены',
  review: 'Здесь будут ТЗ, направленные подрядчикам и ожидающие их решения',
  returned: 'Здесь появятся ТЗ, которые подрядчик вернул на доработку или отклонил',
  approved: 'Здесь будут ТЗ, согласованные подрядчиками',
}

function fmtDate(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

// Пока продукт не выбран в диалоге, api отдаёт productName как «—» —
// в списке это выглядит как ошибка, поэтому подменяем на имя шаблона.
function docTitle(doc: DocRow): string {
  if (doc.productName && doc.productName !== '—') return doc.productName
  return doc.templateName || 'Без названия'
}

function FolderIcon() {
  return (
    <svg viewBox="0 0 40 40" fill="none" aria-hidden="true">
      <path
        d="M6 12a2 2 0 0 1 2-2h7l3 3h14a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V12Z"
        stroke="currentColor" strokeWidth="1.6" strokeLinejoin="round"
      />
    </svg>
  )
}

export function DocumentsView({
  onOpenInConstructor,
  focusTzId,
}: {
  // sectionKey — раздел, к которому подрядчик оставил замечание: конструктор
  // подведёт к нужным полям, а не откроет форму с начала.
  onOpenInConstructor: (tzId: string, sectionKey?: string) => void
  // Документ, с которого открыт раздел: приходит из конструктора сразу после
  // формирования ТЗ, чтобы пользователь попадал на свой документ, а не искал
  // его в списке.
  focusTzId?: string | null
}) {
  const [documents, setDocuments] = useState<DocRow[]>([])
  const [category, setCategory] = useState<Category>('all')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [showVersions, setShowVersions] = useState(false)
  const [versions, setVersions] = useState<TzVersionItem[]>([])
  const [versionsLoading, setVersionsLoading] = useState(false)

  // --- согласование
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [comments, setComments] = useState<ReviewComment[]>([])
  const [companies, setCompanies] = useState<CompanyRef[]>([])
  const [sendOpen, setSendOpen] = useState(false)
  const [sendIds, setSendIds] = useState<string[]>([])
  const [sendNote, setSendNote] = useState('')
  const [sendError, setSendError] = useState<string | null>(null)
  const [sending, setSending] = useState(false)

  // Документ текущей заявки: если раздел открыли без явного focusTzId (просто
  // кликнув «Мои заявки»), показываем именно его — на всех экранах одна заявка.
  const currentDocument = useCurrentDocument()
  const previewRef = useRef<HTMLDivElement | null>(null)

  const refresh = useCallback(() => {
    listTzDocuments()
      .then((r) => setDocuments(r.items ?? []))
      .catch(() => undefined)
  }, [])

  useEffect(() => {
    refresh()
    getCompanies().then((r) => setCompanies(r.items ?? [])).catch(() => undefined)
  }, [refresh])

  useEffect(() => {
    const target = focusTzId ?? currentDocument?.tzId
    if (!target) return
    setSelectedId(target)
    setShowVersions(false)
    // Раздел открыт «на конкретном документе» — он обязан быть виден. Какая
    // категория была выбрана в прошлый заход, тут значения не имеет.
    setCategory('all')
    // Только на входе в раздел: дальше пользователь сам выбирает строки, и
    // подменять его выбор текущей заявкой нельзя.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusTzId])

  // Строка нужного документа могла оказаться ниже видимой части списка —
  // подводим её к глазам после того, как список приехал с сервера.
  useEffect(() => {
    if (!selectedId || documents.length === 0) return
    const row = document.getElementById(`doc-row-${selectedId}`)
    row?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
    // Подводим строку к глазам, когда список приехал с сервера, — но не на
    // каждый клик пользователя: он и так видит, куда нажал.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [documents])

  // Согласование выбранного документа: направления по всей цепочке версий и
  // общий тред замечаний.
  const reloadReview = useCallback(async (tzId: string) => {
    const [a, c] = await Promise.all([getAssignments(tzId), getComments(tzId)])
    setAssignments(a.items ?? [])
    setComments(c.items ?? [])
  }, [])

  useEffect(() => {
    if (!selectedId) return
    setAssignments([])
    setComments([])
    void reloadReview(selectedId)
  }, [selectedId, reloadReview])

  // История версий: тащим при открытии панели, показываем все строки
  // с тем же parent_tz_id плюс сам корневой документ.
  useEffect(() => {
    if (!showVersions || !selectedId) return
    setVersionsLoading(true)
    getTzDocumentVersions(selectedId)
      .then((r) => setVersions(r.items ?? []))
      .catch(() => setVersions([]))
      .finally(() => setVersionsLoading(false))
  }, [showVersions, selectedId])

  // Счётчики считаются по всему списку, а не по видимой части: чипсы должны
  // показывать, сколько заявок в каждом состоянии, независимо от выбранного.
  const counts = useMemo(() => {
    const result = {} as Record<Category, number>
    for (const c of CATEGORIES) result[c.key] = documents.filter(c.match).length
    return result
  }, [documents])

  const visible = useMemo(() => {
    const active = CATEGORIES.find((c) => c.key === category) ?? CATEGORIES[0]
    return documents.filter(active.match)
  }, [documents, category])

  const selectedRow = useMemo(
    () => documents.find((d) => d.tzId === selectedId) ?? null,
    [documents, selectedId],
  )

  // Компании, уже получившие эту версию ТЗ: повторно направлять некому.
  const alreadySent = useMemo(
    () => new Set(assignments.filter((a) => a.tzId === selectedId).map((a) => a.companyId)),
    [assignments, selectedId],
  )

  // Модалка направления открывается с уже отмеченными исполнителями из самого
  // ТЗ: чаще всего направляют именно им, а не выбирают заново из полного списка.
  const openSend = async () => {
    if (!selectedId) return
    setSendError(null)
    setSendNote('')
    setSendOpen(true)
    try {
      const doc = await getTzDocument(selectedId)
      const executors: string[] = (doc?.payload?.executors ?? [])
        .map((e: any) => e?.id)
        .filter((id: any) => typeof id === 'string' && !alreadySent.has(id))
      setSendIds(executors)
    } catch {
      setSendIds([])
    }
  }

  const send = async () => {
    if (!selectedId || sendIds.length === 0 || sending) return
    setSending(true)
    setSendError(null)
    try {
      const r = await sendToContractors(selectedId, sendIds, sendNote.trim() || undefined)
      if (!r.ok) {
        setSendError(r.body?.message ?? 'не удалось направить ТЗ')
        return
      }
      setSendOpen(false)
      setSendIds([])
      await reloadReview(selectedId)
      refresh()
    } finally {
      setSending(false)
    }
  }

  const comment = async (text: string, sectionKey: string | null) => {
    if (!selectedId) return
    const r = await addComment(selectedId, text, sectionKey)
    if (r.items?.length) setComments(r.items)
  }

  return (
    <div className="documents" ref={previewRef}>
      <h2>Мои заявки</h2>

      {/* Категории — состояние заявки в процессе: где она сейчас и чей ход.
          Счётчик на чипсе показывает, сколько заявок в этом состоянии; пустые
          категории остаются видны, чтобы список не «прыгал» между заходами. */}
      <div className="chips list-filters">
        {CATEGORIES.map((c) => (
          <button
            key={c.key}
            className={`chip${category === c.key ? ' on' : ''}`}
            onClick={() => setCategory(c.key)}
          >
            {c.label}
            {counts[c.key] > 0 && <span className="count">{counts[c.key]}</span>}
          </button>
        ))}
      </div>

      <div className="documents-layout">
        <section className="card documents-list">
          <div className="card-title">
            {category === 'all'
              ? 'Созданные ТЗ'
              : CATEGORIES.find((c) => c.key === category)?.label}
            {visible.length > 0 && <span className="count">{visible.length}</span>}
          </div>
          {visible.length === 0 ? (
            <div className="documents-list-empty">
              <FolderIcon />
              <strong>
                {documents.length === 0 ? 'ТЗ пока не создавались' : 'В этой категории пусто'}
              </strong>
              <p className="muted small">{EMPTY_HINTS[category]}</p>
            </div>
          ) : (
            <ul className="plain">
              {visible.map((doc) => {
                const tone = readinessTone(doc.readiness)
                return (
                  <li
                    key={doc.tzId}
                    id={`doc-row-${doc.tzId}`}
                    className={`doc-row${selectedId === doc.tzId ? ' on' : ''}`}
                    onClick={() => {
                      setSelectedId(doc.tzId)
                      setShowVersions(false)
                    }}
                  >
                    <div className="doc-row-icon"><DocxIcon /></div>
                    <div className="doc-row-body">
                      <div className="doc-row-head">
                        <strong>{docTitle(doc)}</strong>
                        <span className="muted small">{fmtDate(doc.createdAt)}</span>
                      </div>
                      <div className="muted small doc-row-sub">
                        {doc.templateName}
                        {doc.objectName && doc.objectName !== '—' ? ` · ${doc.objectName}` : ''}
                      </div>
                      <div className="doc-row-meta">
                        <span className={`badge ${doc.status === 'draft' ? 'draft' : 'ok'}`}>
                          {doc.status === 'draft' ? 'Черновик' : 'Готово'}
                        </span>
                        {doc.reviewStatus && (
                          <span className={`badge ${reviewTone(doc.reviewStatus)}`}>
                            {REVIEW_STATUS_LABELS[doc.reviewStatus] ?? doc.reviewStatus}
                          </span>
                        )}
                        <span className={`badge ${tone}`}>Готовность {doc.readiness}%</span>
                        {/* Замечания подрядчика видно в любой категории, а не
                            только среди возвращённых: их могут оставить и по
                            ТЗ, решение по которому ещё не вынесено. */}
                        {doc.commentsCount > 0 && (
                          <span className="badge warn">Замечаний: {doc.commentsCount}</span>
                        )}
                        <span className="badge neutral">
                          {doc.risksCount > 0 ? `Рисков: ${doc.risksCount}` : 'Рисков нет'}
                        </span>
                      </div>
                    </div>
                  </li>
                )
              })}
            </ul>
          )}
        </section>

        <section className="card documents-preview">
          {!selectedId && (
            <div className="documents-empty">
              <DocxIcon />
              <strong>Выберите ТЗ слева</strong>
              <p className="muted small">Документ откроется прямо здесь — со всеми полями и оформлением</p>
            </div>
          )}

          {selectedId && (
            <>
              <div className="documents-toolbar">
                <a className="btn accent small" href={documentFileUrl(selectedId)} download>
                  <DocxIcon />
                  Скачать .docx
                </a>
                <a className="btn accent small" href={documentFileUrl(selectedId, 'pdf')} download>
                  <PdfIcon />
                  Скачать .pdf
                </a>
                <button
                  className="btn small primary"
                  onClick={() => onOpenInConstructor(selectedId)}
                >
                  Открыть в конструкторе
                </button>
                <button
                  className="btn small"
                  disabled={selectedRow?.status === 'draft'}
                  title={
                    selectedRow?.status === 'draft'
                      ? 'Черновик нельзя направить: сформируйте документ'
                      : undefined
                  }
                  onClick={() => void openSend()}
                >
                  Направить подрядчику
                </button>
                <button
                  className="btn small ghost"
                  onClick={() => setShowVersions((v) => !v)}
                >
                  {showVersions ? 'Скрыть версии' : 'История версий'}
                </button>
              </div>

              {sendOpen && (
                <div className="send-panel">
                  <div className="card-title">Направить ТЗ подрядчикам</div>
                  <ul className="checklist inline send-companies">
                    {companies.map((c) => {
                      const sent = alreadySent.has(c.companyId)
                      return (
                        <li key={c.companyId}>
                          <label className={sent ? 'muted' : ''}>
                            <input
                              type="checkbox"
                              disabled={sent}
                              checked={sendIds.includes(c.companyId)}
                              onChange={(e) =>
                                setSendIds((ids) =>
                                  e.target.checked
                                    ? [...ids, c.companyId]
                                    : ids.filter((id) => id !== c.companyId),
                                )
                              }
                            />
                            <span>{c.name}{sent ? ' — уже направлено' : ''}</span>
                          </label>
                        </li>
                      )
                    })}
                  </ul>
                  <textarea
                    rows={2}
                    placeholder="Сопроводительная записка (необязательно)"
                    value={sendNote}
                    onChange={(e) => setSendNote(e.target.value)}
                  />
                  {sendError && <div className="banner error">{sendError}</div>}
                  <div className="send-actions">
                    <button
                      className="btn small primary"
                      disabled={sendIds.length === 0 || sending}
                      onClick={() => void send()}
                    >
                      {sending ? 'Отправляю…' : `Направить (${sendIds.length})`}
                    </button>
                    <button className="btn small ghost" onClick={() => setSendOpen(false)}>
                      Отмена
                    </button>
                  </div>
                </div>
              )}

              {assignments.length > 0 && (
                <div className="review-block">
                  <div className="card-title">Согласование</div>
                  <ul className="plain assignment-list">
                    {assignments.map((a) => (
                      <li key={a.assignmentId} className="assignment-row">
                        <strong>{a.companyName}</strong>
                        <span className={`badge ${reviewTone(a.status)}`}>
                          {REVIEW_STATUS_LABELS[a.status] ?? a.status}
                        </span>
                        <span className="badge neutral">в. {a.version}</span>
                        <span className="muted small">
                          {a.decidedAt
                            ? `решение ${fmtDate(a.decidedAt)}`
                            : a.viewedAt
                              ? `просмотрено ${fmtDate(a.viewedAt)}`
                              : `направлено ${fmtDate(a.createdAt)}`}
                        </span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}

              {(assignments.length > 0 || comments.length > 0) && (
                <div className="review-block">
                  <div className="card-title">Замечания подрядчиков</div>
                  <ReviewThread
                    comments={comments}
                    myKind="customer"
                    onSubmit={comment}
                    onFixSection={(sectionKey) => onOpenInConstructor(selectedId, sectionKey)}
                    placeholder="Ответить подрядчику…"
                  />
                </div>
              )}

              {showVersions && (
                <div className="documents-versions">
                  {versionsLoading ? (
                    <p className="muted small">Загружаю…</p>
                  ) : versions.length === 0 ? (
                    <p className="muted small">Версий нет</p>
                  ) : (
                    <ul className="plain">
                      {versions.map((v) => (
                        <li
                          key={v.tzId}
                          className={`version-row${v.tzId === selectedId ? ' on' : ''}`}
                          onClick={() => {
                            setSelectedId(v.tzId)
                            setShowVersions(false)
                          }}
                        >
                          <span className="version-num">v{v.version}</span>
                          <span className="muted small">{fmtDate(v.createdAt)}</span>
                          {v.status === 'draft' && <span className="badge draft">Черновик</span>}
                          <span className={`badge ${readinessTone(v.readiness)}`}>Готовность {v.readiness}%</span>
                          <span className="version-files">
                            <a
                              className="btn small ghost"
                              href={v.downloadUrl}
                              download
                              onClick={(e) => e.stopPropagation()}
                            >
                              .docx
                            </a>
                            <a
                              className="btn small ghost"
                              href={v.pdfUrl ?? documentFileUrl(v.tzId, 'pdf')}
                              download
                              onClick={(e) => e.stopPropagation()}
                            >
                              .pdf
                            </a>
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}

              <DocxPreview tzId={selectedId} />
            </>
          )}
        </section>
      </div>
    </div>
  )
}
