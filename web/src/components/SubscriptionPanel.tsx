import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, Eye, EyeOff } from 'lucide-react'
import { api, type Connector } from '@/api'
import { Certificate } from '@/components/Certificate'
import { copyText, ErrorText } from '@/components/common'

export type SubscriptionView = 'connection' | 'certificate' | 'settings' | 'test'

/**
 * The connector in this sandbox, one part per tab of the detail page: subscribe,
 * the issued URL and credentials, the client certificate, the data the service fills in, a test event.
 */
export function SubscriptionPanel({ sandbox, c, view }: { sandbox: string; c: Connector; view: SubscriptionView }) {
  const queryClient = useQueryClient()
  const sub = useQuery({ queryKey: [sandbox, 'subscription', c.name], queryFn: () => api.subscription(sandbox, c.name) })
  const [shown, setShown] = useState<Record<string, boolean>>({})
  const [draft, setDraft] = useState<Record<string, string> | null>(null)
  const [confirm, setConfirm] = useState<'' | 'rotate' | 'cancel'>('')
  const [msg, setMsg] = useState('')

  const done = (text = '') => {
    setConfirm('')
    setMsg(text)
    return queryClient.invalidateQueries({ queryKey: [sandbox] })
  }
  const subscribe = useMutation({ mutationFn: () => api.subscribe(sandbox, c.name), onSuccess: () => done() })
  const cancel = useMutation({ mutationFn: () => api.unsubscribe(sandbox, c.name), onSuccess: () => done() })
  const rotate = useMutation({
    mutationFn: () => api.rotate(sandbox, c.name),
    onSuccess: () =>
      done(
        c.clientCertificate
          ? 'New credentials and certificate generated. The previous ones are no longer valid.'
          : 'New credentials generated. The previous ones no longer grant access.',
      ),
  })
  const save = useMutation({
    mutationFn: (values: Record<string, string>) => api.saveSettings(sandbox, c.name, values),
    onSuccess: () => {
      setDraft(null)
      return done('Settings saved.')
    },
  })
  const test = useMutation({
    mutationFn: () => api.testEvent(sandbox, c.name),
    onSuccess: r => done(`Test event sent to ${r.sent} ${r.sent === 1 ? 'destination' : 'destinations'}. Follow it in Webhooks.`),
  })
  const error = sub.error ?? subscribe.error ?? cancel.error ?? rotate.error ?? save.error ?? test.error

  if (!sub.data) return sub.isLoading ? <span className="muted">Loading...</span> : <ErrorText error={sub.error} />
  const s = sub.data

  const status = (
    <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
      <span className="small muted grow" role="status">
        {msg}
      </span>
      <ErrorText error={error} />
    </div>
  )

  if (s.status === 'Available')
    return (
      <section className="panel col" style={{ padding: 16, gap: 10 }}>
        <b>
          Not subscribed yet in <span className="mono">{sandbox}</span>.
        </b>
        <span className="small muted">
          When you subscribe, Chameleon generates a URL and credentials exclusive to this sandbox.
          {sandbox === 'default' ? ' In default, without a subscription, the connector accepts any credential.' : ' Until then, calls to this connector are refused.'}
        </span>
        <ErrorText error={error} />
        <div>
          <button className="btn btn-pri" disabled={subscribe.isPending} onClick={() => subscribe.mutate()}>
            Subscribe in {sandbox}
          </button>
        </div>
      </section>
    )

  if (view === 'certificate')
    return (
      <section className="panel col" style={{ paddingTop: 10 }}>
        {s.certificate && <Certificate sandbox={sandbox} c={c} cert={s.certificate} onDone={text => void done(text)} />}
        <div style={{ padding: '0 14px 10px' }}>{status}</div>
      </section>
    )

  if (view === 'test')
    return (
      <div className="col" style={{ gap: 6 }}>
        <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
          <button type="button" className="btn btn-sec" disabled={test.isPending} onClick={() => test.mutate()}>
            Send test event
          </button>
          <span className="small muted">Delivers a sample event to the registered destinations.</span>
        </div>
        {status}
      </div>
    )

  const env = s.credentials.map(x => `CHAMELEON_${c.name.toUpperCase().replace(/-/g, '_')}_${x.env}=${x.value}`).join('\n')
  const values = draft ?? s.settings ?? {}

  if (view === 'settings')
    return (
      <div className="col" style={{ gap: 8 }}>
        <section className="panel">
          <div className="panel-head row">
            <h2 className="grow" style={{ fontSize: 14 }}>
              Your service settings
            </h2>
            <span className="st" style={{ background: 'var(--chip)', color: 'var(--fg-soft)' }}>
              FILLED IN BY YOU
            </span>
          </div>
          <form
            className="col"
            style={{ padding: 14, gap: 12 }}
            onSubmit={e => {
              e.preventDefault()
              save.mutate(values)
            }}
          >
            {!!s.missing?.length && (
              <div className="note warn">
                Missing: <b>{s.missing.join(', ')}</b>. Until then, Chameleon accepts calls but does not deliver events.
              </div>
            )}
            {c.settings.map(f => (
              <label key={f.key}>
                <span className="lbl">
                  {f.label}
                  {f.required && ' *'}
                </span>
                <input
                  className="field mono"
                  style={{ width: '100%' }}
                  type={f.kind === 'url' ? 'url' : 'text'}
                  placeholder={f.kind === 'url' ? 'https://...' : undefined}
                  value={values[f.key] ?? ''}
                  onChange={e => setDraft({ ...values, [f.key]: e.target.value })}
                />
                {f.help && <span className="small muted">{f.help}</span>}
              </label>
            ))}
            {c.settings.length === 0 && (
              <span className="small muted">
                Nothing to fill in here.{c.registrations ? ' Your service registers the webhook through the provider API, as in the real one; see the Webhooks tab.' : ''}
              </span>
            )}
            {c.settings.length > 0 && (
              <div>
                <button className="btn btn-pri" disabled={save.isPending || draft === null}>
                  Save
                </button>
              </div>
            )}
          </form>
        </section>
        {status}
      </div>
    )

  return (
    <section className="panel">
      <div className="panel-head row">
        <h2 className="grow" style={{ fontSize: 14 }}>
          Point your service at Chameleon
        </h2>
        <span className="st" style={{ background: 'var(--info-bg)', color: 'var(--info-fg)' }}>
          GENERATED HERE
        </span>
      </div>
      <p className="small muted" style={{ margin: 0, padding: '10px 14px 0' }}>
        Set these values in your service's environment variables for <span className="mono">{sandbox}</span>, in place of the real provider's.
      </p>
      <table className="t">
        <tbody>
          {s.credentials.map(x => (
            <tr key={x.env}>
              <td className="muted" style={{ width: 150 }}>
                {x.label}
              </td>
              <td className="mono" style={{ whiteSpace: 'normal', overflowWrap: 'anywhere' }}>
                {x.secret && !shown[x.env] ? '•'.repeat(16) : x.value}
              </td>
              <td style={{ width: 80, textAlign: 'right' }}>
                {x.secret && (
                  <button
                    className="iconbtn"
                    aria-label={shown[x.env] ? `Hide ${x.label}` : `Show ${x.label}`}
                    onClick={() => setShown({ ...shown, [x.env]: !shown[x.env] })}
                  >
                    {shown[x.env] ? <EyeOff size={14} aria-hidden /> : <Eye size={14} aria-hidden />}
                  </button>
                )}
                <button className="iconbtn" aria-label={`Copy ${x.label}`} onClick={async () => setMsg((await copyText(x.value)) ? `${x.label} copied.` : 'Could not copy.')}>
                  <Copy size={14} aria-hidden />
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="row" style={{ padding: 14, gap: 8, flexWrap: 'wrap' }}>
        <button className="btn btn-sec" onClick={async () => setMsg((await copyText(env)) ? 'Copied as .env.' : 'Could not copy.')}>
          Copy as .env
        </button>
        {confirm === 'rotate' ? (
          <>
            <span className="small">The current ones stop granting access.</span>
            <button className="btn btn-danger" disabled={rotate.isPending} onClick={() => rotate.mutate()}>
              Confirm
            </button>
            <button className="btn btn-sec" onClick={() => setConfirm('')}>
              Cancel
            </button>
          </>
        ) : (
          <button className="btn btn-sec" onClick={() => setConfirm('rotate')}>
            Generate new credentials
          </button>
        )}
      </div>
      <div className="row" style={{ flexWrap: 'wrap', padding: '0 14px 14px', gap: 8 }}>
        <div className="grow">{status}</div>
        {confirm === 'cancel' ? (
          <>
            <span className="small">The credentials stop being valid{sandbox === 'default' ? '.' : ' and calls start being refused.'}</span>
            <button className="btn btn-danger" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
              Cancel subscription
            </button>
            <button className="btn btn-sec" onClick={() => setConfirm('')}>
              Keep
            </button>
          </>
        ) : (
          <button className="btn btn-danger-ghost" onClick={() => setConfirm('cancel')}>
            Cancel subscription
          </button>
        )}
      </div>
    </section>
  )
}
