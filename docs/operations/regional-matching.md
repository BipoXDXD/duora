# Pareamento regional: preparação do corte V3

Esta entrega implementa somente as etapas 3 e 4 da [issue #108](https://github.com/BipoXDXD/duora/issues/108)
e do [plano aprovado](../superpowers/plans/2026-10-21-issue-108-regional-implementation.md), conforme
[ADR-0033](../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md). **Não ativa o filtro.**
Não implementa detector, leitor MMDB, atualização, UI ou habilitação internacional. Catálogo BR + 27 UFs.
Titular, substituto e canal de alertas permanecem pendentes; isto não declara operação pronta.

## Versões e corte futuro

`Participation:RegionalMatchingStartsAt` é um instante opcional com offset explícito (UTC recomendado).
Sem configuração, a publicação continua criando rodadas V2. Nenhum valor é fornecido em configuração de
aplicação, infraestrutura ou implantação nesta entrega.

Quando houver autorização de ativação e G4 satisfeito, o operador poderá configurar o instante de corte.
**Somente rodadas publicadas depois dessa configuração** e cujo início seja igual ou posterior ao instante
recebem V3; as demais recebem V2. Um evento publicado pode ter versões distintas por rodada ao atravessar
o corte. A versão é gravada na publicação, e a execução sempre lê a versão persistida sob o lock da rodada.
Não existe reclassificação automática de rodadas já publicadas, mesmo ainda Scheduled. Antes do corte,
inventariar as rodadas V2 futuras e concluir ou cancelar/republicar os eventos por procedimento administrativo
revisado. Não editar versões diretamente nem anunciar um corte global enquanto houver rodadas V2 a executar.

Reinício, retry, retirada ou alteração dessa configuração não convertem rodadas V3 em V2. O par registra a
versão executada; a FK composta impede que um par declare versão diferente da sua rodada. V1/V2 continuam
no seletor para histórico; o banco desta fatia já admitia apenas V2 e agora admite V2/V3. Nenhuma linha
anterior é reescrita pela migração `RegionalMatchingV3`; o default PostgreSQL continua 2.

## Pré-requisitos da ativação

- Servidor e worker da mesma release, com migração revisada/ensaiada e escritores de localização sob guarda.
  **Binários antigos que gravam perfil sem guarda ou executam V2 fixo não podem coexistir com V3.**
  Antes da troca, pausar novas reservas com `Participation__MatchingEnabled=false`, drenar trabalhos em curso
  e garantir a saída de todos os processos incompatíveis. Não há deploy nem migração de produção neste PR.
- Fluxo manual e comunicação para contas existentes, ainda previstos nas próximas etapas. Ausência de
  preferência é pending e exclui de novas reservas V3; não produzir consentimento por backfill. Esta entrega
  não fornece a UI nem ativa o bloqueio regional nas rodadas V2 existentes.
- Revisão humana dos testes de concorrência, privilégios, compatibilidade e do inventário de rodadas;
  autorização específica para migração, implantação e ativação (G4). Detector não é requisito do fluxo manual.
- G2/G3/G5 continuam impedindo operação da base/detector. Nomes, substituto e canal não foram designados.

## Concorrência e falhas

PATCH da localização por country/subdivision ou regionCode usa a guarda `safety.user_guards` antes da escrita
condicional. Conta e versão são revalidadas após esperar; alteração confirmada incrementa o marcador de
segurança na mesma transação. Edição de bio/nome com localização omitida preserva os três campos e não
altera a preferência regional nem idiomas. Conta restrita recebe 403; ETag vencido recebe 412; timeout/deadlock
recebe 503 com Retry-After, sem alteração parcial. Retry exige nova leitura; não reaplicar escrita vencida.

O pareamento adquire guardas em UUID ordenado e locks de conta/evento/rodada antes de ler localização,
preferência e comunicação. Edição confirmada antes das guardas entra no snapshot; edição posterior vale
para próximas reservas e não cancela sala confirmada. Não há consulta externa ou IP nessas transações.

V3 exclui pending e aceita pares somente quando ambos aceitam o país declarado da outra pessoa. País não
declarado só satisfaz unrestricted; subdivisão não filtra. Idioma comum, consentimento bilateral entre locales,
bloqueios, elegibilidade, histórico, unicidade e prioridade continuam obrigatórios. Espera não abre filtros.
Preferências privadas e países por participante não são acrescentados à telemetria.

Em defeito no filtro ou concorrência, **pausar novos pares com `Participation__MatchingEnabled=false`**,
conservar dados/versões e corrigir para frente. Nunca usar retirada do corte ou retorno a V2 como fallback
silencioso. A migração Down recusa apagar histórico V3; rollback de binário exige pausa e plano revisado,
sem remover schema/preferências e sem consumidores antigos executando rodadas V3.
