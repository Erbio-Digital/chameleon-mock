import { useState } from 'react'
import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { Copy, X } from 'lucide-react'
import { api, type Interaction } from '@/api'
import { Code, copyText, ErrorText, Pill, tabPanel, Tabs } from '@/components/common'
import { change, dayTime, hms, isCall, ms, result, toneOf } from '@/format'

// Headers the client sets by itself: left out of the curl.
const skipHeaders = new Set(['host', 'content-length', 'connection', 'accept-encoding', 'user-agent'])

/** The call as a curl against this Chameleon; credentials stay redacted, personal data masked. */
function asCurl(sandbox: string, i: Interaction) {
  const quote = (s: string) => `'${s.replace(/'/g, `'\\''`)}'`
  const parts = [`curl -X ${i.method}`, quote(`${window.location.origin}/s/${sandbox}${i.path}`)]
  for (const [name, value] of Object.entries(i.requestHeaders ?? {}))
    if (!skipHeaders.has(name.toLowerCase())) parts.push(`-H ${quote(`${name}: ${value === '[redacted]' ? '<token>' : value}`)}`)
  if (i.requestBody) parts.push(`-d ${quote(i.requestBody)}`)
  return parts.join(' \\\n  ')
}

const parsed = (body: string | null) => {
  if (!body) return body
  try {
    return JSON.parse(body) as unknown
  } catch {
    return body
  }
}

/** Everything the panel shows of one interaction as one JSON, for evidence (a bug report, an AI). Already masked by the server. */
function asEvidence(i: Interaction, entityType: string | null, changes: Interaction[]) {
  return JSON.stringify(
    {
      seq: i.seq,
      id: i.id,
      at: i.at,
      connector: i.connector,
      kind: i.byClock ? 'Clock' : i.kind,
      method: i.method,
      path: i.path,
      status: i.statusCode,
      durationMs: i.durationMs,
      entity: i.entityId && { id: i.entityId, type: entityType },
      fault: i.fault,
      note: i.detail,
      correlationId: i.correlationId,
      request: i.method === null ? null : { headers: i.requestHeaders, body: parsed(i.requestBody) },
      response: i.method === null ? null : { status: i.statusCode, body: parsed(i.responseBody), truncated: i.truncated },
      transitions: changes.map(t => ({ entity: t.entityId, type: t.entityType, from: t.fromState, to: t.toState, at: t.at })),
    },
    null,
    2,
  )
}

type Tab = 'summary' | 'request' | 'response' | 'correlation'

/** Detail pane of one feed row: summary, request, response and the rest of its correlation. */
export default function Detail(props: {
  sandbox: string
  seq: string
  loaded?: Interaction
  onClose: () => void
  onPick: (seq: number) => void
  onCorrelation: (id: string) => void
}) {
  const { sandbox, loaded } = props
  const [tab, setTab] = useState<Tab>('summary')
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors })
  const [copied, setCopied] = useState<[what: string, label: string]>(['', ''])
  // A row from an older page or a shared link: look it up by seq through the cursor.
  const fetched = useQuery({
    queryKey: [sandbox, 'interaction-seq', props.seq],
    queryFn: async () =>
      /^\d+$/.test(props.seq)
        ? ((await api.interactions(sandbox, {}, { afterSeq: Number(props.seq) - 1, limit: 1 })).items[0] ?? null)
        : api.interaction(sandbox, props.seq),
    enabled: !loaded,
  })
  const i = loaded ?? fetched.data
  const related = useQuery({
    queryKey: [sandbox, 'correlation', i?.correlationId],
    queryFn: () => api.interactions(sandbox, { correlationId: i!.correlationId! }, { limit: 50 }),
    enabled: !!i?.correlationId,
  })

  if (!i)
    return (
      <aside className="panel aside" style={{ width: 500, padding: 16 }}>
        {fetched.isLoading ? 'Loading...' : <ErrorText error={fetched.error ?? 'record not found'} />}
      </aside>
    )

  const isHttp = i.method !== null
  const changes =
    i.kind === 'StateTransition'
      ? [i]
      : !isCall(i)
        ? []
        : [...(related.data?.items ?? [])].reverse().filter(t => t.kind === 'StateTransition' && !t.byClock && t.correlationId === i.correlationId)
  // Inbound calls carry no entity type: a connector with a single type has only one answer.
  const types = connectors.data?.find(c => c.name === i.connector)?.entities ?? []
  const entityType = i.entityType ?? (types.length === 1 ? types[0].type : null)
  const entityLink = i.entityId && entityType ? `/s/${sandbox}/entities/${i.connector}/${entityType}/${encodeURIComponent(i.entityId)}` : null
  const copy = async (what: string, text: string) => {
    setCopied([what, (await copyText(text)) ? 'Copied' : 'Could not copy'])
    setTimeout(() => setCopied(['', '']), 1500)
  }
  const copiedLabel = (what: string, label: string) => (copied[0] === what ? copied[1] : label)
  const tabs: [Tab, string][] = [
    ['summary', 'Summary'],
    ['request', 'Request'],
    ['response', 'Response'],
    ['correlation', 'Same correlation'],
  ]

  return (
    <aside className="panel aside" style={{ width: 500 }} aria-label="Call detail">
      <div style={{ padding: '14px 16px 10px' }} className="col">
        <div className="row">
          <Pill tone={toneOf(i)} />
          <span className="mono muted small">#{i.seq}</span>
          <div className="grow" />
          <button className="chip-btn" aria-label="Close detail" onClick={props.onClose}>
            <X size={14} aria-hidden />
          </button>
        </div>
        <h2 className="mono" style={{ fontSize: 13.5, fontWeight: 600, overflowWrap: 'anywhere', marginTop: 6 }}>
          {i.summary}
        </h2>
      </div>
      <Tabs id="feed-detail" tabs={tabs} value={tab} onChange={setTab} />
      <div {...tabPanel('feed-detail', tab)} className="col" style={{ padding: '14px 16px 16px', gap: 12 }}>
        {tab === 'summary' && (
          <dl className="kv">
            <dt>Result</dt>
            <dd>
              <Pill tone={result(i)} />
            </dd>
            <dt>Change</dt>
            <dd className="mono">{changes.length ? changes.map(change).join(', ') : 'none'}</dd>
            <dt>Time (sandbox)</dt>
            <dd className="mono">{dayTime(i.at)}</dd>
            <dt>Duration</dt>
            <dd className="mono">{ms(i.durationMs) || 'N/A'}</dd>
            <dt>Connector</dt>
            <dd className="mono">{i.connector}</dd>
            <dt>Entity</dt>
            <dd className="mono">{entityLink ? <Link to={entityLink}>{i.entityId}</Link> : (i.entityId ?? 'N/A')}</dd>
            <dt>Fault applied</dt>
            <dd>{i.fault ?? 'none'}</dd>
            <dt>Note</dt>
            <dd>{i.detail ?? 'N/A'}</dd>
            <dt>Correlation</dt>
            <dd className="mono">
              {i.correlationId ? (
                <>
                  {i.correlationId} ·{' '}
                  <button className="linkbtn" onClick={() => props.onCorrelation(i.correlationId!)}>
                    filter
                  </button>
                </>
              ) : (
                'N/A'
              )}
            </dd>
          </dl>
        )}
        {tab === 'request' && (
          <>
            <div>
              <span className="lbl">Headers</span>
              <pre className="code mono">
                {isHttp ? Object.entries(i.requestHeaders ?? {}).map(([k, v]) => `${k}: ${v}`).join('\n') || '(no headers)' : '(internal record, no HTTP)'}
              </pre>
            </div>
            <div>
              <span className="lbl">Body {isHttp && <span className="mono">· {i.method} {i.path}</span>}</span>
              <Code value={i.requestBody} />
            </div>
            <p className="small muted" style={{ margin: 0 }}>
              Credentials appear as [redacted] and personal data (tax IDs, names, emails, phone numbers) arrives masked.
            </p>
          </>
        )}
        {tab === 'response' && (
          <>
            <div>
              <span className="lbl">Response body</span>
              <Code value={i.responseBody} />
            </div>
            {i.truncated && (
              <p className="small" style={{ margin: 0, color: 'var(--warn-fg)' }}>
                Content truncated at 64 KB.
              </p>
            )}
          </>
        )}
        {tab === 'correlation' && (
          <div className="col" style={{ gap: 6 }}>
            {!i.correlationId && <span className="muted">No correlation.</span>}
            {related.isLoading && <span className="muted">Loading...</span>}
            <ErrorText error={related.error} />
            {[...(related.data?.items ?? [])].reverse().map(r => (
              <button
                key={r.seq}
                onClick={() => props.onPick(r.seq)}
                className="row box"
                style={{ background: r.seq === i.seq ? 'var(--info-bg)' : 'var(--panel)', cursor: 'pointer', font: 'inherit', color: 'var(--fg)', textAlign: 'left' }}
              >
                <Pill tone={toneOf(r)} />
                <span className="mono grow" style={{ fontSize: 12, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {r.summary}
                </span>
                <span className="mono muted" style={{ fontSize: 11 }}>
                  {hms(r.at)}
                </span>
              </button>
            ))}
          </div>
        )}
        <div className="row" style={{ borderTop: '1px solid var(--line-soft)', paddingTop: 12 }}>
          {isHttp && i.kind !== 'OutboundWebhook' && (
            <button className="btn btn-sec" onClick={() => copy('curl', asCurl(sandbox, i))}>
              <Copy size={14} aria-hidden />
              {copiedLabel('curl', 'Copy as curl')}
            </button>
          )}
          <button className="btn btn-sec" onClick={() => copy('all', asEvidence(i, entityType, changes))}>
            <Copy size={14} aria-hidden />
            {copiedLabel('all', 'Copy all')}
          </button>
          {entityLink && (
            <Link className="btn btn-sec" to={entityLink}>
              Open entity
            </Link>
          )}
        </div>
      </div>
    </aside>
  )
}
