# ADR-0051: Papel `postgres` na sonda do fornecedor da selfie

- **Status:** Proposed (2026-11-07). A coordenação aprovou o papel com as condições da Decisão. Segue `Proposed` porque
  a sonda ainda não foi instalada: nada disto foi conferido em produção, e o aceite vem depois da janela.
- **Origem:** spec 10 ("Credenciais": "monitor e backup com direitos específicos", `spec.md:871`); item B2 da proposta
  de resiliência da selfie; PR #385
- **Revisar quando:** a imagem do PostgreSQL for trocada, o `pg_hba` mudar ou a sonda precisar ler outra tabela

## Contexto

O alerta de fornecedor da selfie precisa ler `ops.outbox_deliveries` a partir do host. A spec pede direitos específicos
para monitoramento, e hoje não existe papel de monitor no banco.

Alternativas consideradas:

- **Papel próprio de leitura.** É o destino certo, mas exige criar o papel e mudar o `pg_hba`, que hoje tem
  `local all all reject` para todo papel que não seja `postgres`. Agora, não reduziria o risco real: o script já roda
  como root com acesso ao Docker.
- **`-c role=` na conexão.** Conectaria como `postgres` e rebaixaria a sessão a um papel de leitura. Não foi testada, e o
  efeito sobre o `pg_hba` e sobre a criação do papel segue sem conclusão.
- **Rota anônima no web.** Superfície HTTP nova, allowlist, rate limit e deploy do backend, para responder um booleano.
- **Ler o log do worker.** Quebra em qualquer mudança de texto.

## Decisão

Vamos usar o papel `postgres` por `peer` no socket do container, como o `duora-deploy`, como exceção à spec, com estes
limites, todos em `scripts/deploy/selfie_provider_alert.py`:

- `default_transaction_read_only=on` na conexão;
- `statement_timeout` de 3 s e `lock_timeout` de 1 s;
- consulta fixa no código, sem parâmetro de entrada, que lê só a fila e devolve um booleano.

A spec não muda. A divergência fica como lacuna aberta. Destino: um papel próprio, só com leitura em
`ops.outbox_deliveries`, entregue numa troca da imagem do PostgreSQL. O #353 hoje não inclui esse papel, e incluí-lo
exige nova qualificação da imagem. A lacuna fecha quando a sonda conectar com esse papel e a prova em
`DatabaseObserverImageTests` passar com ele.

## Consequências

- Positivas: sem rota, sem credencial nova, sem mudança no backend e sem depender da troca da imagem.
- Negativas: uma sonda de monitoramento conecta como superusuário. Os limites vêm da conexão, não do papel.
- Trade-off: `default_transaction_read_only` é um padrão da sessão, não um privilégio. Protege contra engano, não contra
  quem altera o script, que já seria root no host.

## Segurança e operação

Nenhum dado de participante sai da consulta: o resultado é um booleano, e o evento leva texto constante. O script não
recebe segredo. A consulta usa os índices parciais da fila e roda a cada 30 s.

## Compliance

`test_selfie_provider_alert.py` fixa o argv e a conexão por igualdade exata. `DatabaseObserverImageTests` roda o argv real
na imagem derivada, como `999:999`, com o `pg_hba` do repositório, e prova a escrita recusada.
`SelfieProviderProbeTests` roda a consulta contra o schema migrado e o dispatcher real.
