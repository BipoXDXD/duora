# Dossiê unificado — região e decisões de produto

Data: 24/10/2026, America/Sao_Paulo. Responsável pela consolidação: assistente sob delegação expressa do titular, coordenado pelo principal; operador único de host/fila:69c1. Escopo: #108/#78, decisões #9 e dependências de #79/#80. Sem coleta nova de idade/documentos/atributos sensíveis ou habilitação de países fora do recorte aprovado.

## Decisões registradas e situação real

O [registro de produto](../../product/decisions-2026-10-24.md) ratifica as escolhas anteriormente abertas, com motivos e limites. Não requer um novo OK genérico. A ausência de teste físico, módulo ou recurso continua explícita: decisão não é implementação nem resultado de operação.

Base técnica inspecionada: main `1d713328625c27494b2f9cba9497f3c503f2af7e`, que contém #233. Foram obtidos uma vez os corpos e comentários atuais das issues108/78/9/79/80. Suas descrições misturam histórico: catálogo BR+US(#196), runtime opt-in(#204), comunicação da SPA(#216) e HTTP/PostgreSQL regional(#228) já estão integrados. Os defaults desligados não comprovam flags efetivas do host.

## Entregas concretas desta frente

- [PR #236](https://github.com/BipoXDXD/duora/pull/236), head `3ac89c5aef5d88d8f336c4b1fc84bb31229fa641`: fecha a admissão de publicação quando MatchingEnabled=false. Regressão PostgreSQL verifica ausência de eventos/rodadas/jobs e retomada explícita em V3. [CI40139528531](https://github.com/BipoXDXD/duora/actions/runs/40139528531) concluiu os nove gates SUCCESS, incluindo backend e imagens AMD64/ARM64. Sem merge. Não muda defaults, país, schema, infra ou MFA. Publicação já em voo/processo antigo ainda exige drenagem.
- Guarda G2 reforçada em mudança separada de política: current main antes/depois, run/tentativa exatos, workflow ci.yml, nove jobs únicos/success e resposta completa. Nova conferência depois do review protegido, antes do download. Retenção do JSON sanitizado passa de um para90dias; não há retenção de MMDB/segredo.
- Testes leves da guarda: RED de13testes com nove falhas em subcasos novos; após correção,13/13 PASS em0,002s. Actionlint sem ShellCheck e diff-check aprovados. Nenhum build/.NET/Docker pesado no Mac. A CI própria da mudança ainda será vinculada ao head final.
- Decisões #9: políticas explicitadas, ADR-0038 ratificado como desenho e ADR-0040 aceita exclusão futura de Ended/Blocked. A main ainda não tem persistência Connections; não se inventa consulta ou teste contra tabela inexistente.

## G2 — fonte, snapshot e perfil

Evidência histórica preservada: [dossiê de22/10](2026-10-22-issue-108-g2.md), inventário39818934781 e validação39833751768. Tipo `ipinfo bundle_location_lite.mmdb`, hash `c79e2755a3820baa605aef79f05074b64df3fe95eedae0fd3ca4aec0530fa5c2`, build22/10 08:02:37UTC,25.750.684bytes e8.309.353redes IPv4/IPv6. ValidationPassed=true; GateAccepted=false histórico preservado.

A [nova autorização delimitada](https://github.com/BipoXDXD/duora/issues/108#issuecomment-6390539993) permite uma sequência inventory→revisão da identidade pelo principal→validate, após os nove gates da main fornecidos pelo integrador. Limites iguais:64MiB/10milhões de redes/180s, redirect apenas dl.assets.ipinfo.io, um download por etapa sem retry e job20min. Ambiente protegido/revisor/BYPASS desativado permanecem. Nenhuma credencial sai do job.

A documentação primária [IPinfo Lite](https://ipinfo.io/developers/ipinfo-lite-database) consultada em24/10 conserva o schema e informa Last Updated23/10, MMDB24,52MB; a [oferta](https://ipinfo.io/lite) anuncia gratuito e atualização diária. Isso é informação do fornecedor, distinta de medição independente da cadência. Não justifica precisão física ou residência. A mainH233 foi qualificada pelo integrador em CI40138928892/attempt1, nove jobs próprios SUCCESS. O [novo inventário real](2026-10-24-issue-108-g2.md), run40140827300/1 SUCCESS, observou build24/10 08:04:06UTC e hash3f7282d7… no mesmo tipo/schema/perfil. A identidade foi enviada ao principal antes de validate; GateAccepted=false preservado. Detalhes e limites estão no relatório próprio.

Aceite necessário: proveniência e identidade do novo arquivo, limites, coerência técnica, amostras autorizadas/critério de sugestão e limites de relógio/capacidade. Não reenviar IP de participante ao fornecedor nem usar exemplos anycast como localização física conhecida. Hash/schema validado identifica bytes e estrutura, sem transformar marketing em medição.

## G3 — operação e recursos

Decididos: titular/Gmail privado/sem substituto, cobertura regional segunda–sexta09h–18h America/Sao_Paulo, prazos dentro da cobertura, B2 dedicado sem Object Lock e gasto incremental máximo zero. A disponibilidade de quota gratuita, bucket/ACL/exclusão real, credenciais sob custódia e entrega Gmail ainda precisam de evidências. Não reutilizar bucket imutável de backup nem contratar serviço por inferência.

69c1 informou que não há neste checkpoint prova qualificada de NTP Web+Worker, inventário funcional/jobs ou bucketG3 dedicado. O recibo sanitizado já existente de22:26:35UTC, hash `8de46905bcf64ce0d728ee2cc370a1d7c287c55e4207ff956855460194fb5079`, prova identidades/mounts/config hashes daquele preflight233, não esses itens deG3. Não foi pedida coleta nova durante233/125 nem copiado segredo/Compose completo.

O [pacote para a janela posterior](../../operations/regional-acceptance-packet.md) limita a coleta a relógios/recursos efetivos, diretório privado/volumes RO/RW, role/grants de manutenção e teste controlado da cópia/exclusão/entrega, com referências sanitizadas. Testes MinIO/pickup/notifier sintético não serão usados como prova do provedor/Gmail. **G3 ainda não aceito.**

## G4 — filtro manual e corte

#228 já cobre14 cenários HTTP autenticados/PostgreSQL, incluindo BR/US, bilateralidade, pending, país ausente, idioma/locale e replay por consumidor novo. Fonte disponível em RegionalMatchingApiTests; não repetir essa entrega. Testes de migração/10.000perfis e recusa de Down são provas sintéticas, sem certificação de capacidade produtiva ou binário antigo.

#236 acrescenta proteção de publicação durante pausa. Ainda são necessários inventário agregado vivo de rodadasV2 futuras/abertas e jobs/leases, consumidores/release/digests, propagação de configuração por processo e verificação de capacidade/privilegios. MatchingEnabled é IOptions em singleton, sem recarga dinâmica; instância antiga ou requisição em voo exige drenagem. O Compose base não transporta essas chaves por si; não tocar nos arquivos de infra do autor230 em paralelo.

Não escolher/configurar corte UTC por conveniência, reclassificar rodadas por SQL, retirar corte para voltar a V2 irrestrito ou alterar preferências por backfill. **G4 operacional não aceito nesta elaboração**; a implementação manual pode ser homologada sem detector.

## G5 — recuperação e entrega no alvo

Reutilizar os testes existentes de pipeline, idade, fencing, retenção, cópia, reinício, dedupe/ordem e entrega incerta, vinculando o CI do head qualificado. Eles não comprovam disco/clock do host, provedor real ou recebimento Gmail. Ensaios do alvo ficam com69c1 depois dos gates/passagem, com serviços produtivos preservados e resultados antes/depois. Recuperação não pode rejuvenescer data, promover base expirada ou ampliar consentimento. **G5 ainda não aceito.**

## Dependências editoriais, assistivas e de conta

O responsável UI entregou [PR #237](https://github.com/BipoXDXD/duora/pull/237), head `d1b8b039b1170f693bbd59e6f0d3c78bec83cb76`, baseH233: correções de reflow320px, anúncio do novo cartão e foco de Rotas. Pacote técnico próprio tem RED/GREEN,39 testes focados,10 critérios e28 layouts; types/lint/format aprovados segundo seus registros e revisão independente semP1/P2. O recibo final do autor informa [CI40140234349](https://github.com/BipoXDXD/duora/actions/runs/40140234349) SUCCESS9/9, frontend881 testes/69 arquivos, E2E549, backend3341/zero falhas e gates AMD64/ARM64 aprovados. Evidência reutilizada, sem nova consulta da CI nesta frente. Sem merge/publicação; não atribuir esses testes à presente execução.

Digests editoriais preservados: Constelação `1b102ddd60b6ef7cb690a228a2cd57751f04c5a5fa66f9ffd8f7b932f9275cd9`; Rotas `21201dc55c18179c5e0e268dbc7bef12176766a49863a08b276dcf0ec45b4d0d`. Aceites anteriores de editorial/direitos/escuta não são reabertos. O responsável UI confirmou a #101 CLOSED/not_planned desde22/10 10:01:28UTC: fechamento administrativo não comprova T23/AT ou publicação. O parecer técnico delimitado não equivale a T23 integral, conforto ou NVDA/VoiceOver físico. Publicação administrativa por MFA precisa de versão/ambiente/recibos reais.

Conta/MFA e sessão/reenvio foram encaminhados ao responsável para coordenação de autoria antes de qualquer edição dos seus arquivos. Nenhum arquivo de conta/UI de outro autor foi alterado nesta frente.

## Critério de encerramento

#9 pode registrar escolhas resolvidas, conservando implementação/ensaio pendentes com rastreabilidade. #78 exige reconciliação do catálogo já integrado, revisão editorial/governança e seu aceite de operação; contract do legado permanece entrega separada, sem apagar compatibilidade. #108 exige os resultados e aceites reais deG2/G3/G4/G5 e a operação autorizada. #79/#80 somente fecham quando as dependências aplicáveis de conteúdo/publicação e região estiverem efetivamente concluídas. Não foram fechadas nesta elaboração.
