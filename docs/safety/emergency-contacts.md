# Official emergency contacts — issue #9

Verified against primary public sources on **2026-10-25**:

| Country       | Contact | Classification                                                                 | Primary source                                                                                                                                   |
| ------------- | ------- | ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| Brazil        | 190     | Military Police emergency                                                      | [Ministry of Women — Ligue 180](https://www.gov.br/mulheres/pt-br/ligue180) explicitly directs emergencies to 190.                               |
| Brazil        | 192     | SAMU medical emergency                                                         | [Ministry of Health — SAMU 192](https://www.gov.br/saude/pt-br/composicao/saes/samu-192) identifies the free national emergency medical service. |
| Brazil        | 180     | Information and reporting violence against women; **not an emergency service** | [Ministry of Women — Ligue 180](https://www.gov.br/mulheres/pt-br/ligue180).                                                                     |
| United States | 911     | Police, fire or ambulance emergency                                            | [National 911 Program — Calling 911](https://www.911.gov/calling-911/).                                                                          |

The public layout shows both country groups in PT-BR and en-US without expanding a control or signing in.
The Safety header link reaches the visible safety area. Language changes only the copy; it never chooses a
country or a telephone number. No IP, geolocation, profile country, emergency-default country, automatic
dialling, account request or telemetry is introduced. Telephone links require the person's explicit action.
People elsewhere are directed to their local emergency services without inventing a number.

This is the emergency-content contribution to issue #9, not acceptance of the whole Safety module,
legal documents, reports, blocking, quick exit, or the physical assistive-technology matrix. The existing
quick-exit contract (spec §7.3), security invariants, API contracts and database are unchanged. Presentation
follows spec §13.4/13.6 and ADR-0022/0026. No personal data or new observability event is collected.

Regression tests mount the real public layout without a session provider and exercise both languages,
an unsupported browser language without country context, changing language without changing numbers,
the non-emergency label for 180 and keyboard reachability. These checks do not establish physical NVDA,
VoiceOver, telephone connection, emergency response, deployment or country coverage beyond BR/US.

Rollback is a source revert of this UI-only change. It does not alter stored data or require a migration.
Revalidate the linked official sources before changing numbers or adding a country; never infer a
country from language or IP. The legal/authentication files and route registry remain owned by the
separate legal-document workstream.
