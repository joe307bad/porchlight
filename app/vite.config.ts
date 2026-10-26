import { defineConfig } from 'vitest/config';

export default defineConfig({
  server: {
    port: 5173,
    strictPort: true,
    // The auth service (server/, `npm run dev` there): same origin, so its cookies are first-party.
    proxy: { '/auth': 'http://localhost:3017' },
  },
  // GLB models are assets (tests import them inline to parse them in Node).
  assetsInclude: ['**/*.glb'],
  test: { environment: 'node', include: ['tests/**/*.test.ts'] },
});
