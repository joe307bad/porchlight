// Load test: bots that sign in (an account and a character each), join a
// town and wander it at walking pace, like players would. Watch the server's
// log for its tick times (logged every minute per room).
//
//   npm run bots -- 100 [seconds] [town]       (server/.env.development supplies SESSION_SECRET)
//
// Bots authenticate with a session sealed with SESSION_SECRET, so this only
// works against a server whose secret you hold: dev, not production.

import { Client, type Room } from '@colyseus/sdk';
import { SESSION_COOKIE } from '../src/app.ts';
import { seal } from '../src/cookies.ts';
import { SEND_MS } from '../src/multiplayer/protocol.ts';

const count = Number(process.argv[2] ?? 20);
const seconds = Number(process.argv[3] ?? 60);
const town = process.argv[4] ?? 'seed=bots';
const base = process.env.BOTS_URL ?? `http://localhost:${process.env.PORT ?? 3017}`;
const secret = process.env.SESSION_SECRET;
if (!secret) throw new Error('SESSION_SECRET is not set');

// Each bot is its own account (with one character), as if 100 people had signed in.
async function bot(i: number): Promise<{ room: Room; patches: number }> {
  const sub = `load-test-bot-${i}`;
  const t = seal(secret!, { sub, name: `Bot ${i}`, email: null, idToken: '' }, 3600);
  const h = { cookie: `${SESSION_COOKIE}=${t}`, 'content-type': 'application/json' };
  const list = (await (await fetch(`${base}/mp/characters`, { headers: h })).json()) as { characters: { id: string }[] };
  const id =
    list.characters[0]?.id ??
    ((await (await fetch(`${base}/mp/characters`, { method: 'POST', headers: h, body: JSON.stringify({ name: `Bot ${i}` }) })).json()) as { id: string }).id;
  const client = new Client(`${base}/mp`);
  client.auth.token = t;
  const room = await client.joinOrCreate('city', { town, width: 128, height: 128, character: id });
  const stats = { room, patches: 0 };
  room.onStateChange(() => stats.patches++);
  let x = 20 + Math.random() * 88;
  let z = 20 + Math.random() * 88;
  let heading = Math.random() * Math.PI * 2;
  let seq = 0;
  let epoch = 0;
  room.onMessage('spawn', () => {});
  room.onMessage('dots', () => {});
  room.onMessage('world', () => {});
  room.onMessage('correct', (c: { x: number; z: number; epoch: number }) => {
    x = c.x;
    z = c.z;
    epoch = c.epoch;
  });
  const timer = setInterval(() => {
    heading += (Math.random() - 0.5) * 0.4;
    x += Math.sin(heading) * 3.4 * (SEND_MS / 1000);
    z += Math.cos(heading) * 3.4 * (SEND_MS / 1000);
    if (x < 2 || x > 126 || z < 2 || z > 126) heading += Math.PI;
    x = Math.min(126, Math.max(2, x));
    z = Math.min(126, Math.max(2, z));
    seq = (seq + 1) & 0xffff;
    room.send('move', { seq, t: Math.round(performance.now()), epoch, x, y: 0, z, yaw: heading, anim: 1, car: 0, ride: '' });
  }, SEND_MS);
  room.onLeave(() => clearInterval(timer));
  return stats;
}

console.log(`${count} bots → ${base}, town ${town}, for ${seconds}s`);
const bots: { room: Room; patches: number }[] = [];
for (let i = 0; i < count; i++) {
  bots.push(await bot(i));
  await new Promise((r) => setTimeout(r, 20));
}
console.log(`all joined (${new Set(bots.map((b) => b.room.roomId)).size} room(s))`);
await new Promise((r) => setTimeout(r, seconds * 1000));
const patches = bots.reduce((s, b) => s + b.patches, 0);
console.log(`patches per bot per second: ${(patches / count / seconds).toFixed(1)}`);
await Promise.all(bots.map((b) => b.room.leave()));
process.exit(0);
