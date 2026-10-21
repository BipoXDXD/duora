# Tabelas de plataforma no bundle do navegador

O SDK de captura publica tabelas de strings como funções que inicializam objetos locais. Esse formato
impede o empacotador de descartar tabelas inteiras que nenhum consumidor utiliza.

`frontend/config/platform-enums.ts` transforma somente as declarações literais do módulo
`@aws-amplify/core/dist/esm/Platform/types.mjs` em objetos equivalentes. Não seleciona membros ou
categorias: quando uma tabela é usada, todos os seus membros permanecem. O empacotador pode eliminar as
tabelas sem consumidores. Arquivos instalados da dependência, avisos de licença e assets permanecem
intactos.

A transformação reconhece apenas o formato de declaração local com atribuições de strings. Chamadas,
valores calculados, inicialização com efeitos observáveis e o setter de protótipo não são transformados.
Outros módulos não passam por essa transformação. O build atual não publica source maps; antes de
habilitá-los, a transformação precisa fornecer os mapeamentos correspondentes.

A mudança é de empacotamento: não altera estados, permissões ou contratos da captura (INV-01,
API-P05/API-P06). Os testes de bundling comprovam descarte de tabelas não usadas, preservação integral
das usadas e manutenção dos efeitos de inicializações que não podem ser simplificadas. As suítes da
interface e de navegador continuam obrigatórias. Após atualizar o SDK, conferir novamente o bundle
final e a equivalência dos valores exportados. A reversão é retirar o plugin da configuração do Vite;
nenhum dado ou asset precisa ser migrado.

## Propriedades não utilizadas

`frontend/config/browser-build.ts` configura o minificador Terser 5.51.2 como dependência apenas de
build (licença BSD-2-Clause, já aceita pela política do projeto). A extração de propriedades constantes
com duas passagens elimina membros de registros acessados somente por chaves estáticas. Objetos
exportados, acessos dinâmicos e getters observáveis permanecem preservados. Não são habilitadas
transformações inseguras nem renomeação de propriedades. A suíte inclui esses casos para proteger a
compatibilidade. A troca abrange todos os chunks JavaScript, por isso os testes de navegador contra o
bundle final são um gate obrigatório.

## Estilos da captura

`frontend/config/liveness-styles.ts` seleciona os módulos de estilos das primitivas usadas pelo detector
(incluindo dependências de campos, seleção de câmera, ícones e conteúdo acessível). A seleção usa os
seletores dos arquivos modulares oficiais, mas filtra o CSS original para preservar a ordem da cascata,
as condições de mídia e todas as variações de estado. O módulo `base` contém os tokens compartilhados
e o reset original; não é substituído pelo reset alternativo publicado separadamente pelo fornecedor.
Fontes, URLs, keyframes e recursos permanecem intactos. Nenhuma decisão depende de procurar palavras
no conteúdo ou de observar apenas as classes de uma tela renderizada.

As variáveis de componentes são mantidas quando referenciadas por declarações preservadas, incluindo
o fechamento transitivo de aliases e todas as definições de um token nos temas. Os demais tokens
compartilhados permanecem. O parser PostCSS 8.5.30 é dependência apenas de build, com licença MIT e
versão já usada transitivamente pelo frontend. Os arquivos instalados do SDK não são alterados.

A suíte compara declarações, contextos e ordem com o CSS original, cobre dependências de tokens e
classes das primitivas alcançadas pelo detector, e executa a transformação em um bundle Vite real.
Uma atualização do SDK exige que essas verificações e os testes de navegador continuem passando;
nova primitiva deve ter seu módulo de estilos incluído. A reversão é retirar `livenessStyles()` da
configuração do Vite. Não há mudança de autenticação, de autorização ou dos contratos da captura.
