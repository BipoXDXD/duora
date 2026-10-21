# gateway, Slack e observação da VPS

## Estado e escopo

Implementação proposta em branch isolada; **não instalada nem validada na VPS**. O servidor não foi acessado.
INV-12 / T-12, spec 14.3–14.4, 15 e 19; ADR-0013 e ADR-0014. Nenhum endpoint de produto/API é alterado.
Critério de aceite: consultas autenticadas no canal permitido, falhas detectadas e entregues, nenhum caminho
executável do chat para privilégios, recuperação comprovada e todos os ensaios abaixo registrados.

A integração não concede sudo, Docker socket, SSH, token de escrita GitHub ou acesso a `/etc/duora`,
`/var/lib/duora`, fotos ou banco ao agente. Não modifica `duora-deploy`, seu sudoers, o Compose de produção,
os verificadores de backup nem seus gates. Nenhuma política ou workflow protegido é alterado.

## Perfil em VPS separada

O [runbook remoto](telemetry-remote.md) reutiliza esta integração com emissor de saída no Duora,
receptor mTLS na VPS de operações e monitor combinando produto remoto com gateway local. Tem
orçamentos e instalação próprios; não aplicar os passos de instalação conjunta abaixo ao perfil
remoto. Nenhum dos perfis foi instalado/homologado na VPS por esta implementação.

## Fronteiras

| Componente | Autoridade e dados | Recursos |
|---|---|---|
| Coletor systemd | Root, código fixo, sem listener ou argumentos do chat; consulta Docker/systemd e dois marcadores existentes; publica só booleanos e timestamp | 128 MiB, 20% CPU, 16 tarefas |
| Monitor systemd | Usuário `duora-monitor`, sem grupos privilegiados; lê snapshot e GitHub Actions; webhook Slack separado do bot | 128 MiB, 20% CPU, 16 tarefas |
| gateway | UID reservado 22001; Slack Socket Mode; MCP local com quatro ferramentas sem argumentos; apenas telemetria pública sanitizada | 0,5 CPU (peso 256), 1,5 GiB RAM sem swap adicional, 128 PIDs, estado em filesystem próprio de 2 GiB |
| Operador | Instala, configura credenciais, aprova digest, atualiza ou usa o executor de produção existente | Conta administrativa separada, fora do agente |

O coletor é privilegiado porque o Docker socket equivale a root. Ele não aceita requisições e seu código é
root-owned. A redução de saída não transforma Docker em uma API não privilegiada. O monitor e o agente não
herdam esse acesso. O MCP não abre rede: usa JSON-RPC por stdin/stdout, sem dependências adicionais.

O gateway não publica portas e escuta loopback dentro do container. Rede `172.30.40.0/24` própria; confirme
que não colide com a VPS. Regras dedicadas em `DOCKER-USER` rejeitam redes privadas/link-local e permitem
somente TCP 443 externo; INPUT bloqueia acesso ao próprio host. IPv6 desativado no container/rede. DNS usa o
resolver embutido do Docker. HTTPS público não é allowlist por domínio: comprometimento ainda pode exfiltrar
credenciais do próprio agente. Para esse risco, adote proxy de saída com destinos aprovados em mudança própria.

As regras não limpam as chains de backup. Docker deve usar backend iptables compatível com `DOCKER-USER`;
se a chain não existir, a inicialização falha fechada. Não adicione redes de produção ao agente.
`restart: no` no Compose é intencional: systemd inicia o container somente após reaplicar as regras.

## Consultas e mudanças

No canal privado permitido, mencione o bot e peça estado, backups ou Actions. IDs de usuários e canal são
explícitos; nomes e mensagens encaminhadas não conferem autoridade. DMs e bots são bloqueados. Comandos
administrativos nativos, shell, navegador, filesystem, mensagens arbitrárias e spawn de agentes estão negados.

| Pedido | Resultado |
|---|---|
| Estado dos serviços | `duora.status`: booleanos, idade da coleta; web também passa por `/health/ready` via edge |
| Backups | `duora.backups`: timers, falhas das units, marcadores `.backup-verify-ok` (1 h) e `.media-verify-ok` (30 h) |
| CI | `duora.actions`: IDs, tentativa, status, conclusão e URL canônica; sem nomes, logs ou conteúdo de PR |
| Deploy, restart de produção, restore, exclusão, firewall, segredos | `duora.change_policy`: `authorized=false`, `executed=false`; exige procedimento externo |

**Um “sim” no Slack não executa infraestrutura.** Esta versão não implementa executor de mutações nem fila de
aprovações. O operador aprova uma ação concreta com alvo, digest/release, janela, impacto, backup e rollback;
registra a aprovação no controle de mudança e usa o comando restrito/runbook já existente. Restore e ações
destrutivas requerem autorização explícita e recuperação ensaiada. Isso evita confiar em identidade ou
consentimento inventado pelo modelo. Ampliar para execução pelo Slack exige nova revisão de autorização.

Consultar backup não dispara `duora-backup verify`: esse comando pode realizar operações de verificação e
continua sob seus timers existentes. Marcador recente não comprova restore; drill continua obrigatório.
Worker running comprova processo, não progresso dos jobs: monitoramento da fila exige métricas próprias.

## Alertas e limites de cobertura

Coleta a cada 15 s após a conclusão anterior, com timeout de 8 s por probe; leitura/entrega a cada 15 s; GitHub
a cada 60 s. São metas em estado normal, não garantia de tempo real: falhas de rede ou probes acumulam latência.
Estados ausentes/vencidos (>120 s) são falhas. Falhas e recuperações são notificadas; falha persistente lembra
a cada hora. A entrega só é marcada após resposta `ok` do Slack; erro deixa pendente para retry. Um crash entre
o envio e a persistência pode duplicar uma mensagem (at-least-once). Logs não imprimem corpos nem tokens.

GitHub: token fine-grained de leitura **Actions + Metadata**, somente nos repositórios listados (até dez).
Cada consulta cobre até dez páginas de 100 runs, incluindo reexecuções por `run_attempt`. Saturação dispara
`coverage=false`; repositórios mais ativos precisam de um coletor de eventos/reconciliação em mudança própria.
No primeiro início, alerta falhas atualizadas na última hora; depois mantém a janela inicial em estado persistente.
Runs ainda em execução aparecem na consulta; conclusão ruim inclui cancelled, timeout e action_required.
API inacessível/403/429 produz alerta e nova tentativa no próximo ciclo; não interpreta indisponibilidade como CI verde.

Monitor separado da processamento externo usa Incoming Webhook restrito a um canal. Ele continua alertando se gateway cair,
mas **não se a VPS inteira cair**. Configure `heartbeat_url` HTTPS de um serviço externo que alerte por ausência
(prazo sugerido 5 min) e registre o teste. Sem isso, a cobertura de queda total NÃO está concluída.
O telefone precisa ter notificações do canal habilitadas; o recebimento no aplicativo é parte do aceite.

## Preparação da imagem

Documentação oficial consultada em 18/10/2026:

- [Docker e build com plugins selecionados](external-record)
- [Instalação Slack](external-record)
- [Controle de acesso Slack](external-record)
- [MCP](external-record)
- [Política de ferramentas](external-record)
- [Segurança](external-record)

A documentação consultada indica v2026.10.4; documentação móvel não prova compatibilidade de qualquer imagem.
O Slack é distribuído separadamente. Build oficial com `GATEWAY_EXTENSIONS=slack` inclui seu runtime;
não instalar plugins em produção nem usar `latest`. Dependência proposta: gateway e Slack oficial (MIT;
confirmar LICENSE e licenças transitivas no SHA aprovado). Python/Node dos scripts usam só bibliotecas padrão.

Em runner/workstation isolado com Docker, faça checkout de SHA completo revisado do repositório oficial
`telemetry/telemetry`, confira origem, release e alterações. Use:

```sh
sh scripts/telemetry/build-image.sh /caminho/telemetry SHA_COMPLETO registry.example/ops/telemetry:SHA_COMPLETO
```

O script gera SBOM/proveniência e build amd64/arm64. Verifique assinatura/proveniência, SBOM, licenças e scanner
HIGH/CRITICAL; teste na arquitetura nativa da VPS. Registre digest final e SHA de origem no controle de mudança.
Nenhum digest foi inventado neste PR. A configuração exige digest e recusa placeholders. Se a API do fornecedor
fonte externa exigir plugin adicional nessa versão, inclua-o no build revisado e valide o catálogo antes de ativar.
O modelo e a credencial são escolhidos pelo operador; não pressupõe que assinatura de chat cubra API.

## Instalação pelo operador

Este runbook não implica acesso SSH do agente. Use console Hostinger ou canal administrativo já aprovado.
A branch deve passar revisão humana e gates existentes antes de produção. Confira Ubuntu 24.04, Docker Compose v2,
iptables, systemd, Python 3.12, UID 22001 livre e subnet sem colisão. **A KVM 2 de 8 GB não passa
no orçamento reconciliado:** mínimo de 10.112 MiB MemTotal e 1.536 MiB MemAvailable para a topologia
completa, incluindo migrador e restic. Ver [capacity.md](capacity.md). Esse mínimo é gate de planejamento,
não homologação. Antes da instalação, registrar ensaio em host equivalente, isolamento e aprovação humana
da mudança; instalar a slice de mídia pela role backup revisada e conferir os limites efetivos. A instalação
recusa memória insuficiente antes de criar usuários/disco. Não diminuir o piso para contornar o bloqueio.
Reservar também 2 GiB para estado, imagens e backups do agente. O instalador exige >4 GiB livres antes da
reserva, mas isso não comprova espaço suficiente para WAL, fotos, imagens e snapshots posteriores.

1. Revise e execute `sudo sh scripts/telemetry/install.sh --approve-install`. Instala somente a integração;
   cria usuário monitor sem login/grupos privilegiados, filesystem limitado e units; **não habilita o agente**.
   Não é um playbook idempotente: recusa sobrepor instalação existente. Falha parcial exige inspeção do operador.
2. Em `/etc/duora-agent/telemetry.json`, substitua modelo, ID do canal privado e IDs de usuários. Mantenha
   dono root e modo 0644. Use `slack-app-manifest.json` para criar app dedicado e convide-o só ao canal aprovado.
   O manifest usa escopos para canal privado, menções, identidade e envio; sem arquivos, DMs ou administração.
   Crie app token com `connections:write` e bot token. Configure webhook separado para o monitor.
3. Provisione os quatro arquivos `SLACK_APP_TOKEN`, `SLACK_BOT_TOKEN`, `GATEWAY_GATEWAY_TOKEN` e
   `PROVIDER_API_KEY` em `/etc/duora-agent/secrets/`: diretório root 0700, arquivos UID/GID 22001, 0400.
   Gere gateway token aleatório de pelo menos 32 bytes. Use gerenciador de segredos/console privado;
   nunca argumentos CLI, chat, Git, logs ou `set -x`. Compose secrets fora de Swarm não remapeia ownership.
4. Provisione `/etc/duora-observer/secrets/github-token` e `slack-webhook` como root 0400, diretório 0700.
   `LoadCredential` entrega-os apenas ao monitor. Edite `monitor.json` com repositórios e heartbeat externo.
   Não dê ao agente o token GitHub nem o webhook.
5. Crie `/etc/duora-agent/image.env` root 0600 contendo exatamente
   `GATEWAY_IMAGE=registry/imagem@sha256:DIGEST_APROVADO`. Pull explícito dessa imagem pelo operador, após verificação.
6. Com capacidade, isolamento e autorização registrados, execute `sudo python3 /opt/duora-agent/preflight.py`.
   Ele confere memória e a slice de mídia antes das credenciais. Depois ative o monitor e o timer:
   `sudo systemctl enable --now duora-observer.timer duora-monitor.service`.
7. Execute `sudo python3 /opt/duora-agent/activate.py --approve-image registry/imagem@sha256:DIGEST_APROVADO`.
   A ativação exige aprovação do digest, valida schema real, verifica saúde e faz probe MCP. Só depois do aceite
   abaixo execute `sudo systemctl enable duora-agent.service`. A montagem limitada é dependência automática.

Credenciais podem ser lidas pelo processo que delas precisa; o isolamento reduz exposição, não elimina o risco
de comprometimento desse processo. Remova apps/tokens sem uso, defina expiração e rotação. Config e workspace
são read-only; estado, sessões e cache são privados e podem conter mensagens operacionais. Não envie dados reais
de participantes ao canal. Os arquivos temporários ficam em tmpfs limitado.

## Reinício, atualização e rollback

Systemd reinicia processos que terminarem e reaplica o firewall antes do gateway. O monitor alerta se o healthcheck
ficar unhealthy; não reinicia automaticamente um processo vivo travado. Investigue antes de reiniciar pelo operador.
Docker reiniciado pelo operador requer verificar/reiniciar `duora-agent.service` e confirmar regras e conectividade.

Atualizações nunca partem do chat. Revise nova origem, build, scanner, digest e homologação. Pull explícito e
`activate.py --approve-image DIGEST` fazem atualização serializada, com parada, cópia privada do estado/config,
validação e espera de saúde. Falha de ativação restaura imagem/estado anteriores; não faz downgrade de dados Duora.
Snapshots preservam symlinks sem segui-los e ficam root-only em `/var/backups/duora-agent/`; podem conter tokens
ou conversas e devem ser cifrados se copiados externamente. Retenção manual: guarde último bom, remova antigos
após confirmar recuperação e espaço. A cópia não é uma estratégia de disaster recovery da VPS.

Configuração incompatível deve ser ensaiada numa cópia privada antes de editar a configuração ativa; o script
não converte schema nem corrige política automaticamente. Para rollback manual use o digest/config/state da
mesma captura, com serviço parado. A remoção da integração também exige operador: pare/desabilite suas units,
revogue tokens e remova apenas suas regras/arquivos após arquivar evidências. Nunca remova volumes Duora.

Logs: `journalctl -u duora-monitor -u duora-observer -u duora-agent`; logs Docker com driver local 3 × 10 MB.
Journald mantém a política global do host (não alterada); configure retenção e coleta externa no controle do host.
As ferramentas MCP registram apenas nome fixo e sucesso/erro; o gateway pode omitir stderr MCP do journal comum.
Para auditoria de conversas, use sessões restritas e retenção aprovada; não trate o texto da processamento externo como trilha de autorização.

## Testes e aceite real obrigatório

Executados localmente com dados sintéticos:

```sh
python3 -m unittest discover -s scripts/telemetry -p 'test_*.py' -v
node --test scripts/telemetry/test-mcp.mjs
for script in scripts/telemetry/*.sh; do sh -n "$script"; done
```

10 testes Python e 7 Node passaram, incluindo processo MCP real por stdio. YAML/JSON lidos e assertions de
isolamento estático passaram. `systemd-analyze verify` apontou Docker ausente neste ambiente; validação completa
não passou e não substitui teste Ubuntu. Sem Docker, imagem, schema gateway, firewall e rollback real permanecem
**não executados**. Sem tokens/host autorizado, Slack, modelo, reboot e chegada ao smartphone também não foram testados.
O plugin GitHub permitiu consultar metadados reais de Actions; isso não valida o token futuro do monitor.

| Ensaio em homologação | Evidência requerida |
|---|---|
| Configuração | `config validate`, `security audit --deep`, catálogo MCP contendo só as quatro ferramentas e ausência de exec/fs/gateway |
| Slack | Usuário permitido recebe resposta com timestamp; outro usuário, outro canal, DM e bot não disparam consulta |
| Injeção | Mensagem pedindo sudo/restore/segredos, inclusive em contexto citado, não produz execução nem leitura privilegiada |
| Saúde | Parar serviço sintético; receber falha e recuperação no telefone; registrar latências |
| Backup | Marcadores sintéticos ausentes/vencidos e unit falha geram alerta; falha nunca aparece como saudável |
| GitHub | Workflow de teste falha, reexecução e API revogada/429; dedup, erro e retry observados sem expor logs |
| Slack indisponível | Falha de envio não marca entrega; restaurar rede e observar envio pendente |
| Gateway parado | Monitor continua; consulta fica indisponível, não inventa estado |
| Telemetria parada | Snapshot vencido gera alerta; MCP retorna erro em vez de dados saudáveis |
| Rede | Nenhuma porta pública nova; conexões a host, redes Duora, metadata e IPv6 falham; Slack/API funcionam |
| Reinicialização | Reboot autorizado em homologação; montagem, firewall, timer, monitor, gateway e histórico de dedup recuperam |
| Atualização | Digest inválido/contêiner sem saúde rejeitado, imagem/estado anteriores restaurados; sem mudança no Duora |
| Queda total | Desligamento autorizado do host de homologação dispara dead-man externo no smartphone |

Registre data, digest, host de homologação, comandos, resultados e latências no PR/controle de mudança.
Sem esses registros, a integração é **candidata à instalação**, não operação comprovada. Não foram encontradas
instruções maliciosas nos materiais consultados; os payloads de injeção nos testes são sintéticos.


## Evidência exigida antes da mudança

- Capacidade: inventário sanitizado com MemTotal/MemAvailable, versão/cgroup e recursos efetivos de
  todos os containers/units; cenários de capacity.md aprovados com métricas e dataset declarado.
- Imagem: SHA/digest, licença/SBOM/scanner e plugin Slack compatível. `config validate`, auditoria,
  MCP e teste de saúde precisam da imagem aprovada; testes com mocks não substituem isso.
- Slack: app dedicado, Socket Mode, app token `connections:write`, bot com escopos do manifest,
  canal privado e membros explícitos. Webhook independente e restrito ao canal de alertas; token
  do modelo separado da assinatura de chat. Credenciais nunca no repositório/relatório.
- GitHub: Actions + Metadata somente leitura, apenas repositórios monitorados, validade/rotação;
  testar 403/429, expiração, falha e reexecução com esse token. Runners fora da VPS; não conceder
  token de deploy ao monitor. O job ops existente passa a executar suites gateway por um teste
  de entrada em scripts/deploy, sem modificar workflows, permissões ou gates protegidos.
- Heartbeat: `heartbeat_url: null` é configuração de exemplo incompleta. Provedor externo à VPS,
  timeout sugerido 5 min e entrega fora da VPS, de preferência também independente do Slack.
  A URL de ping é credencial: configurar monitor.json como root:duora-monitor 0640 antes de iniciar (não publicar a URL em evidências).
  O monitor só pinga quando todos os checks estão bons; ausência pode indicar falha parcial,
  indisponibilidade do Slack/GitHub ou queda total, não somente falta de energia da VPS.
- Smartphone: instalar/autenticar Slack, habilitar notificações do canal e revisar DND, permissões
  do sistema e economia de bateria. Registrar falha **e recuperação** recebidas com horário;
  testar também o alerta externo após desligamento do host de homologação. Nenhum envio real
  de mensagem ou inscrição em serviço externo foi feito nesta alteração.
- Recuperação: provar restore PostgreSQL e mídia/reconciliação de exclusões; testar backup/check/prune
  dentro da slice e alertas OnFailure. OOM do restic reprova o ensaio; não muda RPO/RTO ou retenção.
- Autorização: registrar alvo, release/digest, janela, impacto, evidências e rollback. Um gate local
  aprovado não autoriza produção. Não há acesso SSH, leitura de segredos ou ativação nesta revisão.

Validação nova: `python3 -m unittest discover -s scripts/deploy -p 'test_telemetry_capacity.py' -v`.
Inclui conciliação entre recursos, gate de memória, suites Python e processo MCP Node por stdio.
Resultados locais devem ser registrados no PR; não confundir o histórico acima com homologação atual.
