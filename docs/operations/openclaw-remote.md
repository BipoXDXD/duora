# OpenClaw em VPS separada

## Estado, finalidade e fronteiras

Perfil candidato à homologação, não instalado nem ativado. Complementa [openclaw.md](openclaw.md),
sem reconstruir Slack, MCP, build da imagem, firewall do gateway ou rollback existentes.
INV-12/T-12, ADR-0013/0014 e [ADR-0031](../architecture/adr/0031-openclaw-remoto-telemetria-mtls.md).
Nenhuma API do produto muda. Aceite: somente o emissor autorizado publica o contrato público; falhas
não viram saúde; gateway continua sem caminho para administração/dados do Duora.

| Host/processo | Autoridade | Recursos máximos |
|---|---|---|
| Duora: coletor fixo | Root consulta estados Docker/systemd/marcadores; sem listener ou input do chat | 128 MiB, 20% CPU |
| Duora: emissor `duora-shipper` | Lê somente snapshot sanitizado; conexão de saída HTTPS com certificado cliente | 64 MiB, 10% CPU |
| Operações: receptor `duora-receiver` | Recebe só `/v1/telemetry`; escreve `/var/lib/duora-remote/host.json` | 128 MiB, 20% CPU |
| Operações: coletor local fixo | Root consulta somente estado/health do gateway, sem probes do produto | 128 MiB, 20% CPU |
| Operações: monitor `duora-monitor` | Combina produto remoto + gateway local, consulta Actions, notifica Slack | 128 MiB, 20% CPU |
| Operações: gateway UID 22001 | Mesmo Slack/MCP de leitura, quatro ferramentas sem argumentos | 1.536 MiB, 0,5 CPU, 128 PIDs |

Não montar filesystem, Docker socket ou rede administrativa entre hosts. O gateway só lê o snapshot
publicado pelo monitor; não lê certificados de transporte, token GitHub, webhook ou segredos do Duora.
O receptor e o emissor têm contas sem login/grupos privilegiados, `LoadCredential`, sem capabilities,
filesystem protegido, limites sem swap adicional e logs fixos sem corpos, URLs, headers ou tokens.
O coletor continua privilegiado: seu código e diretório precisam permanecer root-owned, não graváveis
por emissor/monitor/receptor/agente. Um host Duora comprometido pode mentir sobre saúde; mTLS autentica
a origem, não prova que o sistema de origem está íntegro.

## Protocolo e falhas

O Duora inicia a conexão para a VPS de operações: não há listener novo no host do produto.
O receptor escuta um IPv4 explícito em porta não privilegiada (exemplo 8443). Firewall do host e do
provedor devem permitir essa porta **somente do IPv4 de saída do Duora**. O receptor também rejeita
outro IP antes do handshake. Não colocar reverse proxy/CDN/NAT que altere esse IP sem revisão própria;
não confiar em X-Forwarded-For. Administração usa canal separado, fora do OpenClaw.

TLS >= 1.2, CA privada dedicada, certificado servidor com SAN/hostname verificado pelo emissor,
certificado cliente obrigatório e fingerprint SHA-256 DER explicitamente permitido. Até dois
fingerprints durante rotação; certificados de mesma CA sem pin não publicam. Expiração é verificada
pelo TLS. Sem fallback HTTP, proxy de ambiente, redirect ou desativação de verificação TLS.
A CA e sua chave privada ficam fora dos dois servidores; apenas CA pública e chaves leaf de cada papel
chegam aos respectivos hosts. Proteger também backups desses arquivos; não os enviar ao chat/Git.

Documento <= 16 KiB: somente `collected_at` numérico finito e `checks` com conjunto exato de chaves
fixas de `telemetry.CHECKS` e valores booleanos. São estados de postgres/web/worker/edge/backup,
readiness, units/timers de backup e validade dos dois marcadores. Não há nomes livres, logs,
participantes, fotos, URLs, comandos ou caminhos recebidos. Campos ausentes/adicionais, JSON com
chaves duplicadas, timestamps booleanos/NaN/futuros/vencidos, chunked, framing ambíguo ou rota
inesperada falham. Adicionar um probe exige revisar e versionar os dois lados juntos.

Validade máxima de 120 s desde a **coleta**, sem tolerar relógio futuro: NTP precisa estar sincronizado.
O receptor persiste atomicamente snapshot + watermark e sincroniza arquivo/diretório antes de ACK 204.
Timestamp inferior é replay; timestamp igual só recebe ACK se conteúdo idêntico, sem regravar nem
estender validade (retry de ACK perdido). Watermark sobrevive a restart e rotação; nunca apagá-lo
para contornar erro. Arquivo corrompido impede publicação até recuperação pelo operador.
Depois de uma interrupção longa, uma coleta nova pode avançar o watermark; a antiga não se torna nova.

O receptor é serial, com fila limitada a quatro conexões, timeout por socket e prazo absoluto de headers/corpo de 5 s e nenhuma execução
a partir da requisição. O emissor tem timeout de 8 s; cada ciclo obtém nova coleta e não armazena fila
ilimitada. Timer tenta novamente 15 s após conclusão. Os probes existentes podem demorar até 90 s;
isso é orçamento de timeout, não garantia de entrega em 15 s. Falhas de coleta/envio/certificado
produzem snapshot ausente/vencido e alerta no monitor remoto, mantendo o gate saudável fechado.
Uma origem permitida ainda pode causar indisponibilidade de telemetria; firewall e limites reduzem
o impacto, sem promessa de proteção completa contra DoS.

O monitor conserva `collected_at` do produto na validação, combina health local do gateway e publica
booleans de falha ao MCP. Publicação recente do monitor não torna saudável produto vencido.
O heartbeat externo continua obrigatório para queda de toda a VPS de operações. Configurar também
verificação externa do Duora, independente da IA/Slack. Não depender de o OpenClaw interpretar alertas.

## Capacidade e pré-requisitos

[capacity.md](capacity.md): `source` exige **8.512 MiB MemTotal**, incluindo produto, migrador,
restic 1 GiB, coletor 128 MiB, emissor 64 MiB e margem 1.536 MiB. KVM 2 de 8 GB decimais continua
reprovada. No emissor, `--runtime` exige também slice de mídia carregada com MemoryMax=1 GiB.
`remote-gateway` exige **3.456 MiB**, incluindo gateway + receptor/monitor/coletor local e margem.
Ambos exigem MemAvailable >= 1.536 MiB. KVM 4 / KVM 1 são candidatos, não homologação de carga.
No host de operações, preflight/ativação/partida recusam containers existentes do projeto `duora`
(inclusive parados) e exigem os três tetos auxiliares de 128 MiB. Não usar outro nome de projeto
como forma de coinstalar o produto. O orçamento remoto não permite execução de LLM, browser ou builds.

A seleção remota é o arquivo root 0644 `/etc/duora-agent/profile.json` com exatamente
`{"profile":"remote-gateway"}`. Ausência preserva orçamento/perfil conjunto; outro conteúdo/dono/tipo/modo
é erro. Esse seletor não desativa política Slack/MCP, firewall ou gate de imagem/credenciais.
Memória nominal não prova enforcement ou desempenho; medir CPU, PSI, I/O, WAL, backups, migração,
latência e OOM em host equivalente, além de espaço para estado/imagens/snapshots. Não construir imagem
na VPS. Restaurar produto/mídia segue os runbooks existentes, sem gateway no caminho crítico.

## Preparação e instalação pelo operador

Só executar em ambiente de homologação isolado autorizado, após revisão humana e gates do PR.
Ubuntu 24.04, Python 3.12/OpenSSL, systemd/cgroup v2, Docker/Compose onde necessário, horários
sincronizados, IPv4s/rota de saída estáveis, subnet do agente sem colisão e regras de ingresso
revisadas. Nenhum comando desta seção foi executado em produção por esta alteração.

1. Rever orçamento dos dois hosts e provar baseline/backup/restore antes da mudança. Registrar
   release e digests. Criar a slice de mídia com a role backup revisada no host Duora; verificar
   limites efetivos e capacidade do dataset do restic. Não desativar WAL, timers ou retenção.
2. Checkout da release revisada nos dois hosts pelo canal administrativo. Instalação é **fresh-only**:
   recusa diretórios da integração e contas reservadas remotas preexistentes; não converte instalação
   conjunta automaticamente. Instalar arquivos sem ligar serviços:

   ```sh
   # Somente no host Duora:
   sudo sh scripts/openclaw/install-remote.sh source --approve-install
   # Somente no host de operações:
   sudo sh scripts/openclaw/install-remote.sh gateway --approve-install
   ```

   O segundo comando reutiliza o instalador existente com perfil remoto, prepara filesystem de
   estado de 2 GiB, mas **não monta, habilita ou inicia serviço**. `daemon-reload` só recarrega units.
   Falha parcial exige inspeção; não remover arquivos/contas desconhecidos para repetir às cegas.
3. Emitir CA privada dedicada **offline** e certificados leaf distintos: servidor com SAN do nome
   DNS usado no endpoint e EKU `serverAuth`; emissor com EKU `clientAuth`. Recomenda-se validade
   curta (exemplo 30 dias) e inventário de expiração/rotação. Cada chave leaf é gerada no host
   administrativo correspondente e nunca reutilizada no gateway. Usar OpenSSL/PKI institucional;
   não reutilizar a CA ou chaves do backup PostgreSQL. Não copiar a chave da CA para nenhuma VPS.
   Para obter o pin público de um certificado cliente já aprovado:

   ```sh
   openssl x509 -in client-cert.pem -outform DER | openssl dgst -sha256
   ```

   A saída contém apenas o fingerprint público; configurar os 64 caracteres hexadecimais minúsculos,
   sem prefixo/espaços. Validar cadeia, SAN, uso e datas antes de distribuir certificados.
4. Provisionar como root, diretório `/etc/duora-telemetry/secrets` 0700, arquivos root 0400:
   **no Duora**, `source-ca.pem` (CA do servidor), `source-cert.pem`, `source-key.pem`;
   **em operações**, `receiver-ca.pem` (CA do cliente), `receiver-cert.pem`, `receiver-key.pem`.
   As units expõem só esses arquivos ao seu processo via `LoadCredential`. Não usar variáveis de
   ambiente, argumentos CLI ou repositório para transportar chaves.
5. No Duora editar `/etc/duora-telemetry/sender.json` root 0644 com apenas `endpoint`, por exemplo
   `https://ops.example.net:8443/v1/telemetry`. Em operações editar `receiver.json` root 0644 com
   `bind`, `port`, `source_ipv4` e `client_fingerprints`. Não bindar wildcard ou interface do agente.
   Aplicar allowlist de rede acima antes de iniciar receptor. Confirmar que o container do gateway
   continua bloqueado para host e redes privadas por suas regras existentes; não abrir exceção.
6. Preparar Slack, credenciais do modelo, GitHub read-only, webhook e heartbeat de
   [openclaw.md](openclaw.md) **somente na VPS de operações**. O monitor lê snapshot remoto e
   gateway local devido aos drop-ins instalados. Não instalar monitor/Slack no host Duora.

## Partida autorizada e ensaio

Instalação não equivale a aprovação de partida. Depois de capacidade/isolamento/regras/credenciais
conferidos e autorização registrada para a homologação:

```sh
# Operações: montagem privada, primeiro uso do filesystem preparado, antes de preflight/ativação.
sudo systemctl start var-lib-duoraagent.mount
sudo chown 22001:22001 /var/lib/duoraagent
sudo chmod 0700 /var/lib/duoraagent
sudo systemctl enable --now duora-telemetry-receiver.service
sudo systemctl enable --now duora-observer.timer duora-monitor.service
# Duora: somente a coleta fixa e o envio de saída; o timer chama o coletor como dependência.
sudo systemctl enable --now duora-telemetry-send.timer
# Operações: segue ativação existente, digest revisado, pull prévio explícito e rollback de estado.
sudo python3 /opt/duora-agent/preflight.py
sudo python3 /opt/duora-agent/activate.py --approve-image registry/imagem@sha256:DIGEST_APROVADO
```

Habilitar `duora-agent.service` no boot só após aceite completo. O gateway inicialmente parado gera
alerta esperado; não silenciar falhas do produto/telemetria para esconder essa fase de preparação.
Os comandos permanecem exemplos operacionais; esta alteração só executou testes locais sintéticos.

Registrar os ensaios abaixo com host/commit/digest, comandos, resultados, timestamps e latência no
smartphone. Dados/credenciais reais não entram no relatório.

| Ensaio | Aceite |
|---|---|
| Dois hosts e runtime | Units/drop-ins, MemTotal/MemAvailable, cgroups, pesos/quotas e firewall conferidos; sem rota administrativa ao agente |
| mTLS | Emissor autorizado publica; sem certificado, CA errada, pin errado, expirado ou hostname incorreto não publicam |
| Sanitização | Logs/nomes/dados livres, campos extras/ausentes, JSON duplicado, corpo >16 KiB e chunked não alteram snapshot |
| Replay | Pacote antigo e timestamp igual com conteúdo diferente rejeitados, inclusive após restart; ACK perdido repete sem renovar validade |
| Rede/coleta indisponível | Bloquear envio/parar coletor: freshness falha em até TTL após última coleta; Slack no telefone recebe falha/recuperação |
| Snapshot corrompido | Não é resetado nem declarado saudável; recuperar pelo procedimento abaixo |
| Independência | Parar gateway: alertas do monitor continuam. Queda Duora: monitor remoto alerta. Queda operações: dead-man externo chega no telefone |
| Certificados | Rotação com dois pins/CA de transição ensaiada; retirar pin antigo impede publicação; expiração detectada |
| Produto | Cenários de capacity.md, WAL/full/diff/restic, migração e restore/reconciliação aprovados sem reduzir garantias |
| Reboot/update | Units e montagem recuperam, firewall reaplicado, watermark/dedup persistem e rollback do gateway funciona |

## Rotação, recuperação e rollback

Rotação leaf cliente: emitir novo certificado offline; adicionar seu fingerprint como segundo pin;
reiniciar receptor pelo operador; substituir os arquivos `source-cert.pem`/`source-key.pem` por canal
privado e aguardar próxima execução (LoadCredential é recarregado em cada oneshot). Confirmar nova
entrega e retirar pin antigo; reiniciar receptor. Para CA/servidor, distribuir bundle de transição ao
emissor antes de trocar servidor e reiniciar receptor. Remover CA antiga após confirmação. Não aceitar
CA pública genérica nem desativar verificação para resolver erro. Revogação imediata usa remoção do pin;
não há distribuição automática de CRL/renovação neste perfil. Monitorar expiração fora do agente.

Preservar `/var/lib/duora-remote/host.json` (somente estados públicos), `/var/lib/duora-monitor/state.json`
(dedup de alertas) e arquivos de configuração/PKI com permissões privadas adequadas. Watermark ausente
aceita o primeiro pacote recente: perda/restore antigo desse arquivo abre janela de replay <=120 s.
Restaurar watermark mais recente confiável com emissor/receptor parados; se indisponível/corrompido,
revogar o certificado cliente, provisionar certificado/pin novos e iniciar com estado vazio em janela
auditada, após provar que o emissor envia coleta nova. Não editar timestamp para fazê-lo parecer recente.
Gateway/state rollback usa activate.py existente; nunca restaurar banco ou mídia para reverter o agente.

Rollback do perfil remoto: parar/desabilitar somente `duora-telemetry-send.timer` e seu serviço no Duora;
parar/desabilitar receptor/monitor/gateway/timer local em operações, revogar certificado cliente/tokens,
fechar apenas ingresso 8443 dedicado e preservar evidências. Não remover units de backup, volumes,
chaves de backup, regras DOCKER-USER globais ou dados do produto. Migração de instalação conjunta
preexistente requer janela própria, backup de configurações/estado e desativação dos antigos
observer/monitor/gateway antes de provisionar o novo perfil; não sobrepor instaladores fresh-only.

## Validação local e pendências

```sh
python3 -m unittest discover -s scripts/openclaw -v
python3 -m unittest discover -s scripts/deploy -p test_openclaw_capacity.py -v
node --test scripts/openclaw/test-mcp.mjs
pyright --pythonpath CAMINHO_PYTHON scripts/openclaw
```

A entrada existente no job ops descobre os testes novos sem alterar workflows. Os testes mTLS geram
PKI descartável em diretório temporário e fazem conexão loopback real, incluindo certificado expirado.
Isso não comprova instalação systemd, firewall, limites Linux, conectividade entre VPSs, capacidade
real, imagem/config OpenClaw ou recebimento no telefone. Esses ensaios e autorização de produção
continuam pendentes. Fontes de semântica: [Python ssl](https://docs.python.org/3.12/library/ssl.html)
e [systemd credentials](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html).

## Plano e evidências de homologação

O [plano de homologação](openclaw-homologation.md) organiza pré-requisitos e aceite das duas VPSs.
A [validação local de 21/10/2026](openclaw-local-validation.md) registra testes e pendências;
ela não comprova instalação, capacidade sustentada ou monitoramento operacional.
