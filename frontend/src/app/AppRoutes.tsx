import { Route, Routes } from 'react-router'
import { LandingPage } from '@/features/landing/LandingPage'
import { AppLayout } from './AppLayout'
import { NotFoundPage } from './NotFoundPage'

export function AppRoutes(): React.JSX.Element {
  return (
    <Routes>
      <Route element={<AppLayout />}>
        <Route index element={<LandingPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  )
}
