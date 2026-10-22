# Emails transacionais pelo Resend

Implementação opt-in do transporte de emails de conta. Não ativa o provedor, altera a VPS nem publica uma
release por si só. Cadastro, confirmação, login e recuperação mantêm seus contratos, CSRF, respostas
neutras e políticas de sessão. INV-01, INV-06, INV-08 e INV-12; API-A02/A03 e rotas de recuperação.

## Configuração e autorização

O **worker** seleciona `Email:Provider`: `PickupDirectory` (padrão compatível) ou `Resend` (case-sensitive).
Um valor desconhecido ou configuração inválida impede o startup. Resend não exige diretório local e nunca
faz fallback para `.eml` se o provedor falhar. Não há SDK ou dependência nova.

| Chave do worker | Uso |
| --- | --- |
| `Email__Provider=Resend` | Seleciona o transporte externo |
| `Email__Resend__From` | Endereço ou `Duora <endereço>`, em domínio verificado pelo operador |
| `Email__Resend__ApiKey` | Chave restrita a envio, fornecida por arquivo secreto |

O suporte a `SecretsDirectory` já existente lê o arquivo `Email__Resend__ApiKey` em `/run/secrets`.
O override **`infra/compose/compose.resend.yml`** monta `${DUORA_SECRETS_DIR}/resend_api_key` só no worker
com esse nome. Não colocar a chave em `.env`, CLI, frontend, commit ou log. A chave e a verificação de
DNS/domínio são atividades do operador, fora desta homologação. O compose principal continua com pickup:
**não há entrega externa até uma futura ativação explícita**.

O endpoint de produção é fixo: `https://api.resend.com/emails`, com validação TLS do sistema. Sem redirects,
cookies, logging de HttpClientFactory nem retries internos. O teste injeta somente um HttpClient interno
apontado ao receptor HTTP em loopback; não existe configuração pública para trocar o endpoint.

## Entrega, falhas e recuperação

O adaptador envia `from`, um `to`, `subject` e `text`, preservando os catálogos PT-BR/EN-US. A chave
`Idempotency-Key` é o ID da entrega, constante em retries e retomadas. Só uma resposta 2xx com `id` não
vazio confirma aceitação. Corpo de resposta limitado a 16 KiB; erros não incluem corpo, chave ou endereço.

A fila existente conserva lease/fencing, timeout de envio, backoff com jitter, limite de tentativas e
expiração. 4xx (inclusive 401/403/409/429), 5xx, rede, timeout e resposta inválida não viram `Delivered`:
seguem a política limitada existente e terminam em `Failed` para revisão se esgotarem tentativas.
Não se interpreta `Retry-After`; o backoff existente continua valendo. Quotas/rate limits do plano precisam
ser acompanhados na ativação. Cancelamento do worker deixa o lease recuperável por outra instância.

**`Delivered` significa aceito pelo provedor, não entregue à caixa postal.** Bounces, spam e eventos de
entrega não são acompanhados por webhook neste PR. Nenhuma mensagem real foi enviada nesta homologação.

O Resend retém chaves por **24 horas**. Avisos de segurança podem permanecer na outbox por 72 horas;
um aceite seguido de perda da resposta/queda e retomada depois de 24 horas pode duplicar um aviso.
Não há garantia exactly-once global. Manter remetente e conteúdo estáveis enquanto houver tentativas
pendentes: alterar o corpo com a mesma chave pode gerar 409. Não apagar pendências, IDs ou registros para
forçar reenvio. Referências: [API](https://resend.com/docs/api-reference/emails/send-email),
[idempotência](https://resend.com/docs/dashboard/emails/idempotency-keys).

## Homologação reproduzível

Com .NET 10 e Docker local, na raiz do repositório:

```sh
dotnet restore backend/Duora.slnx --locked-mode
dotnet build backend/Duora.slnx --no-restore
dotnet backend/tests/Duora.IntegrationTests/bin/Debug/net10.0/Duora.IntegrationTests.dll \
  -class 'Duora.IntegrationTests.Outbox.*' -class Duora.IntegrationTests.WorkerHostTests
pnpm --dir frontend install --frozen-lockfile
pnpm --dir frontend exec vitest run src/features/auth
```

`ResendEmailSenderTests` cobre payload/headers, erros, resposta inválida, cancelamento e configuração.
`ResendEmailFlowTests` usa PostgreSQL 18 real descartável, API de conta, composition root real do worker e
receptor HTTP loopback. Confirma cadastro, código recebido, login, reset recebido, invalidação da sessão e
senha antigas, aviso de segurança, payload apagado após sucesso, concorrência, backoff e retomada com a mesma
chave. Tokens são lidos da mensagem HTTP capturada, não da outbox. Não usa conta, banco ou segredo real.
Os testes de confiabilidade anteriores continuam ativos. O teste de MFA aguarda o efeito de foco com
`waitFor`, sem remover sua asserção nem mudar a interface.

Metodologia: captura determinística e conferência do estado persistido, conforme a aula
[Testing Email Services — Deep Dive](https://dometrain.com/take/course/from-zero-to-hero-sending-email-with-dotnet-3066009/testing-email-services-deep-dive-64006542/),
com PostgreSQL real para o protocolo de fila.

## Ativação futura e rollback

Revisão humana do PR, CI verde e autorização específica são pré-requisitos. Antes de ativar, validar domínio,
remetente, permissão da chave, quotas, secret file legível pelo UID do worker, composição final e snapshot da
fila. Validar entrega externa com destinatários explicitamente autorizados em homologação separada.
Não usar contas de participantes para provar entrega.

Para incidente, pausar o consumidor de email de forma coordenada, registrar pendências/leases/resultados
ambíguos e preservar o banco. Ao voltar ao transporte anterior de pickup ou a uma release anterior, **não
consumir a fila externa pendente com pickup**: isso a marcaria como entregue apenas em arquivos locais.
Manter consumo pausado até decidir a recuperação; retomar com Resend, os mesmos IDs/remetente/conteúdo e
atenção à janela de 24 h. Não restaurar banco, remover volumes ou reinserir entregas para rollback do
transporte. A remoção do override não é rollback de emails já enviados. Bancos, volumes, receptor 2.0.2 e
procedimentos de rollback da release permanecem intactos.
