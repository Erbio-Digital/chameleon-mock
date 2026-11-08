import { useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Plug } from 'lucide-react'
import { api, type Connector, type SubscriptionStatus, type SubscriptionSummary } from '@/api'
import { ErrorText, Header, Pill, StateFlow, tabPanel, Tabs, TrustBadge } from '@/components/common'
import { Accounts } from '@/components/Accounts'
import { Registrations } from '@/components/Registrations'
import { SubscriptionPanel } from '@/components/SubscriptionPanel'
import { ms, type Tone } from '@/format'

// Catalog generated from each connector's own description: a new connector shows up here without
// panel code, with its state in the current sandbox.
const categories: Record<string, Tone> = {
  banking: ['Banking', 'var(--info-bg)', 'var(--info-fg)'],
  fiscal: ['Fiscal', 'var(--ok-bg)', 'var(--ok-fg)'],
  helpdesk: ['Helpdesk', 'var(--purple-bg)', 'var(--purple-fg)'],
  lookup: ['Data lookup', 'var(--warn-bg)', 'var(--warn-fg)'],
}
const categoryTone = (c: string): Tone => categories[c] ?? ['Other', 'var(--chip)', 'var(--fg-soft)']

const statusTone: Record<SubscriptionStatus, Tone> = {
  Active: ['Active', 'var(--ok-bg)', 'var(--ok-fg)'],
  Pending: ['Setup pending', 'var(--warn-bg)', 'var(--warn-fg)'],
  Available: ['Available', 'var(--chip)', 'var(--fg-soft)'],
}
const actionLabel: Record<SubscriptionStatus, string> = { Active: 'Details', Pending: 'Set up', Available: 'Subscribe' }

export default function Connectors() {
  const { sandbox = 'default', name } = useParams()
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors })
  const subs = useQuery({ queryKey: [sandbox, 'subscriptions'], queryFn: () => api.subscriptions(sandbox) })
  const [category, setCategory] = useState('')
  const [q, setQ] = useState('')
  const selected = connectors.data?.find(c => c.name === name)
  if (name && selected) return <Detail sandbox={sandbox} c={selected} status={statusOf(subs.data, selected.name)} />

  const all = connectors.data ?? []
  const used = [...new Set(all.map(c => c.category))]
  const text = q.trim().toLowerCase()
  const visible = all.filter(
    c => (!category || c.category === category) && (!text || [c.name, c.title, c.description, categoryTone(c.category)[0]].join(' ').toLowerCase().includes(text)),
  )
  const count = (st: SubscriptionStatus) => all.filter(c => statusOf(subs.data, c.name) === st).length

  return (
    <>
      <Header title="Connectors">
        Providers registered in this instance. Subscribe in the sandbox <span className="mono">{sandbox}</span> and fill in what is missing so your service can talk to it.
      </Header>
      <div className="row" style={{ flexWrap: 'wrap', gap: 8 }}>
        <span className="st" style={{ background: 'var(--ok-bg)', color: 'var(--ok-fg)' }}>
          {count('Active')} active
        </span>
        <span className="st" style={{ background: 'var(--warn-bg)', color: 'var(--warn-fg)' }}>
          {count('Pending')} with setup pending
        </span>
        <div className="grow" />
        <div role="group" aria-label="Filter by type" className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
          {['', ...used].map(k => (
            <button key={k} className="pillbtn" aria-pressed={category === k} onClick={() => setCategory(k)}>
              {k ? categoryTone(k)[0] : 'All'}
            </button>
          ))}
        </div>
        <input className="field search" type="search" aria-label="Search connectors" placeholder="Search" value={q} onChange={e => setQ(e.target.value)} />
      </div>
      <ErrorText error={connectors.error ?? subs.error} />
      {used
        .filter(k => visible.some(c => c.category === k))
        .map(k => {
          const group = visible.filter(c => c.category === k)
          return (
            <section key={k} className="col" style={{ gap: 10 }}>
              <div className="row" style={{ gap: 8 }}>
                <h2 style={{ fontSize: 14 }}>{categoryTone(k)[0]}</h2>
                <span className="small muted">
                  {group.length} {group.length === 1 ? 'connector' : 'connectors'}
                </span>
              </div>
              <div className="grid-cards">
                {group.map(c => {
                  const st = statusOf(subs.data, c.name)
                  return (
                    <article key={c.name} className={c.packageId && !c.verified ? 'card card-warn' : 'card'}>
                      <div className="row" style={{ gap: 12 }}>
                        <Logo name={c.name} size={40} />
                        <div className="grow">
                          <b style={{ fontSize: 15 }}>{c.title ?? c.name}</b>
                          <div className="small muted mono">
                            {c.name}
                            {c.version && ` ${c.version}`}
                          </div>
                        </div>
                      </div>
                      <p className="grow" style={{ margin: 0, color: 'var(--fg-soft)', lineHeight: 1.5 }}>
                        {c.description ?? `${c.entities.map(e => e.type).join(', ')} · ${c.routes.length} operations`}
                      </p>
                      <div className="row">
                        <Pill tone={statusTone[st]} />
                        {c.packageId && <TrustBadge verified={c.verified} />}
                        <div className="grow" />
                        <Link className={`btn ${st === 'Available' ? 'btn-pri' : 'btn-sec'}`} to={`/s/${sandbox}/connectors/${c.name}`}>
                          {actionLabel[st]}
                        </Link>
                      </div>
                    </article>
                  )
                })}
              </div>
            </section>
          )
        })}
      {connectors.data && visible.length === 0 && <div className="empty">No connector matches this filter.</div>}
    </>
  )
}

const statusOf = (subs: SubscriptionSummary[] | undefined, name: string): SubscriptionStatus =>
  subs?.find(s => s.connector === name)?.status ?? 'Available'

// The connector's own logo when it ships one; otherwise the generic plug.
function Logo({ name, size }: { name: string; size: number }) {
  const [missing, setMissing] = useState(false)
  const box = { width: size, height: size, borderRadius: 8, flex: 'none' } as const
  return missing ? (
    <span className="st" style={{ ...box, justifyContent: 'center', background: 'var(--info-bg)', color: 'var(--info-fg)' }}>
      <Plug size={size / 2} aria-hidden />
    </span>
  ) : (
    <img src={`/_chameleon/connectors/${name}/logo`} alt="" style={{ ...box, objectFit: 'contain' }} onError={() => setMissing(true)} />
  )
}

// Groups a connector's operations by what they are about, read from the path alone so a new connector
// needs no panel code: webhook registrations, authentication, then the first resource segment after
// the connector prefix (Inter: pix, cobranca, banking; Bradesco: cob; NFS-e: nfse).
function groupOf(path: string): string {
  const parts = path.split('/').filter(p => p && !p.startsWith('{') && !/^v\d+$/.test(p))
  if (parts.some(p => /^webhooks?$/.test(p))) return 'webhook'
  if (parts.some(p => p === 'oauth' || p === 'token')) return 'authentication'
  return parts[0] ?? '/'
}

function Operations({ c, prefix }: { c: Connector; prefix: string }) {
  const groups = new Map<string, string[]>()
  for (const r of c.routes) {
    const g = groupOf(r.split(' ')[1].slice(prefix.length))
    groups.set(g, [...(groups.get(g) ?? []), r])
  }
  return (
    <div className="col" style={{ gap: 12 }}>
      {[...groups].sort(([a], [b]) => +(a === 'webhook') - +(b === 'webhook')).map(([g, routes]) => (
        <section key={g} className="panel">
          <div className="panel-head row">
            <h2 className="grow" style={{ fontSize: 14, textTransform: 'capitalize' }}>
              {g}
            </h2>
            <span className="small muted">
              {routes.length} {routes.length === 1 ? 'operation' : 'operations'}
            </span>
          </div>
          <table className="t">
            <tbody>
              {routes.map(r => {
                const [method, path] = r.split(' ')
                return (
                  <tr key={r}>
                    <td className="mono" style={{ height: 38, whiteSpace: 'normal', overflowWrap: 'anywhere' }}>
                      <span className="method" style={{ color: `var(--m-${method.toLowerCase()})` }}>
                        {method}
                      </span>
                      {path.slice(prefix.length) || '/'}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </section>
      ))}
    </div>
  )
}

function Entities({ c }: { c: Connector }) {
  return (
    <div className="col" style={{ gap: 16 }}>
      {c.entities.map(e => (
        <section key={e.type} className="panel col" style={{ padding: 16, gap: 12 }}>
          <div className="row">
            <h2 className="mono grow" style={{ fontSize: 14 }}>
              {e.type}
            </h2>
            <span className="small muted">
              key <span className="mono">{e.key}</span>
            </span>
          </div>
          <StateFlow entity={e} />
          <div>
            <h3 className="h3">Actions</h3>
            {c.actions
              .filter(a => a.entityType === e.type)
              .map(a => (
                <div key={a.name} className="small" style={{ marginBottom: 4 }}>
                  <b>{a.label}</b> <span className="mono muted">{a.name}</span>
                  <span className="muted">
                    {' '}
                    · from {a.allowedFrom.join(', ')}
                    {a.parameters.length ? ` · parameters: ${a.parameters.map(p => p.name).join(', ')}` : ''}
                  </span>
                </div>
              ))}
          </div>
        </section>
      ))}
    </div>
  )
}

type Tab = 'connection' | 'certificate' | 'settings' | 'webhooks' | 'accounts' | 'operations' | 'entities'

function Detail({ sandbox, c, status }: { sandbox: string; c: Connector; status: SubscriptionStatus }) {
  // "/{connector}" is how Chameleon routes to the connector, not part of the provider's API: it belongs to the base URL.
  const prefix = `/${c.name}`
  const sub = useQuery({ queryKey: [sandbox, 'subscription', c.name], queryFn: () => api.subscription(sandbox, c.name) })
  const subscribed = status !== 'Available'
  // Only the tabs that mean something for this connector in this sandbox.
  const all: [Tab, string, boolean][] = [
    ['connection', 'Connection', true],
    ['certificate', 'Certificate (mTLS)', subscribed && !!sub.data?.certificate],
    ['settings', 'Settings', subscribed && c.settings.length > 0],
    ['webhooks', 'Webhooks', subscribed && (!!c.registrations || c.hasTestEvent)],
    ['accounts', 'Accounts', subscribed && c.hasAccounts],
    ['operations', `Operations (${c.routes.length})`, true],
    ['entities', 'Entities and actions', c.entities.length > 0],
  ]
  const tabs = all.filter(t => t[2]).map(([k, label]) => [k, label] as const)
  const [params, setParams] = useSearchParams()
  const tab = tabs.find(([k]) => k === params.get('tab'))?.[0] ?? 'connection'
  return (
    <>
      <Link className="linkbtn row" style={{ alignSelf: 'flex-start', textDecoration: 'none' }} to={`/s/${sandbox}/connectors`}>
        <ArrowLeft size={14} aria-hidden />
        All connectors
      </Link>
      <section className="panel" style={{ borderTop: '3px solid var(--accent)' }}>
        <div className="row" style={{ gap: 12, padding: 16, flexWrap: 'wrap' }}>
          <Logo name={c.name} size={48} />
          <div className="grow" style={{ minWidth: 0 }}>
            <h1>{c.title ?? c.name}</h1>
            <div className="small">
              <span className="muted">Connector type: </span>
              {categoryTone(c.category)[0]}
            </div>
            {c.packageId && (
              <div className="small row" style={{ gap: 8 }}>
                <span className="mono">
                  {c.packageId} {c.version}
                </span>
                <TrustBadge verified={c.verified} />
              </div>
            )}
          </div>
          <Pill tone={[`${statusTone[status][0]} in ${sandbox}`, statusTone[status][1], statusTone[status][2]]} />
        </div>
        <Tabs
          id="connector"
          tabs={tabs}
          value={tab}
          onChange={k => setParams(k === 'connection' ? {} : { tab: k })}
          style={{ overflowX: 'auto', overflowY: 'hidden' }}
        />
      </section>

      <div {...tabPanel('connector', tab)} className="col" style={{ gap: 12 }}>
        {tab === 'connection' && <SubscriptionPanel sandbox={sandbox} c={c} view="connection" />}
        {tab === 'certificate' && <SubscriptionPanel sandbox={sandbox} c={c} view="certificate" />}
        {tab === 'settings' && <SubscriptionPanel sandbox={sandbox} c={c} view="settings" />}
        {tab === 'webhooks' && (
          <>
            {c.webhooks && (
              <p className="small muted" style={{ margin: 0 }}>
                Default delivery policy: {c.webhooks.maxAttempts} attempts, base wait {ms(c.webhooks.baseDelayMs)}, timeout {ms(c.webhooks.timeoutMs)}.
              </p>
            )}
            {c.hasTestEvent && <SubscriptionPanel sandbox={sandbox} c={c} view="test" />}
            {c.registrations && <Registrations sandbox={sandbox} connector={c.name} />}
          </>
        )}
        {tab === 'accounts' && <Accounts sandbox={sandbox} bank={c.name} />}
        {tab === 'operations' && (
          <>
            <p className="small muted" style={{ margin: 0 }}>
              Paths relative to the base URL, the same as the real API.
            </p>
            <Operations c={c} prefix={prefix} />
          </>
        )}
        {tab === 'entities' && <Entities c={c} />}
      </div>
    </>
  )
}
