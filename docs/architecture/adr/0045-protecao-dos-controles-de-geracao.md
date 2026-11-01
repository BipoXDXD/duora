# ADR-0045: Fronteira experimental dos controles de gerações privadas

- **Status:** Proposed (01/11/2026); sem admissão operacional.
- **Origem:** #311; prova cooperativa integrada pelo PR #316 em `b915b10`.
- **Revisar quando:** se propuser autoridade real, identidade de processo, admissão SQL, restore ou integração dos coletores.

## Contexto

A prova anterior conserva quatro contraexemplos: PUT sem fence, troca do inode da trava, perda do marcador e rollback dos controles. A Web e o Worker versionados compartilham UID 1654; o Worker tem acesso direto RW a `derivatives`. Trava advisory não revoga essa capacidade. Nenhum teste local permite substituir writer incerto por assentado.

## Proposta para o primeiro PR experimental

Demonstrar em volumes descartáveis Linux uma autoridade UID 17010, clientes UID 17020/legado 1654 e controlador de revogação UID 17030. Esses IDs pertencem somente à fixture. Não provisionar usuário, serviço, mount ou credencial em produção.

O provisionador temporário cria diretório root:17010/0750, trava/journal root:17010/0660 e enrollment root:17010/0440 sob pai root sem escrita por grupos/outros. A autoridade pode bloquear o inode e gravar o journal; não pode substituir seus nomes. Atores sem capabilities são testados mesmo com o volume RW, para distinguir negação pelo kernel de mount somente leitura.

Enrollment finito vincula domínio, época, versão, reservas sintéticas, donos/modos e identidades dos inodes/dispositivos. Abertura percorre componentes com descritores e `O_NOFOLLOW`; `statx` verifica os objetos abertos, e `O_CLOEXEC` impede herança por exec. UID/GID do peer vêm de `SO_PEERCRED`, nunca do pedido. Os métodos reais do FileSystem são compilados por link; a aplicação não referencia o executável experimental.

O journal deve possuir estados explícitos e integridade encadeada. Falta/corrupção de controle não autoriza criação ou retorno a Open. Fechamento deve ordenar Closing sincronizado, revogação idempotente confirmada em processo/volume separado e Closed sincronizado. Somente depois pode responder ClosedExperimental; varredura exige prova anterior e nunca produz ACK SQL. O controlador local não é um ledger externo homologado.

Alternativas: permissões/marcador sob o mesmo dono legado conservam bypass; retirar escritores pode integrar o corte inicial, mas não substitui proteção durável/restore. A autoridade isolada é recomendada para experimentar a fronteira, não uma decisão de instalar serviço novo. Reuso futuro do Worker e encaminhamento do preparo usado pela Web exigem desenho/revisão próprios.

## Hipóteses e limites

Root/provisionador, imagem da autoridade e controlador são confiáveis. A posse do UID da autoridade permite gravar conteúdo do journal; este ensaio não protege contra comprometimento da própria autoridade. Nome/inode protegido e hash encadeado não fornecem autenticidade contra administrador nem detectam rollback de todo o universo de confiança.

Reservas/manifestações são sintéticas, pré-provisionadas; não há API dinâmica de reserva/manifesto PostgreSQL. Testar UID legado não equivale a executar binário histórico. Retirada de famílias/descritores antigos, política real de lançamento, barreira de épocas após restart/restore e reconciliação operacional pertencem ao próximo PR. Não qualificar essas garantias por esta fixture.

SIGKILL comprova morte de um processo testado, não energia/host. Persistência usa fsync com resultado verificado, sem homologação de hardware. SDK arm64 preso a digest não qualifica amd64 nativo. S3/B2, versões remotas e requests aceitos com resposta perdida permanecem separados.

## Segurança e operação

Nenhuma alteração em IObjectStore, preparadores, coletores, migrations, retenção, autorização HTTP, flags/allowlists, Compose, app_dirs, backup ou serviços. Writer incerto continua sem deleted_at e com limpeza repetível; não preencher writer_settled_at artificialmente. Preservar um ano/65 dias, relógio do banco e token/lease do coletor. #311 e #69 permanecem abertas. Rollback remove somente arquivos experimentais/documentais.

## Compliance

INV-06/08/11/12; T-08/11/16/26; relação com API-M01/M03/M04. A aceitação deste PR é da prova limitada: testes de syscall reais, peer/grants, escrita real, journal/crash/retry e mutações, mais regressões #316/#308 intactas. Nenhuma integração de negócio pode consumir respostas experimentais. Gates gerais e revisão humana continuam exigidos para merge.

Referências: [statx](https://man7.org/linux/man-pages/man2/statx.2.html), [credenciais Unix](https://man7.org/linux/man-pages/man7/unix.7.html), [investigação #316](../../testing/reports/filesystem-generation-closure-poc.md) e [pendências remotas](../../testing/reports/remote-generation-closure-gaps.md).
