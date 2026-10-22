# ADR-0038: Transporte SMTP opcional e limites de deduplicação

- **Status:** Proposed; implementação e homologação isoladas, revisão humana e ativação pendentes
- **Origem:** [#68](https://github.com/BipoXDXD/duora/issues/68), [PR #207](https://github.com/BipoXDXD/duora/pull/207), [ADR-0019](0019-implementacao-identidade-sessao-outbox.md), spec 10.6 e TM-21.
- **Revisar quando:** Provedor, autenticação, semântica de retry, política de TLS ou garantia de deduplicação mudar.

## Contexto

O adaptador pickup grava um arquivo por ID de entrega e deduplica o efeito local. O ADR-0019 descreveu essa
propriedade como ausência de e-mails duplicados. Essa garantia não se transfere a um servidor SMTP: ele pode
aceitar a mensagem antes de o Worker perder a resposta ou falhar na conclusão local. A fila com lease e
fencing protege as alterações no banco; não desfaz um efeito remoto já aceito.

O responsável aprovou implementar transporte SMTP opcional e testes remotos descartáveis, sem produção.
A aprovação do desenho e as evidências isoladas não equivalem à aprovação de ativação operacional.

## Decisão proposta

Preservar `Pickup` como padrão e adicionar `Email:Transport=Smtp`, sem alterar Compose/configuração de
produção. MailKit/MimeKit 4.18.1 (MIT) fazem protocolo, MIME e autenticação; PKCS 10.0.13 é fixado para remover
a versão transitiva vulnerável, mantendo audit/gates ativos. Não implementar um cliente SMTP próprio.

Exigir autenticação, TLS implícito (`SslOnConnect`) ou STARTTLS obrigatório (`StartTls`), certificado válido e
checagem normal de revogação. Não oferecer envio em texto claro, TLS oportunista, callback que aceite
certificado inválido ou log de protocolo. Credenciais vêm da configuração privada por arquivo de segredo.

Cada entrega usa uma conexão própria e o prazo/cancelamento existentes da outbox. O ID da entrega gera um
`Message-ID` estável para correlação. O adaptador retorna depois do aceite de DATA sem depender de QUIT;
falha de encerramento após esse aceite não deve causar retry. O SMTP continua **at-least-once**, sujeito à
janela de aceite remoto sem confirmação local. `Message-ID` não é uma chave de idempotência garantida pelo
provedor. `Delivered` significa aceite do servidor, não recebimento final, ausência de bounce ou leitura.

Esta proposta substitui parcialmente apenas as afirmações irrestritas de deduplicação do ADR-0019 quando
SMTP estiver selecionado. Pickup mantém sua deduplicação por arquivo; persistência transacional, leases,
fencing, expiração e política de retry permanecem iguais. Não se aplica ao monitor operacional de e-mail.

## Consequências

- Há transporte SMTP real sem mudar a gravação transacional do Web nem conceder ao Worker runtime web.
- Mensagens e credenciais não entram no log: o dispatcher mantém somente ID e tipo técnico da falha.
- Há risco residual de duplicata após aceite sem confirmação/commit; mitigação adicional depende de um
  provedor com contrato de idempotência e nova decisão. Bounce e entrega entre provedores não foram homologados.
- Rollback entre versões exige consumidor compatível antes do produtor, release intermediária elegível,
  drenagem comprovada por entrega e revisão humana; nenhuma dessas operações foi ativada em produção.

## Compliance

`SmtpTransportTests` e `SmtpProtocolTests` exercitam a composição real, configuração inválida, TLS/STARTTLS,
MIME UTF-8, autenticação/certificado recusados e cancelamento. A autoridade sintética publica CRL assinada e é
removida ao final do teste; validação e revogação não são desativadas.

O [relatório de homologação](../../testing/smtp-outbox-homologation.md) registra os oito testes SMTP, seis
cenários com versões distintas contra PostgreSQL descartável e um aviso pelo Worker real com aceite Gmail e
um recibo IMAP. Um envio observado uma vez não prova deduplicação nos retries nem entrega entre provedores.
Os gates integrais da CI e revisão humana continuam obrigatórios antes de merge; esta proposta não autoriza
deploy, habilitação de SMTP ou consulta de dados de produção.
