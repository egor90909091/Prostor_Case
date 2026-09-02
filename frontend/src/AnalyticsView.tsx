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
import { severityLabel } from './Blocks'
import { getAnalytics, listTzDocuments } from './api'

interface Overview {
  topSearchedProducts: { name: string; cnt: number }[]
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
  // Появляется только когда накачена миграция 08_review.sql — до неё
  // раздела согласования на дашборде просто нет.
  review?: {
    sent: number
    pending: number
    approved: number
    revision: number
    rejected: number
    avgDecisionHours: number
  }
}

// Палитра для секторов диаграммы: производные от фирменных синего #004596 и
// оранжевого #e65907, чтобы графики не выбивались из визуального языка бренда.
const SLICE_COLORS = ['#004596', '#e65907', '#5b90d6', '#f0a35c', '#002855', '#9dbde6', '#a83f05']

const dayLabel = (iso: string) => {
  const [, m, d] = iso.split('-')
  return `${d}.${m}`
}

/**
 * Дашборд аналитики в двух видах.
 *
 * Заказчик видит показатели своей работы: сколько ТЗ собрано, насколько они
 * готовы, как идёт согласование с подрядчиками, на чём чаще всего спотыкаются
 * его ТЗ. Сводная картина платформы — качество распознавания запросов, спрос
 * по каталогу, рынок исполнителей, кандидаты на упаковку в продукт — это
 * материалы владельца платформы, и они открыты только админу.
 *
 * Заказчик пока один, поэтому его показатели считаются по всем документам;
 * когда заказчиков станет много, фильтр добавится здесь же, а состав панелей
 * менять уже не придётся.
 */
export function AnalyticsView({ scope = 'customer' }: { scope?: 'customer' | 'admin' }) {
  const platform = scope === 'admin'
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

  const exportCsv = () => {
    downloadCsv(buildAnalyticsCsv(data, documents, totalRequests, recognizedShare, platform), platform)
  }

  return (
    <div className="analytics">
      <div className="analytics-head">
        <h2>{platform ? 'Аналитика платформы' : 'Аналитика по моим заявкам'}</h2>
        <div className="analytics-actions">
          <button className="btn small ghost" onClick={exportCsv}>
            <DownloadIcon />
            Скачать CSV
          </button>
          <button className="btn small ghost" onClick={() => window.print()}>
            <PrintIcon />
            Печать / PDF
          </button>
        </div>
      </div>

      <div className="kpis">
        {platform && (
          <>
            <Kpi label="Запросов за 30 дней" value={totalRequests} />
            <Kpi label="Распознано" value={`${recognizedShare}%`} />
          </>
        )}
        <Kpi label="Создано ТЗ" value={data.tzCreated} />
        <Kpi label="Средняя готовность" value={`${data.tzAvgReadiness}%`} />
        {data.review && data.review.sent > 0 && (
          <>
            <Kpi label="Направлено подрядчикам" value={data.review.sent} />
            <Kpi label="Согласовано" value={data.review.approved} />
          </>
        )}
      </div>

      <div className="grid">
        {platform && (
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
        )}

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

        {platform && (
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
        )}

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

        {data.review && data.review.sent > 0 && (
          <Panel title="Согласование ТЗ подрядчиками">
            <Bars
              rows={[
                { label: 'Ждут ответа', value: data.review.pending },
                { label: 'Согласовано', value: data.review.approved },
                { label: 'На доработке', value: data.review.revision },
                { label: 'Отклонено', value: data.review.rejected },
              ]}
              max={data.review.sent}
            />
            <p className="muted small">
              Всего направлений: {data.review.sent}
              {data.review.avgDecisionHours > 0 &&
                ` · среднее время до решения: ${data.review.avgDecisionHours} ч`}
            </p>
          </Panel>
        )}

        {platform && (
          <Panel title="Исполнители с наибольшим числом аналогичных работ">
            <Bars rows={data.topExecutors.map((r) => ({ label: `${r.name} · ${r.products} услуг`, value: r.works }))}
                  max={max(data.topExecutors, 'works')} />
          </Panel>
        )}

        {platform && (
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
        )}

        <Panel title="Типичные ошибки при формировании ТЗ">
          {data.topRisks.length === 0 ? (
            <p className="muted">Пока нет данных — сформируйте несколько ТЗ</p>
          ) : (
            <ul className="risks risk-stats">
              {data.topRisks.map((risk, i) => (
                <li key={i} className={`risk ${risk.severity}`}>
                  <span className="sev">{severityLabel(risk.severity)}</span>
                  <span className="risk-stat-title">{risk.title}</span>
                  <span className="risk-stat-count">{risk.cnt}</span>
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

        {platform && (
        <Panel title="Кандидаты на упаковку в типовой продукт" wide>
          <table className="mini">
            <colgroup>
              <col style={{ width: '40%' }} />
              <col style={{ width: '15%' }} />
              <col style={{ width: '22%' }} />
              <col style={{ width: '23%' }} />
            </colgroup>
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
        )}

        <Panel title={platform ? 'Последние ТЗ на платформе' : 'Мои последние ТЗ'} wide>
          {documents.length === 0 ? (
            <p className="muted">ТЗ пока не создавались</p>
          ) : (
            <table className="mini">
              <colgroup>
                <col style={{ width: '30%' }} />
                <col style={{ width: '32%' }} />
                <col style={{ width: '16%' }} />
                <col style={{ width: '11%' }} />
                <col style={{ width: '11%' }} />
              </colgroup>
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

function DownloadIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="none" aria-hidden="true">
      <path d="M10 3v9.5M6.5 9l3.5 3.5L13.5 9" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M4 14.5V16a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1v-1.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  )
}

function PrintIcon() {
  return (
    <svg viewBox="0 0 20 20" fill="none" aria-hidden="true">
      <path d="M6 7V3.5a.5.5 0 0 1 .5-.5h7a.5.5 0 0 1 .5.5V7" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" />
      <rect x="3.5" y="7" width="13" height="7" rx="1" stroke="currentColor" strokeWidth="1.5" />
      <path d="M6 12.5h8v4a.5.5 0 0 1-.5.5h-7a.5.5 0 0 1-.5-.5v-4Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" />
    </svg>
  )
}

// Строим один CSV со всеми секциями сразу (а не отдельный файл на панель) —
// пользователю аналитики обычно нужна выгрузка целиком для дальнейшей
// обработки в Excel, а не блок за блоком.
function csvCell(value: string | number | null | undefined): string {
  const s = String(value ?? '')
  return /[;"\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
}

function csvSection(title: string, header: string[], rows: (string | number | null | undefined)[][]): string {
  const lines = [title, header.map(csvCell).join(';')]
  if (rows.length === 0) {
    lines.push('нет данных')
  } else {
    rows.forEach((row) => lines.push(row.map(csvCell).join(';')))
  }
  return lines.join('\n')
}

// Выгрузка повторяет то, что человек видит на экране: панели платформы
// попадают в файл только у админа. Иначе «Скачать CSV» отдавало бы заказчику
// ровно те данные, которые с дашборда убраны.
function buildAnalyticsCsv(
  data: Overview,
  documents: any[],
  totalRequests: number,
  recognizedShare: number,
  platform: boolean,
): string {
  const sections = [
    csvSection('Сводка', ['Метрика', 'Значение'], [
      ...(platform
        ? [['Запросов за 30 дней', totalRequests], ['Распознано, %', recognizedShare]]
        : []),
      ['Создано ТЗ', data.tzCreated],
      ['Средняя готовность, %', data.tzAvgReadiness],
    ]),
    ...(platform
      ? [csvSection('Запросы по дням', ['День', 'Распознано', 'Не распознано'],
          data.requestsByDay.map((r) => [r.day, r.recognized, r.unrecognized]))]
      : []),
    csvSection('Создание ТЗ по дням', ['День', 'Количество'],
      data.tzByDay.map((r) => [r.day, r.cnt])),
    ...(platform
      ? [csvSection('Самые востребованные услуги', ['Услуга', 'Запросов'],
          data.topSearchedProducts.map((r) => [r.name, r.cnt]))]
      : []),
    csvSection('Виды ТЗ', ['Шаблон', 'Количество'],
      data.tzByTemplate.map((r) => [r.name, r.cnt])),
    ...(platform
      ? [
          csvSection('Исполнители', ['Исполнитель', 'Работ', 'Услуг'],
            data.topExecutors.map((r) => [r.name, r.works, r.products])),
          csvSection('Сочетаемые услуги', ['Услуга', 'Сопутствующая услуга', 'Договоров'],
            data.topPairs.map((r) => [r.product, r.related, r.cnt])),
        ]
      : []),
    csvSection('Типичные ошибки при формировании ТЗ', ['Риск', 'Критичность', 'Количество'],
      data.topRisks.map((r) => [r.title, r.severity, r.cnt])),
    csvSection('Часто добавляемые этапы', ['Этап', 'Количество'],
      data.topStages.map((r) => [r.name, r.cnt])),
    ...(platform
      ? [csvSection('Кандидаты на упаковку в типовой продукт',
          ['Услуга', 'Расчётов', 'Компаний', 'Типовой срок, дн'],
          data.productizationCandidates.map(
            (r) => [r.name, r.calcs_cnt, r.companies_cnt, r.typical_duration_days ?? '']))]
      : []),
    csvSection(platform ? 'Последние ТЗ на платформе' : 'Мои последние ТЗ',
      ['Услуга', 'Объект', 'Готовность, %', 'Рисков'],
      documents.map((d) => [d.productName, d.objectName, d.readiness, d.risksCount])),
  ]
  return sections.join('\n\n')
}

function downloadCsv(content: string, platform: boolean) {
  const blob = new Blob(['﻿' + content], { type: 'text/csv;charset=utf-8;' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  const date = new Date().toISOString().slice(0, 10)
  a.href = url
  a.download = `${platform ? 'analytics_platform' : 'analytics'}_${date}.csv`
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
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
