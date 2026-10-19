# ADR-0029: Código de e-mail e verificação etária por selfie sem documentos

- **Status:** Proposed (2026-10-19); requisitos de produto aprovados pelo responsável na issue #93, implementação sujeita à revisão.
- **Origem:** #93, spec 4.1/8.2/12.2, API-A02/A03/P05/P06, INV-01/05; ADR-0019 e ADR-0024.
- **Revisar quando:** fornecedor real escolhido, evidência de fraude/atrito, ou aplicativo móvel aprovado.

## Decisão de produto

Confirmar posse do e-mail por código e avaliar elegibilidade etária por selfie com posicionamento orientado e qualidade suficiente. Nunca exigir documento, nem como fallback. SMS é apenas hipótese futura para um app; não entra nesta entrega. Boa qualidade de captura não comprova maioridade. Permanecem resultado autenticado do fornecedor, conta pendente em indisponibilidade e suspeita de menor restritiva.

## Implementação proposta

- Novos cadastros recebem oito dígitos aleatórios (`RandomNumberGenerator.GetInt32`), inclusive zeros iniciais, com validade de 15 minutos a partir do cadastro. É uma escolha técnica inicial, não um requisito irreversível de produto.
- Código e senha do cadastro são exigidos em API-A03. O campo `token` do contrato existente transporta o código; a URL tem apenas o marcador `token=code`, nunca o código. Não há confirmação automática nem login automático.
- O hash lento e salgado do código usa `PasswordHasher`, vinculado ao security stamp em `identity.user_tokens`, sem migração. O registro é removido na mesma transação que confirma a conta. O código aparece no payload privado da outbox para entrega, sujeito à limpeza existente após conclusão; nunca em logs ou URLs. Entrega expira após 10 minutos para evitar mensagens já vencidas.
- Conta nova, pendente e confirmada executam os mesmos dois hashes e os mesmos comandos de cadastro. Conta confirmada não recebe novo código. Mantêm-se rate limits, senha, teto de cinco falhas e troca de stamp; verificação, contagem e consumo rodam sob a trava de conta existente, com releitura antes da prova, para serializar tentativas paralelas e recadastros. A contagem também exige o stamp lido.
- Repetir cadastro é o caminho explícito para obter novo código, com os limites existentes de API-A02. Troca senha, aceites e stamp do pendente e invalida a prova antiga. Não existe botão de reenvio sem autenticação nem novo endpoint nesta fatia.
- Leitor tolerante aceita tokens Data Protection dos cadastros legados sem registro de código. Cadastro que já usa código não aceita token legado como atalho.
- A SPA oferece `/conta/verificacao-etaria`, alinhada ao retorno do dispatcher, com instruções em pt-BR/en-US, início por gesto, estado, revisão e continuação pelo fluxo HTTPS autorizado pelo servidor. Sem captura local, upload de documento, telefone ou resultado etário informado pelo cliente.
- `AgeCheckStart` explicita `RequiresSelfie=true` e `DocumentsAllowed=false`. São obrigações do adaptador; não demonstram que um fornecedor externo as cumpre.

## Limite da entrega

Só existem os providers `None` e `Fake`; o Fake continua proibido em produção. Esta fatia NÃO implementa captura, qualidade, prova de presença ou estimativa etária real. Para concluir #93 é necessário selecionar e integrar fornecedor com fluxo de selfie sem fallback documental, calibrar enquadramento/nitidez/iluminação e acessibilidade, validar ataques de apresentação e definir retenção. A orientação da SPA não substitui essa validação. Nenhuma pessoa deve ser aprovada apenas por enviar uma foto. O fornecedor não foi contratado nem habilitado por este PR.

## Alternativas e consequências

Link secreto como único passo não atende à decisão de código digitável. Remover a senha do cadastro reabriria o pré-sequestro de conta; ela permanece. Uma selfie em armazenamento local sem destinatário definido criaria coleta sem finalidade operacional, por isso a captura fica com o futuro fornecedor. O código curto reduz esforço de digitação, mas exige expiração e limite de tentativas. O hash adicional aumenta custo do cadastro igualmente nos três caminhos.

## Entrega e rollback

Publicar SPA, web e worker da mesma release, conforme o modelo existente. Worker antigo pode apresentar o código no link; web antiga não valida códigos. Para rollback, pausar cadastros durante a troca coordenada e orientar novo cadastro de pendentes afetados; nunca remover a exigência de confirmação para contornar incompatibilidade. Tokens legados permanecem legíveis pela nova release. Sem alteração de DTO, migração ou segredo. API-A03 documenta 503 quando a espera pela trava excede o teto, como as demais operações de credencial.

## Verificação

Testes de código cobrem prazo exato, hash persistido e remoção, replay, conteúdo dos e-mails sem código na URL e igualdade de trabalho do cadastro. Suíte existente continua cobrindo senha, recadastro, teto paralelo e corrida com confirmação. Testes da SPA cobrem digitação/colagem, ausência de envio automático, CSRF, orientação sem documentos, indisponibilidade sem aprovação e URLs inseguras. Testes do fornecedor real e qualidade da selfie permanecem pendentes; não são simulados como evidência de produção.
