import { useEffect, useRef, useState } from 'react'
import { renderAsync } from 'docx-preview'
import { documentFileUrl } from './api'

/**
 * Рендер .docx прямо в браузере: тащим бинарь по тому же URL, что и для
 * скачивания, и просим docx-preview отрисовать WordprocessingML в DOM.
 * Результат похож на то, что показывает Word, — с полями, стилями и
 * пагинацией.
 *
 * Вынесено из DocumentsView отдельным компонентом, потому что тот же
 * документ смотрит и подрядчик в своих входящих: два экрана, один рендер.
 */
export function DocxPreview({ tzId }: { tzId: string }) {
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const hostRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    let cancelled = false

    const render = async () => {
      setLoading(true)
      setError(null)
      try {
        const res = await fetch(documentFileUrl(tzId))
        if (!res.ok) throw new Error(`HTTP ${res.status}`)
        const blob = await res.blob()

        if (cancelled || !hostRef.current) return
        hostRef.current.innerHTML = ''
        await renderAsync(blob, hostRef.current, undefined, {
          className: 'docx-preview',
          inWrapper: true,
          breakPages: true,
          ignoreWidth: false,
          ignoreHeight: false,
          renderHeaders: true,
          renderFooters: true,
        })
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'не удалось отрисовать документ')
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void render()
    return () => {
      cancelled = true
    }
  }, [tzId])

  return (
    <>
      {loading && <p className="muted small">Рендерю документ…</p>}
      {error && (
        <div className="banner error">
          Не удалось отрисовать документ: {error}.
          <a href={documentFileUrl(tzId)}>Скачать .docx</a>
        </div>
      )}
      <div className="docx-host" ref={hostRef} />
    </>
  )
}
