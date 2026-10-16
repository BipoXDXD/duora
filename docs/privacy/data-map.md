# Mapa de dados pessoais

Finalidades, categorias de dados por tabela, base legal, fornecedores e acessos por papel. Fontes: spec 1.3,
6.4, 8, 10.2, 12, 16.3, 19.2 e 25.15; [model.md](../data/model.md) (tabelas e classes de sensibilidade);
[threat-model.md](../security/threat-model.md); fichas D-PRIV-01 (LGPD, Marco Civil da Internet) e D-PRIV-02
(ECA Digital, ANPD). Prazos de guarda: [retention.md](retention.md).

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
([model.md](../data/model.md#classificação-de-sensibilidade)). "Sensível LGPD?" indica se o dado **pode**
ser dado pessoal sensível (LGPD art. 5.º, II) ou de risco elevado; a resposta final é jurídica.

| Tabela | Categorias de dado | Titular | Classe | Finalidade | Sensível LGPD? | Base legal |
|---|---|---|---|---|---|---|
| `identity.users` | E-mail, hash de senha, estado da conta, data de nascimento, tentativas de login | Pessoa usuária | S / P3 | F-01, F-02 | Não, mas a data de nascimento serve à proteção etária | A definir com apoio jurídico |
| `identity.login_sessions` | Instantes de uso, resumo do dispositivo, prefixo de rede | Pessoa usuária | P2 | F-01, F-07 | Não | A definir com apoio jurídico |
| `identity.auth_tickets`, `identity.user_tokens` | Ticket de sessão, chave TOTP, códigos de recuperação | Pessoa usuária | S | F-01 | Não | A definir com apoio jurídico |
| `identity.age_verifications` | Resultado mínimo, validade, referência do fornecedor, versão da política | Pessoa usuária | P3 | F-02 | Possível (biometria no fornecedor, se o método usar) | A definir com apoio jurídico |
| `identity.consent_records` | Versões de termos aceitas e instantes | Pessoa usuária | P2 | F-01, F-08 | Não | A definir com apoio jurídico |
| `profiles.profiles` | Nome de exibição, faixa etária, região aproximada (UF em ISO 3166-2:BR, opcional; nunca cidade, CEP nem coordenada), descrição | Pessoa usuária | P2 | F-03 | Pode conter dado sensível em texto livre (descrição) | A definir com apoio jurídico |
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
precisa (spec 1.1; a região do perfil para na UF, e o `CHECK` do banco não aceita nada mais fino), importação de redes sociais, telemetria comportamental com preferências identificáveis,
session replay ou gravação de digitação em telas privadas (spec 13.6), conteúdo de chat em analytics.

## Fornecedores

Todos **a escolher** (spec 23.2). A coluna "dados enviados" é o máximo permitido ao adaptador; o teste de
contrato de cada adaptador afirma esse conjunto (TM-60).

| Função | Interface | Dados enviados (máximo) | Dados recebidos | Requisitos antes de dados reais |
|---|---|---|---|---|
| Verificação etária | `IAgeEligibilityProvider` | ID de solicitação (nonce), URL de retorno fixa; o que o método exigir é coletado **pelo fornecedor**, não pelo Duora | Resultado mínimo, referência, validade | Método proporcional, taxa de erro, contestação, acessibilidade, retenção e local de tratamento, callbacks assinados (spec 12.2, 16.3) |
| E-mail transacional | `ITransactionalEmailSender` | Endereço, modelo e variáveis neutras (sem nome de outra pessoa, trecho de mensagem ou motivo de moderação) | Recibos, bounces, reclamações | Domínio autenticado (SPF, DKIM, DMARC), retenção de logs do fornecedor, suppression (spec 16.4) |
| Armazenamento de fotos | `IObjectStore` (adaptador de filesystem) | Bytes de fotos (quarentena e derivados) e arquivo de exportação, no disco da VPS (Hostinger); nenhum terceiro além do provedor da VPS | — | Diretório do host com dono e modo restritos, montagem por área, cópia externa obrigatória (adendo do ADR-0010) |
| Backup | pgBackRest (banco e WAL) e restic (derivados das fotos), no **Backblaze B2, região US East (EUA)** | Cópia física do banco e WAL e cópia dos derivados das fotos, **cifradas no cliente** antes de sair da VPS (a Backblaze só recebe blocos cifrados; as chaves de cifra ficam na VPS e no cofre do responsável) | — | Criptografia com chave sob custódia separada, retenção, proteção contra exclusão (spec 18.3); escolhido em 2026-10-16 |
| Monitoramento | OTLP | Telemetria filtrada, sem dado privado | — | Filtragem, retenção, controle de acesso (spec 19.2) |
| Moderação assistida (condicional) | `IContentReviewProvider` | Só conteúdo denunciado ou upload em revisão, estritamente necessário | Rótulo e prioridade | Política de dados, retenção, proibição de treino, revisão humana (spec 12.3; ADR-0013) |
| Agentes de desenvolvimento | — | **Nenhum dado pessoal real**; só dados sintéticos | — | INV-12; spec 6.4, 14.3 |

Transferência internacional é provável em vários desses serviços; local de tratamento e salvaguardas entram
na avaliação jurídica de cada contrato.

### Transferências internacionais registradas

| Destino | País | Dados | Proteção técnica | Retenção no destino | Salvaguarda (LGPD, art. 33) |
|---|---|---|---|---|---|
| Backblaze B2, região US East (bucket próprio do Duora) | Estados Unidos | Tudo o que o banco guarda (via pgBackRest) e os derivados das fotos aprovadas (via restic); nunca a quarentena | Cifra no cliente (pgBackRest `aes-256-cbc`; restic AES-256), TLS, chave presa ao bucket e ao prefixo, File Lock `compliance` de 21 dias | Banco: janela de 14 dias + 21 de lock + 30 de lifecycle; fotos: 14 dias de snapshots + 21 + 30 ([retention.md](retention.md)) | **A definir** (registrado em 2026-10-16; sem análise jurídica nesta fase, por decisão do responsável). Antes de dados reais: escolher o mecanismo (por exemplo, cláusulas-padrão contratuais da ANPD) e registrar aqui |

A Backblaze guarda só blocos cifrados e não tem as chaves; ainda assim, o backup é cópia de dado pessoal fora do
Brasil e conta como transferência internacional.

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

| Direito (LGPD art. 18, a confirmar no escopo jurídico) | Mecanismo técnico |
|---|---|
| Acesso e portabilidade | API-D01, D02, D04: exportação dos dados do titular, sem decisões, evidências ou dados de terceiros (spec 12.1) |
| Correção | API-P02, P03, N02 |
| Eliminação | API-D03: desativação e revogação imediatas; execução por etapas em `privacy.deletion_steps`; exceções por `retention_holds` com escopo e vencimento ([retention.md](retention.md)) |
| Revogação de consentimento, se for a base | `identity.consent_records.withdrawn_at`; efeito depende da base definida |
| Informação sobre compartilhamento | Tabela de fornecedores acima, depois de escolhidos |

## Lacunas

Pendências que este mapa não resolve; cada uma bloqueia dados reais até ter dono e decisão (spec 23.2).

1. **Bases legais** de cada finalidade, inclusive para dados possivelmente sensíveis (preferências, mensagens,
   denúncias), que seguem o art. 11 da LGPD e não o art. 7.º.
2. **Dimensões de preferência** não estão definidas na spec; se incluírem gênero buscado ou orientação, o
   tratamento é de dado sensível e a minimização precisa ser revista ([model.md](../data/model.md#schema-profiles)).
3. **Data de nascimento** ([model.md](../data/model.md#decisões-de-modelagem-propostas), D12): confirmar se é
   necessária ou se basta a faixa etária mais o resultado da verificação.
4. **Registros de acesso:** aplicabilidade do Marco Civil (art. 15, seis meses) ao Duora, armazenamento
   separado e acesso restrito (spec 12.1, 12.4).
5. **ECA Digital:** avaliar com apoio jurídico se o art. 9.º se aplica ao Duora (serviço restrito a adultos) e,
   se sim, como "verificação de idade a cada acesso, vedada a autodeclaração" se compara à verificação com
   validade prevista na spec 12.2.
6. **Registro das operações de tratamento** (LGPD art. 37) e comunicação de incidentes (art. 48): responsável
   e procedimento (spec 12.5).
7. **Fotos de terceiros** nas imagens de perfil e **terceiros mencionados** em mensagens e denúncias: tratamento
   na exportação e na exclusão.
