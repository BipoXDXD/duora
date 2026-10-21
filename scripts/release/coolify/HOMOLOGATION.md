# Homologação descartável — Duora #168 / GoblinShift #124

Data: 21/10/2026. Resultado: os cenários abaixo passaram em Docker, Coolify e PostgreSQL reais, com workloads e dados sintéticos. Isto homologa o ingresso compartilhado nas condições descritas; não certifica toda a aplicação nem autoriza instalação/ativação em produção.

## Artefato e ambiente

Receptor **2.0.2**, protocolo 2, SHA-256:
`1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb`.
Os mesmos bytes foram executados no laboratório e estão nos dois repositórios. A troca de metadados 2.0.1 → 2.0.2 não alterou os bytes executados; evita reutilizar a versão publicada por trabalho paralelo.

VM Lima 2.2.1 dedicada: Ubuntu 24.04, kernel 6.8, ARM64, 2 CPUs, 5 GiB RAM, disco 72 GiB. Imagens linux/amd64 executadas por Rosetta. Nenhum diretório do usuário montado; encaminhamento TCP de aplicações desabilitado. Docker Engine **29.9.2**, API 1.56 (receptor usa 1.52), containerd **2.4.2**, store containerd; Coolify **4.4.7**, fonte fixada em `86c21c0d5babfa8c7d65a9c27e5863c629a89c7c`; PostgreSQL **18-alpine** nos dois bancos sintéticos. API Coolify acessível somente dentro da VM. Credenciais, contas e chaves descartáveis foram geradas dentro dela.

Dois recursos Coolify reais, cada um com serviço HTTP mínimo, volume próprio e PostgreSQL separado. Todos os roles permitidos de cada projeto tiveram grafos OCI importados e referências imutáveis resolvidas; os roles usam a mesma imagem sintética mínima. Somente um serviço HTTP por projeto foi publicado. O parser de manifestos Duora é real; o executor completo de negócio, migrator, pgBackRest/WAL e topologia completa não foram exercitados. Pedidos administrativos sintéticos não representam CI/main/SBOM reais; os gates de GitHub permanecem obrigatórios e inalterados.

## Resultados observados

| Cenário real | Resultado e evidência |
| --- | --- |
| Importação OCI dos dois projetos | Receptor inicial 2.0.0 recusou lookup por config digest no containerd; candidato importa, confere manifesto/config/runtime ID e resolve `repository:release-commit@manifest` localmente sem pull |
| Concorrência entre projetos | Duora v2 recebe start real; receptor suspenso com SIGSTOP mantendo flock. Goblin concorrente recusa antes de entrada/mutação, sem alterar current. Após SIGCONT e saúde confirmada, retry Goblin termina saudável |
| Interrupção durante publicação | SIGKILL no receptor Goblin após UUID real aceito. Flock é liberado, mas intents comum/projeto impedem Duora, mesmo após a operação externa prosseguir |
| Recuperação de pendência | Administrador adquire host.lock, confirma fila terminal, imagem/proprietário e saúde reais, guarda recibo e arquiva somente os intents reconciliados. Duora volta a publicar; nenhum apagamento cego de estado |
| Workload defeituoso | Imagem cujo processo termina imediatamente é recusada. Recibo saudável anterior fica idêntico; pendência fica preservada até reconciliação |
| Rollback de imagem | Após reconciliação, imagem anterior v1 volta a responder HTTP v1. Duora também retorna de v2 para v1. Sem downgrade automático de schema, restore sobre banco original ou remoção de volume |
| Bancos e volumes | IDs dos dois containers PostgreSQL mantidos; linhas `preserve-duora` / `preserve-goblinshift`, coluna compatível adicionada e sentinelas dos volumes mantidas após concorrência, falha e rollback |
| Backup/restore | `pg_dump -Fc` e `pg_restore` reais em bancos separados `lab_restore`; linhas e coluna compatível idênticas aos originais |
| SSH restrito | Duas contas/chaves com forced command por projeto, host key conferida e sudo exato; sem grupo Docker. Comando arbitrário e pacote Goblin pela chave Duora recusados; publicação válida por ambas as chaves termina saudável |
| Pouco espaço | Filesystem tmpfs de 16 MiB acrescentado ao inventário administrativo do laboratório; recusa por espaço antes de consumir pacote/criar intent. Inventário restaurado e mount removido |

Registros principais no pacote de evidências entregue: `scenarios.json`, `extra-checks.json`, `postgres-baseline.json`, `baseline-import-probe.json`, `corrected-import-probe.json`, `environment-final.json`, logs individuais e intents arquivados. Nenhum token, arquivo .env ou chave privada integra o pacote.

## Correções e regressões

1. Identidade varia pelo backend: inspecionar config validado primeiro; fallback para manifesto validado **somente em HTTP 404 tipado**. Erros de rede/servidor não acionam fallback. Runtime ID precisa ser config ou manifesto do grafo OCI e Descriptor.digest, se presente, precisa coincidir. Preservadas referência imutável e checagens de runtime.
2. Coolify pode marcar fila `finished` enquanto Docker informa health `starting`. Aguardar até 120 segundos dentro do deadline global, conferindo identidade a cada leitura; imagem/proprietário errado, unhealthy e deadline expirado continuam recusados, com intent e recibo anterior preservados.

Regressões observadas falhando antes das correções. Suítes locais: **47 testes Duora / 46 GoblinShift**, todos aprovados; Ruff check/format de GoblinShift (588 arquivos), mypy (529 arquivos), comparação de artefatos, `git diff --check` e guardas dos 94 assets originais aprovados. Revisão independente final sem defeitos relevantes de alta confiança. A CI completa dos heads finais deve ser conferida nos PRs; não confundir rodadas anteriores com a revisão atual.

## Repetição do ensaio

O pacote de reprodução entregue contém `lab.yaml`, `labctl.py`, scripts de provisionamento/fixtures/cenários e README com ordem e pré-condições. Criar VM nova sem mounts de host, instalar a fonte Coolify fixada e copiar apenas artefatos revisados. Gerar novas credenciais dentro da VM. Executar import probe, releases baseline v1, `scenarios.py`, `extra_checks.py` e coletor nessa ordem. Os subprocessos de receiver e APIs Docker/Coolify/PostgreSQL são reais; não trocar por mocks para alegar homologação real. O harness é administrativo e somente de laboratório; não instalar os scripts em produção. O estado da VM descartada não deve ser reaproveitado como ambiente confiável.

Testes determinísticos integrados à CI existente:

```sh
python3 -m unittest discover -s scripts/release/coolify -v  # Duora
python3 -m unittest discover -s deploy/coolify/release -v  # GoblinShift
```

## Limites e ensaios invalidados

- ARM64 com emulação não prova paridade com VPS AMD64 nativa, versões instaladas, carga real ou isolamento de toda a topologia. Nenhum inventário/credencial/serviço de produção foi consultado ou alterado nesta homologação.
- Não foram simulados reboot/perda completa da VPS, corrupção do daemon/disco, falta de inodes, disco cheio durante import, restauração WAL/pgBackRest, migração destrutiva ou rollback de schema incompatível.
- Um daemon clássico VFS separado recusou o tar OCI bruto por ausência de manifest.json. **Compatibilidade de transporte OCI no store clássico não foi aprovada**; a cobertura de identidade clássica é unitária. A recusa ocorre antes de configurar Coolify. Usar backend homologado ou realizar ensaio próprio antes da instalação.
- O segundo daemon interferiu no bridge docker0 exclusivamente da VM. Essa rodada foi invalidada e arquivada em `confounded-extra-daemon-scenarios.json`; daemon extra parado, bridge restaurado, conectividade Coolify→VM reconferida e cenários executados novamente. Nenhum resultado dessa rodada foi contado como aprovação.
- O harness administrativo inicialmente tentou reconciliar ainda em health starting e recusou corretamente. Registro em `early-admin-health-reconciliation.json`; espera limitada foi adicionada ao harness, a pendência real foi preservada e a recuperação retomada. Não houve limpeza para fabricar sucesso.
- Fixture passou por ajustes de Git HTTP, loader Busybox, labels e nomes de containers antes da rodada válida; falhas anteriores permanecem em logs separados. Não se enfraqueceram gates de produto.
- Medição final, sem certificação de capacidade: cerca de 67,7 GB livres, 9,17 milhões de inodes livres; nenhum evento OOM no kernel. `docker stats`/I/O é snapshot, não pico nem teste de carga. Orçamento de admissão continua sem quota física ou proteção contra escrita externa.
- Locks só coordenam receptores participantes; operações manuais, webhook, daemon e outros hosts continuam fora da garantia. [ISOLATION.md](ISOLATION.md) mantém o runbook conservador de instalação/recuperação/rollback.

## Revisão e instalação

Alterações limitadas ao receptor compartilhado, metadados, testes e documentação. Workflows, permissões, recursos existentes, schema de produto e políticas de rollback preservados. Nenhuma tentativa de instrução não confiável detectada. Não houve merge nem instalação em produção por esta homologação.

Há atualização paralela do receptor em StillOn #112 usando hash 2.0.1 diferente. **Não ativar parcialmente**: comparar todos os senders e o executável da VPS, atualizar coordenadamente todos os consumidores para os mesmos bytes aprovados e seguir o runbook sob as travas. Este trabalho não altera StillOn nem afirma que essa coordenação foi concluída. Merge de fonte não atualiza o executável root da VPS.
