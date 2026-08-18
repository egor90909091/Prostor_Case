import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { renderAsync } from 'docx-preview'
import { createTzDocument, draftTz, getStages, getTzDocument, getTemplates } from './api'
import { Readiness, severityLabel } from './Blocks'

interface Stage {
  key: string
  name: string
  usedCount?: number
  medianDays?: number | null
  documentation?: string | null
}

interface Risk {
  code: string
  severity: string
  title: string
  recommendation: string
}

interface FieldStatus {
  key: string
  title: string
  weight: number
  blocking: boolean
  filled: boolean
  hint?: string
}

interface Section {
  key: string
  title: string
  body?: string | null
  required: boolean
  filled: boolean
}

interface DraftResponse {
  templateId: string
  templateName: string
  readiness: number
  canGenerate: boolean
  recommendation: string
  typicalDays?: number | null
  fields: FieldStatus[]
  risks: Risk[]
  sections: Section[]
}

// Поля, у которых в конструкторе уже есть отдельный специализированный
// виджет (даты, чек-листы этапов) — их не нужно повторно рисовать как
// обычный текстовый инпут, даже если шаблон включает их в required_fields.
const DEDICATED_WIDGET_KEYS = new Set(['period', 'stages', 'operations', 'executors'])

// Ключ поля в required_fields не всегда совпадает с именем свойства в
// state (Drafting.cs на бэкенде читает часть полей по другому имени) —
// единственное расхождение сегодня: source_data → sourceData.
const STATE_KEY_OVERRIDES: Record<string, string> = { source_data: 'sourceData' }

// Разделы, которые обычно требуют развёрнутого текста, а не одной строки
const TEXTAREA_FIELD_KEYS = new Set([
  'purpose', 'perimeter', 'source_data', 'documentation', 'acceptance', 'other', 'kpi', 'conditions',
])

// Конструктор не имеет бэкенд-сессии (как чат), поэтому состояние формы
// хранится в localStorage: переключение вкладок и перезагрузка страницы
// не теряют введённые данные. Ключ — const, значение — сериализованный
// state. При редактировании существующего ТЗ (editTzId) сохранение
// пропускается: state грузится из БД и не должен перетирать черновик.
const STATE_STORAGE_KEY = 'prostor.constructor.state'
const INITIAL_STATE = { flags: {}, stages: [], executors: [], period: {} }

export function ConstructorView({
  sessionId,
  editTzId,
  onNavigateToDocuments,
}: {
  sessionId: string | null
  editTzId?: string | null
  onNavigateToDocuments?: () => void
}) {
  const [state, setState] = useState<any>(() => {
    // При редактировании существующего ТЗ — стартуем с пустого state,
    // он загрузится из БД в эффекте ниже. Иначе — пробуем восстановить
    // из localStorage, чтобы переключение вкладок и F5 не стирали форму.
    if (editTzId) return INITIAL_STATE
    try {
      const stored = localStorage.getItem(STATE_STORAGE_KEY)
      if (stored) return { ...INITIAL_STATE, ...JSON.parse(stored) }
    } catch { /* повреждённый JSON — игнорируем */ }
    return INITIAL_STATE
  })
  const [templates, setTemplates] = useState<{ id: string; name: string }[]>([])
  const [availableStages, setAvailableStages] = useState<Stage[]>([])
  const [draft, setDraft] = useState<DraftResponse | null>(null)
  const [result, setResult] = useState<{ tzId: string; downloadUrl: string; version: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  // Рендер готового .docx в браузере: после сохранения тащим бинарь по
  // downloadUrl и просим docx-preview отрисовать WordprocessingML в DOM.
  // Позволяет пользователю увидеть финальный документ, не уходя в «Мои
  // заявки» и не скачивая файл.
  const [rendered, setRendered] = useState(false)
  const [renderError, setRenderError] = useState<string | null>(null)
  const previewHostRef = useRef<HTMLDivElement | null>(null)
  // Метаданные редактируемого документа: показываются бейджем в шапке,
  // а parentTzId прокидывается в createTzDocument, чтобы новая строка
  // стала версией, а не отдельным ТЗ.
  const [editing, setEditing] = useState<{
    tzId: string
    version: number
    createdAt: string
    parentTzId?: string | null
  } | null>(null)

  // Предзаполнение конструктора идёт из одного из трёх источников
  // (приоритет сверху вниз):
  //   * editTzId — существующее ТЗ из «Мои заявки»: тащим payload из БД.
  //   * sessionId — диалог с агентом: state приходит из chat.session.
  //     Но только если в localStorage ещё нет сохранённого состояния —
  //     иначе пользователь уже редактировал форму, и чат-данные не должны
  //     её перетирать при переключении вкладок.
  //   * localStorage — ранее сохранённое состояние формы.
  // Шаблоны нужны всегда — без них конструктор не знает, какой набор
  // полей рисовать.
  useEffect(() => {
    getTemplates()
      .then((r) => setTemplates(r.items ?? []))
      .catch(() => setError('Сервис ТЗ недоступен'))

    if (editTzId) {
      getTzDocument(editTzId)
        .then((doc) => {
          // payload хранит исходный state плюс служебные поля (sections,
          // readiness) — служебные убираем, они пересчитаются на draft.
          const payload = doc.payload ?? {}
          const { sections: _sections, readiness: _readiness, ...rest } = payload
          void _sections
          void _readiness
          setState({ ...INITIAL_STATE, ...rest })
          setEditing({
            tzId: doc.tzId,
            version: doc.version,
            createdAt: doc.createdAt,
            parentTzId: doc.parentTzId,
          })
          if (doc.productId) {
            getStages(doc.productId)
              .then((r) => setAvailableStages(r.items ?? []))
              .catch(() => undefined)
          }
        })
        .catch(() => setError('Не удалось загрузить ТЗ'))
      return
    }

    // Если в localStorage уже есть сохранённое состояние — не грузим
    // из чата: пользователь уже работал с формой, и его правки важнее
    // данных диалога. Чат-данные грузятся только при первом открытии
    // конструктора (когда localStorage пуст).
    const hasStored = !!localStorage.getItem(STATE_STORAGE_KEY)
    if (!sessionId || hasStored) return

    fetch(`/api/v1/chat/sessions/${sessionId}/state`)
      .then((r) => (r.ok ? r.json() : null))
      .then((s) => {
        if (!s) return
        setState({ ...INITIAL_STATE, ...s })
        if (s.productId) getStages(s.productId).then((r) => setAvailableStages(r.items ?? []))
      })
      .catch(() => undefined)
  }, [sessionId, editTzId])

  // Сохраняем состояние формы в localStorage на каждое изменение.
  // Пропускаем только режим редактирования существующего ТЗ — там
  // state грузится из БД и не должен перетирать черновик пользователя.
  useEffect(() => {
    if (editTzId) return
    try {
      localStorage.setItem(STATE_STORAGE_KEY, JSON.stringify(state))
    } catch { /* переполнение storage — не критично */ }
  }, [state, editTzId])

  // Подгружаем этапы услуги при появлении productId в state — нужно,
  // чтобы чек-лист этапов не был пустым после восстановления из localStorage
  // (тогда чат-эффект выше не выполняется и stages сами не подтянутся).
  useEffect(() => {
    const productId = state.productId
    if (!productId || editTzId) return
    // Не перезапрашиваем, если уже есть — например, после загрузки из чата.
    if (availableStages.length > 0) return
    getStages(productId)
      .then((r) => setAvailableStages(r.items ?? []))
      .catch(() => undefined)
  }, [state.productId, editTzId, availableStages.length])

  // Рендер готового .docx в браузере после сохранения. Та же связка
  // docx-preview, что и на странице «Мои заявки»: бинарь приходит по
  // downloadUrl, отрисовывается в previewHostRef. Сбрасываем состояние
  // рендера при начале нового сохранения, чтобы не показывать старый
  // документ, пока грузится новый.
  useEffect(() => {
    if (!result?.tzId) return
    let cancelled = false
    setRendered(false)
    setRenderError(null)

    const render = async () => {
      try {
        const res = await fetch(result.downloadUrl)
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
        if (!cancelled) setRendered(true)
      } catch (e) {
        if (!cancelled) setRenderError(e instanceof Error ? e.message : 'не удалось отрисовать документ')
      }
    }

    void render()
    return () => { cancelled = true }
  }, [result])

  const templateId: string = state.templateId ?? 'tpl-generic'

  const refresh = useCallback(async (next: any) => {
    try {
      const response = await draftTz(sessionId, next.templateId ?? 'tpl-generic', next)
      setDraft(response)
      setError(null)
    } catch {
      setError('Не удалось пересчитать готовность')
    }
  }, [sessionId])

  // Пересчёт готовности на каждое изменение: операция дешёвая и без побочных эффектов
  useEffect(() => {
    const timer = setTimeout(() => void refresh(state), 250)
    return () => clearTimeout(timer)
  }, [state, refresh])

  // Любая правка формы делает ранее сохранённый документ устаревшим:
  // сбрасываем result, чтобы финальный рендер не висел и не вводил
  // в заблуждение. Пользователь пересохранит — увидит свежую версию.
  const update = (patch: any) => {
    setResult(null)
    setState((prev: any) => ({ ...prev, ...patch }))
  }

  const toggleStage = (stage: Stage) => {
    setResult(null)
    setState((prev: any) => {
      const chosen: any[] = prev.stages ?? []
      const exists = chosen.some((s) => s.key === stage.key)
      return {
        ...prev,
        stages: exists
          ? chosen.filter((s) => s.key !== stage.key)
          : [
              ...chosen,
              {
                key: stage.key,
                name: stage.name,
                days: stage.medianDays ?? null,
                documentation: stage.documentation ?? null,
              },
            ],
      }
    })
  }

  // Ручной ввод этапов — для услуг, по которым в каталоге нет истории
  // расчётов (getStages вернул пустой список). Без этого раздел «Этапы
  // работ» был бы просто пустым, а поле — блокирующим и невыполнимым.
  const [customStageName, setCustomStageName] = useState('')
  const [customStageDays, setCustomStageDays] = useState('')
  const [customStageDocs, setCustomStageDocs] = useState('')

  const addCustomStage = () => {
    const name = customStageName.trim()
    if (!name) return
    setResult(null)
    setState((prev: any) => ({
      ...prev,
      stages: [
        ...(prev.stages ?? []),
        {
          key: `custom-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
          name,
          days: customStageDays ? Number(customStageDays) : null,
          documentation: customStageDocs.trim() || null,
        },
      ],
    }))
    setCustomStageName('')
    setCustomStageDays('')
    setCustomStageDocs('')
  }

  const removeStage = (key: string) => {
    setResult(null)
    setState((prev: any) => ({
      ...prev,
      stages: (prev.stages ?? []).filter((s: any) => s.key !== key),
    }))
  }

  const generate = async () => {
    setSaving(true)
    setError(null)
    // В режиме редактирования сохраняем как новую версию. parentTzId
    // всегда указывает на КОРЕНЬ цепочки (а не на текущую версию): если
    // редактируется v2, корень — это v1 (editing.parentTzId), и новая
    // v3 тоже ссылается на v1. Так все версии группируются под одним
    // root и попадают в один список через GetDocumentVersionsAsync.
    // Для корневого документа (parentTzId = null) корень — он сам.
    const rootTzId = editing ? (editing.parentTzId ?? editing.tzId) : null
    const response = await createTzDocument(sessionId, templateId, state, false, rootTzId)
    setSaving(false)
    if (response.ok) {
      setResult({
        tzId: response.body.tzId,
        downloadUrl: response.body.downloadUrl,
        version: response.body.version,
      })
      if (rootTzId) {
        setEditing({
          tzId: response.body.tzId,
          version: response.body.version,
          createdAt: new Date().toISOString(),
          // Корень цепочки не меняется при следующих сохранениях —
          // новая версия тоже ссылается на него.
          parentTzId: rootTzId,
        })
      }
    } else if (response.status === 422) {
      setError('ТЗ не готово к выгрузке: устраните критичные риски')
    } else if (response.status === 500) {
      // Бэкенд мог вернуть человекочитаемую подсказку (например, про
      // ненакаченную миграцию) — показываем её, а не общий текст.
      const errBody = response.body as any
      setError(errBody?.hint ?? errBody?.message ?? 'Ошибка сервера')
    } else {
      const errBody = response.body as any
      setError(errBody?.message ?? 'Не удалось сформировать документ')
    }
  }

  const chosenKeys = useMemo(
    () => new Set((state.stages ?? []).map((s: any) => s.key)),
    [state.stages],
  )

  // Набор текстовых полей зависит от выбранного шаблона: тип ТЗ на
  // «Сопровождение инженерных работ» не покажет «Периметр работ», а тип
  // на ПТД/ПЗ добавит «Контрольные сроки (КПЭ)» — состав приходит из
  // required_fields шаблона (tz.template в БД), а не зашит на фронте.
  const textFields = useMemo(
    () => (draft?.fields ?? []).filter((f) => !DEDICATED_WIDGET_KEYS.has(f.key)),
    [draft?.fields],
  )

  return (
    <div className="constructor">
      <div className="col main">
        <h2>Конструктор технического задания</h2>
        {editing && (
          <div className="banner">
            Редактируется ТЗ v{editing.version} от {new Date(editing.createdAt).toLocaleString('ru-RU')}.
            Сохранение создаст новую версию этого документа.
          </div>
        )}
        {!sessionId && !editing && (
          <div className="banner">
            Откройте конструктор из чата, чтобы поля заполнились автоматически.
            Сейчас доступно ручное заполнение.
          </div>
        )}
        {error && <div className="banner error">{error}</div>}

        <section className="card">
          <div className="card-title">Тип технического задания</div>
          <select value={templateId} onChange={(e) => update({ templateId: e.target.value })}>
            {templates.map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
          {state.productName && <div className="card-sub">Услуга из диалога: {state.productName}</div>}
        </section>

        <section className="card">
          <div className="card-title">Сроки выполнения</div>
          <div className="row">
            <label>
              с{' '}
              <input
                type="date"
                value={state.period?.from ?? ''}
                onChange={(e) => update({ period: { ...state.period, from: e.target.value } })}
              />
            </label>
            <label>
              по{' '}
              <input
                type="date"
                value={state.period?.to ?? ''}
                onChange={(e) => update({ period: { ...state.period, to: e.target.value } })}
              />
            </label>
            {draft?.typicalDays ? (
              <span className="muted">типовой срок: {draft.typicalDays} дн.</span>
            ) : null}
          </div>
        </section>

        <section className="card">
          <div className="card-title">Условия выполнения работ</div>
          <ul className="checklist inline">
            {[
              { key: 'model3d', title: 'Построение 3D геологической модели' },
              { key: 'subcontract', title: 'Допускается субподряд' },
              { key: 'urgent', title: 'Срочное выполнение' },
            ].map((flag) => (
              <li key={flag.key}>
                <label>
                  <input
                    type="checkbox"
                    checked={!!state.flags?.[flag.key]}
                    onChange={(e) =>
                      update({ flags: { ...state.flags, [flag.key]: e.target.checked } })
                    }
                  />
                  <span>{flag.title}</span>
                </label>
              </li>
            ))}
          </ul>
        </section>

        <section className="card">
          <div className="card-title">Этапы работ</div>
          {availableStages.length > 0 ? (
            <>
              <div className="card-sub">Структура собрана из реальных расчётов по выбранной услуге</div>
              <ul className="checklist">
                {availableStages.map((stage) => (
                  <li key={stage.key}>
                    <label>
                      <input
                        type="checkbox"
                        checked={chosenKeys.has(stage.key)}
                        onChange={() => toggleStage(stage)}
                      />
                      <span>
                        {stage.name}
                        <span className="muted">
                          {stage.medianDays ? ` · ${stage.medianDays} дн.` : ''}
                        </span>
                        {stage.documentation && (
                          <div className="muted small">Результат: {stage.documentation}</div>
                        )}
                      </span>
                    </label>
                  </li>
                ))}
              </ul>
            </>
          ) : (
            <>
              <div className="card-sub">
                Для этой услуги в системе нет типового состава этапов — впишите их вручную.
              </div>
              {(state.stages ?? []).length > 0 && (
                <ul className="checklist">
                  {(state.stages ?? []).map((stage: any) => (
                    <li key={stage.key} className="row stage-row">
                      <span>
                        {stage.name}
                        {stage.days ? <span className="muted"> · {stage.days} дн.</span> : null}
                        {stage.documentation && (
                          <div className="muted small">Результат: {stage.documentation}</div>
                        )}
                      </span>
                      <button type="button" className="btn ghost small" onClick={() => removeStage(stage.key)}>
                        Удалить
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              <div className="field">
                <label>Название этапа</label>
                <input
                  placeholder="Например: Построение геологической модели"
                  value={customStageName}
                  onChange={(e) => setCustomStageName(e.target.value)}
                />
              </div>
              <div className="row">
                <label>
                  Срок, дн.{' '}
                  <input
                    type="number"
                    min={0}
                    placeholder="—"
                    value={customStageDays}
                    onChange={(e) => setCustomStageDays(e.target.value)}
                    style={{ width: 80 }}
                  />
                </label>
              </div>
              <div className="field">
                <label>Отчётные материалы этапа (необязательно)</label>
                <input
                  placeholder="Например: информационный отчёт в PDF"
                  value={customStageDocs}
                  onChange={(e) => setCustomStageDocs(e.target.value)}
                />
              </div>
              <button type="button" className="btn small" disabled={!customStageName.trim()} onClick={addCustomStage}>
                + Добавить этап
              </button>
            </>
          )}
        </section>

        <section className="card">
          <div className="card-title">Ключевые поля</div>
          {textFields.map((field) => {
            const stateKey = STATE_KEY_OVERRIDES[field.key] ?? field.key
            return (
              <div className="field" key={field.key}>
                <label>{field.title}{field.blocking && <span className="req"> *</span>}</label>
                {TEXTAREA_FIELD_KEYS.has(field.key) ? (
                  <textarea
                    rows={2}
                    placeholder={field.hint ?? ''}
                    value={state[stateKey] ?? ''}
                    onChange={(e) => update({ [stateKey]: e.target.value })}
                  />
                ) : (
                  <input
                    placeholder={field.hint ?? ''}
                    value={state[stateKey] ?? ''}
                    onChange={(e) => update({ [stateKey]: e.target.value })}
                  />
                )}
              </div>
            )
          })}
          {/* Безвесовое поле-катчол: не влияет на готовность ни у одного
              шаблона (нет в required_fields), но раздел «Иные условия»
              есть в канонической форме ТЗ, поэтому доступен всегда. */}
          <div className="field">
            <label>Иные условия</label>
            <textarea
              rows={2}
              placeholder="Дополнительные требования"
              value={state.other ?? ''}
              onChange={(e) => update({ other: e.target.value })}
            />
          </div>
        </section>

        {draft && (
          <section className="card">
            <div className="card-title">Предпросмотр документа</div>
            {draft.sections
              .filter((s) => s.filled || s.required)
              .map((section, index) => (
                <div className="preview-section" key={section.key}>
                  <h4>{index + 1}. {section.title}</h4>
                  <p className={section.filled ? '' : 'gap'}>
                    {section.body ?? 'раздел не заполнен'}
                  </p>
                </div>
              ))}
          </section>
        )}

        {result && (
          <section className="card">
            <div className="card-title">
              Готовый документ{result.version ? ` · v${result.version}` : ''}
            </div>
            <div className="card-sub">
              Так документ будет выглядеть в Word. Можно скачать{onNavigateToDocuments ? ' или перейти к списку заявок.' : '.'}
            </div>
            {onNavigateToDocuments && (
              <button type="button" className="btn small ghost" onClick={onNavigateToDocuments}>
                К списку заявок
              </button>
            )}
            {renderError && (
              <div className="banner error">
                Не удалось отрисовать документ: {renderError}.
                <a href={result.downloadUrl}>Скачать .docx</a>
              </div>
            )}
            {!rendered && !renderError && <p className="muted small">Рендерю документ…</p>}
            <div className="docx-host" ref={previewHostRef} />
          </section>
        )}
      </div>

      <aside className="col side sticky">
        <h3>Проверка качества</h3>
        <Readiness value={draft?.readiness ?? 0} />
        {draft?.recommendation && <p className="rec">{draft.recommendation}</p>}

        <div className="weights">
          {(draft?.fields ?? []).map((field) => (
            <div className={`weight ${field.filled ? 'on' : ''}`} key={field.key}>
              <span>{field.title}</span>
              <span className="muted">{field.weight}</span>
            </div>
          ))}
        </div>

        <h4>Риски</h4>
        {draft?.risks.length === 0 && <p className="muted">Риски не выявлены</p>}
        <ul className="risks">
          {(draft?.risks ?? []).map((risk) => (
            <li className={`risk ${risk.severity}`} key={risk.code}>
              <span className="sev">{severityLabel(risk.severity)}</span>
              <div>
                <strong>{risk.title}</strong>
                <div className="muted">{risk.recommendation}</div>
              </div>
            </li>
          ))}
        </ul>

        <button
          className="btn primary wide"
          disabled={saving || !draft?.canGenerate}
          onClick={() => void generate()}
        >
          {saving ? 'Формирую…' : 'Сформировать документ'}
        </button>
        {!draft?.canGenerate && (
          <p className="muted small">Кнопка разблокируется, когда не останется критичных рисков.</p>
        )}

        {result && (
          <div className="card success">
            <strong>ТЗ сформировано</strong>
            <a className="btn" href={result.downloadUrl}>Скачать .docx</a>
          </div>
        )}
      </aside>
    </div>
  )
}
