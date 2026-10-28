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
   PT-BR e EN-US) na forma do commit `97a44f1`. As fontes passam a `approved: true` e o catálogo embutido foi
   regenerado (`python3 scripts/supply-chain/legal_documents.py --write`); a API responde `published: true`,
   `controllerName: "Luca Grimmer"` e `registrationAvailable: true`, e o gate de documentos deixa de recusar o
   cadastro. Os outros gates de operação (implantação, entrega real do e-mail de confirmação, gates regionais) não
   mudam com esta decisão.
4. **Ajustes no aviso de privacidade depois da aprovação, também aprovados em 28/10:** a revisão
   independente do PR pediu mudanças no aviso de privacidade (PT-BR e EN-US), feitas antes de qualquer publicação
   ou aceite. Foi incluída a seção da selfie de presença, com os fatos decididos pelo dono para a #93: Amazon
   Rekognition Face Liveness na AWS us-east-1, DPA padrão, nenhuma imagem guardada, resultado aprovado guardado
   enquanto a conta existir e tentativas não aprovadas apagadas em 30 dias. Saiu a promessa de publicar nova
   versão antes de ativar a selfie ou um provedor de e-mail, e a AWS entrou na lista de fornecedores. O prazo de
   65 dias das cópias de segurança deu lugar a uma redação sem prazo, porque a retenção do backup do banco ainda é
   proposta ([retention.md](../privacy/retention.md)). Os Termos não mudaram. A flag `approved` continua
   `true`. O dono aprovou estes ajustes em 28/10/2026, na conversa com o coordenador: perguntado se aprovaria a
   lista do que mudou depois da aprovação original, respondeu "2. Sim". Os SHA-256 finais abaixo identificam o
   texto aprovado.

5. **Mantém as três mudanças no aviso:** à pergunta "Mantém as 3 mudanças no aviso?", o dono respondeu
   "1- Sim" (selfie, fornecedores e redação do backup do item 4).
6. **Resend ligado para o e-mail de confirmação:** à pergunta "Ligo o Resend para o e-mail de confirmação
   chegar, ou cadastro fica fechado?", o dono respondeu "2- Liga". O aviso de privacidade (PT-BR e EN-US) ganhou um
   parágrafo em "Operação e fornecedores": e-mails transacionais (confirmação de cadastro, redefinição de senha e
   avisos da conta) enviados pela Resend (Resend, Inc., EUA), domínio meetduora.com, envio pela região sa-east-1
   (São Paulo), endereço do destinatário e conteúdo processados só para entregar, rastreamento de abertura e de
   clique desligado. A ativação em produção é um passo de operação separado deste texto.

SHA-256 de conteúdo da versão `2026-10-25`, fixados no teste `PublishedDocumentKeepsItsExactFingerprint`:

| Documento | Primeira aprovação (`97a44f1`) | Final, aprovado |
|---|---|---|
| terms/pt-BR | `07fd125a…f30227` | igual |
| terms/en-US | `fc36a782…27c8b6` | igual |
| privacy/pt-BR | `2f1428b3…b13d2` | `ee20f316d68c3e7c08d2fd69f763358e6f8d8142164b9cf1afc8b1d722c8405a` |
| privacy/en-US | `2789b0d6…58c4` | `a4802a18c0f2d731784375c7bba88d70f9e8778168d36b885297f59f0b6e174d` |

Os textos foram aprovados na versão `2026-10-25` porque nenhum deles tinha sido publicado nem aceito antes; a partir
de agora, qualquer mudança de texto, identidade, contato ou tradução exige versão nova
([public-legal-documents.md](../identity/public-legal-documents.md)).
