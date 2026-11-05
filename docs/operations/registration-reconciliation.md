# Reconciliação de inscrições legadas

Runbook da operação do [ADR-0049](../architecture/adr/0049-execucao-da-reconciliacao-de-inscricoes-legadas.md)
([#321](https://github.com/BipoXDXD/duora/issues/321)). A operação é só por API: prévia
([API-ADM25](../api/registration-reconciliation-preview.md)), run e aplicação
([API-ADM26 a API-ADM29](../api/registration-reconciliation-runs.md)).

**Este documento não autoriza nada.** Publicar uma release com estas rotas não autoriza consultar participantes,
cadastrar corte nem aplicar. Sem `RegistrationReconciliation:Cutover`, nenhuma unidade é candidata e nenhum run tem
unidades: esse é o estado padrão.

## Critérios para uma aplicação real

Todos, antes do primeiro `POST /runs`:

1. **Autorização escrita do responsável**, com ambiente, escopo (quais unidades ou que limite) e janela. O identificador
   dela é o `authorizationReference` do run.
2. **Corte revisado.** `EvidenceId`, `Lower` e `Upper` vêm de evidência de implantação revisada, nunca da data da
   migração nem de um valor escolhido na hora. Cadastrar ou mudar o corte não é só definir uma variável: o
   `compose.prod.yml` declara o `environment` de forma explícita, sem `RegistrationReconciliation__Cutover__*`, e o Compose
   é travado por sha256. Exige mudança revisada do Compose, janela e ressela do baseline, como em
   [deploy.md](deploy.md). Sem isso, a produção segue sem corte e nenhuma unidade é candidata.
3. **Gate da [#320](https://github.com/BipoXDXD/duora/issues/320) (ADR-0049, D3).** Nenhum consumidor de
   `events.waitlisted_registration_promoted` pode estar ativo sem tratar como suprimido todo fato vinculado a uma unidade.
   Hoje o fato não tem consumidor. Se isso mudar, confirme o teste de contrato do gate antes de continuar. **Não aplique
   em ambiente com consumidor que ignore o vínculo.**
4. **Release compatível** (ver abaixo) e backup recuperável confirmado antes da janela.
5. **Um moderador com MFA**, TOTP da última hora e sessão administrativa própria. Não se compartilha sessão.

Os textos `justification` e `authorizationReference` não levam dado de participante: nada de nome, e-mail ou motivo de
moderação. O código não consegue garantir isso; é regra deste runbook.

## Procedimento

1. **Prévia.** `GET .../preview`. Revise as contagens por motivo. `heldRegistrations` e as unidades `blocked` ou
   `oversized` ficam fora: não tente forçá-las.
2. **Run.** `POST .../runs` com até 10 pares e os `unitDigest` vistos, numa `Idempotency-Key` estável. `412` significa que
   algo mudou: refaça a prévia. `409 RUN_ALREADY_OPEN`: há outro run aberto, que precisa ser fechado antes. Guarde o `ETag`.
3. **Uma unidade por vez.** `POST .../runs/{runId}/units/{unitId}:apply`, com `Idempotency-Key` própria por unidade, gerada
   antes do pedido. Guarde o recibo de cada resposta (ver Evidência).
4. **Trate a resposta** pela tabela abaixo. Não siga para a próxima unidade com um desfecho que você não entendeu.
5. **Feche.** `POST .../runs/{runId}:close` com `If-Match` do `ETag`, ao terminar ou ao interromper.

| Resposta | Significado | O que fazer |
|---|---|---|
| `200 Applied` | Retirada e promoções confirmadas num commit | Guardar o recibo e seguir |
| `200 SkippedChanged` | A unidade mudou depois do manifesto; nada foi retirado | Guardar o recibo. Revê-la exige prévia e run novos |
| `200 NeedsReview` | Teto excedido ou reserva ativa; nada foi retirado | Investigar. Revê-la exige run novo |
| `401 REAUTHENTICATION_REQUIRED` | TOTP com mais de uma hora; nada foi gravado | Fazer o step-up e repetir **com a mesma chave** |
| `403` | A autorização caiu; nada foi gravado | Parar. Fazer step-up só se a queda for esperada |
| `409 RUN_CLOSED` | Unidade `Pending` em run fechado | Parar: ela fica `Pending` para sempre neste run |
| `409 CUTOVER_CHANGED` | O corte configurado não é o do run, ou o binário tem outro seletor | Parar. Restaurar a configuração revisada ou fechar o run |
| `409 IDEMPOTENCY_KEY_REUSED` | Esta chave já gravou outra unidade | Usar a chave certa, nunca reaproveitar |
| `503` com `Retry-After: 2` ou `30` | Unidade ocupada, contenção, plano instável ou timeout; nada foi gravado | Repetir **com a mesma chave**, depois do `Retry-After` |
| `503` com `Retry-After: 5` | Banco indisponível. Se a conexão caiu no commit, **o desfecho é desconhecido** | Tratar como resposta incerta (abaixo) |
| `500` | Defeito: efeito divergiu do manifesto, com rollback integral, ou falha inesperada | **Não repetir.** Anotar o `traceId`, conferir o `state` no `GET`, investigar e fechar o run |

Motivos fechados em `reason`: `MeasureChanged`, `RegistrationChanged`, `CeilingExceeded`, `PlanTooLarge`, `ActiveReservation`
e os demais nomes de `LegacyRegistrationIssue`.

Um `503` de deadlock é indistinguível dos outros na resposta, mas sobe no log como erro
`Deadlock ao aplicar uma unidade da reconciliação`. Deadlock é defeito a investigar: pare e abra uma issue, ainda que a
repetição funcione.

## Resposta incerta

Timeout de rede, resposta perdida ou `503` com `Retry-After: 5`: primeiro leia `GET .../runs/{runId}` e confira o `state` da
unidade. **Repetir o `:apply` só é replay em unidade terminal.** Em unidade `Pending` com o run aberto, ele aplica de verdade.

- Terminal: o desfecho é o que está em `receipt`. Não é preciso repetir.
- `Pending`: nada foi aplicado. Repita com a mesma chave só se a aplicação continuar autorizada e na janela.

## Evidência por unidade

Guarde, no registro da autorização, por unidade: `runId`, `unitId`, `state`, `reason`, `factIds`, `executedByUserId`,
`executedAt` e `correlationId`. O recibo não traz nome nem e-mail. `GET .../runs/{runId}` traz o mesmo recibo em
`units[].receipt`, sem efeito nenhum, e é a fonte da evidência.

Cada desfecho deixa, na mesma transação:

- `events.legacy_registration_reconciliation_applied` ou `events.legacy_registration_reconciliation_skipped`, com alvo
  unidade, ator `Moderator`, ID real e correlação. A auditoria não tem campo para o motivo: ele está no recibo imutável da
  unidade-alvo;
- em `Applied`, as auditorias System `events.registration_withdrawn_by_suspension` e `events.registration_promoted`, e os
  vínculos em `ops.registration_reconciliation_unit_facts`.

Os `factIds` do recibo sobrevivem à retenção: depois de 7 dias sem entrega o fato e o vínculo somem, e o recibo fica.

## Verificação posterior

Com acesso de leitura autorizado, por ID, sem exportar dado de participante:

1. `GET .../runs/{runId}`: run `Closed`, e nenhuma unidade `Pending` além das que você interrompeu de propósito.
2. Para cada `Applied`: a inscrição do manifesto está `Withdrawn`, com `withdrawn_by_suspension_id` igual à medida original.
3. Para cada evento tocado: `confirmed_count` igual às inscrições `Confirmed` e `CheckedIn`, e `confirmed_count <= capacity`.
4. A quantidade de vínculos da unidade é igual ao tamanho de `factIds`, enquanto a retenção não apagar os fatos.
5. Nenhuma entrega foi criada para esses fatos em `ops.outbox_deliveries`.
6. A medida original e `identity.users` não mudaram. A reconciliação não reatribui a medida nem restaura ninguém.

## Interrupção segura

Pare de mandar `:apply` e chame `:close`. O `:close` espera as unidades em curso, e as seguintes recebem `409 RUN_CLOSED`.
Uma unidade em curso termina: não há como cancelá-la pela API.

- `503` no `:close`: estourou o `lock_timeout` de 2 s esperando uma unidade. Repita. Com `Retry-After: 5` o desfecho é
  desconhecido: confira `state` e `version` no `GET` e, se ainda estiver `Open`, repita.
- `401 REAUTHENTICATION_REQUIRED`: faça o step-up e repita.
- `500`: defeito. Confira o `GET`, anote o `traceId` e investigue.
- `412`: o `ETag` está velho. Leia o run e use o atual.
- `409 RUN_CLOSED`: já estava fechado.
- Sem alternativa de fechar, por exemplo sem moderador autorizado: **não mexa no banco.** Sem pedidos, nada é aplicado, e o
  run aberto só impede criar outro.

Crash do processo ou do banco no meio de uma unidade volta atrás inteiro: a unidade fica `Pending`.

## Release compatível

- Web de uma só versão durante a janela. Não aplique em deploy em andamento nem com versões mistas.
- A migração `20261105120000_RegistrationReconciliationManifest` já deve estar aplicada.
- O corte não muda com run aberto: isso bloqueia o run em `409 CUTOVER_CHANGED`, de propósito. Uma mudança de Compose
  durante a janela tem esse efeito.
- Mudar `LegacyRegistrationSelector.Version` com run aberto tem o mesmo efeito. Feche o run antes de publicar esse binário.
- Um binário anterior a esta fatia não tem `:apply` nem `:close`. Um run aberto continua sem efeito, mas não fecha.

## Reversão sem apagar histórico

Não existe desfazer. Não há rota nem script para restaurar uma inscrição retirada, e não se escreve um. O ADR-0044 é
explícito: retirada não volta, e restaurar poderia ultrapassar a capacidade ou desalojar quem foi promovido.

- O histórico fica: o `Down` da migração recusa rodar, triggers recusam apagar run, unidade e recibo, e a retirada por
  suspensão não pode ser reescrita.
- Reverter o **código** é publicar uma release sem as rotas, com o run fechado antes. Tabelas e recibos ficam.
- Parar a **operação** é fechar o run. Remover o corte depois é a mesma mudança de Compose descrita nos critérios, com
  janela e ressela. Sem corte, nada mais é candidato.
- Unidade aplicada por engano é incidente: registre em [support-incidents.md](support-incidents.md) e decida a correção
  com o responsável, para frente e com rastro próprio. A jornada de readmissão no mesmo evento não existe hoje e exigiria
  decisão de produto (ADR-0044).
