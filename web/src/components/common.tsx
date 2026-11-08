import { useEffect, useRef, type CSSProperties, type KeyboardEvent, type ReactNode } from 'react'
import type { EntityDescriptor } from '@/api'
import { errorText, type Tone } from '@/format'

export function Header({ title, children, actions }: { title: string; children?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="row" style={{ alignItems: 'flex-end', gap: 16 }}>
      <div className="grow">
        <h1>{title}</h1>
        {children && <p className="sub">{children}</p>}
      </div>
      {actions}
    </div>
  )
}

export const Pill = ({ tone: [label, bg, fg], children }: { tone: Tone; children?: ReactNode }) => (
  <span className="st" style={{ background: bg, color: fg }}>
    {children ?? label}
  </span>
)

export const TrustBadge = ({ verified }: { verified: boolean }) => (
  <Pill tone={verified ? ['Verified', 'var(--ok-bg)', 'var(--ok-fg)'] : ['Unverified', 'var(--warn-bg)', 'var(--warn-fg)']} />
)

export function ErrorText({ error }: { error: unknown }) {
  const text = errorText(error)
  return text ? <p className="err">{text}</p> : null
}

/** JSON indented when it parses, as text otherwise (truncated bodies, XML, form posts). */
export function Code({ value }: { value: unknown }) {
  let text: string
  if (value === null || value === undefined || value === '') text = '(no body)'
  else if (typeof value === 'string') {
    try {
      text = JSON.stringify(JSON.parse(value), null, 2)
    } catch {
      text = value
    }
  } else text = JSON.stringify(value, null, 2)
  return <pre className="code mono">{text}</pre>
}

/** States in columns by distance from the initial one; the current state highlighted. */
export function StateFlow({ entity, current }: { entity: EntityDescriptor; current?: string | null }) {
  const depth = new Map<string, number>([[entity.initial, 0]])
  const queue = [entity.initial]
  while (queue.length) {
    const from = queue.shift()!
    for (const t of entity.transitions)
      if (t.from === from && !depth.has(t.to)) {
        depth.set(t.to, depth.get(from)! + 1)
        queue.push(t.to)
      }
  }
  const last = Math.max(...depth.values())
  for (const s of entity.states) if (!depth.has(s)) depth.set(s, last + 1)
  const columns: string[][] = []
  for (const s of entity.states) (columns[depth.get(s)!] ??= []).push(s)

  return (
    <div className="row" style={{ gap: 6, overflowX: 'auto', paddingBottom: 4 }}>
      {columns.map((col, i) => (
        <div key={i} className="row" style={{ gap: 6 }}>
          {i > 0 && <span className="muted">→</span>}
          <div className="col" style={{ gap: 6 }}>
            {col.map(s => {
              const via = entity.transitions.find(t => t.to === s)
              return (
                <div key={s} className={`stbox mono${s === current ? ' on' : ''}`}>
                  {s}
                  <span className="muted" style={{ display: 'block', fontWeight: 400, fontSize: 10 }}>
                    {via ? `via ${via.trigger}` : 'initial'}
                  </span>
                </div>
              )
            })}
          </div>
        </div>
      ))}
    </div>
  )
}

/** Modal on the native <dialog>: focus stays inside and returns on close; Escape or the backdrop closes it. */
export function Modal({ label, onClose, children }: { label: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const dialog = ref.current
    dialog?.showModal()
    return () => dialog?.close()
  }, [])
  return (
    // The <dialog> fills the screen as the backdrop's hit area; the visible box is the inner .dialog.
    <dialog
      ref={ref}
      aria-label={label}
      className="overlay"
      onCancel={e => {
        e.preventDefault()
        onClose()
      }}
      onClick={e => e.target === e.currentTarget && onClose()}
    >
      <div className="dialog">
        <h2 style={{ fontSize: 16 }}>{label}</h2>
        {children}
      </div>
    </dialog>
  )
}

/** Tab strip with ids, aria-controls and arrow-key moves; spread `tabPanel(id, value)` on the panel it controls. */
export function Tabs<K extends string>({
  id,
  tabs,
  value,
  onChange,
  style,
}: {
  id: string
  tabs: readonly (readonly [K, string])[]
  value: K
  onChange: (key: K) => void
  style?: CSSProperties
}) {
  const move = (e: KeyboardEvent<HTMLDivElement>) => {
    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0
    if (!step) return
    const next = tabs[(tabs.findIndex(([k]) => k === value) + step + tabs.length) % tabs.length][0]
    onChange(next)
    document.getElementById(`${id}-tab-${next}`)?.focus()
  }
  return (
    <div className="tabs" role="tablist" style={style} onKeyDown={move}>
      {tabs.map(([key, label]) => (
        <button
          key={key}
          id={`${id}-tab-${key}`}
          role="tab"
          aria-selected={value === key}
          aria-controls={`${id}-panel`}
          tabIndex={value === key ? 0 : -1}
          onClick={() => onChange(key)}
        >
          {label}
        </button>
      ))}
    </div>
  )
}

// eslint-disable-next-line react-refresh/only-export-components -- helper of Tabs
export const tabPanel = (id: string, value: string) => ({ id: `${id}-panel`, role: 'tabpanel', 'aria-labelledby': `${id}-tab-${value}` })

/* eslint-disable react-refresh/only-export-components -- browser helpers shared by the panels */
export async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    return false
  }
}

export function save(name: string, blob: Blob) {
  const url = URL.createObjectURL(blob)
  Object.assign(document.createElement('a'), { href: url, download: name }).click()
  setTimeout(() => URL.revokeObjectURL(url))
}

export const download = (name: string, text: string) => save(name, new Blob([text], { type: 'application/x-pem-file' }))
