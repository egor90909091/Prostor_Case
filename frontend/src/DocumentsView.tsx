import { useCallback, useEffect, useRef, useState } from 'react'
import { renderAsync } from 'docx-preview'
import { getTzDocumentVersions, listTzDocuments, type TzVersionItem } from './api'
import { DocxIcon } from './Blocks'

interface DocRow {
  tzId: string
  createdAt: string
  readiness: number
  templateName: string
  productName: string
  objectName: string
  risksCount: number
  status: 'draft' | 'final'
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

export function DocumentsView({ onOpenInConstructor }: { onOpenInConstructor: (tzId: string) => void }) {
  const [documents, setDocuments] = useState<DocRow[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [renderError, setRenderError] = useState<string | null>(null)
  const [showVersions, setShowVersions] = useState(false)
  const [versions, setVersions] = useState<TzVersionItem[]>([])
  const [versionsLoading, setVersionsLoading] = useState(false)

  const previewRef = useRef<HTMLDivElement | null>(null)
  const previewHostRef = useRef<HTMLDivElement | null>(null)

  const refresh = useCallback(() => {
    listTzDocuments()
      .then((r) => setDocuments(r.items ?? []))
      .catch(() => undefined)
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  // Рендер выбранного .docx прямо в браузере через docx-preview:
  // тащим бинарь по тому же URL, что и для скачивания, и просим
  // библиотеку отрисовать WordprocessingML в DOM. Результат похож
  // на то, что показывает Word, — с полями, стилями и пагинацией.
  useEffect(() => {
    if (!selectedId) return
    let cancelled = false

    const render = async () => {
      setLoading(true)
      setRenderError(null)
      try {
        const res = await fetch(`/api/v1/tz/documents/${selectedId}/file`)
        if (!res.ok) throw new Error(`HTTP ${res.status}`)
        const blob = await res.blob()

        if (cancelled || !previewHostRef.current) return
        previewHostRef.current.innerHTML = ''
        await renderAsync(blob, previewHostRef.current, undefined, {
          className: 'docx-preview',
          inWrapper: true,
          breakPages: true,
          ignoreWidth: false,
          ignoreHeight: false,
          renderHeaders: true,
          renderFooters: true,
        })
      } catch (e) {
        if (!cancelled) setRenderError(e instanceof Error ? e.message : 'не удалось отрисовать документ')
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void render()
    return () => {
      cancelled = true
    }
  }, [selectedId])

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

  return (
    <div className="documents" ref={previewRef}>
      <h2>Мои заявки</h2>

      <div className="documents-layout">
        <section className="card documents-list">
          <div className="card-title">Созданные ТЗ</div>
          {documents.length === 0 ? (
            <p className="muted">ТЗ пока не создавались</p>
          ) : (
            <ul className="plain">
              {documents.map((doc) => (
                <li
                  key={doc.tzId}
                  className={`doc-row${selectedId === doc.tzId ? ' on' : ''}`}
                  onClick={() => {
                    setSelectedId(doc.tzId)
                    setShowVersions(false)
                  }}
                >
                  <div className="doc-row-head">
                    <strong>{doc.productName}</strong>
                    <span className="muted small">{fmtDate(doc.createdAt)}</span>
                  </div>
                  <div className="muted small">
                    {doc.templateName}
                    {doc.objectName && doc.objectName !== '—' ? ` · ${doc.objectName}` : ''}
                  </div>
                  <div className="doc-row-meta">
                    {doc.status === 'draft' && <span className="badge draft">Черновик</span>}
                    <span className="badge readiness">Готовность {doc.readiness}%</span>
                    <span className="badge">Рисков: {doc.risksCount}</span>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="card documents-preview">
          {!selectedId && (
            <div className="documents-empty">
              <p className="muted">Выберите ТЗ слева, чтобы просмотреть документ</p>
            </div>
          )}

          {selectedId && (
            <>
              <div className="documents-toolbar">
                <a
                  className="btn accent small"
                  href={`/api/v1/tz/documents/${selectedId}/file`}
                  download
                >
                  <DocxIcon />
                  Скачать .docx
                </a>
                <button
                  className="btn small primary"
                  onClick={() => onOpenInConstructor(selectedId)}
                >
                  Открыть в конструкторе
                </button>
                <button
                  className="btn small ghost"
                  onClick={() => setShowVersions((v) => !v)}
                >
                  {showVersions ? 'Скрыть версии' : 'История версий'}
                </button>
              </div>

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
                          <span className="badge">Готовность {v.readiness}%</span>
                          <a
                            className="btn small ghost"
                            href={v.downloadUrl}
                            download
                            onClick={(e) => e.stopPropagation()}
                          >
                            Скачать
                          </a>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}

              {loading && <p className="muted small">Рендерю документ…</p>}
              {renderError && (
                <div className="banner error">
                  Не удалось отрисовать документ: {renderError}.
                  <a href={`/api/v1/tz/documents/${selectedId}/file`}>Скачать .docx</a>
                </div>
              )}

              <div className="docx-host" ref={previewHostRef} />
            </>
          )}
        </section>
      </div>
    </div>
  )
}
