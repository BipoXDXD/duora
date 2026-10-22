# Plano de homologação operacional da #108 — G2, G3, G4 e G5

> **Para execução futura:** aplicar `superpowers:executing-plans` somente após autorização do escopo da etapa.
> As caixas abaixo descrevem trabalho futuro; a elaboração deste plano não executa homologação ou ativação.

**Status:** plano aprovado pelo responsável em 22/10/2026; publicação no repositório/issue e abertura de PR
documental autorizadas. Homologação, provisionamento, envios reais, implantação e ativação não autorizados.
G2/G3/G4/G5 permanecem pendentes de evidências e aceites próprios.

**Objetivo:** produzir evidências auditáveis e decisões explícitas para homologar separadamente a base IPinfo,
a capacidade operacional, o filtro manual V3 e a recuperação/ativação da detecção.

**Arquitetura:** Profiles conserva declaração e preferência privadas independentes; Matching V3 aplica país
bilateral sob guardas. A sugestão local, atualização e operação da base têm integração e autorização próprias.
O fluxo manual não depende da base IPinfo.

**Tecnologias vigentes:** .NET 10, PostgreSQL 18, React/TypeScript, MMDB com MaxMind.Db 5.2.0/Apache-2.0;
IPinfo IP to Country `country.mmdb`/CC BY-SA 4.0. Não se propõe dependência nova neste plano.

**Especificação:** [ADR-0033](../../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md),
[plano técnico](2026-10-21-issue-108-regional-implementation.md),
[runbook regional](../../operations/regional-matching.md) e
[runbook IPinfo](../../operations/ipinfo-country.md).

**Base conferida:** main `ccc6c558579edf2c09204068cf33c8b8cb4398da`, PR #177 integrado;
[issue #108](https://github.com/BipoXDXD/duora/issues/108) aberta em 22/10/2026.

## Estado e limites desta elaboração

As etapas técnicas 1–10 foram integradas. O PR #177 registra 9/9 checks verdes no head `9779a1a`, com
3.034 testes backend, 836 frontend e 549 E2E. O ensaio de 10.000 perfis prova preservação/timeout/retry da
expansão e recusa de downgrade da cadeia; não prova capacidade de produção, cada Down individual ou execução
de binário antigo. Ver [relatório técnico](../../testing/reports/2026-10-22-regional-homologation.md).

Já aprovados: fornecedor/produto/formato/licença dos dados; biblioteca/versão/licença do leitor;
política de atualização, idade e retenção; proprietário como titular e seu Gmail privado como destino.
Sem substituto por decisão expressa. Não reabrir essas escolhas como se ainda estivessem indefinidas.

Continuam abertos G2, G3, G4 e G5. Detector, atualizador e monitor permanecem desativados e não registrados
para operação. Não configurar corte V3, baixar base real, provisionar serviço, usar segredo, enviar alerta,
aplicar migração ou implantar nesta elaboração. Catálogo executável BR + 27 UFs; EUA seguem na #78.
Nenhuma alteração de finalidade, coleta ou retenção de dados de participantes. Endereço privado não vai ao
repositório, issue, relatório ou log. Testes pesados somente no CI remoto ou ambiente de homologação
explicitamente autorizado; nunca Docker/build/suítes pesadas no Mac de 16GB.

## Separação das aprovações

| Ato | Evidência/decisão necessária | O que permite |
|---|---|---|
| Aprovar este plano | Responsável aceita sequência e critérios | Planejamento aprovado; não libera efeitos externos |
| Autorizar um ensaio | Ambiente, recursos, acessos, orçamento, duração e efeitos permitidos registrados | Somente aquele ensaio, com serviços produtivos desligados |
| Aceitar um gate | Dossiê revisado, limitações e decisão nominal datada | Atende o pré-requisito delimitado; não ativa serviço |
| Aprovar PR de integração | Revisão humana e nove checks verdes no head exato | Integra código; não autoriza deploy/corte |
| Autorizar implantação/ativação | Release/digest, ambiente, janela, operador, escopo e reversão aprovados | Somente componentes/ambiente explicitamente autorizados |

O proprietário pode acumular papéis de produto e operação, preservando identidade da decisão e trilha de
auditoria. Não se presume segundo responsável. Mudanças de fornecedor/licença/finalidade/retenção/limites
aprovados exigem nova decisão do dono do produto; políticas/workflows protegidos seguem PR específico.

## Sequência e dependências

1. Com a aprovação documental concluída, definir ambiente isolado e orçamento dos ensaios; preparar dossiês sem acesso externo.
2. Executar G2 e preparar G3; desenho/ensaios de G4 podem avançar independentemente da base IPinfo.
3. Concluir G4 se houver intenção de liberar apenas filtro/manual, por autorização própria. Detector continua desligado.
4. Concluir G2/G3 e os PRs específicos de adaptadores/integração inativos antes dos ensaios operacionais G5.
5. Aceitar G5 e decidir separadamente se/quando autorizar updater, monitor/retenção e detector. Integração atual
   não oferece simples flag para trocar `DisabledCountryLookup`: exige PR explícito.
6. Avaliar aceite efetivo da #108 após a entrega operacional autorizada. Nenhum merge ou gate isolado encerra a issue.

Não fixar datas de corte, orçamento ou duração de observação do fornecedor sem decisão/evidência. A sequência
pode permanecer parada após o planejamento enquanto as restrições atuais estiverem em vigor.

## Foco de revisão e cenários obrigatórios

| Risco | Onde demonstrar a proteção |
|---|---|
| Hash correto com produto errado, epoch incoerente ou relógio recuado | G2 valida proveniência/manifesto; G5 ensaia restart e bloqueio sem rejuvenescimento |
| Aviso nunca entregue, duplicado ou recuperação ultrapassando falha | G3 define contrato/contingência; G5 verifica dedupe por Id, ordem e entrega autorizada |
| Binário antigo ou rodada V2 atravessando corte | G4 inventaria consumidores/rodadas e ensaia pausa/publicação/versionamento |
| Backup anterior/expirado reativado ou escritor atrasado após retenção | G5 verifica aprovação durável, tombstone, geração/fencing e ausência de downgrade silencioso |
| Sugestão ou falha mudando manual/idiomas ou relaxando filtro | G4 valida UI/API/pareamento; G5 derruba detecção preservando escolhas e reservas |

## Tarefa 1 — G2: homologar a distribuição e o perfil de validação

**Consome:** produto/licenças já aprovados, downloader/validador/leitor inativos.
**Entrega:** relatório do artefato e perfil operacional aprovado; sem integração nos hosts.
**Arquivos de referência:** `backend/src/Duora.Infrastructure/Geolocation/CountryDatabaseDownloader.cs`,
`backend/src/Duora.Infrastructure/Geolocation/CountryDatabaseArtifactValidator.cs`,
`backend/src/Duora.Infrastructure/Geolocation/CountryDatabaseManifest.cs`,
`backend/tests/Duora.IntegrationTests/Geolocation/CountryDatabaseReaderTests.cs`,
`backend/tests/Duora.IntegrationTests/Geolocation/CountryDatabaseDownloadTests.cs` e
`backend/tests/Duora.IntegrationTests/Geolocation/CountryDatabasePipelineTests.cs`.

- [ ] Obter autorização específica de acesso/download em ambiente isolado e definir custódia local do segredo.
  Não solicitar credencial no chat nem inseri-la no dossiê. Confirmar origem HTTPS, autenticação, continuidade
  do acesso, redirecionamentos documentados e ausência de credencial encaminhada a outro host.
- [ ] Conferir `country.mmdb` real: tipo exato, schema, códigos de país, IPv4/IPv6, tamanho, número de redes,
  epoch e sua semântica verificável. Registrar proveniência, licença/atribuição, tamanho, SHA-256 e datas
  originais. O hash local identifica bytes, mas não comprova sozinho autenticidade da origem.
- [ ] Observar publicações na janela previamente acordada; registrar mudanças de hash/epoch e distinguir
  ausência de nova publicação, falha de download e rejeição. Aceitar a cadência efetiva do produto específico;
  a agenda Duora 06h não é evidência de horário/publicação do fornecedor.
- [ ] Propor e aprovar números operacionais para `MaxBytes`, `Timeout`, `MaxNetworks`, hosts permitidos e
  capacidade de memória/disco. Medir abertura/refresh e sobreposição de leitores/cópias no runtime JIT alvo.
  Não copiar limites das fixtures nem declarar suporte NativeAOT/trimming ou Windows sem ensaio próprio.
- [ ] Demonstrar leitura local com amostras de redes de referência com uso/proveniência autorizados, sem
  IPs de participantes. Registrar separadamente coerência técnica e precisão por país; acordar conjunto,
  limiar de aceite e tratamento de erros antes de medir. Resultado não prova residência/localização física.
  Um código US retornado não autoriza habilitar US: o catálogo continua recusando-o como unsupported.
- [ ] Verificar relógio/sincronização entre Web/Worker e após restart, contra referência operacional confiável.
  Datas futuras/ausentes, recuo e regressão de epoch impedem admissão; download repetido não renova idade.
- [ ] Revisar primeiro snapshot e aprovar explicitamente seu hash e perfil; preservar atribuição IPinfo,
  CC BY-SA 4.0 e licença separada do leitor. Mudança de produto/schema/licença reabre revisão.

**Pendências/aprovações:** acesso real, perfil/limites numéricos, janela/cadência e datas confiáveis,
critério/amostras de precisão, recursos do ambiente, primeiro hash e aceite técnico pelo responsável.
Escolha IPinfo/MMDB e MaxMind.Db já estão aprovadas e não precisam ser reaprovadas sem mudança.
**Bloqueio:** sem relatório e aceite, não integrar dados reais ou operar download/detecção.

## Tarefa 2 — G3: comprovar capacidade operacional

**Consome:** governança aprovada, titular/destino definidos, interfaces `ICountryDatabaseNotifier` e
`ICountryDatabaseArchive`; G2 informa dimensionamento final.
**Entrega:** desenho operacional, matriz de acesso, orçamento e plano de atendimento aceitos.

- [ ] Registrar cobertura com dias, horários, fuso e procedimento para ausência do titular. Preservar a decisão
  de não ter substituto por enquanto; não inventar nome ou plantão. Definir contingência e obter aceite
  expresso da limitação antes da operação; sem cobertura viável, manter operação bloqueada.
- [ ] Manter atendimento de aviso em até um dia útil e indisponibilidade em quatro horas dentro da cobertura
  acordada. Não prometer 24x7 nem mudar esses prazos implicitamente.
- [ ] Escolher serviço privado fora do host, localização, ACLs, capacidade e teto de custo; aprovar fornecedor,
  orçamento e provisionamento separadamente. Demonstrar leitura Web/publicação Worker sem acesso público.
- [ ] Definir identidade de manutenção e grants mínimos para retenção destrutiva de metadados. Não usar o
  migrador em produção, nem conceder UPDATE/DELETE/TRUNCATE à aplicação em registros imutáveis. Planejar
  auditoria, custódia/rotação/revogação de credenciais e acesso do titular aos procedimentos.
- [ ] Especificar/adaptar archive e notifier em PRs próprios, inicialmente inativos. Exigir dedupe por Id durável,
  ordem de incidentes/recovery, timeouts/retries limitados e sanitização; sem dados individuais, tokens ou URL assinada.
- [ ] Demonstrar em sandbox sem saída externa a geração/deduplicação/reserva/retry de notificações. Essa
  simulação não comprova entrega ao Gmail. Registrar comprovação de transporte como pendência até autorização
  específica de um futuro teste controlado de envio; nenhum alerta real é enviado por este plano.
- [ ] Validar exclusão de versões/réplicas/uploads incompletos no desenho do storage; planejar limites simultâneos
  de 14 versões/14 dias, até duas cópias ativa/anterior fora do host e metadados sanitizados por 90 dias.
  Prever limpeza de crash/órfãos e evitar base em backup geral de longa duração.

**Pendências/aprovações:** cobertura/contingência com ausência expressa de substituto, serviço/orçamento/ACLs,
identidade/grants de manutenção, adaptadores, gestão de segredos e demonstração de exclusão/entrega.
Titular/Gmail privado são decisões vigentes; o endereço fica fora dos documentos públicos.
**Bloqueio:** transporte simulado, interface pronta ou destino escolhido não fecham G3. Enquanto envio real
estiver proibido e sem evidência operacional prévia aceita, a parte de entrega permanece pendente.

## Tarefa 3 — G4: homologar filtro regional e transição manual

**Consome:** schema/API/UI manual e V3 já integrados; não depende de ativar IPinfo.
**Entrega:** dossiê de migração/capacidade, consumidores, comunicação e corte proposto, sujeito a autorização.
**Referências:** `RegionalRolloutRehearsalTests`, `RegionalMatchingMigrationTests`, `RegionalMatchingTests`,
`RegionalPreferenceGuardTests`, `RegionalPreferenceTimeoutTests`, `regional-preferences.spec.ts`.

- [ ] Escolher volume representativo do piloto e ambiente autorizado. Medir expansão, waits/timeout/retry,
  privilégios e consumo de recursos; comparar snapshots e verificar ausência de consentimento inferido.
  Não transformar o teste CI de 10.000 perfis em certificação de produção. Definir limites de aceite antes do ensaio.
- [ ] Ensaiar coordenação concorrente, reserva única, bloqueio e alteração moderna/legada durante reserva;
  conferir INV-01/02/03/05/06. Pending não pareia em V3; país ausente só satisfaz unrestricted; aceitação é bilateral.
- [ ] Validar fluxo manual existente em PT-BR/EN-US: ETags independentes, 412, sucesso parcial, duas abas,
  omissão/limpeza de localização, preferência privada e idiomas preservados. Sugestão atrasada não sobrescreve
  escolha manual. Espera não relaxa filtros. Nenhuma nova linha de catálogo/seed.
- [ ] Preparar PR específico para corrigir o aviso da SPA antes da ativação: o texto de que salvar ainda não
  restringe pares não pode continuar sob V3 ativo. Homologar texto/comportamento por estado real do corte.
- [ ] Inventariar releases/digests de Web/Worker/SPA e rodadas V2 futuras. Planejar pausa por
  `Participation__MatchingEnabled=false`, drenagem e retirada de todos os consumidores/escritores incompatíveis;
  servidor/worker compatíveis antes da SPA. Ensaiar sem alteração de produção.
- [ ] Revisar comunicação e configuração explícita das contas existentes, sem converter perfil/idiomas em
  preferência. Propor instante UTC com offset, tratar rodadas V2 por conclusão ou cancelamento/republicação
  administrativos revisados; não reclassificar rodadas publicadas ou editar versões por SQL.
- [ ] Ensaiar falha e reversão: pausar novos pares, conservar preferências/schema/versões e reservas confirmadas,
  corrigir para frente. Não usar V2 irrestrito como rollback nem executar Down destrutivo. Binário antigo
  exige ensaio próprio de compatibilidade e pausa de escritas incompatíveis segundo ADR-0030.
- [ ] Revisão humana e nove checks verdes no head exato de cada PR; registrar aceite e proposta de release/corte.

**Pendências/aprovações:** limites/capacidade no ambiente alvo, revisão de migração/privilégios/concorrência,
aviso da SPA, comunicação/contas existentes, inventário de consumidores/rodadas, instante e procedimento de corte.
**Bloqueio:** G4 aceito não ativa filtro. Migração/deploy/corte exigem autorização própria; podem ser autorizados
futuramente mantendo detector/atualizador desligados e BR como único país executável.

## Tarefa 4 — G5: ensaio integrado de recuperação e decisão de release

**Consome:** G2 aceito, G3 comprovado e PRs de integração/operadores restritos aprovados; G4 para o filtro.
**Entrega:** relatório de exercícios com tempos, rastros sanitizados e decisão explícita de prontidão/release.

- [ ] Preparar ambiente isolado com cópia autorizada e admissível, manifesto/hash aprovados, armazenamento
  homologado e relógios verificados. Não reutilizar ponteiro/credenciais de backup; manter serviços de produção desligados.
- [ ] Ensaiar truncamento/corrupção/schema/licença inválidos, 401/403, 429/5xx/Retry-After, rede/disco indisponíveis,
  excesso de bytes/redes, redirecionamento recusado e falta de permissão. Cada falha deve preservar a ativa admissível
  ou manter sugestão indisponível sem mudar escolhas. Credencial/schema/integridade exigem intervenção humana.
- [ ] Ensaiar dois executores, perda de lease, writer atrasado, crash antes/depois do ponteiro, retenção em paralelo,
  arquivo ausente e restart/relógio recuado. Confirmar fencing/geração, revogação e marca persistida.
- [ ] Confirmar agenda 06h America/Sao_Paulo e quatro tentativas máximas por ciclo: inicial e retries
  +15min/+1h/+4h relativos à primeira falha, respeitando Retry-After e sem repetir cada dia perdido.
- [ ] Usar relógio controlado para limites exatos 36h sem execução, 72h de idade e 168h de recusa; testar monitor
  independente, mudança de gravidade, lembrete diário, recovery, falha/retry e dedupe no restart. Não esperar
  sete dias reais nem alterar relógios de produção. Notificações ficam no coletor sem envio externo.
- [ ] Restaurar sem rede somente a construção aprovada mais recente admissível; verificar hash/manifesto/schema,
  datas originais e tombstones. Cópia antiga/não aprovada/adulterada deve ser recusada. Ausência da cópia mais
  recente não autoriza downgrade automático para outra construção. Integridade exige liberação humana auditada
  com geração vigente, sem reaproveitar autorização de reparo após promoção posterior.
- [ ] Acordar o marco inicial/final de medição antes do exercício. Registrar obtenção, validação e confirmação
  do ponteiro com relógio monotônico, separando atraso de detecção/atendimento da execução da recuperação.
  Aceite da meta até 15 minutos com cópia válida disponível exige medição documentada, sem confundir esse
  tempo com a janela de atendimento humano. Sem cópia admissível, exigir indisponibilidade segura e fluxo
  manual preservado, sem promessa de recuperação.
- [ ] Demonstrar retenção real no storage autorizado: teto 14 versões/14 dias inclusive antiga ativa, duas cópias
  privadas, 90 dias de metadados e exclusão de órfãos/réplicas; backup/redownload não rejuvenescem validade.
- [ ] Testar percurso de notificação com coletor sem saída. O aceite de entrega real ao canal designado depende
  de autorização adicional de envio controlado e comprovação de recepção/deduplicação; fica pendente sob a
  restrição atual de não enviar alertas reais. Não declarar G5 completo apenas com simulação.
- [ ] Revisar atribuição/licenças, auditoria/autorização do procedimento humano e evidências de G2/G3. Registrar
  exercício trimestral e após mudanças no atualizador/storage como rotina futura, sem criar agendamento agora.
- [ ] Submeter decisão de prontidão e autorizações distintas de implantação e ativação por componente. Updater,
  monitor/retenção e detector têm efeitos diferentes e não são ligados automaticamente pelo aceite do filtro.

**Pendências/aprovações:** infraestrutura/acesso de ensaio, adaptadores e operador autorizado, primeiro snapshot
aprovado em G2, medição de recuperação, ensaio do canal real, cobertura e aceite de release. Os efeitos externos
somente podem ocorrer no escopo de autorização posterior; o plano atual os mantém bloqueados.

## Dossiê e regra de fechamento de cada gate

Criar, quando a execução for autorizada, `docs/testing/reports/YYYY-MM-DD-issue-108-g2.md` e relatórios equivalentes
para G3/G4/G5. Esta elaboração cria somente o plano. Cada relatório deve registrar:

1. Gate, requisito, ambiente e aprovação que autorizou o ensaio; identidade do operador/revisor.
2. SHA/release/digests, schema e perfil de configuração sanitizado; hash/epoch do artefato quando aplicável.
3. Cenário, pré-condição, comando/procedimento efetivamente executado, resultado esperado/observado e duração.
4. Link permanente ao CI/artefato ou evidência controlada; nenhuma credencial, IP/payload de participante ou endereço privado.
5. Falhas, limitações, correções e repetição necessária; não inferir aprovação de teste não executado.
6. Decisão datada: aceito, recusado ou pendente; quem decidiu, condições e escopo exato do aceite.
7. Plano de interrupção/reversão e verificação final de serviços/catálogo/issue.

CI exigido para futuros PRs: backend, frontend, e2e, contract, ops, secret-scan, supply-chain e imagens
AMD64/ARM64, no mesmo head revisado. Testes pesados só no CI/ambiente autorizado. Sem redução de gate,
quarentena de teste ou alteração lateral de política protegida. Novos comportamentos exigem testes que
reproduzam a falha antes da implementação; documentação não exige fabricar RED/GREEN.

Um gate só fecha com evidência revisada e aceite explícito; limites e lacunas não podem desaparecer em um
checkbox. Se o ensaio obrigatório estiver proibido, o gate fica pendente. Ao final de qualquer ensaio,
confirmar detector/atualizador/monitor produtivos desligados e ausência de novos seeds/alertas reais/corte,
salvo autorização posterior específica que altere expressamente esse escopo.

## Aprovações de execução ainda necessárias, em ordem

1. Recursos/ambiente dos ensaios, com efeitos externos delimitados por etapa; o plano documental já foi aprovado.
2. G2: acesso/download autorizado, limites/cadência/epoch/critério de precisão e primeiro hash.
3. G3: cobertura/contingência, armazenamento/orçamento/ACLs, manutenção mínima, adaptadores e comprovação de transporte.
4. G4: migração/capacidade/consumidores, aviso/comunicação e proposta de corte; depois, autorização própria de release/filtro.
5. G5: ensaios operacionais e futuro envio controlado, aceite de recuperação/entrega; depois, autorização própria
   de implantação e ativação de cada componente da detecção/atualização.

A aprovação documental registrada acima não supre nenhuma dessas autorizações de execução. #108 permanece aberta; EUA não fazem parte deste plano.
