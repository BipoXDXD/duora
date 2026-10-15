# ADR-0011: pgBackRest com WAL externo e restore obrigatório

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 18 e 23.1 (ADR-011)
- **Revisar quando:** volume ou metas exigirem outra topologia

## Contexto

O banco roda na mesma VPS que a aplicação (ADR-0009). A perda do host, corrupção, exclusão acidental ou
comprometimento não podem levar junto as cópias. Restaurar também não pode reativar contas excluídas nem
sessões revogadas (INV-11).

Alternativas registradas na spec:

- **`pg_dump` como único mecanismo.** Descartado para RPO em minutos; dumps lógicos só complementam.
- **Snapshot do provedor ou réplica.** Complementos, não substitutos de cópia externa independente e
  restaurável. Réplica não substitui backup.
- **Sidecar sem acesso a `PGDATA`, socket e configuração.** Descartado: não constitui backup físico funcional.

## Decisão

Vamos usar pgBackRest numa imagem PostgreSQL derivada, fixada e testada, com backup físico completo semanal,
diferencial diário e arquivamento contínuo de WAL num repositório externo e isolado, preservando janela
recuperável de 14 dias. Metas propostas, não SLAs: RPO de até 5 minutos e RTO de até 2 horas para o banco.
O restore segue o procedimento de 10 passos da spec 18.4, incluindo modo de recuperação, revogação de sessões e
aplicação de tombstones antes de reabrir.

Porquê técnico: PITR com perda limitada e cópia fora do host. Porquê de negócio: o lançamento depende de
recuperação comprovada.

## Consequências

- Positivas: recuperação a um ponto no tempo; cópias fora da VPS.
- Negativas: imagem de banco derivada para manter; retenção de fulls, diferenciais e WAL precisa ser calculada
  em conjunto; `archive_timeout` sozinho não garante o RPO.
- Trade-off: cópia imutável e expiração automática do pgBackRest cumprem papéis diferentes e exigem política
  adaptada (spec 18.3).

## Segurança e operação

A aplicação não lê nem apaga backups. Criptografia com chave sob custódia independente. Key ring, configuração,
manifests e tombstones têm cobertura explícita. Ensaio com dados reais nunca vira homologação comum.

## Compliance

- Restore isolado semanal automatizado e ensaio completo de perda de VPS ao menos mensal no piloto
  (spec 18.5; a implementar no M3, com restore sintético já no gate de M0).
- T-11 (restore anterior a exclusão/revogação) e T-25 (VPS perdida e reconstruída): M3.
- Alerta antes de exceder o RPO e ao falhar archive (spec 19.1).
- Gate de lançamento: backup verde sem restauração comprovada não libera (spec 18.5, 23.3).
