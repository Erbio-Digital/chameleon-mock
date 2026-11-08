// Client for the Chameleon control API. Everything except the sandbox lifecycle runs in a sandbox,
// addressed by the /s/{id} prefix.

export type InteractionKind = 'InboundRequest' | 'ControlAction' | 'StateTransition' | 'OutboundWebhook'

export interface Interaction {
  id: string
  seq: number
  sandboxId: string
  kind: InteractionKind
  connector: string
  entityId: string | null
  entityType: string | null
  correlationId: string | null
  at: string
  recordedAt: string
  summary: string
  detail: string | null
  method: string | null
  path: string | null
  statusCode: number | null
  durationMs: number | null
  requestHeaders: Record<string, string> | null
  requestBody: string | null
  responseBody: string | null
  truncated: boolean
  fault: string | null
  fromState: string | null
  toState: string | null
  /** A transition the sandbox clock made on its own; the others belong to the call that caused them. */
  byClock: boolean
}

export interface Page {
  items: Interaction[]
  nextBeforeSeq: number | null
}

export interface WebhookPolicy {
  maxAttempts: number
  baseDelayMs: number
  timeoutMs: number
}

export interface Sandbox {
  id: string
  persistent: boolean
  clockMode: 'Realtime' | 'Frozen'
  now: string
  /** Sandbox clock minus real time when answered; positive = ahead. */
  driftSeconds: number
  createdAt: string
  expiresAt: string | null
  webhookPolicy: WebhookPolicy | null
  /** Where extra webhook headers go; the values never come back. */
  webhookHeaders: { host: string; name: string }[]
}

export interface EntityDescriptor {
  type: string
  key: string
  initial: string
  states: string[]
  transitions: { from: string; to: string; trigger: string }[]
}

export interface ActionDescriptor {
  name: string
  entityType: string
  label: string
  allowedFrom: string[]
  parameters: { name: string; type: string; required: boolean }[]
  /** Runs on a key that does not exist yet and creates the entity. */
  createsMissing?: boolean
}

export interface VerifiedConnector {
  name: string
  packageId: string
  version: string
  title: string
  description: string
  provider: string
  repository: string
  license: string
  sdk: string
  revoked: string | null
  installed: boolean
  installedVersion: string | null
}

export interface CommunityPackage {
  packageId: string
  version: string
  title: string
  description: string
  repository: string | null
  license: string | null
  projectUrl: string | null
  inCatalog: boolean
}

export interface CommunityResult {
  offline: boolean
  error: string | null
  packages: CommunityPackage[]
}

export interface Connector {
  name: string
  /** Null: registered inside the host process (tests), not loaded from a package. */
  packageId: string | null
  version: string | null
  verified: boolean
  entities: EntityDescriptor[]
  actions: ActionDescriptor[]
  /** Null: the connector sends no webhooks (lookup APIs). */
  webhooks: WebhookPolicy | null
  // Present when the provider keeps webhook registrations (key = what identifies one, e.g. "key").
  registrations: { key: string } | null
  routes: string[]
  title: string | null
  category: string
  description: string | null
  credentials: { env: string; label: string; kind: 'Id' | 'Secret' | 'Url' }[]
  settings: ConnectorSetting[]
  hasTestEvent: boolean
  // Banking connector: keeps sandbox accounts (simulated balances).
  hasAccounts: boolean
  clientCertificate: boolean
  webhookCertificate: boolean
  // Outgoing operations settle asynchronously: the "settlement" fault route applies.
  settlement: boolean
}

export interface BankAccount {
  nickname: string
  bank: string
  branch: string
  account: string
  pixKey: string | null
  balance: string
  createdAt: string
}

export interface LedgerEntry {
  id: string
  at: string
  kind: 'Credit' | 'Debit' | 'Adjustment'
  amount: string
  description: string
  reference: string | null
}

export type BankAccountDetail = BankAccount & { entries: LedgerEntry[] }

export interface ConnectorSetting {
  key: string
  label: string
  kind: 'url' | 'text'
  required: boolean
  help: string | null
}

export type SubscriptionStatus = 'Available' | 'Pending' | 'Active'

export interface SubscriptionSummary {
  connector: string
  status: SubscriptionStatus
  subscribedAt: string | null
  missing: string[]
}

export interface Subscription {
  status: SubscriptionStatus
  subscribedAt?: string
  rotatedAt?: string | null
  credentials: { env: string; label: string; secret: boolean; value: string }[]
  settings?: Record<string, string>
  missing?: string[]
  // Present when the connector requires a client certificate (mTLS).
  certificate?: SubscriptionCertificate | null
}

export interface SubscriptionCertificate {
  pem: string
  keyPem: string | null
  /** Password of the .pfx export; null until chosen on the first download. */
  pfxPassword: string | null
  caPem: string
  thumbprint: string
  notAfter: string
  source: 'Generated' | 'Csr'
}

export interface WebhookRegistration {
  connector: string
  key: string
  url: string
  createdAt: string
}

export interface EntitySummary {
  key: string
  state: string | null
  version: number
  dueAt: string | null
  updatedAt: string
  data: unknown
}

export type DeliveryStatus = 'Pending' | 'Delivering' | 'Delivered' | 'DeadLettered'

export interface Delivery {
  id: string
  sandboxId: string
  connector: string
  entityId: string
  correlationId: string | null
  url: string
  status: DeliveryStatus
  event: string | null
  attempts: number
  maxAttempts: number
  baseDelayMs: number
  timeoutMs: number
  nextAttemptAt: string
  note: string | null
  createdAt: string
}

export interface Attempt {
  number: number
  at: string
  url: string
  statusCode: number | null
  durationMs: number
  responseBody: string | null
  error: string | null
  blocked: boolean
}

export interface EntityDetail extends EntitySummary {
  allowedActions: string[]
  timeline: Interaction[]
  webhooks: Delivery[]
}

export type FaultKind = 'Timeout' | 'Error' | 'Latency' | 'Malformed' | 'Duplicate' | 'Reorder' | 'Hold'
export const WEBHOOK_ROUTE = 'webhook'
export const SETTLEMENT_ROUTE = 'settlement'
/** Every provider route of the connector; a fault on the exact route wins. */
export const ALL_ROUTES = '*'

export interface ArmedFault {
  connector: string
  route: string
  kind: FaultKind
  status: number | null
  delayMs: number | null
  remaining: number | null
  until: string | null
}

export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

// Shared instance. With OIDC, the session token goes on every call; a 401 sends the panel back to the login.
let accessToken: string | null = null
let onUnauthorized = () => {}
export function setApiAuth(token: string | null, unauthorized: () => void) {
  accessToken = token
  onUnauthorized = unauthorized
}

/** The current bearer, for the live channel. */
export const getAccessToken = () => accessToken

async function send(path: string, init?: RequestInit) {
  const response = await fetch(path, {
    ...init,
    headers: accessToken ? { Authorization: `Bearer ${accessToken}`, ...init?.headers } : init?.headers,
  })
  if (response.status === 401 && accessToken) onUnauthorized()
  return response
}

async function request<T>(path: string, init?: RequestInit & { json?: unknown }): Promise<T> {
  const { json, ...rest } = init ?? {}
  const response = await send(path, {
    ...rest,
    headers: json === undefined ? rest.headers : { 'Content-Type': 'application/json', ...rest.headers },
    body: json === undefined ? rest.body : JSON.stringify(json),
  })
  const text = await response.text()
  let body
  try {
    body = text ? JSON.parse(text) : undefined
  } catch {
    // A proxy error page or a plain-text answer: the status still tells what happened.
    throw new ApiError(response.status, response.ok ? 'Invalid response from the server.' : `${response.status} ${response.statusText}`)
  }
  if (!response.ok) throw new ApiError(response.status, body?.message ?? `${response.status} ${response.statusText}`)
  return body as T
}

const scoped = (sandbox: string, path: string) => `/s/${encodeURIComponent(sandbox)}/_chameleon${path}`

function query(params: Record<string, string | number | null | undefined>) {
  const q = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') q.set(k, String(v))
  const s = q.toString()
  return s ? `?${s}` : ''
}

export interface FeedFilter {
  connector?: string
  kind?: string
  statusClass?: string
  byClock?: string
  from?: string
  entityId?: string
  correlationId?: string
}

export interface OidcConfig {
  issuer: string
  clientId: string
  /** Set when the provider issues JWT access tokens for this API; otherwise the id_token is the bearer. */
  audience: string | null
  scope: string
}

export interface ChameleonConfig {
  auth: 'none' | 'oidc'
  oidc: OidcConfig | null
  /** E-mail domains allowed to sign in (without the "@"), shown as a hint. */
  allowedEmailDomains: string[]
}

export type Role = 'OPERATOR' | 'AGENT' | 'CI'

export interface AccessToken {
  id: string
  name: string
  createdAt: string
  lastUsedAt: string | null
  revokedAt: string | null
}

export const api = {
  config: () => request<ChameleonConfig>('/_chameleon/config'),
  me: () => request<{ email: string | null; role: Role }>('/_chameleon/me'),
  tokens: () => request<AccessToken[]>('/_chameleon/tokens'),
  createToken: (name: string) =>
    request<{ id: string; name: string; token: string; createdAt: string }>('/_chameleon/tokens', { method: 'POST', json: { name } }),
  revokeToken: (id: string) => request<void>(`/_chameleon/tokens/${encodeURIComponent(id)}`, { method: 'DELETE' }),

  sandboxes: () => request<Sandbox[]>('/_chameleon/sandboxes'),
  createSandbox: (body: { id: string; clockMode: string; ttlSeconds?: number; from?: string; permanent?: boolean }) =>
    request<Sandbox>('/_chameleon/sandboxes', { method: 'POST', json: body }),
  resetSandbox: (id: string, confirm?: string) =>
    request<Sandbox>(`/_chameleon/sandboxes/${encodeURIComponent(id)}/reset${query({ confirm })}`, { method: 'POST' }),
  removeSandbox: (id: string, confirm?: string) =>
    request<void>(`/_chameleon/sandboxes/${encodeURIComponent(id)}${query({ confirm })}`, { method: 'DELETE' }),
  setClockMode: (id: string, mode: string) => request<Sandbox>(`/_chameleon/sandboxes/${encodeURIComponent(id)}/clock`, { method: 'POST', json: { mode } }),
  clockNow: (id: string) => request<Sandbox>(`/_chameleon/sandboxes/${encodeURIComponent(id)}/clock/now`, { method: 'POST' }),
  setWebhookPolicy: (id: string, policy: WebhookPolicy | null) =>
    policy
      ? request<Sandbox>(`/_chameleon/sandboxes/${encodeURIComponent(id)}/webhook-policy`, { method: 'PUT', json: policy })
      : request<Sandbox>(`/_chameleon/sandboxes/${encodeURIComponent(id)}/webhook-policy`, { method: 'DELETE' }),
  /** value null keeps the stored one for the same host and name. */
  setWebhookHeaders: (id: string, headers: { host: string; name: string; value: string | null }[]) =>
    request<Sandbox>(`/_chameleon/sandboxes/${encodeURIComponent(id)}/webhook-headers`, { method: 'PUT', json: { headers } }),
  advanceClock: (sandbox: string, seconds: number) =>
    request<{ now: string }>(scoped(sandbox, '/clock/advance'), { method: 'POST', json: { seconds } }),

  interactions: (sandbox: string, filter: FeedFilter, cursor: { afterSeq?: number; beforeSeq?: number; limit?: number }) =>
    request<Page>(scoped(sandbox, `/interactions${query({ ...filter, ...cursor })}`)),
  interaction: (sandbox: string, id: string) => request<Interaction>(scoped(sandbox, `/interactions/${encodeURIComponent(id)}`)),

  connectors: () => request<Connector[]>('/_chameleon/connectors'),
  marketplaceVerified: () => request<VerifiedConnector[]>('/_chameleon/marketplace/verified'),
  marketplaceCommunity: (q: string) => request<CommunityResult>(`/_chameleon/marketplace/community${query({ q })}`),
  entities: (sandbox: string, connector: string, type: string) =>
    request<EntitySummary[]>(scoped(sandbox, `/connectors/${connector}/entities/${type}`)),
  entity: (sandbox: string, connector: string, type: string, key: string) =>
    request<EntityDetail>(scoped(sandbox, `/connectors/${connector}/entities/${type}/${encodeURIComponent(key)}`)),
  runAction: (sandbox: string, connector: string, type: string, key: string, action: string, args?: Record<string, unknown>) =>
    request<unknown>(scoped(sandbox, `/connectors/${connector}/entities/${type}/${encodeURIComponent(key)}/actions/${action}`), {
      method: 'POST',
      json: args,
    }),

  webhooks: (sandbox: string) => request<Delivery[]>(scoped(sandbox, '/webhooks')),
  webhook: (sandbox: string, id: string) =>
    request<{ delivery: Delivery; payload: string | null; attempts: Attempt[] }>(scoped(sandbox, `/webhooks/${encodeURIComponent(id)}`)),
  resendWebhook: (sandbox: string, id: string, url?: string) =>
    request<void>(scoped(sandbox, `/webhooks/${encodeURIComponent(id)}/resend`), { method: 'POST', json: url ? { url } : {} }),
  subscriptions: (sandbox: string) => request<SubscriptionSummary[]>(scoped(sandbox, '/subscriptions')),
  subscription: (sandbox: string, connector: string) => request<Subscription>(scoped(sandbox, `/connectors/${connector}/subscription`)),
  subscribe: (sandbox: string, connector: string) => request<unknown>(scoped(sandbox, `/connectors/${connector}/subscription`), { method: 'POST' }),
  unsubscribe: (sandbox: string, connector: string) => request<void>(scoped(sandbox, `/connectors/${connector}/subscription`), { method: 'DELETE' }),
  rotate: (sandbox: string, connector: string) => request<unknown>(scoped(sandbox, `/connectors/${connector}/subscription/rotate`), { method: 'POST' }),
  saveSettings: (sandbox: string, connector: string, settings: Record<string, string>) =>
    request<{ status: SubscriptionStatus; missing: string[] }>(scoped(sandbox, `/connectors/${connector}/subscription/settings`), {
      method: 'PUT',
      json: settings,
    }),
  signCsr: (sandbox: string, connector: string, csr: string) =>
    request<{ thumbprint: string; notAfter: string; source: string }>(scoped(sandbox, `/connectors/${connector}/subscription/certificate`), {
      method: 'POST',
      json: { csr },
    }),
  setPfxPassword: (sandbox: string, connector: string, password: string) =>
    request<void>(scoped(sandbox, `/connectors/${connector}/subscription/certificate/pfx-password`), { method: 'PUT', json: { password } }),
  pfx: async (sandbox: string, connector: string) => {
    const response = await send(scoped(sandbox, `/connectors/${connector}/subscription/certificate.pfx`))
    if (!response.ok) throw new ApiError(response.status, (await response.json().catch(() => null))?.message ?? `${response.status} ${response.statusText}`)
    return response.blob()
  },
  testEvent: (sandbox: string, connector: string) =>
    request<{ sent: number }>(scoped(sandbox, `/connectors/${connector}/subscription/test-event`), { method: 'POST' }),
  registrations: (sandbox: string) => request<WebhookRegistration[]>(scoped(sandbox, '/webhook-registrations')),
  setRegistration: (sandbox: string, connector: string, key: string, url: string) =>
    request<WebhookRegistration>(scoped(sandbox, `/connectors/${connector}/webhook-registrations/${encodeURIComponent(key)}`), {
      method: 'PUT',
      json: { url },
    }),
  removeRegistration: (sandbox: string, connector: string, key: string) =>
    request<void>(scoped(sandbox, `/connectors/${connector}/webhook-registrations/${encodeURIComponent(key)}`), { method: 'DELETE' }),

  accounts: (sandbox: string, bank?: string) =>
    request<BankAccount[]>(scoped(sandbox, `/accounts${query({ bank })}`)),
  account: (sandbox: string, nickname: string) => request<BankAccountDetail>(scoped(sandbox, `/accounts/${encodeURIComponent(nickname)}`)),
  openAccount: (sandbox: string, bank: string, body: { nickname: string; branch: string; account: string; pixKey?: string; balance?: number }) =>
    request<BankAccountDetail>(scoped(sandbox, `/connectors/${bank}/accounts`), { method: 'POST', json: body }),
  updateAccount: (sandbox: string, bank: string, nickname: string, body: { branch: string; account: string; pixKey: string }) =>
    request<BankAccountDetail>(scoped(sandbox, `/connectors/${bank}/accounts/${encodeURIComponent(nickname)}`), { method: 'PATCH', json: body }),
  setBalance: (sandbox: string, bank: string, nickname: string, balance: number) =>
    request<BankAccountDetail>(scoped(sandbox, `/connectors/${bank}/accounts/${encodeURIComponent(nickname)}/balance`), {
      method: 'PUT',
      json: { balance },
    }),

  faults: (sandbox: string) => request<ArmedFault[]>(scoped(sandbox, '/faults')),
  armFault: (
    sandbox: string,
    body: { connector: string; route: string; kind: FaultKind; status?: number; delayMs?: number; times?: number; durationSeconds?: number },
  ) =>
    request<unknown>(scoped(sandbox, '/faults'), { method: 'POST', json: body }),
  clearFault: (sandbox: string, connector: string, route: string) =>
    request<void>(scoped(sandbox, `/faults${query({ connector, route })}`), { method: 'DELETE' }),
}
