# ADR-0046 — Google OIDC com senha local e associação explícita

Status: política aprovada pelo dono em01/11/2026 (“Manter senha Duora inicialmente”); implementação local sujeita a testes, revisão humana e gates antes de integração/ativação. Origem: item Google da issue276. Substitui somente o trecho do ADR0005 que excluía coexistência de login local e externo; preserva cookie, Identity e ticket PostgreSQL.

## Decisão

Um único provedor Google, cliente web confidencial, handler oficial ASP.NET Core OpenIdConnect10.0.13 (MIT), Authorization Code com PKCE S256, state/correlação e nonce. JWT com assinatura/JWKS, issuer permitido, audience/azp, validade/iat e tipos de claims validados. Endpoints e autoridade fixos; redirect URI sai da PublicOrigin validada. Só openid/email, sem refresh token, UserInfo ou tokens no navegador/persistência.

Identidade externa usa issuer canônico Google+sub opaco, representados por login_provider=google-oidc imutavelmente associado ao emissor e provider_key=sub em identity.user_logins. E-mail nunca associa contas automaticamente. Um Google pertence a uma conta Duora e cada conta tem no máximo um Google, com constraints PostgreSQL. Não há fusão de contas.

Cadastro Google retém senha Duora, recibos exatos de documentos ativos, declaração adulta e código Duora. Só cria vínculo na transação que confirma código+senha, preso ao stamp do cadastro criado/substituído pelo próprio fluxo. Recadastro invalida a intenção anterior; conta confirmada não muda e resposta de cadastro continua neutra. Confirmação não abre sessão automática.

Vínculo existente exige sessão de participante, e-mail confirmado, senha recente (janela vigente1h inclusiva), TOTP quando ativo, prova Google, SID/stamp iguais no retorno e confirmação explícita. Privilegiados mantêm somente login local; claims Google nunca concedem papel ou satisfazem MFA. Google login com MFA ativo exige fator Duora antes de criar sessão. Google login não preenche reauthenticated_at: ações sensíveis continuam exigindo senha/TOTP via A13.

Sessão usa os handlers/ticket store atuais, nova chave e revogação anterior, expiração ociosa/absoluta preservadas. Novo authentication_method identifica LocalPassword/GoogleOidc sem funcionar como autorização. Unlink exige prova local, muda stamp, revoga sessões/intents e renova a corrente sem estender limite ou provas; aviso e auditoria no mesmo commit. Reset comum mantém vínculo, MFA e contato, invalidando sessões/intents por stamp; diante de invasão, orientar entrar localmente, remover Google e revogar sessões. Revogação no Google não encerra automaticamente tickets Duora existentes.

Intenções duráveis por navegador, referências CSPRNG256bits por hash, prova mínima protegida por Data Protection, prazo5min e transições condicionais. Prova não autoriza API. Rede Google fora de locks; conta→intenção→vínculo→sessões→auditoria/outbox é a ordem Identity. Unique violation reverte a transação. Worker limpa vencidos independentemente de flags/novos desafios, sem depender de runtime/protetor Web. Prazo não é prorrogado por restart ou reenvio do código.

Callback exato /signin-google-oidc é consumido pelo handler na autenticação, antes do middleware de CSRF. Somente protocolo válido pode preparar prova; retorno303 para URL fixa limpa. Todo efeito de conta/sessão exige POST com CSRF; vínculo usa o contexto privado vigente. GETauthorize apenas inicia desafio previamente autorizado por POST, GETflow apenas mostra estado transitório. Não há isenção global de /auth, CORS aberto ou login por GET.

## Padrão desligado e operação

GoogleOidc.Enabled, RegistrationEnabled e LinkingEnabled são false por padrão; nenhum segredo real foi configurado. Ativação incompleta falha; modo off/OpenAPI não exige credenciais nem registra handler externo. Privacidade, credenciais/console de homologação, redaction de query do callback no proxy/tracing e homologação do fornecedor são gates antes de habilitar. Não ampliar CSP para SDK Google: navegação inteira dispensa script externo.

Rollback operacional: desligar desafios/cadastro/vínculo, manter esquema expandido e login local. Em incidente, revogar sessões com authentication_method=GoogleOidc e intenções; não aplicar Down nem reativar vínculos em restore. Exclusão por FK remove vínculos/intents; restore continua sujeito à reconciliação de revogações/tombstones (INV11), sem alegar que toda recuperação operacional do produto está implementada.

## Aceite e limites

INV01/05/06/11/12; T01/06/13/14/15/21. Testes devem exercitar handler real com discovery/JWKS/token endpoint sintéticos, PostgreSQL18.6 com papéis reais, ausência de auto-link por e-mail, pré-cadastro/recadastro, token/state/nonce/PKCE, MFA, troca de SID, unicidade concorrente e rollback de aviso/vínculo. Componentes/E2E sintéticos não equivalem à console Google nem à homologação com conta real. Código é candidato local, sem PR/push/merge/deploy; evidências e lacunas ficam no relatório de entrega.

## Revisão da recuperação local

Listar e remover um vínculo existente são gestão de credencial Duora, independentes de Enabled/LinkingEnabled e de rede/credenciais Google. Permanecem disponíveis ao titular suspenso e a quem passou de participante a papel privilegiado, com sessão/contexto privado e, na remoção, senha utilizável e recente, stamp atual e TOTP recente quando há MFA. Isso não autoriza nova associação nem login Google para privilegiados ou suspensos fora dos controles vigentes. A tela da conta suspensa expõe a gestão; apenas o botão de novo vínculo depende das flags e do papel de participante.
