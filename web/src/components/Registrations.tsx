import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type WebhookRegistration } from '@/api'
import { ErrorText, Modal } from '@/components/common'
import { dayTime } from '@/format'

type Editing = { connector: string; key: string; url: string; isNew: boolean }

const capitalize = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)

/**
 * Where each provider notifies the app. The app registers through the provider's own API,
 * as in production; here the operator can also set or remove one to prepare a test.
 */
export function Registrations({ sandbox, connector }: { sandbox: string; connector?: string }) {
  const queryClient = useQueryClient()
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors })
  const list = useQuery({ queryKey: [sandbox, 'registrations'], queryFn: () => api.registrations(sandbox) })
  const [editing, setEditing] = useState<Editing | null>(null)
  const [removing, setRemoving] = useState('')

  const owners = (connectors.data ?? []).filter(c => c.registrations && (!connector || c.name === connector))
  const rows = (list.data ?? []).filter(r => !connector || r.connector === connector)
  const keyLabel = (name: string) => connectors.data?.find(c => c.name === name)?.registrations?.key ?? 'key'

  const done = () => {
    setEditing(null)
    setRemoving('')
    return queryClient.invalidateQueries({ queryKey: [sandbox, 'registrations'] })
  }
  const save = useMutation({
    mutationFn: (e: Editing) => api.setRegistration(sandbox, e.connector, e.key.trim(), e.url.trim()),
    onSuccess: done,
  })
  const remove = useMutation({
    mutationFn: (r: WebhookRegistration) => api.removeRegistration(sandbox, r.connector, r.key),
    onSuccess: done,
  })

  if (connectors.data && owners.length === 0)
    return (
      <div className="empty">
        {connector ? 'This connector has no webhook registration: the URL comes with each request.' : 'No connector has webhook registration: the URL comes with each request.'}
      </div>
    )

  return (
    <div className="panel" style={{ minWidth: 0, overflow: 'auto' }}>
      <div className="panel-head row">
        <span className="small muted grow">Registered by the application through the provider API, or here by the operator.</span>
        <button
          className="btn btn-sec"
          disabled={!owners.length}
          onClick={() => setEditing({ connector: owners[0].name, key: '', url: '', isNew: true })}
        >
          New registration
        </button>
      </div>
      <ErrorText error={list.error ?? remove.error} />
      <table className="t">
        <thead>
          <tr>
            {!connector && <th>Connector</th>}
            <th>{connector ? capitalize(keyLabel(connector)) : 'Key'}</th>
            <th>URL</th>
            <th>Registered at</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {rows.map(r => {
            const id = `${r.connector}/${r.key}`
            return (
              <tr key={id}>
                {!connector && <td className="mono">{r.connector}</td>}
                <td className="mono">{r.key}</td>
                <td className="mono" style={{ overflowWrap: 'anywhere' }}>
                  {r.url}
                </td>
                <td className="mono muted">{dayTime(r.createdAt)}</td>
                <td className="row" style={{ justifyContent: 'flex-end', gap: 6 }}>
                  {removing === id ? (
                    <>
                      <button className="btn btn-danger" disabled={remove.isPending} onClick={() => remove.mutate(r)}>
                        Confirm removal
                      </button>
                      <button className="btn btn-sec" onClick={() => setRemoving('')}>
                        Cancel
                      </button>
                    </>
                  ) : (
                    <>
                      <button className="btn btn-sec" onClick={() => setEditing({ ...r, isNew: false })}>
                        Change URL
                      </button>
                      <button className="btn btn-sec" onClick={() => setRemoving(id)}>
                        Remove
                      </button>
                    </>
                  )}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {list.data && rows.length === 0 && <div className="empty">No webhooks registered in this sandbox.</div>}

      {editing && (
        <Modal label={editing.isNew ? 'New webhook registration' : 'Change webhook URL'} onClose={() => setEditing(null)}>
          <form
            className="col"
            style={{ gap: 12 }}
            onSubmit={e => {
              e.preventDefault()
              save.mutate(editing)
            }}
          >
            {editing.isNew && !connector && (
              <label className="col" style={{ gap: 4 }}>
                <span className="small">Connector</span>
                <select className="field" value={editing.connector} onChange={e => setEditing({ ...editing, connector: e.target.value })}>
                  {owners.map(c => (
                    <option key={c.name}>{c.name}</option>
                  ))}
                </select>
              </label>
            )}
            <label className="col" style={{ gap: 4 }}>
              <span className="small">{capitalize(keyLabel(editing.connector))}</span>
              <input
                className="field mono"
                required
                readOnly={!editing.isNew}
                value={editing.key}
                onChange={e => setEditing({ ...editing, key: e.target.value })}
              />
            </label>
            <label className="col" style={{ gap: 4 }}>
              <span className="small">URL</span>
              <input
                className="field mono"
                type="url"
                required
                placeholder="https://..."
                value={editing.url}
                onChange={e => setEditing({ ...editing, url: e.target.value })}
              />
            </label>
            <ErrorText error={save.error} />
            <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
              <button type="button" className="btn btn-sec" onClick={() => setEditing(null)}>
                Cancel
              </button>
              <button className="btn btn-pri" disabled={save.isPending}>
                Save
              </button>
            </div>
          </form>
        </Modal>
      )}
    </div>
  )
}
