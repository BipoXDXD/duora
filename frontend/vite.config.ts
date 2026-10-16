/// <reference types="vitest/config" />
import { existsSync, readFileSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv, type ProxyOptions, type ServerOptions } from 'vite'
import { readDevServerSettings, type TlsFiles } from './config/dev-server.ts'

// Variáveis privadas do servidor de desenvolvimento; nunca use VITE_* para isso.
const PRIVATE_ENV_PREFIX = 'DUORA_'

// O teste de registro das ilustrações (src/shared/ui/illustration.test.tsx) lê o catálogo e o registro de licenças
// com ?raw; o Vite confere o id com a query, então a permissão precisa ser do diretório, não do arquivo.
const TEST_DIRS_OUTSIDE_ROOT = ['../tools/visual', '../docs/design']

export default defineConfig(({ command, mode, isPreview }) => {
  const isDevServer = command === 'serve' && isPreview !== true && process.env.VITEST === undefined

  return {
    plugins: [react(), tailwindcss()],
    resolve: {
      alias: {
        '@': path.resolve(import.meta.dirname, './src'),
      },
    },
    server: isDevServer
      ? createDevServerOptions(loadEnv(mode, import.meta.dirname, PRIVATE_ENV_PREFIX))
      : {
          fs: {
            allow: [
              import.meta.dirname,
              ...TEST_DIRS_OUTSIDE_ROOT.map((dir) => path.resolve(import.meta.dirname, dir)),
            ],
          },
        },
    test: {
      environment: 'jsdom',
      include: ['src/**/*.test.{ts,tsx}', 'config/**/*.test.ts'],
      setupFiles: ['./src/test/setup.ts'],
      restoreMocks: true,
      // Fuso fixo: datas exibidas no fuso de quem usa (spec 13.6) ficam iguais em qualquer máquina.
      env: { TZ: 'UTC' },
      css: { include: [/index\.css/] },
    },
  }
})

function createDevServerOptions(env: Record<string, string>): ServerOptions {
  const settings = readDevServerSettings(env, os.homedir())
  const certificate = readCertificate(settings.tls)

  // O backend usa o certificado de desenvolvimento do ASP.NET Core, exportado no mesmo PEM.
  // O Node não consulta o Keychain do macOS, então a CA é passada explicitamente em vez de
  // desligar a verificação TLS (secure: false).
  const backendProxy: ProxyOptions = {
    target: { ...settings.backend, host: settings.backend.hostname, ca: certificate.cert },
    secure: true,
  }

  return {
    https: certificate,
    strictPort: true,
    proxy: {
      '/api': backendProxy,
      '/hubs': { ...backendProxy, ws: true },
    },
  }
}

function readCertificate(tls: TlsFiles): { cert: string; key: string } {
  if (!existsSync(tls.certFile) || !existsSync(tls.keyFile)) {
    throw new Error(
      `Certificado HTTPS local não encontrado (${tls.certFile} / ${tls.keyFile}). ` +
        'Rode "pnpm cert:dev" ou defina DUORA_DEV_CERT_FILE e DUORA_DEV_KEY_FILE.',
    )
  }
  return { cert: readFileSync(tls.certFile, 'utf8'), key: readFileSync(tls.keyFile, 'utf8') }
}
