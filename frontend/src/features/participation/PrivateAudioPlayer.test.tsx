import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { expect, it } from 'vitest'
import { PrivateOperationProvider } from '@/features/session/PrivateOperationProvider'
import { apiClient } from '@/shared/api'
import { I18nProvider } from '@/shared/i18n/I18nProvider'
import { createPrivateAudioCoordinator } from '@/shared/private-audio'
import { fakeAudioContext } from '@/test/audio-context'
import { API, server } from '@/test/msw'
import { pcmWave } from '@/test/pcm-wave'
import { installTestMe } from '@/test/private-context'
import { PrivateAudioPlayer } from './PrivateAudioPlayer'

const PATH = '/api/v1/rooms/room/audio/cue'
function mount(audio = fakeAudioContext(), unsupported = false) {
  const me = installTestMe({
    id: '00000000-0000-4000-8000-000000000001',
    email: 'synthetic@example.test',
    status: 'Active',
    emailConfirmed: true,
    mfaEnabled: false,
    roles: ['participant'],
  })
  const coordinator = createPrivateAudioCoordinator({
    client: apiClient,
    origin: location.origin,
    createContext: () => {
      if (unsupported) throw new Error('audio-unsupported')
      return audio.context
    },
  })
  const view = render(
    <I18nProvider initialLocale="pt-BR">
      <PrivateOperationProvider owner={me.owner}>
        <PrivateAudioPlayer path={PATH} coordinator={coordinator} />
      </PrivateOperationProvider>
    </I18nProvider>,
  )
  return { ...view, audio }
}

it('só obtém bytes no gesto e oferece controles acessíveis sem perder o foco', async () => {
  let reads = 0
  server.use(
    http.get(`${API}/rooms/room/audio/cue`, () => {
      reads += 1
      return new HttpResponse(pcmWave().buffer, { headers: { 'Content-Type': 'audio/wav' } })
    }),
  )
  const { audio, unmount } = mount()
  const user = userEvent.setup()
  expect(reads).toBe(0)
  expect(audio.value.resume).not.toHaveBeenCalled()
  const play = screen.getByRole('button', { name: 'Reproduzir áudio' })
  await user.click(play)
  await screen.findByRole('button', { name: 'Pausar áudio' })
  expect(play).toHaveFocus()
  expect(reads).toBe(1)
  expect(audio.gain.gain.value).toBe(0)
  await user.click(screen.getByRole('button', { name: 'Ativar som' }))
  expect(screen.getByRole('button', { name: 'Silenciar áudio' })).toHaveAttribute('aria-pressed', 'true')
  expect(audio.gain.gain.value).toBe(1)
  fireEvent.change(screen.getByRole('slider', { name: 'Posição do áudio' }), { target: { value: '0.05' } })
  await waitFor(() => {
    expect(audio.outputs.at(-1)?.start).toHaveBeenCalledWith(0, 0.05)
  })
  await user.click(screen.getByRole('button', { name: 'Pausar áudio' }))
  await user.click(screen.getByRole('button', { name: 'Reiniciar áudio' }))
  await waitFor(() => {
    expect(audio.outputs.at(-1)?.start).toHaveBeenCalledWith(0, 0)
  })
  expect(reads).toBe(1)
  expect(document.querySelector('audio,source')).toBeNull()
  unmount()
  expect(audio.outputs.at(-1)?.stop).toHaveBeenCalledOnce()
  expect(audio.value.close).toHaveBeenCalledOnce()
})

it('navegador sem áudio falha com aviso claro e nenhum src alternativo', async () => {
  mount(undefined, true)
  const user = userEvent.setup()
  await user.click(screen.getByRole('button', { name: 'Reproduzir áudio' }))
  expect(await screen.findByRole('status')).toHaveTextContent('Você pode ler a transcrição.')
  expect(document.querySelector('audio,source')).toBeNull()
})
