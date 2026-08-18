import { useState } from 'react'
import type { Block, BlockItem, TurnAction } from './api'

interface Props {
  block: Block
  disabled: boolean
  onAction: (action: TurnAction) => void
  onOpenConstructor: () => void
}

export function BlockView({ block, disabled, onAction, onOpenConstructor }: Props) {
  switch (block.type) {
    case 'text':
      return <p className="msg-text">{block.text}</p>
    case 'product_list':
      return <ProductList block={block} disabled={disabled} onAction={onAction} />
    case 'company_list':
      return <CompanyList block={block} />
    case 'clarify':
      return <Clarify block={block} />
    case 'period_request':
      return <PeriodRequest block={block} disabled={disabled} onAction={onAction} />
    case 'executor_list':
      return <ExecutorList block={block} disabled={disabled} onAction={onAction} />
    case 'stage_list':
      return <StageList block={block} disabled={disabled} onAction={onAction} />
    case 'operation_list':
      return <OperationList block={block} disabled={disabled} onAction={onAction} />
    case 'conditions':
      return <Conditions block={block} disabled={disabled} onAction={onAction} />
    case 'related_products':
      return <SimpleList block={block} icon="↔" />
    case 'similar_calcs':
      return <SimilarCalcs block={block} />
    case 'recommendations':
      return <Recommendations block={block} />
    case 'tz_gaps':
      return <TzGaps block={block} />
    case 'actions':
      return (
        <div className="actions">
          {(block.items ?? []).map((item, i) => (
            <button key={i} className="btn primary" disabled={disabled} onClick={onOpenConstructor}>
              {item.title}
            </button>
          ))}
        </div>
      )
    case 'tz_ready':
      return (
        <div className="card success">
          <strong>{block.text}</strong>
          {block.meta?.tzId && (
            <a className="btn" href={`/api/v1/tz/documents/${block.meta.tzId}/file`}>
              Скачать .docx
            </a>
          )}
        </div>
      )
    case 'action':
      return <p className="msg-action">{describeAction(block)}</p>
    default:
      return <p className="msg-text">{block.text ?? block.type}</p>
  }
}

function describeAction(block: Block): string {
  const map: Record<string, string> = {
    select_product: 'выбрана услуга',
    set_period: 'указаны сроки',
    select_executors: 'выбраны исполнители',
    select_stages: 'выбраны этапы',
    select_operations: 'выбраны операции',
    set_flag: 'изменено условие работ',
    set_field: 'заполнено поле',
    tz_created: 'сформировано ТЗ',
    reset: 'начать заново',
  }
  return map[block.text ?? ''] ?? (block.text ?? '')
}

// ------------------------------------------------------------------ услуги
function ProductList({ block, disabled, onAction }: Omit<Props, 'onOpenConstructor'>) {
  // tentative приходит с сервера, когда выдача не прошла проверку уверенности
  // (TurnPipeline.Assess). Подавать такие результаты как находку нельзя —
  // именно так пять нерелевантных услуг выглядели уверенным ответом.
  const tentative = !!block.meta?.tentative
  return (
    <div className={`cards${tentative ? ' tentative' : ''}`}>
      {tentative && (
        <div className="banner hint-weak">
          Точного соответствия нет — это ближайшие по смыслу варианты, проверьте их
          перед выбором.
        </div>
      )}
      {(block.items ?? []).map((item: BlockItem) => (
        <div className={`card${item.weak ? ' weak' : ''}`} key={item.id}>
          <div className="card-head">
            <span className="rank">{item.rank}</span>
            <div>
              <div className="card-title">{item.title}</div>
              <div className="card-sub">{item.category}</div>
            </div>
            {/* три знака вместо двух: на двух вся выдача выглядела как один
                и тот же балл, хотя различия были — и это читалось как поломка */}
            <span className="score" title="итоговая релевантность">
              {Number(item.score).toFixed(3)}
            </span>
          </div>
          {item.snippet && <div className="snippet">{item.snippet}</div>}
          <div className="tags">
            {(item.reasons ?? []).map((r: string, i: number) => (
              <span className={`tag${i === 0 && item.weak ? ' warn' : ''}`} key={i}>{r}</span>
            ))}
          </div>
          <button
            className={`btn ${tentative ? '' : 'primary'}`}
            disabled={disabled}
            onClick={() => onAction({ type: 'select_product', id: item.id })}
          >
            Выбрать услугу
          </button>
        </div>
      ))}
    </div>
  )
}

// ------------------------------------------------- исполнители по способностям
// Отдельно от ExecutorList: там компании подобраны под конкретную услугу и
// период и у них есть занятость. Здесь периода нет, поэтому и загрузки нет —
// показывать её было бы враньём.
function CompanyList({ block }: { block: Block }) {
  const tentative = !!block.meta?.tentative
  return (
    <div className={`cards${tentative ? ' tentative' : ''}`}>
      <div className="card-title">{block.text}</div>
      {(block.items ?? []).map((item: BlockItem) => (
        <div className="card" key={item.id}>
          <div className="card-head">
            <span className="rank">{item.rank}</span>
            <div>
              <div className="card-title">{item.name}</div>
              <div className="card-sub">
                рейтинг {item.rating}/5
                {item.calcsCnt ? ` · выполнено работ: ${item.calcsCnt}` : ''}
              </div>
            </div>
            <span className="score">{Number(item.score).toFixed(3)}</span>
          </div>
          {(item.topProducts ?? []).length > 0 && (
            <div className="snippet">Делает: {(item.topProducts ?? []).join('; ')}</div>
          )}
          <ul className="reasons">
            {(item.reasons ?? []).map((r: string, i: number) => (
              <li key={i}>{r}</li>
            ))}
          </ul>
        </div>
      ))}
      <p className="muted small">
        Занятость в конкретные сроки здесь не учитывается: без периода она не определена.
        Выберите услугу и укажите даты — тогда система проверит загрузку.
      </p>
    </div>
  )
}

function Clarify({ block }: { block: Block }) {
  return (
    <div className="card hint">
      <div className="card-title">{block.text}</div>
      <ul className="plain">
        {(block.items ?? []).map((item: BlockItem, i: number) => (
          <li key={i}>• {item.text}</li>
        ))}
      </ul>
    </div>
  )
}

// ------------------------------------------------------------------ сроки
function PeriodRequest({ block, disabled, onAction }: Omit<Props, 'onOpenConstructor'>) {
  const [from, setFrom] = useState<string>(block.meta?.suggestedFrom ?? '')
  const [to, setTo] = useState<string>(block.meta?.suggestedTo ?? '')

  return (
    <div className="card">
      <div className="card-title">{block.text}</div>
      {block.meta?.typicalDays && (
        <div className="card-sub">
          Типовая длительность по истории: {block.meta.typicalDays} дн.
        </div>
      )}
      <div className="row">
        <label>
          с <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>
          по <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <button
          className="btn primary"
          disabled={disabled || !from || !to}
          onClick={() => onAction({ type: 'set_period', from, to })}
        >
          Подобрать исполнителей
        </button>
      </div>
    </div>
  )
}

// ------------------------------------------------------------- исполнители
const AVAILABILITY: Record<string, string> = {
  free: 'свободен',
  moderate: 'умеренная загрузка',
  busy: 'высокая загрузка',
  overloaded: 'перегружен',
}

function ExecutorList({ block, disabled, onAction }: Omit<Props, 'onOpenConstructor'>) {
  const [selected, setSelected] = useState<string[]>([])
  const items = block.items ?? []

  const toggle = (id: string) =>
    setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]))

  return (
    <div className="cards">
      {items.map((item: BlockItem) => (
        <label className={`card selectable ${selected.includes(item.id) ? 'on' : ''}`} key={item.id}>
          <div className="card-head">
            <input
              type="checkbox"
              checked={selected.includes(item.id)}
              onChange={() => toggle(item.id)}
              disabled={disabled}
            />
            <div>
              <div className="card-title">
                {item.name}
                {item.subcontract && <span className="badge warn">субподряд</span>}
              </div>
              <div className="card-sub">
                {AVAILABILITY[item.availability] ?? item.availability} · загрузка {item.loadPct}% ·
                рейтинг {item.rating}/5
              </div>
            </div>
            <span className="score">{Number(item.score).toFixed(2)}</span>
          </div>
          <ul className="reasons">
            {(item.reasons ?? []).map((r: string, i: number) => (
              <li key={i}>{r}</li>
            ))}
          </ul>
        </label>
      ))}
      <button
        className="btn primary wide"
        disabled={disabled || selected.length === 0}
        onClick={() =>
          onAction({
            type: 'select_executors',
            ids: selected,
            subcontract: items.filter((i) => selected.includes(i.id) && i.subcontract).map((i) => i.id),
          })
        }
      >
        Выбрать исполнителей ({selected.length})
      </button>
    </div>
  )
}

// ------------------------------------------------------------------ этапы
function StageList({ block, disabled, onAction }: Omit<Props, 'onOpenConstructor'>) {
  const items = block.items ?? []
  const [selected, setSelected] = useState<string[]>(
    items.filter((i) => i.preselected).map((i) => i.id),
  )

  const toggle = (id: string) =>
    setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]))

  return (
    <div className="card">
      <div className="card-title">{block.text}</div>
      <div className="card-sub">Собраны из реальных расчётов по этой услуге</div>
      <ul className="checklist">
        {items.map((item: BlockItem) => (
          <li key={item.id}>
            <label>
              <input
                type="checkbox"
                checked={selected.includes(item.id)}
                onChange={() => toggle(item.id)}
                disabled={disabled}
              />
              <span>
                {item.title}
                <span className="muted">
                  {item.medianDays ? ` · ${item.medianDays} дн.` : ''}
                  {item.usedCnt > 1 ? ` · встречается ${item.usedCnt} раз` : ''}
                </span>
                {item.documentation && <div className="muted small">Результат: {item.documentation}</div>}
              </span>
            </label>
          </li>
        ))}
      </ul>
      <button
        className="btn primary"
        disabled={disabled || selected.length === 0}
        onClick={() => onAction({ type: 'select_stages', ids: selected })}
      >
        Подтвердить этапы ({selected.length})
      </button>
    </div>
  )
}

function OperationList({ block, disabled, onAction }: Omit<Props, 'onOpenConstructor'>) {
  const items = block.items ?? []
  const [selected, setSelected] = useState<string[]>(
    items.filter((i) => i.preselected).map((i) => i.id),
  )
  const [open, setOpen] = useState(false)

  const toggle = (id: string) =>
    setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]))

  return (
    <div className="card">
      <button className="collapse" onClick={() => setOpen(!open)}>
        {open ? '▾' : '▸'} {block.text} ({items.length})
      </button>
      {open && (
        <>
          <ul className="checklist">
            {items.map((item: BlockItem) => (
              <li key={item.id}>
                <label>
                  <input
                    type="checkbox"
                    checked={selected.includes(item.id)}
                    onChange={() => toggle(item.id)}
                    disabled={disabled || item.required}
                  />
                  <span>
                    {item.title}
                    {item.required && <span className="badge">обязательная</span>}
                  </span>
                </label>
              </li>
            ))}
          </ul>
          <button
            className="btn"
            disabled={disabled}
            onClick={() => onAction({ type: 'select_operations', ids: selected })}
          >
            Сохранить операции
          </button>
        </>
      )}
    </div>
  )
}

function Conditions({ block, disabled, onAction }: Omit<Props, 'onOpenConstructor'>) {
  return (
    <div className="card">
      <div className="card-title">{block.text}</div>
      <ul className="checklist">
        {(block.items ?? []).map((item: BlockItem) => (
          <li key={item.key}>
            <label>
              <input
                type="checkbox"
                defaultChecked={!!item.value}
                disabled={disabled}
                onChange={(e) => onAction({ type: 'set_flag', key: item.key, flag: e.target.checked })}
              />
              <span>{item.title}</span>
            </label>
          </li>
        ))}
      </ul>
    </div>
  )
}

// ------------------------------------------------------- вспомогательные
function SimpleList({ block, icon }: { block: Block; icon: string }) {
  return (
    <div className="card">
      <div className="card-title">{block.text}</div>
      <ul className="plain">
        {(block.items ?? []).map((item: BlockItem, i: number) => (
          <li key={i}>
            <span className="icon">{icon}</span> {item.title}
            {item.cnt ? <span className="muted"> · вместе в {item.cnt} договорах</span> : null}
          </li>
        ))}
      </ul>
    </div>
  )
}

function SimilarCalcs({ block }: { block: Block }) {
  return (
    <div className="card">
      <div className="card-title">{block.text}</div>
      <table className="mini">
        <tbody>
          {(block.items ?? []).map((item: BlockItem) => (
            <tr key={item.id}>
              <td>{item.title}</td>
              <td className="muted">{item.company}</td>
              <td className="muted">{item.days ? `${item.days} дн.` : ''}</td>
              <td className="muted">{item.stages ? `${item.stages} эт.` : ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Recommendations({ block }: { block: Block }) {
  return (
    <div className="card hint">
      <div className="card-title">{block.text}</div>
      <ul className="plain">
        {(block.items ?? []).map((item: BlockItem, i: number) => (
          <li key={i}>• {item.text}</li>
        ))}
      </ul>
    </div>
  )
}

export function TzGaps({ block }: { block: Block }) {
  const readiness = Number(block.meta?.readiness ?? 0)
  return (
    <div className="card">
      <div className="card-title">Готовность ТЗ</div>
      <Readiness value={readiness} />
      {block.meta?.recommendation && <p className="rec">{block.meta.recommendation}</p>}
      <ul className="risks">
        {(block.items ?? []).map((risk: BlockItem, i: number) => (
          <li key={i} className={`risk ${risk.severity}`}>
            <span className="sev">{severityLabel(risk.severity)}</span>
            <div>
              <strong>{risk.title}</strong>
              <div className="muted">{risk.recommendation}</div>
            </div>
          </li>
        ))}
      </ul>
    </div>
  )
}

export function Readiness({ value }: { value: number }) {
  const tone = value >= 90 ? 'ok' : value >= 60 ? 'mid' : 'low'
  return (
    <div className="readiness">
      <div className="bar">
        <div className={`fill ${tone}`} style={{ width: `${Math.max(value, 2)}%` }} />
      </div>
      <span className={`pct ${tone}`}>{value}%</span>
    </div>
  )
}

export function severityLabel(severity: string): string {
  if (severity === 'blocking') return 'критично'
  if (severity === 'warning') return 'внимание'
  return 'инфо'
}
