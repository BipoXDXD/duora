// Run with: node scripts/compare-time-zones.mjs
// Uses the same installed Playwright browsers as the SPA acceptance suite.
import { chromium, webkit } from '@playwright/test'

function compare() {
  return ['pt-BR', 'en-US'].flatMap((locale) =>
    ['America/New_York', 'America/Sao_Paulo', 'UTC'].flatMap((timeZone) =>
      ['short', 'shortGeneric'].map((timeZoneName) => ({
        locale,
        timeZone,
        timeZoneName,
        output: new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit', timeZone, timeZoneName }).format(
          new Date('2026-10-16T23:00:00Z'),
        ),
      })),
    ),
  )
}
const results = [{ runtime: `Node ${process.version} / ICU ${process.versions.icu}`, rows: compare() }]
for (const type of [chromium, webkit]) {
  const browser = await type.launch()
  try {
    const page = await browser.newPage()
    results.push({ runtime: `${type.name()} ${browser.version()}`, rows: await page.evaluate(compare) })
  } finally {
    await browser.close()
  }
}
console.log(JSON.stringify(results, null, 2))
