# Contexto de operações privadas — contrato e evidência

Fonte implementada no predecessor do aviso de expiração. O plano aprovado é
`docs/superpowers/plans/2026-10-24-private-operation-context.md`; a proteção por suspensão e os timers
continuam separados. Este documento não aceita gates operacionais G2/G3/G4/G5 e não encerra
#9, #79, #80, #108 ou #174.

## Contrato e correspondência

- `PrivateOperationContext`, o provider antiforgery e os metadados do endpoint vinculam finalidade e
  SID pelo token protegido do framework. Só `/me`200 e seu403 de senha inicial emitem o contexto.
- `MeResponse.sessionContext` e `RecoveryCodesResponse.resultSessionId` são obrigatórios. O contexto
  fica no corpo/no-store, nunca num cabeçalho de identidade. O identificador de resultado não é
  credencial. O novo403 tipado não amplia a allowlist da sessão restrita.
- A conta e seu owner opaco entram juntos no cache. O bootstrap recusado não pode proteger um owner
  que o ultrapassou; o ticket é reconferido também no catch, antes dos efeitos.
- Owner depende de usuário/SID/fase/status/email confirmado/MFA/papéis ordenados. Token aleatório
  novo na mesma sessão só afeta operações futuras. Proteção explícita cria nova geração.
- Nenhuma operação privada obtém CSRF público. As seis mutações públicas e o logout já anônimo têm
  escopo próprio. Todas conservam a geração antes da validação até o último callback.
- A ativação exige desmontagem e limpeza de cache/mutações, com verificações de epoch antes/depois
  do cancelamento e antes do ack. Um ack antigo não remove dados de um owner mais recente.
- O resultado de MFA é validado dentro da função de consulta, antes de publicar o novo Me no cache.
  Só o owner ativado recebe a chave/códigos; perda do recibo informa conclusão incerta, sem replay.

`docs/api/openapi.json` foi preparado de forma aditiva a partir dos DTOs reais, sem executar SDK no
Mac. O TypeScript foi gerado pela ferramenta travada do frontend. A geração real do OpenAPI e sua
comparação byte a byte continuam obrigatórias no gate `contract` hospedado; não se afirma que o
JSON foi gerado localmente pelo backend.

## Áudio privado e recursos

O player usa fetch vinculado à operação e Web Audio. Não há `audio.src`, URL Blob/data, fallback de
fonte, preload, autoplay ou mudança de CSP. Descrição/transcrição permanecem em texto; controles
incluem gesto inicial, pausa, reinício, som inicialmente mudo e posição pelo teclado. O botão em
carregamento mantém foco com `aria-disabled`; progresso não é região viva.

O parser espelha `ParticipationService.Catalog.RequirePcmWave`: RIFF/fmt/data canônico PCM16,
1–2 canais, 8–48kHz, até60s e6MiB. Tamanho declarado, stream acumulado e dimensões do buffer decodificado
são conferidos. A frequência real do AudioContext não pode exceder48kHz.

Como política local de consistência, para `N` frames PCM validados à taxa `Fs` e contexto à taxa `Ft`, o decoder deve produzir entre
`floor((N-1)*Ft/Fs)+1` e `ceil(N*Ft/Fs)` frames: do último instante amostrado inclusivo até o fim
nominal exclusivo. A janela só admite a quantização de um intervalo da origem na reamostragem;
não admite perda de um intervalo inteiro, crescimento além do teto quantizado ou padding. Quando a
conversão não é integral, `ceil` permite menos de um frame alvo acima da duração nominal. Taxas iguais
exigem exatamente `N` frames. A duração segue [AudioBuffer.duration](https://www.w3.org/TR/webaudio-1.1/#dom-audiobuffer-duration),
o quociente real `length/sampleRate`, mantendo os
tetos de60s/PCM. No WAV sintético de64.000 frames a8kHz convertido a48kHz, a janela é
383.995–384.000 frames, cobrindo o resultado nativo observado no WebKit;383.994 e384.001 são recusados.

| Representação reservada                          |           Limite |
| ------------------------------------------------ | ---------------: |
| Quatro entradas/cópias/headroom de6MiB           | 25.165.824 bytes |
| Dois buffers PCM de48kHz ×60s ×2 canais ×4 bytes | 46.080.000 bytes |
| Total de representações contabilizadas           | 71.245.824 bytes |
| Teto de referência                               |            72MiB |

Há um slot por documento, externo às árvores React e às gerações. Fetch/decode pendente mantém a
reserva até seu finally, mesmo depois de descarte; não existe fila. Proteção interrompe saídas,
desconecta nós, fecha o contexto e descarta referências. Só um PCM atual integra o orçamento. Isso
não mede nem garante RSS total, cópias internas do navegador, tempo físico de GC ou conforto assistivo.

O teste de navegador usa a CSP literal extraída do Caddyfile e um WAV canônico sintético. A sonda de
Caddy real também compara fetch privado sem prova403/com prova200 e decodificação. Os testes de
rejeição de mídia Blob/data permanecem. Esses cenários estão preparados para a CI; não executados no Mac.

## Evidência local e pendências

Execução local em25/10/2026, somente Node com512MiB/um worker e ambiente mínimo explícito. Não houve
SDK .NET, PostgreSQL, Docker, build de navegador ou carga na VPS no Mac.

- RED/GREEN causal de escopo público/privado, geração entre CSRF e POST, respostas e erros tardios,
  callbacks antigos, tuple/fase, bootstrap fora de ordem, barreira e formulário com resolver suspenso.
- A suíte completa percorreu990 testes:945 passaram e45 falharam em seis arquivos com a antiga
  configuração sem contexto/CSRF público/revalidação automática. Após migração explícita desses
  fixtures, os seis arquivos passaram82/82. As verificações de parsing/canários/sem replay foram
  preservadas. Não houve reparo global de respostas Me incompletas.
- Revisão parcial do checkpointdc1a876 apontou erro obsoleto de bootstrap. Três casos causais
  reproduziram a limpeza indevida de C por erro A; a correção7c648a0 reconfere o ticket antes dos
  efeitos. Parser/árvore/fronteira HTTP passaram29/29. A revisão independente dos cinco primeiros blocos
  e dessa correção foi favorável; o delta final ainda exige seu próprio parecer.
- Áudio/catálogos/Room:40 casos dirigidos aprovados; depois do ajuste de foco, os dois testes do
  player foram novamente aprovados. Decodificação cancelada entre árvores nunca libera slot cedo.
- MFA:33 casos puros iniciais e60 casos dirigidos posteriores de login/MFA/formulário público
  aprovados. Outros61 testes puros/QueryClient/formulário passaram. Os grupos têm sobreposição;
  não somar contagens como uma nova suíte completa.
- Tipos/lint das fontes alteradas passaram. O lint de app e E2E é separado por projeto; uma execução
  conjunta excedeu512MiB, e o limite não foi aumentado.

Os testes HTTP/PG foram escritos/estendidos na fonte: A→B e mesma pessoa/novoSID, GET/HEAD/escrita,
finalidade pública fechada, bootstrap403, resultado novoSID da confirmação e mesmoSID/stamp inalterado
na regeneração. Seis fronteiras positivas de login/renovação nos testes existentes agora pedem
explicitamente o bootstrap antes da próxima operação privada. O helper de login cru e os casos negativos
continuam crus; não há renovação automática por pedido privado. Não se afirma RED nem execução do backend
antes da CI.

Ainda exigidos no exato head final/base verdadeira: backend, frontend, e2e, ops, imagens AMD64 e ARM64,
secret-scan, contract e supply-chain, todos concluídos com sucesso. Evidência de ancestrais não qualifica
esta composição. Nenhum rerun manual/CI provisória ou gate reduzido está autorizado por este documento.

## Integração e operação

Preservar os ancestrais públicos237d1b,241a6 e2432472 e reconciliar a main real na criação final do PR.
O caminho GitData gerenciado pode mudar SHAs locais inéditos, preservando exatamente tree/blobs/modos
e esses pais literais; o novo head gerenciado recebe revisão e CI próprias. Não usar PAT/CLI/credenciais
legadas para contornar esse caminho.

A fila compartilhada e o operador69 controlam merge, janela e publicação. Preparar uma única entrega
Web+SPA com rollback coerente; não ativar serviços, flags regionais, transporte de e-mail ou publicação
licenciada por este trabalho. G2 técnico não é aceite durável; G3/G4/G5 e a causa da pressão de memória
da VPS continuam independentes. Nenhum resultado deste contrato encerra #174.
