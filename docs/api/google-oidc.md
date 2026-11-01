# Google OIDC — contratos API-A18 a API-A28

Implementação do [ADR-0046](../architecture/adr/0046-google-oidc-com-senha-local.md), desligada por padrão. Prefixo `/api/v1`. OIDC não altera os contratos locais A02/A03/A04/A13, exceto os pontos internos de composição transacional e descarte de intenção após troca de identidade.

| ID | Método e rota | Proteção e efeito |
|---|---|---|
| API-A18 | GET /auth/google/options | Flags públicas, sem dados de conta ou credenciais |
| API-A19 | POST /auth/google/requests | CSRF público; Login/Register; cria intenção e devolve URL interna fixa |
| API-A20 | GET /auth/google/authorize | Navegador ligado à intenção; consome início autorizado e desafia handler oficial; não abre sessão |
| API-A21 | GET /auth/google/flow | Estado mínimo da própria intenção/navegador e fase; expiração UTC Z; mudança de SID impede exposição |
| API-A22 | POST /auth/google/completions | CSRF ligado ao fluxo; conclui login por vínculo existente, com MFA Duora quando ativo; nova sessão |
| API-A23 | POST /auth/google/registrations | Prova externa, senha e recibos/declaração iguais ao cadastro local; 202 neutro; sem vínculo ativo |
| API-A24 | POST /auth/google/registration-confirmations | Código de oito dígitos + senha; verifica stamp e confirma/vincula no mesmo commit; sem login automático |
| API-A25 | GET /me/external-logins | Sessão/contexto privado; lista do titular sem sub/token |
| API-A26 | POST /me/external-logins/google/requests | Participante confirmado, prova local recente e MFA aplicável; cria intenção Link presa a SID/stamp |
| API-A27 | POST /me/external-logins/google/confirmations | Mesmo titular/SID/stamp/prova; confirmação explícita; vínculo, aviso e auditoria atômicos; renova sessão |
| API-A28 | DELETE /me/external-logins/google | Prova local e MFA aplicável; remove vínculo, troca stamp, revoga sessões/intenções e renova a corrente |

O callback de protocolo `GET /signin-google-oidc` fica fora da API: transporte exato code/query, proteção contra parâmetros ambíguos/implicit, correlação/state/nonce/PKCE e token validados antes de preparar prova. Sucesso ou falha válida retorna 303 para `/google/continue`; transporte inválido retorna 400. Rotas de signout do handler externo não são utilizadas. Nenhuma isenção ampla de CSRF foi criada.

DTOs e status detalhados estão no OpenAPI gerado. Campos desconhecidos são recusados; e-mail/sub/UUID alvo não podem selecionar uma conta existente. O contato vem do registro Duora; no cadastro novo, o endereço vem da prova externa validada. Nenhum token Google vai ao cliente. Datas públicas usam `DateTime` UTC para preservar o sufixo Z exigido pelo cliente.
