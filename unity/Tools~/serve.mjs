// Serves the Unity WebGL build the way Vite serves the web app in development:
// the build at http://localhost:5173/, with /auth and /mp (WebSockets too)
// passed through to the auth + game server on :3017, so sign-in cookies are
// first-party and the server's APP_URL (localhost:5173) still matches.
//
//   node unity/Tools~/serve.mjs [build-dir]      (default unity/Builds/WebGL)
//
// Run the server first (./dev.sh starts it, along with Vite: stop Vite, or
// set PORT here to something else and open that instead).

import { createReadStream, statSync } from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(process.argv[2] ?? path.join(here, '..', 'Builds', 'WebGL'));
const port = Number(process.env.PORT ?? 5173);
const upstream = { host: '127.0.0.1', port: Number(process.env.SERVER_PORT ?? 3017) };

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

const proxied = (url) => url.startsWith('/auth') || url.startsWith('/mp');

function proxy(req, res) {
  const out = http.request({ ...upstream, method: req.method, path: req.url, headers: req.headers }, (up) => {
    res.writeHead(up.statusCode ?? 502, up.headers);
    up.pipe(res);
  });
  out.on('error', () => {
    res.writeHead(502, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: "The game server isn't running (:3017)." }));
  });
  req.pipe(out);
}

function serveFile(req, res) {
  let rel = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (rel.endsWith('/')) rel += 'index.html';
  const file = path.join(root, rel);
  if (!file.startsWith(root)) {
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
  if (ext === '.gz' || ext === '.br') {
    headers['content-encoding'] = ext === '.gz' ? 'gzip' : 'br';
    ext = path.extname(file.slice(0, -ext.length));
  }
  headers['content-type'] = TYPES[ext] ?? 'application/octet-stream';
  res.writeHead(200, headers);
  createReadStream(file).pipe(res);
}

const server = http.createServer((req, res) => (proxied(req.url ?? '') ? proxy(req, res) : serveFile(req, res)));

// WebSockets (/mp): pass the upgrade and then the raw bytes through.
server.on('upgrade', (req, socket, head) => {
  if (!proxied(req.url ?? '')) return socket.destroy();
  const up = net.connect(upstream, () => {
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

server.listen(port, () => console.log(`Porchlight (Unity WebGL) at http://localhost:${port}/  ← ${root}`));
