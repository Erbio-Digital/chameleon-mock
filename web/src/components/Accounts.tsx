import { Fragment, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type BankAccount, type LedgerEntry } from '@/api'
import { ErrorText, Modal } from '@/components/common'
import { dayTime } from '@/format'

const brl = (value: string) => Number(value).toLocaleString('en-US', { style: 'currency', currency: 'BRL' })
const kindLabel: Record<LedgerEntry['kind'], string> = { Credit: 'Credit', Debit: 'Debit', Adjustment: 'Adjustment' }

type Opening = { nickname: string; branch: string; account: string; pixKey: string; balance: string }
const blank: Opening = { nickname: '', branch: '', account: '', pixKey: '', balance: '' }
type Editing = { nickname: string; branch: string; account: string; pixKey: string }

/** Simulated accounts of a bank in the sandbox: received Pix credits the account owning the key. */
export function Accounts({ sandbox, bank }: { sandbox: string; bank: string }) {
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey: [sandbox, 'accounts', bank], queryFn: () => api.accounts(sandbox, bank) })
  const [opening, setOpening] = useState<Opening | null>(null)
  const [balanceOf, setBalanceOf] = useState<{ nickname: string; balance: string } | null>(null)
  const [editing, setEditing] = useState<Editing | null>(null)
  const [open, setOpen] = useState<string | null>(null)

  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: [sandbox, 'accounts'] }),
      queryClient.invalidateQueries({ queryKey: [sandbox, 'account'] }),
    ])
  const create = useMutation({
    mutationFn: (o: Opening) =>
      api.openAccount(sandbox, bank, {
        nickname: o.nickname.trim(),
        branch: o.branch.trim(),
        account: o.account.trim(),
        pixKey: o.pixKey.trim() || undefined,
        balance: o.balance ? Number(o.balance) : undefined,
      }),
    onSuccess: () => {
      setOpening(null)
      return refresh()
    },
  })
  const setBalance = useMutation({
    mutationFn: (b: { nickname: string; balance: string }) => api.setBalance(sandbox, bank, b.nickname, Number(b.balance)),
    onSuccess: () => {
      setBalanceOf(null)
      return refresh()
    },
  })
  const update = useMutation({
    mutationFn: (e: Editing) =>
      api.updateAccount(sandbox, bank, e.nickname, { branch: e.branch.trim(), account: e.account.trim(), pixKey: e.pixKey.trim() }),
    onSuccess: () => {
      setEditing(null)
      return refresh()
    },
  })
  const rows = list.data ?? []

  return (
    <div className="panel" style={{ minWidth: 0, overflow: 'auto' }}>
      <div className="panel-head row">
        <span className="small muted grow">A Pix payment received on a key credits the account that owns it. A balance set here shows up in the statement as an adjustment.</span>
        <button className="btn btn-sec" onClick={() => setOpening(blank)}>
          New account
        </button>
      </div>
      <ErrorText error={list.error} />
      <table className="t">
        <thead>
          <tr>
            <th>Nickname</th>
            <th>Branch</th>
            <th>Account</th>
            <th>Pix key</th>
            <th style={{ textAlign: 'right' }}>Balance</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {rows.map(a => (
            <Fragment key={a.nickname}>
              <tr>
                <td className="mono">{a.nickname}</td>
                <td className="mono">{a.branch}</td>
                <td className="mono">{a.account}</td>
                <td className="mono" style={{ overflowWrap: 'anywhere' }}>
                  {a.pixKey ?? <span className="muted">no key</span>}
                </td>
                <td className="mono" style={{ textAlign: 'right' }}>
                  <b>{brl(a.balance)}</b>
                </td>
                <td className="row" style={{ justifyContent: 'flex-end', gap: 6 }}>
                  <button className="btn btn-sec" aria-expanded={open === a.nickname} onClick={() => setOpen(open === a.nickname ? null : a.nickname)}>
                    Statement
                  </button>
                  <button
                    className="btn btn-sec"
                    onClick={() => setEditing({ nickname: a.nickname, branch: a.branch, account: a.account, pixKey: a.pixKey ?? '' })}
                  >
                    Edit
                  </button>
                  <button className="btn btn-sec" onClick={() => setBalanceOf({ nickname: a.nickname, balance: a.balance })}>
                    Set balance
                  </button>
                </td>
              </tr>
              {open === a.nickname && (
                <tr>
                  <td colSpan={6} style={{ background: 'var(--bg)', padding: '10px 12px 14px' }}>
                    <Ledger sandbox={sandbox} account={a} />
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
      {list.data && rows.length === 0 && <div className="empty">No accounts in this sandbox.</div>}

      {opening && (
        <Modal label="New account" onClose={() => setOpening(null)}>
          <form
            className="col"
            style={{ gap: 12 }}
            onSubmit={e => {
              e.preventDefault()
              create.mutate(opening)
            }}
          >
            {(
              [
                ['nickname', 'Nickname (unique in the sandbox)', 'operations', '[a-z0-9-]{1,40}', true],
                ['branch', 'Branch', '1234', '[0-9]{4}', true],
                ['account', 'Account', '56789-0', '[0-9-]{1,20}', true],
                ['pixKey', 'Pix key (optional)', 'pix-test@example.com', undefined, false],
              ] as const
            ).map(([field, label, placeholder, pattern, required]) => (
              <label key={field} className="col" style={{ gap: 4 }}>
                <span className="small">{label}</span>
                <input
                  className="field mono"
                  required={required}
                  pattern={pattern}
                  placeholder={placeholder}
                  value={opening[field]}
                  onChange={e => setOpening({ ...opening, [field]: e.target.value })}
                />
              </label>
            ))}
            <label className="col" style={{ gap: 4 }}>
              <span className="small">Initial balance (R$)</span>
              <input className="field mono" type="number" min={0} step="0.01" placeholder="0.00" value={opening.balance} onChange={e => setOpening({ ...opening, balance: e.target.value })} />
            </label>
            <ErrorText error={create.error} />
            <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
              <button type="button" className="btn btn-sec" onClick={() => setOpening(null)}>
                Cancel
              </button>
              <button className="btn btn-pri" disabled={create.isPending}>
                Register
              </button>
            </div>
          </form>
        </Modal>
      )}

      {editing && (
        <Modal label={`Edit ${editing.nickname}`} onClose={() => setEditing(null)}>
          <form
            className="col"
            style={{ gap: 12 }}
            onSubmit={e => {
              e.preventDefault()
              update.mutate(editing)
            }}
          >
            {(
              [
                ['branch', 'Branch', '1234', '[0-9]{4}', true],
                ['account', 'Account', '56789-0', '[0-9-]{1,20}', true],
                ['pixKey', 'Pix key (empty removes it)', 'pix-test@example.com', undefined, false],
              ] as const
            ).map(([field, label, placeholder, pattern, required]) => (
              <label key={field} className="col" style={{ gap: 4 }}>
                <span className="small">{label}</span>
                <input
                  className="field mono"
                  required={required}
                  pattern={pattern}
                  placeholder={placeholder}
                  value={editing[field]}
                  onChange={e => setEditing({ ...editing, [field]: e.target.value })}
                />
              </label>
            ))}
            <div className="small muted">Balance and statement are kept. The webhook is registered per key: when you change the key, register the webhook for the new one.</div>
            <ErrorText error={update.error} />
            <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
              <button type="button" className="btn btn-sec" onClick={() => setEditing(null)}>
                Cancel
              </button>
              <button className="btn btn-pri" disabled={update.isPending}>
                Save
              </button>
            </div>
          </form>
        </Modal>
      )}

      {balanceOf && (
        <Modal label={`Set balance of ${balanceOf.nickname}`} onClose={() => setBalanceOf(null)}>
          <form
            className="col"
            style={{ gap: 12 }}
            onSubmit={e => {
              e.preventDefault()
              setBalance.mutate(balanceOf)
            }}
          >
            <label className="col" style={{ gap: 4 }}>
              <span className="small">New balance (R$)</span>
              <input
                className="field mono"
                type="number"
                min={0}
                step="0.01"
                required
                autoFocus
                value={balanceOf.balance}
                onChange={e => setBalanceOf({ ...balanceOf, balance: e.target.value })}
              />
            </label>
            <div className="small muted">The difference goes into the statement as a simulator adjustment; earlier entries are kept.</div>
            <ErrorText error={setBalance.error} />
            <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
              <button type="button" className="btn btn-sec" onClick={() => setBalanceOf(null)}>
                Cancel
              </button>
              <button className="btn btn-pri" disabled={setBalance.isPending}>
                Save
              </button>
            </div>
          </form>
        </Modal>
      )}
    </div>
  )
}

function Ledger({ sandbox, account }: { sandbox: string; account: BankAccount }) {
  const detail = useQuery({ queryKey: [sandbox, 'account', account.nickname], queryFn: () => api.account(sandbox, account.nickname) })
  const entries = detail.data?.entries ?? []
  if (detail.data && entries.length === 0) return <div className="small muted">No entries.</div>
  return (
    <>
      <ErrorText error={detail.error} />
      <div className="h3">Statement of {account.nickname}</div>
      <table className="t sub">
        <thead>
          <tr>
            <th>Date (sandbox clock)</th>
            <th>Type</th>
            <th>Description</th>
            <th>Reference</th>
            <th style={{ textAlign: 'right' }}>Amount</th>
          </tr>
        </thead>
        <tbody>
          {entries.map(e => (
            <tr key={e.id}>
              <td className="mono muted">{dayTime(e.at)}</td>
              <td>{kindLabel[e.kind]}</td>
              <td>{e.description}</td>
              <td className="mono muted">{e.reference ?? ''}</td>
              <td className="mono" style={{ textAlign: 'right', color: Number(e.amount) < 0 ? 'var(--err-fg)' : undefined }}>
                {brl(e.amount)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}
