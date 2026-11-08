# Alerta de fornecedor da selfie

Avisa por e-mail quando as chamadas do worker à AWS (Face Liveness) ficam falhando ou quando a fila da selfie para de
andar. **Não está instalado.** A instalação exige janela própria e aval, sem data definida. Até lá, este alerta não
entrega nada em produção.

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
| `selfie_provider_down` | 2 min de falha, tolerando até 100 s entre leituras ruins (uma leitura perdida no pior intervalo do timer, 47 s); intervalo maior recomeça a contagem |
| `selfie_provider_recovered` | a última falha observada tem 15 min ou mais. Não garante 15 min observados: as leituras podem ter parado, e com pouco tráfego pode ser só falta de tentativas |
| `selfie_queue_stalled` | 2 min desde a primeira leitura com a fila parada. Uma leitura saudável recomeça a contagem; leitura perdida não zera nem encerra, então os 2 min não são de leituras contínuas |
| `selfie_queue_resumed` | primeira leitura saudável depois de `selfie_queue_stalled` |
| `selfie_probe_blind` | 10 min seguidos sem conseguir ler a fila |
| `selfie_probe_restored` | primeira leitura boa depois de `selfie_probe_blind` |

Sonda quebrada é falta de evidência: nem abre nem fecha o alerta, sai com erro e deixa a unit em `failed`.

## O que o alerta vê

Vê falha numa tentativa aberta, marcada pelo worker há cerca de 65 s ou menos. Com o worker parado, o marcador envelhece
e esta leitura fica calada: quem cobre esse caso é a leitura de fila parada, abaixo.

Não alertam, de propósito: abandono, captura recusada e resultado inconclusivo, que não gravam o marcador. Sem
tentativas também não há alerta.

Falsos negativos, em que algo falhou e este alerta não vê:

- streaming do navegador para a AWS, que não passa pelo worker;
- STS no `/capture`, que roda no web e não grava o marcador;
- sessão `FAILED` na AWS: é uma resposta válida, e a tentativa termina `Expired`;
- exceção fora da lista transitória do dispatcher: sobe ao laço do worker, sem marcador;
- tentativa que falha por menos de 2 min até expirar.

Na lista transitória entra todo `AmazonServiceException`, então credencial errada aparece como falha do fornecedor.

## Fila parada

A mesma consulta responde se há entrega da selfie esperando há 60 s ou mais (`Pending` pronta, ou `InFlight` com o lease
vencido) sem nenhuma entrega da selfie com lease vivo. O lease vivo prova só que houve um claim recente, não que a entrega
andou. Com fornecedor lento, uma fila longa quase sempre tem uma chamada em andamento e não aparece como parada.

Cobre o worker parado e a vaga presa: a chamada que o corte de 10 s abandonou e que ignora o cancelamento segura a vaga
até terminar, e esse worker não toma mais entregas.

Não dispara, embora a fila possa não estar andando:

- fila vazia: sem trabalho esperando não há o que acusar;
- réplica presa com outra andando: o lease vivo da outra basta;
- crash-loop que faz claim a cada menos de 30 s: o lease fica vivo e o alerta não dispara, até a entrega esgotar as 40
  tentativas e encerrar como `Failed` ou `Expired`;
- exceção depois do claim, fora da lista transitória: o lote seguinte toma outra entrega, ou a mesma quando o lease vence,
  com o mesmo efeito.

Pode disparar sem defeito:

- backlog com fornecedor rápido: a chamada dura pouco e o laço pausa 1 s entre lotes, então as leituras costumam não ver
  lease vivo. Isso exige entregas prontas há 60 s, o que, com uma entrega por lote, 1 s de pausa e chamadas de até 300 ms,
  pede cerca de 45 a 60 na fila (cálculo, sem medição);
- `SelfieVerification:Enabled=false` com entrega `Pending` residual: o dispatcher não toma nada, o alerta abre e não fecha
  enquanto essa entrega existir.

## O que fica sem cobertura

O script só avisa enquanto roda, e não há sinal de vida. Nenhum e-mail chega se:

- o timer parar ou for removido, o host cair, ou o Python não iniciar;
- o outbox estiver ausente ou inacessível, ou o disco cheio: a unit falha a cada execução, mas o evento não existe;
- o `mail.py` ou o Gmail falharem.

Nesses casos só resta olhar o host: `systemctl list-timers`, `systemctl --failed` e
`journalctl -u duora-selfie-provider-alert`. Os quatro Healthchecks externos não acompanham este script. Um quinto check
está permitido, mas exige PR próprio: o script não faz ping, e a unit bloqueia rede com `RestrictAddressFamilies=AF_UNIX`.

## Ao receber `selfie_provider_down`

1. Veja a causa no worker: `Selfie provider unavailable (<tipo>)`. O log traz só o tipo da exceção.
2. Tipo de autenticação ou permissão aponta para a chave do usuário IAM do worker, não para a AWS fora do ar.
3. Para os demais, confira o AWS Health em `us-east-1`. Não há ação corretiva no Duora durante a queda: as tentativas
   expiram sem contar como reprovação, mas gastam a cota de 5 por hora da pessoa.

## Ao receber `selfie_queue_stalled`

1. Procure no log do worker `is still running; this worker makes no new call until it ends`: é a vaga presa. A vaga vive
   em memória, então só o reinício do worker a libera.
2. Sem essa linha, confira se o worker está de pé. Erro no laço da selfie só deixa a fila parada quando acontece antes do
   claim, como banco inacessível: depois do claim, ver os limites em "Fila parada".
3. Reiniciar o worker é ação em produção: siga a autorização e a janela vigentes.

## Ao receber `selfie_probe_blind`

O alerta do fornecedor está cego. Confira se o container `postgres` responde e se `docker ps` com os rótulos da consulta
acha exatamente um container. Depois, veja o erro fixo no journal da unit.
