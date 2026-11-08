import { useState, type FormEvent } from 'react'
import { Link, Navigate, useNavigate, useParams } from 'react-router'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type ActionDescriptor, type Connector, type EntityDescriptor } from '@/api'
import { Code, ErrorText, Header, Modal, Pill, StateFlow } from '@/components/common'
import { dayTime, deliveryTone, hms, sandboxClock, span, stateTone, useNow } from '@/format'

export default function Entities() {
  const { sandbox = 'default', connector, type, key } = useParams()
  const navigate = useNavigate()
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors })
  const types = (connectors.data ?? []).flatMap(c => c.entities.map(e => ({ connector: c.name, type: e.type })))
  const lists = useQueries({
    queries: types.map(t => ({
      queryKey: [sandbox, 'entities', t.connector, t.type],
      queryFn: () => api.entities(sandbox, t.connector, t.type),
    })),
  })

  const descriptor = connectors.data?.find(c => c.name === connector)
  const entity = descriptor?.entities.find(e => e.type === type)

  if (!connectors.data) return connectors.isLoading ? <div className="empty">Loading...</div> : <ErrorText error={connectors.error} />
  if (!types[0]) return <div className="empty">No connector has entities.</div>
  // No type in the URL, or one this instance does not have (old link): the first one.
  if (!descriptor || !entity) return <Navigate to={`/s/${sandbox}/entities/${types[0].connector}/${types[0].type}`} replace />

  const current = lists[types.findIndex(t => t.connector === connector && t.type === type)]
  const base = `/s/${sandbox}/entities/${connector}/${type}`

  return (
    <>
      <Header title="Entities">
        What the application created in the sandbox <span className="mono">{sandbox}</span>. States, actions and diagram come from each connector's description.
      </Header>
      <div className="split">
        <div className="panel" style={{ width: 210, flex: '0 0 210px', padding: 8 }}>
          {connectors.data?.map(c => (
            <div key={c.name}>
              <div className="mono muted" style={{ padding: '8px 10px 4px', fontSize: 11, fontWeight: 600 }}>
                {c.name}
              </div>
              {c.entities.map(e => {
                const count = lists[types.findIndex(t => t.connector === c.name && t.type === e.type)]?.data?.length
                return (
                  <button
                    key={e.type}
                    className="typebtn"
                    aria-pressed={c.name === connector && e.type === type}
                    onClick={() => navigate(`/s/${sandbox}/entities/${c.name}/${e.type}`)}
                  >
                    <span className="mono grow">{e.type}</span>
                    <span className="muted" style={{ fontSize: 11.5 }}>
                      {count ?? ''}
                    </span>
                  </button>
                )
              })}
            </div>
          ))}
        </div>

        <div className="panel" style={{ flex: 1, minWidth: 0, overflow: 'auto' }}>
          <div className="panel-head">
            <h2 className="mono" style={{ fontSize: 14, fontWeight: 600 }}>
              {connector} · {type}
            </h2>
          </div>
          <ErrorText error={current?.error} />
          {current?.isLoading && <div className="empty">Loading...</div>}
          <table className="t">
            <thead>
              <tr>
                <th>{entity?.key ?? 'Key'}</th>
                <th>State</th>
                <th>Version</th>
                <th>Updated</th>
              </tr>
            </thead>
            <tbody>
              {current?.data?.map(r => (
                <tr key={r.key} className={r.key === key ? 'sel' : undefined}>
                  <td className="mono">
                    <button className="rowbtn" onClick={() => navigate(`${base}/${encodeURIComponent(r.key)}`)}>
                      {r.key}
                    </button>
                  </td>
                  <td>
                    <Pill tone={stateTone(r.state)} />
                  </td>
                  <td className="mono">{r.version}</td>
                  <td className="mono muted">{hms(r.updatedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {current?.data?.length === 0 && <div className="empty">None yet in this sandbox.</div>}
          <p className="small muted" style={{ margin: 0, padding: '12px 14px' }}>
            Entities are created by the application under test.
          </p>
          <Prepare sandbox={sandbox} descriptor={descriptor} entityType={entity} onDone={k => navigate(`${base}/${encodeURIComponent(k)}`)} />
        </div>

        {key && <Detail sandbox={sandbox} descriptor={descriptor} entityType={entity} entityKey={key} />}
      </div>
    </>
  )
}

// Arms a key the app has not used yet, with the actions the connector marks as creating it.
function Prepare(props: { sandbox: string; descriptor: Connector; entityType: EntityDescriptor; onDone: (key: string) => void }) {
  const { sandbox, descriptor, entityType } = props
  const queryClient = useQueryClient()
  const [entityKey, setEntityKey] = useState('')
  const [asking, setAsking] = useState<ActionDescriptor | null>(null)
  const run = useMutation({
    mutationFn: ({ action, args }: { action: string; args?: Record<string, unknown> }) =>
      api.runAction(sandbox, descriptor.name, entityType.type, entityKey.trim(), action, args),
    onSuccess: res => {
      setAsking(null)
      queryClient.invalidateQueries({ queryKey: [sandbox] })
      // The connector may normalize the key (an id in upper case): open what it stored.
      const stored = (res as Record<string, unknown> | null)?.[entityType.key]
      props.onDone(typeof stored === 'string' ? stored : entityKey.trim())
      setEntityKey('')
    },
  })
  const actions = descriptor.actions.filter(a => a.entityType === entityType.type && a.createsMissing)
  if (!actions.length) return null

  const trigger = (a: ActionDescriptor) => {
    run.reset()
    if (a.parameters.length) setAsking(a)
    else run.mutate({ action: a.name })
  }
  return (
    <section style={{ padding: '0 14px 14px' }}>
      <h3 className="h3">Prepare before the first call</h3>
      <p className="small muted" style={{ margin: '0 0 8px' }}>
        For a {entityType.key} the application has not used yet (or one it caches): enter the key and pick the scenario.
      </p>
      <div className="row" style={{ flexWrap: 'wrap' }}>
        <input
          className="field mono"
          aria-label={entityType.key}
          placeholder={entityType.key}
          value={entityKey}
          onChange={ev => setEntityKey(ev.target.value)}
          style={{ width: 160 }}
        />
        {actions.map(a => (
          <button key={a.name} className="btn btn-sec" disabled={!entityKey.trim() || run.isPending} onClick={() => trigger(a)}>
            {a.label}
          </button>
        ))}
      </div>
      {!asking && <ErrorText error={run.error} />}
      {asking && (
        <ActionDialog action={asking} entityKey={entityKey.trim()} pending={run.isPending} error={run.error} onClose={() => setAsking(null)} onRun={args => run.mutate({ action: asking.name, args })} />
      )}
    </section>
  )
}

function Detail(props: { sandbox: string; descriptor: Connector; entityType: EntityDescriptor; entityKey: string }) {
  const { sandbox, descriptor, entityType, entityKey } = props
  const queryClient = useQueryClient()
  const now = useNow()
  const connector = descriptor.name
  const type = entityType.type
  const entity = useQuery({
    queryKey: [sandbox, 'entity', connector, type, entityKey],
    queryFn: () => api.entity(sandbox, connector, type, entityKey),
  })
  const sandboxes = useQuery({ queryKey: ['sandboxes'], queryFn: api.sandboxes })
  const [asking, setAsking] = useState<ActionDescriptor | null>(null)
  const run = useMutation({
    mutationFn: ({ action, args }: { action: string; args?: Record<string, unknown> }) =>
      api.runAction(sandbox, connector, type, entityKey, action, args),
    onSuccess: () => {
      setAsking(null)
      queryClient.invalidateQueries({ queryKey: [sandbox] })
    },
  })

  const e = entity.data
  if (!e)
    return (
      <aside className="panel aside" style={{ width: 470, padding: 16 }}>
        {entity.error ? <ErrorText error={entity.error} /> : 'Loading...'}
      </aside>
    )

  // Only what the current state allows; the rest is not offered.
  const actions = descriptor.actions.filter(a => a.entityType === type && e.allowedActions.includes(a.name))
  const sb = sandboxes.data?.find(s => s.id === sandbox)
  const sandboxNow = sb ? sandboxClock(sb, sandboxes.dataUpdatedAt, now).at : undefined
  const dueIn = e.dueAt && sandboxNow ? (Date.parse(e.dueAt) - sandboxNow) / 1000 : null
  const trigger = (a: ActionDescriptor) => {
    run.reset()
    if (a.parameters.length) setAsking(a)
    else run.mutate({ action: a.name })
  }
  const transitions = e.timeline.filter(i => i.kind === 'StateTransition').reverse()

  return (
    <aside className="panel aside" style={{ width: 470 }} aria-label="Entity detail">
      <div className="aside-head">
        <div className="row mono muted" style={{ fontSize: 11.5 }}>
          <span className="grow">
            {connector} · {type}
          </span>
          <span>version {e.version}</span>
        </div>
        <div className="row" style={{ gap: 10 }}>
          <h2 className="mono grow" style={{ fontSize: 13.5, fontWeight: 600, overflowWrap: 'anywhere' }}>
            {e.key}
          </h2>
          <Pill tone={stateTone(e.state)} />
        </div>
      </div>
      <div className="aside-body">
        <section>
          <h3 className="h3">Actions allowed now</h3>
          <div className="row" style={{ flexWrap: 'wrap' }}>
            {actions.map((a, i) => (
              <button key={a.name} className={`btn ${i === 0 ? 'btn-pri' : 'btn-sec'}`} disabled={run.isPending} onClick={() => trigger(a)}>
                {a.label}
              </button>
            ))}
            {actions.length === 0 && <span className="muted">No action available in {e.state}.</span>}
          </div>
          {!asking && <ErrorText error={run.error} />}
          {dueIn !== null && (
            <p className="small muted" style={{ margin: '8px 0 0' }}>
              {dueIn > 0 ? `Due by the clock in ${span(dueIn)}. Advance the clock at the top to make it due now.` : 'Due by the clock; the next cycle applies the expiry.'}
            </p>
          )}
        </section>

        <section>
          <h3 className="h3">Lifecycle</h3>
          <StateFlow entity={entityType} current={e.state} />
        </section>

        <section>
          <h3 className="h3">Timeline</h3>
          <ol className="col" style={{ listStyle: 'none', margin: 0, gap: 8, borderLeft: '2px solid var(--line)', paddingLeft: 12 }}>
            {transitions.map(t => (
              <li key={t.seq} style={{ fontSize: 12.5 }}>
                <Link className="mono" to={`/s/${sandbox}/feed/${t.seq}`} style={{ color: 'var(--fg)' }}>
                  {t.fromState ?? 'created'} → {t.toState}
                </Link>
                <span className="muted" style={{ display: 'block', fontSize: 11.5 }}>
                  {dayTime(t.at)}
                  {t.detail ? ` · ${t.detail}` : ''}
                </span>
              </li>
            ))}
          </ol>
        </section>

        <section>
          <h3 className="h3">Webhook deliveries</h3>
          {e.webhooks.map(w => (
            <Link key={w.id} to={`/s/${sandbox}/webhooks/${w.id}`} className="row box" style={{ textDecoration: 'none', color: 'var(--fg)', marginBottom: 6 }}>
              <Pill tone={deliveryTone[w.status]} />
              <span className="mono grow" style={{ fontSize: 11.5 }}>
                {w.event ?? 'webhook'}
              </span>
              <span className="muted" style={{ fontSize: 11.5 }}>
                attempts {w.attempts}/{w.maxAttempts}
              </span>
            </Link>
          ))}
          {e.webhooks.length === 0 && <span className="muted">No deliveries yet.</span>}
        </section>

        <section>
          <h3 className="h3">Data (masked)</h3>
          <Code value={e.data} />
        </section>
      </div>

      {asking && (
        <ActionDialog action={asking} entityKey={e.key} pending={run.isPending} error={run.error} onClose={() => setAsking(null)} onRun={args => run.mutate({ action: asking.name, args })} />
      )}
    </aside>
  )
}

function ActionDialog(props: {
  action: ActionDescriptor
  entityKey: string
  pending: boolean
  error: unknown
  onClose: () => void
  onRun: (args: Record<string, unknown>) => void
}) {
  const { action } = props
  const submit = (ev: FormEvent<HTMLFormElement>) => {
    ev.preventDefault()
    const form = new FormData(ev.currentTarget)
    const args: Record<string, unknown> = {}
    for (const p of action.parameters) {
      const raw = String(form.get(p.name) ?? '').trim()
      if (raw === '') continue
      args[p.name] = p.type === 'number' ? Number(raw) : p.type === 'boolean' ? raw === 'true' : raw
    }
    props.onRun(args)
  }
  return (
    <Modal label={action.label} onClose={props.onClose}>
      <form onSubmit={submit} className="col" style={{ gap: 14 }}>
        <p className="muted" style={{ margin: 0, fontSize: 12.5 }}>
          Action <span className="mono">{action.name}</span> on <span className="mono">{props.entityKey}</span>. The parameters come from the connector description.
        </p>
        {action.parameters.map((p, i) => (
          <label key={p.name}>
            <span className="lbl">
              {p.name} · {p.type} · {p.required ? 'required' : 'optional'}
            </span>
            {p.type === 'boolean' ? (
              <select className="field" style={{ width: '100%' }} name={p.name} required={p.required} autoFocus={i === 0} defaultValue="">
                <option value="">{p.required ? 'Choose' : 'Leave empty'}</option>
                <option value="true">Yes</option>
                <option value="false">No</option>
              </select>
            ) : (
              <input className="field" style={{ width: '100%' }} name={p.name} required={p.required} autoFocus={i === 0} type={p.type === 'number' ? 'number' : 'text'} />
            )}
          </label>
        ))}
        <ErrorText error={props.error} />
        <div className="row" style={{ justifyContent: 'flex-end' }}>
          <button type="button" className="btn btn-sec" onClick={props.onClose}>
            Cancel
          </button>
          <button type="submit" className="btn btn-pri" disabled={props.pending}>
            {action.label}
          </button>
        </div>
      </form>
    </Modal>
  )
}
