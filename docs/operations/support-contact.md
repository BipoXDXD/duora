# Contato de suporte

Decisão do responsável em 21/10/2026, issue #82: `support@meetduora.com`, atendimento PT-BR/EN-US,
proprietário como titular e cobertura declarada 24/7. O destino interno informado é seu Gmail pessoal;
o endereço privado não integra documentos públicos, logs ou configuração versionada.

O Zoho Mail Lite possui um alias Duora Support da caixa existente, com identidade From. Em 28/10/2026 o
encaminhamento automático para o Gmail do titular foi confirmado ativo no console admin do Zoho, com cópia
retida na caixa Zoho ("Excluir cópia" desmarcado). Prova ponta a ponta: mensagem enviada pelo Resend de
`no-reply@meetduora.com` para `support@meetduora.com` às 08:19 UTC chegou ao Zoho e foi encaminhada à caixa de
entrada do Gmail. Recebimento iniciado de outro provedor também chegou ao Zoho (08:12 UTC). Não registrar
códigos, credenciais ou mensagens pessoais em issues.

`Duora:SupportContact` no Web/Worker define o contato público citado nos avisos de segurança. O mesmo valor
pode ser sobrescrito pela configuração do ambiente; conferir ambos antes da release. Não é credencial,
remetente SMTP nem ativação do transporte transacional; #68 conserva essa homologação.

A cobertura 24/7 é a decisão declarada pelo titular, não uma medição de SLA ou escala já comprovada. O
titular atende sem substituto, por decisão expressa ([decisões de 24/10](../product/decisions-2026-10-24.md),
item 19). Severidades, tempos-alvo, evidência mínima, escalonamento e contingência do canal (spec 12.5) estão em
[support-incidents.md](support-incidents.md); a política DMARC do domínio, em [dmarc.md](dmarc.md). Não atribuir
pessoas fictícias.
