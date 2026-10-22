# Baseline do sanitizador atual — S0 / #174 / #86

Esta entrega caracteriza `ImageSharpPhotoSanitizer` atual, com fonte vinculada diretamente do Worker num
host **somente de teste**. Nenhum decoder de produção é extraído, nenhum launcher/sandbox instalado e
nenhuma política, cota ou limite executável alterado. M0 da #173 é contrato documental futuro.

## Método reproduzível

`SanitizerResourceEnvelopeTests` gera18entradas sintéticas com seed0x1742026 e registra o SHA-256 de cada uma.
O corpus inclui JPEG/PNG/WebP texturizados5000x4000(20MP), PNG com ruído denso1280x1280, metadados/ICC nos
três formatos, GPS/orientação, dois poliglotas, animaçãoPNG/WebP, tipo trocado, bombas nos três formatos,
truncamento e entrada de5MiB+1byte recusada pelo sanitizador. Nenhuma imagem real ou segredo é usado.

Cada entrada roda duas vezes em processos novos, sequencialmente dentro do ensaio. O helper herda somente
DOTNET_ROOT/DOTNET_NOLOGO e não inicia host, storage, fornecedor ou rede. O teste exige mesmos hashes/bytes/
dimensões das variantes, resultados técnicos esperados e ausência de metadados; rejeições não têm variantes.
Source link é compilação do mesmo arquivo, não uma reimplementação. O helper fica em backend/tests e não
integra publicação de Web/Worker; ImageSharp4.1.3/licença já existentes permanecem fixados.

Campos por amostra no output xUnit: input e hash, resultado/código, dimensões/bytes/hash por variante,
SanitizerElapsedMilliseconds, SanitizerCpuMilliseconds, AllocatedDuringSanitizeBytes, RSS inicial/pico,
GC/runtime/arquitetura/SO e memory.current do cgroup quando disponível. Resumo tabular no job backend.
Hashes permitem reproduzir o corpus e resultados sem publicar bytes de imagem.

O intervalo é toda a chamada SanitizeAsync, não exclusivamente decode. RSS é o pico observado do processo
até o retorno da sanitização, incluindo runtime/entrada; não é memória exclusiva do decoder. O contador de
alocações é global desse processo, não bytes vivos nem limite de RSS. Geração da entrada ocorre no processo
pai e fica fora da amostra filha; validação/hashing das saídas ocorre após a amostra de RSS/tempo. Buffers
reportados como input/output são tamanhos visíveis, não inventário completo dos temporários internos.

Cgroup é herdado/compartilhado com o runner; memory.current não pode ser atribuído à foto ou ao filho e não
é pico isolado do sandbox. Ausência de cgroupv2/arquivo aparece como null, nunca zero inventado. CPU/tempo
são afetados pela concorrência normal da suíte e não constituem benchmark de capacidade/SLA.

## Comparação com candidatos

scripts/media/sanitizer-profile-v1.json apenas transcreve os candidatos P174-3/4, marcado
candidate-not-production-approved e sem instalação/enforcement. Registrar maiores saídas/RSS/tempo observados
no CI e compará-los aos candidatos; não usar512MiB/50s/16MiBporvariante/24MiBtotal como aprovação deste teste.
Se ultrapassar candidato, manter o fato no relatório e revisar o perfil; não truncar saída, ampliar upload,
alterar algoritmo, retirar caso ou enfraquecer gate para obter aprovação. O corpus não prova pior caso nem
exige que uma saída válida alcance o teto candidato. Encoder/versão, parâmetros e seed fazem parte da evidência.

## Resultado remoto

Ainda não atestado neste arquivo inicial. O PR registrará head exato, run e resultados reais; uma execução
será vinculada após os checks. Não executar build/restore/Docker/testes no Mac para preencher esta seção.
Startup/transferência/cleanup do sandbox, overhead do launcher/daemon rootless, diferenças GC do Worker real,
enforcement e host/release escolhido são entregas S1–S3/C1–C2, não medidas deste baseline.

#173/#174/#86/#69 continuam abertas; upload, selfie, filtro/detector e novos serviços permanecem inativos.
