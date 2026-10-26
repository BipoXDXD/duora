# Preservação mínima da validação de e-mail

A [derivação sanitizada](mail-validation-preservation.json) preserva os resultados essenciais de dois runs
do laboratório temporário `BipoXDXD/duora-mail-validation-20261022`: **39803643878** (Worker, SMTP e IMAP na
mesma conta Gmail) e **39829188122** (28 testes SMTP/Resend e seis cenários Infrastructure/PostgreSQL/pickup).
Ela contém commits, IDs/URLs das execuções e hashes dos artefatos; não é o artefato original nem uma nova
homologação. Não inclui destinatário, cabeçalhos, corpos, configurações reais, credenciais ou dados pessoais.

As duas fontes completas originais, os ZIP/JSON dos dois artefatos e seus metadados foram preservados em
arquivo local protegido e verificado pelo mantenedor. SHA-256 do arquivo:
`c33aeeff18c7850ebb89689e92ea6651b18517c6afcbd089f384cec7b8c69648`.
Isso não é backup de todo o histórico Git/Actions. Os links originais deixam de funcionar se o repositório
temporário for excluído; os IDs e hashes são mantidos como proveniência. Não se promete preservar os logs
de todas as tentativas. Os originais privados não são importados para este repositório público.

## Reutilização sem duplicar a aplicação

O [harness mínimo](../../scripts/testing/mail-outbox/) preserva o probe e os seis cenários de outbox. As
fontes são obtidas por referências já alcançáveis da main do Duora: `a10194a` (anterior à #139), `a4711d6`
(#139) e `d7ed56b` (SMTP integrado). Os 28 testes de transporte já estão em
`backend/tests/Duora.IntegrationTests/Outbox/`; não são copiados para outro projeto.

O [modelo de workflow](../../scripts/testing/mail-outbox/workflow.example.yml) fica fora de
`.github/workflows`: **é inativo**. Ele mostra os checkouts fixados, a montagem do diretório descartável,
build dos probes e PostgreSQL/pickup em runner hospedado. Uma eventual promoção exige PR específico e
revisão técnica; não altera os workflows, gates, runtime ou implantação atuais. Não tem secrets, endpoints
externos de e-mail ou dispatch automático. O script recusa execução local/self-hosted antes de criar
artefatos ou iniciar processos; o destinatário do probe é sempre sintético. Não rodar Docker no Mac.

O modelo usa o commit SMTP integrado no lugar do snapshot modificado do laboratório antigo. Uma futura
execução será uma nova qualificação, não uma reprodução exata dos resultados históricos. As fontes exatas
dos runs antigos permanecem somente no arquivo local protegido. O harness de SMTP/IMAP externo não foi
publicado: novos envios dependem de plano e autorização específicos; a prova existente não deve ser repetida
sem necessidade. O teste offline de recusa local pode ser executado com:

```sh
python3 -m unittest discover -s scripts/testing/mail-outbox -p 'test_*.py' -v
```

## Pendências mantidas

A homologação externa completa continua pendente: entrega entre contas/provedores e bounce não foram
comprovados; pickup não demonstra entrega externa. SMTP permite duplicata após aceite remoto ambíguo.
A prova entre versões usa Infrastructure e não imagens completas do Worker; uma release intermediária
somente leitora e o ensaio operacional completo ainda faltam. Consumidor compatível antes do produtor;
confirmar processamento e recibos por ID antes de reverter o worker. Fila vazia não comprova entrega.
As issues #68/#82 permanecem no Duora; esta preservação não conclui essas homologações nem as da #93.
