# Saúde do observador do Coolify

Este branch de infraestrutura é fixado por SHA no recurso Duora deployment. As imagens da aplicação continuam vindo da release aprovada da main; o observador usa o digest da edge dessa release.

O observador mantém sleep como processo principal, usuário 10001, filesystem somente leitura, 16 MiB e 16 PIDs, sem portas, volumes, Docker socket ou credenciais. Seu healthcheck verifica GET /health/ready no alias duora-web e HTTP no Caddy duora-edge-1 com Host meetduora.com. Isso exige o Caddyfile administrativo HTTP na porta 80 atrás do proxy do Coolify, já usado no runtime; o Caddyfile TLS embutido na imagem é sobreposto por esse bind administrativo.

A rede externa duora_edge é a rede privada existente do runtime. O Coolify acrescenta a rede isolada do recurso. O wrapper administrativo aceita apenas essas redes revisadas e o healthcheck literal, com intervalo de 15 s, timeout de 12 s, três tentativas e período inicial de 30 s. Não aceita healthcheck cosmético, comando arbitrário ou alias/endereço de rede informado pelo recurso.

A fonte scripts/release/coolify/observer_start.py é instalada administrativamente como /usr/local/sbin/duora-coolify-start, root:root 0755, após backup e comparação com a versão instalada. A configuração root-only fixa observer_healthcheck, observer_networks e a nova SOURCE_COMMIT. O arquivo não é instalado pelo CI nem recebe comandos da aplicação.

O executor mantém verificações de backup/WAL, schema e runtime. A etapa final do observador usa compose up --wait --wait-timeout 90; falha de saúde impede sucesso da publicação. O teste de saúde não substitui a verificação de identidade das imagens nem os controles do executor.

Testes de contrato: python3 -m unittest discover -s scripts/release/coolify -p test_observer_start.py -v. Os casos cobrem a sonda literal, espera por saúde e recusa de comandos, mounts, root e redes divergentes. A validação operacional deve confirmar Docker healthy e Coolify running:healthy, além de HTTPS público 200. Uma sonda descartável sem rede pode confirmar o estado unhealthy sem interromper serviços reais; remover apenas o container de teste criado para esse fim.
