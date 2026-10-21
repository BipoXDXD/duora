# ADR-0033: Detecção aproximada opcional e preferência regional por país

- **Status:** Accepted (2026-10-21), decisão aprovada; implementação e ativação pendentes
- **Origem:** [issue #108](https://github.com/BipoXDXD/duora/issues/108), desdobramento de #79/#80; spec 5.1; ADR-0012, ADR-0026 e ADR-0030
- **Revisar quando:** houver evidência para subdivisão no pareamento, mudança da cadeia de proxies, necessidade de persistir detecções ou proposta de fornecedor/base

## Contexto e alcance da aprovação

O responsável aprovou a proposta regional em 21/10/2026 e solicitou seu registro na issue #108 e em ADR,
mantendo a escolha da base de geolocalização pendente, sem habilitar os EUA nem implementar o detector.
Este ADR registra essa decisão; não autoriza instalação de dependência, coleta adicional, migração ou implantação.

O [ADR-0030](0030-profile-country-subdivision.md) e o PR #110 disponibilizaram país/subdivisão opcionais,
integridade PostgreSQL e compatibilidade com `regionCode`. O PR #119 aprovou documentalmente BR e US,
27 UFs brasileiras e 50 estados norte-americanos mais DC, sem ativação. O catálogo executável permanece
BR + 27 UFs. A issue #78 continua dona do catálogo, códigos, fonte, ativação e transição legada.

CLDR 48.2/Unicode-3.0 fornece a distribuição aprovada para códigos/rótulos, conferidos contra ISO 3166;
não fornece o mapeamento IP para país nem licencia uma base de geolocalização. Não criar taxonomia ou seed paralelo.

Na main inspecionada (`a729c6e1d7f0730fd1ef86952d41506c8291c1ea`), `PairSelector` aplica preferências bilaterais,
mas `ParticipationService.Events` ainda constrói candidatos com `Preferences.Unrestricted`.
`ReverseProxy.cs` estabelece confiança nos cabeçalhos encaminhados, mas não implementa geolocalização.

## Alternativas consideradas

| Alternativa | Precisão e privacidade | Dependências/custo | Manipulação, VPN e falha |
|---|---|---|---|
| Somente manual | Declaração corrigível, sem processamento adicional de IP; não comprova localização física | Menor custo, catálogo interno | Autodeclaração pode ser falsa; VPN não interfere; pessoa escolhe ou não informa |
| Somente IP | Estima a conexão, não necessariamente a pessoa; acrescenta inferência | Base local atualizada ou API externa, com operação/licença e eventual cobrança | Cabeçalhos exigem cadeia confiável; VPN pode indicar a saída; desconhecido não permite presumir país |
| Híbrida, escolhida | Sugestão opcional por país, declaração manual prevalece; sem histórico | Mantém fluxo manual e detector independente | Corrigir/ignorar sugestão; falha volta à escolha manual sem abrir filtros |

Recomenda-se consulta local a uma base somente de países, sem transmissão de IP a fornecedor por consulta.
Fornecedor, produto/base e licença ainda não foram escolhidos. Atualizar uma base local também cria dependência
operacional. A opção exclusivamente por IP foi rejeitada como autoridade da localização; a opção manual continua
disponível mesmo sem detector. Nenhuma delas comprova residência, nacionalidade ou elegibilidade jurisdicional.

## Decisão

### Granularidade e preferência

O filtro inicial é por país. Subdivisão permanece opcional no perfil e não participa do pareamento nesta etapa.
País não significa proximidade física. Não coletar cidade, CEP, endereço, coordenadas ou distância, nem criar áreas.
Não há evidência de quantidade de participantes que justifique fragmentar os grupos em 78 subdivisões, somadas
às restrições existentes de evento, disponibilidade, idiomas e bloqueios. Reavaliar apenas com demanda e
evidência de disponibilidade; eventual nova medição agregada depende de autorização própria.

A preferência privada tem três estados distintos:

- `pending`: não configurada; impede novos pareamentos, inclusive na transição de contas existentes.
- `countries`: conjunto explícito, não vazio, sem duplicatas, limitado aos países habilitados.
- `unrestricted`: escolha explícita de ausência de restrição regional, com conjunto vazio; preserva elegibilidade,
  países habilitados e todos os demais filtros.

Não converter ausência, falha de detecção ou UF legada em consentimento irrestrito. A localização continua opcional.
Pessoa sem país declarado só satisfaz o critério regional de quem escolheu `unrestricted`; não satisfaz BR ou US.
Sua própria preferência também precisa estar configurada. A ativação desse requisito em contas existentes
necessita fluxo de configuração e implantação posterior, não ocorre com este registro.

### Três conceitos independentes

| Conceito | Autoridade | Persistência e uso |
|---|---|---|
| Região detectada | Estimativa do servidor | Temporária no formulário; privada; descartada ao sair; não alimenta diretamente candidatos |
| Região declarada | Ação explícita da pessoa | `countryCode`/`subdivisionCode` existentes no perfil; país usado como atributo próprio no pareamento |
| Região desejada | Escolha explícita da pessoa | Preferência privada com versão independente da declaração linguística |

A ação opcional de sugerir país pela conexão só preenche o rascunho inicial ainda não editado da localização
e da preferência. Sucesso seleciona a própria região de forma visível; salvar exige ação explícita.
Esta é a interpretação aprovada do aceite de detecção inicial na #108: pré-seleção, não gravação automática.
Os dois campos podem ser alterados independentemente. Resposta atrasada não substitui edição manual;
nova detecção não substitui declaração ou preferência salva. Não há redetecção periódica.

Alterar a região declarada não muda a região desejada. Nenhuma alteração regional muda locale, fuso ou idiomas
de comunicação, nem o inverso. Não inferir proficiência ou localização desses atributos. Espera aumenta
prioridade apenas entre candidatos compatíveis; nunca flexibiliza qualquer filtro.

### Contratos e persistência futuros

Os nomes abaixo expressam a arquitetura aprovada, não contratos já implementados:

- `POST /me/region-detection`: sem IP enviado pelo cliente; retorna estado, país habilitado quando disponível
  e instante da observação. Estados: `detected`, `unknown`, `unsupported`, `unavailable`, `disabled`.
  Não grava perfil ou preferência. Erros de autenticação e limite mantêm seus códigos HTTP próprios.
- `GET /me/regional-preferences`: estado, países aceitos e ETag.
- `PUT /me/regional-preferences`: substituição explícita com `If-Match`; 428 ausente, 412 desatualizado,
  400 conteúdo inválido. `pending` não equivale a `unrestricted`.
- PATCH existente do perfil: única escrita da localização declarada; mantém semântica do par país/subdivisão,
  null, omissão, ETag e compatibilidade com `regionCode` definida no ADR-0030.

Profiles continua dono dos dados: tabela privada de preferência regional e tabela filha de países aceitos,
FK para `profiles.countries`, versão regional independente da linguística. Estado e países são gravados
atomicamente. Reutilizar autorização do titular, CSRF, no-store, erros e geração OpenAPI/TypeScript.
Preservar dados/versões dos perfis existentes; não criar declaração ou preferência a partir de inferência.
Os nomes físicos e a migração serão definidos no plano posterior, sem nova taxonomia.

### Seleção e concorrência

Reutilizar `Preferences`: `ownValues["country"]` contém apenas país declarado; `acceptedValues["country"]`
contém os países aceitos em `countries`; `unrestricted` omite apenas essa restrição. Excluir `pending` antes
de construir candidatos. A aceita o país declarado de B **e** B aceita o de A; interseção dos conjuntos desejados
não basta. Preservar bloqueios, elegibilidade, histórico e `CommunicationPolicy.Agree`, inclusive idioma comum
e opt-in bilateral quando os locales diferem.

Escritas da localização, inclusive pela API legada, devem entrar no protocolo de guardas compartilhadas
`safety.user_guards` já usado por preferências e pareamento. O `ProfileStore` inspecionado ainda não faz isso.
Reutilizar a ordem de locks, conferir versões e revalidar localização, preferências, idiomas, bloqueios e
elegibilidade após as guardas. Preservar a unicidade das reservas e não fazer I/O externo sob locks.

Se a edição confirmar primeiro, o pareamento usa o novo valor; se a reserva confirmar primeiro, a edição vale
para novos pares e não desfaz a sala existente implicitamente. Conflitos preservam o rascunho na SPA, sem
reaplicar automaticamente escrita vencida. Versionar a mudança de seleção para distinguir rodadas com filtro regional.

## Segurança e operação

O detector usará apenas `HttpContext.Connection.RemoteIpAddress` após o middleware confiável. Não interpretar
`X-Forwarded-For` diretamente, aceitar IP no corpo ou confiar em cabeçalho de país fornecido pelo navegador.
O código atual permite um salto e redes configuradas; sem redes, desliga o processamento dos cabeçalhos.
Mudança de CDN/proxy exige revisão da cadeia; configuração confiável não comprova geolocalização.

IP ausente/não público, cadeia inconclusiva e base ausente/vencida produzem ausência de sugestão. País encontrado
fora do catálogo habilitado produz `unsupported`, nunca seleção de outro país. VPN pode localizar a saída;
permitir correção manual, sem contratar detecção de VPN. Não usar locale, idioma, fuso ou servidor como fallback.

Não persistir IP bruto, hash de IP, coordenadas, payload do fornecedor ou histórico regional; não acrescentá-los
a logs/telemetria. Observação detectada dura só o formulário. Persistência futura dessa observação exige
nova aprovação de finalidade e retenção. Nenhuma consulta ao detector dentro de seleção de pares ou locks.

A futura base local deverá ter versão verificável, validação e troca atômica, recuperação da versão anterior
e limite de desatualização aprovado. Detector indisponível mantém o fluxo manual e preferências salvas.
Desligar detector não abre filtros. Se o filtro falhar, pausar novos pares pelo mecanismo existente
`Participation__MatchingEnabled=false`, preservando dados; não voltar a candidatos irrestritos.

## Consequências e pendências

- Menor coleta e fragmentação, compatibilidade com o modelo integrado e correção manual de estimativas.
- País é amplo, autodeclarado e não comprova presença física; não serve como controle de elegibilidade jurisdicional.
- Há custo de operação da base e do fluxo híbrido. `pending` exige configuração antes de novos pares.
- **Pendente:** fornecedor/base de geolocalização, licença, orçamento, responsáveis, cadência e limite de
  desatualização. A aprovação da arquitetura não escolhe esses itens nem autoriza transmissão de IP a terceiros.
- **Fora desta entrega:** implementar detector, endpoints, schema, filtros ou coleta; habilitar US; instalar
  dependências; aplicar migração ou implantar. A #78 conserva a ativação de países e pendências jurisdicionais.

## Compliance

Verificação futura ligada ao aceite da #108, spec 5.1 e INV-01/02/03/05/06:

- Unidade: bilateralidade/assimetria, país ausente, três estados, espera sem flexibilização e independência linguística.
- Detector/proxy: IPv4/IPv6, IP não público, headers forjados, proxy não confiável, salto excedente, VPN,
  país não habilitado, base ausente/vencida e sucesso/falha sem escrita implícita.
- PostgreSQL real: FKs/unicidade, preservação na migração, duas escritas com mesma versão, edição versus reserva,
  locks, rollback e caminho legado; não substituir por SQLite/EF InMemory.
- HTTP/contrato: autorização, CSRF, no-store, 400/412/428, rate limit, compatibilidade e não exposição de preferências.
- SPA/E2E: pré-seleção, falha, edição durante detecção, abas concorrentes, conflito preservando rascunho e idiomas.
- Operação sintética: atualização/recuperação da base, custo/latência, ausência de coleta adicional e desligamento seguro.

Esta entrega é documental. Esses testes são requisitos futuros, não resultados executados. A implementação
depende de plano e autorização posteriores; merge e implantação seguem os gates existentes.

## Referências

- [ADR-0012](0012-pareamento-deterministico.md), [ADR-0026](0026-publico-idiomas-e-regiao-do-servidor.md), [ADR-0030](0030-profile-country-subdivision.md).
- [Issue #108](https://github.com/BipoXDXD/duora/issues/108), [PR #110](https://github.com/BipoXDXD/duora/pull/110), [PR #119](https://github.com/BipoXDXD/duora/pull/119).
- [ASP.NET Core: proxies e forwarded headers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).
- [Limitações com VPN/proxy](https://support.maxmind.com/knowledge-base/articles/anonymizer-and-proxy-data-maxmind), referência técnica, não escolha de fornecedor.

## Adendo de 2026-10-21 — IPinfo IP to Country em MMDB e licença aprovadas

Após a comparação de bases locais, o responsável aprovou explicitamente **IPinfo IP to Country em MMDB**
e a licença **Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0)**. Este adendo resolve
as pendências de fornecedor, base, formato e licença registradas acima; as seções anteriores preservam o
contexto da decisão original. Não altera os demais requisitos do ADR nem autoriza implementação ou ativação.

### Escolha e justificativa

A distribuição escolhida é o recorte **IP to Country**, arquivo `country.mmdb`, documentado entre os schemas
alternativos da oferta gratuita IPinfo Lite. Não é o pacote completo `ipinfo_lite.mmdb` com ASN, nem uma base
City, de coordenadas ou de detecção de VPN. A futura integração utilizará apenas o código ISO do país;
rótulos continuarão vindo do catálogo do produto. CLDR/Unicode-3.0 e IPinfo/CC BY-SA 4.0 têm finalidades
separadas e não substituem a licença um do outro.

A escolha favorece consulta local, sem envio de IP de participante ao fornecedor por consulta, distribuição
sem cobrança de licença na oferta consultada e atualização diária anunciada para IPinfo Lite. Formato MMDB
permite consulta local sem importar faixas de IP para o PostgreSQL do produto. Gratuidade não elimina o custo
operacional de download, validação, armazenamento e manutenção. Não houve contratação ou criação de conta.

Não se afirma superioridade de precisão medida para Brasil/EUA: a comparação foi documental, sem benchmark.
A detecção continua sendo estimativa corrigível pela pessoa; VPN pode indicar o país da saída. Falhas e país
não habilitado continuam sem fallback permissivo, conforme a decisão original.

### Licença, proveniência e minimização

A licença aprovada exige preservar atribuição à IPinfo, link para a licença e indicação de alterações quando
houver; adaptações compartilhadas ficam sujeitas ao ShareAlike. A implementação futura deverá documentar
os avisos de atribuição, origem, nome do artefato, versão/data, hash e eventuais transformações. Manter a base
separada do código e evitar redistribuição ou transformação desnecessária. A aprovação da licença dos dados
não aprova automaticamente uma biblioteca de leitura ou outra dependência de software.

Nenhum arquivo de base ou token entra neste PR. Não publicar credenciais de download, consultar IPs de
participantes, adicionar ASN/cidade/coordenadas ao fluxo, armazenar histórico de detecção ou transmitir IPs
a uma API externa. Downloads futuros da base são distintos de consultas de participantes.

### Pendências que permanecem

- Confirmar acesso, continuidade e cadência efetiva da distribuição específica `country.mmdb`; a atualização
  diária anunciada para a oferta não equivale a validação operacional deste artefato.
- Designar responsável técnico e orçamento operacional; definir atualização, alertas, retenção de versões,
  recuperação e limite de desatualização. Verificação diária, alerta após 72 horas e suspensão após sete dias
  foram recomendações da comparação, **não decisões aprovadas por esta escolha de base/licença**.
- Validar licença/versão da biblioteca de leitura, proveniência do primeiro snapshot e apresentação da atribuição
  no produto, antes de introduzir dependências ou dados.
- Elaborar plano e obter autorização para implementação, migrações e ativação; homologar precisão com amostra
  autorizada e referência de localização, sem usar dados de participantes por padrão. Testes sintéticos cobrem
  contratos e falhas, mas não demonstram precisão geográfica.

**Esta entrega é exclusivamente documental.** Não implementa detector, endpoints, persistência, filtros ou
rotina de atualização; não altera contratos, seeds, configurações ou coleta; não habilita EUA nem outros países;
não aplica migração nem realiza implantação. Cobertura mundial da base não amplia o catálogo executável,
que permanece BR + 27 UFs. A issue #108 continua aberta para as pendências e entregas futuras; a #78 conserva
a responsabilidade pela habilitação de países. A prioridade manual e a independência linguística permanecem.

### Referências da escolha

- [IPinfo Lite Database — distribuição IP to Country, MMDB e licença](https://ipinfo.io/developers/ipinfo-lite-database).
- [IPinfo Lite — oferta gratuita e atualização diária](https://ipinfo.io/lite).
- [CC BY-SA 4.0 — atribuição, alterações e compartilhamento pela mesma licença](https://creativecommons.org/licenses/by-sa/4.0/).

Fontes consultadas na comparação de 21/10/2026. A validação desta entrega é documental; não houve teste do
arquivo de base, benchmark de precisão ou execução do detector.
