import { render } from '@testing-library/react'
import type { FaceLivenessDetectorCore } from '@aws-amplify/ui-react-liveness'
import type { ComponentProps } from 'react'
import { expect, it, vi } from 'vitest'
import SelfieCapture from './SelfieCapture'

type DetectorProps = ComponentProps<typeof FaceLivenessDetectorCore>
const sdk = vi.hoisted(() => ({ props: null as DetectorProps | null }))

vi.mock('@aws-amplify/ui-react-liveness', () => ({
  FaceLivenessDetectorCore: (props: DetectorProps) => {
    sdk.props = props
    return null
  },
}))
vi.mock('@/shared/i18n/i18n-context', () => ({ useI18n: () => ({ locale: 'pt-BR' }) }))

// Issue #390: binaryPath ou faceModelUrl trocariam a origem fixada em config/liveness-supply-chain.test.ts.
it('a captura não troca a origem padrão do WASM nem do modelo', () => {
  render(
    <SelfieCapture
      session={{
        sessionId: 'synthetic-session',
        region: 'us-east-1',
        credentials: {
          accessKeyId: 'synthetic',
          secretAccessKey: 'synthetic',
          sessionToken: 'synthetic',
          expiration: '2099-01-01T00:00:00Z',
        },
      }}
      onComplete={() => Promise.resolve()}
      onCancel={() => undefined}
    />,
  )

  expect(Object.keys(sdk.props?.config ?? {})).toEqual(['credentialProvider'])
})
