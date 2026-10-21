# Evidências da investigação ADR-0034

Material diagnóstico da [investigação #152/#153](../../reports/2026-10-21-issues-152-153.md), sobre executáveis
idênticos à base `6ade44a8f4cf88be1b7250aeb7d26dce9d34f656`. Os arquivos de código terminam em .txt: não fazem
parte de compilação ou testes de produto. Não implementam negociação nem alteram contrato.

- [observations.json](observations.json): seis respostas sintéticas de Kestrel e controles de metadado/schema;
  traceIds variáveis substituídos por marcador. Não contém cookies, tokens, fotos, contas ou dados reais.
- [schema-validation.json](schema-validation.json): resultado da validação das capturas originais.
- Os quatro .txt abaixo são os probes usados, para reprodução isolada. Python jsonschema 4.25.1 é dependência
  somente da venv de investigação ([pacote e licença MIT](https://pypi.org/project/jsonschema/4.25.1/)).

## Reproduzir sem modificar projetos/contratos

Requisitos: Docker local, SDK .NET 10.0.400, Node 24, pnpm 12.11.2, Python >= 3.9. Use um clone isolado
contendo este relatório, com diretório chamado `duora`. Os scripts TypeScript/Python usam esse nome no
caminho relativo para a fonte. Os executáveis do clone devem permanecer iguais à base indicada acima.
Defina REPO como a raiz desse clone. PROBE ficará em uma pasta irmã; não use ambiente implantado.

```sh
REPO="$PWD"
PROBE="$REPO/../probes/153"
mkdir -p "$PROBE"
cp docs/testing/evidence/adr-0034/http-probe.cs.txt "$PROBE/ValidationContractProbe.cs"
cp docs/testing/evidence/adr-0034/probe.targets.txt "$PROBE/Probe.targets"
cp docs/testing/evidence/adr-0034/schema-check.py.txt "$PROBE/validate_published.py"
cp docs/testing/evidence/adr-0034/client-probe.ts.txt "$PROBE/frontend-probe.ts"
python3 -m venv "$PROBE/venv"
"$PROBE/venv/bin/pip" install 'jsonschema==4.25.1'
dotnet restore backend/tests/Duora.IntegrationTests/Duora.IntegrationTests.csproj --locked-mode
dotnet build backend/tests/Duora.IntegrationTests/Duora.IntegrationTests.csproj --no-restore -p:CustomAfterMicrosoftCommonTargets="$PROBE/Probe.targets"
DUORA_PROBE_OUTPUT="$PROBE/captures" dotnet backend/tests/Duora.IntegrationTests/bin/Debug/net10.0/Duora.IntegrationTests.dll -class Duora.IntegrationTests.ValidationContractProbe -xml "$PROBE/probe-results.xml"
"$PROBE/venv/bin/python" "$PROBE/validate_published.py"
pnpm --dir frontend install --frozen-lockfile
frontend/node_modules/.bin/tsc --ignoreConfig --strict --skipLibCheck --target ES2022 --module commonjs --types node --typeRoots "$REPO/frontend/node_modules/@types" --rootDir "$REPO/.." --outDir "$PROBE/ts-output" "$PROBE/frontend-probe.ts"
DUORA_CAPTURE_DIR="$PROBE/captures" node "$PROBE/ts-output/probes/153/frontend-probe.js"
pnpm --dir frontend test src/shared/api/api-client.test.ts src/shared/api/request-failure.test.ts src/shared/ui/problem-canary.test.tsx --maxWorkers=2
git diff --exit-code -- backend frontend docs/api .github AGENTS.md Directory.Build.props Directory.Packages.props
git diff --check
```

O probe é incluído só na compilação local do assembly de testes, por import MSBuild externo. A aplicação e
os projetos permanecem intactos. O teste usa a fixture PostgreSQL existente e destrói o contêiner no fim.
Os schemas com opções clonadas e declarações hipotéticas são controles de diagnóstico, não contratos
adotados. Nenhum novo formato é retornado pelo servidor capturado.

Resultados esperados: 1 teste HTTP aprovado; 6 rejeições estritas por code/traceId e controles aprovados;
probe TypeScript aprovado; 70 testes existentes aprovados; zero diff nos arquivos protegidos acima.
Para descartar o assembly de teste instrumentado, fazer build normal do projeto sem o parâmetro de import;
não é necessário editar fonte ou remover qualquer teste versionado.
