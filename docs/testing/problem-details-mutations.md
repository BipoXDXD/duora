# Canários de erros nas mutações — issue #142

Acompanhamento da [#104](https://github.com/BipoXDXD/duora/issues/104) e do
[PR #128](https://github.com/BipoXDXD/duora/pull/128). A #142 acrescenta regressões de apresentação no
navegador; não muda contrato, mensagens, limites, autorização ou comportamento da aplicação.

## Fronteira e requisitos

As respostas HTTP são interceptadas pelo Playwright; o bundle de produção executa componentes, parsers,
classificação, mutações e traduções reais. Todos os novos cenários rodam em `pt-BR` e `en-US`, nos três
projetos originais: Chromium, WebKit e mobile Chromium. Cada estado de erro exige:

- mensagem local esperada e `html lang` correto;
- ausência do marcador de texto/HTML em `title`, `detail` e `errors` no texto e no HTML do DOM;
- ausência de execução de `<img ... onerror="window.CANARY=1">`;
- zero violações nas verificações axe WCAG A/AA existentes, sem dispensar avaliação manual.

Relacionado às seções 8.1, 8.6, 13.2 e 13.7 da spec e a T-23/A11Y da matriz de aceite. As invariantes de
privacidade INV-05 e privilégio INV-12 são preservadas: fixtures sintéticas, sem dados de participantes,
credenciais reais ou serviços externos. Preferências continuam independentes de locale/fuso e respeitam o
contrato de versão existente (API-P03/P07). MFA (API-A12), selfie (API-P05/P06) e ações de sala (API-R03)
são exercitados somente pela interface já implementada, sem alterar seus controles de autorização.

## Matriz da cobertura

| Operação/caminho | Cobertura anterior | Acréscimo na #142 |
|---|---|---|
| Login, cadastro, recuperação, confirmação, reset, bootstrap da sessão | Canários bilíngues em `problem-details.spec.ts` | Mantidos, sem duplicação |
| Perfil/sair e leituras de conta, preferências, eventos, sala e selfie | Canários representativos no PR #128 | Mantidos |
| PUT `/me/preferences` | Formulário e testes existentes; canário de leitura | 400 VALIDATION_FAILED, 428 PRECONDITION_REQUIRED e 503 TEMPORARILY_UNAVAILABLE com rascunho preservado |
| PUT `/me/locale` | Fluxos existentes de idioma | Mesmos três erros; seleção em rascunho não muda idioma da UI quando a gravação falha |
| PUT `/me/time-zone` | Salvar, remover e ação do dispositivo em E2E | Mesmos três erros com fuso em rascunho preservado |
| PUT preferências — 412 PRECONDITION_FAILED | Testes de conflito existentes sem essa matriz E2E de canários | Alerta local, salvar bloqueado antes da revisão, recarga preserva rascunho, novo envio leva If-Match atualizado e idiomas escolhidos |
| PUT fuso — 412 PRECONDITION_FAILED | Testes de componente de conflito | Alerta local, salvar bloqueado, recarga mostra fuso salvo mantendo rascunho; novo envio leva versão atual e escolha preservada |
| PUT locale — 412 PRECONDITION_FAILED | Testes existentes de locale | Fallback local, sem troca da UI no erro; revisar restaura escolha salva; novo envio usa versão atual e só o sucesso troca idioma |
| PUT locale — código desconhecido | Proteção compartilhada e login no PR #128 | UNKNOWN, constructor, __proto__, toString; fallback local; recarregar a página recupera idioma salvo |
| PUT preferências — 429 RATE_LIMITED | Classificador e alerta unitários | Texto localizado com Retry-After=2; sem repetição automática da gravação durante avanço de dois minutos do relógio do navegador |
| POST `/me/mfa/confirmations` | Canário de início de inscrição | MFA_CODE_INVALID/400 e REAUTHENTICATION_REQUIRED/401 com traduções específicas; constructor/400 com fallback; código digitado e inscrição permanecem disponíveis |
| POST `/me/selfie-verifications` | E2E PT de indisponibilidade, sem canário completo | SELFIE_UNAVAILABLE/503 bilíngue; mensagem local e botão disponível após falha |
| POST `/me/selfie-verifications/{id}/capture` | Bundle com permissão pendente | SELFIE_UNAVAILABLE/503 bilíngue; botão disponível e nenhuma chamada à câmera |
| POST `/rooms/{id}/accept`, `/reject`, `/exit` | Leituras de sala com canário | TEMPORARILY_UNAVAILABLE/503; fallback local, mantém sala e controle habilitado |
| POST `/rooms/{id}/actions` | Testes de jogo existentes | VERSION_CONFLICT/409 ao escolher carta; fallback local, controle disponível |
| POST `/rooms/{id}/routes-actions` | Testes do painel de Rotas | VERSION_CONFLICT/409 ao confirmar introdução; fallback local, controle disponível |
| PUT inscrição de evento e POST check-in | Consulta de eventos com canário | TEMPORARILY_UNAVAILABLE/503 nas duas mutações; fallback local e controles disponíveis |

São 29 cenários por idioma, 58 por projeto e **174 novas execuções E2E**. As expectativas de texto são
literais e independentes do catálogo usado pelo produto. Um teste falharia, por exemplo, se o componente
passasse a exibir texto do servidor, se a busca aceitasse propriedades herdadas ou se a revisão de versão
apagasse o rascunho que hoje deve ser preservado.

Arquivos: `frontend/e2e/problem-settings.spec.ts`, `frontend/e2e/problem-mutations.spec.ts` e utilitário
`frontend/e2e/support/problem-canary.ts`.

## Limites e caminhos não aplicáveis

- A matriz contém respostas **sintéticas**. Não prova persistência PostgreSQL, aplicação real de cota,
  atomicidade, autorização no servidor, entrega a fornecedores ou homologação operacional.
- 428 conserva o fallback genérico atual dos formulários. Não se acrescenta mensagem específica ou
  mudança de política de repetição para fazer o teste passar.
- Os caminhos de erro foram escolhidos por consumidor/mutação; não se testa o produto cartesiano de
  todos os códigos, etapas e formulários. A restauração de locale na revisão difere da preservação de
  rascunho em preferências/fuso e é verificada conforme o comportamento atual.
- Concluir selfie depende do callback do SDK depois da captura. Essa etapa não é simulada por um novo
  atalho de produção nem validada com câmera/AWS reais nesta entrega. A captura é recusada antes de abrir
  o SDK; homologação da selfie continua na #93.
- Outras ações/etapas dos jogos, opções de MFA sem tela própria e funcionalidades futuras não são
  criadas para completar a matriz. Novos consumidores precisam ampliar os canários quando implementados.
- O teste 429 protege apresentação e ausência de repetição automática no intervalo simulado; não
  demonstra aplicação de cota ou contagem regressiva. A UI existente apresenta a espera indicada.
- Migração dos valores legados `errors.body` e `errors.Idempotency-Key` de upload permanece na
  [#141](https://github.com/BipoXDXD/duora/issues/141); nenhum valor foi alterado.

## Validação e reversão

Rodar `pnpm typecheck`, `pnpm lint`, `pnpm format:check`, `pnpm test`, `pnpm build` e `pnpm e2e`.
A sensibilidade do cenário 429 foi verificada com uma mutação temporária local: agendar uma nova execução
da mutação após 60 segundos causou falha nos dois idiomas, com duas gravações observadas em vez de uma.
O código de produção foi restaurado antes da execução final; nenhuma alteração funcional integra o PR.
O relógio é instalado antes da navegação e o teste aguarda a estabilização da rede após o avanço para
observar também o encadeamento assíncrono de atualização de CSRF e gravação.

Os nove gates do repositório continuam exigidos. O relatório do PR registra comandos/resultados executados,
commit e CI. Nenhum workflow ou scanner é reduzido. A reversão consiste em reverter os arquivos de teste e
este inventário; não há migração, flag ou implantação.
