import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { ChatView } from './ChatView'
import { ConstructorView } from './ConstructorView'
import { AnalyticsView } from './AnalyticsView'
import { DocumentsView } from './DocumentsView'
import { ContractorInboxView } from './ContractorInboxView'
import { getCompanies, type CompanyRef } from './api'
import { CUSTOMER, setActor, useActor } from './identity'

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
  // Роль — переключатель демо-контекста, а не авторизация: логинов и паролей
  // в прототипе нет, см. identity.ts. Заказчик один (НТЦ), подрядчиков много —
  // это компании из catalog.company.
  const actor = useActor()
  const isContractor = actor.kind === 'contractor'
  const [companies, setCompanies] = useState<CompanyRef[]>([])

  const [tab, setTab] = useState<Tab>('chat')
  const [sessionId, setSessionId] = useState<string | null>(null)
  // Не просто id, а «запрос на открытие»: конструктор теперь остаётся
  // смонтированным, и повторный клик по тому же ТЗ должен снова его загрузить.
  const [editRequest, setEditRequest] =
    useState<{ tzId: string; at: number; sectionKey?: string } | null>(null)
  const [chatRequest, setChatRequest] = useState<{ sessionId: string; at: number } | null>(null)
  // Документ, на котором нужно открыть «Мои заявки»: после формирования ТЗ
  // пользователь попадает сразу на свой свежий документ, а не в общий список,
  // где его ещё надо найти глазами.
  const [focusTzId, setFocusTzId] = useState<string | null>(null)
  // Факт «в конструкторе сформировали ТЗ», переданный в чат: он отмечает его
  // отдельным ходом, поэтому нужен не только id, но и момент — иначе повторная
  // генерация того же документа не отличалась бы от предыдущей.
  const [documentCreated, setDocumentCreated] = useState<{ tzId: string; at: number } | null>(null)
  // Счётчик сбросов заявки. Конструктор больше не размонтируется при
  // переключении вкладок, поэтому «Начать заново» в чате обязано дотянуться
  // до него сигналом — иначе форма осталась бы заполненной данными старой
  // заявки, хотя localStorage уже очищен.
  const [resetToken, setResetToken] = useState(0)

  // Единое приложение с общей навигацией: переход из чата в конструктор
  // передаёт идентификатор сессии, а вместе с ним — все данные диалога.
  const openConstructor = (id: string) => {
    setSessionId(id)
    setEditRequest(null)
    // Именно запрос, а не просто id: конструктор смонтирован всегда, и перенос
    // данных диалога в форму должен происходить по клику, а не при старте.
    setChatRequest({ sessionId: id, at: Date.now() })
    setTab('constructor')
  }

  // Открытие ранее сохранённого ТЗ из «Мои заявки» — в отличие от
  // openConstructor, тут нет sessionId, конструктор грузит state по
  // editTzId напрямую из БД.
  // sectionKey приходит из замечания подрядчика: конструктор откроется не
  // просто на этом ТЗ, а на разделе, к которому есть претензия.
  const openTzInConstructor = (tzId: string, sectionKey?: string) => {
    setEditRequest({ tzId, at: Date.now(), sectionKey })
    setTab('constructor')
  }

  const openDocuments = (tzId?: string) => {
    setFocusTzId(tzId ?? null)
    setTab('documents')
  }

  useEffect(() => {
    getCompanies().then((r) => setCompanies(r.items ?? [])).catch(() => undefined)
  }, [])

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
            <span>{isContractor ? 'Входящие ТЗ' : active.crumb}</span>
          </div>
        </div>

        {/* Под ролью подрядчика раздел один — входящие ТЗ, и навигация
            вырождается в подпись. Показывать её не за что. */}
        {!isContractor && (
          <nav className="topbar-nav">
            {TABS.map((t) => (
              <button key={t.key} className={tab === t.key ? 'on' : ''} onClick={() => setTab(t.key)}>
                {t.label}
              </button>
            ))}
          </nav>
        )}

        {/* Правый блок шапки: кто сейчас работает с системой. Это демо-роль,
            а не учётная запись — логина и пароля в прототипе нет, роль лежит
            в localStorage и уходит на бэкенд заголовком (см. identity.ts). */}
        <div className="topbar-right">
          <select
            className="top-select"
            value={actor.kind === 'customer' ? 'ntc' : actor.id}
            aria-label="Роль"
            onChange={(e) => {
              const value = e.target.value
              if (value === 'ntc') {
                setActor(CUSTOMER)
                return
              }
              const company = companies.find((c) => c.companyId === value)
              if (company) {
                setActor({
                  kind: 'contractor',
                  id: company.companyId,
                  name: company.name,
                  code: company.code,
                })
              }
            }}
          >
            <optgroup label="Заказчик">
              <option value="ntc">НТЦ</option>
            </optgroup>
            <optgroup label="Подрядчик">
              {companies.map((c) => (
                <option key={c.companyId} value={c.companyId}>{c.name}</option>
              ))}
            </optgroup>
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
        <div hidden={isContractor || tab !== 'chat'}>
          <ChatView
            onOpenConstructor={openConstructor}
            onOpenDocuments={openDocuments}
            onResetSession={() => {
              setSessionId(null)
              setDocumentCreated(null)
              setEditRequest(null)
              setChatRequest(null)
              setFocusTzId(null)
              setResetToken((n) => n + 1)
            }}
            documentCreated={documentCreated}
          />
        </div>
        {/*
          Конструктор, как и чат, остаётся смонтированным: при conditional
          render он терял всё несохранённое состояние на каждом переключении
          вкладки — зону готового документа, замечания ИИ, а в режиме
          редактирования ещё и перечитывал ТЗ из БД поверх правок пользователя.
        */}
        <div hidden={isContractor || tab !== 'constructor'}>
          <ConstructorView
            sessionId={sessionId}
            editRequest={editRequest}
            chatRequest={chatRequest}
            resetToken={resetToken}
            onNavigateToDocuments={openDocuments}
            onDocumentCreated={(tzId) => setDocumentCreated({ tzId, at: Date.now() })}
          />
        </div>
        {!isContractor && tab === 'documents' && (
          <DocumentsView onOpenInConstructor={openTzInConstructor} focusTzId={focusTzId} />
        )}
        {!isContractor && tab === 'analytics' && <AnalyticsView />}
        {/* Экран подрядчика. Чат и конструктор выше остаются смонтированными
            и под этой ролью: примерка роли подрядчика не должна стирать
            диалог и форму заказчика — вернувшись, он продолжит с того места,
            где остановился. */}
        {isContractor && <ContractorInboxView actor={actor} />}
      </main>

      {/* Нижняя панель вкладок — только для телефонов, на десктопе скрыта в CSS.
          Переключает то же самое состояние tab, что и вкладки в шапке.
          Под ролью подрядчика раздел один, переключать нечего. */}
      {/* Именно условный рендер, а не hidden: у .tabbar в CSS стоит
          display: grid, который перебивает атрибут hidden. */}
      {!isContractor && (
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
      )}
    </div>
  )
}
