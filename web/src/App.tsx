import { useState } from 'react'
import { Link, Navigate, NavLink, Outlet, Route, Routes, useLocation, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, Clock, KeyRound, LogOut, Moon, Plug, Receipt, Server, Store, Sun, Webhook, Zap } from 'lucide-react'
import { api } from '@/api'
import { LiveProvider, useLive } from '@/live'
import { useSession } from '@/auth'
import { ErrorText } from '@/components/common'
import { dayTime, expiresText, sandboxClock, span, useNow } from '@/format'
import Feed from '@/pages/Feed'
import Entities from '@/pages/Entities'
import Webhooks from '@/pages/Webhooks'
import Faults from '@/pages/Faults'
import Connectors from '@/pages/Connectors'
import Marketplace from '@/pages/Marketplace'
import Sandboxes from '@/pages/Sandboxes'
import Tokens from '@/pages/Tokens'

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/s/default/feed" replace />} />
      <Route path="/s/:sandbox" element={<Shell />}>
        <Route index element={<Navigate to="feed" replace />} />
        <Route path="feed/:id?" element={<Feed />} />
        <Route path="entities/:connector?/:type?/:key?" element={<Entities />} />
        <Route path="webhooks/:id?" element={<Webhooks />} />
        <Route path="faults" element={<Faults />} />
        <Route path="connectors/:name?" element={<Connectors />} />
        <Route path="marketplace" element={<Marketplace />} />
        <Route path="sandboxes/:id?" element={<Sandboxes />} />
        <Route path="tokens" element={<Tokens />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

const sandboxNav = [
  ['feed', 'Feed', Activity],
  ['entities', 'Entities', Receipt],
  ['webhooks', 'Webhooks', Webhook],
  ['faults', 'Faults', Zap],
  ['connectors', 'Connectors', Plug],
] as const

function Shell() {
  const { sandbox = 'default' } = useParams()
  const { user, config } = useSession()
  const connectors = useQuery({ queryKey: ['connectors'], queryFn: api.connectors, staleTime: Infinity })
  const unverified = (connectors.data ?? []).filter(c => c.packageId && !c.verified)
  return (
    // Keyed by sandbox: switching tears down the live channel and the feed of the previous one.
    <LiveProvider key={sandbox} sandbox={sandbox}>
      <div className="app">
        {config.auth === 'none' && (
          <div className="banner-warn" role="status">
            Authentication is off. Anyone who reaches this instance can operate it; set CHAMELEON_AUTH=oidc outside a local machine.
          </div>
        )}
        {unverified.length > 0 && (
          <div className="banner-warn" role="status">
            Unverified connectors loaded: {unverified.map(c => c.name).join(', ')}. Their code was not reviewed by the catalog maintainers.
          </div>
        )}
        <TopBar sandbox={sandbox} />
        <div className="body">
          <nav className="nav" aria-label="Menu">
            <div className="sec">SANDBOX</div>
            {sandboxNav.map(([path, label, Icon]) => (
              <NavLink key={path} to={`/s/${sandbox}/${path}`}>
                <Icon aria-hidden />
                {label}
              </NavLink>
            ))}
            <div className="sec">INSTANCE</div>
            <NavLink to={`/s/${sandbox}/marketplace`}>
              <Store aria-hidden />
              Marketplace
            </NavLink>
            <NavLink to={`/s/${sandbox}/sandboxes`}>
              <Server aria-hidden />
              Sandboxes
            </NavLink>
            {user?.role === 'OPERATOR' && (
              <NavLink to={`/s/${sandbox}/tokens`}>
                <KeyRound aria-hidden />
                Access tokens
              </NavLink>
            )}
          </nav>
          <main className="page">
            <Outlet />
          </main>
        </div>
      </div>
    </LiveProvider>
  )
}

function TopBar({ sandbox }: { sandbox: string }) {
  const navigate = useNavigate()
  const location = useLocation()
  const now = useNow()
  const sandboxes = useQuery({ queryKey: ['sandboxes'], queryFn: api.sandboxes })
  const section = location.pathname.split('/')[3] ?? 'feed'

  return (
    <header className="topbar">
      <Link className="wordmark" to={`/s/${sandbox}/feed`}>
        <img src="/ui/logo.svg" alt="" />
        Chameleon
      </Link>
      <div className="vr" />
      <label className="row">
        <span className="small muted">Sandbox</span>
        <select className="ctl" value={sandbox} onChange={e => navigate(`/s/${e.target.value}/${section}`)}>
          {sandboxes.data ? (
            sandboxes.data.map(s => (
              <option key={s.id} value={s.id}>
                {s.id}
                {s.persistent ? ' · permanent' : s.expiresAt ? ` · ${expiresText(s, now)}` : ''}
              </option>
            ))
          ) : (
            <option value={sandbox}>{sandbox}</option>
          )}
        </select>
      </label>
      <ErrorText error={sandboxes.error} />
      <ClockWidget sandbox={sandbox} />
      <div className="grow" />
      <ThemeToggle />
      <LiveStatus />
      <UserBadge />
    </header>
  )
}

function ClockWidget({ sandbox }: { sandbox: string }) {
  const queryClient = useQueryClient()
  const now = useNow()
  const sandboxes = useQuery({ queryKey: ['sandboxes'], queryFn: api.sandboxes })
  const current = sandboxes.data?.find(s => s.id === sandbox)
  const done = () => {
    queryClient.invalidateQueries({ queryKey: ['sandboxes'] })
    queryClient.invalidateQueries({ queryKey: [sandbox] })
  }
  const change = useMutation({ mutationFn: (run: () => Promise<unknown>) => run(), onSuccess: done })
  if (!current) return null

  const frozen = current.clockMode === 'Frozen'
  const clock = sandboxClock(current, sandboxes.dataUpdatedAt, now)
  const drifting = Math.abs(clock.drift) >= 2
  return (
    <div className="clock" role="group" aria-label="Sandbox clock">
      <Clock size={15} className="muted" aria-hidden />
      <span className="mono" style={{ fontWeight: 600, fontSize: 12.5 }}>
        {dayTime(clock.at)}
      </span>
      <span className="st" style={frozen ? { background: 'var(--info-bg)', color: 'var(--info-fg)' } : { background: 'var(--ok-bg)', color: 'var(--ok-fg)' }}>
        {frozen ? 'Frozen' : 'Real time'}
      </span>
      {drifting && <span className="st drift">{`${clock.drift > 0 ? 'ahead' : 'behind'} ${span(clock.drift)}`}</span>}
      <button className="chip-btn" disabled={change.isPending} onClick={() => change.mutate(() => api.advanceClock(sandbox, 60))}>
        +1 min
      </button>
      <button className="chip-btn" disabled={change.isPending} onClick={() => change.mutate(() => api.advanceClock(sandbox, 3600))}>
        +1 h
      </button>
      <button
        className="chip-btn"
        disabled={change.isPending}
        onClick={() => change.mutate(() => api.setClockMode(sandbox, frozen ? 'Realtime' : 'Frozen'))}
      >
        {frozen ? 'Real time' : 'Freeze'}
      </button>
      <button
        className="chip-btn now-btn"
        data-hot={drifting || frozen}
        disabled={change.isPending}
        title="Back to the real time and to real-time flow"
        onClick={() => change.mutate(() => api.clockNow(sandbox))}
      >
        Now
      </button>
      <ErrorText error={change.error} />
    </div>
  )
}

function ThemeToggle() {
  const [theme, setTheme] = useState(() => document.documentElement.dataset.theme)
  const dark = theme === 'dark'
  const toggle = () => {
    const next = dark ? 'light' : 'dark'
    document.documentElement.dataset.theme = next
    try {
      localStorage.setItem('chameleon-theme', next)
    } catch {
      // private window: the theme just does not stick
    }
    setTheme(next)
  }
  const label = dark ? 'Use light theme' : 'Use dark theme'
  return (
    <button className="chip-btn" onClick={toggle} aria-label={label} title={label}>
      {dark ? <Sun size={14} aria-hidden /> : <Moon size={14} aria-hidden />}
      {dark ? 'Light' : 'Dark'}
    </button>
  )
}

function UserBadge() {
  const { user, signOut } = useSession()
  if (!user) return null
  return (
    <span className="row small">
      <span className="mono">{user.email}</span>
      <span className="tag">{user.role}</span>
      <button className="chip-btn" onClick={signOut} title="Sign out">
        <LogOut size={14} aria-hidden />
        Sign out
      </button>
    </span>
  )
}

function LiveStatus() {
  const { status } = useLive()
  const [label, color] =
    status === 'live' ? ['Live', 'var(--live)'] : status === 'connecting' ? ['Connecting', 'var(--warn-fg)'] : ['Disconnected', 'var(--danger)']
  return (
    <span className="livedot" style={{ color }}>
      <span style={{ color: status === 'live' ? 'var(--ok-fg)' : color }}>{label}</span>
    </span>
  )
}
