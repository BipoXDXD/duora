import { pageSessionContext } from '@/shared/page-session-context'
import { createSecurityResultStore } from './security-result'

export const pageSecurityResult = createSecurityResultStore(pageSessionContext)
