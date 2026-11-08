import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import App from './App'
import { AuthGate } from './auth'
import './index.css'

// The chosen theme, else the system's. Only a per-browser convenience.
let theme: string | null = null
try {
  theme = localStorage.getItem('chameleon-theme')
} catch {
  // storage blocked: follow the system
}
document.documentElement.dataset.theme = theme ?? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } } })

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthGate>
        <BrowserRouter basename="/ui">
          <App />
        </BrowserRouter>
      </AuthGate>
    </QueryClientProvider>
  </StrictMode>,
)
