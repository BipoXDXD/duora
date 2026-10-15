// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { readDevServerSettings } from './dev-server.ts'

const HOME = '/home/dev'

describe('readDevServerSettings', () => {
  it('usa o backend local padrão e o certificado exportado na home quando nada é configurado', () => {
    const settings = readDevServerSettings({}, HOME)

    expect(settings).toEqual({
      backend: { protocol: 'https:', hostname: 'localhost', port: 5001 },
      tls: {
        certFile: '/home/dev/.aspnet/https/duora-spa.pem',
        keyFile: '/home/dev/.aspnet/https/duora-spa.key',
      },
    })
  })

  it('aceita a origem do backend vinda de DUORA_API_ORIGIN', () => {
    const settings = readDevServerSettings({ DUORA_API_ORIGIN: 'https://127.0.0.1:7443' }, HOME)

    expect(settings.backend).toEqual({ protocol: 'https:', hostname: '127.0.0.1', port: 7443 })
  })

  it('aceita caminhos próprios de certificado e chave', () => {
    const settings = readDevServerSettings(
      { DUORA_DEV_CERT_FILE: '/certs/spa.pem', DUORA_DEV_KEY_FILE: '/certs/spa.key' },
      HOME,
    )

    expect(settings.tls).toEqual({ certFile: '/certs/spa.pem', keyFile: '/certs/spa.key' })
  })

  it.each([
    ['http://localhost:5000', 'HTTPS'],
    ['https://api.example.com:443', 'local'],
    ['https://localhost', 'porta'],
    ['https://localhost:5001/api', 'origem'],
    ['não é uma URL', 'URL'],
  ])('rejeita DUORA_API_ORIGIN=%s com mensagem que cita "%s"', (origin, fragment) => {
    expect(() => readDevServerSettings({ DUORA_API_ORIGIN: origin }, HOME)).toThrow(fragment)
  })
})
