# IPinfo Lite — leitura real no runtime Linux, 22/10/2026

O titular autorizou configuração/ativação e forneceu o artefato e a credencial por arquivo local. O ensaio usou
somente o artefato do fornecedor, sem dados ou IPs de participantes. Fonte autenticada previamente conferida.

O predicado baseado em `RegionInfo` rejeitava `EH` no ICU Linux da imagem .NET 10.0.402. O metadado e o schema
eram coerentes. Reprodução anterior: `REGRESSION FAIL: ISO EH rejected by the production schema predicate`.
A exceção explícita para esse código ISO não adiciona países aos catálogos de perfis/preferências. XK já era
reconhecido; os dois códigos têm regressão sintética cobrindo validação, abertura e leitura.

Após a correção, os arquivos de produção do validador, schema, manifesto e política de idade foram compilados
num executável descartável com MaxMind.Db 5.2.0, na imagem SDK fixada pelo repositório. Imagem Linux JIT,
limite de um CPU e 768 MiB. Resultado: artefato aceito em 11,30 s; pico RSS 114.982.912 bytes. Percurso completo
IPv4/IPv6 limitado a 10 milhões de redes e 64 MiB; nenhum código presente ficou não reconhecido.

- SHA-256: `c79e2755a3820baa605aef79f05074b64df3fe95eedae0fd3ca4aec0530fa5c2`.
- Tamanho: 25.750.684 bytes; tipo `ipinfo bundle_location_lite.mmdb`; formato 2.0, IPVersion 6.
- Construção: 22/10/2026 08:02:37 UTC. Revalidação: 22/10/2026 13:57:11 UTC. Construção não foi rejuvenescida.
- Ensaio anterior Python: 1.342.113 redes IPv4 e 2.940.901 IPv6 (4.283.014 no total).
- Relógio do host: NTPSynchronized=yes no ensaio. Isso não substitui acompanhamento entre processos/restarts.

Não comprova precisão geográfica, cadência de publicações, SLA de recuperação, cópia externa ou ativação dos
hosts. Os números são limites candidatos de operação, não defaults implícitos. CI do PR continua obrigatório.
O ensaio temporário não publica MMDB, segredo ou endereço privado no GitHub. IPinfo / CC BY-SA 4.0; leitor
MaxMind.Db / Apache-2.0. Evidência sanitizada guardada localmente separada do repositório.
