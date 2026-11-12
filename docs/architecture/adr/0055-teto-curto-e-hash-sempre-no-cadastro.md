# ADR-0055: Teto curto de trava e hash sempre no cadastro, contra o canal de tempo (TM-06)

- **Status:** Accepted (2026-11-12), aceito pelo dono em 12/11. Proposto em 2026-11-10 por decisão da coordenação,
  10/11: (iv-a). Toca anti-enumeração: veio com a revisão de segurança e a revisão humana no merge.
- **Origem:** issue #466 (risco residual do #464/#461); TM-06; medição no comentário da #466
- **Relaciona:** [ADR-0009](0009-uma-instancia-web-no-piloto.md) (uma instância web); a seção de concorrência na
  autenticação de [concurrency.md](../../data/concurrency.md#concorrência-na-autenticação-issue-65)
- **Revisar quando:** o cadastro desistir com frequência em produção (o Warning do log), entrar uma segunda réplica
  web (ADR-0009: o rate limit de cadastro é por instância, e com N réplicas um atacante soma N vezes mais amostras,
  o que derruba o argumento de que não dá para somá-las), o cadastro pelo Google mudar de transação, ou uma medida em
  produção mostrar resíduo acima do ruído

## Contexto

Desde o #464, o cadastro de uma conta cuja linha está travada por outra operação (login, confirmação, reset) desiste no
teto de espera e responde o mesmo `202` de qualquer cadastro. Corpo e headers não dizem nada; o tempo dizia. O teto era o
do login (`Identity:AccountLockTimeout`, 5 s em produção), então a conta travada respondia ~5 s mais devagar que um
e-mail novo. A medição da #466 achou também um segundo canal: com o orçamento de emissão esgotado (3 por hora, que o
próprio atacante consegue gastar), o cadastro pulava o hash do código e respondia ~38 ms mais rápido.

O cadastro normal custa ~72 ms, quase todo em dois PBKDF2 (senha e código, ~30 ms cada); o SQL soma ~7 ms.

Alternativas consideradas (custo medido ou estimado na #466):

- **Piso fixo até o teto do login.** Custaria ~5 s em todo cadastro em produção e ainda vazaria o que passasse do piso.
- **Jitter.** Ruído uniforme some na média, e 5 s de diferença se veem numa amostra só.
- **Trabalho fora da requisição (fila).** Fecharia o canal por construção, mas pede tabela de jobs, worker, idempotência,
  senha em repouso na fila, e não cabe no cadastro pelo Google, que é síncrono na transação. Recusada por enquanto.
- **Piso pequeno (~15 ms) sobre o teto curto.** Só se a medida mostrar resíduo acima do ruído (veja Consequências).

## Decisão

Vamos dar ao cadastro um teto de trava próprio e curto e fazer os dois hashes em todos os caminhos:

- `Identity:RegistrationLockTimeout`, padrão **5 ms**, aceito de 1 ms a 50 ms e validado no boot (`ValidateOnStart`),
  separado do `Identity:AccountLockTimeout` do login. Abaixo de 1 ms o `lock_timeout` viraria `0`, que no PostgreSQL
  desliga o teto; acima de 50 ms a espera volta a aparecer no tempo (o teto de 100 ms medido somou +78 ms). `NOWAIT` não
  existe para `INSERT ... ON CONFLICT`, por isso o teto é um `lock_timeout` de poucos ms.
- O código de confirmação e o hash dele saem antes da transação. A desistência pela trava e o orçamento esgotado pagam
  os mesmos dois hashes que o cadastro que emite.
- A desistência continua respondendo o `202` idêntico, com o Warning de log sem e-mail nem id.

5 ms é o menor valor que ainda tolera uma trava de linha momentânea (um upsert concorrente leva ~1 ms) e fica abaixo do
desvio de uma amostra do cadastro (~2–4 ms neste Mac). O cadastro normal não paga nada a mais: sem disputa, a trava sai
na hora e o teto não entra em jogo.

## Consequências

- Positivas: a diferença da conta travada cai de ~teto (5 s) para poucos ms, e a do orçamento esgotado, de ~38 ms para
  ~4 ms; o cadastro normal custa o mesmo; a desistência libera a conexão em ms, não em 5 s.
- Negativas: um cadastro legítimo que coincide com login, confirmação ou reset da mesma conta, ou com outro cadastro do
  mesmo e-mail, desiste mais vezes. Recebe o `202`, não recebe e-mail e precisa tentar de novo. A frequência disso em
  produção não foi medida; o Warning `Registration gave up waiting for the account row lock` é o sinal.
- Trade-off: sobra um resíduo de poucos ms, que não é tempo constante. Medido (mediana das diferenças pareadas, N = 31,
  PBKDF2 real, Mac local): conta travada +2,5 ms, orçamento esgotado −3,8 a −4,5 ms, contra um desvio por amostra de
  2,2–3,9 ms. Fica na ordem do ruído de uma amostra local, e abaixo do jitter de rede de produção; com o rate limit de
  3 cadastros por hora por conta e a trava durando só a janela de outra operação, não dá para somar amostras. Isso vale
  com uma instância web: o limitador é por instância, e uma segunda réplica (ADR-0009) multiplica as amostras (veja
  "Revisar quando"). Por isso o
  piso de ~15 ms não entrou. Se uma medida em produção mostrar resíduo acima do ruído, o piso é a próxima etapa.

## Segurança e operação

Nenhuma superfície nova, segredo ou dado pessoal. O contrato HTTP não muda. Configuração nova opcional:
`Identity__RegistrationLockTimeout` (formato `TimeSpan`, ex.: `00:00:00.005`); fora da faixa, o web não sobe. Com mais de
uma réplica web (ADR-0009), o teto continua por transação no PostgreSQL e vale igual em cada uma.

## Compliance

- `RegistrationTimingTests` mede o tempo do cadastro para e-mail novo, conta travada e orçamento esgotado, com hash de
  custo fixo, e falha se a mediana das diferenças pareadas passar de ±25 ms. Na base respondia +262 ms e −46,6 ms; sob loadavg ~30 o orçamento
  esgotado chegou a −12,2 ms, e as sabotagens deram −53 ms e +294 ms.
- `StartupConfigurationTests` cobre a faixa (0, 0,9 ms, 51 ms e 5 s recusados; 1 ms e 50 ms aceitos) e o padrão de 5 ms
  separado do teto do login.
- `RegistrationLockTimeoutTests` cobre a desistência: mesmo `202`, nada gravado, Warning sem PII.
