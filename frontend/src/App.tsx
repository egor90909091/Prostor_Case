import { useState } from 'react'
import { ChatView } from './ChatView'
import { ConstructorView } from './ConstructorView'
import { AnalyticsView } from './AnalyticsView'

type Tab = 'chat' | 'constructor' | 'analytics'

// Хлебные крошки шапки: чисто визуальная подпись активного раздела,
// источник истины по-прежнему один — состояние tab.
const CRUMBS: Record<Tab, string> = {
  chat: 'Подбор услуги',
  constructor: 'Техническое задание',
  analytics: 'Аналитика',
}

export default function App() {
  const [tab, setTab] = useState<Tab>('chat')
  const [sessionId, setSessionId] = useState<string | null>(null)

  // Единое приложение с общей навигацией: переход из чата в конструктор
  // передаёт идентификатор сессии, а вместе с ним — все данные диалога.
  const openConstructor = (id: string) => {
    setSessionId(id)
    setTab('constructor')
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-left">
          <div className="brand">
            <span className="logo-mark" aria-hidden="true">П</span>
            ПРОСТОР
            <span className="brand-caret" aria-hidden="true">▼</span>
          </div>
          <div className="crumbs">
            <span className="sep">›</span>
            <span>{CRUMBS[tab]}</span>
          </div>
        </div>

        <nav>
          <button className={tab === 'chat' ? 'on' : ''} onClick={() => setTab('chat')}>
            ИИ-агент поиска
          </button>
          <button className={tab === 'constructor' ? 'on' : ''} onClick={() => setTab('constructor')}>
            Конструктор ТЗ
          </button>
          <button className={tab === 'analytics' ? 'on' : ''} onClick={() => setTab('analytics')}>
            Аналитика
          </button>
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
        {tab === 'constructor' && <ConstructorView sessionId={sessionId} />}
        {tab === 'analytics' && <AnalyticsView />}
      </main>
    </div>
  )
}
