# Pareamento linguístico — desenho para revisão

Status: proposta técnica; não implementada. Data: 19/10/2026.
Base inspecionada: `921500657619700b5b9a6095d1b79b5eb6638927` da main.
Branch local: `feat/language-matching-policy`.

## Objetivo e autorização

Implementar a política aprovada nas issues #79 e #80, reaproveitando os dois trechos do PR #92 na spec e no ADR-0026. O responsável confirmou nesta conversa a inclusão da infraestrutura necessária do M2. Essa confirmação resolve o alcance; as escolhas técnicas abaixo são propostas para revisão.

A entrega deve permitir que dois adultos elegíveis, com idiomas declarados compatíveis, aceitem um convite, concluam uma experiência com conteúdo localizado individualmente e saiam sem usar microfone. Não pode expor preferências alheias, inferir proficiência, traduzir comunicação ao vivo ou ampliar critérios pela espera.

## Evidência da base

- `Duora.Core/Matching/PairSelector.cs` implementa seleção pura e bilateral, sem dimensão linguística.
- `Duora.Infrastructure/Profiles/ProfileStore.cs` persiste nome, descrição e região; não persiste preferências de comunicação nem locale.
- `Duora.Contracts/Profiles/ProfileContracts.cs` contém somente os contratos atuais de perfil.
- Não existem implementações de reservas/rodadas/salas em Infrastructure ou endpoints de sala em Web.
- A SPA possui autenticação, conta e perfil; não possui a experiência/sala.
- `docs/testing/acceptance.md` identifica T-02, T-03, T-05, T-06, T-08 e T-09 como parciais, com as partes transacionais do M2 pendentes.
- `CatalogText` e os conteúdos das duas experiências usam strings únicas. O domínio já fixa `ExperienceVersion`.
- Os E2E atuais usam `FakeDuoraApi`; um teste apenas com esse dublê não comprova a integração real solicitada.

## 1. Preferências privadas e locale

Profiles será dono de `profiles.preferences`, com `user_id`, `allow_different_display_locales`, `version` e `updated_at`. Uma tabela dependente `profiles.communication_languages(user_id, language_code)` terá chave composta e FK restritiva; nenhuma lista concatenada ou JSON de preferências. Os idiomas de comunicação iniciais serão códigos `pt` e `en`, separados dos locales `pt-BR` e `en-US`.

Identity manterá `preferred_locale` na conta, nullable para migração aditiva, com CHECK dos dois locales. Conta legada usa `en-US` para exibição quando o valor está ausente; esse fallback nunca declara idioma de comunicação. No cadastro, resolver o locale da requisição; alteração posterior explícita não modifica os idiomas aceitos. O idioma da interface e os critérios privados aparecem em controles distintos na SPA.

Ausência de linha de preferências significa declaração pendente, não preferência irrestrita. A gravação explícita exige pelo menos um idioma suportado, sem duplicatas, e booleano explícito para aceitar pares com locale diferente. Checkbox sem marcação inicial. A tela informa que chat e voz não têm tradução automática e que aceitar o pareamento não autoriza microfone.

API-P07 (`GET /me/preferences`) devolve somente ao titular: `communicationLanguages`, `allowDifferentDisplayLocales`, `declarationRequired`, `version`, com ETag forte. Estado não declarado: lista vazia, opt-in falso, versão 0. API-P03 (`PUT /me/preferences`) recebe os dois campos declarados, com If-Match obrigatório, e devolve o recurso atualizado. Campo desconhecido ou idioma inválido: 400 `VALIDATION_FAILED`; sem precondição: 428; versão antiga: 412. Nunca aceitar UUID do titular enviado no corpo.

A alteração de locale pelo titular também participa da guarda de usuário, pois muda a exigência de opt-in. O contrato de conta/perfil terá recurso de localização próprio com ETag, evitando misturar concorrência de locale com edição de nome/bio. OpenAPI é gerado pelo Web, seguido do cliente TypeScript gerado.

## 2. Política pura de compatibilidade

Um tipo imutável de declaração linguística carrega locale de exibição, conjunto de idiomas aceitos e opt-in explícito. O seletor elimina a aresta antes da prioridade se falta declaração, a interseção é vazia ou os locales diferem sem dois opt-ins. Locale igual não dispensa interseção.

Para vários idiomas comuns, escolher por ordenação ordinal dos códigos canônicos (`en` antes de `pt`), independentemente de UUID, posição, espera ou ordem da lista. Registrar `communication_policy_version = 1` e idioma escolhido no par. A versão do algoritmo de pareamento também muda para distinguir a regra nova de resultados históricos.

Opt-in é aceitação explícita do tipo de par nas preferências, como definido na issue. A aceitação individual do convite continua obrigatória para iniciar a sala. Rejeição ou expiração por qualquer pessoa cancela o convite e libera as duas reservas, sem revelar motivos individuais.

## 3. Reserva transacional e guardas

A pré-seleção é apenas uma sugestão. `BuildRoundPairings` abre transação curta, configura lock_timeout, adquire guardas de todos os candidatos em UUID ordenado e depois o coordenador de evento/rodada. Reconsulta, após os locks, elegibilidade etária/conta, inscrição/check-in, bloqueios nos dois sentidos, reservas existentes, histórico do evento, preferências e locales. Só então executa o seletor e grava pares/reservas/convites.

API-P03 e alteração de locale adquirem a mesma guarda antes de verificar a versão, atualizar o estado e incrementar `security_version`. Assim não há janela entre revalidação e reserva. Atualização concluída antes da guarda do coordenador deve ser observada; atualização posterior vale para pareamentos futuros e não muda silenciosamente uma sala iniciada.

As tabelas e nomes seguem `docs/data/model.md`: `events.pairings`, `events.pairing_participants`, `experiences.game_sessions` e participantes. Índice único parcial de reserva ativa por usuário garante INV-02 mesmo com dois coordenadores; unicidades de rodada e histórico impedem duplicação. Contenção resulta em retry limitado da transação completa ou falha temporária neutra; nunca afrouxar a política.

Bloqueio incorpora, na mesma transação existente de `BlockService`, cancelamento/aborto da sala e liberação das reservas. Respeitar guardas de usuário → evento/rodada → relação → sala. Toda leitura de convite/sala também inclui participação, elegibilidade e ausência de bloqueio; terceiro recebe indisponibilidade neutra.

## 4. Infraestrutura M2 necessária

Implementar persistência e serviços dos eventos/inscrições/check-in/rodadas que alimentam a seleção, reutilizando as máquinas de estado puras existentes. A entrada operacional deve ser autenticada e autorizada; dados sintéticos de teste não se tornam endpoints de produção. Publicação administrativa e consulta/inscrição/check-in usam os contratos já definidos na spec.

Persistir salas e snapshots conforme ADR-0020, incluindo controle separado dos estados privados. A sala usa o mesmo ID de `game_session`, sem criar entidade paralela `rooms`. APIs de convite, projeção, ação e saída usam participante autorizado e versão esperada. Deduplicação de ações e idempotência HTTP não substituem reautorização no replay.

Jobs de convite/rodada/deadline usam persistência, lease e fencing conforme ADR-0021. Reexecução não cria novas reservas ou reinicia partidas. Eventos de invalidação não carregam estado privado; recuperação HTTP permanece autoritativa. A implementação deve cobrir recuperação após reinício antes de declarar o fluxo M2 concluído.

A entrega linguística não adiciona fornecedor de voz nem captura de microfone. O fluxo textual precisa ser suficiente para a experiência cooperativa; chat contextual, quando disponibilizado nessa fatia, segue autorização bilateral, texto simples e ausência de tradução. Conexões e conversa persistente após interesse mútuo continuam um subprojeto separado, sem promessa de M2 integral.

## 5. Conteúdo localizado e mesma experiência

Uma única versão contém estrutura compartilhada (IDs de etapas/cartões/opções) e traduções obrigatórias `pt-BR` e `en-US` de cada texto, descritor acessível, transcrição e fala. A projeção escolhe o locale do participante e preserva a mesma estrutura. Cada recurso de áudio pré-gerado declarado exige variantes nos dois locales, com manifesto/hash/proveniência conforme ADR-0027; não gerar áudio em runtime.

Publicação rejeita texto ou variante de áudio ausente. Não fingir localização copiando o texto de um idioma para ambos. Catálogo sintético de testes possui transcrições e arquivos verificáveis; mídia definitiva e revisão editorial continuam requisitos antes de publicação real.

Leitor tolerante compreende snapshots antigos; gravador novo só é habilitado depois desse leitor. Conteúdo legado não se converte silenciosamente em catálogo bilíngue publicável. Novas salas fixam versão e locales de projeção no nascimento; edição posterior de preferências não altera o acordo ou a versão.

A projeção autorizada entrega idioma combinado e versão da política, nunca a lista completa de idiomas aceitos, opt-in do outro participante ou motivo de ausência de par. Convite/entrada apresenta o acordo em texto localizado e o aviso de ausência de tradução.

## 6. SPA e acessibilidade

Adicionar preferências privadas com salvamento condicional e tratamento de 412 que preserve o rascunho, seguindo o padrão atual do perfil. Contas legadas devem declarar explicitamente idiomas antes de participar do novo pareamento.

Adicionar entrada de evento, check-in, estado de espera neutro, convite com aceitar/recusar, sala com projeção individual e ações da experiência, conclusão e saída. Toda cópia pertence aos catálogos existentes; teclado, foco, leitores de tela e ausência de áudio devem permitir conclusão. Nenhum `getUserMedia` no fluxo de participação, convite, entrada ou conclusão.

Região permanece critério independente. Não criar detecção por IP, país/subdivisão ou uma nova taxonomia geográfica. Alterações de locale ou idioma não reescrevem região e o teste confirma essa independência.

## 7. Validação exigida

| Camada | Cenários a implementar e executar |
|---|---|
| Unidade | Interseção vazia; locale igual sem declaração; matriz 2×2 de opt-in com locales diferentes; ordenação determinística; espera longa sem relaxamento; bloqueios prevalecem; igualdade estrutural e localização de ambas as experiências; publicação incompleta recusada |
| PostgreSQL real | Migração de legado sem proficiência; ETag concorrente no primeiro INSERT e UPDATE; preferência/locale concorrente com reserva em ambas as ordens de commit usando barreiras; dois coordenadores/uma reserva; bloqueio concorrente; rollback sem estado parcial; convite recusado/expirado libera ambos; reinício e replay; versão imutável |
| HTTP | CSRF e titular; negação a terceiro; leitura e replay após bloqueio; payload sem preferências do outro; contratos e códigos de validação; cliente antigo não obtém pareamento permissivo |
| SPA | Declaração explícita, conflito preservando rascunho, avisos nos dois idiomas, locale sem alteração das preferências/região |
| E2E real | Duas contas sintéticas e terceiro contexto; sem idioma comum não há convite; recusa por cada lado; aceite bilateral; locales distintos recebem mesmos IDs e textos próprios; conclusão sem permissão de microfone e zero chamadas a getUserMedia |

Executar restore/build/format/test do backend, tipos/lint/format/Vitest/build da SPA, geração OpenAPI e Playwright contra backend e PostgreSQL descartáveis. Não substituir testes de locks por SQLite/InMemory nem chamar E2E com API simulada de prova transacional. Registrar comandos, resultados e impedimentos reais. Gates de secret-scan, contract e supply-chain continuam necessários para merge.

## 8. Entrega, observabilidade e rollback

Organizar commits revisáveis: documentação reaproveitada; preferências/localização; política pura; infraestrutura de reserva/sala; conteúdo; SPA; testes e evidências. Não misturar mudanças em AGENTS, workflows ou permissões.

Métricas de espera e falta de pares são agregadas, sem UUID, idiomas individuais, chat ou áudio; evitar segmentação por grupos pequenos. Logs não serializam DTOs privados. Auditoria registra versão de política/algoritmo sem copiar as preferências.

Migrações são aditivas e ensaiadas em PostgreSQL descartável. Rollback operacional pausa novos pareamentos antes de retornar a binário antigo: código que desconhece a política não pode retomar seleção permissiva. Preservar as declarações e versões fixadas; não apagar dados para voltar. Reativação exige leitor e seletor compatíveis com a política.

Merge e produção exigem revisão humana. A aprovação deste desenho não comprova qualquer teste nem implementação.

## Fontes

- https://github.com/BipoXDXD/duora/issues/79
- https://github.com/BipoXDXD/duora/issues/80
- https://github.com/BipoXDXD/duora/pull/92
- Spec 5.1/5.2, 8, 10.4; ADRs 0016, 0020, 0021, 0026, 0027 e 0028.
- `docs/data/concurrency.md`, `docs/data/model.md`, `docs/testing/acceptance.md`.
