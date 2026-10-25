import { createSessionContextStore } from './session-context'

/** Uma capacidade por documento; remontar a árvore React não libera uma proteção existente. */
export const pageSessionContext = createSessionContextStore()
