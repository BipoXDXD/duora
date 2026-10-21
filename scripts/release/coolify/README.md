# Publicação automática pelo Coolify

Depois da CI bem-sucedida na main, o workflow Coolify release confere o commit exato e todos os gates. Imagens são transportadas em OCI por SSH, com digest e plataforma conferidos, para uma entrada restrita ao projeto. A VPS publica no recurso existente do Coolify e só registra sucesso após saúde real. Não há runner de PR nem token GitHub na VPS.

A chave COOLIFY_RELEASE_SSH_KEY e o known_hosts COOLIFY_RELEASE_KNOWN_HOSTS ficam no environment production, restrito à main. A variável COOLIFY_RELEASE_TARGET identifica a conta restrita. O token Coolify é local, root-only, com API limitada a IPs locais. Chaves de backup continuam separadas.

A configuração de containers permanece fixada no commit de infraestrutura revisado; releases só atualizam imagens. Volumes, bancos e assets não são removidos. preserved-assets.json bloqueia remoção ou alteração das mídias originais. Imagens antigas são retidas. Não há downgrade automático do schema; falha exige inspeção do deployment e do estado pending.json na VPS. Para rollback, verificar compatibilidade de schema e backup antes de publicar uma release anterior pelo procedimento administrativo.

Receptor: /opt/coolify-release/receiver.py; configuração /etc/coolify-release/<projeto>.json; recibos /var/lib/coolify-release/<projeto>/. Alterações do receptor no Git não atualizam automaticamente o executável root da VPS: exigem revisão e instalação administrativa.

O receptor limita o transporte OCI a 4 GiB e a expansão agregada das camadas a 16 GiB.
A trava comum da VPS serializa os receptores e intents duráveis bloqueiam novas releases após falhas.
O inventário administrativo de armazenamento e o protocolo v2 com hash exato são obrigatórios.
Instalação coordenada, reconciliação, limites e testes estão em [ISOLATION.md](ISOLATION.md).
