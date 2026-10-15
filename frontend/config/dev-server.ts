import path from 'node:path'

/**
 * Configuração do servidor de desenvolvimento lida de variáveis de ambiente privadas.
 * O prefixo DUORA_ não é exposto ao bundle (o Vite só publica VITE_*), então nada aqui
 * chega ao navegador.
 */
export interface DevServerSettings {
  readonly backend: BackendTarget
  readonly tls: TlsFiles
}

export interface BackendTarget {
  readonly protocol: 'https:'
  readonly hostname: string
  readonly port: number
}

export interface TlsFiles {
  readonly certFile: string
  readonly keyFile: string
}

type Environment = Readonly<Record<string, string | undefined>>

const DEFAULT_BACKEND_ORIGIN = 'https://localhost:5001'
const LOCAL_HOSTNAMES: ReadonlySet<string> = new Set(['localhost', '127.0.0.1', '[::1]'])

export function readDevServerSettings(env: Environment, homeDir: string): DevServerSettings {
  const defaultCertBase = path.join(homeDir, '.aspnet', 'https', 'duora-spa')

  return {
    backend: parseBackendOrigin(env.DUORA_API_ORIGIN ?? DEFAULT_BACKEND_ORIGIN),
    tls: {
      certFile: env.DUORA_DEV_CERT_FILE ?? `${defaultCertBase}.pem`,
      keyFile: env.DUORA_DEV_KEY_FILE ?? `${defaultCertBase}.key`,
    },
  }
}

function parseBackendOrigin(raw: string): BackendTarget {
  if (!URL.canParse(raw)) {
    throw new Error(`DUORA_API_ORIGIN não é uma URL válida: "${raw}".`)
  }
  const url = new URL(raw)

  if (url.protocol !== 'https:') {
    throw new Error(`DUORA_API_ORIGIN precisa usar HTTPS (recebido "${raw}").`)
  }
  if (!LOCAL_HOSTNAMES.has(url.hostname)) {
    throw new Error(`DUORA_API_ORIGIN precisa apontar para um backend local (recebido "${raw}").`)
  }
  if (url.port === '') {
    throw new Error(`DUORA_API_ORIGIN precisa declarar a porta explicitamente (recebido "${raw}").`)
  }
  if (url.pathname !== '/' || url.search !== '' || url.hash !== '') {
    throw new Error(`DUORA_API_ORIGIN deve ser só a origem, sem caminho (recebido "${raw}").`)
  }

  return { protocol: 'https:', hostname: url.hostname, port: Number(url.port) }
}
