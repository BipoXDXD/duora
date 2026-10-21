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
