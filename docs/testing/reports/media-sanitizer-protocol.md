# S1 — protocolo e executável separados

Entrega em andamento no PR #186, para #174. Sem instalação, transporte de produção ou ativação.
O Worker ainda registra o sanitizador atual; a remoção ocorre somente na S3, após o launcher da S2.

## Evidência inicial

- Head `fc134a5f8b3f3f1bf3e8152eaa94675a6b471e11`, [CI39739563347](https://github.com/BipoXDXD/duora/actions/runs/39739563347):
  restore travado, build e formatação passaram. Backend: 3.078 testes, 42 falhas esperadas por
  `NotImplementedException` na API ainda vazia do protocolo; restantes 3.036 passaram.
- Head `cfc1901`: compilador apontou comparação de pattern `int` com limite `long`; corrigida
  para comparação numérica normal, preservando o teto de 5 MiB. Essa execução não é prova funcional.
- Testes adicionais exigem o executável separado e rejeição de envelopes WebP/metadados mesmo com
  hash de transporte válido. Head `abea93c`, [CI39741954588](https://github.com/BipoXDXD/duora/actions/runs/39741954588):
  build/formatação passaram; backend 3.090 testes, 3.079 passaram, 11 falhas esperadas e zero ignorados.
  Os 42 casos iniciais passaram. Dez falhas demonstram aceitação indevida de metadados/framing de imagem;
  uma demonstra ausência do executável separado. Implementação posterior adiciona essas duas partes.
  Resultados finais serão registrados no head efetivamente validado.

Nenhum restore, build, instalação, Docker ou teste pesado local. `git diff --check` é a verificação
local leve; todos os testes executáveis pertencem ao GitHub Actions. Nenhuma issue concluída por este registro.

## Limites

Protocolo não é sandbox. Ainda faltam launcher, controle do runtime/cgroups, ausência de rede/segredos,
supervisão externa e término confirmado, integração do Worker sem fallback e homologação operacional.
512 MiB/1 CPU/64 PIDs/tmpfs16 MiB/50s e 16/24 MiB continuam candidatos, não capacidade aprovada.
