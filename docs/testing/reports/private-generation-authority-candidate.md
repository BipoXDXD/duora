# #311 — checkpoint integrado da autoridade exclusiva

Base: 6c397a34a19969e93148b7e5cdb760c508bbdec2, 01/11/2026. ADR-0047.
Estado: candidato técnico integrado, não conclusão da issue ou habilitação operacional.

## Evidência obtida antes do checkpoint

- Restore locked serial da base e build serial: PASS, zero erros, três avisos ImageSharp Debug preexistentes.
- RED inicial: sete asserções falharam no runtime original; PUT privado ainda aceito, configuração não recusada especificamente e provas inexistentes.
- Testes SQL/routing ampliados: 15/15 PASS, zero ignorados. Incluem prova sem DML/EXECUTE do app, binding retrospectivo negado, ramo writer ACK bloqueado em binding novo, token antigo/prova alheia negados e conclusão sem ACK fabricado.
- Linux aarch64 nativo, runtime2: exit0 e preservação dos16 volumes anteriores. Fonte sintética SHA-256 77201441e8344399b606280d5022fbdb8c62f6847f132716a5ba6de2600ac799; imagem sha256:517b0e73084aa22b232777adb4b58649f112bf4b0d0cd6df7a34344fc81716c3.
- Formatação backend executada após a rodada. O hash de fontes da imagem identifica a rodada anterior ao reflow; validação do SHA final será registrada na entrega posterior.

## Casos Linux realmente exercitados no código novo

Host17010 e cliente17020 são processos distintos; o cliente só tem conexão app/socket/domain e não lê os arquivos de referência/verificador. DI e preparadores reais StageAsync/PrepareAsync e coletor real são chamados pelo cliente.

PASS: preparação de candidato, promoção e coleta com SQL real; EOF pausado após admissão mantém Close Busy e prova/deleted_at ausentes; liberação conclui I/O e nova varredura registra prova sem writer ACK; replay/Register/PUT tardio negados; variante16MiB/conjunto24MiB; corpo excedente/truncado recusado; isolamento da candidata vizinha; syscalls1654/17020/autoridade não substituem controles; rollback local Open/SQL não apaga revogação externa; restart na mesma época recusado; nova época após saída real recupera Close antigo; privateprefix legado sem binding fica indisponível, com arquivos intactos.

Runtime1 não é PASS global: detectou InvalidDataException não capturada no Program durante restart recusado. O catch foi corrigido explicitamente, sem afrouxar teste. A rodada foi encerrada com remoção apenas do CID próprio, exit137 e volumes preservados. Runtime2 repetiu integralmente e passou. Falha Npgsql multicomando da primeira fixture SQL também foi corrigida; não foi contada como RED de segurança.

## Comandos

```sh
dotnet restore backend/Duora.slnx --locked-mode --disable-parallel
dotnet build backend/Duora.slnx --no-restore -m:1 /p:UseSharedCompilation=false
dotnet restore scripts/testing/storage-authority/RuntimeChecks.csproj --disable-parallel
dotnet build scripts/testing/storage-authority/RuntimeChecks.csproj --no-restore -m:1 /p:UseSharedCompilation=false
dotnet test --project backend/tests/Duora.IntegrationTests --no-build --no-restore -- --filter 'FullyQualifiedName~PrivateGenerationRoutingTests|FullyQualifiedName~PrivateGenerationProofPrivilegeTests'
python3 scripts/testing/storage-authority/run_linux.py --report-dir <diretorio-novo-de-evidencia>
dotnet format backend/Duora.slnx --no-restore
```

## Pendências deste checkpoint

Revisão independente, testes de falha restantes do host novo, unidade/arquitetura/regressões amplas, verify-format/locked no SHA final e artefatos finais. Evidência de energia/host-alvo, custódia contra restore físico, launcher operacional, amd64 e S3/B2 não foi executada e não será inferida dos testes locais. Os46 testes antigos não são atribuídos ao host novo.

Só Testing admite a composição; Production e outros ambientes recusam. Sem instalação/ativação, PR/issue/push/merge, dados/segredos reais ou alteração da raiz/worktrees alheias. Política de legado: sem fallback privado, leituras privadas sem binding indisponíveis no modo gerenciado; default off mantém o comportamento existente. Retenção um ano/65 dias e isolamento preservados. Não foram detectadas instruções não confiáveis tentando ampliar o escopo.

