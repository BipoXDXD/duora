import './app/zod-config'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './app/App'
import { isPrivatePath, reloadWhenRestoredFromBackForwardCache } from './app/back-forward-cache'
import { reloadOnceForNewVersion } from './app/new-version-reload'
import { reloadPage } from './shared/ui/reload-page'
import './index.css'

// O Vite avisa por este evento quando um chunk não baixa; quase sempre a aba ficou aberta durante um deploy.
// O erro segue para o error boundary da área, que oferece recarregar se a recarga automática não acontecer.
window.addEventListener('vite:preloadError', () => {
  reloadOnceForNewVersion({ storage: () => window.sessionStorage, now: Date.now(), reload: reloadPage })
})

// Voltar no navegador não pode reexibir a tela de outra identidade a partir do bfcache (T-21, T-14).
reloadWhenRestoredFromBackForwardCache({
  target: window,
  isPrivatePage: () => isPrivatePath(window.location.pathname),
  hide: () => {
    document.body.hidden = true
  },
  reload: reloadPage,
})

const rootElement = document.getElementById('root')
if (rootElement === null) {
  throw new Error('Elemento #root ausente em index.html.')
}

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
