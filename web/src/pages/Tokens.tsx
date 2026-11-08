import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api'
import { copyText, ErrorText, Header } from '@/components/common'
import { dayTime } from '@/format'

const MAX_NAME = 60

/** Personal access tokens of the signed-in operator, for the MCP. */
export default function Tokens() {
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey: ['tokens'], queryFn: api.tokens })
  const [name, setName] = useState('')
  const [created, setCreated] = useState<{ name: string; token: string } | null>(null)
  const [copied, setCopied] = useState('')
  const [revoking, setRevoking] = useState('')
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['tokens'] })

  const create = useMutation({
    mutationFn: () => api.createToken(name.trim()),
    onSuccess: t => {
      setCreated({ name: t.name, token: t.token })
      setCopied('')
      setName('')
      return refresh()
    },
  })
  const revoke = useMutation({
    mutationFn: (id: string) => api.revokeToken(id),
    onSuccess: () => {
      setRevoking('')
      return refresh()
    },
  })
  const trimmed = name.trim()

  return (
    <>
      <Header title="Access tokens">
        Personal tokens for the MCP and for calling the control API outside the panel. They are valid until revoked and do not give access to the certificate
        private key or the .pfx.
      </Header>

      <form
        className="panel row"
        style={{ padding: 16, gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}
        onSubmit={e => {
          e.preventDefault()
          if (trimmed) create.mutate()
        }}
      >
        <label className="grow" style={{ minWidth: 220 }}>
          <span className="lbl">Name</span>
          <input
            className="field"
            style={{ width: '100%' }}
            maxLength={MAX_NAME}
            placeholder="e.g. Claude Code on my laptop"
            value={name}
            onChange={e => setName(e.target.value)}
          />
        </label>
        <button className="btn btn-pri" disabled={!trimmed || create.isPending}>
          Create token
        </button>
        <ErrorText error={create.error} />
      </form>

      {created && (
        <div className="note warn col" style={{ gap: 8 }}>
          <strong>Token {created.name} created. Copy it now: it will not be shown again.</strong>
          <code className="mono" style={{ overflowWrap: 'anywhere' }}>
            {created.token}
          </code>
          <div className="row">
            <button className="btn btn-sec" onClick={async () => setCopied((await copyText(created.token)) ? 'Copied.' : 'Could not copy.')}>
              Copy
            </button>
            <button className="btn btn-sec" onClick={() => setCreated(null)}>
              I have copied it
            </button>
            <span className="small">{copied}</span>
          </div>
        </div>
      )}

      <div className="panel" style={{ minWidth: 0, overflow: 'auto' }}>
        <ErrorText error={list.error ?? revoke.error} />
        <table className="t">
          <thead>
            <tr>
              <th>Name</th>
              <th>Created at</th>
              <th>Last used</th>
              <th>Status</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(list.data ?? []).map(t => (
              <tr key={t.id}>
                <td>{t.name}</td>
                <td className="mono muted">{dayTime(t.createdAt)}</td>
                <td className="mono muted">{t.lastUsedAt ? dayTime(t.lastUsedAt) : 'never'}</td>
                <td>{t.revokedAt ? `revoked at ${dayTime(t.revokedAt)}` : 'active'}</td>
                <td className="row" style={{ justifyContent: 'flex-end', gap: 6 }}>
                  {t.revokedAt ? null : revoking === t.id ? (
                    <>
                      <button className="btn btn-danger" disabled={revoke.isPending} onClick={() => revoke.mutate(t.id)}>
                        Confirm revocation
                      </button>
                      <button className="btn btn-sec" onClick={() => setRevoking('')}>
                        Cancel
                      </button>
                    </>
                  ) : (
                    <button className="btn btn-sec" onClick={() => setRevoking(t.id)}>
                      Revoke
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {list.data?.length === 0 && <div className="empty">No tokens created.</div>}
      </div>
    </>
  )
}
