import { useState } from 'react'
import { ChatView } from './ChatView'
import { ConstructorView } from './ConstructorView'
import { AnalyticsView } from './AnalyticsView'

type Tab = 'chat' | 'constructor' | 'analytics'

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
        <div className="brand">
          ПРОСТОР <span className="muted">· ИИ-агент и конструктор ТЗ</span>
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
          <ChatView onOpenConstructor={openConstructor} />
        </div>
        {tab === 'constructor' && <ConstructorView sessionId={sessionId} />}
        {tab === 'analytics' && <AnalyticsView />}
      </main>
    </div>
  )
}
