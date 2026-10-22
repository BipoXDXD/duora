# Homologação isolada de SMTP e outbox

## Escopo e estado

O transporte SMTP é opcional. `Email:Transport` continua `Pickup` por padrão; não há ativação de SMTP,
alteração do Compose ou acesso a produção nesta mudança. Configuração: [environment.md](../development/environment.md).

A prova de envio e recebimento SMTP foi concluída em 2026-10-22. A criação de uma conta dedicada ficou
bloqueada na verificação Google; o usuário autorizou uma conta pessoal existente e forneceu a senha de app
fora do chat. O teste usa somente um aviso sintético, sem ação sobre uma conta real do Duora. O remetente e o
destinatário pertencem à mesma conta Gmail: isso comprova SMTP e leitura IMAP reais, não entrega entre
provedores diferentes.

A implementação usa MailKit/MimeKit 4.18.1 (MIT), com `System.Security.Cryptography.Pkcs` fixado em 10.0.13:
o limite inferior do MimeKit admite uma versão vulnerável
([aviso Microsoft](https://github.com/advisories/GHSA-846f-f764-76mx)). Não há supressão do audit de dependências.

## Ensaio entre versões executado

[Execução remota 39799180397, job compatibility](https://github.com/BipoXDXD/duora-mail-validation-20261022/actions/runs/39799180397/job/119894677208).
O artefato `cross-version-evidence` registra IDs sintéticos, identidade dos módulos compilados, estado,
tentativas, limpeza do payload e SHA-256 do comprovante pickup. O container PostgreSQL 18.6 foi removido no
`finally`. O ensaio não iniciou Docker nem compilou .NET no Mac.

| Binário | Fonte |
|---|---|
| `legacy` | `a10194a90a4a27df1e95432027c5694eba259b69`, anterior ao aviso de recuperação |
| `reader` | `a4711d6ec2cdd11dda892ed5ab41f66bb2009edc`, merge da #139 |
| `current` | `f99456549baa85ae02127e50eda2d4de09d7c423` mais a implementação SMTP sob teste, versionada no repositório isolado |

São executáveis de teste ligados à Infrastructure real de cada versão. Eles usam o produtor
`AccountEmailOutbox`, a fila SQL, o dispatcher e o adaptador pickup reais. Não são imagens históricas completas
do Worker nem manifestos elegíveis para deploy. Os papéis `duora_migrator` e `duora_app` são separados, com o
script real de inicialização e a reconciliação de privilégios. O app não migra o banco.

| Cenário | Resultado observado |
|---|---|
| Produtor antigo → consumidor atual, `PasswordChangedNotice` | `Delivered`, comprovante pickup e payload limpo |
| Produtor #139 → consumidor atual, `RecoveryCodeUsedNotice` | `Delivered`, comprovante pickup e payload limpo |
| Produtor atual → consumidor #139, mesmo aviso novo | `Delivered`, comprovante pickup e payload limpo |
| Aviso novo `Pending`, depois `InFlight` com dono encerrado | Guarda de rollback bloqueada nos dois estados; consumidor #139 retomou após expiração do lease; `Delivered` na segunda tentativa |
| Consumidor antigo após drenagem comprovada por ID | Aviso antigo entregue |
| Controle negativo, em outro banco: aviso novo → consumidor anterior à #139 | `Failed/InvalidPayload`, payload limpo e nenhum comprovante; resultado esperado |

A consulta de pendências zeradas é insuficiente: o controle negativo também esvazia o payload. A liberação de
rollback no ensaio positivo exige estado `Delivered` e comprovante de cada ID registrado antes da drenagem.
A guarda do ensaio não foi instalada no executor de produção.

O commit #139 contém **leitor e produtor**. Ele não é uma release intermediária somente leitora. Antes de um
rollout operacional continuam obrigatórios a construção, revisão e homologação do manifesto intermediário
sem o produtor novo e a ordem descrita em [deploy.md](../operations/deploy.md). Este ensaio cobre a
compatibilidade de leitura e a recuperação da fila, não autoriza rollback direto nem promoção em produção.

## Transporte SMTP e prova externa

Os testes de transporte exercitam seleção pelo contêiner real de serviços, configuração inválida e
cancelamento de conexão TLS travada. Os testes de protocolo usam sockets reais, TLS implícito e STARTTLS,
credenciais sintéticas e certificado de uma autoridade temporária no runner. O certificado de teste é removido
ao final; o transporte não substitui a validação normal de certificados. Também há recusa de autenticação e
de certificado não confiável antes de qualquer DATA.

Para repetir a prova externa, autenticar uma conta de homologação autorizada fora do chat, rodar o Worker
com a configuração SMTP e a mesma base descartável, e registrar somente os IDs, horários, resultado de
aceite e hash do comprovante. Consultar na caixa autorizada apenas a mensagem sintética pelo `Message-ID`
esperado, sem registrar conteúdo, tokens, credenciais ou mensagens pessoais. Não reutilizar o segredo do
monitor operacional. Remover a credencial do runner ao final e manter apenas evidência sanitizada.

`Delivered` no SMTP comprova aceite remoto, não entrega final. O `Message-ID` estável permite correlação e não
oferece exatamente uma entrega na janela de perda da resposta/commit local. Bounce e deduplicação de provedor
não foram homologados. Não confundir estes testes com os alertas operacionais do monitor do Duora.

## Verificação do transporte

[Execução 39800211694, job transport](https://github.com/BipoXDXD/duora-mail-validation-20261022/actions/runs/39800211694/job/119898440542):
restore da solução com `--locked-mode`, build com analisadores e avisos como erros, **8 testes, 0 erros,
0 falhas, 0 ignorados**. A autoridade de teste também publica uma CRL assinada; a checagem de revogação
continua ativa. O adaptador converte o cancelamento TLS envolvido pelo MailKit em `OperationCanceledException`.

Comandos executados no runner isolado:

```sh
dotnet restore source/backend/Duora.slnx --locked-mode
dotnet restore validation/transport-tests/MailTests.csproj --locked-mode
dotnet build validation/transport-tests/MailTests.csproj -c Release --no-restore
dotnet validation/transport-tests/bin/Release/net10.0/Duora.IntegrationTests.dll -reporter verbose
python3 validation/cross_version.py
```

O RED anterior à implementação foi registrado na execução 39795792462: os quatro testes iniciais falharam
pela seleção SMTP inexistente. O projeto direcionado referencia a Infrastructure real e evita compilar o
decoder de imagens, cuja licença não existe no repositório descartável; nenhuma verificação de licença foi
desativada nem foi copiada chave de produção.

Arquivos do produto alterados: `Directory.Packages.props`, referência e lockfiles dos projetos afetados,
`EmailTransportOptions`, `SmtpTransactionalEmailSender`, registro em `EmailServiceCollectionExtensions`,
testes `SmtpTransportTests`/`SmtpProtocolTests`, comentários da porta/dispatcher e documentação de configuração.
A infraestrutura remota de ensaio está no repositório privado separado; os workflows do produto não mudaram.

Ainda não executados: entrega entre provedores/contas distintos, teste de bounce,
imagens históricas completas e release intermediária somente leitora. São limites da homologação e
pré-requisitos para essas operações, não autorizações de ativação. A CI integral da versão `065c5a8` passou
nos nove checks na [execução 39805175092](https://github.com/BipoXDXD/duora/actions/runs/39805175092), incluindo
3.203 testes do backend e 549 testes de navegação. A integração com alterações posteriores da `main` exige
novamente os nove checks no head atualizado antes do merge. Nenhum deploy, migração de produção ou alteração
dos monitores foi realizado neste ensaio. Nenhuma tentativa de instrução não confiável foi identificada.

## Comprovante de SMTP real

[Execução remota 39803643878](https://github.com/BipoXDXD/duora-mail-validation-20261022/actions/runs/39803643878/job/119910972519),
commit `585ffd0736ae5ab5e64b1a53962c8f183b0191bb` do repositório privado isolado. O workflow manual
`external-smtp.yml` executou restore travado, build do Worker real e do probe, PostgreSQL descartável com
papéis separados, produção do aviso `RecoveryCodeUsedNotice`, envio pelo Worker e conferência IMAP somente
do `Message-ID` esperado. Não foi um envio por Python nem pelo monitor operacional.

- Entrega sintética: `01a16432-df97-74e7-9d71-58ee6c028e64`.
- Outbox: `Delivered`, uma tentativa, sem erro, payload limpo.
- Aceite remoto registrado em `2026-10-22T15:20:20.794156Z` (12h20 de Brasília).
- IMAP: exatamente uma mensagem correspondente encontrada com acesso somente leitura.
- SHA-256 do recibo RFC 822: `55f551014c6e92aa25b2176c2d46724a1b1cf449aaf52d6b7802f279366a3513`.
- Artefato sanitizado: `external-smtp-evidence`, ID `12437435763`, somente IDs, estados e hashes.
- Worker parado e container removido no término; segredo temporário `SMTP_TEST_SETTINGS` excluído e
  ausência conferida. Credenciais locais permanecem privadas conforme solicitado pelo usuário.

O teste leu somente a mensagem sintética encontrada pelo identificador exato; não leu mensagens pessoais,
não marcou como lida nem apagou mensagem. O e-mail do teste permanece na caixa para conferência do titular.
Não houve produção, reinício de serviços, mudança dos monitores ou Docker no Mac.
