/**
 * Caminhos das rotas da SPA. Os de confirmação e redefinição são contrato com os links enviados
 * por e-mail pelo backend (`/confirmar-email#userId=…&token=…` e `/redefinir-senha#userId=…&token=…`,
 * com os parâmetros no fragmento): mudar aqui exige mudar lá.
 */
export const paths = {
  selfie: '/conta/selfie',
  home: '/',
  register: '/criar-conta',
  checkEmail: '/verifique-seu-email',
  confirmEmail: '/confirmar-email',
  login: '/entrar',
  forgotPassword: '/esqueci-a-senha',
  resetPassword: '/redefinir-senha',
  changePassword: '/trocar-senha-inicial',
  mfaEnrollment: '/conta/ativar-mfa',
  account: '/conta',
  profile: '/conta/perfil',
} as const
