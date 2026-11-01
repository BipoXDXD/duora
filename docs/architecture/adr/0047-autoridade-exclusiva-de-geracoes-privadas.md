# ADR-0047: Autoridade exclusiva de gerações privadas

- **Status:** Accepted para o desenho (01/11/2026); implementação e homologação operacional distintas.
- **Origem:** #311; contraexemplos do PR #316 e fronteira experimental do ADR-0045.
- **Revisar quando:** mudar autoridade, identidade, domínio de restore, contrato, custódia ou qualificação.

## Contexto

O proprietário dos diretórios legados consegue ignorar flock, substituir seu inode e restaurar controles
Open. Web/Worker compartilham UID 1654 em caminhos versionados. Marcador cooperativo e lease SQL não
revogam descritores/I/O já admitidos. A escolha de autoridade exclusiva com revogações fora dos backups
restauráveis foi aprovada pelo responsável em 01/11/2026.

## Decisão

Vamos concentrar toda mutação de novas gerações privadas numa autoridade executável com identidade
exclusiva. Consumidores usam contrato tipado; o PUT genérico privado é negado no modo gerenciado. Raiz
física privada é separada do diretório de derivados legados. Não há fallback entre layouts.

Manifestos persistidos precedem admissão e o primeiro byte. A autoridade conserva exclusão até terminar
todo I/O, fecha irreversivelmente antes da varredura e vincula recibos a domínio/época/manifesto.
Uma referência PostgreSQL independente da base de negócio preserva reservas, revogações e épocas; ela
não integra o backup/restore da aplicação e nunca é inicializada permissivamente no startup.

Não inferimos morte de timeout, cancelamento ou lease. Um controlador/supervisor confiável só readmite
nova época depois de retirar a família antiga e suas capacidades/I/O. Reiniciar processo na mesma
época sem essa prova não é readmissão operacional. Falta/corrupção/estado regressivo falha fechado.
Leitura exige concordância de identidade, estado e revisão entre journal local e referência externa;
arquivo final correto não dispensa controles duráveis coerentes. Admissão consulta o nome real dos
bancos antes de consumir a época, sem confiar apenas em strings de conexão ou aliases de servidor.

A integração fica desligada por padrão e recusada em Production até qualificação separada. Testing
permite desenvolver/exercitar a mesma autoridade e os métodos reais de preparo/coleta com PostgreSQL
sintético. Isso não qualifica hardware, retirada de escritores antigos, restore operacional ou S3/B2.
Registros legados não recebem closure sintetizada; não preenchemos writer_settled_at artificialmente.

## Alternativas

Reforço cooperativo conserva bypass. Encerramento comprovado sem autoridade adicional é viável apenas
com confinamento completo por geração; no desenho atual exigiria provar a retirada de Web/Worker e de
todas as suas capacidades. Essa prova continua necessária no corte inicial da arquitetura escolhida.

## Consequências

O contrato concentra enforcement e torna revogação recuperável, ao custo de IPC, fsync, processo e
custódia adicionais. Saturação/indisponibilidade bloqueia admissão; não retorna ao PUT direto. Recursos
e latência precisam ser medidos no host-alvo antes de habilitação.

## Segurança e operação

Nenhum provisionamento/deploy/ativação é autorizado por este ADR. Não alterar Compose, identidades,
permissões ou dados reais por consequência de merge. Rollback mantém fences/épocas/negação de acesso;
cliente incompatível fica indisponível. Restore ocorre em staging sem leitura e reconcilia revogações
atuais antes de admitir nova época. Perda de toda a referência corrente não autoriza reset.

Preservar token/lease do coletor, fechamento anterior à última varredura, guardas de autorização, isolamento
de assets confirmados e retenção mínima de um ano/65 dias no relógio PostgreSQL. #69 e o ledger externo
de exclusões não são substituídos por esta referência.

## Compliance

INV-06/08/11/12; API-M01/M03/M04; T-08/11/16/26. Regressões devem exercitar os quatro contraexemplos,
I/O pausado, crash/resposta perdida, restore/épocas, posse SQL e métodos reais. Qualificação de energia,
filesystem/arquitetura-alvo e provedor permanece evidência separada. O fechamento terminal operacional
de escritores incertos continua desabilitado.
