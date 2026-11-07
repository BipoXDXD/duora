# Decisões do dono — 07/11/2026

**Estado:** decididas pelo titular do projeto em 07/11/2026 e registradas pelo assistente. Complementam os registros
de [28/10](decisions-2026-10-28.md) e [29/10](decisions-2026-10-29.md); o que não está aqui continua valendo.

1. **Identificação retirada dos sites, por enquanto:** a ordem foi "retire o nome da pessoa física dos sites por
   enquanto". Isto substitui o item 1 do registro de 28/10 para as versões novas.
   - **Termos e Privacidade:** versão nova `2026-11-07`, em PT-BR e EN-US. A única mudança de conteúdo é a
     identificação. `controllerName` é o texto neutro exato `Responsável pelo Duora`, igual nas quatro fontes. O
     contato continua `support@meetduora.com`. Nenhuma empresa entra no lugar: a operadora não aparece sem CNPJ e
     aval do dono.
   - **Temporário:** a identificação volta, em versão nova, quando houver CNPJ.
   - **Gate de publicação:** escolhida a opção A entre texto neutro único, `null` (cadastro fechado com 503) e rótulo
     por idioma (exigiria afrouxar o gate). O texto neutro passa no gate sem identificar ninguém, e o gate não foi
     alterado. Os testes exigem o texto exato nas quatro versões ativas e falham se a identificação voltar.
   - **Sem parecer jurídico:** publicar sem identificar o controlador é um risco aceito pelo dono nesta decisão; nada
     aqui afirma conformidade com a LGPD.
   - **Aprovação do texto exato:** como no registro de 29/10, o dono decidiu a retirada, não a redação. A redação vai
     para a aprovação dele na revisão do PR que a introduz; o merge desse PR é a aprovação de publicação. As fontes
     já estão com `approved: true` porque uma versão ativa não aprovada fecharia o cadastro (503).
   - **Novo aceite:** a versão nova dos Termos abre o aviso de reaceite, que não bloqueia o uso (item 3 do registro
     de 29/10). Não há fluxo de aviso para a nova versão da Privacidade.
   - **Versões antigas:** ficam intactas no catálogo. Ver o item 2.

2. **Versões substituídas deixam de ser servidas ao público:** `GET /api/v1/legal-documents` devolve só as versões
   ativas, e as rotas exatas de versão substituída passam a responder como indisponíveis. Conteúdo e digests não
   mudam. O catálogo embutido continua com todas as versões, como evidência: um aceite antigo se comprova comparando
   versão, idioma e SHA-256 de `identity.consent_records` com o documento retido. Quem aceitou uma versão antiga deixa
   de ter link público para ler aquele texto no site. O texto segue no repositório público, no histórico e na imagem
   da aplicação.

SHA-256 de conteúdo da versão `2026-11-07`, fixados no teste `PublishedDocumentKeepsItsExactFingerprint`:

| Documento | SHA-256 |
|---|---|
| terms/pt-BR | `101bd6102a89bc32135e2cfda3f7c2bbbe739ca6e7e4a3d6024124348960e8e8` |
| terms/en-US | `b07c56cc2655f8625d5221521eaba32cfa874bd55fe6609ab66cec187f64d80e` |
| privacy/pt-BR | `aad9288cc724d5310430cea90dafb5c24cf097274e70a1e802265d9867e80b01` |
| privacy/en-US | `236e00727761937ec434356e6dcc11d6d1cf57a79a45c8830ee0ff7890eb58a7` |
