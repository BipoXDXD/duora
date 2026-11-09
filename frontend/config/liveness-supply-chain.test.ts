// @vitest-environment node
import { readFileSync, readdirSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { describe, expect, it } from 'vitest'
import { z } from 'zod'

// Issue #390 / TM-99. O WASM e o modelo do liveness vêm do CDN da AWS sem verificação de integridade, então o que
// chega ao navegador depende destes pinos. Falhar aqui é o objetivo: subir o pacote ou trocar a origem exige rever
// docs/security/liveness-supply-chain.md e só então atualizar os valores. O config da captura está em
// src/features/account/SelfieCapture.test.tsx.
const PACKAGE_VERSION = '3.7.0'
const TFJS_WASM_VERSION = '4.11.0'
const BLAZEFACE_VERSION = '1.0.2'
const CDN_ORIGIN = 'https://cdn.liveness.rekognition.amazonaws.com'
const STREAMING_ORIGIN = 'wss://streaming-rekognition.us-east-1.amazonaws.com'
const WASM_BASE_URL = `${CDN_ORIGIN}/face-detection/tensorflow/tfjs-backend-wasm/${TFJS_WASM_VERSION}/`
const MODEL_URL = `${CDN_ORIGIN}/face-detection/tensorflow-models/blazeface/${BLAZEFACE_VERSION}/model/model.json`
const WASM_BINARIES = ['tfjs-backend-wasm-simd.wasm', 'tfjs-backend-wasm-threaded-simd.wasm', 'tfjs-backend-wasm.wasm']
const PACKAGE_INTEGRITY =
  'sha512-dIxvhFGowfhsW0YMVntzhx624W2kSvfw2mVt8ZAr14x/MM8qyD8iOw5vRH3dRmgnuVqUG7H9/Q6et+pV1xnd4w=='
const TFJS_WASM_INTEGRITY =
  'sha512-ubv+5cXux8al/ccZvmDpuw5PNm8FAj57GHzC0nDkX6lHJ0iXNklzUPK5SoTYnU7eYwXXzv8synjnqiqnWvfDEA=='

const frontendRoot = join(import.meta.dirname, '..')
const requireFromFrontend = createRequire(join(frontendRoot, 'package.json'))
const installedPackageRoot = dirname(dirname(requireFromFrontend.resolve('@aws-amplify/ui-react-liveness')))
const requireFromPackage = createRequire(join(installedPackageRoot, 'package.json'))
const installedWasmRoot = dirname(requireFromPackage.resolve('@tensorflow/tfjs-backend-wasm/package.json'))

const manifest = z.object({ version: z.string(), dependencies: z.record(z.string(), z.string()) })
const detectorModule = z.object({
  BLAZEFACE_VERSION: z.string(),
  DEFAULT_BLAZEFACE_URL: z.string(),
  DEFAULT_TFJS_WASM_URL: z.string(),
  BlazeFaceFaceDetection: z.custom<new () => { binaryPath: string; faceModelUrl: string }>(
    (value) => typeof value === 'function',
  ),
})

function readManifest(path: string): z.infer<typeof manifest> {
  return manifest.parse(JSON.parse(readFileSync(path, 'utf8')))
}

// O módulo fica fora do "exports" do pacote; o caminho é o do build ESM, que é o que o Vite empacota.
async function importInstalledDetector(): Promise<z.infer<typeof detectorModule>> {
  const file = join(
    installedPackageRoot,
    'dist/esm/components/FaceLivenessDetector/service/utils/blazefaceFaceDetection.mjs',
  )
  return detectorModule.parse(await import(/* @vite-ignore */ pathToFileURL(file).href))
}

function selfieConnectSources(): string[] {
  const caddy = readFileSync(join(frontendRoot, '../infra/caddy/Caddyfile'), 'utf8')
  const policy = /^\s*Content-Security-Policy "([^"]*'wasm-unsafe-eval'[^"]*)"$/m.exec(caddy)?.[1]
  const directive = policy?.split('; ').find((entry) => entry.startsWith('connect-src '))
  if (directive === undefined) throw new Error('connect-src da selfie ausente do Caddyfile')
  return directive.split(' ').slice(1)
}

describe('cadeia de suprimentos do liveness da selfie', () => {
  it('declara a versão exata do pacote, sem faixa', () => {
    const declared = readManifest(join(frontendRoot, 'package.json'))

    expect(declared.dependencies['@aws-amplify/ui-react-liveness']).toBe(PACKAGE_VERSION)
  })

  it('trava no lockfile a versão e a integridade do pacote e do WASM', () => {
    const lockfile = readFileSync(join(frontendRoot, 'pnpm-lock.yaml'), 'utf8')

    expect(lockfile).toContain(
      `'@aws-amplify/ui-react-liveness':\n        specifier: ${PACKAGE_VERSION}\n        version: ${PACKAGE_VERSION}(`,
    )
    expect(lockfile).toContain(
      `'@aws-amplify/ui-react-liveness@${PACKAGE_VERSION}':\n    resolution: {integrity: ${PACKAGE_INTEGRITY}}`,
    )
    expect(lockfile).toContain(
      `'@tensorflow/tfjs-backend-wasm@${TFJS_WASM_VERSION}':\n    resolution: {integrity: ${TFJS_WASM_INTEGRITY}}`,
    )
  })

  it('instala a versão fixada do pacote e do backend WASM, com os três binários conhecidos', () => {
    expect(readManifest(join(installedPackageRoot, 'package.json')).version).toBe(PACKAGE_VERSION)
    expect(readFileSync(join(installedPackageRoot, 'dist/esm/version.mjs'), 'utf8')).toContain(
      `VERSION = '${PACKAGE_VERSION}'`,
    )
    expect(readManifest(join(installedWasmRoot, 'package.json')).version).toBe(TFJS_WASM_VERSION)
    const installedBinaries = readdirSync(join(installedWasmRoot, 'dist')).filter((name) => name.endsWith('.wasm'))
    expect(installedBinaries.sort()).toEqual(WASM_BINARIES)
  })

  it('o detector instalado carrega o WASM e o modelo exatamente das URLs fixadas', async () => {
    const detector = await importInstalledDetector()
    const instance = new detector.BlazeFaceFaceDetection()

    expect(detector.BLAZEFACE_VERSION).toBe(BLAZEFACE_VERSION)
    expect(detector.DEFAULT_TFJS_WASM_URL).toBe(WASM_BASE_URL)
    expect(detector.DEFAULT_BLAZEFACE_URL).toBe(MODEL_URL)
    expect(instance.binaryPath).toBe(WASM_BASE_URL)
    expect(instance.faceModelUrl).toBe(MODEL_URL)
  })

  // Só confere as origens com host explícito; a política inteira é comparada em test_caddy_security_headers.py.
  it('as origens https e wss explícitas da CSP da selfie são o CDN e o streaming do liveness', () => {
    const externalSources = selfieConnectSources().filter((source) => /^(https|wss):\/\/[^{]+$/.test(source))

    expect(externalSources).toEqual([CDN_ORIGIN, STREAMING_ORIGIN])
    expect(new URL(WASM_BASE_URL).origin).toBe(CDN_ORIGIN)
    expect(new URL(MODEL_URL).origin).toBe(CDN_ORIGIN)
  })
})
