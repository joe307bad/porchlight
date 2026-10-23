import { defineConfig } from 'vitest/config';

export default defineConfig({
  server: { port: 5173, strictPort: true },
  // GLB models are assets (tests import them inline to parse them in Node).
  assetsInclude: ['**/*.glb'],
  test: { environment: 'node', include: ['tests/**/*.test.ts'] },
});
