# DMARC de `meetduora.com`

Runbook da política DMARC do domínio (spec 16.4: SPF, DKIM e DMARC conforme o provedor; issue #82). A mudança
é feita pelo operador no DNS do Cloudflare. Este documento não altera DNS, código nem configuração da aplicação.

## Estado publicado (consulta pública de 28/10/2026)

Conferido com `dig +short <nome> TXT @1.1.1.1` e `dig +short meetduora.com MX @1.1.1.1`:

| Nome | Tipo | Valor | Papel |
|---|---|---|---|
| `meetduora.com` | MX | `mx.zoho.com` (10), `mx2.zoho.com` (20), `mx3.zoho.com` (50) | Recebimento, inclusive de `support@` |
| `meetduora.com` | TXT | `v=spf1 include:zohomail.com ~all` | SPF do envio pelo Zoho (respostas de `support@`) |
| `zmail._domainkey.meetduora.com` | TXT | chave DKIM `v=DKIM1; k=rsa; p=…` | DKIM do Zoho |
| `resend._domainkey.meetduora.com` | TXT | chave DKIM `p=…` | DKIM do Resend (`no-reply@`) |
| `send.meetduora.com` | MX e TXT | `feedback-smtp.sa-east-1.amazonses.com`; `v=spf1 include:amazonses.com ~all` | Return-path do Resend; SPF alinha em modo relaxado |
| `_dmarc.meetduora.com` | TXT | `v=DMARC1; p=none` (TTL 300) | Política sem ação e **sem relatórios** |

Com `p=none` e sem `rua`, nenhum receptor aplica política e ninguém recebe relatório: não há como saber se uma
fonte legítima falha nem se o domínio está sendo falsificado.

O DNS mostra as chaves; não prova que cada fonte assina com elas. A assinatura DKIM do Zoho com
`d=meetduora.com` precisa aparecer nos relatórios antes de endurecer a política (critério C3 abaixo).

## Fase 1: registro a publicar agora

Substituir o TXT de `_dmarc.meetduora.com` por:

```text
v=DMARC1; p=none; rua=mailto:support@meetduora.com
```

- `p=none`: continua sem efeito na entrega; a fase só liga a observação.
- `rua=mailto:support@meetduora.com`: relatórios agregados para um endereço que existe hoje e é lido (alias
  da caixa Zoho, com encaminhamento ao titular). Como o destino está no mesmo domínio, não precisa do registro
  de autorização de destino externo.
- Sem `ruf`: relatórios de falha podem trazer cabeçalhos e trechos de mensagens; não são necessários.
- Alinhamento relaxado (padrão, sem `adkim`/`aspf`): o Resend usa `send.meetduora.com` como return-path, que só
  alinha com `meetduora.com` em modo relaxado.
- Sem `pct`: com o volume do piloto, aplicar a política a uma fração das mensagens não gera amostra útil.
- Sem `sp`: subdomínios herdam `p`. Nenhum subdomínio envia com `From` próprio hoje.

No Cloudflare: DNS → Records → editar o TXT `_dmarc`, conteúdo acima, TTL Auto (300 s), sem proxy. Conferir com
`dig +short _dmarc.meetduora.com TXT @1.1.1.1` e registrar data e hora em UTC na issue #82.

**Custo do `rua` em `support@`.** Os relatórios chegam como anexos XML compactados na caixa de atendimento e são
encaminhados ao Gmail. Não são casos de atendimento: criar no Zoho um filtro que mova para a pasta `DMARC` as
mensagens com assunto contendo `Report Domain: meetduora.com` e não registrá-las em
[support-incidents.md](support-incidents.md). Os relatórios trazem IPs de servidores remetentes e contagens; não
anexá-los a issues, porque o repositório é público.

## Como ler os relatórios

Abrir o XML localmente (sem enviar a serviço de terceiros). Para cada `<record>`, anotar `source_ip`, `count`,
`policy_evaluated/dkim`, `policy_evaluated/spf`, `identifiers/header_from` e, em `auth_results`, o domínio `d=`
do DKIM e o domínio do SPF. Classificar cada IP:

| Fonte | Como reconhecer | Resultado esperado |
|---|---|---|
| Resend (Amazon SES) | `auth_results` com DKIM `d=meetduora.com` e SPF em `send.meetduora.com` | DMARC `pass` por DKIM e por SPF |
| Zoho | IP do Zoho (reverso normalmente em `zoho.com`), SPF `meetduora.com` | DMARC `pass` por SPF; também por DKIM se o Zoho assinar |
| Encaminhamento de terceiros | IP de provedor que repassa mensagem legítima | Pode falhar SPF; DKIM costuma sobreviver |
| Desconhecida | Nenhuma das anteriores | Falsificação ou envio esquecido; investigar |

## Critério para passar a `p=quarantine`

Avançar só quando **todos** valerem, conferidos nos relatórios de uma janela de **14 dias corridos** contados a
partir do primeiro relatório recebido:

- **C1.** Relatórios de pelo menos 2 receptores distintos (por exemplo, Google e Microsoft).
- **C2.** As duas fontes legítimas aparecem: Resend (mensagens do produto) e Zoho (respostas de `support@`).
  Se uma não aparecer por falta de tráfego, enviar mensagens de teste para contas externas autorizadas e
  esperar o relatório seguinte.
- **C3.** 100% das mensagens das fontes legítimas com DMARC `pass`. Para o Zoho, conferir também DKIM alinhado
  (`d=meetduora.com`); se só passar por SPF, ativar a assinatura DKIM no console do Zoho e reiniciar a janela.
- **C4.** Toda fonte com falha está classificada: legítima corrigida (e a janela reinicia) ou ilegítima.
- **C5.** Nenhuma mudança de provedor de e-mail, de return-path ou de DNS de e-mail prevista para os próximos
  30 dias.

Registrar a conferência na issue em números agregados (receptores, fontes, mensagens, percentual de `pass`),
sem IPs nem conteúdo.

## Fase 2: registro de quarentena

```text
v=DMARC1; p=quarantine; rua=mailto:support@meetduora.com
```

Depois de publicar, acompanhar os relatórios por 30 dias corridos e repetir o teste do canal de
[support-incidents.md](support-incidents.md) mais um envio do produto pelo Resend para conta externa autorizada,
conferindo que nenhum dos dois cai em spam.

**Rollback:** voltar ao registro da fase 1 se uma fonte legítima aparecer com DMARC `fail`, se um teste
legítimo cair em spam ou se um provedor de envio mudar. Com TTL de 300 s, o efeito é rápido nos resolvedores que
respeitam o TTL. Registrar a causa e reiniciar a fase 1 a partir da correção.

## Fora do escopo

- `p=reject`: fica para decisão própria, depois de pelo menos 30 dias em `quarantine` sem falha legítima.
- Mudanças de SPF ou DKIM: este runbook só observa; se os relatórios pedirem correção, ela entra em PR e
  registro próprios.
- Remetente transacional e entrega: ver [resend.md](resend.md).
