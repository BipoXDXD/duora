// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { z } from 'zod'
import mainSource from '@/main.tsx?raw'
import './zod-config'

describe('Zod sem eval', () => {
  // Sem jitless, o Zod 4 testa `new Function('')` na primeira validação de objeto: a CSP da borda (sem
  // 'unsafe-eval', com Trusted Types) bloqueia e registra uma violação em toda página (deploy.md).
  it('liga o modo jitless', () => {
    expect(z.config().jitless).toBe(true)
  })

  it('é o primeiro import do main.tsx, antes de qualquer módulo validar algo', () => {
    const firstImport = mainSource.split('\n').find((line) => line.startsWith('import '))

    expect(firstImport).toBe("import './app/zod-config'")
  })
})
