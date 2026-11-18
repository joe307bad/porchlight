// Manage Porchlight is served at /manage/: on the web by Caddy (from the game's
// build, infra/docker/Caddyfile), in the desktop app by the game itself
// (Game/Net/ManageLink.cs). It's built straight into the game's
// StreamingAssets, so every build of the game carries it.
//
// `npm run dev` serves it at http://localhost:5174/manage/ with /auth and /mp
// passed to the server on :3017 (sign in on the game at localhost:5173 first:
// cookies don't mind the port).

import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const server = `http://127.0.0.1:${process.env.SERVER_PORT ?? 3017}`;

export default defineConfig({
  base: '/manage/',
  plugins: [react()],
  build: {
    outDir: fileURLToPath(new URL('../unity/Assets/StreamingAssets/manage', import.meta.url)),
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false,
  },
  server: {
    port: 5174,
    proxy: { '/auth': server, '/mp': server },
  },
});
