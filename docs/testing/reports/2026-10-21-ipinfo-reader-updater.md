# Issue 108: leitor e atualização MMDB inativos

Escopo autorizado: etapas 7 e 8 do plano; MaxMind.Db 5.2.0/Apache-2.0 aprovado explicitamente pelo responsável.
[PR #167](https://github.com/BipoXDXD/duora/pull/167). Issue #108 permanece aberta. Não aplicar migração em produção,
ativar serviços ou habilitar EUA. O responsável designou a si como titular, Gmail privado para alertas e nenhum
substituto por enquanto ([decisão](https://github.com/BipoXDXD/duora/issues/108#issuecomment-6327178280)).
Cobertura regional, contingência, transporte/entrega de alertas, armazenamento e G3/G5 continuam pendentes.

## Alterações

Core: políticas de idade e agenda. Infrastructure: leitor, manifesto, validação, download e store de atualização.
Worker: executor não registrado. SQL aditivo: kind exclusivo e tabelas Operations com manifestos append-only.
Dependência/lockfiles e licença do leitor; testes sintéticos; ADR/plano/modelo/runbook/matriz de testes.
Web/WorkerHost, frontend, catálogo executável e configuração operacional não foram alterados.

## Evidência e limite dos resultados

Antes da interrupção local: unidade baseline 1494/1494; políticas de geolocalização14/14; integração geolocalização32/32.
Esses resultados antecedem os testes adicionais e não comprovam o head final.

Comandos usados nessa etapa inicial:

```sh
dotnet test --project backend/tests/Duora.UnitTests -- --filter-class '*Geolocation.*'
dotnet test --project backend/tests/Duora.IntegrationTests --no-build -- --filter-class '*Geolocation.*'
```

Os componentes tiveram ciclos RED/GREEN locais: políticas, store, leitor, transporte e executor. Ocorreram falhas
iniciais de ambiente/build paralelo, assets desatualizados e diferenças Npgsql (comandos posicionais múltiplos e
timestamptz escalar). Foram corrigidas sem reduzir testes, políticas ou gates.

Após travamento do Mac de16GB, o responsável determinou evitar sobrecarga. Não reiniciar Docker nem executar builds/suítes
locais. Formatação inicial sem workspace deixou114 diagnósticos de whitespace no CI; aplicadas as correções exatas do
formatter remoto. O primeiro ops falhou por IncompleteRead do Ansible Galaxy; o gate permanece inalterado.

A validação final usa os nove checks existentes do CI remoto, incluindo restore locked, build, dotnet format,
`dotnet test --solution backend/Duora.slnx --no-build`, frontend, E2E, contratos, ops, segredos, dependências e imagens.
Saídas e estados do commit final são registrados no PR; este documento não presume checks verdes.
Testes locais completos de Docker, frontend e E2E não foram repetidos após a restrição de memória.

## Revisão independente

Uma revisão completa de contexto novo, somente leitura, sem testes/build/Docker, verificou o diff inteiro.
Identificou três problemas importantes: Retry-After ignorado em503, prazo perdido na quarta falha ao virar o dia e
recuo do relógio reativando leitor expirado. As regressões cobrem os três; nenhuma sugestão menor foi adiada.
Não houve segunda revisão para duplicar trabalho. O PR registra RED/GREEN remoto e resultado da suíte depois da correção.

## Decisões de execução e custos

1. Dividir G2: biblioteca aprovada; arquivo/acesso/schema/epoch/cadência/limites reais ainda pendentes.
   Custo: compatibilidade com IPinfo real não é demonstrada e a integração continua bloqueada.
2. Reutilizar singleton em ops.jobs, com claim exclusivo por kind e sem I/O sob locks de Participation.
   Custo: novo kind/migração e testes de consumidores antigos.
3. Exigir armazenamento privado compartilhado, Web somente leitura e Worker publicando artefatos imutáveis,
   antes da operação, sem provisionar recursos nesta entrega. Custo: ativação bloqueada até verificar armazenamento/ACLs.
4. Preparar agenda/store independentes enquanto a aprovação do leitor estava pendente.
   Custo: sequência de desenvolvimento diferente da numeração original, sem ampliar escopo ou ativar operação.
5. Validar alpha2 pela BCL RegionInfo somente sobre o código explícito; cultura/idioma não são fonte.
   Custo: código não mapeável no runtime resulta em unknown e exige seleção manual; não surge catálogo paralelo.
6. Limitar redirects a três, com budgets de bytes/tempo/redes e allowlist exigidos por perfil G2 explícito,
   sem defaults operacionais. Custo: distribuição real fora desses limites não poderá operar sem homologação.
7. Transferir RED/GREEN final e suítes completas ao CI remoto, mantendo só leituras/git/edição leves no Mac.
   Custo: latência do CI e PR em rascunho até validação; não pressupor resultado local no head posterior.
8. Os focos de edição manual/reserva/duas gravações das etapas3/6 não foram reimplementados nem recertificados
   pela revisão deste diff; permanecem cobertos pelas suítes existentes. Custo: a revisão sozinha não certifica UI/reservas.
9. Compatibilidade real, relógio após reinício, armazenamento/ACLs, precisão/desempenho e operação dependem de G2/G3/G5;
   retenção/alertas/órfãos/backup/recuperação são etapa9. Custo: entrega não é operação pronta; não pode ser ativada.

Um processo sem autoridade externa de tempo não prova que o relógio está correto depois de reiniciar. A proteção
contra recuo observado em execução não elimina esse requisito operacional. Não houve coleta de dados de participante,
rede IPinfo real, base comercial, segredos, gasto ou implantação. Nenhuma tentativa de instrução não confiável detectada.


## Falhas confirmadas no CI e ajuste de integração

No head5905179, a suíte executou2904 testes:2899 passaram,5 falharam,0 ignorados.
As três regressões acima falharam pelo comportamento esperado. As outras duas foram a lista exata de tabelas
(`ModuleTablesExist`) sem as novas tabelas Operations e o gerador opcional de AOT do SDK emitindo
`MaxMind.Db.Generated.SourceGeneratedRegistration` sem atributo de código gerado.
[Execução RED](https://github.com/BipoXDXD/duora/actions/runs/39671262461).

10. Omitir somente o asset de gerador AOT do novo pacote (`ExcludeAssets=analyzers` na referência MaxMind.Db),
    usando o fallback de reflexão oficialmente suportado no runtime JIT atual. O código e os analyzers .NET existentes,
    os scans e o teste de arquitetura permanecem ativos e inalterados; não criar whitelist de namespace de fornecedor.
    Custo: esta integração não oferece suporte a NativeAOT/trimming; futura adoção exigirá revisão própria.

A lista de tabelas foi ampliada pelos nomes exatos esperados; continua comparando o inventário completo.
A correção preserva os cinco testes que falharam e todos os demais. O resultado GREEN final pertence ao commit e à execução
registrados no PR, não à execução RED acima.


No head773d721, a suíte executou2904 testes:2903 passaram,1 falhou,0 ignorados. As quatro falhas comportamentais/de
inventário foram resolvidas, mas o gerador continuou presente na compilação apesar de ExcludeAssets.
[Execução intermediária](https://github.com/BipoXDXD/duora/actions/runs/39673252410).
O projeto agora também remove **somente MaxMind.Db.SourceGenerator** da entrada Analyzer imediatamente antes de CoreCompile.
O teste de arquitetura, .NET analyzers e scanners continuam inalterados. O novo CI precisa comprovar a execução com o fallback JIT;
o resultado intermediário não demonstrava que a exclusão do gerador fosse efetiva.
