// Serves the game's WebGL build in development: the build at
// http://localhost:5173/ (and Manage Porchlight at /manage/), with /auth and /mp (WebSockets too) passed through
// to the auth + game server on :3017, so sign-in cookies are first-party and
// the server's APP_URL (localhost:5173) matches, as Caddy does in production.
//
//   node unity/Tools~/serve.mjs [build-dir]      (default unity/Builds/WebGL)
//
// Keycloak's pages (/realms, /resources, /admin, /js) go through too, to :8080, so
// one address is all a browser or the desktop app needs: ./dev.sh --host <name>
// serves it all at http://<name>:5173/ for other machines (HOST=0.0.0.0 here).
//
// ./dev.sh runs this alongside the server.

import { createReadStream, statSync } from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(process.argv[2] ?? path.join(here, '..', 'Builds', 'WebGL'));
// Manage Porchlight as `npm run build` in manage/ last made it (the game's next build carries the same).
const manage = path.join(here, '..', 'Assets', 'StreamingAssets', 'manage');
const port = Number(process.env.PORT ?? 5173);
const upstream = { host: '127.0.0.1', port: Number(process.env.SERVER_PORT ?? 3017) };
const keycloak = { host: '127.0.0.1', port: Number(process.env.KEYCLOAK_PORT ?? 8080) };
const host = process.env.HOST ?? '127.0.0.1';

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript',
  '.wasm': 'application/wasm',
  '.data': 'application/octet-stream',
  '.json': 'application/json',
  '.css': 'text/css',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.glb': 'model/gltf-binary',
  '.gltf': 'model/gltf+json',
  '.jpg': 'image/jpeg',
  '.bin': 'application/octet-stream',
};

const toServer = (url) => url.startsWith('/auth') || url.startsWith('/mp');
const toKeycloak = (url) => /^\/(realms|resources|admin|js)\//.test(url);
const proxied = (url) => toServer(url) || toKeycloak(url);
const upstreamOf = (url) => (toKeycloak(url) ? keycloak : upstream);

function proxy(req, res) {
  // dev.sh without Docker: no server on purpose, so the game plays alone. (A 502 means it's meant to be there.)
  if (process.env.NO_GAME_SERVER) {
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: 'No game server in this run.' }));
    return;
  }
  const out = http.request({ ...upstreamOf(req.url ?? ''), method: req.method, path: req.url, headers: req.headers }, (up) => {
    res.writeHead(up.statusCode ?? 502, up.headers);
    up.pipe(res);
  });
  // The browser went (a closed tab, the feed with it): close this end too, so the server knows.
  res.on('close', () => {
    if (!res.writableFinished) out.destroy();
  });
  out.on('error', () => {
    if (res.headersSent || res.destroyed) return res.destroy();
    res.writeHead(502, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: `Nothing is answering on :${upstreamOf(req.url ?? '').port}.` }));
  });
  req.pipe(out);
}

function serveFile(req, res) {
  let rel = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  // Manage Porchlight (manage/, built into the game's StreamingAssets), at /manage/ as Caddy serves it.
  if (rel === '/manage') {
    res.writeHead(302, { location: '/manage/' });
    return res.end();
  }
  let base = root;
  if (rel.startsWith('/manage/')) {
    base = manage;
    rel = rel.slice('/manage'.length);
    let found = false;
    try {
      found = statSync(path.join(base, rel)).isFile();
    } catch {}
    if (!found) rel = '/index.html';
  }
  if (rel.endsWith('/')) rel += 'index.html';
  const file = path.join(base, rel);
  if (!file.startsWith(base)) {
    res.writeHead(403);
    return res.end();
  }
  let size;
  try {
    const st = statSync(file);
    if (!st.isFile()) throw new Error('not a file');
    size = st.size;
  } catch {
    res.writeHead(404);
    return res.end('not found');
  }
  const headers = { 'content-length': size, 'cache-control': 'no-cache' };
  let ext = path.extname(file);
  // Unity's compressed build files (gzip, named .unityweb): tell the browser, so
  // it decompresses them (and can compile the wasm as it streams in).
  if (ext === '.unityweb') {
    headers['content-encoding'] = 'gzip';
    ext = path.extname(file.slice(0, -ext.length));
  }
  headers['content-type'] = TYPES[ext] ?? 'application/octet-stream';
  res.writeHead(200, headers);
  createReadStream(file).pipe(res);
}

const server = http.createServer((req, res) => (proxied(req.url ?? '') ? proxy(req, res) : serveFile(req, res)));

// WebSockets (/mp): pass the upgrade and then the raw bytes through.
server.on('upgrade', (req, socket, head) => {
  if (!proxied(req.url ?? '') || process.env.NO_GAME_SERVER) return socket.destroy();
  const up = net.connect(upstreamOf(req.url ?? ''), () => {
    const lines = [`${req.method} ${req.url} HTTP/1.1`];
    for (let i = 0; i < req.rawHeaders.length; i += 2) lines.push(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}`);
    up.write(lines.join('\r\n') + '\r\n\r\n');
    if (head.length) up.write(head);
    up.pipe(socket);
    socket.pipe(up);
  });
  up.on('error', () => socket.destroy());
  socket.on('error', () => up.destroy());
});

server.listen(port, host, () => console.log(`Porchlight at ${process.env.PUBLIC_URL ?? `http://localhost:${port}`}/  ← ${root}`));
