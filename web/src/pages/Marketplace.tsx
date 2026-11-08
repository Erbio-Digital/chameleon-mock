import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, type CommunityPackage, type VerifiedConnector } from '@/api'
import { copyText, ErrorText, Header, Pill, tabPanel, Tabs, TrustBadge } from '@/components/common'

type Tab = 'verified' | 'community'
const tabs = [
  ['verified', 'Verified'],
  ['community', 'Community'],
] as const

/** Read-only: installing means changing CHAMELEON_CONNECTORS and restarting the container. */
export default function Marketplace() {
  const [tab, setTab] = useState<Tab>('verified')
  return (
    <>
      <Header title="Marketplace">
        Connectors you can add to this instance. To install one, copy its configuration into the container environment and restart it.
      </Header>
      <Tabs id="market" tabs={tabs} value={tab} onChange={setTab} />
      <div {...tabPanel('market', tab)} className="col" style={{ gap: 12 }}>
        {tab === 'verified' ? <Verified /> : <Community />}
      </div>
    </>
  )
}

function Verified() {
  const list = useQuery({ queryKey: ['marketplace', 'verified'], queryFn: api.marketplaceVerified, staleTime: 60_000 })
  return (
    <>
      <ErrorText error={list.error} />
      {list.isPending && <p className="muted">Loading the catalog...</p>}
      {list.data?.length === 0 && <div className="empty">The catalog is empty.</div>}
      <div className="grid-cards">
        {list.data?.map(e => (
          <MarketCard
            key={e.name}
            title={e.title}
            description={e.description}
            version={e.version}
            license={e.license}
            repository={e.repository}
            provider={e.provider}
            verified
            revoked={e.revoked !== null}
            note={note(e)}
            config={`CHAMELEON_CONNECTORS=${e.name}`}
            disabled={e.revoked !== null}
          />
        ))}
      </div>
    </>
  )
}

function note(e: VerifiedConnector): string | null {
  if (e.revoked) return `Revoked: ${e.revoked}`
  if (e.installed && e.installedVersion !== e.version) return `Installed ${e.installedVersion}; catalog has ${e.version}`
  if (e.installed) return 'Installed'
  return null
}

function Community() {
  const [term, setTerm] = useState('')
  const [submitted, setSubmitted] = useState('')
  const result = useQuery({ queryKey: ['marketplace', 'community', submitted], queryFn: () => api.marketplaceCommunity(submitted), staleTime: 60_000 })
  return (
    <>
      <div className="note warn" role="note">
        Community connectors are not verified: nobody reviewed their code. They run inside this instance with its permissions. Load one only if
        you trust its author.
      </div>
      <form
        className="row"
        onSubmit={e => {
          e.preventDefault()
          setSubmitted(term.trim())
        }}
      >
        <input className="field search" placeholder="Search NuGet" value={term} onChange={e => setTerm(e.target.value)} aria-label="Search community connectors" />
        <button className="btn btn-sec">Search</button>
      </form>
      <ErrorText error={result.error} />
      {result.data?.offline && <p className="muted">This instance is offline (CHAMELEON_OFFLINE=true): no search.</p>}
      {result.data?.error && <p className="err">{result.data.error}</p>}
      {result.data && !result.data.offline && !result.data.error && result.data.packages.length === 0 && <div className="empty">No community connectors found.</div>}
      <div className="grid-cards">
        {result.data?.packages.map((p: CommunityPackage) => (
          <MarketCard
            key={p.packageId}
            title={p.title}
            description={p.description}
            version={p.version}
            license={p.license}
            repository={p.repository ?? p.projectUrl}
            provider={null}
            verified={false}
            note={p.inCatalog ? 'Also in the verified catalog: prefer that one.' : null}
            config={`CHAMELEON_CONNECTORS=${p.packageId}@${p.version}\nCHAMELEON_ALLOW_UNVERIFIED=true`}
            disabled={false}
          />
        ))}
      </div>
    </>
  )
}

interface CardProps {
  title: string
  description: string
  version: string
  license: string | null
  repository: string | null
  provider: string | null
  verified: boolean
  revoked?: boolean
  note: string | null
  config: string
  disabled: boolean
}

/** Only http(s) links are rendered: catalog and NuGet metadata come from third parties. */
const safeUrl = (url: string | null) => {
  if (!url) return null
  try {
    const u = new URL(url)
    return u.protocol === 'http:' || u.protocol === 'https:' ? u : null
  } catch {
    return null
  }
}

function MarketCard(p: CardProps) {
  const provider = safeUrl(p.provider)
  const repository = safeUrl(p.repository)
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    const ok = await copyText(p.config)
    setCopied(ok)
    if (ok) setTimeout(() => setCopied(false), 1500)
  }
  return (
    <article className={p.verified && !p.revoked ? 'card' : 'card card-warn'}>
      <div className="row" style={{ justifyContent: 'space-between', gap: 8 }}>
        <b style={{ fontSize: 15 }}>{p.title}</b>
        {p.revoked ? <Pill tone={['Revoked', 'var(--warn-bg)', 'var(--warn-fg)']} /> : <TrustBadge verified={p.verified} />}
      </div>
      <p className="grow" style={{ margin: 0, color: 'var(--fg-soft)', lineHeight: 1.5 }}>
        {p.description}
      </p>
      <dl className="kv">
        <dt>Version</dt>
        <dd className="mono">{p.version}</dd>
        <dt>License</dt>
        <dd>{p.license ?? 'not declared'}</dd>
        {provider && (
          <>
            <dt>Provider</dt>
            <dd>
              <a href={provider.href} target="_blank" rel="noreferrer noopener">
                {provider.host}
              </a>
            </dd>
          </>
        )}
        {repository && (
          <>
            <dt>Repository</dt>
            <dd>
              <a href={repository.href} target="_blank" rel="noreferrer noopener">
                {repository.host + repository.pathname.replace(/\/$/, '')}
              </a>
            </dd>
          </>
        )}
      </dl>
      {p.note && <p className="small muted" style={{ margin: 0 }}>{p.note}</p>}
      <pre className="code mono">{p.config}</pre>
      <button className="btn btn-sec" disabled={p.disabled} onClick={copy}>
        {copied ? 'Copied' : 'Copy configuration'}
      </button>
    </article>
  )
}
