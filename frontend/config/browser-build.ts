import type { BuildOptions } from 'vite'

// Extrai propriedades constantes para descartar membros sem consumidores.
// Mantém as otimizações seguras padrão, sem renomear propriedades públicas.
export const browserBuild: BuildOptions = {
  minify: 'terser',
  terserOptions: { compress: { passes: 2, hoist_props: true } },
}
