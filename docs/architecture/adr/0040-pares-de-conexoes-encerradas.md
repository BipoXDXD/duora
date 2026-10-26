# ADR-0040 — Não reencontrar conexões encerradas no MVP

- **Status:** Accepted (2026-10-24), decisão de produto sob delegação expressa do titular.
- **Origem:** #9, item6; spec5.1/7.1; propostaD10 do modelo.
- **Revisar quando:** for aprovada uma jornada explícita de reconexão, com consentimento bilateral novo e compatibilidade de unicidades.

## Contexto

O modelo previsto torna único o par de uma conexão, inclusive encerrada. Permitir novo pareamento desse par expõe quem encerrou e pode terminar numa decisão positiva incapaz de criar conexão. Reconexão está fora do MVP. A main1d713 não contém ainda persistência `social.connections`; não confundir o desenho documentado com módulo executável.

## Decisão

Pares com conexão Ended ou Blocked não são elegíveis para novos pareamentos em qualquer evento. A restrição é bilateral e não revela autor/motivo do encerramento. A implementação futura deve ler esse conjunto autoritativo sob as mesmas guardas do par, antes da seleção e da reserva, e revalidá-lo quando encerrar conexão concorrer com matching. Não remover a unicidade para contornar o requisito.

O filtro complementa bloqueios e não repetição no evento; não muda a versão de uma sala já iniciada nem desfaz reserva implicitamente. A abertura do estado de conexão deve usar uma operação própria e consentimento novo se for introduzida no futuro.

## Alternativas

- Permitir reencontro sem reconexão: recusado pelo risco de exposição e pelo desfecho incompatível com a unicidade.
- Tratar encerramento como bloqueio global do usuário: recusado; o efeito pertence ao par, sem penalizar sua participação com outras pessoas.

## Segurança e operação

Nenhuma coleta nova, campo de perfil, inferência ou motivo individual em telemetria. Sem migração/SQL/produtivo neste registro. Uma decisão aceita não cria a tabela nem comprova a integração de Connections. A exclusão deve permanecer após restart e em eventos distintos.

## Compliance

Ao implementar Connections: testes PostgreSQL de Ended/Blocked entre eventos, controle positivo de outros pares, disputa EndConnection×reserva sob guardas e nenhuma projeção do motivo; INV-02/03/05/06. O aceite de produto está concluído; execução desses testes e código do módulo não são afirmados por este ADR.
