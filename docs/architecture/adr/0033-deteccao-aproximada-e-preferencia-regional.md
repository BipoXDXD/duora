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

## Adendo de 2026-10-21 — governança operacional aprovada

O responsável aprovou a governança abaixo, solicitando registro documental e mantendo **responsáveis nominais
(titular e substituto) e canal de alertas pendentes**. Este adendo resolve os parâmetros operacionais anteriormente
listados como recomendações; as seções e o adendo anteriores preservam o histórico. A aprovação não implementa
nem ativa detector, atualizador ou monitoramento, e não habilita EUA ou outros países.

### Atualização e promoção

- Verificar a distribuição aprovada `country.mmdb` diariamente às **06h de America/Sao_Paulo**. O horário é
  do Duora, sem pressupor horário de publicação da IPinfo. Não substituir produto/base/fornecedor automaticamente.
- Em falha transitória, repetir após 15 minutos, 1 hora e 4 horas, respeitando `Retry-After`. Não repetir
  automaticamente erros de credencial, schema ou integridade como se fossem falhas transitórias.
- Baixar por HTTPS para área temporária, com credencial protegida e redirecionamentos limitados aos destinos
  documentados do fornecedor. Nunca registrar token ou URL assinada nem sobrescrever o arquivo ativo diretamente.
- Validar legibilidade MMDB, schema esperado, IPv4/IPv6, data coerente, limites de tamanho e testes de leitura.
  Só promover arquivo aprovado, por troca atômica, preservando a versão anterior válida.
- Uma execução de atualização por vez; consultas em andamento terminam com a versão consistente que abriram.
- Manifesto por versão: fornecedor, produto, data de construção/publicação, instante de download, SHA-256,
  licença, resultado da validação e instante de ativação. Hash calculado localmente identifica o artefato e
  detecta alterações posteriores; sozinho não comprova autenticidade da origem.
- Primeira versão exige homologação humana. Depois, promoção automática apenas dentro do mesmo produto,
  schema e licença, com todos os testes aprovados. Mudança nesses elementos ou regressão bloqueia promoção
  para revisão humana. A implementação posterior deverá detalhar os testes e limites de tamanho.

### Idade e alertas

Idade é o tempo desde a data verificável de construção/publicação do artefato, nunca desde o download.
Baixar o mesmo arquivo novamente não renova sua validade. Data ausente, incoerente ou futura impede promoção
até investigação. Os limites abaixo são política do Duora, não exigências atribuídas ao fornecedor.

| Condição | Comportamento aprovado | Alerta |
|---|---|---|
| Base válida com idade menor que 72 horas | Sugestão disponível | Nenhum alerta de idade |
| Idade a partir de 72 horas e menor que 7 dias | Sugestão ainda disponível | Aviso ao responsável |
| Idade a partir de 7 dias | Suspender somente a detecção; manter seleção manual | Indisponibilidade |
| Nenhuma base válida ou arquivo ativo corrompido | Tentar recuperação; sem versão admissível, suspender detecção | Imediato |
| Todas as tentativas diárias falharam | Preservar base ativa enquanto admissível | Aviso operacional |
| Credencial recusada, schema inesperado ou integridade inválida | Interromper tentativa; não promover arquivo | Imediato para investigação |
| Atualizador sem executar há 36 horas | Investigar agendamento, mesmo com base recente | Aviso independente |

Monitorar a idade independentemente do sucesso do atualizador; sua falha não pode impedir a suspensão no limite.
Alertas são deduplicados por incidente: notificar mudança de gravidade, lembrar diariamente enquanto houver
intervenção pendente e confirmar recuperação. Titular, substituto e canal ainda serão definidos.

Prazos de atendimento aprovados: até um dia útil para aviso e quatro horas **dentro da cobertura acordada**
para indisponibilidade. Não pressupor plantão 24x7; a cobertura precisa ser explicitada ao designar a operação.

Suspensão da detecção não altera preferências salvas, idiomas, reservas ou filtros de pareamento. Não recorrer
a API externa, país arbitrário ou base vencida. Permanecem a prioridade manual e o tratamento `unsupported`
para país não habilitado.

### Retenção e armazenamento

| Material | Retenção aprovada |
|---|---|
| Bases aprovadas | No máximo 14 versões, cada uma por até 14 dias desde o download |
| Versões admissíveis para recuperação | Somente validadas, com idade de construção/publicação menor que 7 dias |
| Downloads incompletos/rejeitados | Excluir ao terminar execução; manter somente diagnóstico sanitizado |
| Manifestos, histórico de promoção e alertas | 90 dias, sem IPs consultados ou dados de participantes |
| Histórico de detecção de participantes | Não armazenar |

Arquivo expirado pode permanecer para diagnóstico dentro da retenção, mas não volta ao serviço. Remover o
arquivo ativo ao completar 14 dias desde o download, mesmo sem substituta. A retenção não prolonga a validade.

Guardar também a versão ativa e a anterior em armazenamento privado separado do host, submetido aos mesmos
prazos e exclusões. Evitar bases em imagens de distribuição ou backups gerais de longa duração. Dimensionamento,
serviço de armazenamento e valor do orçamento operacional serão definidos antes de provisionamento; nenhum
recurso ou gasto é criado por este registro. Preservar atribuição/licença nos artefatos retidos.

### Recuperação

1. Rejeitar atualização defeituosa e manter a versão ativa válida.
2. Diante de erro estrutural/leitura na ativa, selecionar a última aprovada ainda com idade inferior a sete dias.
3. Conferir hash, manifesto, schema e leitura antes da troca atômica.
4. Sem versão admissível, deixar a detecção indisponível e sustentar o fluxo manual.
5. Retomar automaticamente após versão válida e recente em falhas de disponibilidade. Incidentes de integridade,
   licença ou schema exigem liberação humana antes da retomada.

Recuperação não redefine data original nem reescreve localização/preferência. Backup restaurado também passa
pela validação e limite de idade; não reativar cegamente arquivo antigo. Meta aprovada: recuperar em até
15 minutos quando houver cópia válida disponível, a homologar antes de assumir esse tempo como capacidade
comprovada. Sem cópia admissível, não há promessa de recuperação automática; seleção manual permanece disponível.

Antes da ativação, ensaiar arquivo truncado, credencial inválida, rede indisponível, disco cheio, atualização
concorrente, reinício durante promoção, base vencida e restauração de backup antigo. Repetir ensaio de recuperação
trimestralmente e após mudanças no atualizador ou armazenamento. Esses ensaios ainda não foram executados.

### Responsabilidade e observabilidade

Definir responsável técnico nominal e substituto, ambos com acesso ao procedimento. O dono do produto aprova
mudanças de fornecedor, licença, finalidade, retenção e limite de idade. Titular/substituto e canal de alertas
continuam pendentes por instrução explícita do responsável; não foram presumidos.

Monitorar apenas idade/identificação da base ativa, última execução/atualização bem-sucedida, falhas por categoria,
latência da atualização, validação/recuperação e disponibilidade da detecção. Distinguir ausência de publicação,
falha de download e rejeição de artefato. Não incluir IPs de participantes, países por pessoa, tokens ou payloads.

### Pré-requisitos e escopo preservado

Permanecem pré-requisitos de homologação: acesso e cadência efetiva de `country.mmdb`, confiabilidade de sua data,
limites de download, biblioteca de leitura e termos, validação do primeiro arquivo e ensaios operacionais.
A cadência diária publicada para a oferta IPinfo não substitui essa homologação. Antes de ativar a operação,
resolver responsáveis/canal, cobertura de atendimento e detalhes de armazenamento/orçamento, e obter autorização
para implementação e implantação. Não há autorização para contratar ou provisionar nesta entrega.

**Somente documentação:** nenhuma rotina, alerta, automação, dependência, download de base, detector, endpoint,
coleta, migração ou implantação foi criado. O catálogo executável permanece BR + 27 UFs; EUA não habilitados.
A issue #108 fica aberta; a #78 continua responsável pela habilitação de países. Este registro não altera
permissões de automação, workflows ou gates de revisão existentes.


### Execução autorizada das etapas 5 e 6 — 21/10/2026

Contrato HTTP e sugestão temporária foram preparados usando o fluxo manual existente. O registro de produção
continua somente `DisabledCountryLookup`: capacidade false e POST disabled, sem leitor, arquivo ou serviço real.
A disponibilidade é consultada por GET autenticado separado, sem lookup; só ação explícita autoriza o POST sem corpo.
Somente rascunho inicial intocado pode receber sugestão; edição manual, gravação/releitura explícita, pedido posterior,
cancelamento e desmontagem protegem contra resposta atrasada. Perfil com versão persistida preserva inclusive
país null: sem metadado de origem regional, só versão 0 pode ser pré-selecionada; perfil existente usa escolha manual.
Isso evita substituir uma escolha salva de não informar, sem coletar origem adicional. Cada formulário conserva
salvamento/ETag independente.

Os limites iniciais propostos (6/minuto/conta e 60/minuto/origem, em memória) e a admissão conservadora de IP estão
registrados no [plano](../../superpowers/plans/2026-10-21-issue-108-regional-implementation.md#registro-de-execução-de-21102026--etapas-5-e-6).
São hipóteses para revisão antes da operação real, não homologação de carga ou autorização de ativação.
Continuam pendentes responsáveis nominais, substituto e canal de alertas. G2–G5, detector/MMDB real, atualização,
implantação, corte do filtro V3 e habilitação dos EUA não são resolvidos por esta entrega.


### Execução autorizada das etapas 7 e 8 — 21/10/2026

O responsável aprovou explicitamente **MaxMind.Db 5.2.0, Apache-2.0**, como leitor local MMDB. A licença do
software é separada da licença **CC BY-SA 4.0 dos dados IPinfo IP to Country**. O texto da licença acompanha
os binários do leitor. A aprovação da biblioteca resolve somente essa parte de G2; não homologa o arquivo real.

O leitor e a atualização foram preparados e testados com MMDB próprio e redes de documentação. Não há arquivo
IPinfo, segredo, download real, registro do leitor no Web ou do atualizador no Worker. `DisabledCountryLookup`
continua sendo a única implementação registrada. O catálogo permanece BR + 27 UFs; a #108 continua aberta.

Operations possui o tipo isolado `country-database-update` em `ops.jobs`, estado singleton e manifestos imutáveis.
Claim, renovação e promoção usam lease/fencing; download e validação ocorrem fora de transações. A agenda diária
é 06h America/Sao_Paulo, sem recuperar cada dia perdido. Os retries +15min/+1h/+4h partem da primeira falha do ciclo,
respeitando Retry-After; falhas de credencial, schema, integridade ou aprovação bloqueiam novas tentativas até revisão.

Publicação registra o primeiro manifesto duravelmente, instala arquivo por hash sem sobrescrita e só então
confirma o ponteiro por fencing. O consumidor confere o mesmo conteúdo usado pelo parser, geração, hash, schema e
epoch; troca o leitor após validar o candidato, preservando consultas em andamento. Redownload não altera a data
original, a validade ou o início da retenção. Sem versão admissível, a sugestão fica indisponível e o fluxo manual permanece.

A idade usa o epoch do MMDB, nunca a data de download: aviso a partir de 72h e recusa a partir de 168h, inclusive
com atualizador parado. Retrocesso observado do relógio não pode reativar uma base expirada no processo; confiança
no relógio após reinício e ensaios de recuperação operacional permanecem requisitos antes da ativação.

O perfil de validação, limites de bytes/tempo/redes, metadados reais, autenticação e hosts de redirecionamento
exigem homologação explícita G2. Não existem valores operacionais presumidos. Primeira promoção requer aprovação
do hash; troca de produto/schema/licença e regressão de epoch exigem revisão. O armazenamento privado compartilhado
(leitura no Web, escrita no Worker), seus acessos, orçamento e relógios precisam ser verificados antes de registrar serviços.

Retenção automática, monitor independente, histórico de alertas e recuperação administrativa são a etapa 9,
ainda não implementada. Responsáveis nominais, substituto e canal de alertas seguem pendentes (G3); G5 e autorização
de implantação também permanecem abertos. Ver [preparação operacional](../../operations/ipinfo-country.md) e
[matriz de testes](../../testing/regional-matching.md#leitor-e-atualização-inativos--etapas-7-e-8).
