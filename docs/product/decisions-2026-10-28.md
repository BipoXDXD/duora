# Decisões do dono — 28/10/2026

**Estado:** decididas pelo titular do projeto em 28/10/2026, em conversa direta, e registradas pelo assistente.
Complementam a [#9](https://github.com/BipoXDXD/duora/issues/9) e o [registro de 24/10](decisions-2026-10-24.md);
o que não está aqui continua valendo.

1. **Controlador publicado:** Luca Grimmer, pessoa física, sem CNPJ. O nome entra exatamente assim em
   `controllerName` das quatro fontes de `docs/legal/` (Termos e Privacidade, PT-BR e EN-US) e no texto que
   identifica o responsável. O contato continua `support@meetduora.com`.
2. **Sem contatos de emergência por país:** o produto e os documentos não publicam números de emergência por
   país (item 11 da #9). Isso **substitui** o item 21 do registro de 24/10. Saíram a área "Segurança e contatos de
   emergência" do layout público, o link "Segurança" do cabeçalho, as chaves `emergency.*` dos catálogos de
   idioma, os testes que exigiam os números e `docs/safety/emergency-contacts.md`. A orientação genérica de
   procurar o serviço de emergência local, sem número, continua nos modelos de suporte
   ([support-incidents.md](../operations/support-incidents.md)). Sair da sala imediatamente e sem formulário
   (spec 7.3) e bloquear sem depender de denúncia não mudam.
3. **Textos aprovados e publicados:** o dono aprovou os quatro textos da versão `2026-10-25` (Termos e Privacidade,
   PT-BR e EN-US) como estão. As fontes passam a `approved: true` e o catálogo embutido foi regenerado
   (`python3 scripts/supply-chain/legal_documents.py --write`); a API responde `published: true`,
   `controllerName: "Luca Grimmer"` e `registrationAvailable: true`, e o gate de documentos deixa de recusar o
   cadastro. Os outros gates de operação (implantação, entrega real do e-mail de confirmação, gates regionais) não
   mudam com esta decisão.

Os textos foram aprovados na versão `2026-10-25` porque nenhum deles tinha sido publicado nem aceito antes; a partir
de agora, qualquer mudança de texto, identidade, contato ou tradução exige versão nova
([public-legal-documents.md](../identity/public-legal-documents.md)).
