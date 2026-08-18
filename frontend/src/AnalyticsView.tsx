import { useEffect, useMemo, useState } from 'react'
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  Cell,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { getAnalytics, listTzDocuments } from './api'

interface Overview {
  topSearchedProducts: { name: string; cnt: number }[]
  unrecognizedQueries: { query: string; created_at: string }[]
  topPairs: { product: string; related: string; cnt: number }[]
  topExecutors: { name: string; works: number; products: number }[]
  tzCreated: number
  tzAvgReadiness: number
  tzByTemplate: { name: string; cnt: number }[]
  topRisks: { title: string; severity: string; cnt: number }[]
  topStages: { name: string; cnt: number }[]
  productizationCandidates: {
    name: string
    calcs_cnt: number
    companies_cnt: number
    typical_duration_days: number | null
  }[]
  requestsByDay: { day: string; recognized: number; unrecognized: number }[]
  tzByDay: { day: string; cnt: number }[]
}

// Палитра для секторов диаграммы: производные от фирменных синего #004596 и
// оранжевого #e65907, чтобы графики не выбивались из визуального языка бренда.
const SLICE_COLORS = ['#004596', '#e65907', '#5b90d6', '#f0a35c', '#002855', '#9dbde6', '#a83f05']

const dayLabel = (iso: string) => {
  const [, m, d] = iso.split('-')
  return `${d}.${m}`
}

export function AnalyticsView() {
  const [data, setData] = useState<Overview | null>(null)
  const [documents, setDocuments] = useState<any[]>([])

  useEffect(() => {
    getAnalytics().then(setData).catch(() => undefined)
    listTzDocuments().then((r) => setDocuments(r.items ?? [])).catch(() => undefined)
  }, [])

  const requests = useMemo(
    () =>
      (data?.requestsByDay ?? []).map((r) => ({
        ...r,
        label: dayLabel(r.day),
        total: r.recognized + r.unrecognized,
      })),
    [data],
  )

  const tzTrend = useMemo(
    () => (data?.tzByDay ?? []).map((r) => ({ ...r, label: dayLabel(r.day) })),
    [data],
  )

  if (!data) return <div className="analytics"><p className="muted">Загружаю аналитику…</p></div>

  const totalRequests = requests.reduce((s, r) => s + r.total, 0)
  const recognizedShare = totalRequests
    ? Math.round((requests.reduce((s, r) => s + r.recognized, 0) / totalRequests) * 100)
    : 0

  const max = (rows: { cnt?: number; works?: number; calcs_cnt?: number }[], key: string) =>
    Math.max(1, ...rows.map((r: any) => r[key] ?? 0))

  return (
    <div className="analytics">
      <h2>Аналитика</h2>

      <div className="kpis">
        <Kpi label="Запросов за 30 дней" value={totalRequests} />
        <Kpi label="Распознано" value={`${recognizedShare}%`} />
        <Kpi label="Создано ТЗ" value={data.tzCreated} />
        <Kpi label="Средняя готовность" value={`${data.tzAvgReadiness}%`} />
      </div>

      <div className="grid">
        <Panel title="Количество запросов по дням" wide>
          {totalRequests === 0 ? (
            <p className="muted">За последние 30 дней запросов не было</p>
          ) : (
            <ResponsiveContainer width="100%" height={240}>
              <AreaChart data={requests} margin={{ top: 8, right: 8, left: -18, bottom: 0 }}>
                <defs>
                  <linearGradient id="gRecognized" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stopColor="#004596" stopOpacity={0.35} />
                    <stop offset="100%" stopColor="#004596" stopOpacity={0.02} />
                  </linearGradient>
                  <linearGradient id="gUnrecognized" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stopColor="#e65907" stopOpacity={0.3} />
                    <stop offset="100%" stopColor="#e65907" stopOpacity={0.02} />
                  </linearGradient>
                </defs>
                <XAxis dataKey="label" tick={{ fontSize: 11, fill: '#8a93a1' }} interval="preserveStartEnd"
                       tickLine={false} axisLine={{ stroke: '#dde3ec' }} minTickGap={24} />
                <YAxis tick={{ fontSize: 11, fill: '#8a93a1' }} tickLine={false} axisLine={false}
                       allowDecimals={false} width={34} />
                <Tooltip content={<ChartTooltip />} />
                <Area type="monotone" dataKey="recognized" name="Распознано" stackId="1"
                      stroke="#004596" strokeWidth={2} fill="url(#gRecognized)" />
                <Area type="monotone" dataKey="unrecognized" name="Не распознано" stackId="1"
                      stroke="#e65907" strokeWidth={2} fill="url(#gUnrecognized)" />
              </AreaChart>
            </ResponsiveContainer>
          )}
          <Legend items={[
            { color: '#004596', label: 'Распознано' },
            { color: '#e65907', label: 'Не распознано' },
          ]} />
        </Panel>

        <Panel title="Какие виды ТЗ выбираются чаще">
          {data.tzByTemplate.length === 0 ? (
            <p className="muted">ТЗ пока не создавались</p>
          ) : (
            <div className="donut-wrap">
              <ResponsiveContainer width="100%" height={200}>
                <PieChart>
                  <Pie data={data.tzByTemplate} dataKey="cnt" nameKey="name"
                       cx="50%" cy="50%" innerRadius={52} outerRadius={80} paddingAngle={2}
                       stroke="none">
                    {data.tzByTemplate.map((_, i) => (
                      <Cell key={i} fill={SLICE_COLORS[i % SLICE_COLORS.length]} />
                    ))}
                  </Pie>
                  <Tooltip content={<ChartTooltip suffix=" ТЗ" />} />
                </PieChart>
              </ResponsiveContainer>
              <Legend items={data.tzByTemplate.map((r, i) => ({
                color: SLICE_COLORS[i % SLICE_COLORS.length],
                label: `${r.name} · ${r.cnt}`,
              }))} />
            </div>
          )}
        </Panel>

        <Panel title="Самые востребованные услуги по запросам">
          {data.topSearchedProducts.length === 0 ? (
            <p className="muted">Нет данных</p>
          ) : (
            <ResponsiveContainer width="100%" height={Math.max(160, data.topSearchedProducts.length * 34)}>
              <BarChart data={data.topSearchedProducts} layout="vertical"
                        margin={{ top: 0, right: 16, left: 8, bottom: 0 }}>
                <XAxis type="number" hide allowDecimals={false} />
                <YAxis type="category" dataKey="name" width={150} tick={<TruncatedTick />}
                       tickLine={false} axisLine={false} />
                <Tooltip content={<ChartTooltip suffix=" запросов" />} cursor={{ fill: '#eaf1f9' }} />
                <Bar dataKey="cnt" fill="#004596" radius={[0, 4, 4, 0]} barSize={16} />
              </BarChart>
            </ResponsiveContainer>
          )}
        </Panel>

        <Panel title="Создание ТЗ по дням">
          {data.tzCreated === 0 ? (
            <p className="muted">ТЗ пока не создавались</p>
          ) : (
            <ResponsiveContainer width="100%" height={200}>
              <BarChart data={tzTrend} margin={{ top: 8, right: 8, left: -18, bottom: 0 }}>
                <XAxis dataKey="label" tick={{ fontSize: 11, fill: '#8a93a1' }} tickLine={false}
                       axisLine={{ stroke: '#dde3ec' }} minTickGap={24} interval="preserveStartEnd" />
                <YAxis tick={{ fontSize: 11, fill: '#8a93a1' }} tickLine={false} axisLine={false}
                       allowDecimals={false} width={34} />
                <Tooltip content={<ChartTooltip suffix=" ТЗ" />} cursor={{ fill: '#eaf1f9' }} />
                <Bar dataKey="cnt" name="ТЗ" fill="#004596" radius={[3, 3, 0, 0]} maxBarSize={22} />
              </BarChart>
            </ResponsiveContainer>
          )}
        </Panel>

        <Panel title="Исполнители с наибольшим числом аналогичных работ">
          <Bars rows={data.topExecutors.map((r) => ({ label: `${r.name} · ${r.products} услуг`, value: r.works }))}
                max={max(data.topExecutors, 'works')} />
        </Panel>

        <Panel title="Наиболее часто сочетаемые услуги">
          <ul className="plain">
            {data.topPairs.map((pair, i) => (
              <li key={i}>
                {pair.product} <span className="muted">+</span> {pair.related}
                <span className="muted"> · {pair.cnt} договоров</span>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Типичные ошибки при формировании ТЗ">
          {data.topRisks.length === 0 ? (
            <p className="muted">Пока нет данных — сформируйте несколько ТЗ</p>
          ) : (
            <ul className="plain">
              {data.topRisks.map((risk, i) => (
                <li key={i}>
                  <span className={`sev ${risk.severity}`}>{risk.severity}</span> {risk.title}
                  <span className="muted"> · {risk.cnt}</span>
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel title="Наиболее часто добавляемые этапы">
          {data.topStages.length === 0 ? (
            <p className="muted">Пока нет данных</p>
          ) : (
            <Bars rows={data.topStages.map((r) => ({ label: r.name, value: r.cnt }))}
                  max={max(data.topStages, 'cnt')} />
          )}
        </Panel>

        <Panel title="Кандидаты на упаковку в типовой продукт">
          <table className="mini">
            <thead>
              <tr><th>Услуга</th><th>Работ</th><th>Исполнителей</th><th>Типовой срок</th></tr>
            </thead>
            <tbody>
              {data.productizationCandidates.map((row, i) => (
                <tr key={i}>
                  <td>{row.name}</td>
                  <td>{row.calcs_cnt}</td>
                  <td>{row.companies_cnt}</td>
                  <td>{row.typical_duration_days ? `${row.typical_duration_days} дн.` : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>

        <Panel title="Запросы, не распознанные агентом">
          {data.unrecognizedQueries.length === 0 ? (
            <p className="muted">Все запросы нашли услуги</p>
          ) : (
            <ul className="plain">
              {data.unrecognizedQueries.map((row, i) => (
                <li key={i}>«{row.query}»</li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel title="Последние созданные ТЗ">
          {documents.length === 0 ? (
            <p className="muted">ТЗ пока не создавались</p>
          ) : (
            <table className="mini">
              <thead>
                <tr><th>Услуга</th><th>Объект</th><th>Готовность</th><th>Рисков</th><th /></tr>
              </thead>
              <tbody>
                {documents.map((doc) => (
                  <tr key={doc.tzId}>
                    <td>{doc.productName}</td>
                    <td>{doc.objectName}</td>
                    <td>{doc.readiness}%</td>
                    <td>{doc.risksCount}</td>
                    <td><a href={`/api/v1/tz/documents/${doc.tzId}/file`}>docx</a></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Panel>
      </div>
    </div>
  )
}

function Kpi({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="kpi">
      <div className="kpi-value">{value}</div>
      <div className="kpi-label">{label}</div>
    </div>
  )
}

function Panel({ title, children, wide }: { title: string; children: React.ReactNode; wide?: boolean }) {
  return (
    <section className={`card panel${wide ? ' panel-wide' : ''}`}>
      <div className="card-title">{title}</div>
      {children}
    </section>
  )
}

function Legend({ items }: { items: { color: string; label: string }[] }) {
  return (
    <div className="chart-legend">
      {items.map((it, i) => (
        <span className="legend-item" key={i}>
          <span className="legend-dot" style={{ background: it.color }} />
          {it.label}
        </span>
      ))}
    </div>
  )
}

function ChartTooltip({ active, payload, label, suffix = '' }: any) {
  if (!active || !payload?.length) return null
  return (
    <div className="chart-tip">
      {label && <div className="chart-tip-label">{label}</div>}
      {payload.map((p: any, i: number) => (
        <div className="chart-tip-row" key={i}>
          <span className="legend-dot" style={{ background: p.color || p.payload?.fill }} />
          <span>{p.name}</span>
          <b>{p.value}{suffix}</b>
        </div>
      ))}
    </div>
  )
}

function TruncatedTick({ x, y, payload }: any) {
  const text: string = payload.value ?? ''
  const short = text.length > 22 ? `${text.slice(0, 21)}…` : text
  return (
    <text x={x} y={y} dy={4} textAnchor="end" fontSize={12} fill="#4d5665">
      <title>{text}</title>
      {short}
    </text>
  )
}

function Bars({ rows, max }: { rows: { label: string; value: number }[]; max: number }) {
  if (rows.length === 0) return <p className="muted">Нет данных</p>
  return (
    <div className="bars">
      {rows.map((row, i) => (
        <div className="bar-row" key={i}>
          <span className="bar-label" title={row.label}>{row.label}</span>
          <span className="bar-track">
            <span className="bar-fill" style={{ width: `${(row.value / max) * 100}%` }} />
          </span>
          <span className="bar-value">{row.value}</span>
        </div>
      ))}
    </div>
  )
}
