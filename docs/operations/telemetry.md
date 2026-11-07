# Telemetria operacional

O produto mantém os coletores de estado sanitizado, transporte HTTPS com mTLS, receptor, monitor de
falhas, validação de capacidade e seus testes. Os dados exportados continuam restritos a estados
booleanos e timestamps; não incluem logs, credenciais nem conteúdo de participantes.

## Registro externo

Instalador, configuração, workspace, imagem, firewall, ativação e rollback do serviço complementar
pertencem ao registro independente `duora-operations-20261021`. Os originais e suas evidências foram
preservados integralmente. Esses arquivos não fazem parte do pacote do Duora. O registro externo é
mantido pelo responsável operacional, separado da produção editorial.

Nenhum serviço existente foi alterado pela separação. Os caminhos instalados `/opt/duora-agent`,
`/opt/duora-telemetry`, `/etc/duora-agent` e `/etc/duora-telemetry` continuam os mesmos. A mudança é nos
caminhos do código-fonte: os componentes genéricos ficam em `scripts/telemetry` e `infra/telemetry`.
Não execute um instalador antigo contra uma árvore renomeada; use o pacote operacional independente
completo e previamente revisado. A instalação ou migração exige seu próprio ensaio e aceite.

## Controles preservados

- Planejamento: 10.112 MiB no perfil completo, 8.512 MiB na fonte e 3.456 MiB no receptor remoto.
  A reserva de 1.536 MiB do serviço externo continua contabilizada; retirá-lo do Git não libera memória.
- Fonte sem listener novo. O receptor exige certificado cliente e origem autorizada, valida schema
  fechado, limite de 16 KiB, TTL de 120 segundos e watermark durável contra replay.
- Monitor independente, deduplicação de alertas e verificação de recuperação. Nenhum sucesso é inferido
  de um container em execução. Ausência ou vencimento de dados é falha explícita.
- Units mantêm limites de memória, privilégios mínimos e caminhos privados inacessíveis.

## Fornecedor da selfie

O alerta `selfie:provider` avisa quando as chamadas do worker à AWS (Face Liveness) estão falhando. Não há rota
nova: `collect.py` roda uma consulta fixa e somente leitura no container `postgres`, com `peer` no socket, como o
`duora-deploy`, e exporta só booleanos. A consulta lê a fila `ops.outbox_deliveries`, nunca de quem é a tentativa.

| Check | Falso significa | Quem decide |
|---|---|---|
| `selfie:provider` | a última chamada ao fornecedor falhou numa tentativa aberta, com marcador gravado há menos de 60 s | `monitor.py`, com histerese |
| `selfie:probe` | a consulta não respondeu; sem ela, o alerta do fornecedor está cego | direto, como os demais checks |

Com a sonda quebrada, `selfie:provider` fica ausente: falta de evidência não é fornecedor caído nem saudável. Com o
worker parado, o marcador envelhece e quem alerta é `service:worker`.

Histerese, com limiares iniciais em `monitor.py`, sem medição de produção:

- abre com falha por 2 min, tolerando intervalos de até 60 s entre leituras ruins;
- intervalo maior recomeça a contagem, então uma falha isolada nunca chega ao Slack;
- fecha depois de 15 min sem falha. `RECUPERADO` significa só isso: com pouco tráfego, pode ser falta de tentativas;
- o estado fica em `state.json` e sobrevive a um reinício do monitor.

Tentativas que falham por causa da pessoa (abandono, captura recusada, resultado inconclusivo) não gravam o marcador
e não alertam. Sem tentativas não há alerta: uma queda sem ninguém tentando passa despercebida. Toda falha de chamada
conta como fornecedor, inclusive credencial errada.

Ao receber o alerta:

1. Veja a causa no worker: `Selfie provider unavailable (<tipo>)`. O log traz só o tipo da exceção.
2. Tipo de autenticação ou permissão aponta para a chave do usuário IAM do worker, não para a AWS fora do ar.
3. Para os demais, confira o AWS Health em `us-east-1`. Não há ação corretiva no Duora durante a queda: as tentativas
   expiram sem contar como reprovação, mas gastam a cota de 5 por hora da pessoa.

O heartbeat externo ignora só `selfie:provider`. Queda de fornecedor externo não diz nada sobre o monitor estar vivo;
qualquer outro check falso continua silenciando o dead-man, inclusive `selfie:probe`.

`selfie:provider` e `selfie:probe` são os únicos checks opcionais do contrato remoto, para emissor e receptor poderem
ser instalados em momentos diferentes. Instale o gateway antes da fonte: um receptor antigo recusa os checks novos.

Execute `python3 -m unittest discover -s scripts/deploy` com as dependências travadas para validar os
orçamentos e as suítes de telemetria. Isso não comprova TLS, firewall, notificações ou capacidade de uma
VPS real; o aceite operacional permanece no registro externo.
