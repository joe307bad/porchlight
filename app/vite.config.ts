import { defineConfig } from 'vitest/config';

export default defineConfig({
  server: {
    port: 5173,
    strictPort: true,
    // The server (server/, `npm run dev` there): sign-in at /auth, the game at
    // /mp (HTTP and WebSockets). Same origin, so its cookies are first-party.
    proxy: {
      '/auth': 'http://localhost:3017',
      '/mp': { target: 'http://localhost:3017', ws: true },
    },
  },
  // GLB models are assets (tests import them inline to parse them in Node).
  assetsInclude: ['**/*.glb'],
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts'],
    // Some suites generate dozens of towns up front (invariants: 50); CI's
    // two cores, shared with the other test files, need more than 10 s for it.
    hookTimeout: 60_000,
  },
});
