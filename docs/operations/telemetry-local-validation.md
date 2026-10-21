# gateway remoto: evidências da validação local

Data: 21/10/2026. Relacionado a INV-12/T-12 e ADR-0031; nenhuma API do produto foi alterada.
O [plano de homologação](telemetry-homologation.md) e o [runbook remoto](telemetry-remote.md)
continuam exigindo aceite das duas VPSs. **Não instalado, não ativado, sem recursos contratados.**

## Base e rastreabilidade

Os testes funcionais abaixo foram executados no snapshot
`d79321d477362e3d207680bb68b378278febc21b`, cuja integração é idêntica ao merge do PR #113
`60142612d772ba0bccbb7d488d1e8337b8c2f745`. O PR parte da main
`6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f`. O delta posterior não altera `scripts/telemetry`,
`infra/telemetry`, os runbooks da integração, os testes de orçamento ou a role backup.
O scanner foi repetido no snapshot inicial e na árvore documental atual; os resultados não são
homologação do host nem uma nova execução de todos os jobs do CI.

Ambiente: macOS arm64, Python 3.12.14, Node 24.21.0, Docker Desktop 29.8.2 (Linux/arm64),
Compose 5.6.0. Somente fixtures, PKI temporária, loopback TLS e containers locais descartáveis.
Nenhum SSH, credencial de produção, instalação systemd, aplicação de Ansible ou ativação remota.

## Resultados

| Validação | Resultado e limite |
|---|---|
| Suíte `scripts/deploy` | 365 testes descobertos: 364 passaram, 1 opt-in de navegador não executado |
| gateway Python, executado pela suíte ops | 31/31 passaram; inclui oito testes com mTLS loopback real |
| MCP Node, executado pela suíte ops | 7/7 passaram |
| Validador da chave APT | 6/6 passaram |
| Pyright gateway e ferramentas ops/backup | Zero erros/avisos |
| Ansible lint e syntax-check | Perfil production passou; nenhum playbook aplicado |
| Compose e shell | Normalização e sintaxe passaram; digest fictício somente no processo de validação |
| Gitleaks | Nenhuma ocorrência no snapshot e nos pacotes; não cobre histórico Git inteiro |
| Fronteiras do orçamento | Sete decisões esperadas confirmadas com memória sintética |
| Backup/restore PITR local | Passou; RPO 55,6 s, RTO 16,0 s, timeline 2; dataset sintético |
| Trivy fs | Reexecução passou; ver escopo, comando e log abaixo |

Os testes filhos gateway/MCP são registrados separadamente; não somar as contagens como uma
única suíte independente. O opt-in `test_browser_enforces_the_policies` exige
`DUORA_TEST_BROWSER=1` e dependências Playwright do frontend, ausentes na cópia usada.
Backend/frontend/e2e completos e auditoria da imagem gateway não foram executados nesta etapa.

O drill usa PostgreSQL/pgBackRest e MinIO: backup full, WAL natural, indisponibilidade S3,
versionamento/Object Lock, exclusão induzida do repositório, PITR de versões anteriores, promoção,
verificação pós-restore e detecção de lacuna de WAL. O PostgreSQL comprometido ainda consegue
apagar o repositório via TLS; a recuperação depende de versões protegidas, como no ADR-0023.
O filtro DOCKER-USER do host é explicitamente omitido pelo drill local e continua pendente na VPS.
Containers e volumes do drill foram removidos. Os tempos não estimam RPO/RTO do dataset real,
restauração de mídia ou reconciliação de negócio.

## Trivy e resolução de nomes

A tentativa anterior e uma nova tentativa com DNS padrão falharam **antes da análise**, ao baixar
a base de vulnerabilidades. DNS direto em resolvedor público respondeu; a reexecução usou
`--dns 1.1.1.1` somente no container descartável, sem alterar DNS/VPN do host, certificados TLS,
scanners, severidades ou exceções. Nenhum ignore, gate ou workflow foi modificado.

```sh
docker run --rm --dns 1.1.1.1 -v "$PWD:/repo:ro" -w /repo \
  ghcr.io/aquasecurity/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa \
  fs . --scanners vuln,misconfig --severity HIGH,CRITICAL --include-dev-deps \
  --ignorefile .trivyignore --exit-code 1
```

Saída da árvore documental antes da atualização do Go: [trivy-current.log](evidence/telemetry-2026-10-21/trivy-current.log).
O scan usa a base baixada nessa execução. Zero achados HIGH/CRITICAL reportados dentro do escopo
configurado e das exceções existentes. Não é scan da imagem gateway ou garantia sobre
vulnerabilidades futuras. O aviso de ausência de Python site-packages significa que a detecção
de licenças pip foi omitida; este comando não substitui a auditoria de licenças/SBOM do CI.

## Correção posterior no build da borda

O CI do PR #121 detectou CVE-2026-111943 no Go 1.26.9 embutido no Caddy,
na análise das imagens amd64/arm64. O scan de filesystem acima não cobre esse binário.
O build passou a usar Go 1.26.10, fixado pelo digest do índice multiarch oficial,
que inclui a [correção de crypto/x509](https://go.dev/doc/devel/release#go1.26.10).
Caddy e x/net mantêm suas versões; nenhum gate, scanner ou ignore foi alterado.
A aceitação desta correção depende dos builds e scans das duas arquiteturas no CI
do novo commit. As evidências locais acima continuam vinculadas aos snapshots informados.

## Pacotes e evidências

Foram preparados dois pacotes locais: `vps-duora-source.tar.gz` (59 arquivos) e
`vps-operacoes-gateway.tar.gz` (58 arquivos). Hashes e tamanho em
[packages.json](evidence/telemetry-2026-10-21/packages.json). Os binários não são duplicados no Git.
Ambos preservam o layout e os scripts originais; incluem runbooks, templates, inventário pendente,
mapa de credenciais e manifests. Extração segura, integridade e entradas dos instaladores foram
conferidas; 47 arquivos originais por pacote comparados byte a byte com a fonte fixada.
Nenhuma chave/certificado/token real, imagem aprovada ou `image.env` utilizável acompanha os pacotes.
Templates com `REPLACE_*` e heartbeat nulo não constituem configuração instalável.

- [Comandos, datas UTC e códigos de saída](evidence/telemetry-2026-10-21/checks.json).
- [RPO/RTO e fases do drill](evidence/telemetry-2026-10-21/restore-drill.json).
- [Integridade dos pacotes](evidence/telemetry-2026-10-21/package-verification.json).
- [Casos sintéticos de capacidade](evidence/telemetry-2026-10-21/capacidade-sintetica.csv).
- [Matriz T01–T11 com pendências reais](evidence/telemetry-2026-10-21/matriz-homologacao.csv).

Os registros foram sanitizados: caminhos locais substituídos por `${REPO}`, `${PYTHON312}` e
`${EVIDENCE}`; sem tokens, dados de participantes ou chaves de teste. Logs brutos de testes
permanecem na entrega local; o ledger preserva comandos, datas e resultados. As matrizes não
marcam testes remotos como aprovados.

## Pendências de acesso, credenciais e aceite

- **Capacidade:** source exige MemTotal 8.512 MiB, operações 3.456 MiB e ambos MemAvailable
  >=1.536 MiB, sem contar swap. A hipótese KVM2 de 8 GB decimais (7.629,395 MiB) foi rejeitada;
  separar o gateway não elimina esse déficit. Os tetos não são reservas. Medir migração,
  WAL/full/diff/restic/imagens concorrentes, CPU/PSI/I/O, disco, latência, OOM e deadlines.
- **Duas VPSs autorizadas:** inventário, Ubuntu/cgroup v2, NTP/DNS/IPv4, slice de mídia agregada
  1 GiB, limites/ownership/montagem/firewalls efetivos, ausência de produto em operações,
  baseline e dataset sintético. Instaladores fresh-only não convertem uma instalação existente.
- **PKI e imagem:** CA privada offline distinta do backup, leafs/SAN/EKUs/pins/expiração/rotação,
  LoadCredential; imagem gateway por SHA/digest, arquitetura, Slack plugin, provenance,
  SBOM/licenças e scanner. Nada foi publicado pelo script de build.
- **Integrações:** Slack app/bot/canal/usuários explícitos, API do modelo, gateway token e webhook
  independente; token GitHub Actions+Metadata somente leitura em repositórios de teste.
  Ensaiar 403/429/expiração/rerun/paginação; polling síncrono pode atrasar alertas.
- **Alertas no aparelho:** heartbeat fora das duas VPSs, verificação externa do Duora, rota
  independente do Slack, notificações/DND/bateria e falha/recuperação no smartphone.
  TTL de 120 s não é SLA de entrega. As metas propostas precisam de aceite e medição.
- **Recuperação:** carga, restore representativo de banco e mídia, reconciliação, rotação,
  reboot/update/rollback e persistência de watermark/dedup. Instalação inativa e partida exigem
  autorizações próprias; este PR não autoriza produção.

Nenhuma garantia de WAL, retenção, Object Lock, backup ou margem foi reduzida. Nenhuma tentativa
de instrução não confiável foi detectada. Rollback deste PR é documental; rollback operacional
continua exclusivamente no runbook existente, sem restaurar produto para reverter o gateway.
