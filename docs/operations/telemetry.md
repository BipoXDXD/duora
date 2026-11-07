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

Execute `python3 -m unittest discover -s scripts/deploy` com as dependências travadas para validar os
orçamentos e as suítes de telemetria. Isso não comprova TLS, firewall, notificações ou capacidade de uma
VPS real; o aceite operacional permanece no registro externo.
