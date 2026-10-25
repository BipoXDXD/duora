# Cliente externo limitado da issue #86

Console .NET 10, exclusivo de desenvolvimento/homologação, sem referência pelos projetos de produção.
Usa o cliente oficial **Microsoft.AspNetCore.SignalR.Client 10.0.13**, MIT, e HTTP padrão do .NET.
Não implementa servidor, launcher, produtor de atestação, protocolo SignalR ou criptografia próprios.
O arquivo `profile.inactive.json` recusa execução antes de criar transporte. Nenhum destino padrão.

## Cobertura real e lacunas

- `http-read`: CSRF GET, login POST 204 por cookie, confirmação de `/me` do participante sintético, leituras
  de eventos e da sala atribuída, logout somente da própria sessão. Rotas estão no OpenAPI da fonte H
  `1d713328625c27494b2f9cba9497f3c503f2af7e`; nenhum endpoint de produto foi adicionado ou alterado.
- `signalr-invalidation`: o cliente oficial conecta por WebSocket, recebe uma notificação tipada como JSON,
  registra sequência, reconecta explicitamente 50% das conexões e reconcilia por HTTP. Não envia comando
  genérico de hub, grupo ou usuário. Exige contrato real revisado, com rota/nome/campo de sequência e recibo
  fixados pelo operador. **H não contém hub/mensagens: não existe configuração qualificada para esse modo.**
- `complete-c2` e qualquer outro modo são recusados. Mensagens/transações, corpus licenciado 18/release,
  sobreposição full/diff/WAL/restic, restore isolado/RPO/RTO e controles reais continuam lacunas explícitas.
  O recibo sempre contém `operational_accepted=false`; HTTP e notificações não homologam a capacidade.

## Admissão e isolamento exigidos

Somente o operador 69 pode qualificar uma execução. Este PR não libera janela, instala recursos ou executa
carga. O cliente é externo ao host sob teste. Não rodar no Mac do usuário, nem usar Docker local.

1. Fonte final, imagem/release/corpus e configuração exatas, revisadas; passagem própria com capacidade,
   ancestralidade de cgroups e preservação dos quatro projetos/backups. Gates de memória não são alterados.
2. Listener sintético isolado com **IPv4 explícito, porta 40000–60999 e SNI
   `qualification.<runIdN>.invalid`**. Nada de DNS, proxy ou redirecionamento automático. O endereço é fixado
   na conexão TCP; IPv6, redes privadas, loopback, link-local e metadata são recusados. Documentação IPv4 é
   aceita para testes de política sem tráfego. O operador deve provar que o listener não encaminha produção.
3. mTLS, CA própria e pin SHA-256 do certificado folha; a validação mantém nome/validade/EKU/cadeia customizada.
   Revogação online/download de certificados ficam desligados no laboratório isolado: validade curta,
   pinning, revogação operacional e egress pertencem à qualificação. Nenhuma chave é gerada pelo cliente.
4. Perfil, dataset de exatamente 100 identidades `.invalid`/GUIDs/salas próprias e capabilities em arquivos
   privados, sem links simbólicos ou ancestrais graváveis por grupo/outros; hashes imutáveis. O operador
   qualifica dono/diretório e impede troca concorrente desses arquivos. O perfil privado inteiro é fixado
   por SHA na invocação, não apenas por `operatorQualified=true`. Hashes/JSON não provam isolamento real.
5. Recibos externos de qualificação e supervisor identificados no perfil; produtor de lease independente
   previamente qualificado. O cliente apenas consome `GuardLease`: run/fase/início absoluto/fonte/dataset/
   produtor devem coincidir, observação/expiração de no máximo 3s, stop=false. Erro de leitura, atraso ou
   divergência interrompem, inclusive durante requisições em voo (watchdog 100ms). Não há daemon novo aqui.
6. Supervisor externo com limites de CPU/RAM/processos/egress/tempo e encerramento atribuído, necessário
   também para uma chamada de biblioteca que não finalize. Buffers de transporte SignalR são de 64KiB;
   isso **não** é um limite rígido por mensagem nem comprovação de RAM. O servidor sintético deve impor
   payload/ritmo/tamanho de mensagem; processo e supervisor continuam sujeitos à homologação.

`QualificationReceiptSha256` e `ExternalSupervisorReceiptSha256` são referências a evidências aprovadas,
não mecanismos de criação/assinatura nem validação autônoma do host. Sem essas entregas reais, perfil NULL.

## Autenticação e dados

Contratos públicos usam cookies, não JWT. O cliente coleta CSRF e envia `X-XSRF-TOKEN` da mesma cookie jar;
`Origin` é a origem isolada. Valida ID/e-mail/status Active/e-mail confirmado e papéis vazios ou somente
`participant`, antes de ler a sala do dataset. HTTP 401/403 (incluindo `MFA_REQUIRED`), 429 e qualquer status
inesperado param a fase. Não repete POST incerto, não cadastra usuários e não altera MFA, senha ou política.
Credenciais MFA não são geradas nem contornadas: esse perfil só é qualificável com participantes sintéticos
cujo login por senha já seja permitido pela política real. Isso não cobre a matriz MFA/reauth do produto.
Corpos/IDs/e-mails/senhas/cookies/tokens/notificações não são gravados. Logs SignalR estão desligados.

`SyntheticDataset`: `runId`, `sourceCommit`, `syntheticOnly=true`, `users` com 100 itens
`id/email/password/roomId`; e-mail exato `participant-<idN>@<runIdN>.invalid`. Dataset é secreto, nunca commitado.
`CapabilityBinding`: `runId/sourceCommit/datasetSha256/operatorQualified/openApi` (PinnedFile), e quatro
campos SignalR nulos para HTTP: `hubPath/notification/sequenceProperty/signalRContractReceiptSha256`.
O OpenAPI fixado deve conter todas as seis operações usadas. Para SignalR, o recibo de contrato real
é obrigação externa adicional; não inventar `/hubs/updates` a partir de um ADR de intenção.

## Sequência, prazos e parada

`PhasePlan.Required()` contém 3 repetições de warmup 300s; rampa 10/25/50/100, 300s cada; steady 100/1800s;
burst 100/60s; 3 fases de reconexão 50%/10s; recovery 10/900s. O roteiro é fixo, não encolhível pelo perfil.
Cada fase tem reserva adicional de 120s para teardown. São **33 fases / 16830s (4h40m30s)** com as reservas,
fora a preparação externa. Os units antigos de 1150/1230s não comportam esse roteiro. Full/diff precisam de
execuções e recibos distintos, respeitando locks; não são disparados pelo cliente.

Esse é um roteiro de **leituras/notificações**: `burst` nomeia a etapa de 100 usuários, não prova 100 mensagens/s.
HTTP parcial para antes da primeira etapa que exige reconexão, com `reconnect_capability_missing`; não
simula essa capacidade, não a pula e não emite `client_completed=true` para o roteiro inteiro.
Tick por participante 1000–5000ms; no máximo 100 operações em voo e 1 milhão de admissões por fase.
O permit HTTP cobre headers, corpo limitado e descarte. Conexões persistentes são orçamento separado:
até 100 WebSockets e 100 conexões HTTP neste roteiro, sujeitos ao limite externo de descritores/processo.
O encerramento por prazo só é parcial válido se **cada** participante autenticou/validou identidade e
concluiu ticks com intervalo primeiro→último de pelo menos 90% da duração; é uma guarda do recibo do cliente,
não um relaxamento do aceite C2. Notificações válidas e a reconexão da metade exigida são julgadas por sessão
depois do teardown, incluindo payload inválido tardio. Latência/percentis e cobertura temporal completa da
matriz operacional ainda precisam de evidência própria; os contadores deste cliente não os substituem.
Contagem é feita antes da operação, sem retry. Prazo absoluto e monotônico não deslizam por relógio ou lease
renovado. Interrupção Ctrl-C/stop/stale/deadline cancela trabalho em voo; fases posteriores não começam em falha.

Logout tem orçamento separado de no máximo 100 operações, apenas na reserva e com lease fresco/sem stop.
Um watchdog `CheckCleanup` permanece ativo durante toda a reserva, vinculado ao token das operações em voo.
Login incerto ou login204 seguido de identidade não confirmada exige `cleanup_unknown`, sem logout de
identidade desconhecida. Apenas sessão criada e identidade sintética comprovada admitem a revogação própria.
Na falta disso, só dispose dos recursos próprios e `cleanup_unknown`; não declarar quiescência, repetir
create/start/POST ou apagar ledger/watchdog. Supervisor/69 conciliam sessões/recursos sintéticos restantes.
Uma falha de teardown para o roteiro; não iniciar outra fase nem repetir logout incerto.

As sequências precisam começar em 1 e ser monotônicas por participante no contrato real; duplicação,
gap, desordem ou ausência de recibo impede sucesso SignalR. Reconexão não cria sequência nova artificial.

## Validação e execução futura

Somente em ambiente de desenvolvimento isolado, para os testes novos sem rede:

```sh
dotnet restore backend/benchmarks/Duora.CapacityClient.Tests/Duora.CapacityClient.Tests.csproj --locked-mode
dotnet build backend/benchmarks/Duora.CapacityClient.Tests/Duora.CapacityClient.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test --project backend/benchmarks/Duora.CapacityClient.Tests/Duora.CapacityClient.Tests.csproj --no-build --no-restore --max-threads 1 --parallel none
```

Não encaminhar opções MSBuild ao runner MTP. CI normal do repositório permanece obrigatória; não mudar
workflows/scanners/allowlists. O novo grafo só adiciona SignalR ao console e seu projeto de testes.

A invocação futura, **depois de todas as admissões**, exige `--profile <arquivo privado absoluto> --sha256
<hash aprovado> --receipt <arquivo novo na mesma pasta privada>`. Não há comando de ativação neste documento.
Saída padrão é apenas um status fechado; recibo limitado contém contadores, fases, cobertura e hashes,
sempre sem aceite operacional. Código 2 também abrange resultado parcial/cleanup desconhecido.

Fontes primárias: [configuração oficial SignalR](https://learn.microsoft.com/aspnet/core/signalr/configuration?view=aspnetcore-10.0),
[cliente .NET](https://learn.microsoft.com/aspnet/core/signalr/dotnet-client?view=aspnetcore-10.0),
[ClientWebSocket com HttpMessageInvoker](https://learn.microsoft.com/dotnet/api/system.net.websockets.clientwebsocket.connectasync?view=net-10.0),
[NuGet Microsoft 10.0.13](https://www.nuget.org/packages/Microsoft.AspNetCore.SignalR.Client/10.0.13).
Nupkg oficial 82802B, SHA-256 `cfa72fcaf0df6029dee3f2c769dbabc48a54f0336c83b6e5b131ceb9d15f69ff`,
repository commit `4099f83977f4d29604a9aeb971fb0a7bc1d5f699`; licença MIT no nuspec. Locks em ambos projetos.
