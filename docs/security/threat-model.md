# Modelo de ameaças — M1

Modelo de ameaças do Duora para o marco M1 (identidade e proteção), com os fluxos de M2 que já dependem das
decisões tomadas aqui (hub, decisão privada, worker e outbox). Fontes: spec 1.2, 1.3, 7, 8, 9, 10.4–10.6, 11,
12, 16 e 20.2; [context.md](../architecture/context.md) (fronteiras F1 a F10);
[modules.md](../architecture/modules.md); ADRs [0005](../architecture/adr/0005-identity-local-cookie-ticket-persistente.md),
[0006](../architecture/adr/0006-mesma-origem-spa-api-hub.md), [0007](../architecture/adr/0007-signalr-transporta-invalidacao.md),
[0008](../architecture/adr/0008-postgresql-para-jobs-e-outbox.md), [0010](../architecture/adr/0010-object-storage-externo-leitura-via-api.md),
[0013](../architecture/adr/0013-ia-fora-do-caminho-critico.md), [0014](../architecture/adr/0014-automacao-com-gates.md),
[0016](../architecture/adr/0016-sala-e-sessao-de-jogo.md) e [0017](../architecture/adr/0017-mfa-por-papel.md).

Método: as quatro perguntas do Threat Modeling Manifesto (o que estamos construindo, o que pode dar errado, o
que faremos, fizemos bem) com STRIDE por fluxo crítico (D-SEC-06; *Secure APIs*, cap. 2.2; *Web Application
Security*, 2ª ed., cap. 24). Cada ameaça mitigada vira teste automatizado (*Secure APIs*, cap. 12.1): a tabela
final liga ameaça, mitigação e teste. Controles de classe de risco seguem OWASP API Security Top 10 (2023) e
ASVS 5.0 (D-SEC-01), sem tratá-los como checklist completo.

Documentos irmãos: [auth.md](auth.md) (sessão, MFA, CSRF e recuperação),
[model.md](../data/model.md) (constraints), [concurrency.md](../data/concurrency.md) (protocolos
transacionais), [data-map.md](../privacy/data-map.md) e [retention.md](../privacy/retention.md).

> **Estado:** especificação. Nenhum controle abaixo está implementado; a coluna de teste diz o que provará
> cada mitigação. Revisar este documento a cada novo fluxo, fornecedor ou mudança de permissão (spec 21.2).

## 1. Ativos

| ID | Ativo | Onde vive | Por que importa | Classe ([model.md](../data/model.md#classificação-de-sensibilidade)) |
|---|---|---|---|---|
| AT-01 | Credenciais: hash de senha, chave TOTP, códigos de recuperação, tokens de confirmação e reset | `identity.users`, `identity.user_tokens`, e-mail em trânsito | Tomada de conta | S |
| AT-02 | Sessões: cookie `__Host-duora`, ticket protegido, referência opaca | Navegador, `identity.auth_tickets`, `identity.login_sessions` | Sequestro de sessão; sessão revogada que volta a valer | S |
| AT-03 | Key ring de Data Protection e certificado que o protege | Volume restrito, backup sob custódia separada | Forjar cookie, antiforgery e tokens de e-mail | S |
| AT-04 | Resultado e referência da verificação etária | `identity.age_verifications`, fornecedor | INV-01: menor participando; falsificação de maioridade | P3 |
| AT-05 | Preferências privadas | `profiles.preferences` | Podem revelar orientação sexual (dado sensível); nunca no DTO de outra pessoa | P3 |
| AT-06 | Fotos: originais em quarentena e derivados | Disco da VPS (`IObjectStore` de filesystem), cópia dos derivados no B2, `media.*` | Dado pessoal; localização em EXIF; exposição após bloqueio | P3 |
| AT-07 | Decisões privadas e pistas de jogo | `social.decisions`, `experiences.game_participants` | INV-05: rejeição revelada, vantagem no jogo | P3 |
| AT-08 | Mensagens de chat | `social.messages` | Conteúdo íntimo; leitura após bloqueio (INV-03) | P3 |
| AT-09 | Denúncias, evidências e medidas de moderação | `safety.reports`, `safety.report_evidence`, `safety.moderation_actions`, `safety.appeals` | Exposição da pessoa denunciante; retaliação; INV-10 | P3 |
| AT-10 | Bloqueios | `safety.blocks` | Revelar ao bloqueado quem bloqueou; contornar bloqueio | P3 |
| AT-11 | Trilha de auditoria | `ops.audit_events` | Repúdio de ações privilegiadas; adulteração apaga rastro | P2 |
| AT-12 | Outbox, jobs e recibos de webhook | `ops.*` | Efeito duplicado, notificação a destinatário bloqueado ou excluído | P1–P2 |
| AT-13 | Tombstones de exclusão | `privacy.deletion_tombstones` e registro externo | INV-11: restore ressuscita conta excluída | P2 |
| AT-14 | Segredos de infraestrutura: connection strings, chaves de fornecedor, segredo de webhook, chaves de backup | Arquivos de segredo no host, cofre do CI | Comprometimento completo | S |
| AT-15 | Backups e WAL | Repositório externo (F7) | Cópia integral de tudo acima | S |
| AT-16 | Código, workflows, `AGENTS.md` e suítes de segurança | Repositório | Gate removido silenciosamente; backdoor (INV-12) | — |
| AT-17 | Disponibilidade da sala e da rodada | Web, worker, banco | Encontro interrompido; saída segura indisponível | — |

## 2. Atores

| ID | Ator | Acesso legítimo | Objetivo hostil plausível |
|---|---|---|---|
| AC-01 | Visitante anônimo | Páginas públicas, cadastro, login, recuperação | Enumerar contas, força bruta, spam de cadastro, CSRF de login |
| AC-02 | Participante mal-intencionado | Conta ativa e elegível | BOLA em sala/perfil/conversa/mídia, descobrir decisão alheia, contornar bloqueio, assédio, denúncia falsa em massa |
| AC-03 | Pessoa bloqueada ou ex-conexão | Conta ativa | Continuar vendo perfil, fotos ou mensagens; descobrir quem bloqueou; perseguir |
| AC-04 | Menor de idade | Nenhum para participar | Passar pela verificação etária (INV-01) |
| AC-05 | Conta comprometida | Sessão roubada ou senha vazada | Ler conversas, trocar e-mail/senha, desativar MFA, excluir a conta da vítima |
| AC-06 | Moderador | Casos atribuídos (ADR-0017: MFA obrigatório) | Curiosidade, acesso a evidência fora do caso, exportação em massa, sanção sem motivo |
| AC-07 | Administrador de produto | Catálogo, eventos e configuração funcional | Autopromoção, acesso a dados sensíveis, publicar conteúdo sem revisão |
| AC-08 | Operador técnico | Infraestrutura, releases, recuperação | Usar banco como ferramenta de atendimento; restore que reativa contas |
| AC-09 | Agente de desenvolvimento | Branch e workspace isolados, dados sintéticos (INV-12) | Ser induzido por conteúdo não confiável (issue, README, log) a ler segredos, enfraquecer gates ou fazer deploy (spec 14.3, D-SEC-02) |
| AC-10 | Fornecedor (verificação etária, e-mail, provedor da VPS, monitoramento, backup) | Dados mínimos da sua função; callbacks em `/webhooks/{provider}` | Fornecedor comprometido ou curioso: callback forjado, retenção excessiva, uso secundário, vazamento |
| AC-11 | Atacante de rede ou de cadeia de suprimentos | Nenhum | MITM sem TLS, pacote malicioso, action não fixada, imagem trocada no registry |
| AC-12 | Navegador compartilhado | Mesmo dispositivo, outra identidade (T-21) | Ver cache, fotos ou socket da identidade anterior |

Uma pessoa pode acumular papéis, mas com contas distintas (spec 1.3); [auth.md](auth.md#papéis-e-contas)
propõe que conta privilegiada nunca seja também conta de participante.

## 3. Fronteiras de confiança

As fronteiras são as de [context.md](../architecture/context.md#fronteiras-de-confiança). Esta tabela diz qual
fluxo crítico cruza cada uma.

| # | Fronteira | Fluxos críticos que a cruzam |
|---|---|---|
| F1 | Internet → Caddy | Todos os fluxos do navegador; callbacks de fornecedor; sondas |
| F2 | Caddy → Web | API, hub, webhooks, upload, stream de mídia |
| F3 | Web/Worker → PostgreSQL | Todos; protocolos de [concurrency.md](../data/concurrency.md) |
| F4 | Worker → fornecedores | E-mail (cadastro, recuperação, notificações), solicitação de verificação etária, quarentena e derivados |
| F5 | Fornecedores → `/webhooks/{provider}` | Callback de verificação etária; bounces e recibos de e-mail |
| F6 | Web → disco da VPS (fotos) | Stream de mídia aprovada (API-M04) e upload para quarentena (API-M01) |
| F7 | PostgreSQL → backup | Backup físico, WAL, tombstones e key ring |
| F8 | Collector → monitoramento | Telemetria de todos os fluxos (risco de vazamento de dado privado em log) |
| F9 | CI → registry → VPS | Release; risco de agente e de cadeia de suprimentos |
| F10 | Operador → VPS | Administração, restore, bootstrap administrativo |

Fronteiras lógicas internas, sem rede, mas com a mesma disciplina: **navegador × servidor** (nenhum estado
autoritativo no cliente, spec 6.1); **titular × terceiro** dentro da mesma conta autenticada (BOLA);
**participante × moderação × administração** (BFLA); **web × worker** (o worker não emite cookies nem publica
no hub, spec 3.1).

## 4. Superfícies

| Superfície | Rotas ou canal | Autenticação | Notas |
|---|---|---|---|
| Autenticação pública | API-A01 a A04, A06, A07 | Pública (allowlist) | Antiforgery, Origin, rate limit e anti-enumeração ([auth.md](auth.md#contrato-de-rotas-api-a01-a-a16)) |
| Conta própria | API-A05, A08 a A16, P01 a P07, N01, N02, D01 a D04 | Cookie | Reautenticação nas ações sensíveis |
| Interação entre pessoas | API-P04, E*, R*, C*, S*, M04 | Cookie + elegibilidade | Autorização por relação, estado e bloqueio em cada operação |
| Upload | API-M01 | Cookie | Stream, limites, quarentena (spec 16.1) |
| Hub | `/hubs/updates` (WSS) | Cookie no handshake | Origin, limites, fechamento na revogação (spec 9) |
| Administração e moderação | API-ADM01 a ADM20 | Cookie de conta privilegiada + MFA | Permissão por caso, step-up, auditoria |
| Webhooks | `/webhooks/{provider}` | Assinatura do fornecedor | Sem cookie nem antiforgery; replay protection (spec 16.4) |
| Links de e-mail | Confirmação e recuperação | Token de uso controlado | Destino é a SPA; a ação é `POST` (scanner não confirma) |
| Saúde | `/health/live`, `/health/ready` | Interno | Sem detalhe de dependências |
| Operação | SSH, executor de deploy, restore | Chave + origem restrita | Fora da API; F9, F10 |
| Desenvolvimento | Repositório, CI, workspace do agente | GitHub + branch protegida | INV-12; T-12 |

## 5. STRIDE por fluxo crítico

Legenda: **S** spoofing, **T** tampering, **R** repudiation, **I** information disclosure, **D** denial of
service, **E** elevation of privilege. Cada linha cita a ameaça (TM-xx) detalhada na seção 6.

### 5.1 Cadastro e login (API-A01 a A04)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Força bruta e credential stuffing em `POST /auth/login`, trocando de IP | TM-01 |
| S | CSRF de login: site hostil autentica a vítima numa conta do atacante (login CSRF) | TM-02 |
| S | Session fixation: sessão anônima ou anterior reaproveitada depois do login | TM-03 |
| T | Overposting no cadastro (`role`, `state`, `emailConfirmed`, `isAdult`) | TM-04 |
| R | Login, falha e bloqueio de conta sem registro de quem, quando e de onde | TM-05 |
| I | Enumeração de contas por mensagem, status ou tempo de resposta diferentes no cadastro e no login | TM-06 |
| I | Senha, token ou cookie em log, trace ou mensagem de erro | TM-07 |
| D | Cadastro em massa, envio de e-mail em massa (custo e reputação do domínio) | TM-08 |
| D | Lockout abusivo: atacante trava a conta da vítima errando a senha | TM-09 |
| E | Conta `PendingEmail`/`PendingEligibility` acessa salas ou perfis alheios | TM-10 |
| S | Pré-sequestro: atacante cadastra o e-mail da vítima e a conta fica com a senha dele quando a vítima confirma | TM-66 |
| S | Endereço parecido (Unicode) que coincide na normalização acha a conta de outra pessoa e recebe o link de reset | TM-67 |

### 5.2 Sessão e revogação (API-A05, A08 a A10, A13, A16; hub)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Cookie roubado usado depois de logout, troca de senha, suspensão ou exclusão | TM-11 |
| S | Cookie forjado com key ring vazado ou recriado por efemeridade do container | TM-12 |
| T | Sessão estendida além do limite absoluto por sliding expiration | TM-13 |
| R | Revogação sem trilha (quem revogou, motivo) | TM-05 |
| I | Lista de sessões revela IP completo ou user agent detalhado de outra pessoa | TM-14 |
| I | Navegador compartilhado: cache, fotos ou socket de A aparecem para B | TM-15 |
| D | Ticket store indisponível derruba todas as requisições autenticadas | TM-16 |
| E | Sessão revogada mantém hub conectado recebendo notificações | TM-17 |
| E | Sessão de participante usada em rota administrativa; sessão administrativa sem MFA | TM-18 |

### 5.3 Recuperação de conta (API-A06, A07; e-mail)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Token de reset adivinhado, reutilizado ou vazado em `Referer`, log ou histórico | TM-19 |
| S | Reset troca a senha mas sessões do atacante continuam válidas | TM-11 |
| T | Link de reset com destino de retorno arbitrário (open redirect) | TM-20 |
| R | Troca de senha sem notificação ao titular | TM-21 |
| I | Resposta diferente para e-mail cadastrado e não cadastrado | TM-06 |
| D | Inundação de e-mails de reset para a vítima | TM-08 |
| E | Reset contorna MFA de conta privilegiada | TM-22 |

### 5.4 Bloqueio (API-S01 a S03)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Bloquear em nome de outra pessoa (blocker vindo do corpo) | TM-04 |
| T | Bloqueio concorrente com envio de mensagem, pareamento ou leitura: operação autorizada depois do commit do bloqueio | TM-23 |
| R | Desbloqueio e bloqueio sem trilha para a moderação | TM-05 |
| I | Bloqueado descobre que foi bloqueado (erro específico, 403 em vez de indisponibilidade neutra, contagem) | TM-24 |
| I | Foto, perfil ou mensagem alcançados por URL antiga, cache ou resposta idempotente armazenada | TM-25 |
| D | Bloqueios em massa para forçar locks longos ou degradar pareamento | TM-26 |
| E | Desbloqueio reabre conexão, conversa ou decisão automaticamente | TM-27 |

### 5.5 Denúncia e moderação (API-S04 a S06; API-ADM11 a ADM15)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Denúncia em nome de outra pessoa; recurso de sanção alheia | TM-04, TM-28 |
| T | Evidência adulterada ou apagada depois da denúncia (mensagem retida só pela retenção do chat) | TM-29 |
| R | Moderador nega ter lido evidência ou aplicado medida | TM-30 |
| I | Denunciado descobre quem denunciou; denunciante vê dados internos da investigação | TM-31 |
| I | Moderador lê evidência de caso não atribuído; exportação em massa | TM-32 |
| D | Denúncias falsas em massa para soterrar a fila ou provocar suspensão automática | TM-33 |
| E | Sanção permanente aplicada só por classificador; recurso revisado pelo mesmo autor | TM-34 |

### 5.6 Verificação etária com callback de fornecedor (API-P05, P06; `/webhooks/{provider}`)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Navegador envia `isAdult: true` ou resultado do fornecedor | TM-35 |
| S | Callback forjado ou de outro fornecedor aprova uma conta | TM-36 |
| T | Replay de callback antigo aprovado para outra solicitação ou conta | TM-36 |
| R | Resultado sem ligação verificável a usuário, procedimento, política e instante da decisão | TM-37 |
| I | Documento ou biometria brutos guardados pelo Duora; inferências do fornecedor expostas em API-P06 | TM-38 |
| D | Fornecedor lento ou fora do ar trava o worker ou o cadastro | TM-39 |
| E | Indisponibilidade do fornecedor libera participação por autodeclaração; suspeita de menor volta a `Active` por decurso de prazo | TM-40 |

### 5.7 Upload de foto (API-M01 a M04)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Upload em nome de outra conta (owner vindo do corpo) | TM-04 |
| T | Arquivo poliglota, SVG/HTML, nome de arquivo usado como caminho | TM-41 |
| I | EXIF com localização publicado; chave interna do bucket ou URL assinada em DTO ou log | TM-42 |
| I | Foto de perfil bloqueado, excluído ou reprovado servida por URL antiga ou cache | TM-25 |
| D | Decompression bomb, imagem gigante, uploads pendentes em massa | TM-43 |
| E | Original não verificado servido antes de `Approved` | TM-44 |

### 5.8 Hub SignalR (`/hubs/updates`)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Cross-site WebSocket hijacking: página hostil abre o hub com o cookie da vítima | TM-45 |
| T | Método genérico (`JoinGroup`, `SendToUser`) usado para assinar sala ou conversa alheia | TM-46 |
| I | Payload de notificação com dado privado (texto, "a outra pessoa aceitou", motivo de bloqueio) | TM-47 |
| D | Conexões em excesso por conta, mensagens grandes, conexões sem autenticação válida | TM-48 |
| E | Sessão revogada ou bloqueio recente mantém assinatura ativa | TM-17 |

### 5.9 Decisão privada (API-R05 a R07; M2)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Decidir em nome da outra pessoa ou em sala da qual não participa | TM-49 |
| T | Duas decisões positivas concorrentes criam zero ou duas conexões; replay de decisão antiga reabre conexão encerrada | TM-50 |
| I | Resposta, erro, notificação ou tempo revelam decisão negativa, horário de resposta ou expiração como rejeição | TM-51 |
| E | Decisão após bloqueio, suspensão ou fim da janela cria conexão | TM-50 |

### 5.10 Worker e outbox

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Processo zumbi com lease vencido conclui job depois que outro assumiu | TM-52 |
| T | Dual write: efeito no banco sem intenção na outbox, ou e-mail enviado sem commit | TM-53 |
| R | Reprocessamento manual sem trilha nem revalidação | TM-54 |
| I | Notificação enviada a destinatário bloqueado, excluído ou sem preferência; e-mail com conteúdo íntimo no assunto | TM-55 |
| I | Payload da outbox com dado privado copiado para log ou painel | TM-07 |
| D | Fila envenenada (poison message), retry storm contra fornecedor, idade crescente sem alerta | TM-56 |
| E | Worker com capacidade de emitir cookie ou publicar no hub por chave compartilhada | TM-57 |

### 5.11 Transversais (agente, fornecedor, infraestrutura)

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S/E | Agente de desenvolvimento induzido por issue, log ou README a ler segredo, enfraquecer teste ou fazer deploy | TM-58 |
| T | Dependência ou action de terceiro maliciosa; imagem trocada por tag mutável | TM-59 |
| I | Fornecedor retém ou usa dados além do contrato | TM-60 |
| I | Backup restaurado reativa conta excluída ou sessão revogada | TM-61 |
| E | Mass assignment, BOLA e BFLA nas rotas de perfil, sala e administração | TM-04, TM-62, TM-18 |
| I | Erro com stack trace, SQL ou nome de classe | TM-63 |

### 5.12 Voz ao vivo na sala

Decisão do responsável de 2026-10-19: WebRTC P2P com `iceTransportPolicy: "relay"` nos dois lados e
Cloudflare Realtime TURN ([ADR-0028](../architecture/adr/0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md)). Credencial por API-R08, sinalização pelo hub
(adendo ao ADR-0007), nenhuma mídia na VPS. Voz identifica: o livro de
áudio registra hostilidade contra jogadoras reconhecidas pela voz (*The Game Audio Strategy Guide*, cap. 12,
p. 383).

| STRIDE | Ameaça | Ref. |
|---|---|---|
| S | Terceiro, conta não elegível ou participante de outra sala entra na voz com credencial forjada, vazada ou reaproveitada; uma pessoa ouve a outra sem que ela tenha entrado na voz | TM-68 |
| S | Página hostil obtém a credencial de voz com o cookie da vítima | TM-75 |
| I | IP de uma pessoa exposto à outra por candidato ICE direto | TM-69 |
| I | Áudio gravado, transcrito, logado ou enviado a LLM, moderação ou fornecedor | TM-70 |
| I | Microfone aberto sem gesto (permissão pedida ao entrar, captura que continua depois de "desligar") | TM-71 |
| I | Telemetria ou metadados da voz que identificam a pessoa (endereço, impressão de voz) | TM-76 |
| E | Voz continua depois de bloqueio, saída, sala abortada, medida de moderação, fim da sala ou revogação da sessão (INV-03) | TM-72 |
| R | Assédio por voz sem registro que sustente a denúncia | TM-73 |
| D | Relay usado como proxy UDP ou para esgotar banda; sinalização em rajada | TM-74 |
| D | Gasto da conta da Cloudflare inflado por abuso | TM-79 |
| T | VPS comprometida troca a fingerprint DTLS no SDP e se põe no meio da mídia | TM-78 |
| E | Menor de idade alcança a voz ao vivo | TM-77 |

## 6. Ameaça → mitigação → teste

Convenções:

- **Teste:** `T-xx` quando a matriz da spec 20.2 cobre a ameaça ([acceptance.md](../testing/acceptance.md));
  `a criar: NomeDoTeste` quando não há cenário T-xx correspondente. Nomes em inglês, no estilo dos testes já
  existentes (frase que descreve o comportamento). Um teste de acesso afirma o status **e** que o corpo não
  traz dado alheio.
- **Marco:** M1 salvo indicação; fluxos de sala, decisão e chat entram no M2 e já ficam registrados aqui.
- Status codes seguem a spec 8.1 (`401` sem sessão, `403` quando a existência pode ser conhecida, `404` para
  esconder recurso de terceiro).

| ID | Ameaça | Mitigação (fonte) | Teste |
|---|---|---|---|
| TM-01 | Força bruta e credential stuffing | Lockout por conta no banco (Identity), crescente e temporário, contando falhas de senha e de MFA; rate limit por IP em memória; `429` + `Retry-After`; hash de senha do framework ([auth.md](auth.md#rate-limit-e-lockout); spec 11.4; *Secure APIs* cap. 5.1) | `SecondFactorHardeningTests` (falhas paralelas todas contadas com a conta travada; TOTP de uso único por `last_totp_step`); a criar: `LoginIsThrottledPerAccountEvenWhenSourceIpChanges`; a criar: `LoginIsThrottledPerSourceIp` |
| TM-02 | CSRF de login e de qualquer mutação | Antiforgery em toda rota cookie que altera estado, inclusive login e logout; checagem de `Origin`/`Sec-Fetch-Site`; `SameSite=Lax` como camada extra ([auth.md](auth.md#csrf); spec 11.3; *Web Application Security* caps. 11 e 29) | T-13 |
| TM-03 | Session fixation | Nova sessão (novo ticket e nova referência) a cada login, reautenticação de privilégio e troca de senha; sessão anterior revogada ([auth.md](auth.md#fixação-e-renovação)) | a criar: `LoginIssuesNewSessionReferenceAndRevokesPrevious` |
| TM-04 | Mass assignment e identidade vinda do corpo | DTOs de escrita com allowlist; campo desconhecido → `400`; `userId`, `senderId`, `blockerId`, `reporterId`, `ownerId`, papel, estado e resultado etário sempre da sessão ou do servidor (spec 8.1, 11.4) | a criar: `WriteEndpointsRejectUnknownFieldsAndLeaveDatabaseUnchanged` (parametrizado por rota); S01: `BlockEndpointTests.BodyIsRejectedAndNothingIsWritten` |
| TM-05 | Repúdio de ações de conta e de segurança | `ops.audit_events` append-only (sem `UPDATE`/`DELETE` para a conta da aplicação) para login, falha, lockout, troca de senha/MFA, revogação, bloqueio, desbloqueio, denúncia e toda ação privilegiada ([model.md](../data/model.md#t-ops-audit-events)) | parcial: MFA, reautenticação e troca de senha auditados (`MfaEnrollmentTests.EnrollmentAndActivationAreAudited`, asserts em `MfaLoginTests` e `ReauthenticationTests`); login (inclusive segundo fator ausente em A04/A13), falha de senha em A04/A13/A16, início do bloqueio, tentativa durante o bloqueio, desbloqueio, reset, logout e revogação de sessão, também sob pedidos simultâneos, com `correlation_id` que o cliente não escolhe: `LoginAuditTests`; login sem conta confirmada não grava linha (sem ator legítimo; métrica `identity.login.failed_unknown_account` e log sem e-mail nem IP) ([auth.md](auth.md#auditoria-de-login-e-de-sessão)); falta a origem (prefixo de rede), sem coluna na tabela; `AuditPrivilegesTests.AppRoleCanOnlyInsertAndSelectAuditEvents`; `AuditTrailTests` (registro na transação do efeito) |
| TM-06 | Enumeração de contas | Cadastro e reset respondem `202` com corpo idêntico exista ou não a conta; login com erro único; e-mail ao titular explica o caso. Contra o canal de tempo: cadastro e pedido de reset fazem o mesmo trabalho (mesmos comandos SQL, um hash no cadastro, uma linha de outbox) em todos os casos; login com falha custa sempre um hash e respeita um piso de latência com jitter ([auth.md](auth.md#anti-enumeração); *Secure APIs* cap. 4.4) | `RegistrationTests.RegistrationIsIndistinguishableForAnEmailThatAlreadyHasAnAccount`; `LoginTests.WrongPasswordAndUnknownEmailGetTheSameResponse`; `PasswordResetTests.ResetRequestIsIndistinguishableForUnknownEmail`; `EnumerationTimingTests` (trabalho igual por caminho e piso do login) |
| TM-07 | Segredo ou dado privado em log, trace ou erro | Allowlist de headers logáveis; redação de cookie, token, senha, código MFA, URL assinada, corpo de chat e preferências; canário nos testes (spec 19.2; D-SEC-05; *Secure APIs* cap. 11.5) | a criar: `CanaryValuesNeverAppearInResponsesHeadersOrCapturedLogs` |
| TM-08 | Abuso de envio de e-mail | Rate limit por IP e por e-mail normalizado (hash) em cadastro, reenvio de confirmação e reset; outbox com deduplicação por destinatário e janela ([auth.md](auth.md#rate-limit-e-lockout)) | a criar: `PasswordResetEmailsAreCappedPerAddressWindow` |
| TM-09 | Lockout abusivo da vítima | Bloqueio temporário e crescente (não permanente); aviso ao titular; recuperação por e-mail continua disponível; limite por IP pega a origem antes da conta ([auth.md](auth.md#rate-limit-e-lockout)) | a criar: `AccountLockoutExpiresAndPasswordResetStillWorks` |
| TM-10 | Conta não elegível participa | Política de autorização `Eligible` em toda rota de interação; estado revalidado no comando, não só na claim; conta restrita só vê a própria conta, segurança e recurso (spec 8.2; INV-01) | T-01 |
| TM-11 | Cookie válido após revogação | `ITicketStore` consulta `login_sessions` (revogação, ociosa, absoluta, stamp) a cada requisição; troca de senha, reset, suspensão e exclusão revogam todas as sessões ([auth.md](auth.md#revogação)) | T-14; a criar: `PasswordResetRevokesAllExistingSessions` |
| TM-12 | Cookie forjado ou invalidado por key ring | Key ring persistente em volume restrito, protegido por certificado, nome de aplicação estável por ambiente, homologação e produção separadas, backup sob custódia separada ([auth.md](auth.md#data-protection); spec 11.2) | T-15 |
| TM-13 | Sessão além do absoluto | Expiração ociosa recalculada como `min(agora + ociosa, absoluta)`; absoluta nunca muda depois do login ([auth.md](auth.md#expiração)) | a criar: `SlidingRenewalNeverExceedsAbsoluteExpiry` (com `FakeTimeProvider`) |
| TM-14 | Lista de sessões vaza dados | API-A09 mostra só sessões da própria conta, com descrição mínima (navegador/SO resumidos, prefixo de rede, criada/vista por último); detalhes sob reautenticação | a criar: `SessionListShowsOnlyOwnSessionsWithMinimalFields` |
| TM-15 | Navegador compartilhado | Logout revoga no servidor, fecha o hub, limpa cache do TanStack Query por identidade e renova o token antiforgery (spec 8.2, 13.5) | T-21 |
| TM-16 | Ticket store indisponível | Banco indisponível falha fechado (`503` sem detalhe), nunca aceita ticket não verificado; readiness reflete o banco ([auth.md](auth.md#ticket-no-servidor)) | a criar: `AuthenticatedRequestFailsClosedWhenTicketStoreIsUnavailable` |
| TM-17 | Hub sobrevive à revogação ou ao bloqueio | Registro de conexões por sessão no processo web; revogação fecha as conexões da sessão; `CloseOnAuthenticationExpiration`; bloqueio revoga assinaturas do par; acompanhamento reautorizado a cada assinatura (spec 9.2) | T-14; T-03 (ramo hub, M2) |
| TM-18 | BFLA e sessão administrativa fraca | Contas privilegiadas separadas; MFA confirmado exigido para emitir sessão administrativa; permissão específica por rota `/admin/*`; step-up nas ações críticas; 15 min / 8 h (ADR-0017; spec 8.5) | `AdminRouteTests` (enumera as rotas `/admin`: sem sessão 401, participante 403, privilegiado sem MFA 403 `MFA_ENROLLMENT_REQUIRED`, step-up por TOTP), `AdminMfaTests.APrivilegedSessionWithoutMfaIsDeniedEverywhereElse`, `AdminSessionTests` (15 min / 8 h), `RoleSeparationTests`; verificação ADR-0017 em [acceptance.md](../testing/acceptance.md#verificações-derivadas-de-adrs) |
| TM-19 | Token de reset fraco ou vazado | Token do framework (Data Protection, ligado ao security stamp, vida curta), entregue no fragmento da URL da SPA e enviado por `POST`; uso único na prática pela troca de stamp; `Referrer-Policy: no-referrer` ([auth.md](auth.md#recuperação-de-senha)) | a criar: `PasswordResetTokenCannotBeReusedAfterSuccessfulReset`; a criar: `ExpiredPasswordResetTokenIsRejected` |
| TM-20 | Open redirect | Retorno só para caminhos internos de uma allowlist; nunca URL absoluta vinda de parâmetro (spec 11.1) | a criar: `ReturnUrlOutsideAllowlistIsIgnored` |
| TM-21 | Troca de credencial sem aviso | E-mail neutro ao titular em troca de senha, reset, ativação/desativação de MFA, novos códigos de recuperação e reemissão da senha temporária de conta privilegiada pelo terminal (`admin grant --reissue`, aviso `PasswordReset`, decisão do dono na issue #68), sem segredo, gravado na outbox na transação da mudança (spec 8.2; [auth.md](auth.md#avisos-de-mudança-de-credencial), [bootstrap](auth.md#bootstrap-administrativo-d4)) | `CredentialChangeNotificationTests` (um aviso por mudança, nenhum na tentativa sem efeito, idioma en, canário de senha, token e códigos fora do payload); `CredentialChangeRollbackTests` (falha depois de gravar o aviso, inclusive na reemissão: nem aviso, nem mudança, nem revogação, nem auditoria); `AdminBootstrapTests.ReissueNotifiesTheHolderThatThePasswordWasReset` e `AReissuedPasswordAppearsOnlyInTheOperatorTerminalNotInTheHoldersNotice`; `SecurityNoticeDispatchTests` (texto e instante em UTC no worker, aviso com token recusado, todo aviso que o gravador grava é entregue pelo leitor); `SecurityNoticeEmailsTests` |
| TM-22 | Reset contorna MFA de conta privilegiada | Reset de conta privilegiada não emite sessão nem remove MFA; recuperação de MFA privilegiado é procedimento auditado fora da API ([auth.md](auth.md#mfa)) | `AdminMfaTests.PasswordResetDoesNotBypassMfaForPrivilegedAccount` |
| TM-23 | Corrida entre bloqueio e interação | Bloqueio e toda interação trancam `user_guards` em ordem de UUID antes de sala/conversa; envio, leitura e pareamento revalidam o bloqueio dentro da transação ([concurrency.md](../data/concurrency.md#c2-bloquear-vs-enviar-mensagem-ler-ou-parear)) | T-03 |
| TM-24 | Bloqueado descobre o bloqueio | Resposta neutra de indisponibilidade igual a "recurso inexistente" (`404` com código genérico); API-S01 não revela estado do alvo; nada no hub (spec 7.3) | `BlockEndpointTests.ResponseIsTheSameWhateverTheTargetState` (S01: alvo novo, inexistente, já bloqueado ou que bloqueou antes → mesmo `204` vazio, decisão G3); `BlockSideChannelTests` (mesmos comandos SQL para primeiro bloqueio, repetição, alvo inexistente e UUID vazio; não prova ausência de oráculo de tempo). **Risco residual (oráculo de lock):** alvo inexistente não tem guarda, então o pedido nunca espera por ela. Vetor: o atacante com duas contas manda, ao mesmo tempo, os dois bloqueios do mesmo id suspeito; se a conta existe, os dois serializam na guarda do alvo e um deles demora a mais o tempo de uma transação curta (milissegundos). Custo: duas contas elegíveis, pedidos simultâneos e medição repetida contra o ruído da rede; cada sonda **bloqueia de fato** a conta, se existir, e fica auditada (`safety.block_created`). Teto: 30 sondas por conta por hora (G6); ids são UUIDv7, com 74 bits aleatórios, então serve para confirmar um id já conhecido, nunca para varrer. Aceito no M1; fechar exigiria guarda para todo UUID ou lock consultivo por id (`pg_advisory_xact_lock`), a reavaliar se o vetor aparecer em monitoramento; a criar: `BlockedUserSeesSameResponseAsForNonexistentProfile` (P04, com o Profiles) |
| TM-25 | Conteúdo alcançado depois de bloqueio, exclusão ou reprovação | API-M04 e API-P04 revalidam relação, bloqueio e estado do asset a cada leitura; `Cache-Control: no-store`; replay idempotente reautoriza antes de devolver (spec 8.7, 16.1; ADR-0010) | T-26; T-27 |
| TM-26 | Bloqueios em massa | Rate limit por conta em API-S01; transação curta com lock só das duas guardas; sem chamada externa sob lock | `BlockRateLimitTests` (30/h por conta em memória, G6; outra sessão da mesma conta também recebe `429`); `BlockConcurrencyTests` (lock só das duas guardas, `lock_timeout` de 2 s) |
| TM-27 | Desbloqueio restaura interação | Desbloqueio só remove a linha de bloqueio; conexão continua `Blocked`/`Ended`, decisões e salas não voltam (spec 7.3) | a criar: `UnblockDoesNotReopenConnectionOrConversation` (M2) |
| TM-28 | Recurso de sanção alheia | API-S06 filtra por `actionId` **e** sujeito = titular; alheio → `404` | a criar: `AppealOfAnotherUsersModerationActionReturns404` |
| TM-29 | Evidência adulterada ou perdida | Denúncia copia o recorte autorizado para `report_evidence` com hash SHA-256 e retenção própria, independente da retenção do chat; evidência é imutável (sem `UPDATE` de conteúdo) ([model.md](../data/model.md#t-safety-report-evidence)) | a criar: `ReportEvidenceSurvivesSourceMessageRetention`; a criar: `ReportEvidenceContentIsImmutable` |
| TM-30 | Repúdio de moderação | Toda leitura de evidência e toda decisão gravam auditoria com moderador, caso, motivo e versão da política (spec 12.3) | T-10 |
| TM-31 | Exposição da pessoa denunciante | Denunciado nunca recebe identidade do denunciante; API-S05 mostra só estado permitido; denúncia não bloqueia automaticamente (spec 7.3, 8.4) | a criar: `ReportStatusExposesOnlyAllowedFields` (conjunto exato de chaves) |
| TM-32 | Moderador fora do caso | Acesso por atribuição (`assigned_moderator_id`), sem listagem global de evidências nem exportação; leitura auditada (spec 8.5) | T-10; a criar: `ModeratorCannotReadEvidenceOfUnassignedCase` |
| TM-33 | Denúncias em massa | Rate limit por conta e por alvo; denúncia não gera sanção automática; triagem prioriza, humano decide (spec 12.3) | a criar: `ReportsDoNotTriggerAutomaticSanction`; a criar: `ReportEndpointIsRateLimitedPerReporter` |
| TM-34 | Sanção sem humano; recurso não independente | Sanção permanente exige autor humano; recurso revisado por moderador diferente do autor da medida (spec 12.3) | T-10; a criar: `AppealCannotBeDecidedByOriginalAuthor` |
| TM-35 | Resultado etário vindo do navegador | API-P05 só cria solicitação; nenhum DTO aceita resultado; estado muda só por callback autenticado ou consulta do worker ao fornecedor (spec 12.2) | `AgeVerificationRequestTests.AgeVerificationRequestIgnoresClientSuppliedResult` (corpo com `isAdult`, `result`, `state`, `outcome` ou `expiresAt` → `400`, banco inalterado) |
| TM-36 | Callback forjado ou repetido | Assinatura sobre os bytes recebidos, timestamp com janela, `webhook_receipts` único por `(provider, provider_event_id)` gravado antes de processar, associação a solicitação criada pelo servidor (nonce/referência), limite de tamanho (spec 16.4) | T-17; `AgeWebhookTests.WebhookWithInvalidSignatureIsRejectedWithoutSideEffects`, `WebhookReplayOutsideTimestampWindowIsRejected`, `CallbackNotMatchingAServerCreatedRequestChangesNothing`, `DuplicateCallbackHasASingleEffect`, `TwoConcurrentCallbacksResultInASingleTransition`, `TheSameCallbackTwiceConcurrentlyIsRecordedOnce`, `BodyOfExactly64KiBIsAcceptedAndOneByteMoreIsRefused` |
| TM-37 | Resultado sem rastreabilidade | `age_verifications` guarda usuário, fornecedor, referência, versão da política e instante da decisão; a verificação adulta não tem validade (verificação única no cadastro, ADR-0024, adendo de 2026-10-16); transições auditadas ([model.md](../data/model.md#t-identity-age-verifications)) | `AgeWebhookTests.AgeVerificationResultRecordsPolicyVersionWithoutExpiry` |
| TM-38 | Documento ou biometria no Duora; inferências expostas | Nenhum campo para documento ou imagem; API-P06 devolve estado mínimo e próximo passo (spec 12.1, 12.2) | `AgeVerificationStatusTests.AgeVerificationStatusExposesOnlyStateAndNextStep`, `FlowUrlOutsideTheAllowlistIsNeverShown` |
| TM-39 | Fornecedor lento | Chamada só no worker, via outbox, com timeout, retry por backoff, teto de tentativas e uma chamada por vez por worker; nada no request do usuário (spec 16.4; ADR-0024). O adaptador real usa `IHttpClientFactory` com circuit breaker. API-P05 com rate limit por conta e por IP (`429` com `Retry-After`) e teto de 3 solicitações criadas por conta numa janela móvel de 7 dias (decidido), contado no banco sob lock da conta; rate limit com valor inicial a medir no piloto (adendo de 2026-10-19 do ADR-0024, que substitui o risco aceito na revisão do #39). Webhook sem rate limit próprio, risco aceito (assinatura conferida antes de qualquer escrita, corpo limitado a 64 KiB) | T-17: `AgeVerificationProviderDispatchTests` (lento, falhando, esgotado); a criar: `AgeVerificationRequestIsRateLimitedPerAccount`, `AgeVerificationRequestIsRateLimitedPerIp`, `AgeVerificationRequestsAboveTheWeeklyCapAreRefused`, `ConcurrentRequestsAtTheCapCreateOnlyOne` |
| TM-40 | Liberação indevida | Indisponibilidade mantém `PendingEligibility`; suspeita de menor → `Restricted` e revisão, mesmo para quem já é adulta verificada (`NotAdult` tardio); nenhuma transição automática para `Active` por prazo; a verificação adulta não vence, e o tempo só faz uma solicitação abandonada dar lugar a uma nova (spec 4.2, 12.2; ADR-0024, adendo de 2026-10-16) | T-17; T-01; `AgeEligibilityPropertyTests.ElapsedTimeNeverGrantsEligibility`; `AgeWebhookTests.LateNotAdultRestrictsEvenAVerifiedAdult` |
| TM-41 | Arquivo malicioso | Assinatura real do tipo, allowlist JPEG/PNG/WebP estáticos, reencodar, nome do cliente nunca vira chave; chave aleatória gerada pelo servidor (spec 16.1; D-SEC-04) | T-16 |
| TM-42 | Metadados e chaves expostos | Remoção de EXIF na normalização; DTO de mídia só com `assetId`; chave de bucket nunca em DTO nem log (ADR-0010) | T-16; `MediaResponsesNeverContainStorageKeys` |
| TM-43 | Decompression bomb e uploads em massa | Limite de bytes no stream, de pixels decodificados e de frames; teto de uploads pendentes por conta (guarda `media.upload_guards`); rate limit de envio por conta e por IP (20/h, hipótese) com `429` e `Retry-After`; taxa mínima do corpo na rota contra cliente lento; fila de processamento limitada (concorrência 1) (spec 4.3, 11.5, 16.1) | T-16; `PendingUploadsPerAccountAreCapped`, `PhotoUploadRateLimitTests`, `KestrelPhotoUploadTests` |
| TM-44 | Original servido antes da aprovação | API-M04 só serve `media_variants` de asset `Approved`; quarentena em bucket/prefixo separado sem rota de leitura | T-16; T-26 |
| TM-45 | Cross-site WebSocket hijacking | `Origin` validado na negociação e no upgrade; cookie `SameSite=Lax` (spec 9.3, 11.3) | a criar: `HubRejectsNegotiationFromForeignOrigin` |
| TM-46 | Assinatura de recurso alheio | Hub sem métodos genéricos; acompanhar sala/conversa passa pela mesma autorização da leitura (ADR-0007) | a criar: `HubExposesNoGenericGroupOrSendMethods` (enumera métodos públicos); a criar: `HubSubscriptionToForeignRoomIsDenied` (M2) |
| TM-47 | Dado privado em notificação | Notificação só com `eventId`, `eventType`, `schemaVersion`, `resourceId`, `resourceVersion`, `occurredAt` (spec 9.1) | T-05 |
| TM-48 | Abuso do hub | Teto de conexões por sessão e por conta, tamanho máximo de mensagem, tempo máximo sem autenticação válida (spec 9.3) | a criar: `HubRefusesConnectionsBeyondPerSessionLimit` |
| TM-49 | Decisão em sala alheia | API-R05/R06/R07 filtram por `roomId` e participante = sessão; alheio → `404` (ADR-0016) | T-05 (M2) |
| TM-50 | Corrida e replay na decisão | Protocolo da decisão: idempotência → guardas do par → guarda da relação → sessão de jogo; `UNIQUE (user_low, user_high)` em conexões; conexão encerrada nunca reabre ([concurrency.md](../data/concurrency.md#c1-duas-decisões-positivas)) | T-04; T-20 |
| TM-51 | Vazamento da decisão alheia | Projeção da decisão só com a própria escolha, janela e conexão quando existir; expiração neutra; notificação sem conteúdo (spec 7.1) | T-05 (M2) |
| TM-52 | Zumbi conclui job | Lease com fencing token por linha; conclusão `UPDATE ... WHERE fencing_token = $token`; 0 linhas = resultado descartado ([concurrency.md](../data/concurrency.md#outbox-lease-e-fencing)) | T-08 |
| TM-53 | Dual write | Outbox no mesmo commit da transação de negócio; nenhuma chamada externa dentro da transação (ADR-0008; *Designing Data-Intensive Applications*, 2ª ed., cap. 12) | T-08; a criar: `BusinessTransactionRollbackDiscardsOutboxEvent` |
| TM-54 | Reprocessamento sem controle | API-ADM20 exige permissão, chave de operação, revalidação de prazo, destinatário e autorização; auditado (spec 8.5) | a criar: `JobRetryRevalidatesRecipientAndIsAudited` (M3) |
| TM-55 | Notificação indevida | Revalidar destinatário (estado da conta, bloqueio, preferências) imediatamente antes do envio; deadline vencido → `Expired`; texto neutro sem nome nem trecho (spec 7.2, 10.4) | a criar: `NotificationToBlockedOrDeletedRecipientIsCancelled`; a criar: `ExpiredNotificationIsNeverSent` |
| TM-56 | Fila envenenada e retry storm | Até 8 tentativas com backoff exponencial e jitter, teto de 15 min; falha definitiva vai para revisão; circuit breaker por fornecedor; alerta pela idade do item mais antigo ([concurrency.md](../data/concurrency.md#política-de-retry)) | a criar: `DeliveryMovesToReviewAfterMaxAttempts`; a criar: `RetryDelaysAreBoundedAndJittered` |
| TM-57 | Worker com poder de web | Worker sem key ring de cookies nem `IHubContext`; credencial de banco própria quando viável (spec 3.1, 11.2) | a criar: `WorkerCompositionHasNoCookieAuthenticationOrHubContext` (teste de arquitetura) |
| TM-58 | Agente induzido por conteúdo hostil | Workspace isolado, dados sintéticos, sem credenciais de produção; gates, `CODEOWNERS` e suítes protegidas; conteúdo externo é dado (ADR-0014; spec 14.3; D-SEC-02) | T-12 |
| TM-59 | Cadeia de suprimentos | Lockfiles, `--locked-mode`, actions fixadas por SHA, imagens por digest, SCA e Gitleaks no CI (spec 17.1) | Gates de CI (Gitleaks já ativo); a criar: verificação de digest no executor de deploy (M3) |
| TM-60 | Fornecedor além do contrato | Dados mínimos por fornecedor ([data-map.md](../privacy/data-map.md#fornecedores)); contrato e avaliação antes de dados reais; adaptador envia só os campos listados (spec 16.3) | a criar: teste de contrato de cada adaptador afirmando o conjunto exato de campos enviados (quando houver fornecedor) |
| TM-61 | Restore ressuscita conta ou sessão | Tombstones em registro externo aplicados antes de reabrir; restore revoga todas as sessões (spec 18.4; INV-11) | T-11 (M3) |
| TM-62 | BOLA em perfil, sala, conversa, mídia, exportação | Toda consulta filtra por id **e** titular/participante/relação na própria query; alheio → `404` com corpo igual ao inexistente (spec 8.1, 11.4; *Secure APIs* cap. 4.2) | a criar: `ForeignResourceAccessReturns404WithoutForeignData` (parametrizado por rota); T-05 |
| TM-63 | Erro com detalhe interno | Problem Details com `type`, `title`, `status`, `code`, `traceId`; sem stack, SQL, classe ou versão (spec 8.1, 8.6) | a criar: `UnhandledExceptionReturnsProblemDetailsWithoutInternals` |
| TM-64 | Rota nova sem autenticação | Negar por padrão: política de fallback exige autenticação; rotas públicas numa allowlist explícita (*Secure APIs* ap. A.3) | a criar: `EveryRegisteredRouteRequiresAuthenticationExceptAllowlist` |
| TM-65 | Exportação com dados de terceiros | Exportação só de dados do titular; sem decisões alheias, evidências nem dados de terceiros; arquivo via API-D04 com reautenticação e expiração de 24 h (spec 12.1, 12.4) | a criar: `ExportContainsOnlySubjectDataAndExpires` (M3) |
| TM-66 | Pré-sequestro de conta por cadastro pendente, nas duas ordens (atacante antes da vítima; vítima antes, atacante depois e a vítima clica no link do atacante) | Confirmação exige token válido **e** a senha do cadastro pendente, com resposta única para as duas falhas e teto de 5 falhas que invalida o link; cadastro sobre conta pendente a substitui (senha, aceites e security stamp novos) num upsert atômico; pendente expira em 72 h; reset não serve a conta pendente nem confirma o e-mail. Risco residual: negação de serviço do cadastro de outra pessoa (sobrescrever o pendente ou esgotar as tentativas), sem acesso à conta ([auth.md](auth.md#cadastro-pendente-e-pré-sequestro-de-conta)) | `AccountTakeoverTests.OwnerCannotBeTrickedIntoConfirmingTheAttackersLaterRegistration`, `OwnersRegistrationReplacesAPendingRegistrationMadeBySomeoneElse`, `WrongPasswordAndInvalidTokenAreIndistinguishable`, `TooManyWrongPasswordsInvalidateTheConfirmationLink`, `PendingRegistrationExpiresAndCanNoLongerBeConfirmed`, `PasswordResetIsNotOfferedForAPendingRegistration`, `ConfirmedAccountIsNotReplacedByALaterRegistration`; `ConfirmationAbuseTests` (link reaproveitado não é oráculo de senha, teto atômico em paralelo, corrida com recadastro); `RegistrationTests.ConcurrentRegistrationsOfTheSameEmailCreateOneAccount` |
| TM-67 | Endereço parecido (Unicode) acha a conta de outra pessoa | `EmailAddress` só aceita ASCII imprimível, com domínio internacional em punycode; e-mails de conta vão para o endereço gravado, nunca para o digitado ([auth.md](auth.md#endereço-de-e-mail)) | `LookalikeEmailTests`; `EmailAddressTests.TryParseRejectsNonAsciiAddresses` |
| TM-68 | Entrada indevida na voz | Policy `EligibleParticipant` em API-R08 e em `SendVoiceSignal` (autorização própria do método do hub); credencial só para participante ativo que tocou "Entrar na voz"; conexão só quando as duas pessoas entraram; TTL de ~5 min, renovável, nunca além do fim da sessão; alheio → `404` como a sala (ADR-0016); usernames em `experiences.voice_credentials` | a criar: `VoiceCredentialIsIssuedOnlyToActiveParticipantsOfTheRoom`; a criar: `ExpiredOrForeignVoiceCredentialIsRejected`; a criar: `SendVoiceSignalRequiresEligibleParticipantOfTheRoom`; a criar (E2E): `NoPeerConnectionUntilBothJoinVoice` |
| TM-69 | IP exposto ao par | Quem protege cada pessoa é o próprio navegador: `iceTransportPolicy: "relay"` conferido em `getConfiguration()` antes da oferta, candidato remoto não relay descartado e par selecionado em `getStats()` exigido relay/relay, senão a conexão fecha. O servidor rejeita candidato não `typ relay` (inclusive no SDP) e `raddr`/`rport` diferentes de `0.0.0.0`/`0`, o que só pega erro de cliente honesto | a criar: `ClientClosesVoiceWhenSelectedPairIsNotRelay`; a criar: `VoiceSignalWithNonRelayCandidateIsRejected`; a criar: `VoiceSignalWithRelatedAddressIsRejected`; a criar (E2E com coturn local via Testcontainers, Chromium e WebKit): `RemoteIceCandidatesContainOnlyRelayAddresses` (com `raddr` `0.0.0.0`); manual no Safari/iOS ([bug 312332 do WebKit](https://bugs.webkit.org/show_bug.cgi?id=312332)) |
| TM-70 | Áudio gravado ou enviado a terceiros | Mídia DTLS-SRTP de navegador a navegador: contra uma VPS honesta, nem ela nem o relay têm o áudio em claro (adulteração do SDP por VPS comprometida: TM-78); nenhuma gravação, transcrição ou STT; nenhum módulo referencia cliente de STT ou de LLM para voz; denúncia sem áudio (ADR-0028) | a criar: `NoModuleReferencesSpeechToTextForLiveVoice` (arquitetura); a criar: `VoiceReportCarriesMetadataButNoAudio` |
| TM-71 | Microfone sem consentimento | `getUserMedia` só no handler do gesto "Ligar meu microfone"; desligar para a trilha (`stop`), não só a muta; indicador de microfone aberto sempre visível | a criar: `MicrophonePermissionIsRequestedOnlyAfterExplicitGesture`; a criar: `TurningMicrophoneOffStopsTheCaptureTrack` |
| TM-72 | Voz depois do bloqueio ou de encerramento | O cliente de quem bloqueia fecha a `RTCPeerConnection` na hora. Cada evento da lista (sair da voz ou da sala, bloqueio, denúncia, sala abortada (spec 4.2, 6.3), medida de moderação que restringe a conta, revogação da sessão de login e fim da sessão de jogo) grava na outbox, no mesmo commit, a revogação de todos os usernames da sala em `voice_credentials`, e um evento no hub fecha a conexão nos dois clientes; nova credencial negada (spec 7.3). Fail-closed: sem hub por mais de N s (15 s, hipótese), o cliente encerra a voz. TTL de ~5 min limita a janela se a revogação não derrubar a alocação aberta, o que é conferido em teste de contrato manual contra a Cloudflare real no gate 23.2 | a criar: `BlockEndsLiveVoiceForBothParticipants`; a criar: `BlockerClientClosesPeerConnectionImmediately`; a criar: `EveryVoiceEndingEventRevokesAllRoomCredentials`; a criar: `RevokedLoginSessionCannotRejoinVoice`; a criar (E2E): `VoiceEndsWhenHubIsLostBeyondTimeout`; manual no gate 23.2: revogação contra a Cloudflare real |
| TM-73 | Assédio sem evidência | Aceito por decisão do responsável: a denúncia não leva áudio. Ela registra que havia voz ativa, horário e duração; bloquear e denunciar ficam na tela de voz; silenciar e sair da voz são imediatos. Rever só por ADR | a criar: `VoiceReportCarriesMetadataButNoAudio` |
| TM-74 | Abuso do relay | Credencial de ~5 min e revogável por sala; relay gerenciado pela Cloudflare (fora da rede da VPS); rate limit por conta e por sala na emissão (`429` com `Retry-After`) e por conexão em `SendVoiceSignal`; sinalização com teto de ~16 KiB. Risco residual aceito: com credencial válida, o relay serve de proxy UDP até ela vencer ou ser revogada | a criar: `VoiceCredentialIssuanceIsRateLimited`; a criar: `OversizedVoiceSignalIsRejected`; a criar: `VoiceSignalsAreRateLimitedPerConnection` |
| TM-75 | Credencial obtida por página hostil | Emissão por `POST` autenticado com antiforgery e `Origin` validado, resposta `no-store`; credencial nunca em URL nem em log | a criar: `VoiceCredentialRequiresAntiforgeryAndSameOrigin` |
| TM-76 | Metadados identificantes | Telemetria da voz só com contagens e duração; sem IP do par, sem SDP nem candidatos em log; nenhuma impressão de voz; Cloudflare como sub-processador de IP e metadados no [mapa de dados](../privacy/data-map.md#fornecedores); aviso de que a outra pessoa pode gravar localmente | a criar: `VoiceTelemetryContainsNoPeerAddressesOrSdp` |
| TM-77 | Menor de idade na voz | A denúncia pela tela de voz inclui a categoria "suspeita de menor", que restringe o acesso da conta e revoga a voz na hora (spec 12.2), além da revisão apropriada; a maioridade por jurisdição é a lacuna J-05 do PR #83 | a criar: `MinorSuspicionReportFromVoiceRestrictsAccountAndRevokesVoice` |
| TM-78 | Adulteração do SDP pela VPS | O servidor repassa o SDP sem tocar em `a=fingerprint`. Limite aceito: uma VPS comprometida pode trocar a fingerprint e se pôr no meio; a garantia de "sem áudio em claro" vale contra VPS honesta. Mitigação de base: hardening e deploy só pelo CI (spec 15, 17) | a criar: `ServerForwardsSdpFingerprintUnchanged` |
| TM-79 | Custo por abuso do relay | Alerta de gasto e de GB transferidos na conta da Cloudflare; kill switch que desliga a voz no produto inteiro (o botão some, API-R08 recusa e sessões abertas são revogadas); rate limits de TM-74 | a criar: `VoiceKillSwitchHidesVoiceRefusesCredentialsAndRevokesOpenSessions` |

## 7. Riscos aceitos e pendências

| Item | Situação | Dono |
|---|---|---|
| Captura de tela e mensagem já entregue | Aceito: impossível recolher (spec 1.2). Interface informa o que a outra pessoa vê | Produto |
| Limitador de taxa em memória reinicia com o processo | Aceito no piloto (ADR-0009); lockout por conta fica no banco | Responsável técnico |
| Participante sem MFA depende de senha e e-mail | Aceito (ADR-0017) | Responsável técnico |
| Fornecedores de e-mail, verificação etária, storage e monitoramento | Pendente de escolha (spec 23.2); TM-36, TM-39 e TM-60 se completam com o contrato real | Responsável técnico |
| Base legal, prazos e responsável por incidentes | Pendente de apoio jurídico (spec 12.1, 12.5); bloqueia dados reais | A designar |
| Chave do autenticador e códigos de recuperação em `identity.user_tokens` | Verificar se a versão do Identity guarda em claro; se sim, decidir proteção adicional antes do M1 ([auth.md](auth.md#mfa)) | Responsável técnico |
| Segunda instância web | Fora do escopo: exige revogação distribuída e limites compartilhados (ADR-0009) | — |
| Voz ao vivo: silenciar e encerrar no cliente | Aceito (ADR-0028): sem servidor de mídia, silenciar é no cliente de quem ouve (a outra pessoa não é avisada) e o encerramento forçado depende da revogação no relay e do TTL de ~5 min; mute pelo servidor leva ao plano B (LiveKit Cloud) | Responsável pelo produto |
| Voz ao vivo: VPS comprometida no meio da mídia | Aceito com mitigação de base (TM-78) | Responsável técnico |
| Voz ao vivo: relay como proxy UDP durante a validade da credencial | Aceito (TM-74), com alerta de gasto e kill switch (TM-79) | Responsável técnico |
| Voz ao vivo: gravação local pela outra pessoa | Aceito: impossível impedir; a tela de voz avisa | Produto |
| Voz ao vivo: denúncia sem áudio | Aceito por decisão do responsável (TM-73); rever só por ADR | Responsável pelo produto |
