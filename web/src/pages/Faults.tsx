import { useState } from 'react'
import { useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ALL_ROUTES, api, SETTLEMENT_ROUTE, WEBHOOK_ROUTE, type ArmedFault, type FaultKind } from '@/api'
import { ErrorText, Header } from '@/components/common'
import { hms } from '@/format'

const kinds: Record<FaultKind, [label: string, effect: string]> = {
  Timeout: ['Unavailable (timeout)', 'Responds 504 with no body.'],
  Error: ['Error', 'Responds with the chosen status (default 500).'],
  Latency: ['Slow response', 'Delays, then responds normally.'],
  Malformed: ['Corrupted response', 'Responds 200 with invalid JSON.'],
  Duplicate: ['Duplicate delivery', 'Sends two copies of the webhook.'],
  Reorder: ['Out of order', 'Postpones the delivery so it arrives after the next one.'],
  Hold: ['Hold', 'Holds the deliveries (or the settlement), with no attempt, until disarmed.'],
}
const routeKinds: FaultKind[] = ['Timeout', 'Error', 'Latency', 'Malformed']
const webhookKinds: FaultKind[] = ['Latency', 'Duplicate', 'Reorder', 'Hold']

const routeLabel = (r: string) =>
  r === WEBHOOK_ROUTE ? 'webhook delivery' : r === SETTLEMENT_ROUTE ? 'settlement' : r === ALL_ROUTES ? 'all connector routes' : r
const params = (f: ArmedFault) =>
  f.kind === 'Error' ? `status ${f.status ?? 500}` : f.kind === 'Latency' || f.kind === 'Reorder' ? (f.delayMs ? `delay ${f.delayMs} ms` : 'default delay') : 'N/A'

export default function Faults() {
  const { sandbox = 'default' } = useParams()
  const queryClient = useQueryClient()
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors })
  const armed = useQuery({ queryKey: [sandbox, 'faults'], queryFn: () => api.faults(sandbox) })

  const [picked, setConnector] = useState('')
  const [pickedRoute, setRoute] = useState('')
  const [pickedKind, setKind] = useState<FaultKind>('Timeout')
  const [status, setStatus] = useState('500')
  const [delay, setDelay] = useState('')
  const [times, setTimes] = useState('1')
  const [byTime, setByTime] = useState(false)
  const [minutes, setMinutes] = useState('10')

  const connector = picked || connectors.data?.[0]?.name || ''
  const described = connectors.data?.find(c => c.name === connector)
  const routes = [...(described?.routes ?? []), ALL_ROUTES, WEBHOOK_ROUTE, ...(described?.settlement ? [SETTLEMENT_ROUTE] : [])]
  const route = routes.includes(pickedRoute) ? pickedRoute : routes[0]
  const allowed: FaultKind[] = route === WEBHOOK_ROUTE ? webhookKinds : route === SETTLEMENT_ROUTE ? ['Hold'] : routeKinds
  const kind = allowed.includes(pickedKind) ? pickedKind : allowed[0]
  const delayDefault = kind === 'Reorder' ? '5000' : '1000'
  const refresh = () => queryClient.invalidateQueries({ queryKey: [sandbox, 'faults'] })

  const arm = useMutation({
    mutationFn: () =>
      api.armFault(sandbox, {
        connector,
        route,
        kind,
        status: kind === 'Error' ? Number(status) || 500 : undefined,
        delayMs: kind === 'Latency' || kind === 'Reorder' ? Number(delay || delayDefault) : undefined,
        ...(byTime
          ? { durationSeconds: Math.round(Math.max(1 / 60, Number(minutes) || 10) * 60) }
          : kind === 'Hold'
            ? {}
            : { times: Math.max(1, Number(times) || 1) }),
      }),
    onSuccess: refresh,
  })
  const clear = useMutation({ mutationFn: (f: ArmedFault) => api.clearFault(sandbox, f.connector, f.route), onSuccess: refresh })
  const list = armed.data ?? []

  return (
    <>
      <Header title="Faults">
        Make a provider operation fail on purpose, only in this sandbox, for a number of calls or for a period of time. Afterwards it goes back to normal on its own.
      </Header>
      <div className="cols-form">
        <form
          className="panel col"
          style={{ padding: 16, gap: 14 }}
          onSubmit={e => {
            e.preventDefault()
            arm.mutate()
          }}
        >
          <h2 style={{ fontSize: 15 }}>Arm a fault</h2>
          <label>
            <span className="lbl">Connector</span>
            <select className="field mono" style={{ width: '100%' }} value={connector} onChange={e => setConnector(e.target.value)}>
              {connectors.data?.map(c => (
                <option key={c.name} value={c.name}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span className="lbl">Operation</span>
            <select className="field mono" style={{ width: '100%' }} value={route} onChange={e => setRoute(e.target.value)}>
              {routes.map(r => (
                <option key={r} value={r}>
                  {r === WEBHOOK_ROUTE ? 'Webhook delivery' : r === SETTLEMENT_ROUTE ? 'Settlement (outgoing)' : r === ALL_ROUTES ? 'All connector routes' : r}
                </option>
              ))}
            </select>
          </label>
          <fieldset style={{ border: 'none', margin: 0, padding: 0 }}>
            <legend className="lbl" style={{ padding: 0 }}>
              Type
            </legend>
            <div className="col" style={{ gap: 6 }}>
              {allowed.map(k => (
                <label key={k} className={`kind${k === kind ? ' on' : ''}`}>
                  <input type="radio" name="kind" checked={k === kind} onChange={() => setKind(k)} />
                  <span>
                    <b>{kinds[k][0]}</b>
                    <span className="muted" style={{ display: 'block', fontSize: 12, marginTop: 2 }}>
                      {kinds[k][1]}
                    </span>
                  </span>
                </label>
              ))}
            </div>
          </fieldset>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 10 }}>
            {kind === 'Error' && (
              <label>
                <span className="lbl">Status HTTP</span>
                <input className="field mono" style={{ width: '100%' }} type="number" min={400} max={599} value={status} onChange={e => setStatus(e.target.value)} />
              </label>
            )}
            {(kind === 'Latency' || kind === 'Reorder') && (
              <label>
                <span className="lbl">Delay (ms)</span>
                <input className="field mono" style={{ width: '100%' }} type="number" min={1} placeholder={delayDefault} value={delay} onChange={e => setDelay(e.target.value)} />
              </label>
            )}
            {byTime ? (
              <label>
                <span className="lbl">Minutes</span>
                <input className="field mono" style={{ width: '100%' }} type="number" min={1} max={1440} value={minutes} onChange={e => setMinutes(e.target.value)} />
              </label>
            ) : (
              kind !== 'Hold' && <label>
                <span className="lbl">Times</span>
                <input className="field mono" style={{ width: '100%' }} type="number" min={1} value={times} onChange={e => setTimes(e.target.value)} />
              </label>
            )}
          </div>
          <div role="radiogroup" aria-label="Fault duration" className="row" style={{ gap: 16 }}>
            <label className="radio">
              <input type="radio" name="duration" checked={!byTime} onChange={() => setByTime(false)} />
              {kind === 'Hold' ? 'Until disarmed' : 'For a number of calls'}
            </label>
            <label className="radio">
              <input type="radio" name="duration" checked={byTime} onChange={() => setByTime(true)} />
              For a period of time
            </label>
          </div>
          {route === ALL_ROUTES && (
            <div className="small muted">Applies to every connector operation, not to webhooks or settlement. A fault armed on a specific operation takes precedence.</div>
          )}
          {byTime && <div className="small muted">Every call fails until the sandbox clock passes the deadline. With the clock frozen, the deadline only ends when the clock advances.</div>}
          {list.some(f => f.connector === connector && f.route === route) && (
            <div className="note warn">A fault is already armed on this operation. Arming again replaces the previous one.</div>
          )}
          <ErrorText error={arm.error} />
          <button type="submit" className="btn btn-pri" disabled={!connector || arm.isPending}>
            Arm fault
          </button>
        </form>

        <section className="panel">
          <div className="panel-head">
            <h2 className="grow" style={{ fontSize: 15 }}>
              Armed in the sandbox <span className="mono">{sandbox}</span>
            </h2>
            <span className="small muted">
              {list.length} {list.length === 1 ? 'armed' : 'armed'}
            </span>
          </div>
          <ErrorText error={armed.error ?? clear.error} />
          <table className="t">
            <thead>
              <tr>
                <th>Connector</th>
                <th>Operation</th>
                <th>Type</th>
                <th>Parameters</th>
                <th>Remaining</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {list.map(f => (
                <tr key={`${f.connector} ${f.route}`}>
                  <td className="mono">{f.connector}</td>
                  <td className="mono">{routeLabel(f.route)}</td>
                  <td>
                    <span className="st" style={{ background: 'var(--err-bg)', color: 'var(--err-fg)' }}>
                      {kinds[f.kind]?.[0] ?? f.kind}
                    </span>
                  </td>
                  <td className="mono" style={{ color: 'var(--fg-soft)' }}>
                    {params(f)}
                  </td>
                  <td className="mono">
                    <b>{f.until ? `until ${hms(f.until)}` : f.kind === 'Hold' ? 'until disarmed' : f.remaining}</b>
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    <button className="btn btn-danger" aria-label={`Disarm ${kinds[f.kind]?.[0] ?? f.kind} on ${routeLabel(f.route)}`} onClick={() => clear.mutate(f)}>
                      Disarm
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {armed.data && list.length === 0 && <div className="empty">No faults armed. Operations respond normally.</div>}
          <p className="small muted" style={{ margin: 0, padding: '12px 14px', borderTop: '1px solid var(--line-soft)' }}>
            Every call that hits the fault is marked in the Feed with <span className="mono">[fault:Type]</span>; when armed for a number of calls, it consumes one use.
          </p>
        </section>
      </div>
    </>
  )
}
