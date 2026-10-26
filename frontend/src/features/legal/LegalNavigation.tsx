import { useI18n } from '@/shared/i18n/i18n-context'
import { TextLink } from '@/shared/ui/page'
import { paths } from '@/shared/paths'
import { legalCopy } from './legal-copy'
export function LegalNavigation(): React.JSX.Element {
  const { t } = useI18n()
  const copy = legalCopy(t)
  return (
    <nav aria-label={copy.navigation} className="flex flex-wrap gap-4">
      <TextLink to={paths.terms}>{copy.terms}</TextLink>
      <TextLink to={paths.privacy}>{copy.privacy}</TextLink>
    </nav>
  )
}
