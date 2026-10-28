# Atendimento: severidades, escalonamento e contingência do canal

Processo da spec 12.5 para o contato público `support@meetduora.com` (issue #82). Complementa
[support-contact.md](support-contact.md) (canal, titular e configuração), a
[política de moderação](../privacy/moderation.md) (categorias graves e medidas) e os runbooks da spec 19.3.
Não muda código, contrato, configuração nem dado. Não declara conformidade jurídica: prazos legais de
comunicação continuam a verificar na regulamentação vigente, com apoio qualificado (spec 12.5; J-02 do
[ADR-0026](../architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md)).

## Fatos de partida (28/10/2026)

- `support@meetduora.com` é alias da caixa Zoho Mail Lite do titular. O MX de `meetduora.com` é o Zoho.
- O Zoho encaminha automaticamente para o Gmail pessoal do titular e **retém uma cópia** na caixa Zoho
  (prova ponta a ponta às 08:19 UTC de 28/10, registrada na issue #82). O endereço Gmail não é publicado.
- Titular único, **sem substituto**, por decisão já registrada ([decisões de 24/10](../product/decisions-2026-10-24.md),
  item 19). Cobertura declarada: 24 horas por dia, 7 dias por semana, em PT-BR e EN-US.
- Envio transacional do produto pelo Resend (`no-reply@meetduora.com`, domínio verificado). Esse remetente
  não recebe respostas e não faz parte do atendimento.

"Cobertura 24/7 declarada" significa que o canal recebe a qualquer hora e que o titular é notificado a qualquer
hora. Não existe escala nem segunda pessoa: durante sono, deslocamento ou doença um alvo pode ser perdido. Os
tempos abaixo são **alvos**, não SLA contratual nem medido. Cada perda de alvo é registrada e revisada.

## Severidades

Classifique pela leitura mais grave plausível da mensagem. Mensagem com vários assuntos recebe a severidade
mais alta entre eles. Só uma pessoa rebaixa uma severidade, registrando o motivo; automação, filtro ou resposta
pronta nunca rebaixa S1 (mesma regra dos casos graves da [moderação](../privacy/moderation.md)).

| Severidade | Exemplos no Duora | Primeira resposta humana (alvo) | Ação imediata |
|---|---|---|---|
| **S1 Crítica** | Incidente de segurança ou suspeita de vazamento de dado pessoal; conta comprometida com uso indevido em curso (sessões desconhecidas, mensagens enviadas por outra pessoa); emergência ou risco a participante (`seguranca.pessoa-em-risco`, `conduta.ameaca`, `conduta.conteudo-sexual-nao-consentido`, suspeita de menor) | **4 horas corridas**, qualquer dia e horário | Conter assim que ler, antes de responder |
| **S2 Alta** | Pedido de exclusão de conta ou de outro direito sobre dados pessoais (acesso, correção); conta comprometida sem uso em curso ou perda de acesso que a recuperação de conta não resolveu; denúncia não grave com escalonamento por revisão (`conduta.assedio`, `outro`); bug que impede entrar, confirmar e-mail ou participar | **24 horas corridas** | Abrir o procedimento correspondente |
| **S3 Média** | Bug com contorno ou que não bloqueia o uso; `perfil.falso` e `spam` denunciados por e-mail | **3 dias corridos** | Issue sem dado pessoal, quando for defeito |
| **S4 Baixa** | Dúvida de uso, sugestão, pedido de informação geral | **5 dias corridos** | Responder |

A primeira resposta conta da chegada na caixa Zoho até a resposta enviada de `support@meetduora.com` por uma
pessoa. Confirmação automática não conta como primeira resposta e não substitui este processo (spec 12.5).

## Escalonamento por severidade

Com titular único, escalonar é encaminhar o caso ao procedimento certo e, quando preciso, a quem está fora do
Duora. A ordem é: proteger pessoas, conter, preservar evidência mínima, responder, registrar.

**S1, segurança ou dado pessoal.** Seguir "Suspeita de vazamento" da spec 19.3: restringir acesso e revogar
credenciais afetadas, preservar evidência e avaliar alcance. Não copiar dados para issue (o repositório é
público) nem enviá-los a agente ou modelo externo ([AGENTS.md](../../AGENTS.md)). Acionar apoio jurídico para
decidir se há comunicação obrigatória a autoridade ou a titulares e em que prazo; este runbook não fixa prazo.

**S1, conta comprometida em uso.** Conter pelos meios disponíveis na release. A
[moderação](../privacy/moderation.md) prevê que a suspensão revoga sessões, mas não declara o fluxo implementado:
conferir a ação na release antes de contar com ela. Orientar a pessoa a usar a recuperação de conta e a encerrar sessões
em "Sessões ativas". Não pedir senha, código, documento nem data de nascimento por e-mail.

**S1, risco a participante.** Na primeira resposta, orientar a pessoa a procurar o serviço de emergência local,
com os números de [emergency-contacts.md](../safety/emergency-contacts.md) (Brasil 190 e 192; EUA 911; 180 não é
serviço de emergência). O Duora não presta socorro e não decide pela pessoa. Tratar a denúncia como caso grave
da [moderação](../privacy/moderation.md): entra urgente e pode receber contenção temporária automatizada.
Suspeita de menor ou exploração segue a spec 12.3: restringir acesso e escalar a responsável qualificado e aos
procedimentos aplicáveis.

**S2, pedido sobre dados.** A release atual não oferece exclusão nem exportação self-service (a API publicada
em [openapi.json](../api/openapi.json) não tem essas rotas), então o pedido é atendido por este canal. Só atender
pedido vindo do e-mail da própria conta, sem pedir documento nem data de nascimento. A remoção ou anonimização
planejada segue os 30 dias propostos na spec 12.4; o prazo legal, se houver, depende da lei aplicável, a
confirmar (J-02). Quando existir fluxo self-service, indicá-lo primeiro, porque ele já autentica a pessoa.

**S2 e S3, defeito.** Abrir issue com passos reproduzíveis e dados sintéticos. Nunca colar mensagem, endereço,
nome, captura de tela ou identificador de pessoa real.

**Fornecedores.** Falha de canal ou de infraestrutura escala para o suporte do fornecedor: Zoho (caixa e MX),
Google (Gmail), Resend (envio transacional), Cloudflare (DNS) e Hostinger (VPS).

## Contingência do canal

O caminho normal é: remetente → MX Zoho → caixa Zoho (cópia retida) → encaminhamento → Gmail do titular. A
resposta sai sempre do Zoho, com `support@meetduora.com` como remetente.

| Falha | Como perceber | O que fazer |
|---|---|---|
| **Zoho fora para leitura ou envio** (webmail/app), recebimento normal | Erro no webmail; [status do Zoho](https://status.zoho.com) | Ler as cópias encaminhadas no Gmail. **Não responder pelo Gmail pessoal**: isso publicaria o endereço privado. Em S1, conter no produto sem esperar a resposta. Responder de `support@` quando o Zoho voltar e registrar o atraso. |
| **Zoho fora para recebimento** (MX) | Status do Zoho; teste externo sem chegada | Servidores remetentes retêm e retentam a entrega por um período definido por eles; nada chega a nenhuma caixa enquanto durar. Acompanhar o status. Acima de 4 horas, publicar o aviso público abaixo. Ao voltar, triar por severidade. |
| **Gmail fora** | Gmail indisponível; [status do Google Workspace](https://www.google.com/appsstatus/dashboard/) | Ler e responder no webmail ou no app do Zoho. A cópia retida garante que nada se perde. |
| **Encaminhamento parado** (Zoho e Gmail no ar) | Teste mensal sem chegada ao Gmail | Atender pelo Zoho. Reativar o encaminhamento no console do Zoho, mantendo "Excluir cópia" desmarcado, e repetir o teste. |
| **Zoho e Gmail fora** | Os dois sinais acima | Publicar o aviso público. Registrar início e fim. Ao voltar, triar primeiro S1. |

Não criar canal alternativo improvisado (outro endereço, rede social, telefone): um canal novo é decisão do
responsável e precisa entrar neste documento antes de ser divulgado.

**Lacuna:** o Duora não tem página de status nem aviso configurável sem release. Hoje, publicar o aviso no site
exige uma release com o texto abaixo no catálogo PT-BR/EN-US. Enquanto não houver mecanismo próprio, uma queda
do canal acima de 4 horas não tem comunicação pública imediata.

**Teste do canal.** Mensalmente e depois de qualquer mudança no Zoho, no encaminhamento ou no DNS: enviar de uma
conta externa que não seja o próprio Gmail do titular (o Gmail descarta a cópia encaminhada de mensagem que ele
mesmo enviou) e conferir a chegada na caixa Zoho e no Gmail. Registrar data, hora em UTC e resultado, sem o
conteúdo da mensagem.

## Registro do atendimento e do incidente

O registro é privado e fica **fora do repositório**, que é público: num documento do titular ou em pastas da
própria caixa Zoho. Cada caso guarda somente:

| Campo | Exemplo |
|---|---|
| ID | `SUP-20261028-01` |
| Recebido em (UTC) | `2026-10-28T08:19Z` |
| Idioma | PT-BR ou EN-US |
| Severidade inicial e final, com motivo de qualquer mudança | S2 → S3, "contorno confirmado" |
| Categoria | Segurança, conta, dados, participante, defeito, dúvida |
| Primeira resposta (UTC) e alvo cumprido (sim ou não) | `2026-10-28T10:02Z`, sim |
| Contenção e procedimento acionado | "suspensão aplicada; spec 19.3" |
| Referência à evidência | Pasta Zoho, `Message-ID` ou ID de auditoria; nunca o conteúdo |
| Escalonamento externo | Apoio jurídico, fornecedor, nenhum |
| Encerrado em (UTC) e desfecho genérico | `2026-10-29T12:00Z`, resolvido |

Evidência mínima de S1: a mensagem original na caixa Zoho (com cabeçalhos), os eventos de auditoria do produto
e a linha do registro. Não duplicar o histórico inteiro, não anexar a issue e não enviar a modelo. A retenção
segue a categoria correspondente em [retention.md](../privacy/retention.md) e na
[moderação](../privacy/moderation.md).

Falha do canal também gera registro, com o ID `CANAL-AAAAMMDD-NN`: início, fim, componente, sinais observados e
casos afetados.

**Revisão.** Uma vez por mês: contagem por severidade, alvos perdidos e motivo, falhas do canal e resultado do
teste. A revisão pode ser citada na issue só em números agregados. Se S1 perder o alvo de forma recorrente, os
alvos ou a cobertura declarada precisam de nova decisão do responsável.

## Indisponibilidade do titular

Não há substituto. Quando o titular não puder ler a caixa:

- **Sem aviso prévio** (doença, falta de conexão): as mensagens ficam retidas no Zoho e no Gmail. Ao voltar,
  triar S1 primeiro e registrar cada alvo perdido.
- **Planejada, por mais de 24 horas:** ativar a resposta automática do Zoho com o texto abaixo, informando a
  volta prevista, a orientação de emergência e os fluxos que não dependem de atendimento (hoje, recuperação de
  senha e encerramento de sessões). A resposta automática complementa o processo, não o substitui.
- **Com participantes reais admitidos:** não abrir eventos nem rodadas novas durante a ausência planejada. A
  spec 12.5 diz que uma operação sem ninguém capaz de atender denúncias graves não está pronta para admitir
  participantes.

## Textos públicos

Modelos para a resposta automática, a primeira resposta de S1 e o aviso de canal fora do ar, sempre nos dois
idiomas, no tom neutro do ADR-0026. Os colchetes são preenchidos na hora; nenhum texto leva nome, endereço
pessoal ou detalhe de outra pessoa.

### Ausência planejada (resposta automática do Zoho)

> **PT-BR.** Recebemos sua mensagem. O atendimento do Duora está com capacidade reduzida até [data, fuso] e a
> resposta pode demorar mais que o normal. Em emergência, procure o serviço de emergência local; o Duora não
> presta socorro. Para redefinir a senha ou encerrar sessões abertas, use as opções do próprio aplicativo, que
> funcionam sem atendimento.
>
> **EN-US.** We received your message. Duora support has reduced capacity until [date, time zone], and replies
> may take longer than usual. In an emergency, contact your local emergency services; Duora does not provide
> emergency assistance. To reset your password or end open sessions, use the options in the app, which work
> without support.

### Primeira resposta de S1 com risco a participante

> **PT-BR.** Recebemos sua mensagem e ela está sendo tratada com prioridade. Se você ou outra pessoa estiver em
> perigo agora, procure o serviço de emergência local (no Brasil, 190 ou 192; nos EUA, 911). Já aplicamos as
> medidas de proteção disponíveis e voltaremos a escrever quando houver atualização.
>
> **EN-US.** We received your message and are handling it as a priority. If you or someone else is in danger
> right now, contact your local emergency services (in the United States, 911; in Brazil, 190 or 192). We have
> applied the available protective measures and will write again when there is an update.

### Canal de suporte fora do ar (aviso no site)

> **PT-BR.** O canal de suporte por e-mail está temporariamente indisponível desde [hora, fuso]. Mensagens
> enviadas neste período podem chegar com atraso. Em emergência, procure o serviço de emergência local. A
> redefinição de senha e o encerramento de sessões continuam funcionando no aplicativo.
>
> **EN-US.** Our email support channel has been temporarily unavailable since [time, time zone]. Messages sent
> during this period may arrive late. In an emergency, contact your local emergency services. Password reset
> and ending sessions keep working in the app.

Ao surgir bloqueio, denúncia ou exclusão self-service na SPA, atualizar os três modelos nos dois idiomas.
Os textos ainda não estão no catálogo da SPA. Publicá-los no site é uma mudança de interface, com teste de
catálogo PT-BR/EN-US, em PR próprio.

## O que este documento não cobre

- Medição de SLA, escala de plantão ou segunda pessoa: não existem e não são presumidos.
- Prazos legais de comunicação de incidente: dependem da lei aplicável (J-02) e de apoio jurídico.
- Autenticação do domínio para entrega: ver [dmarc.md](dmarc.md).
