import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { Page, TextLink } from '@/shared/ui/page'

/** Tela neutra depois do cadastro: não confirma nem nega que o e-mail tenha conta (API-A02). */
export function CheckEmailPage(): React.JSX.Element {
  return (
    <Page title={messages.checkEmail.title} intro={messages.checkEmail.body}>
      <p className="text-text-muted">{messages.checkEmail.hint}</p>
      <p>
        <TextLink to={paths.login}>{messages.checkEmail.signInLink}</TextLink>
      </p>
    </Page>
  )
}
