import { z } from 'zod'

// A CSP da borda não tem 'unsafe-eval' e exige Trusted Types (infra/caddy/Caddyfile). Sem jitless, o Zod 4 tenta
// `new Function('')` para decidir se compila validadores, e o navegador registra uma violação em toda página, mesmo
// com o erro engolido. Precisa rodar antes de qualquer validação: é o primeiro import do main.tsx.
z.config({ jitless: true })
