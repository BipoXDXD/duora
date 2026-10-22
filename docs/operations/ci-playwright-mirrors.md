# Mirror APT das dependências do Playwright

O job E2E usa Ubuntu24.04 hospedado pelo GitHub e instala os mesmos navegadores
Chromium/WebKit com `playwright install --with-deps`. Antes da instalação,
prefere archive.ubuntu.com por HTTPS na lista de mirrors fornecida pela imagem
do runner. security.ubuntu.com e o mirror Azure permanecem como alternativas.
As fontes Ubuntu, suítes, componentes, chaves e a verificação de assinaturas do
APT permanecem iguais. Isso só afeta o runner descartável do job E2E.

Motivo observado: no job119981473045 da CI39823612355, imagem
ubuntu24/20261018.339, o APT leu `/etc/apt/apt-mirrors.txt` e permaneceu
baixando pacotes pequenos do azure.archive.ubuntu.com por quase25minutos.
O E2E não chegou a executar. A anotação comprova que o cancelamento final foi
automático por concorrência após outro merge; não se atribui esse cancelamento
a timeout. A demora no download é observada independentemente do cancelamento.
Na CI39812318309, o mesmo transporte já havia consumido mais de24minutos;
essa execução terminou por limite de25minutos.

O projeto runner-images configura originalmente Azure com prioridade1,
archive.ubuntu.com com prioridade2 e security.ubuntu.com com prioridade3.
A CI do Duora apenas ordena os mesmos três destinos como archive1/security2/Azure3.
Não altera a instalação dos navegadores, cache, timeouts, testes, gates,
permissões, publicação ou regras automáticas de concorrência.

Verificação: acompanhar a nova execução completa da CI, confirmar o destino
APT e a conclusão da instalação, execução de Chromium/WebKit e os nove jobs
aprovados no SHA final. A mudança não comprova confiabilidade permanente da rede.
Não repetir ou cancelar administrativamente execuções anteriores para validá-la.

Fontes primárias: [configuração dos mirrors no runner-images](https://github.com/actions/runner-images/blob/main/images/ubuntu/scripts/build/configure-apt-sources.sh)
e [prioridades do transporte APT](https://manpages.ubuntu.com/manpages/noble/man1/apt-transport-mirror.1.html).
