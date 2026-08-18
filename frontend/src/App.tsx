import { useState } from 'react'
import type { ReactNode } from 'react'
import { ChatView } from './ChatView'
import { ConstructorView } from './ConstructorView'
import { AnalyticsView } from './AnalyticsView'
import { DocumentsView } from './DocumentsView'

type Tab = 'chat' | 'constructor' | 'documents' | 'analytics'

// Единый источник для обеих навигаций: верхних вкладок на десктопе и нижней
// панели на телефоне. Список один, поэтому разъехаться они не могут — какая
// из панелей показана, решает только CSS по ширине экрана.
const TABS: { key: Tab; label: string; short: string; crumb: string; icon: ReactNode }[] = [
  {
    key: 'chat',
    label: 'ИИ-агент поиска',
    short: 'Агент',
    crumb: 'Подбор услуги',
    icon: (
      <svg viewBox="0 0 20 20" fill="none" aria-hidden="true">
        <path
          d="M3 5.5A2.5 2.5 0 0 1 5.5 3h9A2.5 2.5 0 0 1 17 5.5v6a2.5 2.5 0 0 1-2.5 2.5H8l-4 3.2V14a1 1 0 0 1-1-1V5.5Z"
          stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"
        />
      </svg>
    ),
  },
  {
    key: 'constructor',
    label: 'Конструктор ТЗ',
    short: 'ТЗ',
    crumb: 'Техническое задание',
    icon: (
      <svg viewBox="0 0 20 20" fill="none" aria-hidden="true">
        <path
          d="M5 2.5h6.5L16 7v10.5H5a.5.5 0 0 1-.5-.5V3a.5.5 0 0 1 .5-.5Z"
          stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"
        />
        <path d="M11.5 2.5V7H16M7.5 11h5M7.5 14h3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
      </svg>
    ),
  },
  {
    key: 'documents',
    label: 'Мои заявки',
    short: 'Заявки',
    crumb: 'Мои заявки',
    icon: (
      <svg viewBox="0 0 20 20" fill="none" aria-hidden="true">
        <path
          d="M5.5 3h9a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1h-9a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z"
          stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"
        />
        <path d="M7.5 7h5M7.5 10h5M7.5 13h3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
      </svg>
    ),
  },
  {
    key: 'analytics',
    label: 'Аналитика',
    short: 'Аналитика',
    crumb: 'Аналитика',
    icon: (
      <svg viewBox="0 0 20 20" fill="none" aria-hidden="true">
        <path
          d="M4 16V9m4 7V4m4 12v-5m4 5V7"
          stroke="currentColor" strokeWidth="1.6" strokeLinecap="round"
        />
      </svg>
    ),
  },
]

export default function App() {
  const [tab, setTab] = useState<Tab>('chat')
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [editTzId, setEditTzId] = useState<string | null>(null)

  // Единое приложение с общей навигацией: переход из чата в конструктор
  // передаёт идентификатор сессии, а вместе с ним — все данные диалога.
  const openConstructor = (id: string) => {
    setSessionId(id)
    setEditTzId(null)
    setTab('constructor')
  }

  // Открытие ранее сохранённого ТЗ из «Мои заявки» — в отличие от
  // openConstructor, тут нет sessionId, конструктор грузит state по
  // editTzId напрямую из БД.
  const openTzInConstructor = (tzId: string) => {
    setEditTzId(tzId)
    setTab('constructor')
  }

  const active = TABS.find((t) => t.key === tab) ?? TABS[0]

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-left">
          <div className="brand">
            <span className="logo-mark" aria-hidden="true">П</span>
            ПРОСТОР
            <span className="brand-caret" aria-hidden="true">▼</span>
          </div>
          {/* На узком экране крошки работают заголовком текущего раздела:
              верхние вкладки там скрыты, и без подписи было бы непонятно, где ты. */}
          <div className="crumbs">
            <span className="sep">›</span>
            <span>{active.crumb}</span>
          </div>
        </div>

        <nav className="topbar-nav">
          {TABS.map((t) => (
            <button key={t.key} className={tab === t.key ? 'on' : ''} onClick={() => setTab(t.key)}>
              {t.label}
            </button>
          ))}
        </nav>

        {/* Правый блок шапки: только контекст организации. Профиля и роли нет —
            авторизация в системе не предусмотрена, показывать имя пользователя
            было бы обещанием функции, которой не существует. */}
        <div className="topbar-right">
          <select className="top-select" defaultValue="ntc" aria-label="Организация">
            <option value="ntc">НТЦ</option>
          </select>
        </div>
      </header>

      <main>
        {/*
          ChatView остаётся смонтированным всегда, а не только на активной вкладке:
          conditional render убивал бы его состояние (историю сообщений, sessionId)
          при каждом переключении вкладок и пересоздавал бы сессию заново через
          createSession. Прячем через hidden — React-состояние переживает
          переключение без сети; восстановление после полной перезагрузки страницы
          отдельно решено в ChatView через localStorage + GET /sessions/{id}.
        */}
        <div hidden={tab !== 'chat'}>
          <ChatView onOpenConstructor={openConstructor} onResetSession={() => setSessionId(null)} />
        </div>
        {tab === 'constructor' && (
          <ConstructorView
            sessionId={sessionId}
            editTzId={editTzId}
            onNavigateToDocuments={() => setTab('documents')}
          />
        )}
        {tab === 'documents' && <DocumentsView onOpenInConstructor={openTzInConstructor} />}
        {tab === 'analytics' && <AnalyticsView />}
      </main>

      {/* Нижняя панель вкладок — только для телефонов, на десктопе скрыта в CSS.
          Переключает то же самое состояние tab, что и вкладки в шапке. */}
      <nav className="tabbar" aria-label="Разделы">
        {TABS.map((t) => (
          <button
            key={t.key}
            className={tab === t.key ? 'on' : ''}
            onClick={() => setTab(t.key)}
            aria-current={tab === t.key ? 'page' : undefined}
          >
            <span className="tabbar-icon">{t.icon}</span>
            <span className="tabbar-label">{t.short}</span>
          </button>
        ))}
      </nav>
    </div>
  )
}
