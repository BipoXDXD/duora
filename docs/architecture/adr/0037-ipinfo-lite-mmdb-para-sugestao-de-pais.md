# ADR-0037: IPinfo Lite MMDB para sugestão opcional de país

- **Status:** Accepted (2026-10-22), escolha aprovada; adaptação, homologação e ativação pendentes
- **Origem:** [issue #108](https://github.com/BipoXDXD/duora/issues/108); decisão do responsável de trocar o plano para IPinfo Lite e abrir PR documental
- **Substitui em parte:** [ADR-0033](0033-deteccao-aproximada-e-preferencia-regional.md), somente a escolha de IPinfo IP to Country `country.mmdb` e o contrato de produto/fonte/schema correspondente
- **Revisar quando:** mudar produto, schema, licença, finalidade, campos utilizados ou limites aprovados; G2 encontrar incompatibilidade

## Contexto

O responsável encontrou apenas IPinfo Lite (`ipinfo_lite.mmdb`) na área de downloads da conta e aprovou
mudar o planejamento para esse produto. A disponibilidade observada na conta não comprova acesso por CI,
autenticação, integridade, precisão nem prontidão operacional. Não foi baixada ou homologada uma base real.

A [documentação oficial da IPinfo Lite](https://ipinfo.io/developers/ipinfo-lite-database), consultada em
22/10/2026, apresenta país e ASN na mesma distribuição, com licença CC BY-SA 4.0. No Lite, `country_code`
é o código ISO de duas letras e `country` é o nome do país. No produto anterior, `country` era o código.
Portanto, trocar apenas o nome do arquivo não preserva o contrato de leitura.

Alternativas consideradas:

- **Manter `country.mmdb`.** Preserva o contrato implementado, mas mantém a dificuldade de acesso relatada;
  não é a escolha atual do responsável.
- **Adotar IPinfo Lite MMDB.** Mantém consulta local e leitor aprovado; exige contrato explícito para o novo
  produto e revisão do tamanho/limites e dos campos adicionais. Escolhida.
- **API de geolocalização por consulta.** Não atende à arquitetura local aprovada e acrescentaria transmissão
  de IPs a terceiro; não autorizada.

## Decisão

A distribuição planejada passa a ser **IPinfo Lite `ipinfo_lite.mmdb`**, com origem documentada
`https://ipinfo.io/data/ipinfo_lite.mmdb`, sob **CC BY-SA 4.0**. Preservar atribuição à IPinfo, referência à
licença e indicação de alterações quando houver, inclusive em cópias/derivações abrangidas pela licença.
O leitor aprovado continua **MaxMind.Db 5.2.0 / Apache-2.0**; não há dependência nova neste PR.

O contrato futuro usará apenas `country_code` para a sugestão por país. Rótulos continuam vindo do catálogo
existente. `country` não será interpretado como código; ASN, organização, domínio e continente não passam a
ser atributos de participantes, filtros ou dados de telemetria. A aprovação não abrange enriquecimento por ASN.

A base bruta contém também esses campos adicionais: a minimização é da projeção utilizada pelo Duora, não
uma alegação de que os bytes de ASN foram removidos do MMDB. O artefato deve permanecer privado, sujeito à
retenção já aprovada (até 14 versões/14 dias; até duas cópias ativa/anterior fora do host; metadados sanitizados
por 90 dias). Não extrair nem persistir campos adicionais por participante ou consulta, nem criar histórico.

O contrato executável atual continua fixado no produto anterior: downloader, manifesto e validador não
admitem Lite, e o leitor espera o campo anterior. Nenhum deles é alterado neste PR documental. A adaptação
futura deve versionar explicitamente produto/fonte/schema, validar os campos esperados e manter rejeição de
produto incorreto, schema desconhecido, arquivo inválido e metadados incoerentes. Não reinterpretar manifestos,
hashes aprovados ou backups antigos como Lite, nem alternar silenciosamente entre produtos.

O `DatabaseType` real, campos efetivos do MMDB, epoch, limites e primeiro hash permanecem pendentes de G2;
a decisão de produto não inventa valores de perfil operacional nem certifica o arquivo pelo nome.

## Consequências

- Preserva arquitetura híbrida, consulta local e prioridade manual; evita depender do acesso ao produto anterior.
- Exige PR de adaptação do contrato e testes com fixtures Lite antes do ensaio real. Testes sintéticos anteriores
  continuam evidência do produto antigo e não homologam Lite.
- Reabre a parte de G2 dependente de produto/schema/dimensionamento; não reabre as demais decisões aprovadas.
- Não muda contratos HTTP, persistência de perfis, concorrência, idiomas, preferência regional ou granularidade.

## Segurança e operação

Permanecem as políticas do ADR-0033: IP obtido somente após a cadeia confiável de proxies, sem cabeçalhos de
país do cliente; sugestão temporária sem gravação automática; escolha manual e idiomas preservados; filtros
não relaxam com espera. Sem IP bruto/hash, payload por consulta, token ou URL assinada em logs/relatórios.

Permanecem agenda 06h America/Sao_Paulo, retries aprovados, avisos 36h/72h e recusa em 168h sem rejuvenescimento
por download/restore. Limites numéricos e recursos do ambiente exigem aprovação após medição; não copiar valores
de fixtures nem usar tamanho publicado como capacidade homologada. Não usar API externa por consulta.

Titular e Gmail privado continuam designados; endereço não é publicado, sem substituto por enquanto.
Cobertura/contingência e demais pendências de G3/G5 continuam abertas. Detector, atualizador e monitor ficam
desativados; sem download, segredo, alerta real, corte V3, implantação ou habilitação dos EUA. A #108 fica aberta.

## Compliance e próximos passos

O [plano operacional atualizado](../../superpowers/plans/2026-10-22-issue-108-operational-homologation.md)
detalha os gates. A sequência restante é:

1. Adaptar downloader/manifesto/validador/leitor em PR próprio, inicialmente inativos, com contrato Lite e fixtures
   sintéticas; revisão humana e todos os checks obrigatórios verdes no head exato.
2. Autorizar o ensaio G2 isolado: acesso/segredo sob custódia apropriada, ambiente CI, recursos/orçamento/janela,
   origem/autenticação/redirecionamentos e amostras/critério de precisão aprovados. Não enviar segredo no chat.
3. Medir artefato real (schema/tipo, IPv4/IPv6, epoch, bytes/redes, memória/refresh), observar cadência e relógios,
   registrar proveniência/atribuição e submeter primeiro hash/perfil ao aceite nominal de G2.
4. Concluir G3, G4 e G5 conforme seus critérios e obter autorizações distintas de integração, implantação e
   ativação. Aceitar G2 não ativa serviço nem habilita país. Simulação não comprova entrega real de alertas.

Este PR verifica documentação, referências e ausência de alterações executáveis; testes pesados somente no CI.
Não demonstra precisão, acesso real, prontidão operacional ou aceite de qualquer gate.


## Adendo de 22/10/2026 — adaptação técnica inativa

O responsável autorizou adaptar downloader, manifesto, validador e leitor no
[PR #183](https://github.com/BipoXDXD/duora/pull/183). A preparação usa produto `ipinfo_lite.mmdb`,
origem Lite fixa e schema lógico `ipinfo-lite-country-v1`, sem homologar o `DatabaseType` real.
A identidade completa é explícita; JSON incompleto ou produto/schema/fonte antigos não são reinterpretados.
O leitor projeta apenas `country_code`, com validação conservadora pelo BCL e sem fallback por nome.
Os demais campos documentados são opcionais e não viram atributos de participantes ou logs de consultas.

A [matriz de testes](../../testing/regional-matching.md#adaptação-inativa-ipinfo-lite--adr-0037) cobre a troca
com MMDBs sintéticos e o fluxo existente de atualização/recuperação. A preparação do contrato não constitui
aceite de G2 ou autorização operacional. Detector, atualizador e monitor continuam desativados; sem corte V3,
EUA, download/base real, credenciais ou alertas reais. A #108 permanece aberta.
