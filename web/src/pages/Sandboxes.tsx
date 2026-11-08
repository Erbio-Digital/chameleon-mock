import { useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { api, type Sandbox } from '@/api'
import { ErrorText, Header, Pill } from '@/components/common'
import { dayTime, expiresText, ms, sandboxClock, sandboxKind, useNow } from '@/format'

const idPattern = /^[a-z0-9][a-z0-9-]{0,39}$/

export default function Sandboxes() {
  const { sandbox = 'default', id } = useParams()
  const navigate = useNavigate()
  const now = useNow()
  const list = useQuery({ queryKey: ['sandboxes'], queryFn: api.sandboxes })
  const [creating, setCreating] = useState(false)
  const selected = list.data?.find(s => s.id === (id ?? sandbox)) ?? list.data?.[0]
  const clockText = (s: Sandbox) => dayTime(sandboxClock(s, list.dataUpdatedAt, now).at) + (s.clockMode === 'Frozen' ? ' · frozen' : '')

  return (
    <>
      <Header
        title="Sandboxes"
        actions={
          <button className="btn btn-pri" onClick={() => setCreating(c => !c)}>
            <Plus size={14} aria-hidden />
            New sandbox
          </button>
        }
      >
        Each sandbox isolates entities, history, faults, webhooks and clock. Permanent
        ones never expire; temporary ones expire by TTL.
      </Header>

      {creating && list.data && (
        <NewSandbox
          sandboxes={list.data}
          current={sandbox}
          onCreated={s => {
            setCreating(false)
            navigate(`/s/${sandbox}/sandboxes/${s.id}`)
          }}
        />
      )}
      <ErrorText error={list.error} />

      <div className="cols-aside">
        <section className="panel" style={{ overflow: 'auto' }}>
          <table className="t">
            <thead>
              <tr>
                <th>Id</th>
                <th>Type</th>
                <th>Clock</th>
                <th>Validity</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {list.data?.map(s => (
                <tr key={s.id} className={`click${s.id === selected?.id ? ' sel' : ''}`} onClick={() => navigate(`/s/${sandbox}/sandboxes/${s.id}`)}>
                  <td className="mono" style={{ fontWeight: 600 }}>
                    <button className="rowbtn" aria-label={`Open sandbox ${s.id}`}>
                      {s.id}
                    </button>
                  </td>
                  <td>
                    <Pill tone={sandboxKind(s)} />
                  </td>
                  <td className="mono muted">{clockText(s)}</td>
                  <td className="muted">{expiresText(s, now)}</td>
                  <td style={{ textAlign: 'right' }}>{s.webhookPolicy && <span className="tag">OWN POLICY</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
        {selected && <Detail key={selected.id} s={selected} clock={clockText(selected)} current={sandbox} />}
      </div>
    </>
  )
}

function NewSandbox({ sandboxes, current, onCreated }: { sandboxes: Sandbox[]; current: string; onCreated: (s: Sandbox) => void }) {
  const queryClient = useQueryClient()
  const [id, setId] = useState('')
  const [from, setFrom] = useState('')
  const [mode, setMode] = useState('Realtime')
  const [ttl, setTtl] = useState('24')
  const [permanent, setPermanent] = useState(false)
  const problem = !id
    ? 'Lowercase letters, digits and hyphen; up to 40 characters.'
    : !idPattern.test(id)
      ? 'Invalid format: lowercase letters, digits and hyphen, starting with a letter or digit.'
      : sandboxes.some(s => s.id === id)
        ? 'A sandbox with this id already exists.'
        : ''
  const hours = Number(ttl)
  const create = useMutation({
    mutationFn: () =>
      api.createSandbox({ id, clockMode: mode, ttlSeconds: permanent ? undefined : Math.round(hours * 3600), from: from || undefined, permanent }),
    onSuccess: s => {
      queryClient.invalidateQueries({ queryKey: ['sandboxes'] })
      onCreated(s)
    },
  })

  return (
    <form
      className="panel row"
      style={{ padding: 14, flexWrap: 'wrap', gap: 12, alignItems: 'flex-start' }}
      onSubmit={e => {
        e.preventDefault()
        create.mutate()
      }}
    >
      <label className="grow">
        <span className="lbl">Id</span>
        <input className="field mono" style={{ width: '100%' }} placeholder="my-test-sandbox" value={id} onChange={e => setId(e.target.value.trim())} aria-invalid={!!id && !!problem} autoFocus />
        <span style={{ display: 'block', fontSize: 11.5, marginTop: 4, color: id && problem ? 'var(--err-fg)' : id ? 'var(--ok-fg)' : 'var(--fg2)' }}>{problem || 'Available.'}</span>
      </label>
      <label style={{ width: 190 }}>
        <span className="lbl">Copy from</span>
        <select className="field" style={{ width: '100%' }} value={from} onChange={e => setFrom(e.target.value)}>
          <option value="">Nothing (empty)</option>
          {sandboxes.map(s => (
            <option key={s.id} value={s.id}>
              {s.id}
            </option>
          ))}
        </select>
      </label>
      <label style={{ width: 160 }}>
        <span className="lbl">Clock</span>
        <select className="field" style={{ width: '100%' }} value={from ? '' : mode} disabled={!!from} onChange={e => setMode(e.target.value)}>
          {from && <option value="">Same as the source</option>}
          <option value="Realtime">Real time</option>
          <option value="Frozen">Frozen</option>
        </select>
      </label>
      <label style={{ width: 120 }}>
        <span className="lbl">TTL (horas)</span>
        <input className="field mono" style={{ width: '100%' }} type="number" min={1} max={24} value={permanent ? '' : ttl} disabled={permanent} onChange={e => setTtl(e.target.value)} />
      </label>
      <label className="row" style={{ marginTop: 26, cursor: 'pointer', whiteSpace: 'nowrap' }}>
        <input type="checkbox" checked={permanent} onChange={e => setPermanent(e.target.checked)} />
        Permanent
      </label>
      <button type="submit" className="btn btn-pri" style={{ marginTop: 19 }} disabled={!!problem || !(permanent || (hours >= 1 && hours <= 24)) || create.isPending}>
        Create
      </button>
      <div className="note info" style={{ flexBasis: '100%' }}>
        <b>What a temporary sandbox is for.</b> Moving the clock or arming faults without affecting anyone testing in another sandbox: an isolated manual test,
        a service running on your machine or an integration test. With <b>Copy from</b>, it starts with the entities, faults, policy and clock of the source (without history),
        and nothing that happens in it flows back to the source. Point the provider URL to <span className="mono">/s/{id || 'your-id'}/</span>; the TTL deletes everything on its own.
        {current !== 'default' && ' Name suggestion: your name and the task.'}
      </div>
      {permanent && (
        <div className="note warn" style={{ flexBasis: '100%' }}>
          <b>Permanent</b> never expires, cannot be removed and a reset requires
          typing its id. Use it for a long-lived shared environment, not for a test.
        </div>
      )}
      <ErrorText error={create.error} />
    </form>
  )
}

function Detail({ s, clock, current }: { s: Sandbox; clock: string; current: string }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors })
  const [advance, setAdvance] = useState('3600')
  const [confirm, setConfirm] = useState('')
  const [asking, setAsking] = useState<'' | 'reset' | 'remove'>('')
  const [message, setMessage] = useState('')
  const done = (text = '') => {
    setMessage(text)
    queryClient.invalidateQueries({ queryKey: ['sandboxes'] })
    queryClient.invalidateQueries({ queryKey: [s.id] })
  }
  const act = useMutation({ mutationFn: (run: () => Promise<unknown>) => run(), onSettled: () => setAsking('') })
  const run = (fn: () => Promise<unknown>, text?: string) => act.mutate(fn, { onSuccess: () => done(text) })

  const reset = () => {
    setConfirm('')
    run(() => api.resetSandbox(s.id, s.persistent ? s.id : undefined), `Sandbox ${s.id} was reset.`)
  }
  const remove = () =>
    act.mutate(() => api.removeSandbox(s.id, s.persistent ? s.id : undefined), {
      onSuccess: () => {
        done()
        navigate(s.id === current ? '/s/default/sandboxes' : `/s/${current}/sandboxes`)
      },
    })
  const kind = sandboxKind(s)
  const removable = s.id !== 'default'

  return (
    <div className="col" style={{ gap: 16 }}>
      <section className="panel" style={{ padding: 16 }}>
        <div className="row" style={{ gap: 10 }}>
          <h2 className="mono grow" style={{ fontSize: 16 }}>
            {s.id}
          </h2>
          <Pill tone={kind} />
        </div>
        <h3 className="h3" style={{ marginTop: 16 }}>
          Clock
        </h3>
        <div className="mono" style={{ fontWeight: 600, marginBottom: 8 }}>
          {clock}
        </div>
        <div className="row" style={{ whiteSpace: 'nowrap' }}>
          <input className="field mono" style={{ width: 90 }} type="number" min={1} value={advance} onChange={e => setAdvance(e.target.value)} aria-label="Seconds to advance or go back" />
          <button className="btn btn-sec" disabled={!(Number(advance) > 0)} onClick={() => run(() => api.advanceClock(s.id, Number(advance)))}>
            Advance
          </button>
          <button className="btn btn-sec" disabled={!(Number(advance) > 0)} onClick={() => run(() => api.advanceClock(s.id, -Number(advance)))}>
            Back
          </button>
          <button className="btn btn-sec" onClick={() => run(() => api.setClockMode(s.id, s.clockMode === 'Frozen' ? 'Realtime' : 'Frozen'))}>
            {s.clockMode === 'Frozen' ? 'Real time' : 'Freeze'}
          </button>
          <button className="btn btn-sec" title="Back to the real time and to real-time flow" onClick={() => run(() => api.clockNow(s.id))}>
            Now
          </button>
        </div>
        <p className="small muted" style={{ margin: '6px 0 0' }}>
          Advancing triggers entity expiries and pending webhooks. Going back only moves the clock back: what has already expired is not undone or
          reprocessed, and what is pending gets further away. In seconds.
        </p>
      </section>

      <Policy key={JSON.stringify(s.webhookPolicy)} s={s} defaults={connectors.data?.flatMap(c => (c.webhooks ? [[c.name, c.webhooks] as const] : [])) ?? []} />

      <WebhookHeaders key={JSON.stringify(s.webhookHeaders)} s={s} />

      <section className="panel" style={{ padding: 16, borderColor: 'var(--err-line)' }}>
        <h2 style={{ fontSize: 14, marginBottom: 6 }}>Danger zone</h2>
        <p className="muted" style={{ margin: '0 0 10px', fontSize: 12.5 }}>
          Reset deletes everything in this sandbox: subscriptions (credentials, certificates and .pfx password), accounts, entities, history, armed faults and webhooks, and resets the clock. Services pointed at it must be reconfigured. The webhook policy and headers are kept.
        </p>
        {s.persistent && (
          <label>
            <span className="lbl">
              Type <span className="mono">{s.id}</span> to confirm
            </span>
            <input className="field mono" value={confirm} onChange={e => setConfirm(e.target.value)} autoComplete="off" />
          </label>
        )}
        <div className="row" style={{ marginTop: 10, flexWrap: 'wrap' }}>
          {asking ? (
            <>
              <span className="small">{asking === 'reset' ? 'Deletes everything in this sandbox.' : `The sandbox ${s.id} will cease to exist.`}</span>
              <button className="btn btn-danger" disabled={act.isPending} onClick={() => (asking === 'reset' ? reset() : remove())}>
                Confirm
              </button>
              <button className="btn btn-sec" onClick={() => setAsking('')}>
                Cancel
              </button>
            </>
          ) : (
            <>
              {/* A persistent sandbox already confirms by typing its id. */}
              <button
                className="btn btn-danger"
                disabled={(s.persistent && confirm !== s.id) || act.isPending}
                onClick={() => (s.persistent ? reset() : setAsking('reset'))}
              >
                Reset
              </button>
              {removable && (
                <button className="btn btn-danger-ghost" disabled={(s.persistent && confirm !== s.id) || act.isPending} onClick={() => setAsking('remove')}>
                  Remove
                </button>
              )}
            </>
          )}
          <span className="small" style={{ color: 'var(--ok-fg)' }}>
            {message}
          </span>
        </div>
        {!removable && (
          <p className="small muted" style={{ margin: '8px 0 0' }}>
            The default sandbox cannot be removed.
          </p>
        )}
        <ErrorText error={act.error} />
      </section>
    </div>
  )
}

function Policy({ s, defaults }: { s: Sandbox; defaults: (readonly [string, { maxAttempts: number; baseDelayMs: number; timeoutMs: number }])[] }) {
  const queryClient = useQueryClient()
  const [useDefault, setUseDefault] = useState(!s.webhookPolicy)
  const [tries, setTries] = useState(String(s.webhookPolicy?.maxAttempts ?? 5))
  const [base, setBase] = useState(String(s.webhookPolicy?.baseDelayMs ?? 2000))
  const [timeout, setTimeoutMs] = useState(String(s.webhookPolicy?.timeoutMs ?? 10000))
  const [saved, setSaved] = useState('')
  const t = Number(tries)
  const b = Number(base)
  const to = Number(timeout)
  // Same limits as the API (WebhookPolicy.Invalid).
  const problem = useDefault
    ? ''
    : !(Number.isInteger(t) && t >= 1 && t <= 20)
      ? 'Attempts between 1 and 20.'
      : !(b >= 0 && b <= 600_000)
        ? 'Base wait between 0 and 600000 ms.'
        : !(to >= 100 && to <= 30_000)
          ? 'Timeout between 100 and 30000 ms.'
          : ''
  const waits = Array.from({ length: Math.max(0, Math.min(t, 6) - 1) }, (_, i) => ms(b * 2 ** i) || '0 ms')
  const save = useMutation({
    mutationFn: () => api.setWebhookPolicy(s.id, useDefault ? null : { maxAttempts: t, baseDelayMs: b, timeoutMs: to }),
    onSuccess: () => {
      setSaved(useDefault ? 'Policy removed; the defaults apply.' : 'Policy saved. New deliveries already use these values.')
      queryClient.invalidateQueries({ queryKey: ['sandboxes'] })
    },
  })

  return (
    <section className="panel" style={{ padding: 16 }}>
      <div className="row" style={{ marginBottom: 12 }}>
        <h2 className="grow" style={{ fontSize: 14 }}>
          Webhook policy
        </h2>
      </div>
      <label className="row" style={{ marginBottom: 12, cursor: 'pointer' }}>
        <input type="checkbox" checked={useDefault} onChange={e => (setUseDefault(e.target.checked), setSaved(''))} />
        Use each connector's default
      </label>
      {useDefault ? (
        <table className="t">
          <thead>
            <tr>
              <th>Connector</th>
              <th>Attempts</th>
              <th>Base</th>
              <th>Timeout</th>
            </tr>
          </thead>
          <tbody>
            {defaults.map(([name, p]) => (
              <tr key={name}>
                <td className="mono">{name}</td>
                <td className="mono">{p.maxAttempts}</td>
                <td className="mono">{p.baseDelayMs} ms</td>
                <td className="mono">{p.timeoutMs} ms</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 10 }}>
            <label>
              <span className="lbl">Attempts (1 to 20)</span>
              <input className="field mono" style={{ width: '100%' }} type="number" min={1} max={20} value={tries} onChange={e => (setTries(e.target.value), setSaved(''))} />
            </label>
            <label>
              <span className="lbl">Base wait (ms)</span>
              <input className="field mono" style={{ width: '100%' }} type="number" min={0} value={base} onChange={e => (setBase(e.target.value), setSaved(''))} />
            </label>
            <label>
              <span className="lbl">Timeout (ms)</span>
              <input className="field mono" style={{ width: '100%' }} type="number" min={100} value={timeout} onChange={e => (setTimeoutMs(e.target.value), setSaved(''))} />
            </label>
          </div>
          <p className="small" style={{ margin: '8px 0 0', color: problem ? 'var(--err-fg)' : 'var(--fg2)' }}>
            {problem || `Waits between attempts: ${waits.join(', ')}${t > 6 ? '...' : ''}. Applies to every connector in this sandbox.`}
          </p>
        </>
      )}
      <div className="row" style={{ marginTop: 12 }}>
        <button className="btn btn-pri" disabled={!!problem || save.isPending} onClick={() => save.mutate()}>
          Save policy
        </button>
        <span className="small" style={{ color: 'var(--ok-fg)' }}>
          {saved}
        </span>
      </div>
      <ErrorText error={save.error} />
    </section>
  )
}

/** Extra headers on webhooks to one host (a test receiver's key). Saved values are never read back. */
function WebhookHeaders({ s }: { s: Sandbox }) {
  const queryClient = useQueryClient()
  const [host, setHost] = useState('')
  const [name, setName] = useState('')
  const [value, setValue] = useState('')
  const current = s.webhookHeaders.map(h => ({ ...h, value: null as string | null }))
  const save = useMutation({
    mutationFn: (headers: { host: string; name: string; value: string | null }[]) => api.setWebhookHeaders(s.id, headers),
    onSuccess: () => {
      setHost('')
      setName('')
      setValue('')
      return queryClient.invalidateQueries({ queryKey: ['sandboxes'] })
    },
  })
  const valid = /^[a-z0-9.-]+$/i.test(host.trim()) && /^[!#$%&'*+.^_`|~0-9a-z-]+$/i.test(name.trim()) && value !== ''

  return (
    <section className="panel" style={{ padding: 16 }}>
      <h2 style={{ fontSize: 14, marginBottom: 6 }}>Webhook headers</h2>
      <p className="small muted" style={{ margin: '0 0 10px' }}>
        Sent only on webhooks to the given host, for example the key of a test receiver. The real provider does not send it: use it only when the
        destination requires it. The value is not shown once saved and appears as [redacted] in the history.
      </p>
      {current.length > 0 && (
        <table className="t" style={{ marginBottom: 12 }}>
          <thead>
            <tr>
              <th>Host</th>
              <th>Header</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {current.map(h => (
              <tr key={`${h.host} ${h.name}`}>
                <td className="mono">{h.host}</td>
                <td className="mono">{h.name}: ••••••</td>
                <td style={{ textAlign: 'right' }}>
                  <button className="btn btn-sec" disabled={save.isPending} onClick={() => save.mutate(current.filter(o => o !== h))}>
                    Remove
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr 2fr auto', gap: 10, alignItems: 'end' }}>
        <label>
          <span className="lbl">Host</span>
          <input className="field mono" style={{ width: '100%' }} placeholder="hooks.example.com" value={host} onChange={e => setHost(e.target.value)} />
        </label>
        <label>
          <span className="lbl">Header</span>
          <input className="field mono" style={{ width: '100%' }} placeholder="x-api-key" value={name} onChange={e => setName(e.target.value)} />
        </label>
        <label>
          <span className="lbl">Value</span>
          <input className="field mono" style={{ width: '100%' }} type="password" autoComplete="off" value={value} onChange={e => setValue(e.target.value)} />
        </label>
        <button
          className="btn btn-pri"
          disabled={!valid || save.isPending}
          onClick={() =>
            save.mutate([
              ...current.filter(o => !(o.host === host.trim().toLowerCase() && o.name.toLowerCase() === name.trim().toLowerCase())),
              { host: host.trim().toLowerCase(), name: name.trim(), value },
            ])
          }
        >
          Save
        </button>
      </div>
      <ErrorText error={save.error} />
    </section>
  )
}
