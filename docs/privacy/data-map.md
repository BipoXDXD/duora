# Mapa de dados pessoais

> **Decisão vigente — 19/10/2026, issue #93:** verificação etária foi retirada. Participação exige conta Active, e-mail confirmado e selfie de qualidade/presença Passed, além dos demais controles. Referências etárias remanescentes descrevem o sistema anterior ou questões jurídicas históricas; não são requisitos de implementação. Ver [ADR-0029](../architecture/adr/0029-confirmacao-email-presenca-sem-idade.md).


Finalidades, categorias de dados por tabela, base legal, fornecedores e acessos por papel. Fontes: spec 1.3,
6.4, 8, 10.2, 12, 16.3, 19.2 e 25.15; [model.md](../data/model.md) (tabelas e classes de sensibilidade);
[threat-model.md](../security/threat-model.md); fichas D-PRIV-01 (LGPD, Marco Civil da Internet), D-PRIV-02
(ECA Digital, ANPD) e D-PRIV-03 (jurisdições fora do Brasil). Prazos de guarda: [retention.md](retention.md).

> **Público e jurisdição ([ADR-0026](../architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md), 2026-10-19).** O público principal do Duora **não é brasileiro**, e o
> armazenamento fica nos EUA (VPS em Boston e backup no B2 US East); acessos de suporte e subprocessadores, a
> confirmar (J-11). O recorte inicial de produto aprovado em 2026-10-21 é EUA e Brasil (J-01 e adendo do
> [ADR-0030](../architecture/adr/0030-profile-country-subdivision.md)); habilitação e liberação operacional seguem pendentes. A
> aplicabilidade da LGPD, inclusive a quem está fora do Brasil, é a confirmar (J-02), e as referências à LGPD neste
> mapa valem só se ela se aplicar. Para as demais jurisdições, as leis prováveis estão em [Jurisdições a confirmar](#jurisdições-a-confirmar),
> como lacunas para o apoio jurídico, sem conclusão sobre aplicabilidade.

> **Este documento não declara conformidade jurídica.** É um inventário técnico para apoiar a análise de
> pessoa qualificada (spec 12.1). Bases legais estão **a definir com apoio jurídico**; fornecedores estão
> **a escolher** (spec 23.2). Sem essas definições, não há coleta de dados reais (gate "Privacidade" da spec
> 23.3). Cada dado guardado é um passivo (*Secure APIs*, cap. 11.5); o critério de design é minimizar
> (*Designing Data-Intensive Applications*, 2ª ed., cap. 14, "Privacy and Use of Data").

## Finalidades

| ID | Finalidade | Descrição | Tabelas principais |
|---|---|---|---|
| F-01 | Conta e autenticação | Criar e proteger a conta, autenticar, manter sessões revogáveis, recuperar acesso | `identity.users`, `identity.login_sessions`, `identity.auth_tickets`, `identity.user_tokens`, `identity.consent_records` |
| F-02 | Elegibilidade etária | Garantir que só adultos participem (INV-01) | `identity.age_verifications`, `identity.users.birth_date` |
| F-03 | Perfil e pareamento | Exibir perfil em contexto legítimo e formar pares mutuamente elegíveis | `profiles.*`, `events.*`, `media.*` (fotos de perfil) |
| F-04 | Experiência e comunicação | Conduzir a sala, coletar decisão privada, criar conexão e conversa | `experiences.game_*`, `social.*` |
| F-05 | Segurança interpessoal e moderação | Bloquear, receber denúncia, preservar evidência, aplicar e revisar medidas (INV-03, INV-10) | `safety.*` |
| F-06 | Notificações | Avisos de serviço, segurança e, se escolhidos, comunicações opcionais | `ops.outbox_*`, `ops.notification_preferences`, `ops.email_receipts` |
| F-07 | Segurança e operação da plataforma | Prevenir abuso e fraude, auditar ações privilegiadas, operar jobs e integrações | `ops.audit_events`, `ops.idempotency_records`, `ops.webhook_receipts`, `ops.jobs`, telemetria |
| F-08 | Direitos do titular | Exportação, exclusão, preservação e reconciliação | `privacy.*` |
| F-09 | Continuidade | Backup e restauração (cópia de tudo acima) | Repositório de backup, WAL, tombstones |
| F-10 | Obrigações de registro | Registros de acesso exigidos por lei, se aplicáveis | Armazenamento separado, a definir (spec 12.1) |

## Categorias de dados por tabela

Classes: P0 público, P1 interno, P2 pessoal, P3 pessoal sensível ou íntimo, S segredo
([model.md](../data/model.md#classificação-de-sensibilidade)). "Sensível?" indica se o dado **pode** ser dado
pessoal sensível ou de risco elevado (LGPD art. 5.º, II, se a LGPD se aplicar, J-02; as categorias equivalentes de outras
jurisdições, como a categoria especial do GDPR e os "dados sensíveis" das leis estaduais dos EUA, entram com J-03 e
J-10); a resposta final é jurídica. "Base legal" é a da LGPD; nas outras jurisdições o conceito pode não existir ou ter
outro nome (por exemplo, consentimento *opt-in* para dado sensível), a confirmar.

| Tabela | Categorias de dado | Titular | Classe | Finalidade | Sensível? | Base legal |
|---|---|---|---|---|---|---|
| `identity.users` | E-mail, hash de senha, estado da conta, data de nascimento, tentativas de login | Pessoa usuária | S / P3 | F-01, F-02 | Não, mas a data de nascimento serve à proteção etária | A definir com apoio jurídico |
| `identity.login_sessions` | Instantes de uso, resumo do dispositivo, prefixo de rede | Pessoa usuária | P2 | F-01, F-07 | Não | A definir com apoio jurídico |
| `identity.auth_tickets`, `identity.user_tokens` | Ticket de sessão, chave TOTP, códigos de recuperação | Pessoa usuária | S | F-01 | Não | A definir com apoio jurídico |
| `identity.age_verifications` | Resultado mínimo, instante da decisão, instante da suspeita de menor (`NotAdult`), referência do fornecedor, versão da política | Pessoa usuária | P3 | F-02 | Possível (biometria no fornecedor, se o método usar) | A definir com apoio jurídico |
| `identity.consent_records` | Tipo, versão, idioma explícito do documento aceito (ou desconhecido/null) e instante | Pessoa usuária | P2 | F-01, F-08 | Não | A definir com apoio jurídico |
| `profiles.profiles` | Nome de exibição, faixa etária, região aproximada (país opcional + subdivisão de primeiro nível opcional na expansão ADR-0030, inicialmente BR e as UFs existentes; campo UF legado mantido; nunca cidade, CEP, código postal nem coordenada), descrição | Pessoa usuária | P2 | F-03 | Pode conter dado sensível em texto livre (descrição) | A definir com apoio jurídico |
| `profiles.preferences` | Critérios privados de compatibilidade | Pessoa usuária | P3 | F-03 | **Provável** se incluírem gênero buscado ou orientação (vida sexual) | A definir com apoio jurídico |
| `profiles.profile_photos`, `media.*` | Fotos (originais em quarentena e derivados), hash, dimensões | Pessoa usuária e terceiros fotografados | P3 | F-03, F-05 | Imagem é dado pessoal; biometria só se houver tratamento biométrico (não previsto) | A definir com apoio jurídico |
| `events.registrations` | Inscrição, check-in, presença | Pessoa usuária | P2 | F-03 | Participar de evento de encontros pode revelar vida afetiva | A definir com apoio jurídico |
| `events.pairings`, `events.pairing_participants` | Quem foi pareado com quem | Duas pessoas | P3 | F-03, F-05 | Possível (vida afetiva) | A definir com apoio jurídico |
| `experiences.game_sessions`, `game_participants`, `game_actions` | Estado de jogo, pistas, escolhas | Duas pessoas | P3 | F-04 | Não por desenho (spec 6.3 evita perguntas íntimas) | A definir com apoio jurídico |
| `social.decisions` | Interesse ou não na outra pessoa | Pessoa que decide | P3 | F-04 | Possível (vida afetiva) | A definir com apoio jurídico |
| `social.connections`, `conversations`, `conversation_members` | Conexão entre duas pessoas, estado | Duas pessoas | P3 | F-04 | Possível | A definir com apoio jurídico |
| `social.messages` | Texto livre do chat | Remetente; terceiros mencionados | P3 | F-04, F-05 (só recorte denunciado) | **Possível** (texto livre) | A definir com apoio jurídico |
| `safety.blocks` | Quem bloqueou quem | Duas pessoas | P3 | F-05 | Não | A definir com apoio jurídico |
| `safety.reports`, `report_evidence` | Relato, categoria, recorte de mensagens, perfil ou foto | Denunciante, denunciado, terceiros | P3 | F-05 | **Possível** (relatos de assédio ou violência) | A definir com apoio jurídico |
| `safety.moderation_actions`, `appeals` | Medida, motivo, autor, recurso | Pessoa sancionada; moderador | P3 | F-05 | Possível | A definir com apoio jurídico |
| `ops.notification_preferences`, `ops.email_receipts` | Escolha de comunicação opcional; recibos de entrega | Pessoa usuária | P2 | F-06 | Não | A definir com apoio jurídico |
| `ops.audit_events` | Ator, ação, alvo, motivo, instante | Pessoa usuária, moderador, operador | P2 / P3 | F-07, F-05 | Não | A definir com apoio jurídico |
| `ops.idempotency_records`, `ops.outbox_*`, `ops.jobs` | IDs, hashes, resultados mínimos | Pessoa usuária | P1 / P2 | F-07 | Não | A definir com apoio jurídico |
| `ops.webhook_receipts` | Callback de fornecedor (verificação etária, e-mail) | Pessoa usuária | P2 / P3 | F-02, F-06 | Possível (resultado etário) | A definir com apoio jurídico |
| `privacy.export_requests`, `deletion_requests`, `deletion_steps` | Pedidos e execução de direitos | Pessoa usuária | P2 / P3 | F-08 | Não | A definir com apoio jurídico |
| `privacy.retention_holds` | Escopo e motivo de preservação | Pessoa usuária | P3 | F-05, F-08, F-10 | Possível | A definir com apoio jurídico |
| `privacy.deletion_tombstones` | HMAC do identificador excluído | Ex-usuário | P2 | F-08, F-09 | Não | A definir com apoio jurídico |
| Telemetria (logs, traces, métricas) | Rota parametrizada, status, duração, `traceId`, IDs técnicos | Pessoa usuária | P1 | F-07 | Não, por desenho (spec 19.2) | A definir com apoio jurídico |
| Registros de acesso (se aplicáveis) | Data, hora e IP de acesso | Pessoa usuária | P2 | F-10 | Não | A definir com apoio jurídico (Marco Civil, art. 15, a avaliar) |

O que **não** é coletado por desenho: documento de identidade ou biometria brutos (spec 12.2), localização
precisa (spec 1.1; a região do perfil para na subdivisão de primeiro nível; na expansão ADR-0030, as FKs limitam ao catálogo aprovado e permitem informar somente país ou nada), importação de redes sociais, telemetria comportamental com preferências identificáveis,
session replay ou gravação de digitação em telas privadas (spec 13.6), conteúdo de chat em analytics.

## Fornecedores

Todos **a escolher** (spec 23.2). A coluna "dados enviados" é o máximo permitido ao adaptador; o teste de
contrato de cada adaptador afirma esse conjunto (TM-60).

| Função | Interface | Dados enviados (máximo) | Dados recebidos | Requisitos antes de dados reais |
|---|---|---|---|---|
| Verificação etária | `IAgeEligibilityProvider` | ID de solicitação (nonce), URL de retorno fixa; o que o método exigir é coletado **pelo fornecedor**, não pelo Duora | Resultado mínimo, referência | Método proporcional, taxa de erro, contestação, acessibilidade, retenção e local de tratamento, callbacks assinados (spec 12.2, 16.3) |
| E-mail transacional | `ITransactionalEmailSender` | Endereço, modelo e variáveis neutras (sem nome de outra pessoa, trecho de mensagem ou motivo de moderação) | Recibos, bounces, reclamações | Domínio autenticado (SPF, DKIM, DMARC), retenção de logs do fornecedor, suppression (spec 16.4) |
| Armazenamento de fotos | `IObjectStore` (adaptador de filesystem) | Bytes de fotos (quarentena e derivados) e arquivo de exportação, no disco da VPS (Hostinger, Boston, EUA); nenhum terceiro além do provedor da VPS | — | Diretório do host com dono e modo restritos, montagem por área, cópia externa obrigatória (adendo do ADR-0010) |
| Backup | pgBackRest (banco e WAL) e restic (derivados das fotos), no **Backblaze B2, região US East (EUA)** | Cópia física do banco e WAL e cópia dos derivados das fotos, **cifradas no cliente** antes de sair da VPS (a Backblaze só recebe blocos cifrados; as chaves de cifra ficam na VPS e no cofre do responsável) | — | Criptografia com chave sob custódia separada, retenção, proteção contra exclusão (spec 18.3); escolhido em 2026-10-16 |
| Monitoramento | OTLP | Telemetria filtrada, sem dado privado | — | Filtragem, retenção, controle de acesso (spec 19.2) |
| Moderação assistida (condicional) | `IContentReviewProvider` | Só conteúdo denunciado ou upload em revisão, estritamente necessário | Rótulo e prioridade | Política de dados, retenção, proibição de treino, revisão humana (spec 12.3; ADR-0013) |
| Agentes de desenvolvimento | — | **Nenhum dado pessoal real**; só dados sintéticos | — | INV-12; spec 6.4, 14.3 |
| Relay da voz ao vivo (Cloudflare Realtime TURN; ADR-0028) | `ITurnCredentialIssuer` | Pedido de credencial sem dado pessoal (TTL e escopo); o navegador de cada participante envia ao relay o **IP**, metadados de conexão (horário, duração, volume) e a mídia **cifrada** (DTLS-SRTP), que a Cloudflare não decifra enquanto a VPS repassar o SDP sem adulterar (TM-78) | Username e senha TURN de ~5 min | Sub-processador de IP e metadados; contrato, retenção e local de tratamento a confirmar; alerta de gasto (ADR-0028) |
| DNS (Cloudflare) | — | Consultas DNS de quem acessa o domínio; se o registro é só DNS ou proxied (a Cloudflare veria o tráfego HTTP) é **a confirmar com o responsável** | — | Se proxied, a Cloudflare vira sub-processador de todo o tráfego HTTP e exige revisão de TLS, cache e WebSocket (spec 2.3) |
| Geração de voz, música e efeitos (origem editorial registrada; ADR-0027) | — (ferramenta offline, fora do runtime) | **Nenhum dado de participante**: só texto do catálogo revisado e prompts de música e de efeitos; voz clonada só de pessoa que assinou consentimento | Áudio gerado | Uso comercial do plano registrado em `docs/design/assets-licenses.md`; `EDITORIAL_API_KEY` só no ambiente de quem gera |

Local de tratamento e salvaguardas de cada fornecedor entram na avaliação jurídica de cada contrato (J-11).

### Local do tratamento e transferências a confirmar

O servidor de produção fica em Boston (EUA) e o backup no Backblaze B2 US East ([ADR-0026](../architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md)): o armazenamento de
**todos** os dados do Duora fica nos EUA; acessos de suporte e subprocessadores, a confirmar (J-11). Os dados de pessoas no Brasil ficam nos EUA desde o primeiro request (fato); se isso é transferência internacional (LGPD art. 33) ou coleta internacional (Resolução CD/ANPD 19/2024, a confirmar) e qual salvaguarda se aplica é a confirmar (J-02). O que antes aparecia como
exceção (só o backup) passa a ser o caso geral. Para pessoas na UE ou no Reino Unido, se houver, o enquadramento
equivalente é a confirmar em J-10.

| Destino | País | Dados | Proteção técnica | Retenção no destino | Salvaguarda |
|---|---|---|---|---|---|
| VPS de produção (Hostinger), Boston | Estados Unidos | Tudo: banco (PostgreSQL), fotos (quarentena e derivados), exportações, logs e Data Protection | TLS na borda, banco e serviços sem porta pública (spec 15.2), segredos fora do repositório, acesso administrativo por chave SSH; cifra do disco em repouso **a confirmar** com o provedor | A de cada classe em [retention.md](retention.md) | **A confirmar.** Pessoas no Brasil: se há transferência (LGPD art. 33) ou coleta internacional (Res. CD/ANPD 19/2024) e qual salvaguarda (por exemplo, cláusulas-padrão contratuais da ANPD), J-02. Pessoas na UE/Reino Unido, se houver: capítulo V do GDPR, J-10. Conferir também onde ficam o suporte e os subprocessadores da Hostinger com acesso ao host (J-11) |
| Backblaze B2, região US East (bucket próprio do Duora) | Estados Unidos | Tudo o que o banco guarda (via pgBackRest) e os derivados das fotos aprovadas (via restic); nunca a quarentena | Cifra no cliente (pgBackRest `aes-256-cbc`; restic AES-256), TLS, chave presa ao bucket e ao prefixo, File Lock `compliance` de 21 dias | Banco: janela de 14 dias + 21 de lock + 30 de lifecycle; fotos: 14 dias de snapshots + 21 + 30 ([retention.md](retention.md)) | **A confirmar**, com o mesmo enquadramento da VPS (registrado em 2026-10-16 e revisto em 2026-10-19; sem análise jurídica nesta fase, por decisão do responsável) |

A Backblaze guarda só blocos cifrados e não tem as chaves. O backup deixou de ser o único dado nos EUA: é uma
segunda cópia lá, num provedor diferente do servidor.

Limite da proteção contra exclusão das fotos: o restic deduplica, e os pacotes antigos (mais de 21 dias) que os
snapshots recentes ainda usam ficam fora do File Lock, protegidos só pelas capacidades das chaves. A mitigação
(`restic check` independente com chave só de leitura de `media/`) é lacuna registrada, sem implementação
([backup-restore.md](../operations/backup-restore.md#backup-das-fotos-restic)).

## Acessos por papel

Leitura (L), escrita (E) e sem acesso (—). "Próprio" = só dados do titular; "contexto" = só em sala, conexão
ou caso legítimo, por projeção mínima; "caso" = só casos atribuídos, com leitura auditada.

| Dado | Titular | Outra pessoa (participante) | Moderador | Admin. de produto | Operador técnico | Agente de desenvolvimento |
|---|---|---|---|---|---|---|
| E-mail, credenciais, sessões | L/E próprio (credenciais nunca legíveis) | — | — | — | — (banco não é ferramenta de atendimento) | — |
| Verificação etária | L estado mínimo | — | L caso (suspeita de menor) | — | — | — |
| Perfil exibido e fotos aprovadas | L/E próprio | L contexto | L caso | — | — | — |
| Preferências | L/E próprio | — (nunca) | — | — | — | — |
| Inscrições, pares | L próprio | — | L caso | L agregados por evento | — | — |
| Pistas e estado de jogo | L própria projeção | L própria projeção | L caso (recorte) | — | — | — |
| Decisões | L/E própria | — (nunca a decisão alheia) | — | — | — | — |
| Mensagens | L/E contexto (conversa autorizada) | L/E contexto | L caso (recorte da evidência) | — | — | — |
| Bloqueios | L/E próprios | — (nem saber que foi bloqueado) | L caso | — | — | — |
| Denúncias e evidências | L estado permitido do próprio protocolo | — | L/E caso | — | — | — |
| Medidas e recursos | L próprias; E recurso | — | L/E caso; recurso por outro moderador | — | — | — |
| Auditoria | — | — | L do próprio caso | L de ações administrativas, se a política permitir | L para incidentes, com trilha | — |
| Telemetria | — | — | — | — | L | — |
| Backup | — | — | — | — | Restore em ambiente restrito, procedimento auditado | — |

Regras transversais: negar por padrão; toda leitura de terceiro filtra por relação e bloqueio na própria
consulta (spec 11.4); acesso privilegiado é por conta separada com MFA (ADR-0017); exportação em massa por
moderação ou administração não existe (spec 8.5).

## Direitos do titular

| Direito (LGPD art. 18, se a LGPD se aplicar, J-02; equivalentes de outras jurisdições em J-03 e J-10, a confirmar no escopo jurídico) | Mecanismo técnico |
|---|---|
| Acesso e portabilidade | API-D01, D02, D04: exportação dos dados do titular, sem decisões, evidências ou dados de terceiros (spec 12.1) |
| Correção | API-P02, P03, N02 |
| Eliminação | API-D03: desativação e revogação imediatas; execução por etapas em `privacy.deletion_steps`; exceções por `retention_holds` com escopo e vencimento ([retention.md](retention.md)) |
| Revogação de consentimento, se for a base | `identity.consent_records.withdrawn_at`; efeito depende da base definida |
| Informação sobre compartilhamento | Tabela de fornecedores acima, depois de escolhidos |

## Lacunas

Pendências que este mapa não resolve; cada uma bloqueia dados reais até ter dono e decisão (spec 23.2). Os itens 1, 4, 5
e 6 citam a legislação brasileira e dependem de a LGPD e o Marco Civil se aplicarem (J-02); os demais valem para
todos. As outras jurisdições
estão em [Jurisdições a confirmar](#jurisdições-a-confirmar).

1. **Bases legais** de cada finalidade, inclusive para dados possivelmente sensíveis (preferências, mensagens,
   denúncias), que seguem o art. 11 da LGPD e não o art. 7.º.
2. **Dimensões de preferência** não estão definidas na spec; se incluírem gênero buscado ou orientação, o
   tratamento é de dado sensível e a minimização precisa ser revista ([model.md](../data/model.md#schema-profiles)).
3. **Data de nascimento** ([model.md](../data/model.md#decisões-de-modelagem-propostas), D12): confirmar se é
   necessária ou se basta a faixa etária mais o resultado da verificação.
4. **Registros de acesso:** aplicabilidade do Marco Civil (art. 15, seis meses) ao Duora, armazenamento
   separado e acesso restrito (spec 12.1, 12.4).
5. **ECA Digital:** ~~avaliar com apoio jurídico se o art. 9.º se aplica ao Duora~~. Decidido pelo dono
   (2026-10-16, issue #9): não haverá análise jurídica; a verificação etária acontece só no cadastro, sem validade
   nem reverificação por acesso ([ADR-0024](../architecture/adr/0024-fluxo-de-verificacao-etaria.md), adendo de
   2026-10-16).
6. **Registro das operações de tratamento** (LGPD art. 37) e comunicação de incidentes (art. 48): responsável
   e procedimento (spec 12.5).
7. **Fotos de terceiros** nas imagens de perfil e **terceiros mencionados** em mensagens e denúncias: tratamento
   na exportação e na exclusão.

## Jurisdições a confirmar

Decisão do dono de 2026-10-19 ([ADR-0026](../architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md)): o público principal não é brasileiro. A lista abaixo mapeia as leis
**prováveis**, para o apoio jurídico confirmar ou descartar. Nenhum item é conclusão: não diz que a lei se aplica,
nem que não se aplica, nem o que ela exige do Duora. Os nomes vêm de conhecimento geral e precisam de fonte
verificada (ficha D-PRIV-03 da spec) antes de virar requisito. Cada item bloqueia dados reais de pessoas na
jurisdição correspondente até ter decisão (gate "Privacidade", spec 23.3).

| ID | Jurisdição | Leis prováveis (a confirmar) | Pergunta para o apoio jurídico | Onde toca o Duora |
|---|---|---|---|---|
| J-01 | EUA e Brasil | — | **Recorte de produto aprovado em 2026-10-21:** EUA (50 estados + DC) e Brasil (27 UFs), conforme adendo do [ADR-0030](../architecture/adr/0030-profile-country-subdivision.md). Países ainda não habilitados por essa decisão; liberação operacional, análises de aplicabilidade e idade mínima das linhas seguintes permanecem pendentes | Elegibilidade do cadastro, país/subdivisão opcionais e campo legado de região (issue #78) |
| J-02 | Brasil | LGPD; Marco Civil; ECA Digital (decisão do dono de 2026-10-16 mantida) | Escopo territorial (art. 3.º): a LGPD se aplica ao Duora pelo local do tratamento, pela oferta a pessoas no Brasil ou pela coleta no Brasil, e alcança também quem está fora do Brasil? Se se aplicar: com servidor e backup nos EUA, qual o enquadramento (transferência internacional, art. 33, ou coleta internacional, Res. CD/ANPD 19/2024) e a salvaguarda e quem é o encarregado (art. 41)? | Tabela de transferências acima; lacunas 1, 4, 5 e 6 |
| J-03 | EUA, estados | Leis estaduais abrangentes de privacidade do consumidor (Califórnia CCPA/CPRA; Virgínia, Colorado, Connecticut, Texas, entre outras) | Os limiares de aplicabilidade (receita, número de residentes, venda de dados) alcançam o Duora? Preferências, vida afetiva e orientação são "dados sensíveis" com *opt-in*? Há avaliação de impacto obrigatória? Que direitos e prazos de resposta? | `profiles.preferences`, `social.decisions`, mensagens; direitos do titular; aviso de privacidade |
| J-04 | EUA, estados | Washington My Health My Data Act e leis parecidas; leis de biometria (Illinois BIPA, Texas CUBI) | Algum dado do Duora cai em "dado de saúde do consumidor"? O método de verificação etária do fornecedor usa biometria ou estimativa facial e, se usar, que consentimento e retenção exige? | F-02 e fornecedor de verificação etária; fotos (não há tratamento biométrico previsto) |
| J-05 | Todas (idade) | Maioridade civil por estado (18 na maioria dos estados dos EUA; 19 e 21 em alguns, a confirmar); COPPA (menores de 13, federal); leis estaduais de verificação etária e de proteção de menores online, em mudança e em litígio; ECA Digital no Brasil | Qual a idade mínima por jurisdição? A faixa etária pública começa em `18-24` (`age_band`, model.md): qual a primeira faixa onde a maioridade for 19 ou 21? Que método de verificação é aceitável em cada uma? Quais obrigações surgem ao descobrir uma conta de menor (COPPA, se menor de 13)? | `AgeCheckOutcome` e política versionada (ADR-0024, adendo de 2026-10-19); suspeita de menor (spec 12.2) |
| J-06 | EUA, estados | Leis estaduais de serviços de encontro online (por exemplo, Nova Jersey e Texas) | Quais estados exigem avisos de segurança, divulgação sobre checagem de antecedentes ou outro requisito para serviços de encontro? O formato de evento do Duora é "serviço de encontro" nessas leis? | Telas de cadastro e de evento; e-mails; documentos voltados ao usuário |
| J-07 | EUA, federal | TAKE IT DOWN Act (2025), sobre imagens íntimas publicadas sem consentimento | O Duora é plataforma coberta? Qual o prazo de remoção após pedido válido e que canal de pedido é exigido? | Fotos, denúncias e moderação (spec 12.3, 16.1) |
| J-08 | EUA, estados | Leis estaduais de notificação de incidente; normas de segurança da informação como a de Massachusetts (201 CMR 17.00) | A obrigação segue a residência da pessoa ou o local do servidor? Que prazos e destinatários de notificação? É exigido programa escrito de segurança? | Incidentes (spec 12.5); [threat-model.md](../security/threat-model.md) |
| J-09 | EUA, federal | CAN-SPAM, para mensagens comerciais por e-mail | Os avisos opcionais (F-06) são comerciais? Que identificação e descadastro exigem? Os transacionais ficam fora? | `ops.notification_preferences`, e-mails de conta |
| J-10 | UE e Reino Unido | GDPR e UK GDPR, se o serviço for oferecido a pessoas lá; Digital Services Act para plataformas online | O Duora oferece o serviço a pessoas na UE/Reino Unido? Se sim: base legal e categoria especial (vida e orientação sexual), representante na UE, transferência para os EUA (cap. V: certificação dos fornecedores no Data Privacy Framework ou cláusulas-padrão) e obrigações de notificação e ação do DSA | Todo o mapa |
| J-11 | Fornecedores | Contratos de tratamento (DPA) e lista de subprocessadores | Onde a Hostinger (empresa com sede fora dos EUA, a confirmar no contrato), a Backblaze e os futuros fornecedores de e-mail e de verificação etária tratam ou acessam dados? Há DPA e cláusulas de transferência? Se J-10 se aplicar, cada fornecedor tem certificação no Data Privacy Framework ou cláusulas-padrão (cap. V do GDPR)? | Tabela de fornecedores e de transferências; J-10 |
| J-12 | Todas | Lei de defesa do consumidor e de práticas enganosas (por exemplo, FTC Act seção 5 nos EUA) aplicada aos documentos voltados ao usuário | Termos e política de privacidade saem em PT-BR e EN-US (ADR-0026): qual versão prevalece, que lei e foro os termos escolhem, e que promessas da política viram obrigação verificável? | Documentos voltados ao usuário; `identity.consent_records` |
| J-13 | Todas | LGPD art. 3.º; GDPR art. 3(2) e art. 27; regras equivalentes das outras jurisdições | Onde fica o controlador e qual o seu estabelecimento? Sem estabelecimento no Brasil nem na UE, que critério de alcance territorial vale (oferta de serviço, monitoramento, coleta) e é preciso representante (GDPR art. 27) ou contato local? | Documentos voltados ao usuário (identificação do controlador); J-02, J-10 |

## Selfie de presença — implementação da issue #93

`identity.selfie_verifications`: ID interno, titular, estado, política e datas; referência de sessão e feedback transitórios removidos após 24h. Sem imagens, templates, idade, documentos ou scores no banco/outbox/log. A exclusão física futura de contas deve remover estas linhas; não há implementação de exclusão física no produto atual.

AWS recebe vídeo de captura diretamente do SDK; o worker obtém frame de referência em memória para brilho/nitidez/pose (DEFAULT). AuditImagesLimit=0, sem S3/OutputConfig. Prazo contratual no fornecedor, opt-out de uso secundário, DPA, região e transferências precisam ser homologados antes de Enabled=true. Expiração de sessão não prova exclusão contratual. Credenciais temporárias STS ficam apenas em memória do navegador e não vão para URL/storage/telemetria.
