/**
 * Caminhos das rotas da SPA. Os de confirmação e redefinição são contrato com os links enviados
 * por e-mail pelo backend (`/confirm-email#userId=…&token=…` e `/reset-password#userId=…&token=…`,
 * com os parâmetros no fragmento): mudar aqui exige mudar lá.
 */
export const paths = {
  selfie: '/account/selfie',
  home: '/',
  register: '/register',
  terms: '/terms',
  privacy: '/privacy',
  checkEmail: '/check-email',
  confirmEmail: '/confirm-email',
  login: '/login',
  forgotPassword: '/forgot-password',
  resetPassword: '/reset-password',
  changePassword: '/change-initial-password',
  mfaEnrollment: '/account/enable-mfa',
  account: '/account',
  photos: '/account/photos',
  profile: '/account/profile',
  preferences: '/account/preferences',
  termsReacceptance: '/account/terms',
  accountSuspended: '/account/suspended',
  events: '/events',
  photoReviewHistory: '/photos/:photoId/review',
  photoReviews: '/admin/photo-reviews',
  photoReviewDetail: '/admin/photo-reviews/:caseId',
} as const

/** Links já emitidos: aliases exatos, sem uma segunda árvore de páginas por idioma. */
export const legacyPaths: Readonly<Record<string, string>> = {
  '/conta/verificacao-etaria': paths.selfie,
  '/conta/preferencias': paths.preferences,
  '/conta/ativar-mfa': paths.mfaEnrollment,
  '/conta/perfil': paths.profile,
  '/conta/selfie': paths.selfie,
  '/trocar-senha-inicial': paths.changePassword,
  '/verifique-seu-email': paths.checkEmail,
  '/confirmar-email': paths.confirmEmail,
  '/redefinir-senha': paths.resetPassword,
  '/esqueci-a-senha': paths.forgotPassword,
  '/criar-conta': paths.register,
  '/recuperar-senha': paths.forgotPassword,
  '/cadastro': paths.register,
  '/entrar': paths.login,
  '/conta': paths.account,
}
