import { useSyncExternalStore } from 'react'
import type { DeliveryStatus, Interaction, InteractionKind, Sandbox } from '@/api'

const pad = (n: number) => String(n).padStart(2, '0')

/** "10/11 14:32:07" (month/day) in the browser's time zone. */
export function dayTime(at: string | number) {
  const d = new Date(at)
  return `${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${hms(d)}`
}

export function hms(at: string | number | Date) {
  const d = new Date(at)
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

export const ms = (n: number | null | undefined) => (n == null ? '' : n >= 1000 ? `${(n / 1000).toFixed(1)} s` : `${n} ms`)

/** 45 s, 12 min, 2 h 05 min. */
export function span(seconds: number) {
  const abs = Math.round(Math.abs(seconds))
  const h = Math.floor(abs / 3600)
  const m = Math.floor(abs / 60) % 60
  return abs < 60 ? `${abs} s` : h ? `${h} h ${pad(m)} min` : `${m} min`
}

export type Tone = readonly [label: string, bg: string, fg: string]
const tone = (label: string, name: 'ok' | 'info' | 'warn' | 'err' | 'purple'): Tone => [label, `var(--${name}-bg)`, `var(--${name}-fg)`]

export const kindTone: Record<InteractionKind, Tone> = {
  InboundRequest: tone('Call', 'info'),
  ControlAction: ['Action', 'var(--chip)', 'var(--fg-soft)'],
  StateTransition: tone('Transition', 'ok'),
  OutboundWebhook: tone('Webhook', 'purple'),
}

/** A clock transition gets its own label: the provider acted on its own (expiry, settlement). */
export const clockTone: Tone = tone('Clock', 'ok')
export const toneOf = (i: { kind: InteractionKind; byClock: boolean }) => (i.byClock ? clockTone : kindTone[i.kind])

export const statusTone = (code: number): Tone => tone(String(code), code < 300 ? 'ok' : code < 500 ? 'warn' : 'err')

export const isCall = (i: Interaction) => i.kind === 'InboundRequest' || i.kind === 'ControlAction'
export const change = (t: Interaction) => `${t.fromState ?? 'created'} → ${t.toState}`
export const changeTone = (t: Interaction): Tone => [change(t), 'var(--ok-bg)', 'var(--ok-fg)']
export const result = (i: Interaction): Tone =>
  i.kind === 'StateTransition' ? changeTone(i) : i.statusCode !== null ? statusTone(i.statusCode) : ['N/A', 'var(--chip)', 'var(--fg-soft)']

export const deliveryTone: Record<DeliveryStatus, Tone> = {
  Pending: tone('Pending', 'warn'),
  Delivering: tone('Delivering', 'info'),
  Delivered: tone('Delivered', 'ok'),
  DeadLettered: tone('Failed', 'err'),
}

// ponytail: state color by name; a connector with other final-state names falls back to blue.
export function stateTone(state: string | null): Tone {
  const s = state ?? 'N/A'
  if (/CONCLUID|Autorizad|RECEBID|REALIZAD|PAG/i.test(s)) return tone(s, 'ok')
  if (/REMOVID|Rejeitad|CANCELAD|EXPIRAD/i.test(s)) return tone(s, 'err')
  if (/ATRASAD/i.test(s)) return tone(s, 'warn')
  return tone(s, 'info')
}

// One shared 1 s ticker for every clock on screen.
let tick = Date.now()
const listeners = new Set<() => void>()
setInterval(() => {
  tick = Date.now()
  listeners.forEach(l => l())
}, 1000)
const subscribe = (l: () => void) => {
  listeners.add(l)
  return () => {
    listeners.delete(l)
  }
}
export const useNow = () => useSyncExternalStore(subscribe, () => tick)

/**
 * The sandbox clock as of now: the answer's instant, moving on in real time unless frozen.
 * Drift is how far it is from real time (positive = ahead).
 */
export function sandboxClock(s: Sandbox, fetchedAt: number, now: number) {
  const elapsed = Math.max(0, now - fetchedAt)
  const frozen = s.clockMode === 'Frozen'
  return {
    at: Date.parse(s.now) + (frozen ? 0 : elapsed),
    drift: s.driftSeconds - (frozen ? elapsed / 1000 : 0),
  }
}

export const sandboxKind = (s: Sandbox): Tone =>
  s.persistent ? tone('Permanent', 'info') : s.id === 'default' ? ['Default', 'var(--chip)', 'var(--fg-soft)'] : tone('Temporary', 'warn')

export function expiresText(s: Sandbox, now: number) {
  if (!s.expiresAt) return 'no expiry'
  const left = (Date.parse(s.expiresAt) - now) / 1000
  return left <= 0 ? 'expiring' : `expires in ${span(left)}`
}

export const errorText = (e: unknown) => (e instanceof Error ? e.message : e ? String(e) : '')
