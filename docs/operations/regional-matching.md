# Pareamento regional: preparação do corte V3

## Estado integrado e cuidados adicionais — revisão de 22/10/2026

O PR #216 integrou o aviso PT-BR/EN-US e os testes de salvamento BR+US. Isso satisfaz a preparação
da comunicação no código; o aceite operacional G4 continua separado. O catálogo BR+US já foi integrado
pelo PR #196, conforme [us-catalog.md](us-catalog.md). O PR #204 integrou o runtime opcional de
geolocalização, conforme [ipinfo-runtime.md](ipinfo-runtime.md): as flags `CountryDatabase` permanecem
false por padrão. As afirmações posteriores sobre BR-only, UI futura ou inexistência de configuração
do detector descrevem entregas históricas, não o estado atual da main. Integração não comprova flags
efetivas nem habilita qualquer componente em um ambiente.

Antes do corte, revisar também estes limites da implementação:

- `ParticipationService` usa `IOptions<ParticipationOptions>` em singleton. Alterar um arquivo ou variável
  no host não atualiza a instância existente. Ensaiar a propagação por processo e a recriação/reinício
  coordenado, conferindo os valores efetivos; não pressupor recarga dinâmica.
- O Compose de produção base não encaminha `Participation__MatchingEnabled` nem
  `Participation__RegionalMatchingStartsAt`. Uma chave no arquivo de interpolação do Compose não é,
  por si só, variável do container. Identificar o mecanismo de configuração aprovado no ambiente,
  revisar sua persistência nas releases seguintes e provar o recebimento pela aplicação antes de usá-lo.
- `MatchingEnabled=false` impede novos claims/conclusões de `build-round` e a construção direta de pares;
  não bloqueia `PublishAsync`. Suspender publicações por procedimento administrativo revisado e drenar
  transações de publicação antes de alterar o corte. Retomar publicações somente após retirar processos
  com configuração antiga: uma publicação que os alcance ainda pode gravar V2.
- Pausa não desfaz trabalho já em execução. Conferir jobs, leases e operações em curso. Os jobs de
  fechamento/expiração continuam elegíveis; não interromper o ciclo das reservas confirmadas sem plano.
  Uma rodada construída mais de 60 segundos após seu início é cancelada pelo serviço, portanto a pausa
  precisa de janela e inventário que evitem perder rodadas.
- A retirada do corte conserva V3 persistido, mas novas publicações sem corte voltam a receber V2.
  Em incidente, pausar também publicações, conservar configuração/schema/preferências/versões e corrigir
  para frente. Elegibilidade genérica de rollback por manifesto/schema não comprova capacidade regional
  do binário anterior; a release de recuo precisa de revisão própria de V3, US e guardas.

O inventário inicial vazio é evidência histórica. Repetir o inventário agregado de rodadas V2 futuras,
rodadas abertas e jobs antes da janela e depois da drenagem, com publicações suspensas. Registrar
release/digests, schema/privilégios, configuração efetiva, limites do ensaio, responsável e aceite G4.
Usar instante UTC com `Z` ou offset explícito revisado; este documento não escolhe nem configura o corte.

## Comunicação para a ativação BR+US

O aviso da SPA descreve agora as rodadas com filtro regional: preferência explícita obrigatória,
aceitação bilateral e preservação das regras das rodadas publicadas antes da ativação. O texto vale
durante a transição e depois dela; não anuncia conversão de V2 ou mudança de escolhas salvas.
Os formulários continuam aceitando BR, US, ambos ou ausência explícita de restrição. Sem preferência
salva, a pessoa permanece pending e não participa de V3. País declarado, idiomas e preferência privada
continuam independentes. Os testes PT-BR/EN-US conferem o aviso e o salvamento BR+US após recarregar.

Esta alteração de comunicação não configura corte nem é evidência de aceite G4. Antes de ativar,
seguir os pré-requisitos abaixo, conferir a release e o inventário vivos e registrar os resultados do
ensaio autorizado e a autorização específica. Não enviar comunicação externa ou preencher preferências
por inferência. O histórico da preparação anterior permanece nas seções seguintes.

Esta entrega implementa somente as etapas 3 e 4 da [issue #108](https://github.com/BipoXDXD/duora/issues/108)
e do [plano aprovado](../superpowers/plans/2026-10-21-issue-108-regional-implementation.md), conforme
[ADR-0033](../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md). **Não ativa o filtro.**
Não implementa detector, leitor MMDB, atualização, UI ou habilitação internacional. Catálogo BR + 27 UFs.
Este parágrafo descreve a entrega histórica 3–4; o estado integrado e as designações vigentes estão abaixo.

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
- G2/G3/G5 continuam impedindo operação da base/detector; designações vigentes no roteiro abaixo.

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


## Capacidade de sugestão nesta entrega (etapas 5 e 6)

O registro executável é sempre `DisabledCountryLookup`. GET autenticado `/api/v1/me/region-detection/capability`
retorna enabled false; POST sem corpo `/api/v1/me/region-detection` retorna disabled. A SPA não oferece ação
quando a capacidade é false ou não pode ser lida. Não existe configuração de ativação real, acesso a arquivo,
download, leitor MMDB ou serviço de geolocalização nesta fatia.

O contrato já aplica limites em memória por conta e origem (IPv4/endereço e IPv6/prefixo /64), sem fila,
429 e Retry-After. Proposta revisável neste PR: `RateLimiting:RegionDetectionPerAccount` = 6/minuto e
`RateLimiting:RegionDetectionPerIp` = 60/minuto. Mudança exige janela positiva e PermitLimit >= 1.
Teste N+1 confirma isolamento das cotas, sem caracterizar validação de carga real. Antes da ativação,
rever limites com evidência agregada de carga/NAT, sem IP ou país por participante em logs/telemetria.
As cotas reiniciam com o processo e supõem a única instância web do ADR-0009.

Responsáveis nominais, substituto e canal de alertas continuam pendentes. Etapas 7–10 e G2–G5 não são
satisfeitos por estes endpoints/UI. Nenhum corte V3, país adicional ou mudança operacional é ativado aqui.


## Homologação e release — etapa 10

Este é o estado integrado que sucede as notas históricas anteriores. Interface manual, API, V3 e componentes
inativos de MMDB/operação já estão implementados. Nenhum corte, detector, atualizador ou alerta real é ativado.
Titular: proprietário; destino: Gmail privado, sem publicar endereço; sem substituto por decisão expressa.
Contingência/cobertura, transporte comprovado e armazenamento/orçamento continuam pendentes (G3).

### Sequência proposta, somente após aprovação de release

1. Revisar evidências do **head exato** nos nove jobs do CI e o ensaio de migração/locks sintético. No ambiente
   de homologação autorizado, repetir com volume representativo e registrar duração, waits e limites acordados.
   O CI com 10.000 perfis não aprova capacidade de produção. Verificar privilégios pelo inventário existente.
2. Registrar versão/digest de servidor, worker e SPA, schema atual/alvo, instante UTC com offset proposto,
   inventário de rodadas V2 futuras, responsável e autorização G4. **Nenhum instante é escolhido neste PR.**
3. Pausar novos pares por `Participation__MatchingEnabled=false`, drenar trabalhos e retirar todos os binários
   sem guardas/V3. Aplicar somente expansão revisada pelo executor autorizado; em contenção abortar e investigar,
   sem loops ilimitados. Entregar servidor/worker compatíveis antes da SPA e verificar endpoints privados.
4. Entregar a interface manual existente. Comunicar em PT-BR/EN-US que país declarado é opcional e separado do
   país desejado; solicitar escolha explícita em preferências. Não converter perfis antigos, locale ou idiomas
   em consentimento. Pending impede novas reservas V3; salvamentos de perfil e preferência têm ETags independentes.
   Texto proposto: “Escolha sua preferência de país para participar das novas rodadas. Sua localização e seus
   idiomas não serão alterados.” / “Choose your country preference to join new rounds. Your location and
   languages will not change.” Revisar comunicação e concluir configuração antes do corte, sem cadastro automático.
5. Antes da ativação, atualizar em PR próprio o aviso atual da SPA de que salvar ainda não restringe os pares:
   ele não pode continuar afirmando isso com V3 ativo. Homologar o texto PT-BR/EN-US e o comportamento E2E
   para o estado real do corte, sem sugerir que rodadas V2 históricas foram reclassificadas.
   Resolver rodadas V2 futuras por conclusão ou cancelamento/republicação administrativos revisados. Só então
   autorizar/configurar `Participation:RegionalMatchingStartsAt` e retomar novos pares. Publicação posterior à
   configuração usa início >= corte para V3; rodadas já publicadas conservam a versão. Não alterar linhas por SQL.
6. Conferir comportamento manual, pending, compatibilidade bilateral e reservas já confirmadas. Detector pode
   permanecer desligado indefinidamente; filtro não depende de G2/G3/G5. G4 e autorização continuam necessários.

### Reversão e critérios de interrupção

- Detector indisponível: manter/desligar somente sugestão, conservar formulário manual e escolhas. Atualmente
  só há registro `DisabledCountryLookup`; ativação futura exige PR de integração, não um flag já disponível.
- Defeito de filtro/concorrência ou consumidores incompatíveis: pausar novos pares com MatchingEnabled,
  drenar operações e corrigir para frente. Reservas confirmadas não são desfeitas implicitamente.
- Recuo de release: pausar também escritas incompatíveis, manter schema, preferências e versões. Avaliar ADR-0030
  antes de permitir binário antigo; ensaio de recusa de Down não equivale a homologação de binário antigo.
- Nunca remover corte para executar V2 irrestrito, apagar preferências, relaxar filtros por espera ou reduzir
  gates. Preservar evidências sanitizadas sem dados individuais.

### Aceites ainda necessários

G2: base real/schema/epoch/cadência/acesso/limites/primeiro hash e relógios. G3: contingência/cobertura, identidade
mínima de manutenção, storage/orçamento/ACLs e transporte/deduplicação/entrega no canal designado. G4: revisão
humana do ensaio, capacidade do ambiente, consumidores, comunicação e inventário/corte. G5: recuperação/alertas
reais em ambiente autorizado, atribuição e autorização específica de implantação/detecção. Não enviar alertas
para satisfazer esses gates neste PR. #108 continua aberta; habilitação dos EUA pertence à #78.
