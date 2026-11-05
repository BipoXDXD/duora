# Prova experimental de fechamento FileSystem — #311

**Protótipo descartável, sem admissão operacional.** Não altera `IObjectStore`, preparadores,
coletores, schema, retenção, uploads ou produção. Não oferece evidência que possa ser gravada
no banco como fechamento. Os testes `counterexample` demonstram garantias **ausentes**.

A pergunta é se um protocolo cooperativo consegue drenar PUTs locais e recuperar o fechamento
após morte real do processo, e quais capacidades ainda permitem contorná-lo. O executável compila
por link o `FileSystemObjectStore`, `ExactLengthStream` e as chaves atuais; não copia sua lógica de PUT.

## Reprodução

Na raiz do repositório, com Docker local (Linux arm64) e acesso para construir a imagem de teste:

```sh
scripts/testing/private-generation-closure/run_linux.sh
python3 scripts/testing/private-generation-closure/check_mutations.py
```

O segundo comando também precisa do SDK .NET 10.0.403 no host. O build de teste instala Python 3
(licença PSF; pacote da distribuição) somente na imagem descartável; nenhuma dependência é adicionada
à aplicação. A base do SDK está presa ao digest arm64 usado no ensaio. Não atribuir cobertura nativa
amd64 a esse comando. A função dos valores nativos de `open` é explícita por arquitetura; outros
valores/arquiteturas são recusados. O build preserva os analisadores e avisos como erros do repositório.

Os testes rodam como UID/GID 1654, sem rede, capacidades Linux ou escrita na raiz da imagem. Dados
sintéticos ficam em volumes Docker descartáveis, separados de `/tmp` (tmpfs apenas para o runtime).
Nenhum diretório de mídia, configuração ou segredo do host é montado. O teste de mutação monta somente
o binário sintético, para leitura. O runner remove os volumes ao sair; não interrompe outros containers.

## Protocolo medido

Identidade: versão 1, domínio/encarnação sintética, área e UUID da geração. `reserve` é exclusivamente
bootstrap offline da fixture; não é API de admissão nem comprova que existe um manifesto SQL.
Nunca é chamado implicitamente por escrita, fechamento ou recuperação. Uma reserva existente não
é reinicializada. Ausência de reserva/trava, corrupção de reserva/fechamento e encarnação incorreta recusam operações.
A ausência isolada de `closed` é indistinguível do estado originalmente aberto; isso é outro
contraexemplo explícito, e exige proteger/reconciliar os controles antes da qualificação.

`write` abre o inode estável de `lock` sem criar e com `O_NOFOLLOW|O_CLOEXEC`; obtém `flock(LOCK_EX|LOCK_NB)`,
valida a reserva e a ausência de `closed`, mantendo a posse desde antes do primeiro `.partial` até
concluir/dispor todo o I/O das três variantes. Não destaca tarefas nem reutiliza admissão após liberar
a trava. `BUSY` significa ausência de prova; não há prazo que quebre a trava.

`close` obtém a mesma trava e valida a identidade. Grava `closing`, sincroniza conteúdo, renomeia para
`closed` sem sobrescrever, sincroniza arquivo e diretório antes de responder. O rename é o ponto em
que outros escritores cooperativos passam a ver a revogação; a resposta só vem após a sincronização.
Retry valida e sincroniza novamente o registro existente. Falha antes do rename não confirmou fechamento;
um `closing` incompleto pode ser refeito sob a mesma trava. Registro `closed` corrompido não é consertado
silenciosamente. A trava nunca é removida/substituída pelo protocolo.

`sweep` exige fechamento anterior, valida/sincroniza a evidência local antes dos DELETEs, remove as três
chaves e somente temporários com o sufixo exato do adaptador (`.<32 hex>.partial`), depois sincroniza o
diretório de dados. Não usa idade. Não toca outras gerações ou assets legados. Um temporário com nome
não atribuível fica para reconciliação; `SWEPT` não é prova universal de ausência de bytes arbitrários.

`CLOSED` e `SWEPT` são resultados **experimentais condicionais à cooperação e integridade dos controles**.
Não representam `writer_settled_at`, autorização de negócio, posse SQL nem `deleted_at`. Não há fallback
remoto e nenhuma referência ao executável no build/publicação da aplicação.

## Evidência e limites

As barreiras por stdin/stdout param operações reais antes da admissão, na leitura de EOF feita pelo
adaptador antes do rename, após PUT, nos passos do marcador e após cada DELETE. `SIGKILL` é real e o pai
espera a morte antes de recuperar. Limites de espera só detectam travamentos. Há teste de descritores
`CLOEXEC` em `/proc`, e teste de herança intencional da descrição aberta de uma trava por outro processo.

Os contraexemplos reproduzem escrita posterior quando um escritor legado ignora a trava, quando o dono
do diretório troca seu inode ou quando um snapshot antigo restaura controles abertos. O snapshot é uma
cópia sintética offline; não é o restore operacional do Duora. Restaurar somente mídia, preservando os
controles fechados, permite repetir a remoção; não impede por si só que o restore copie bytes antigos.
O ambiente tem de continuar sem admissão/leitura até reconciliar esses bytes.

O teste de mutação detecta ausência de validação, liberação prematura da trava e ACK sem marcador.
A mutação que omite sincronização de diretório **sobrevive** aos testes de processos: evidência explícita
que esse ensaio não homologa durabilidade contra perda de energia/crash do host. Injeção de erro antes
de fsync testa propagação de falha, não simula perda de gravações. Reinício entre containers conserva o
mesmo kernel/volume; não é reinício abrupto da VM. NFS/SMB, disco de produção, launcher e S3/B2 não são
qualificados por este protótipo.

Inventário, resultados, matriz da proposta e próximos gates:
[relatório](../../../docs/testing/reports/filesystem-generation-closure-poc.md).
