# Implementação M5a1 — validação da barreira

1. Fixar contrato puro/escopo/estado/canonicalização e scaffold que recusa; preparar testes adversariais independentes de provider. Revisão estática, nenhuma prova externa inferida.
2. Depois da composição qualificada e término dos jobs anteriores, CI hospedada RED antes do validador. Falha de compilação/fixture não éRED.
3. Implementar validação completa bounded/failclosed, revisão independente e CI. Sem adapter, registration, SQL/delete, segredo/namespace ou deploy real.
4. Bindings criptográficos, paginação estável, CreateOrGet/CAS/witness/fencing/epoch e custódia do provider selecionado exigem testes próprios e inventário real; depois integração ao protocolo M5 SQLAborting/SQLCommitted e restore. Não encerrar173/69 ou declarar restore seguro só pelo núcleo puro.
