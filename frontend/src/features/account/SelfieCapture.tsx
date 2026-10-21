import { FaceLivenessDetectorCore } from '@aws-amplify/ui-react-liveness'
import '@aws-amplify/ui-react-liveness/styles.css'
import { useI18n } from '@/shared/i18n/i18n-context'
import type { SelfieCapture as Capture } from './selfie-verification-api'

export default function SelfieCapture({
  session,
  onComplete,
  onCancel,
}: {
  readonly session: Capture
  readonly onComplete: () => Promise<void>
  readonly onCancel: () => void
}): React.JSX.Element {
  const { locale } = useI18n()
  return (
    <FaceLivenessDetectorCore
      sessionId={session.sessionId}
      region={session.region}
      onAnalysisComplete={onComplete}
      onUserCancel={onCancel}
      onError={onCancel}
      config={{
        credentialProvider: () =>
          Promise.resolve({ ...session.credentials, expiration: new Date(session.credentials.expiration) }),
      }}
      displayText={
        locale === 'pt-BR'
          ? {
              startScreenBeginCheckText: 'Iniciar captura',
              cancelLivenessCheckText: 'Cancelar',
              hintCenterFaceText: 'Centralize o rosto',
              hintCenterFaceInstructionText: 'Mantenha o rosto inteiro visível e centralizado.',
              hintMoveFaceFrontOfCameraText: 'Posicione o rosto diante da câmera',
              hintTooManyFacesText: 'Apenas uma pessoa por captura',
              hintFaceDetectedText: 'Rosto encontrado',
              hintCanNotIdentifyText: 'Não foi possível encontrar o rosto',
              hintTooCloseText: 'Afaste-se um pouco',
              hintTooFarText: 'Aproxime-se um pouco',
              hintConnectingText: 'Conectando…',
              hintVerifyingText: 'Verificando presença…',
              hintCheckCompleteText: 'Captura concluída',
              hintIlluminationTooBrightText: 'Reduza a iluminação direta',
              hintIlluminationTooDarkText: 'Procure um local mais iluminado',
              hintIlluminationNormalText: 'Iluminação adequada',
              hintHoldFaceForFreshnessText: 'Mantenha a posição',
              hintFaceOffCenterText: 'Centralize o rosto',
              hintMatchIndicatorText: 'Siga a orientação na tela',
              cameraNotFoundHeadingText: 'Câmera indisponível',
              cameraNotFoundMessageText: 'Conecte uma câmera ou tente outro dispositivo.',
              cameraMinSpecificationsHeadingText: 'Câmera incompatível',
              cameraMinSpecificationsMessageText: 'Tente outro dispositivo compatível.',
              retryCameraPermissionsText: 'Permitir câmera novamente',
              waitingCameraPermissionText: 'Aguardando sua permissão para usar a câmera',
              a11yVideoLabelText: 'Prévia da câmera para confirmação de presença',
              recordingIndicatorText: 'Capturando',
              goodFitCaptionText: 'Posição adequada',
              goodFitAltText: 'Rosto centralizado no enquadramento',
              tooFarCaptionText: 'Muito distante',
              tooFarAltText: 'Rosto distante do enquadramento',
              photosensitivityWarningBodyText:
                'Este procedimento pode exibir luzes coloridas. Cancele se causar desconforto.',
              photosensitivityWarningHeadingText: 'Sensibilidade à luz',
              photosensitivityWarningInfoText: 'Informações sobre sensibilidade à luz',
              photosensitivityWarningLabelText: 'Aviso de sensibilidade à luz',
              errorLabelText: 'Não foi possível concluir',
              tryAgainText: 'Tentar novamente',
              connectionTimeoutHeaderText: 'Conexão interrompida',
              connectionTimeoutMessageText: 'Verifique a conexão e tente novamente.',
              timeoutHeaderText: 'Tempo esgotado',
              timeoutMessageText: 'Inicie uma nova captura.',
              faceDistanceHeaderText: 'Ajuste a posição',
              faceDistanceMessageText: 'Centralize o rosto e siga as orientações.',
              multipleFacesHeaderText: 'Mais de um rosto',
              multipleFacesMessageText: 'Faça a captura sozinho.',
              clientHeaderText: 'Captura indisponível',
              clientMessageText: 'Verifique a permissão de câmera ou tente outro dispositivo.',
              serverHeaderText: 'Serviço indisponível',
              serverMessageText: 'Tente novamente mais tarde.',
              landscapeHeaderText: 'Ajuste a orientação',
              landscapeMessageText: 'Mantenha o dispositivo na orientação indicada.',
              portraitMessageText: 'Mantenha o dispositivo na vertical.',
            }
          : {}
      }
    />
  )
}
