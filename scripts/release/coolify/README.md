# Publicação automática pelo Coolify

Depois da CI bem-sucedida na main, o workflow Coolify release confere o commit exato e todos os gates. Imagens são transportadas em OCI por SSH, com digest e plataforma conferidos, para uma entrada restrita ao projeto. A VPS publica no recurso existente do Coolify e só registra sucesso após saúde real. Não há runner de PR nem token GitHub na VPS.

A chave COOLIFY_RELEASE_SSH_KEY e o known_hosts COOLIFY_RELEASE_KNOWN_HOSTS ficam no environment production, restrito à main. A variável COOLIFY_RELEASE_TARGET identifica a conta restrita. O token Coolify é local, root-only, com API limitada a IPs locais. Chaves de backup continuam separadas.

A configuração de containers permanece fixada no commit de infraestrutura revisado; releases só atualizam imagens. Volumes, bancos e assets não são removidos. preserved-assets.json bloqueia remoção ou alteração das mídias originais. Imagens antigas são retidas. Não há downgrade automático do schema; falha exige inspeção do deployment e do estado pending.json na VPS. Para rollback, verificar compatibilidade de schema e backup antes de publicar uma release anterior pelo procedimento administrativo.

Receptor: /opt/coolify-release/receiver.py; configuração /etc/coolify-release/<projeto>.json; recibos /var/lib/coolify-release/<projeto>/. Alterações do receptor no Git não atualizam automaticamente o executável root da VPS: exigem revisão e instalação administrativa.

O receptor limita o transporte OCI a 4 GiB e a expansão agregada das camadas a 16 GiB.
A trava comum da VPS serializa os receptores e intents duráveis bloqueiam novas releases após falhas.
O inventário administrativo de armazenamento e o protocolo v2 com hash exato são obrigatórios.
Instalação coordenada, reconciliação, limites e testes estão em [ISOLATION.md](ISOLATION.md).

## Eventos, fila e repetições

No Duora, a publicação automática recebe `workflow_run` de CI concluída com sucesso na main.
O SHA vem do evento, mas o gate consulta a API e exige novamente o push da main deste repositório,
com os nove jobs aprovados na tentativa atual. Eventos de PR, fork, CI incompleta ou com falha
não liberam imagens nem segredos de produção. A execução manual continua aceitando o SHA completo.

A fila de produção é separada dos checks de PR, mantém as execuções pendentes (`queue: max`)
e não cancela a execução em andamento. Antes das imagens e novamente antes do envio, um SHA já
superado termina com `eligible=false`, sem publicação e sem transformar essa situação em falha.
A conclusão bem-sucedida da CI do novo SHA inicia sua própria execução. Cancelamentos explícitos
por proprietário/API e o limite de 100 pendências do GitHub continuam possíveis.

Repetir apenas jobs com falha reutiliza o artefato AMD64 mais recente e não expirado do mesmo
workflow run. O manifesto continua sujeito à validação de SHA, tentativa de CI, digests e plataforma;
um artefato de outro run ou um nome semelhante não qualifica.

## Atualizações do Coolify

`observer.py` é a fonte versionada de `/usr/local/sbin/duora-coolify-start`; sua instalação é
administrativa, atômica, sob trava, com backup e validação do Compose efetivamente gerado.
O arquivo não contém credenciais. Alterações de Git não substituem automaticamente o executável root.

Apenas `coolify.version` pode acompanhar uma atualização estável, sem downgrade, no mesmo major,
e precisa coincidir com a tag estável oficial do container Coolify em execução. Não há atualização
silenciosa da configuração revisada. Todas as outras etiquetas, identidade da aplicação, roteamento
desligado, redes, probes, ambiente e restrições do observador continuam com comparação exata.
Major diferente, prerelease, imagem sem versão ou qualquer outra mudança inesperada exige revisão.
O mecanismo não remove `pending.json`, não resolve uma falha de implantação e não altera assets.

A documentação atual do GitHub inclui `queue: max`. O actionlint 1.7.12 ainda não reconhece essa
chave; a aceitação do workflow e seus checks no GitHub verificam o parser real. Não ignorar outros
erros de validação nem retirar gates para contornar limitações do linter.
