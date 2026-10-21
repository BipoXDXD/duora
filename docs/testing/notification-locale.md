# Idioma de notificações sem requisição — issue #102

## Produtores e contrato

O produtor existente sem requisição HTTP é `AdministratorBootstrap` em `admin grant --reissue`:
`PasswordResetNotice` captura `identity.users.preferred_locale` no mesmo `UPDATE ... RETURNING`
que reemite a senha, dentro da transação do efeito, da auditoria e da outbox (TM-21).
`pt-BR` e `en-US` são gravados canonicamente; conta legada sem preferência grava `en-US`.
Não usar `ParseOrDefault(null)` para a conta: esse método é do leitor de payload antigo e preserva pt-BR.

Uma atualização concorrente da preferência e a reemissão se serializam na linha da conta.
A reemissão captura o valor confirmado que seu UPDATE encontra; alterações posteriores não
reescrevem eventos pendentes. O dispatcher entrega o snapshot, sem consultar a conta a cada retry.
O aviso continua bilíngue por segurança, conforme TM-21; esta mudança não altera texto, destinatário,
credenciais, deadline, idempotência, política de MFA ou autorização do comando.

`AccountSecurityService` produz avisos ligados à requisição HTTP e mantém seu contrato atual.
Cadastro, confirmação e pedidos de reset também têm contexto de requisição. `TransactionalEmailDispatcher`
é consumidor, não produtor de novos eventos. Lembretes de eventos e avisos agendados de participação
não têm produtor de e-mail implementado nesta fatia. Quando existirem, devem obter o locale da conta
na transação de criação, resolver com `EmailLanguageTag.Match(locale) ?? EmailLanguageTag.Default`
e fixar a tag no payload; não inferir região, fuso ou idiomas de comunicação.

## Compatibilidade e verificação

Nenhum campo, versão de schema ou migração foi acrescentado. O leitor existente continua aceitando
`en`, `en-US`, `pt-BR`, pt-PT por idioma primário e payload antigo sem tag (pt-BR histórico).
Tags não suportadas usam en-US. Rollback de binários não exige reescrever nem drenar esses payloads.

- `AdminBootstrapTests.ReissueSnapshotsTheStoredLocaleWithoutAnHttpRequest`: PT-BR, EN-US e conta
  legada; mudança posterior não altera o payload. O caso PT-BR reproduziu a falha antes do patch.
- `ReissueWaitsForAConcurrentLocaleChangeAndCapturesTheCommittedValue`: bloqueio observado via
  `pg_blocking_pids` em PostgreSQL real, seguido de commit da preferência e captura correta.
- `TransactionalEmailDispatchTests.SecurityNoticeRetryPreservesTheCapturedLocale`: falha de envio,
  payload/locale intactos e entrega após backoff para ambos os idiomas.
- `EmailLanguageTagTests`, testes de leitura da outbox, bootstrap e rollback TM-21 preservam a
  compatibilidade e atomicidade existentes.

Somente dados sintéticos e PostgreSQL descartável. Não houve envio externo, implantação ou ativação.
