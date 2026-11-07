# Alerta de fornecedor da selfie

Avisa por e-mail quando as chamadas do worker à AWS (Face Liveness) ficam falhando. **Não está instalado.** A
instalação exige janela própria e aval, sem data definida. Até lá, este alerta não entrega nada em produção.

## Como funciona

`scripts/deploy/selfie_provider_alert.py` roda como root em timer de 30 s, só com a stdlib. Sem rota nova: faz uma
consulta fixa e somente leitura no container `postgres`, com `peer` no socket
([ADR-0051](../architecture/adr/0051-papel-postgres-na-sonda-da-selfie.md)). A consulta lê `ops.outbox_deliveries`,
nunca de quem é a tentativa.

Nas transições, grava um evento em `/var/lib/duora-next-deploy-monitor/outbox/`, de onde o `mail.py` do host envia pelo
Gmail. O estado da histerese fica em `/var/lib/duora-selfie-alert/state.json`. Estado ilegível vale como ausente, com
log.

Limiares iniciais, sem medição de produção:

| Evento | Quando |
|---|---|
| `selfie_provider_down` | 2 min de falha, tolerando até 60 s entre leituras ruins; intervalo maior recomeça a contagem |
| `selfie_provider_recovered` | 15 min sem falha. Com pouco tráfego, pode ser só falta de tentativas |
| `selfie_probe_blind` | 10 min seguidos sem conseguir ler a fila |
| `selfie_probe_restored` | primeira leitura boa depois de `selfie_probe_blind` |

Sonda quebrada é falta de evidência: nem abre nem fecha o alerta, sai com erro e deixa a unit em `failed`.

## O que o alerta vê

Vê falha numa tentativa aberta, marcada pelo worker há cerca de 65 s ou menos. Com o worker parado, o marcador envelhece
e esta sonda fica calada: ela não alerta worker parado.

Não alertam, de propósito: abandono, captura recusada e resultado inconclusivo, que não gravam o marcador. Sem
tentativas também não há alerta.

Falsos negativos, em que algo falhou e este alerta não vê:

- streaming do navegador para a AWS, que não passa pelo worker;
- STS no `/capture`, que roda no web e não grava o marcador;
- sessão `FAILED` na AWS: é uma resposta válida, e a tentativa termina `Expired`;
- exceção fora da lista transitória do dispatcher: sobe ao laço do worker, sem marcador;
- tentativa que falha por menos de 2 min até expirar.

Na lista transitória entra todo `AmazonServiceException`, então credencial errada aparece como falha do fornecedor.

## O que fica sem cobertura

O script só avisa enquanto roda, e não há sinal de vida. Nenhum e-mail chega se:

- o timer parar ou for removido, o host cair, ou o Python não iniciar;
- o outbox estiver inacessível ou o disco cheio: a unit falha, mas o evento não existe;
- o `mail.py` ou o Gmail falharem.

Nesses casos só resta olhar o host: `systemctl list-timers`, `systemctl --failed` e
`journalctl -u duora-selfie-provider-alert`. Os quatro Healthchecks externos não acompanham este script.

## Ao receber `selfie_provider_down`

1. Veja a causa no worker: `Selfie provider unavailable (<tipo>)`. O log traz só o tipo da exceção.
2. Tipo de autenticação ou permissão aponta para a chave do usuário IAM do worker, não para a AWS fora do ar.
3. Para os demais, confira o AWS Health em `us-east-1`. Não há ação corretiva no Duora durante a queda: as tentativas
   expiram sem contar como reprovação, mas gastam a cota de 5 por hora da pessoa.

## Ao receber `selfie_probe_blind`

O alerta do fornecedor está cego. Confira se o container `postgres` responde e se `docker ps` com os rótulos da consulta
acha exatamente um container. Depois, veja o erro fixo no journal da unit.
