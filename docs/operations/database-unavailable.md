# Banco indisponível por SqlState

Como acompanhar o 503 de banco indisponível (#498). **Não há coleta nem alerta instalados.** Os logs do web ficam no
driver `local` do Docker, só no host, e nenhum exportador lê as métricas. Até o dono executar os passos do fim deste
documento, nenhum aviso sai de produção.

## O sinal

Quando o banco fica fora do alcance, o handler global responde 503 `TEMPORARILY_UNAVAILABLE` com `Retry-After` e
emite dois sinais com o mesmo critério
([`DatabaseUnavailability.cs`](../../backend/src/Duora.Web/Http/DatabaseUnavailability.cs)):

| Sinal | Onde | Campos |
|---|---|---|
| Log `Warning` `DatabaseUnavailable` (EventId 1) | categoria `Duora.Web.Http.DatabaseUnavailableExceptionHandler` | `SqlState`, `ErrorType`, `Route` (padrão sem ids), `TraceId` |
| Contador `duora.database.unavailable` | Meter `Duora.Database` | só o rótulo `sqlstate` |

Sem SqlState (conexão recusada, pool esgotado, timeout do cliente), os dois levam `-`. Nenhum dos dois leva a
mensagem do Npgsql, que pode trazer host, porta e usuário.

A contenção (`40001`, `40P01`, `55P03`) também responde 503, mas sai no evento `DatabaseContention` (EventId 2) e
**não** entra no contador: passa sozinha em instantes e não pede ação.

Os estados que viraram 503 com `Warning` nos #476 e #492 (antes eram 500 com `Error`): `53000`, `53100`, `53200`,
`53400`, `55006`, `57P05` e `58030`. A lista completa e o porquê de cada exclusão estão no
[README da API](../api/README.md).

## Limiares iniciais

Sem medição de produção: revisar depois das primeiras semanas de coleta.

| SqlState | O que é | Limiar | Ação |
|---|---|---|---|
| `53100` | disco cheio | 1 ocorrência em 5 min | Urgente. Não se resolve sozinho: liberar espaço no volume do Postgres |
| `53400` | limite de configuração | 1 ocorrência em 5 min | Urgente. Persistente: conferir o parâmetro do servidor que estourou no log do `postgres` |
| `58030` | erro de E/S | 1 ocorrência em 5 min | Urgente. Indício de disco ou volume com defeito; conferir o log do `postgres` e o backup ([backup-restore.md](backup-restore.md)) |
| `57P05` | sessão ociosa encerrada | recorrente: em 3 de 6 janelas de 1 h | Não é urgente. Ver abaixo |
| `-`, `08xxx`, `53000`, `53200`, `53300`, `55006`, `57P01` a `57P03` | rede, pool, servidor cheio ou desligando | 20 em 5 min | Conferir se o `postgres` está de pé e se houve deploy ou reinício nesse intervalo |

`53100`, `53400` e `58030` são persistentes: sem alerta próprio, ficam escondidos atrás de um 503 que parece
passageiro. Por isso o limiar é de uma ocorrência.

### `57P05` recorrente

O servidor (ou um proxy na frente dele) encerrou uma conexão ociosa que o pool do Npgsql ainda considerava viva. Um
caso isolado não pede nada. Recorrente, indica `idle_session_timeout` menor que o idle do pool
(`ConnectionIdleLifetime` do Npgsql, padrão 300 s): é configuração, não falha passageira. A correção é deixar o
`idle_session_timeout` do servidor maior que o idle do pool, ou reduzir o idle do pool na connection string. As duas
são mudança de configuração de produção e seguem o fluxo de deploy ([deploy.md](deploy.md)).

## Leitura manual, enquanto não há coleta

Só leitura, no host, pelo dono. O driver `local` guarda 5 arquivos de 10 MB por container
([compose.prod.yml](../../infra/compose/compose.prod.yml)), então o histórico é curto:

```sh
docker logs --since 1h duora-web-1 2>&1 | grep -o 'Database unavailable ([^,]*' | sort | uniq -c
```

A saída conta as ocorrências por SqlState na última hora. O nome `duora-web-1` é o padrão do Compose para o projeto
`duora`; confirme com `docker ps`.

## Passos do dono

Mexem na infraestrutura de produção e ficam com o dono (#498):

1. **Coletar o log do web.** Escolher o destino externo (spec, "Monitoramento": Collector local leve exportando para
   fora) e enviar a ele o log do container `web`, hoje no driver `local`. A coleta não pode levar cabeçalhos nem
   corpos ([deploy.md](deploy.md), filtro dos logs); o evento `DatabaseUnavailable` já sai sem dado pessoal.
2. **Criar o alerta.** No destino, agrupar o evento `DatabaseUnavailable` por `SqlState` com os limiares da tabela
   acima. O alerta urgente (`53100`, `53400`, `58030`) precisa chegar por um canal que não dependa da VPS.
3. **Exportar a métrica, se quiser.** O contador `duora.database.unavailable` já existe no processo, mas nada o
   exporta: ligar um exportador OpenTelemetry no web é mudança de código e de infraestrutura, em PR próprio. O
   alerta pelo log basta para o #498.
4. **Testar o alerta** com um evento sintético no destino, sem provocar falha real no banco de produção.
