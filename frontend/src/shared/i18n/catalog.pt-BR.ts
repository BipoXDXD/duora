/**
 * Catálogo de origem: todos os textos da interface em PT-BR, em formato ICU MessageFormat
 * (spec 13.6). As chaves e os argumentos de cada mensagem saem daqui: outro idioma precisa ter
 * exatamente as mesmas chaves e os mesmos argumentos.
 * Mensagens de erro são neutras: não revelam se um e-mail tem conta (spec 8.2, API-A02 e A06).
 */
export const ptBR = {
  'app.name': 'Duora',
  'app.documentTitle': '{page} · Duora',
  'app.skipToContent': 'Pular para o conteúdo',
  'app.footer': 'Duora · versão em desenvolvimento',
  'app.languageLabel': 'Idioma',

  'common.networkError': 'Não foi possível falar com o Duora. Confira sua conexão e tente de novo.',
  'common.unexpectedError': 'Algo deu errado do nosso lado. Tente de novo em instantes.',
  // unit: seconds | minutes | unknown (sem Retry-After). Ver retry-wait.ts.
  'common.rateLimited':
    'Muitas tentativas seguidas. Por segurança, espere {unit, select, seconds {{count, plural, one {# segundo} other {# segundos}}} minutes {{count, plural, one {# minuto} other {# minutos}}} other {alguns minutos}} antes de tentar de novo.',
  'common.retry': 'Tentar de novo',
  'common.backHome': 'Voltar ao início',
  'common.errorTitle': 'Não deu certo',
  'common.noticeTitle': 'Aviso',
  'common.successTitle': 'Pronto',

  'field.showPassword': 'Mostrar',
  // Nome acessível contém o texto visível (WCAG 2.5.3) e diz o que é mostrado.
  'field.showPasswordName': 'Mostrar senha',

  'validation.emailRequired': 'Informe seu e-mail.',
  'validation.emailInvalid': 'Informe um e-mail no formato nome@exemplo.com.',
  'validation.emailTooLong': 'O e-mail pode ter no máximo {max} caracteres.',
  'validation.passwordRequired': 'Informe sua senha.',
  'validation.passwordTooShort': 'A senha precisa ter pelo menos {min} caracteres.',
  'validation.passwordTooLong': 'A senha pode ter no máximo {max} caracteres.',
  'validation.termsRequired': 'Para criar a conta, aceite os Termos de uso.',
  'validation.privacyRequired': 'Para criar a conta, confirme que leu a Política de privacidade.',
  'validation.invalidValue': 'Confira o valor deste campo.',

  'navigation.pageLoaded': 'Página carregada: {title}',

  'notFound.title': 'Página não encontrada',
  'notFound.body': 'O endereço não corresponde a nenhuma página do Duora.',

  'errorBoundary.title': 'Esta parte do Duora parou de funcionar',
  'errorBoundary.description': 'Seus dados não foram afetados. Tente carregar de novo ou volte ao início.',

  'landing.intro':
    'Encontros entre duas pessoas adultas, em eventos com rodadas e experiências cooperativas. O interesse é privado e a conversa só continua quando ele é mútuo.',
  'landing.register': 'Criar conta',
  'landing.signIn': 'Entrar',

  'register.title': 'Criar conta',
  'register.intro': 'Use um e-mail que só você acessa. Enviaremos um link para confirmar o endereço.',
  'register.email': 'E-mail',
  'register.password': 'Senha',
  'register.passwordHint': 'Entre {min} e {max} caracteres. Uma frase longa é mais fácil de lembrar.',
  'register.legalLegend': 'Termos e privacidade',
  'register.acceptTerms': 'Li e aceito os Termos de uso (versão {version}).',
  'register.acceptPrivacy': 'Li a Política de privacidade (versão {version}).',
  'register.submit': 'Criar conta',
  'register.submitting': 'Criando conta…',
  'register.haveAccount': 'Já tem conta?',
  'register.signInLink': 'Entrar',

  'checkEmail.title': 'Verifique seu e-mail',
  'checkEmail.body':
    'Se for possível criar a conta com esse endereço, você vai receber um e-mail com um link de confirmação.',
  'checkEmail.hint': 'Não chegou? Confira a caixa de spam e as abas de promoções. O link vale por tempo limitado.',
  'checkEmail.signInLink': 'Ir para a entrada',

  'confirmEmail.title': 'Confirmar e-mail',
  'confirmEmail.intro': 'Para concluir a confirmação do seu endereço, informe sua senha e use o botão abaixo.',
  'confirmEmail.password': 'Senha',
  'confirmEmail.passwordHint': 'Use a mesma senha que você criou no cadastro.',
  'confirmEmail.submit': 'Confirmar e-mail',
  'confirmEmail.submitting': 'Confirmando…',
  'confirmEmail.success': 'E-mail confirmado. Agora você já pode entrar.',
  'confirmEmail.invalidLink': 'Este link de confirmação é inválido ou expirou.',
  // Neutra de propósito: não diz se o problema foi o link ou a senha (contrato do M1).
  'confirmEmail.invalidToken': 'Link inválido ou senha incorreta. Confira a senha ou faça o cadastro de novo.',
  'confirmEmail.registerAgain': 'Criar conta de novo',
  'confirmEmail.signInLink': 'Entrar',

  'login.title': 'Entrar',
  'login.email': 'E-mail',
  'login.password': 'Senha',
  'login.submit': 'Entrar',
  'login.submitting': 'Entrando…',
  'login.invalidCredentials': 'E-mail ou senha incorretos.',
  'login.mfaRequired':
    'Esta conta exige um segundo fator de autenticação, e essa etapa ainda não está disponível por aqui. Fale com o suporte.',
  'login.sessionExpired': 'Sua sessão expirou. Entre de novo para continuar.',
  'login.signInRequired': 'Entre para continuar.',
  'login.signedOut': 'Você saiu da sua conta.',
  'login.otherPaths': 'Outras opções',
  'login.forgotPasswordLink': 'Esqueci minha senha',
  'login.noAccount': 'Ainda não tem conta?',
  'login.registerLink': 'Criar conta',

  'forgotPassword.title': 'Recuperar senha',
  'forgotPassword.intro':
    'Informe o e-mail da conta. Se ele estiver cadastrado, enviaremos um link para criar uma senha nova.',
  'forgotPassword.email': 'E-mail',
  'forgotPassword.submit': 'Enviar link',
  'forgotPassword.submitting': 'Enviando…',
  'forgotPassword.sent':
    'Se houver uma conta com esse e-mail, o link chega em alguns minutos. Confira também a caixa de spam.',
  'forgotPassword.signInLink': 'Voltar para a entrada',

  'resetPassword.title': 'Criar senha nova',
  'resetPassword.newPassword': 'Senha nova',
  'resetPassword.submit': 'Salvar senha nova',
  'resetPassword.submitting': 'Salvando…',
  'resetPassword.success': 'Senha alterada. Entre com a senha nova.',
  'resetPassword.invalidLink': 'Este link de recuperação é inválido ou expirou.',
  'resetPassword.invalidToken': 'Este link de recuperação é inválido, expirou ou já foi usado. Peça um link novo.',
  'resetPassword.requestNewLink': 'Pedir um link novo',
  'resetPassword.signInLink': 'Entrar',

  'account.title': 'Sua conta',
  'account.loading': 'Carregando sua conta…',
  'account.loadError': 'Não foi possível carregar sua conta.',
  'account.email': 'E-mail',
  'account.status': 'Situação',
  'account.emailPending': 'Confirme seu e-mail para continuar. Enviamos um link para o endereço cadastrado.',
  'account.signOut': 'Sair',
  'account.signingOut': 'Saindo…',
  'account.statusLabel.PendingEmail': 'Aguardando confirmação do e-mail',
  'account.statusLabel.PendingEligibility': 'Aguardando verificação de idade',
  'account.statusLabel.Active': 'Ativa',
  'account.statusLabel.Restricted': 'Restrita',
  'account.statusLabel.Suspended': 'Suspensa',
  'account.statusLabel.DeletionPending': 'Exclusão solicitada',
  'account.statusLabel.Unknown': 'Indisponível no momento',

  'sessions.title': 'Sessões ativas',
  'sessions.intro': 'Aparelhos e navegadores conectados à sua conta. Encerre os que você não reconhece.',
  'sessions.loading': 'Carregando sessões…',
  'sessions.loadError': 'Não foi possível carregar as sessões.',
  'sessions.empty': 'Nenhuma sessão ativa encontrada.',
  'sessions.current': 'Esta sessão',
  'sessions.createdAt': 'Início',
  'sessions.lastSeenAt': 'Último uso',
  'sessions.revoke': 'Encerrar sessão',
  'sessions.revokeCurrent': 'Encerrar esta sessão',
  'sessions.revoking': 'Encerrando…',
  // Nome acessível do botão: a ação seguida da sessão a que ela se aplica.
  'sessions.actionFor': '{action}: {session}',
} as const
