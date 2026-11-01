# #311 — garantias ausentes para S3/B2

Registro de 01/11/2026, base `40a7df4922c7d2466478a993b744c4a8f2799fa8`.
Separado da [prova FileSystem](filesystem-generation-closure-poc.md).

**Nenhum fechamento remoto foi implementado ou qualificado.** `S3ObjectStore.PutAsync` usa
`PutObjectRequest` por chave sem identidade de fechamento. `DeleteAsync` chama DELETE por bucket/chave,
sem `versionId`. Não há contrato de admissão, revogação, drenagem ou reconciliação de operação incerta.
Esses fatos vêm do código versionado; não de inspeção de conta/bucket de produção.

Antes de permitir conclusão terminal de geração incerta, demonstrar separadamente, para cada endpoint:

- autoridade que recuse todas as novas operações da geração e que nenhum cliente antigo/credencial/caminho
  possa contornar; consulta de marcador e PUT separados não bastam;
- desfecho de requisição aceita com resposta perdida, timeout, cancelamento, morte do cliente e retries,
  incluindo commit após DELETE; uma trava local não drena trabalho aceito pelo servidor;
- inventário e remoção física de versões históricas, delete markers e outros bytes atribuíveis à geração;
  ausência em GET/HEAD não prova esse requisito;
- limites de retenção/Object Lock e políticas sem encurtar as janelas ou ampliar credenciais;
- persistência da revogação diante de restore/rollback e conservação dos controles enquanto houver
  capacidade antiga; recuperação idempotente após perda da resposta;
- integração com fotografia da prova antes da varredura, token/lease de coletor e ACK no relógio do banco.

O adaptador atual não inicia multipart explicitamente; se a implementação futura introduzir multipart,
incluir upload IDs, partes aceitas, abort e commits pendentes na prova. Não inventar essa garantia por
analogia com PUT simples. Compatibilidade S3 não homologa as garantias do B2 nem de outro endpoint.

Não houve teste com provedor, conta, credencial, bucket ou infraestrutura remota. Também não foi construído
um servidor remoto simulado: isso seria teste de protocolo, não homologação do fornecedor. Para esses
adaptadores, conservar escritor incerto, limpeza repetível e `deleted_at` nulo. Este documento não altera
o bucket de backup, lifecycle, Object Lock, o ledger externo pendente da #69 ou gates de ativação.

Ponto de partida documental, ainda exigindo qualificação no futuro:
[proposta técnica](https://github.com/BipoXDXD/duora/issues/311#issuecomment-6551593250) e
[investigação integrada](interrupted-review-writers.md#armazenamento-remoto-e-cancelamento).
