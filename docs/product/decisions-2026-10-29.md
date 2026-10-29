# Decisões do dono — 29/10/2026

**Estado:** decididas pelo titular do projeto em 29/10/2026 e consolidadas no
[comentário de 29/10 da #9](https://github.com/BipoXDXD/duora/issues/9); registradas pelo assistente.
Complementam os registros de [24/10](decisions-2026-10-24.md) e [28/10](decisions-2026-10-28.md); o que não está
aqui continua valendo.

1. **Requisito de 18 anos ou mais (item 5 da #9, "ECA Digital × verificação etária"):** os Termos exigem 18 anos
   ou mais, e o cadastro pede a confirmação explícita "Declaro ter 18 anos ou mais" ("I declare that I am 18 or
   older").
   - **Isto é coerência entre produto e spec, não conformidade com o ECA Digital.** Não houve parecer jurídico, e
     o texto citado na #9 veda a autodeclaração. Nenhum documento do projeto deve afirmar que o Duora cumpre o ECA
     Digital por causa desta decisão. A lacuna 5 do [mapa de dados](../privacy/data-map.md#lacunas) continua
     aberta.
   - **Termos:** versão nova `2026-10-29`, em PT-BR e EN-US. A única mudança de conteúdo é a seção "Idade mínima"
     ("Minimum age"): o serviço é só para quem tem 18 anos ou mais, e quem se cadastra declara ter 18 anos ou
     mais. O resto do texto é o da versão `2026-10-25`, que continua no catálogo para que aceites antigos
     resolvam para o texto aceito. A Política de privacidade não mudou e segue na versão `2026-10-25`.
   - **Aprovação do texto exato:** o dono decidiu o requisito, não a redação. A redação da seção vai para a
     aprovação dele na revisão do PR que a introduz; o merge desse PR é a aprovação de publicação. As fontes já
     estão com `approved: true` porque uma versão ativa não aprovada fecharia o cadastro (503).
   - **Cadastro:** `POST /api/v1/auth/register` exige `confirmedAdult: true`. Ausente, `null` ou `false` dá 400
     `VALIDATION_FAILED` com `REQUIRED` no campo; outro tipo dá 400 `INVALID_REQUEST`. Nos dois casos nada é
     gravado: nem conta, nem aceite, nem outbox.
   - **Registro da declaração:** não há campo de idade nem coluna nova. A declaração fica registrada pelo
     mecanismo de consentimento existente: a linha de `identity.consent_records` dos Termos guarda versão, idioma
     e SHA-256 do texto aceito, e o texto da versão `2026-10-29` contém a declaração.
   - **Nada de idade:** nenhuma data de nascimento, idade ou faixa etária é coletada (item 7 da #9; ADR-0029).
   - **Novo aceite de quem já tem conta:** quando este registro foi feito, o aceite só era gravado no cadastro, e
     o fluxo de reaceite ficou como trabalho separado ([#296](https://github.com/BipoXDXD/duora/issues/296)). Ver o
     item 3.

2. **Seed do property-based testing (item 12 da #9):** continua **aleatória** a cada execução, e a seed é
   registrada no log quando um teste falha, para reproduzir o caso. Não se fixa seed no CI. É o comportamento
   atual do CsCheck nas propriedades do backend: a falha informa a seed, que se repete com `seed:` no `Sample` ou
   com a variável de ambiente `CsCheck_Seed` (ver `PairSelectorPropertyTests`). Nada muda no código.

3. **Reaceite não bloqueia o uso ([#296](https://github.com/BipoXDXD/duora/issues/296), comentário de 29/10):**
   quem tem o aceite de uma versão antiga dos Termos vê um pedido de novo aceite, com a confirmação de 18 anos ou
   mais, e continua usando o app enquanto não aceita. O app só bloqueia o uso quando a conta viola os Termos ou
   quando é reprovada na verificação facial depois de atingir o limite de tentativas; esses bloqueios são de outras
   features e não fazem parte da #296. O novo aceite usa o mecanismo de consentimento existente (versão, idioma e
   SHA-256) por `POST /api/v1/me/terms-acceptances` (API-A17).

SHA-256 de conteúdo dos Termos `2026-10-29`, fixados no teste `PublishedDocumentKeepsItsExactFingerprint`:

| Documento | SHA-256 |
|---|---|
| terms/pt-BR | `7b7c64f8bb387fe287aa06018225ddda597401ce72a8b88a7742958391312ae6` |
| terms/en-US | `825198ad949b28c33a8d790809af4fe7a745969d8abc5d506249143e9a321d5c` |
