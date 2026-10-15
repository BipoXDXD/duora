import { Outlet, Route, Routes, useLocation } from 'react-router'
import { AccountPage } from '@/features/account/AccountPage'
import { CheckEmailPage } from '@/features/auth/CheckEmailPage'
import { ConfirmEmailPage } from '@/features/auth/ConfirmEmailPage'
import { ForgotPasswordPage } from '@/features/auth/ForgotPasswordPage'
import { LoginPage } from '@/features/auth/LoginPage'
import { RegisterPage } from '@/features/auth/RegisterPage'
import { ResetPasswordPage } from '@/features/auth/ResetPasswordPage'
import { LandingPage } from '@/features/landing/LandingPage'
import { RequireSession } from '@/features/session/RequireSession'
import { paths } from '@/shared/paths'
import { AreaErrorBoundary } from '@/shared/ui/error-boundary'
import { AppLayout } from './AppLayout'
import { NotFoundPage } from './NotFoundPage'

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
            <Route path={paths.account} element={<AccountPage />} />
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
