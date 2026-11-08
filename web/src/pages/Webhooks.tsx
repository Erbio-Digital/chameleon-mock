import { useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type Attempt, type DeliveryStatus } from '@/api'
import { Code, ErrorText, Header, Pill, tabPanel, Tabs } from '@/components/common'
import { Registrations } from '@/components/Registrations'
import { deliveryTone, hms, ms, span, useNow, type Tone } from '@/format'

const filters: ['' | DeliveryStatus, string][] = [
  ['', 'All'],
  ['Pending', 'Pending'],
  ['Delivering', 'Delivering'],
  ['Delivered', 'Delivered'],
  ['DeadLettered', 'Failed'],
]

export default function Webhooks() {
  const { sandbox = 'default', id } = useParams()
  const navigate = useNavigate()
  const now = useNow()
  const [status, setStatus] = useState<'' | DeliveryStatus>('')
  const [params, setParams] = useSearchParams()
  const view = params.get('view') === 'registrations' ? 'registrations' : ''
  const list = useQuery({ queryKey: [sandbox, 'webhooks'], queryFn: () => api.webhooks(sandbox) })
  const all = [...(list.data ?? [])].reverse() // newest first
  const visible = all.filter(d => !status || d.status === status)

  const next = (d: (typeof all)[number]) => {
    if (d.status === 'Delivering') return 'now'
    if (d.status !== 'Pending') return 'N/A'
    const left = (Date.parse(d.nextAttemptAt) - now) / 1000
    return left > 0 ? `in ${span(left)}` : 'now'
  }

  return (
    <>
      <Header title="Webhooks">Deliveries the simulator makes to the application, with retries and growing waits between them, and where each provider notifies.</Header>
      <Tabs
        id="webhooks"
        tabs={[
          ['', 'Deliveries'],
          ['registrations', 'Registrations'],
        ]}
        value={view}
        onChange={key => setParams(key ? { view: key } : {})}
      />
      {view === 'registrations' ? (
        <div {...tabPanel('webhooks', view)}>
          <Registrations sandbox={sandbox} />
        </div>
      ) : (
        <div {...tabPanel('webhooks', view)} className="col" style={{ gap: 16 }}>
          <div role="group" aria-label="Filter by status" className="row" style={{ flexWrap: 'wrap' }}>
            {filters.map(([value, label]) => (
              <button key={value} className="pillbtn" aria-pressed={status === value} onClick={() => setStatus(value)}>
                {label} · {value ? all.filter(d => d.status === value).length : all.length}
              </button>
            ))}
          </div>
          <ErrorText error={list.error} />

          <div className="split">
            <div className="panel" style={{ flex: 1, minWidth: 0, overflow: 'auto' }}>
              <table className="t">
                <thead>
                  <tr>
                    <th>Status</th>
                    <th>Connector</th>
                    <th>Entity</th>
                    <th>Event</th>
                    <th>Destination</th>
                    <th>Attempts</th>
                    <th>Next</th>
                  </tr>
                </thead>
                <tbody>
                  {visible.map(d => (
                    <tr key={d.id} className={d.id === id ? 'sel' : undefined}>
                      <td>
                        <Pill tone={deliveryTone[d.status]} />
                      </td>
                      <td className="mono">{d.connector}</td>
                      <td className="mono" style={{ color: 'var(--fg-soft)' }}>
                        {d.entityId}
                      </td>
                      <td className="mono">
                        <button className="rowbtn" onClick={() => navigate(`/s/${sandbox}/webhooks/${d.id}`)}>
                          {d.event ?? 'webhook'}
                        </button>
                      </td>
                      <td className="mono muted" style={{ maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                        {d.url.replace(/^https?:\/\//, '')}
                      </td>
                      <td className="mono">
                        {d.attempts}/{d.maxAttempts}
                      </td>
                      <td className="mono muted">{next(d)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {list.data && visible.length === 0 && <div className="empty">No deliveries{status ? ' with this status' : ' yet in this sandbox'}.</div>}
            </div>
            {id && <Detail sandbox={sandbox} id={id} />}
          </div>
        </div>
      )}
    </>
  )
}

function attemptTone(a: Attempt): Tone {
  if (a.blocked) return ['Blocked', 'var(--err-bg)', 'var(--err-fg)']
  if (a.statusCode === null) return [/time/i.test(a.error ?? '') ? 'Timeout' : 'Error', 'var(--err-bg)', 'var(--err-fg)']
  return [String(a.statusCode), a.statusCode < 300 ? 'var(--ok-bg)' : 'var(--err-bg)', a.statusCode < 300 ? 'var(--ok-fg)' : 'var(--err-fg)']
}

function Detail({ sandbox, id }: { sandbox: string; id: string }) {
  const queryClient = useQueryClient()
  const detail = useQuery({ queryKey: [sandbox, 'webhook', id], queryFn: () => api.webhook(sandbox, id) })
  const sandboxes = useQuery({ queryKey: ['sandboxes'], queryFn: api.sandboxes })
  const [other, setOther] = useState(false)
  const [url, setUrl] = useState('')
  const resend = useMutation({
    mutationFn: () => api.resendWebhook(sandbox, id, other ? url.trim() : undefined),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [sandbox] }),
  })

  if (!detail.data)
    return (
      <aside className="panel aside" style={{ width: 480, padding: 16 }}>
        {detail.error ? <ErrorText error={detail.error} /> : 'Loading...'}
      </aside>
    )

  const { delivery: d, attempts, payload } = detail.data
  const ownPolicy = sandboxes.data?.find(s => s.id === sandbox)?.webhookPolicy
  const busy = d.status === 'Delivering' || d.status === 'Pending'

  return (
    <aside className="panel aside" style={{ width: 480 }} aria-label="Delivery detail">
      <div className="aside-head">
        <div className="row">
          <Pill tone={deliveryTone[d.status]} />
          <span className="mono muted" style={{ fontSize: 11.5 }}>
            {d.id}
          </span>
        </div>
        <h2 className="mono" style={{ fontSize: 13, fontWeight: 600, overflowWrap: 'anywhere' }}>
          POST {d.url}
        </h2>
        <div className="small muted">
          <span className="mono">{d.connector}</span> · <Link className="mono" to={`/s/${sandbox}/feed?entityId=${encodeURIComponent(d.entityId)}`}>{d.entityId}</Link>
          {d.correlationId && (
            <>
              {' '}
              · correlation <span className="mono">{d.correlationId.slice(0, 12)}</span>
            </>
          )}
        </div>
      </div>
      <div className="aside-body">
        <section style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 10 }}>
          {[
            ['Attempts', `${d.attempts}/${d.maxAttempts}`],
            ['Base wait', ms(d.baseDelayMs) || '0 ms'],
            ['Timeout', ms(d.timeoutMs)],
          ].map(([label, value]) => (
            <div key={label} className="box">
              <div className="muted" style={{ fontSize: 11 }}>
                {label}
              </div>
              <div className="mono" style={{ fontWeight: 600 }}>
                {value}
              </div>
            </div>
          ))}
          <div className="small muted" style={{ gridColumn: '1 / -1' }}>
            Policy: {ownPolicy ? "the sandbox's own" : `default of the ${d.connector} connector`}.{' '}
            <Link to={`/s/${sandbox}/sandboxes/${sandbox}`}>Change in the sandbox</Link>
          </div>
        </section>
        {d.note && <div className="note warn">{d.note}</div>}

        <section>
          <h3 className="h3">Attempts</h3>
          <div className="col" style={{ gap: 6 }}>
            {[...attempts].reverse().map(a => (
              <div key={a.number} className="box" style={{ fontSize: 12.5 }}>
                <div className="row">
                  <b>#{a.number}</b>
                  <Pill tone={attemptTone(a)} />
                  <span className="mono muted" style={{ fontSize: 11.5 }}>
                    {a.blocked ? '' : ms(a.durationMs)}
                  </span>
                  <div className="grow" />
                  <span className="mono muted" style={{ fontSize: 11.5 }}>
                    {hms(a.at)}
                  </span>
                </div>
                <div className="mono" style={{ fontSize: 11.5, color: 'var(--fg-soft)', marginTop: 4, overflowWrap: 'anywhere' }}>
                  {a.url !== d.url ? `${a.url} · ` : ''}
                  {a.error ?? (a.responseBody || '(no body)')}
                </div>
              </div>
            ))}
            {attempts.length === 0 && <span className="muted">First attempt in progress.</span>}
          </div>
        </section>

        <section>
          <h3 className="h3">Payload</h3>
          <Code value={payload} />
        </section>

        <section className="col" style={{ borderTop: '1px solid var(--line-soft)', paddingTop: 14, gap: 10 }}>
          <h3 className="h3" style={{ margin: 0 }}>
            Resend
          </h3>
          <label className="radio">
            <input type="radio" name="dest" checked={!other} onChange={() => setOther(false)} />
            Same destination
          </label>
          <label className="radio">
            <input type="radio" name="dest" checked={other} onChange={() => setOther(true)} />
            Another destination
          </label>
          {other && <input className="field mono" aria-label="Destination URL" placeholder="https://..." value={url} onChange={e => setUrl(e.target.value)} />}
          <div className="row" style={{ gap: 10 }}>
            <button className="btn btn-pri" disabled={busy || resend.isPending || (other && !url.trim())} onClick={() => resend.mutate()}>
              Resend now
            </button>
            <span className="small muted">{busy ? 'Wait for the delivery in progress.' : 'Makes an extra attempt now, outside the policy.'}</span>
          </div>
          <ErrorText error={resend.error} />
        </section>
      </div>
    </aside>
  )
}
