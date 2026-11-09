# Cadeia de suprimentos do liveness da selfie (issue #390, TM-99)

Inventário do que o `FaceLivenessDetectorCore` carrega em `/account/selfie` e opções para o conteúdo vindo do CDN da
AWS. **A escolha entre as opções é do dono**; este documento não muda comportamento, CSP nem o fluxo da selfie.

Marcação: **[código]** lido no pacote instalado 3.7.0 ou no repositório; **[doc]** confirmado na fonte citada;
**[medido]** observado em 08/11/2026 (hora local); **[inferência]** não confirmado.

## 1. Artefatos

### Vêm no bundle, da mesma origem

| Artefato | Versão | Origem | Fixação |
|---|---|---|---|
| `@aws-amplify/ui-react-liveness` (JS, CSS filtrado) | 3.7.0 | npm, empacotado pelo Vite | versão exata em `frontend/package.json` e `sha512` no `pnpm-lock.yaml` |
| `@tensorflow/tfjs-core`, `-converter`, `-backend-cpu`, `-backend-wasm` (JS) | 4.11.0 | npm, dependências exatas do pacote | `sha512` no lockfile |
| `@tensorflow-models/face-detection` (JS) | 1.0.3, com patch da AWS | embutido em `dist/esm` do pacote | coberto pela integridade do pacote |

### Vêm de terceiros em runtime

Só depois da `SessionInformation` do servidor, e sem credencial: **[código]** `BlazeFaceFaceDetection` recebe
`binaryPath` e `faceModelUrl` ausentes de `SelfieCapture.tsx` e usa os padrões. Base:
`https://cdn.liveness.rekognition.amazonaws.com/face-detection`.

| Artefato | Caminho | Tipo | Bytes | SHA-256 **[medido]** | Quando é usado |
|---|---|---|---|---|---|
| WASM SIMD | `/tensorflow/tfjs-backend-wasm/4.11.0/tfjs-backend-wasm-simd.wasm` | `application/wasm` | 424594 | `77ebb28a6d34f371dbbf2086b7f2de8994acd8ea5a3cf1fa24d2c26c840cac7b` | navegador com SIMD, sem threads |
| WASM básico | `/tensorflow/tfjs-backend-wasm/4.11.0/tfjs-backend-wasm.wasm` | `application/wasm` | 311123 | `70a5d516060464e5269f01c74bac1772d6b8ab6cb612acf16b5cdaf61f78d892` | navegador sem SIMD |
| WASM com threads | `/tensorflow/tfjs-backend-wasm/4.11.0/tfjs-backend-wasm-threaded-simd.wasm` | `application/wasm` | 435643 | `c052228d4bef185c27bbe59a9e029570c78bbb9f08b3cb46b597851650373de2` | **[inferência]** nunca nesta página: threads exigem `crossOriginIsolated`, e a política nega `cross-origin-isolated` e não define COEP |
| Topologia do modelo | `/tensorflow-models/blazeface/1.0.2/model/model.json` | JSON | 101979 | `97f1127dfb276e92264e95cdc1e5fb755ea8e5b5099c2920cb14ac65fa751a0d` | sempre |
| Pesos do modelo | `/tensorflow-models/blazeface/1.0.2/model/group1-shard1of1.bin` | binário | 201268 | `e9b69ee1c1f8cd33c58120bcd75bf8c56bfba20c880f447e9d293ec102059d4b` | sempre; o nome vem do `weightsManifest` do `model.json` |

Fora do CDN: o WebSocket `wss://streaming-rekognition.us-east-1.amazonaws.com` (`StartFaceLivenessSession`), que é o
serviço em si e não carrega código. **[código]** As URLs de `tfhub.dev` do pacote de detecção são padrões substituídos
por `detectorModelUrl`. **[código]** O runtime é `tfjs`, então o carregador de arquivos do MediaPipe não roda.

As URLs padrão estão em `dist/esm/components/FaceLivenessDetector/service/utils/blazefaceFaceDetection.mjs`. **[doc]**
A referência de `FaceLivenessDetectorCoreConfig` documenta os mesmos padrões
([Amplify UI, Face Liveness](https://ui.docs.amplify.aws/react/connected-components/liveness)); a tabela de
`FaceLivenessDetectorConfig`, na mesma página, ainda cita 3.11.0 e 0.0.7, e não é a do componente usado aqui.

Os hashes acima descrevem o que o CDN serviu naquele momento. **Não são âncora de confiança.** Os arquivos ficaram em
diretório descartável, tratados como dados, sem execução.

### O que a medição mostrou

- **[medido]** Os três WASM do CDN são **idênticos, byte a byte**, aos de `dist/` do `@tensorflow/tfjs-backend-wasm`
  4.11.0 instalado, cuja integridade o lockfile fixa. Vale para esta medição, não como garantia futura.
- **[medido]** Os dois arquivos do modelo não têm equivalente no npm: não houve com o que compará-los.
- **[medido]** Os objetos têm `Last-Modified` de 04/11/2026, `x-amz-version-id`, resposta sem `Cache-Control` e
  `Access-Control-Allow-Origin: *`. **[inferência]** O caminho versionado (`4.11.0`, `1.0.2`) não torna o conteúdo
  imutável: os arquivos foram regravados quatro dias antes da medição. Não achei documento da AWS prometendo
  imutabilidade.

## 2. O que a CSP permite hoje

Só o documento exato `/account/selfie` (`infra/caddy/Caddyfile`, [deploy.md](../operations/deploy.md#documento-da-selfie-issue-93)):

| Diretiva | Valor relevante | Efeito |
|---|---|---|
| `connect-src` | `https://cdn.liveness.rekognition.amazonaws.com` | `fetch` a **qualquer caminho** desse host; a CSP limita o host, não o conteúdo |
| `connect-src` | `wss://streaming-rekognition.us-east-1.amazonaws.com` | streaming, só na região aprovada |
| `script-src` | `'self' 'wasm-unsafe-eval'` | compila WASM de bytes obtidos por qualquer `connect-src`; não libera `eval` de JavaScript nem script de outra origem |
| `worker-src` | `'none'` | sem worker e sem service worker |

Limite: o host de produção usa `/opt/duora/Caddyfile.duora-web.coolify`, fixado por `caddy_sha256`; o teste abaixo lê o
`infra/caddy/Caddyfile` do repositório.

## 3. Integridade verificável

- **[código]** O pacote não expõe hash nem opção de integridade: `binaryPath` e `faceModelUrl` são strings, a palavra
  `integrity` não aparece no detector nem em `backend_wasm.js`, e o WASM sai de um
  `fetch(wasmBinaryFile, {credentials: "same-origin"})` interno.
- **[doc]** SRI declarativo vale para `<script>` e `<link>`
  ([MDN, Subresource Integrity](https://developer.mozilla.org/en-US/docs/Web/Security/Defenses/Subresource_Integrity));
  estes arquivos não entram por esses elementos, logo **não dá para pôr SRI neles sem interceptar o `fetch`**.
- **[doc]** `fetch` aceita `integrity` e rejeita a resposta divergente com erro de rede
  ([MDN, RequestInit](https://developer.mozilla.org/en-US/docs/Web/API/RequestInit)); é a base da opção D.
- **[doc]** A AWS documenta hospedar os arquivos por conta própria, com os mesmos `binaryPath` e `faceModelUrl`
  ([Custom CDN](https://ui.docs.amplify.aws/react/connected-components/liveness/customization#custom-cdn)): pasta com
  os `.wasm` da versão do npm, `model.json` e `group1-shard1of1.bin` no mesmo caminho.
- **[doc]** Atualizar é obrigação com prazo: a AWS pode bloquear a versão major anterior 120 dias após uma nova, e
  anunciar fim de suporte de minor 180 dias depois
  ([Face Liveness update guidelines](https://docs.aws.amazon.com/rekognition/latest/dg/face-liveness-update-guidelines.html)).
  **[medido]** 3.7.0 é a versão mais recente no npm nesta data.

## 4. O que o teste fixa

`frontend/config/liveness-supply-chain.test.ts` e `frontend/src/features/account/SelfieCapture.test.tsx` falham, no job
`frontend`, se mudar: versão exata no
`package.json`; especificador e integridade no lockfile; versão instalada do pacote e do WASM; os três nomes de binário;
as URLs padrão, lidas do detector real; o `config` da captura (só `credentialProvider`); as origens externas do
`connect-src` da selfie. Uma PR do Dependabot que suba o pacote passa a falhar até alguém rever este inventário.

**Não fixa o conteúdo do CDN**, o nome do arquivo de pesos, o Caddyfile do host nem uma requisição real ao CDN.

## 5. Opções para o dono

Ameaça: o conteúdo servido pelo CDN muda (comprometimento ou republicação) e passa a rodar numa página com câmera.
**[inferência]** Impacto limitado pela CSP e pelo fato de a decisão de liveness ser do Rekognition no servidor; o WASM e o
modelo influenciam a detecção local de rosto e a estabilidade da captura. O CDN é da própria AWS, que já é fornecedora do
serviço.

| Opção | Prós | Contras | Custo | Protege o conteúdo? |
|---|---|---|---|---|
| **A. Manter o CDN e aceitar** (estado com este PR) | Nenhuma mudança; AWS mantém os arquivos; caminho padrão do componente | Risco continua; registro formal do aceite | Nenhum | Não: só a versão e a URL |
| **B. Self-host na mesma origem** | Caminho documentado pela AWS; WASM sai do npm com `sha512` do lockfile; modelo versionado no repositório; permite tirar o CDN da CSP e uma origem de terceiro da página com câmera | Revisar os arquivos a cada atualização do pacote; ~300 KB de modelo no repositório, com origem e licença a confirmar; ~730 KB por captura saem do nosso servidor; muda o carregamento da selfie, que pede teste no fornecedor real | Agente: meio dia a um dia. Sem custo novo de infraestrutura relevante. Remover o CDN da CSP exige janela (`caddy_sha256`) | Sim |
| **C. SRI declarativo** | Mecanismo padrão | **Não se aplica**: os arquivos não entram por `<script>`/`<link>` | Não há como implementar | Não |
| **D. Hash em runtime** (envolver `fetch` e passar `integrity` nas 5 URLs exatas) | Mantém o CDN; sem mudar CSP nem janela; bloqueia conteúdo divergente | Monkeypatch global não suportado pela AWS; republicação com outros bytes derruba a selfie; o `model.json` e os pesos precisam de hash fixo sem fonte independente. **[inferência]** Viável pelo código (`fetch` global no momento da chamada, CDN com CORS), não testado | Agente: meio dia, mais teste em navegador e monitoramento de falha | Sim, a custo de disponibilidade |
| **E. Monitorar o hash no CI** (job agendado compara o CDN com os valores gravados) | Barato; revela mudança; compara o WASM com o npm | Só detecta, depois do fato; alerta em republicação legítima; altera workflow, em PR separado | Agente: 2 a 4 h | Só detecção |

Complemento barato de A ou E: restringir o `connect-src` ao caminho `/face-detection/`. Reduz o alcance no host, exige
janela e não protege o conteúdo.

### Recomendação

**B em dois passos, com A valendo até lá.** Primeiro passo só no frontend: servir os arquivos em `/assets/`, já
coberto por `connect-src 'self'`, e validar com captura real. Segundo passo, na próxima janela do Caddyfile do host: tirar o
CDN do `connect-src`, deixando a CSP mais curta.

Motivo: é o único caminho documentado pela AWS que verifica o conteúdo sem depender de monkeypatch, e o WASM já tem
procedência no lockfile. Custo principal: revisar e reempacotar os arquivos a cada atualização do pacote, dentro do prazo de
suporte da AWS, e confirmar origem e licença do modelo.

A confirmar antes de B: licença do modelo; tipo MIME `application/wasm` servido pelo Caddy; carregamento correto em
`/assets/` (hoje com `immutable`, então os nomes precisam mudar a cada versão).
