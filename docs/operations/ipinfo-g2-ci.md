# Ensaio G2 de IPinfo Lite no CI

**Estado:** preparação proposta no PR dedicado #188; G2 continua **pendente**. A #108 fica aberta.
Não houve acesso a token, criação de ambiente, download real ou despacho de ensaio real nesta preparação.
Detector, atualizador e monitor continuam desativados; corte V3 inativo; catálogo BR + 27 UFs, sem EUA ou
alertas reais. Testes pesados somente no GitHub Actions, sem Docker/build/.NET no Mac.

Referências: [plano aprovado](../superpowers/plans/2026-10-22-issue-108-operational-homologation.md),
[ADR-0037](../architecture/adr/0037-ipinfo-lite-mmdb-para-sugestao-de-pais.md),
[governança vigente](ipinfo-country.md), [issue #108](https://github.com/BipoXDXD/duora/issues/108).

## O que fica preparado

O workflow `IPinfo Lite G2` tem preflight sintético em PR, sem segredos, e despacho manual com três modos:

| Modo | Acesso ao fornecedor | Resultado |
|---|---|---|
| `preflight` (padrão) | Nenhum | Regressões sintéticas e guardas do workflow; não aceita G2 |
| `inventory` | Um download, somente após autorização própria | Metadados/tipo/epoch/hash/bytes/redes e medições JIT; tipo observado não vira perfil aprovado |
| `validate` | Um novo download, após revisão do inventário | Exige tipo exato e SHA-256 revisados; reutiliza validador produtivo e recusa bytes diferentes; ainda não aceita G2 |

Sem agenda, retries automáticos de execução, publicação de base, promoção de ponteiro, banco PostgreSQL,
credenciais de produção, conexão com Web/Worker ou transporte de notificação. O downloader/validador/manifesto
são compilados por referência aos mesmos fontes revisados no #183, com MaxMind.Db 5.2.0 e lockfile; não há
nova biblioteca nem implementação concorrente do contrato. O cliente preserva limite de três redirects,
HTTPS/443 e token Bearer só na requisição inicial. Não acrescentar token à URL ou usar `curl -v`.

A origem fixa é `https://ipinfo.io/data/ipinfo_lite.mmdb`. A documentação do fornecedor descreve redirects
para CDN e exemplifica autenticação por query; **a autenticação Bearer do cliente existente ainda precisa
ser comprovada no produto/conta reais**. Se houver 401/403 ou host não autorizado, interromper: não criar
fallback para query, ampliar hosts ou encaminhar token ao CDN silenciosamente.

## Acessos necessários — configuração pelo responsável

1. **IPinfo:** conta com IPinfo Lite habilitado na área [Data Downloads](https://ipinfo.io/account/data-downloads),
   acesso ao produto `ipinfo_lite.mmdb` e token autorizado para download. A disponibilidade na tela não prova
   que o token funciona no runner. Não enviar token/MMDB por chat, issue ou PR.
2. **GitHub:** administrador para criar/configurar o ambiente exclusivo `g2-ipinfo-lite`, e permissão de escrita
   para despacho manual do workflow após integração. A preparação não cria ou modifica ambientes/segredos.
   Na conferência inicial só existia `production`; não reutilizá-lo para G2.
3. **Proteção do ambiente:** lista de revisores contendo exclusivamente o titular `BipoXDXD`, bypass administrativo desativado e
   única regra de deployment branch `main` do tipo **branch**, sem tags/wildcards. O guard verifica esses
   metadados por API antes de liberar o job com segredo. Revisores extras são recusados, pois o GitHub exige
   apenas um dos listados; incluí-los permitiria outra pessoa liberar o acesso. Sem substituto por decisão vigente; não presumir
   segundo aprovador. Com apenas o titular, bloquear autoaprovação tornaria o despacho pelo próprio titular
   impossível; essa opção precisa ser compatível com o operador escolhido, sem inventar outro responsável.
4. **Segredo exclusivo do ambiente:** `G2_IPINFO_LITE_DOWNLOAD_TOKEN`. Não cadastrar no nível do repositório,
   organização ou `production`. O titular pode usar entrada interativa local:

   ```sh
   gh secret set G2_IPINFO_LITE_DOWNLOAD_TOKEN --env g2-ipinfo-lite --repo BipoXDXD/duora
   ```

   Nunca usar `--body TOKEN`, argumento de linha de comando, arquivo no checkout ou token no chat. Custódia,
   validade/rotação/revogação e eventual escopo oferecido pela IPinfo devem ser acordados; não prometer token
   de escopo reduzido se o fornecedor não o oferecer.
5. **Variável do ambiente:** `G2_PROFILE_JSON`, contendo somente a referência pública de autorização e os
   limites do ensaio. Nenhuma credencial, URL assinada, endereço privado ou IP de participante. Revisar
   cada configuração antes de aprovar o job; os valores não são defaults da aplicação.

O job de elegibilidade usa `GITHUB_TOKEN` somente com leitura de conteúdo/Actions. Os nove checks do CI de
push da main precisam estar completos e verdes no mesmo SHA. Checkout sem credencial persistida; segredos
não aparecem em jobs de PR, branches externas ou preflight. O job real só aceita despacho manual da main
no repositório original, depois do preflight e da elegibilidade, e espera a revisão do ambiente.

## Aprovações necessárias antes do primeiro download

Registrar na #108 uma autorização específica do responsável, com operador, SHA, modo, ambiente/arquitetura,
janela, orçamento de Actions/egresso/disco/memória, duração e **um download** permitido. Aprovar armazenamento
apenas temporário no runner e descarte ao fim. Nenhuma autorização é inferida da criação deste PR.

Preencher e aprovar os valores de `MaxBytes`, `TimeoutSeconds`, `MaxNetworks` e a lista mínima de hosts de
redirect. O template abaixo é deliberadamente inválido até as decisões; não copiar limites sintéticos:

```json
{
  "Authorization": "https://github.com/BipoXDXD/duora/issues/108#issuecomment-REFERENCIA_APROVADA",
  "MaxBytes": null,
  "MaxNetworks": null,
  "TimeoutSeconds": null,
  "RedirectHosts": [],
  "DatabaseType": null,
  "ExpectedSha256": null
}
```

Os tetos defensivos da ferramenta (bytes até `int.MaxValue`, 50 milhões de redes e até 600 segundos para
execução do download/medição) limitam o harness; **não são limites operacionais homologados nem propostas de
capacidade**. O cancelamento de 600s é cooperativo; hash/abertura de um leitor em andamento podem ultrapassar
a janela antes da próxima verificação. O job real inteiro tem timeout de 20 minutos incluindo restore/build. Uma execução excedente
falha; não aumentar timeout ou limites automaticamente para obter resultado verde.

Hosts possíveis documentados: `ipinfo.io`, `dl.assets.ipinfo.io`, `dl.ipinfo.io`, `storage.googleapis.com`.
O perfil deve aprovar só o subconjunto necessário. Nenhum host é autorizado por ser retornado em `Location`;
origem, conta, acesso contínuo e redirects reais exigem comprovação. URLs assinadas e cabeçalhos não são registrados.

Para `validate`, revisar o inventário e aprovar explicitamente `DatabaseType` e `ExpectedSha256`, além de nova
execução/janela. Se a publicação mudar entre inventário e validação, o hash diferente **interrompe** o ensaio;
revisar o novo snapshot em vez de aceitar automaticamente. Uma referência de autorização não é verificada
semanticamente pelo código: a revisão humana do ambiente confere seu conteúdo, SHA, janela e perfil.

## Sequência de execução após as autorizações

1. Revisar/autorizar o PR dedicado de workflow e obter os nove checks existentes **e** `g2-synthetic` verdes
   no head exato. Integração não autoriza acesso ou ativação. Não alterar as regras dos checks existentes.
2. Configurar o ambiente/proteções/token/perfil conforme autorização específica; não há configuração pronta
   nesta entrega. Conferir custódia e possibilidade de revogar o token ao encerrar o ensaio.
3. Executar preflight manual na main. Esse modo não lê o segredo nem acessa a IPinfo:

   ```sh
   gh workflow run ipinfo-g2.yml --repo BipoXDXD/duora --ref main -f mode=preflight
   ```

4. Somente com autorização de acesso/download registrada, despachar `mode=inventory` na main e revisar o
   job pendente no ambiente `g2-ipinfo-lite`. Não aprovar job com SHA ou perfil diferente do autorizado.
5. Ler `g2-report.json`. Ele inclui proveniência fixa, SHA-256, datas originais, tipo, estrutura/campos
   conhecidos, totais IPv4/IPv6 e medições de abertura com dois leitores sobrepostos. Não inclui redes,
   IPs, valores de ASN/organização/domínio, corpo HTTP, credencial ou URL assinada.
6. Revisar tipo/schema/epoch/bytes/redes e limites medidos; preencher tipo/hash no perfil de validação e
   obter autorização da próxima execução antes de `mode=validate`. Falha exige diagnóstico/revisão; não há
   fallback ou mudança de limites para tornar o resultado verde.
7. Registrar dossiê sanitizado em `docs/testing/reports/YYYY-MM-DD-issue-108-g2.md`, com referências de
   autorização, SHA/run/attempt, medições, falhas e decisão. Não publicar o arquivo MMDB. **G2 pendente**
   até revisar todas as evidências e registrar aceite nominal datado próprio.

O upload contém **somente** `g2-report.json`, com retenção máxima de um dia no Actions; o relatório público
não contém amostras ou valores de enriquecimento. A base fica num diretório privado do runner, é apagada em
`finally` e num cleanup `always`; timeout/cancelamento abrupto dependem do descarte do runner hospedado.
Não usar cache, upload de diretório, anexos de base, backup geral, runner próprio ou self-hosted nesta preparação.
Os metadados operacionais de 90 dias, archive/retention produtivos e réplicas fora do host continuam sujeitos a G3/G5.

## O que este ensaio não fecha

- **Precisão por país:** aprovar corpus/proveniência e limiar de aceite antes de medir; sem IPs de participantes.
  O harness inicial não incorpora corpus real nem faz consultas externas por IP. Coerência técnica/schema
  e presença de IPv4/IPv6 não provam precisão, residência ou localização física; VPN permanece limitação.
- **Cadência/epoch:** definir janela de observação, referência confiável e semântica do epoch. Executar snapshots
  autorizados separadamente, comparar datas/hash e distinguir ausência de publicação de erro de acesso.
  Nenhum agendamento criado. Download repetido não renova validade.
- **Recursos alvo e relógios:** medições são do processo JIT Linux AMD64 do runner, incluindo cópias/validação;
  não certificam Web/Worker/host de produção, relógio sincronizado, refresh concorrente em operação, NativeAOT,
  trimming, ARM64 ou Windows. Verificar relógios de Web/Worker/restart e capacidade no ambiente autorizado.
- **Aceite do primeiro snapshot/perfil:** validação técnica e hash correspondente não equivalem à aprovação
  durável de dados na aplicação. Nenhum store/lease/fencing/promoção ou manifesto operacional é gravado.
- **G3/G4/G5:** cobertura/contingência, armazenamento/orçamento/ACLs, operadores, entrega ao Gmail, corte manual
  e recuperação têm ensaios e aprovações próprios. Titular/Gmail privado designados, sem substituto por enquanto.
  Não publicar endereço privado nem enviar alerta de teste sem autorização específica.

Fonte oficial consultada em 22/10/2026:
[IPinfo Lite](https://ipinfo.io/developers/ipinfo-lite-database),
[download e hosts CDN](https://ipinfo.io/developers/database-download),
[proteções/revisão de ambientes GitHub](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments).
