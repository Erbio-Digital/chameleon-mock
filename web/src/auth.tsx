import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, setApiAuth, type ChameleonConfig, type OidcConfig, type Role } from '@/api'
import { ErrorText } from '@/components/common'

// Who uses the panel. With auth "none" there is no login; with "oidc" the operator signs in at the
// configured provider (Authorization Code + PKCE, public client) and the token goes on every call.

export interface Session {
  config: ChameleonConfig
  user: { email: string | null; role: Role } | null
  signOut: () => void
}

const SessionContext = createContext<Session | null>(null)

// eslint-disable-next-line react-refresh/only-export-components -- the hook belongs with its provider
export const useSession = () => useContext(SessionContext)!

const CALLBACK = '/ui/callback'

// The code can be redeemed once; StrictMode runs the effect twice in development.
const callbacks = new WeakMap<UserManager, Promise<User>>()

export function AuthGate({ children }: { children: ReactNode }) {
  const config = useQuery({ queryKey: ['config'], queryFn: api.config, staleTime: Infinity })
  if (config.error)
    return (
      <Centered>
        <ErrorText error={config.error} />
      </Centered>
    )
  if (!config.data) return null
  if (config.data.auth === 'oidc' && config.data.oidc) return <OidcGate config={config.data} oidc={config.data.oidc}>{children}</OidcGate>
  return <LocalSession config={config.data}>{children}</LocalSession>
}

function LocalSession({ config, children }: { config: ChameleonConfig; children: ReactNode }) {
  const value = useMemo(() => ({ config, user: null, signOut: () => {} }), [config])
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

/** D2: the access token when the API has its own audience, else the id_token (aud = client id). */
const bearerOf = (user: User, oidc: OidcConfig) => (oidc.audience ? user.access_token : user.id_token) ?? null

function OidcGate({ config, oidc, children }: { config: ChameleonConfig; oidc: OidcConfig; children: ReactNode }) {
  const queryClient = useQueryClient()
  const manager = useMemo(
    () =>
      new UserManager({
        authority: oidc.issuer,
        client_id: oidc.clientId,
        redirect_uri: `${window.location.origin}${CALLBACK}`,
        post_logout_redirect_uri: `${window.location.origin}/ui/`,
        response_type: 'code',
        scope: oidc.scope,
        extraQueryParams: oidc.audience ? { audience: oidc.audience } : undefined,
        userStore: new WebStorageStateStore({ store: window.sessionStorage }),
        automaticSilentRenew: false,
      }),
    [oidc],
  )
  // undefined while the stored session (or the callback) is read.
  const [user, setUser] = useState<User | null>()
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    const apply = (next: User | null) => {
      const token = next && !next.expired ? bearerOf(next, oidc) : null
      setApiAuth(token, () => void manager.removeUser().then(() => apply(null)))
      setUser(token ? next : null)
      // Nothing from another user's session stays on screen.
      if (!token) queryClient.removeQueries({ predicate: q => q.queryKey[0] !== 'config' })
    }
    const load = async () => {
      if (window.location.pathname === CALLBACK) {
        const pending = callbacks.get(manager) ?? manager.signinRedirectCallback()
        callbacks.set(manager, pending)
        const signedIn = await pending
        const saved = signedIn.state
        const back = typeof saved === 'string' && saved.startsWith('/ui/') && !saved.startsWith('//') ? saved : '/ui/'
        window.history.replaceState(null, '', back)
        return signedIn
      }
      return manager.getUser()
    }
    load().then(apply, e => {
      if (window.location.pathname === CALLBACK) window.history.replaceState(null, '', '/ui/')
      setError(e)
      apply(null)
    })
    manager.events.addUserLoaded(apply)
    // removeUser (sign-out fallback, 401) funnels through the same cleanup.
    const unloaded = () => apply(null)
    manager.events.addUserUnloaded(unloaded)
    const expired = () => apply(null)
    manager.events.addAccessTokenExpired(expired)
    return () => {
      manager.events.removeUserLoaded(apply)
      manager.events.removeUserUnloaded(unloaded)
      manager.events.removeAccessTokenExpired(expired)
    }
  }, [manager, oidc, queryClient])

  const sub = user?.profile.sub
  const me = useQuery({ queryKey: ['me', sub], queryFn: api.me, enabled: !!sub, staleTime: Infinity, retry: false })
  const signOut = () => void manager.signoutRedirect().catch(() => manager.removeUser())
  const value = useMemo<Session | null>(() => (me.data ? { config, user: me.data, signOut } : null), [config, me.data]) // eslint-disable-line react-hooks/exhaustive-deps

  if (user === undefined) return null
  if (!user)
    return (
      <Centered>
        <div className="wordmark">
          <img src="/ui/logo.svg" alt="" />
          Chameleon
        </div>
        <ErrorText error={error} />
        <button className="btn btn-pri" onClick={() => manager.signinRedirect({ state: window.location.pathname === CALLBACK ? '/ui/' : window.location.pathname + window.location.search }).catch(setError)}>
          Sign in with {new URL(oidc.issuer).host}
        </button>
        {config.allowedEmailDomains.length > 0 && (
          <p className="muted" style={{ margin: 0 }}>
            Access for {config.allowedEmailDomains.map(d => `@${d}`).join(', ')} and invited e-mails.
          </p>
        )}
      </Centered>
    )
  if (me.error)
    return (
      <Centered>
        <ErrorText error={me.error} />
        <button className="btn btn-sec" onClick={signOut}>
          Sign out
        </button>
      </Centered>
    )
  if (!value) return null
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

const Centered = ({ children }: { children: ReactNode }) => (
  <div className="col" style={{ minHeight: '100vh', alignItems: 'center', justifyContent: 'center', gap: 16, padding: 16 }}>
    {children}
  </div>
)
