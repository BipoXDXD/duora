# T-16 — Corpus completo na release isolada

> Execução inline com superpowers:executing-plans e uma revisão fresca da branch ao final. Detalha o aceite já autorizado da #174/ADR-0036, sem decisões de produto novas.

**Objetivo:** executar os18vetores S0 pelo adaptador real do Worker e launcher/sandbox Release no Actions, comparando rejeições, dimensões, variantes, bytes e metadados com o baseline atual. Um PNG mínimo não comprova sozinho toda a matriz T-16.

**Limites globais:** nada de Docker/build/restore/suítes no Mac; nenhum serviço/dado real/host da aplicação, compra ou ativação. Preservar1500ms/4000ms dos ensaios de prazo e os candidatos de recursos/bytes. Workflow protegido em PR dedicado a esta qualificação. Nenhuma dependência nova; reutilizar projetos, corpus e xUnit existentes. Não cancelar nem manipular CI/publicações. #174 continua aberta até todos os demais gates, inclusive host/capacidade/backup/limpeza durável.

**Dependências:** #200 contrato, #202 publicador confiável e #206 prova de bloqueio/recuperação. Preparação empilhada; merge somente após pais integrados, atualizar com main e checks completos. Nenhum registro de política editorial ou instalação.

## Interfaces e plano

- `NativeSanitizerCorpus.AssertEquivalent(SanitizerBaselineCase, JsonElement baseline, SanitizeOutcome actual)` fica exclusivamente no projeto de testes. Confere resultado/código técnico, formato, dimensões orientadas, ordem thumb/card/full, tamanhos/hashes dos bytes e WebP identificado sem ICC/EXIF/XMP/IPTC/CICP. O algoritmo e o decoder de produção não mudam.
- `SanitizerResourceEnvelopeTests` conserva os dois processos baseline atuais. Quando solicitado explicitamente pelo harness nativo, usa `IsolatedPhotoSanitizer` real com socket/UID/pin do perfil que está executando; configuração parcial ou ausência quando obrigatória falha. Sem opção solicitada, continua apenas o baseline existente, sem teste declarado como ignorado.
- O harness Release passa somente configuração não secreta para um processo do runner como UID do Worker. Executa a classe xUnit já usada no target baseline do projeto, com deadline externo e limpeza do grupo de processos. Sem Docker API/segredo enviado ao decoder. Os18vetores passam pela mesma instância de transporte, sequencialmente; oversized deve rejeitar no cliente antes de transmissão conforme S3.
- O relatório sintético só se completa após18comparações; contém hashes/dimensões/códigos/tempos e perfil ensaiado, nunca imagens. Artefato extra do mesmo workflow/attempt; não alterar o manifesto fechado S4 nem inventar autenticação por hash interno. O commit/produtor confiável vincula o código da matriz; o relatório não substitui gates do pacote.
- O workflow licenciado compila os testes Debug de instrumentação no runner antes do ensaio, sem licença/segredo herdado. Geração e comparação são instrumentação fora do sandbox; somente o executável Release decodifica dentro do sandbox512MiB/1CPU. A instrumentação não é o processo Worker de produção nem homologação de seu orçamento completo.

## Etapas

1. Testes positivos e negativos do comparador, com stub explícito; observar RED nos runners sem erro de compilação incidental.
2. Implementar comparação e observar GREEN. Preservar os testes/algoritmo existentes.
3. Integrar opção obrigatória para qualificação Release no benchmark, harness e workflow. Testes de contrato recusam configuração parcial/relatório incompleto; publicação falha sem a execução solicitada, sem fallback para apenas PNG mínimo.
4. Nove checks, duas arquiteturas e verificador do workflow na árvore final; revisão independente única; uma passada para P1/P2 com RED/GREEN, menores registrados.
5. Merge após pais/janela de coordenação e checks. Depois CI main verde, dispatch confiável com ambas as imagens licenciadas e18comparações reais por arquitetura; ler artefatos completos. Falha em vetor legítimo não autoriza elevar teto ou retirar caso.

## Foco da revisão

- Um resultado diferente, metadado retido, variante ausente/reordenada ou hash divergente nunca qualifica.
- Solicitação de prova nativa não pode cair silenciosamente no baseline local; perfil/peer e UID devem corresponder à fronteira real.
- Nenhum segredo/decoder/fallback entra no Worker; nenhum processo da instrumentação pode ficar órfão após timeout.
- Uma matriz sintética aprovada não é medida do orçamento completo do Worker/daemon nem aceite do host ou ativação.

Referências: ADR-0036; T-16; plano S4; SanitizerBaselineCorpus; IsolatedPhotoSanitizer. O comando xUnit direto é o mesmo do target RecordSyntheticSanitizerBaseline já versionado, sem mudar o gate geral.
