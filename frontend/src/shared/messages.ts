/**
 * Textos da interface em PT-BR, num só lugar para revisão de redação e tradução futura (spec 13.6).
 * Mensagens de erro são neutras: não revelam se um e-mail tem conta (spec 8.2, API-A02 e A06).
 */

const SECONDS_PER_MINUTE = 60
const pluralRules = new Intl.PluralRules('pt-BR')

function pluralize(count: number, singular: string, plural: string): string {
  return `${String(count)} ${pluralRules.select(count) === 'one' ? singular : plural}`
}

/** Espera do Retry-After em texto: segundos abaixo de um minuto, minutos arredondados para cima. */
export function formatWait(seconds: number | null): string {
  if (seconds === null) {
    return 'alguns minutos'
  }
  if (seconds < SECONDS_PER_MINUTE) {
    return pluralize(Math.max(seconds, 1), 'segundo', 'segundos')
  }
  return pluralize(Math.ceil(seconds / SECONDS_PER_MINUTE), 'minuto', 'minutos')
}

export const messages = {
  common: {
    networkError: 'Não foi possível falar com o Duora. Confira sua conexão e tente de novo.',
    unexpectedError: 'Algo deu errado do nosso lado. Tente de novo em instantes.',
    rateLimited: (seconds: number | null) =>
      `Muitas tentativas seguidas. Por segurança, espere ${formatWait(seconds)} antes de tentar de novo.`,
    retry: 'Tentar de novo',
    backHome: 'Voltar ao início',
    errorTitle: 'Não deu certo',
    noticeTitle: 'Aviso',
    successTitle: 'Pronto',
  },
  field: {
    showPassword: 'Mostrar',
    // Nome acessível contém o texto visível (WCAG 2.5.3) e diz o que é mostrado.
    showPasswordName: 'Mostrar senha',
  },
  validation: {
    emailRequired: 'Informe seu e-mail.',
    emailInvalid: 'Informe um e-mail no formato nome@exemplo.com.',
    emailTooLong: 'O e-mail pode ter no máximo 254 caracteres.',
    passwordRequired: 'Informe sua senha.',
    passwordTooShort: (min: number) => `A senha precisa ter pelo menos ${String(min)} caracteres.`,
    passwordTooLong: (max: number) => `A senha pode ter no máximo ${String(max)} caracteres.`,
    termsRequired: 'Para criar a conta, aceite os Termos de uso.',
    privacyRequired: 'Para criar a conta, confirme que leu a Política de privacidade.',
  },
  navigation: {
    pageLoaded: (title: string) => `Página carregada: ${title}`,
  },
  errorBoundary: {
    title: 'Esta parte do Duora parou de funcionar',
    description: 'Seus dados não foram afetados. Tente carregar de novo ou volte ao início.',
  },
  register: {
    title: 'Criar conta',
    intro: 'Use um e-mail que só você acessa. Enviaremos um link para confirmar o endereço.',
    email: 'E-mail',
    password: 'Senha',
    passwordHint: (min: number, max: number) =>
      `Entre ${String(min)} e ${String(max)} caracteres. Uma frase longa é mais fácil de lembrar.`,
    acceptTerms: (version: string) => `Li e aceito os Termos de uso (versão ${version}).`,
    acceptPrivacy: (version: string) => `Li a Política de privacidade (versão ${version}).`,
    submit: 'Criar conta',
    submitting: 'Criando conta…',
    haveAccount: 'Já tem conta?',
    signInLink: 'Entrar',
  },
  checkEmail: {
    title: 'Verifique seu e-mail',
    body: 'Se for possível criar a conta com esse endereço, você vai receber um e-mail com um link de confirmação.',
    hint: 'Não chegou? Confira a caixa de spam e as abas de promoções. O link vale por tempo limitado.',
    signInLink: 'Ir para a entrada',
  },
  confirmEmail: {
    title: 'Confirmar e-mail',
    intro: 'Para concluir a confirmação do seu endereço, use o botão abaixo.',
    submit: 'Confirmar e-mail',
    submitting: 'Confirmando…',
    success: 'E-mail confirmado. Agora você já pode entrar.',
    invalidLink: 'Este link de confirmação é inválido ou expirou.',
    invalidToken: 'Este link de confirmação é inválido, expirou ou já foi usado.',
    signInLink: 'Entrar',
  },
  login: {
    title: 'Entrar',
    email: 'E-mail',
    password: 'Senha',
    submit: 'Entrar',
    submitting: 'Entrando…',
    invalidCredentials: 'E-mail ou senha incorretos.',
    mfaRequired:
      'Esta conta exige um segundo fator de autenticação, e essa etapa ainda não está disponível por aqui. Fale com o suporte.',
    sessionExpired: 'Sua sessão expirou. Entre de novo para continuar.',
    signInRequired: 'Entre para continuar.',
    signedOut: 'Você saiu da sua conta.',
    forgotPasswordLink: 'Esqueci minha senha',
    noAccount: 'Ainda não tem conta?',
    registerLink: 'Criar conta',
  },
  forgotPassword: {
    title: 'Recuperar senha',
    intro: 'Informe o e-mail da conta. Se ele estiver cadastrado, enviaremos um link para criar uma senha nova.',
    email: 'E-mail',
    submit: 'Enviar link',
    submitting: 'Enviando…',
    sent: 'Se houver uma conta com esse e-mail, o link chega em alguns minutos. Confira também a caixa de spam.',
    signInLink: 'Voltar para a entrada',
  },
  resetPassword: {
    title: 'Criar senha nova',
    newPassword: 'Senha nova',
    submit: 'Salvar senha nova',
    submitting: 'Salvando…',
    success: 'Senha alterada. Entre com a senha nova.',
    invalidLink: 'Este link de recuperação é inválido ou expirou.',
    invalidToken: 'Este link de recuperação é inválido, expirou ou já foi usado. Peça um link novo.',
    requestNewLink: 'Pedir um link novo',
    signInLink: 'Entrar',
  },
  account: {
    title: 'Sua conta',
    loading: 'Carregando sua conta…',
    loadError: 'Não foi possível carregar sua conta.',
    email: 'E-mail',
    status: 'Situação',
    emailPending: 'Confirme seu e-mail para continuar. Enviamos um link para o endereço cadastrado.',
    signOut: 'Sair',
    signingOut: 'Saindo…',
    signOutError: 'Não foi possível sair agora. Confira sua conexão e tente de novo.',
    statusLabels: {
      PendingEmail: 'Aguardando confirmação do e-mail',
      PendingEligibility: 'Aguardando verificação de idade',
      Active: 'Ativa',
      Restricted: 'Restrita',
      Suspended: 'Suspensa',
      DeletionPending: 'Exclusão solicitada',
      Unknown: 'Indisponível no momento',
    },
  },
  sessions: {
    title: 'Sessões ativas',
    intro: 'Aparelhos e navegadores conectados à sua conta. Encerre os que você não reconhece.',
    loading: 'Carregando sessões…',
    loadError: 'Não foi possível carregar as sessões.',
    empty: 'Nenhuma sessão ativa encontrada.',
    current: 'Esta sessão',
    createdAt: 'Início',
    lastSeenAt: 'Último uso',
    revoke: 'Encerrar sessão',
    revokeCurrent: 'Encerrar esta sessão',
    revoking: 'Encerrando…',
    revokeError: 'Não foi possível encerrar a sessão. Tente de novo.',
  },
} as const
