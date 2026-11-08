import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { getAccessToken } from '@/api'

// Live channel of one sandbox. Events never carry state the panel keeps: interactions go to the
// feed as they come, everything else just tells the cached queries of the sandbox to refetch.
// The channel is the SignalR hub.

export interface LiveEvent {
  type: 'interaction' | 'entityChanged' | 'webhookUpdated' | 'faultChanged' | 'sandboxChanged' | 'overflow'
  data: Record<string, unknown>
}

type Listener = (events: LiveEvent[]) => void
export type LiveStatus = 'connecting' | 'live' | 'offline'

interface Live {
  status: LiveStatus
  subscribe: (listener: Listener) => () => void
}

const LiveContext = createContext<Live>({ status: 'offline', subscribe: () => () => {} })

export function LiveProvider({ sandbox, children }: { sandbox: string; children: ReactNode }) {
  const queryClient = useQueryClient()
  const listeners = useRef(new Set<Listener>())
  const [status, setStatus] = useState<LiveStatus>('connecting')

  useEffect(() => {
    const onBatch = (batch: { sandboxId: string; events: LiveEvent[] }) => {
      if (batch.sandboxId !== sandbox || !Array.isArray(batch.events)) return
      if (batch.events.some(e => e.type !== 'interaction')) queryClient.invalidateQueries({ queryKey: [sandbox] })
      if (batch.events.some(e => e.type === 'sandboxChanged')) queryClient.invalidateQueries({ queryKey: ['sandboxes'] })
      // Timelines and lists also show interactions: refresh them too.
      if (batch.events.some(e => e.type === 'interaction')) {
        queryClient.invalidateQueries({ queryKey: [sandbox, 'entity'] })
        // A registration changes through a call to the provider (or a control action): no entity event.
        queryClient.invalidateQueries({ queryKey: [sandbox, 'registrations'] })
        queryClient.invalidateQueries({ queryKey: [sandbox, 'subscriptions'] })
        queryClient.invalidateQueries({ queryKey: [sandbox, 'subscription'] })
        // A paid charge credits an account with no entity event of its own.
        queryClient.invalidateQueries({ queryKey: [sandbox, 'accounts'] })
        queryClient.invalidateQueries({ queryKey: [sandbox, 'account'] })
      }
      listeners.current.forEach(l => l(batch.events))
    }
    // Whatever happened while disconnected: let the feed catch up through the cursor.
    const catchUp = () => listeners.current.forEach(l => l([{ type: 'overflow', data: { dropped: 0 } }]))

    const connection = new HubConnectionBuilder()
      .withUrl('/_chameleon/hub', { accessTokenFactory: () => getAccessToken() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()
    let stopped = false

    connection.on('events', onBatch)

    const join = async () => {
      await connection.invoke('Join', sandbox)
      setStatus('live')
    }
    connection.onreconnecting(() => setStatus('connecting'))
    connection.onreconnected(() => {
      join().catch(() => setStatus('offline'))
      catchUp()
    })
    connection.onclose(() => setStatus('offline'))

    setStatus('connecting')
    connection
      .start()
      .then(() => (stopped ? undefined : join()))
      .catch(() => !stopped && setStatus('offline'))

    return () => {
      stopped = true
      if (connection.state === HubConnectionState.Connected) connection.invoke('Leave', sandbox).catch(() => {})
      connection.stop()
    }
  }, [sandbox, queryClient])

  const subscribe = useCallback((listener: Listener) => {
    listeners.current.add(listener)
    return () => {
      listeners.current.delete(listener)
    }
  }, [])
  const value = useMemo(() => ({ status, subscribe }), [status, subscribe])

  return <LiveContext.Provider value={value}>{children}</LiveContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components -- the hook belongs with its provider
export const useLive = () => useContext(LiveContext)
