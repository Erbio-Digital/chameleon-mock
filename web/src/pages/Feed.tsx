import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { Pause, X } from 'lucide-react'
import { api, type FeedFilter, type Interaction } from '@/api'
import { useLive } from '@/live'
import { ErrorText, Header, Pill } from '@/components/common'
import Detail from '@/pages/FeedDetail'
import { change, changeTone, hms, isCall, kindTone, ms, result, toneOf } from '@/format'

// ponytail: keeps at most 5000 rows in memory; paging back past that drops the oldest.
const MAX_ROWS = 5000
const filterKeys = ['connector', 'kind', 'statusClass', 'period', 'entityId', 'correlationId'] as const
type Filters = Partial<Record<(typeof filterKeys)[number], string>>

// Each row is something someone did: the app called, the operator acted, the clock moved an
// entity or the simulator sent a webhook. A transition caused by a call shows inside that call.
const kindOptions: [string, string][] = [
  ['InboundRequest', kindTone.InboundRequest[0]],
  ['ControlAction', kindTone.ControlAction[0]],
  ['Clock', 'Clock'],
  ['OutboundWebhook', kindTone.OutboundWebhook[0]],
]
const isRow = (i: Interaction) => i.kind !== 'StateTransition' || i.byClock
// A clock row has no call to name it: it says which entity moved.
const summary = (i: Interaction) => (i.byClock ? `${i.entityId}: ${change(i)}` : i.summary)

// The period is relative to the sandbox clock at the moment the filter is read.
function toQuery(f: Filters, sandboxNow: number | undefined): FeedFilter {
  const { period, kind, ...rest } = f
  return {
    ...rest,
    ...(kind === 'Clock' ? { kind: 'StateTransition', byClock: 'true' } : { kind }),
    from: period && sandboxNow ? new Date(sandboxNow - Number(period) * 60_000).toISOString() : undefined,
  }
}

const matches = (f: FeedFilter, i: Interaction) =>
  (!f.connector || i.connector === f.connector) &&
  (!f.kind || i.kind === f.kind) &&
  (!f.byClock || i.byClock === (f.byClock === 'true')) &&
  (!f.statusClass || (i.statusCode !== null && Math.floor(i.statusCode / 100) === Number(f.statusClass))) &&
  (!f.from || i.at >= f.from) &&
  (!f.entityId || i.entityId === f.entityId) &&
  (!f.correlationId || i.correlationId === f.correlationId)

/** Result plus the state changes the call caused. */
function Result({ i, changes }: { i: Interaction; changes: Interaction[] }) {
  return (
    <span className="row" style={{ gap: 6 }}>
      <Pill tone={result(i)} />
      {changes.map(t => (
        <Pill key={t.seq} tone={changeTone(t)} />
      ))}
    </span>
  )
}

export default function Feed() {
  const { sandbox = 'default', id } = useParams()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const filters: Filters = Object.fromEntries(filterKeys.map(k => [k, params.get(k) ?? undefined]))
  const filterKey = filterKeys.map(k => params.get(k) ?? '').join('|')

  const { status: liveStatus, subscribe } = useLive()
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors })
  const sandboxes = useQuery({ queryKey: ['sandboxes'], queryFn: api.sandboxes })
  const sandboxNow = sandboxes.data?.find(s => s.id === sandbox)?.now
  const [items, setItems] = useState<Interaction[]>([])
  const [nextBefore, setNextBefore] = useState<number | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [paused, setPaused] = useState(false)
  const [pending, setPending] = useState(0)
  const [error, setError] = useState<unknown>(null)
  const lastSeq = useRef(0)
  const pausedRef = useRef(false)
  const queryRef = useRef<FeedFilter>({})

  const merge = useCallback((incoming: Interaction[]) => {
    if (!incoming.length) return
    lastSeq.current = Math.max(lastSeq.current, ...incoming.map(i => i.seq))
    setItems(prev => {
      const bySeq = new Map(prev.map(i => [i.seq, i]))
      for (const i of incoming) bySeq.set(i.seq, i)
      return [...bySeq.values()].sort((a, b) => b.seq - a.seq).slice(0, MAX_ROWS)
    })
  }, [])

  // Everything recorded after the last row seen, oldest first: resume, overflow, reconnect.
  const catchUp = useCallback(async () => {
    try {
      for (;;) {
        const page = await api.interactions(sandbox, queryRef.current, { afterSeq: lastSeq.current, limit: 500 })
        merge(page.items)
        if (page.items.length < 500) break
      }
    } catch (e) {
      setError(e)
    }
  }, [sandbox, merge])

  const periodReady = !filters.period || sandboxNow !== undefined
  useEffect(() => {
    if (!periodReady) return
    let cancelled = false
    queryRef.current = toQuery(filters, sandboxNow ? Date.parse(sandboxNow) : undefined)
    setItems([])
    setLoaded(false)
    setError(null)
    lastSeq.current = 0
    api
      .interactions(sandbox, queryRef.current, { limit: 100 })
      .then(page => {
        if (cancelled) return
        merge(page.items)
        setNextBefore(page.nextBeforeSeq)
        setLoaded(true)
      })
      .catch(e => !cancelled && setError(e))
    return () => {
      cancelled = true
    }
    // Reloads on filter change only; a new sandbox clock reading alone does not refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sandbox, filterKey, periodReady, merge])

  // Joining may finish after the first page: whatever fell in between comes through the cursor.
  useEffect(() => {
    if (liveStatus === 'live' && loaded && !pausedRef.current) void catchUp()
  }, [liveStatus, loaded, catchUp])

  useEffect(
    () =>
      subscribe(events => {
        const fresh = events
          .filter(e => e.type === 'interaction')
          .map(e => e.data as unknown as Interaction)
          .filter(i => matches(queryRef.current, i))
        if (pausedRef.current) {
          setPending(n => n + fresh.length)
          return
        }
        merge(fresh)
        if (events.some(e => e.type === 'overflow')) void catchUp()
      }),
    [subscribe, merge, catchUp],
  )

  const setPause = (next: boolean) => {
    pausedRef.current = next
    setPaused(next)
    if (!next) {
      setPending(0)
      void catchUp()
    }
  }

  const loadOlder = async () => {
    if (nextBefore === null) return
    try {
      const page = await api.interactions(sandbox, queryRef.current, { beforeSeq: nextBefore, limit: 100 })
      merge(page.items)
      setNextBefore(page.nextBeforeSeq)
    } catch (e) {
      setError(e)
    }
  }

  const setFilter = (key: (typeof filterKeys)[number], value: string | undefined) =>
    setParams(
      prev => {
        const next = new URLSearchParams(prev)
        if (value) next.set(key, value)
        else next.delete(key)
        return next
      },
      { replace: true },
    )

  const rows = items.filter(isRow)
  // Transitions grouped under the call (same correlation) that caused them.
  const caused = new Map<string, Interaction[]>()
  for (const t of items)
    if (!isRow(t) && t.correlationId) caused.set(t.correlationId, [t, ...(caused.get(t.correlationId) ?? [])])

  const open = (seq: string | null) => navigate({ pathname: `/s/${sandbox}/feed${seq ? `/${seq}` : ''}`, search: params.toString() })

  return (
    <>
      <Header
        title="Feed"
        actions={
          paused ? (
            <button className="btn btn-pri" onClick={() => setPause(false)}>
              {pending ? `Resume · ${pending} new` : 'Resume'}
            </button>
          ) : (
            <button className="btn btn-sec" onClick={() => setPause(true)}>
              <Pause size={13} aria-hidden />
              Pause
            </button>
          )
        }
      >
        Application calls, actions, state changes and webhooks of the sandbox <span className="mono">{sandbox}</span>, live.
      </Header>

      <div className="panel row" style={{ padding: '12px 14px', flexWrap: 'wrap', gap: 12, alignItems: 'flex-end' }}>
        <label>
          <span className="lbl">Connector</span>
          <select className="field" value={filters.connector ?? ''} onChange={e => setFilter('connector', e.target.value)}>
            <option value="">All</option>
            {connectors.data?.map(c => (
              <option key={c.name} value={c.name}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span className="lbl">Type</span>
          <select className="field" value={filters.kind ?? ''} onChange={e => setFilter('kind', e.target.value)}>
            <option value="">All</option>
            {kindOptions.map(([k, label]) => (
              <option key={k} value={k}>
                {label}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span className="lbl">Result</span>
          <select className="field" value={filters.statusClass ?? ''} onChange={e => setFilter('statusClass', e.target.value)}>
            <option value="">All</option>
            <option value="2">2xx</option>
            <option value="4">4xx</option>
            <option value="5">5xx</option>
          </select>
        </label>
        <label>
          <span className="lbl">Period</span>
          <select className="field" value={filters.period ?? ''} onChange={e => setFilter('period', e.target.value)}>
            <option value="">Entire history</option>
            <option value="2">Last 2 min</option>
            <option value="15">Last 15 min</option>
            <option value="60">Last hour</option>
          </select>
        </label>
        <label style={{ flex: 1, minWidth: 220 }}>
          <span className="lbl">Entity</span>
          <input
            key={filters.entityId ?? ''}
            className="field mono"
            style={{ width: '100%' }}
            placeholder="entity key"
            defaultValue={filters.entityId}
            onBlur={e => setFilter('entityId', e.target.value.trim())}
            onKeyDown={e => e.key === 'Enter' && e.currentTarget.blur()}
          />
        </label>
        {filters.correlationId && (
          <span className="st" style={{ background: 'var(--info-bg)', color: 'var(--info-fg)', height: 34, padding: '0 6px 0 10px' }}>
            correlation <span className="mono">{filters.correlationId.slice(0, 12)}</span>
            <button className="chip-btn" style={{ background: 'none', padding: 4 }} aria-label="Remove correlation filter" onClick={() => setFilter('correlationId', undefined)}>
              <X size={13} aria-hidden />
            </button>
          </span>
        )}
        <span className="small muted" style={{ paddingBottom: 8 }}>
          {rows.length} {rows.length === 1 ? 'record' : 'records'}
          {nextBefore !== null ? ' (older ones available)' : ''}
        </span>
      </div>
      <ErrorText error={error} />

      <div className="split">
        <div className="panel" style={{ flex: 1, minWidth: 0, overflow: 'auto' }}>
          <table className="t">
            <thead>
              <tr>
                <th>Seq</th>
                <th>Time</th>
                <th>Type</th>
                <th>Connector</th>
                <th>Summary</th>
                <th>Result</th>
                <th style={{ textAlign: 'right' }}>Duration</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(i => (
                <tr key={i.seq} className={String(i.seq) === id || i.id === id ? 'sel' : undefined}>
                  <td className="mono muted">{i.seq}</td>
                  <td className="mono">{hms(i.at)}</td>
                  <td>
                    <Pill tone={toneOf(i)} />
                  </td>
                  <td className="mono" style={{ color: 'var(--fg-soft)' }}>
                    {i.connector}
                  </td>
                  <td className="mono">
                    <button className="rowbtn" onClick={() => open(String(i.seq))} aria-label={`Open details of ${summary(i)}`}>
                      {summary(i)}
                    </button>
                  </td>
                  <td>
                    <Result i={i} changes={isCall(i) ? (caused.get(i.correlationId ?? '') ?? []) : []} />
                  </td>
                  <td className="mono" style={{ textAlign: 'right', color: 'var(--fg-soft)' }}>
                    {ms(i.durationMs)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {rows.length === 0 && <div className="empty">{loaded ? 'Nothing matches these filters.' : error ? 'Could not load the feed.' : 'Loading...'}</div>}
          <div style={{ padding: '10px 12px', borderTop: '1px solid var(--line-soft)' }}>
            {nextBefore !== null ? (
              <button className="linkbtn" onClick={loadOlder}>
                Load older
              </button>
            ) : (
              loaded && <span className="small muted">There are no older records in this sandbox.</span>
            )}
          </div>
        </div>

        {id && (
          <Detail
            sandbox={sandbox}
            seq={id}
            loaded={items.find(i => String(i.seq) === id || i.id === id)}
            onClose={() => open(null)}
            onPick={seq => open(String(seq))}
            onCorrelation={c => setFilter('correlationId', c)}
          />
        )}
      </div>
    </>
  )
}
