---
{
  "document": "privacy",
  "version": "draft-selfie-failures-304",
  "locale": "pt-BR",
  "controllerName": "Responsável pelo Duora",
  "privacyContact": "support@meetduora.com",
  "approved": false
}
---

# Aviso de privacidade

## Controlador e contato

O controlador dos dados pessoais tratados no Duora é o responsável pelo Duora. O canal para solicitações de privacidade e para o exercício de direitos é support@meetduora.com.

## Cadastro, autenticação e aceites

O cadastro processa e-mail e senha para criar e proteger a conta. A senha é transformada em hash pelo mecanismo de autenticação; não é uma informação pública de perfil. A confirmação utiliza o endereço informado. Os registros de aceite vinculam o tipo do documento, sua versão, o idioma efetivamente informado e o instante do aceite. Um registro histórico com idioma desconhecido permanece desconhecido.

## Perfil e preferências

Os recursos de perfil permitem dados e escolhas como nome de apresentação, biografia, país e subdivisão opcionais, idiomas de comunicação, idioma da interface e fuso. Essas escolhas são independentes; idioma não comprova país, residência ou proficiência. Localização aproximada, quando habilitada, é apenas sugestão corrigível e não autoriza geolocalização precisa nem histórico individual de detecção.

## Participação e visibilidade

Inscrições, salas e atividades usam os dados necessários ao respectivo contexto. Outra pessoa recebe somente a projeção autorizada para aquele recurso; não deve receber listas privadas de preferências ou escolhas ainda não reveladas. Fotografias e verificações de presença possuem etapas próprias. Uma verificação de presença ou qualidade não é verificação de idade nem autorização para coletar documento de identidade.

## Operação e fornecedores

O serviço e seus dados, inclusive fotografias, ficam em servidor contratado da Hostinger em Boston, Estados Unidos. Cópias de segurança são cifradas antes de sair do servidor e guardadas no Backblaze B2, na região leste dos Estados Unidos; esse fornecedor não recebe as chaves de cifra. Mensagens enviadas ao suporte são recebidas pelo Zoho Mail e encaminhadas a uma caixa Gmail do responsável. A verificação de presença por selfie é processada pela Amazon Web Services (AWS) na região us-east-1, nos Estados Unidos. Para quem participa a partir do Brasil, esses tratamentos ocorrem fora do país.

E-mails transacionais (confirmação de cadastro, redefinição de senha e avisos da conta) são enviados pela Resend (Resend, Inc., Estados Unidos), a partir do domínio meetduora.com, com envio pela região sa-east-1 (São Paulo). A Resend processa o endereço do destinatário e o conteúdo da mensagem apenas para entregá-la. O rastreamento de abertura e de clique está desligado.

## Verificação de presença por selfie

A verificação de presença por selfie usa o Amazon Rekognition Face Liveness, da AWS, na região us-east-1, sob o acordo de tratamento de dados padrão da AWS. Ela avalia presença e qualidade da captura; não verifica idade nem compara a sua identidade com a de outra pessoa. O Duora não guarda imagens ou vídeos da selfie, templates faciais nem pontuações do fornecedor.

O resultado aprovado, com o estado, a versão da política e as datas da verificação, fica guardado enquanto a conta existir. Os registros das tentativas não aprovadas são apagados pela rotina de limpeza após 30 dias da conclusão ou, se não houver conclusão, da expiração. A referência da sessão do fornecedor e o código local de feedback são apagados pela rotina de limpeza após 24 horas da expiração.

Separadamente, para aplicar os limites de reprovação e impedir que a exclusão das tentativas reinicie esses limites, conservamos um registro mínimo de cada reprovação contabilizada: o identificador da conta, o identificador da tentativa e o instante da reprovação. Esse registro não contém imagem, vídeo, pontuação, feedback nem motivo do fornecedor. Ele pode permanecer por mais de 30 dias e não expira apenas pelo decurso do tempo: fica enquanto a selfie não for aprovada. Os registros mínimos são apagados quando a selfie é aprovada ou quando um moderador revoga a suspensão por reprovações da selfie. A exclusão física da conta também os remove. A revogação de uma suspensão por outro motivo não apaga essa contagem.

## Limites de reprovação e suspensão automática

Contabilizamos somente resultados finais de reprovação por qualidade da captura ou presença inconclusiva, avaliados sob a mesma versão da política registrada na tentativa. Abandono, expiração, indisponibilidade do fornecedor, falha técnica e resultado sob outra versão de política não entram na contagem.

Após a quinta reprovação contabilizada, novas tentativas ficam pausadas por uma hora, contada do instante dessa quinta reprovação no servidor. Ao fim da pausa, há um segundo ciclo de até cinco novas reprovações. A espera não zera a contagem. Os limites adicionais de cinco pedidos por hora e intervalo mínimo de um minuto entre pedidos continuam aplicáveis.

Na décima reprovação contabilizada, novas tentativas são recusadas e o sistema solicita automaticamente a suspensão da conta por reprovações da selfie, sem decisão humana prévia. A suspensão é processada em seguida e pode não ser imediata. Se já houver suspensão por outro motivo, o pedido aguarda essa medida terminar e só prossegue se a contagem continuar esgotada. Enquanto a conta estiver suspensa, você não poderá participar de eventos, salas ou conversas. Essa suspensão não tem prazo de expiração e depende de revogação por moderador para ser desfeita.

## Solicitação de revisão humana

Você pode solicitar a revisão humana da suspensão automática por reprovações da selfie escrevendo para support@meetduora.com, inclusive se não conseguir acessar a conta. O pedido de revisão dessa medida ainda não está disponível pelo aplicativo. Antes de atender, pediremos uma verificação de identidade proporcional ao pedido.

Um moderador pode revogar a suspensão após a revisão. Nesse caso, os registros mínimos de reprovação são apagados e a conta volta ao primeiro ciclo de tentativas. A revogação não equivale à aprovação da selfie: os demais requisitos de participação continuam aplicáveis. Solicitar a revisão, por si só, não remove a suspensão nem reinicia a contagem.

## Conservação e direitos

Os dados são conservados pelo tempo necessário à finalidade de cada recurso. Cópias de segurança seguem ciclo próprio; um dado apagado do serviço pode permanecer nelas até que a cópia expire. Para pedir acesso, correção ou exclusão dos seus dados, escreva para support@meetduora.com; antes de atender, pediremos uma verificação de identidade proporcional ao pedido.

## Alterações deste aviso

Mudanças neste aviso geram uma nova versão, identificada por data e idioma. As versões anteriores continuam acessíveis no endereço próprio de cada versão.
