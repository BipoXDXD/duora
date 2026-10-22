# Protocolo de sanitização v1 — S1

Detalhamento técnico do ADR-0036. Esta entrega não instala launcher, não troca o sanitizador do Worker
e não habilita uploads. Perfil e limites continuam candidatos de ensaio, não homologação operacional.

## Framing

Inteiros de 32 bits são little-endian com sinal, validados antes de alocação. Identificadores são bytes
opacos, nunca texto. Cada processo recebe uma requisição e produz uma resposta; ambas terminam em EOF.
Não há JSON, extensões, nomes de arquivo, caminhos, comandos, ambiente, URLs, dono ou credenciais.
Todo campo desconhecido, sobra, truncamento ou hash divergente invalida a mensagem inteira.

Requisição: magic ASCII `DMRQ` (4), versão `1` (1), nonce (16), formato (1), comprimento (4),
SHA-256 do original (32), teto por variante (4), teto total (4), teto stderr (4), prazo ms (4),
seguido exatamente dos bytes declarados e EOF. Cabeçalho fixo: 74 bytes.

Formato usa valores de fio próprios: JPEG=1, PNG=2, WebP=3. Comprimento: 1–5 MiB.
Nonce identifica uma tentativa; o chamador gera 16 bytes aleatórios. A requisição não escolhe
qualidade, dimensões, memória, paralelismo ou configuração do decoder.

Orçamentos só restringem: variante 1–16 MiB; total 1–24 MiB; stderr 0–64 KiB; prazo 1–50.000 ms.
São limites de transporte. O decoder mantém 20 MP, 256 MB de alocador e 30 s cooperativos;
o launcher futuro aplicará o prazo externo e limites de cgroup, incluindo encerramento confirmado.

Resposta: magic ASCII `DMRS` (4), versão `1` (1), mesmo nonce (16), mesmo SHA-256 de entrada (32),
resultado (1). Rejeição=0: um byte de código e EOF. Sucesso=1: formato detectado (1), largura e
altura orientadas (4+4), contagem obrigatória `3` (1), três frames na ordem thumb/card/full e EOF.

Frame: tipo (1: thumb=1/card=2/full=3), largura (4), altura (4), comprimento (4), SHA-256 (32),
bytes WebP. Dimensões devem corresponder ao fit existente, sem ampliar o original; dimensões
originais positivas e produto máximo 20 MP. Teto individual e soma são conferidos antes de alocar
cada payload. Metadados fixos de sucesso somam 199 bytes, abaixo de 4 KiB.

Códigos técnicos de fio: UnsupportedFormat=1, TypeMismatch=2, TooManyPixels=3, Animated=4,
CorruptImage=5, ProcessingLimit=6. Não transportar decisões editoriais. O escritor recusa
resultados incoerentes antes de começar uma resposta. O leitor confere framing, identidade, hash,
assinatura WebP e dimensões declaradas; ele não decodifica imagens nem comprova a segurança do decoder.

stderr nunca entra em logs: drenar e descartar até o teto, recusar excesso. EOF pode bloquear;
todas as operações recebem cancelamento, e o supervisor futuro deve matar e confirmar a unidade
inteira. Uma resposta válida sozinha não permite publicação: S2/S3 exigem saída normal e término
confirmado antes de entregar o resultado ao pipeline.

## Vetores e evidência

Os testes de fio montam bytes independentemente do escritor e exercitam leitura fragmentada,
comprimentos adversariais, identidade, integridade, códigos, EOF e orçamentos. O corpus sintético
da S0 será executado contra o novo executável, comparando resultados e bytes com o algoritmo atual.
Nenhum restore/build/teste local: execução e evidência exclusivamente no GitHub Actions.
