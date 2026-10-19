import { Outlet, Route, Routes, useLocation } from 'react-router'
import { LandingPage } from '@/features/landing/LandingPage'
import { paths } from '@/shared/paths'
import { AreaErrorBoundary } from '@/shared/ui/error-boundary'
import { AppLayout } from './AppLayout'
import { lazyPage, type LazyPage } from './lazy-page'
import { NotFoundPage } from './NotFoundPage'
import { withFallbackMinimum } from './route-fallback-timing'

// A página inicial e a de rota inexistente ficam no bundle principal: são a primeira tela e a resposta a
// qualquer link quebrado. As demais telas vêm em chunks próprios (spec 13.5).
const RegisterPage = routePage(async () => (await import('@/features/auth/RegisterPage')).RegisterPage)
const CheckEmailPage = routePage(async () => (await import('@/features/auth/CheckEmailPage')).CheckEmailPage)
const ConfirmEmailPage = routePage(async () => (await import('@/features/auth/ConfirmEmailPage')).ConfirmEmailPage)
const LoginPage = routePage(async () => (await import('@/features/auth/LoginPage')).LoginPage)
const ForgotPasswordPage = routePage(
  async () => (await import('@/features/auth/ForgotPasswordPage')).ForgotPasswordPage,
)
const ResetPasswordPage = routePage(async () => (await import('@/features/auth/ResetPasswordPage')).ResetPasswordPage)
const ChangePasswordPage = routePage(
  async () => (await import('@/features/auth/ChangePasswordPage')).ChangePasswordPage,
)
const MfaEnrollmentPage = routePage(async () => (await import('@/features/auth/MfaEnrollmentPage')).MfaEnrollmentPage)
const SelfieVerificationPage = routePage(
  async () => (await import('@/features/account/SelfieVerificationPage')).SelfieVerificationPage,
)
const AccountPage = routePage(async () => (await import('@/features/account/AccountPage')).AccountPage)
const ProfilePage = routePage(async () => (await import('@/features/profile/ProfilePage')).ProfilePage)
const PreferencesPage = routePage(async () => (await import('@/features/preferences/PreferencesPage')).PreferencesPage)
const EventsPage = routePage(async () => (await import('@/features/participation/EventsPage')).EventsPage)
const RoomPage = routePage(async () => (await import('@/features/participation/RoomPage')).RoomPage)
// O guarda espera os chunks das rotas protegidas: assim, quando GET /me responde, a página entra sem um segundo
// fallback. Uma rota protegida nova entra aqui também, senão ela mostra o fallback de novo depois do
// "Carregando sua conta…".
const RequireSession = routePage(async () => {
  const [{ RequireSession: guard }] = await Promise.all([
    import('@/features/session/RequireSession'),
    AccountPage.preload(),
    ChangePasswordPage.preload(),
    MfaEnrollmentPage.preload(),
    ProfilePage.preload(),
    PreferencesPage.preload(),
    EventsPage.preload(),
    RoomPage.preload(),
  ])
  return guard
})

export function AppRoutes(): React.JSX.Element {
  return (
    <Routes>
      <Route element={<AppLayout />}>
        <Route index element={<LandingPage />} />
        <Route element={<Area />}>
          <Route path={paths.register} element={<RegisterPage />} />
          <Route path={paths.checkEmail} element={<CheckEmailPage />} />
          <Route path={paths.confirmEmail} element={<ConfirmEmailPage />} />
          <Route path={paths.login} element={<LoginPage />} />
          <Route path={paths.forgotPassword} element={<ForgotPasswordPage />} />
          <Route path={paths.resetPassword} element={<ResetPasswordPage />} />
        </Route>
        <Route element={<Area />}>
          <Route element={<RequireSession />}>
            <Route path={paths.changePassword} element={<ChangePasswordPage />} />
            <Route path={paths.mfaEnrollment} element={<MfaEnrollmentPage />} />
            <Route path={paths.selfie} element={<SelfieVerificationPage />} />
            <Route path={paths.account} element={<AccountPage />} />
            <Route path={paths.profile} element={<ProfilePage />} />
            <Route path={paths.preferences} element={<PreferencesPage />} />
            <Route path={paths.events} element={<EventsPage />} />
            <Route path="/rooms/:roomId" element={<RoomPage />} />
          </Route>
        </Route>
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  )
}

/** Uma área com error boundary próprio; trocar de rota remonta a área depois de uma falha. */
function Area(): React.JSX.Element {
  const { pathname } = useLocation()
  return (
    <AreaErrorBoundary key={pathname}>
      <Outlet />
    </AreaErrorBoundary>
  )
}

/** Tela da rota sob demanda; se o aviso de carregamento chegou a aparecer, ele cumpre o tempo mínimo. */
function routePage(load: () => Promise<React.ComponentType>): LazyPage {
  return lazyPage(() => withFallbackMinimum(load))
}
