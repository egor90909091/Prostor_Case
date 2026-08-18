import { useCallback, useEffect, useMemo, useState } from 'react'
import { createTzDocument, draftTz, getStages, getTemplates } from './api'
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

export function ConstructorView({ sessionId }: { sessionId: string | null }) {
  const [state, setState] = useState<any>({ flags: {}, stages: [], executors: [], period: {} })
  const [templates, setTemplates] = useState<{ id: string; name: string }[]>([])
  const [availableStages, setAvailableStages] = useState<Stage[]>([])
  const [draft, setDraft] = useState<DraftResponse | null>(null)
  const [result, setResult] = useState<{ tzId: string; downloadUrl: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  // Предзаполнение из диалога — это и есть интеграция Кейса 2 с Кейсом 1
  useEffect(() => {
    getTemplates()
      .then((r) => setTemplates(r.items ?? []))
      .catch(() => setError('Сервис ТЗ недоступен'))

    if (!sessionId) return
    fetch(`/api/v1/chat/sessions/${sessionId}/state`)
      .then((r) => (r.ok ? r.json() : null))
      .then((s) => {
        if (!s) return
        setState({ flags: {}, stages: [], executors: [], period: {}, ...s })
        if (s.productId) getStages(s.productId).then((r) => setAvailableStages(r.items ?? []))
      })
      .catch(() => undefined)
  }, [sessionId])

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

  const update = (patch: any) => setState((prev: any) => ({ ...prev, ...patch }))

  const toggleStage = (stage: Stage) =>
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

  const generate = async () => {
    setSaving(true)
    setError(null)
    const response = await createTzDocument(sessionId, templateId, state)
    setSaving(false)
    if (response.ok) {
      setResult({ tzId: response.body.tzId, downloadUrl: response.body.downloadUrl })
    } else if (response.status === 422) {
      setError('ТЗ не готово к выгрузке: устраните критичные риски')
    } else {
      setError('Не удалось сформировать документ')
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
        {!sessionId && (
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

        {availableStages.length > 0 && (
          <section className="card">
            <div className="card-title">Этапы работ</div>
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
          </section>
        )}

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
