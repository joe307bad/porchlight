// The process's own story in the logs, for investigating restarts and lag:
//
// - on start: the commit (src/REVISION, written by the deploy) and Node version;
// - every minute: memory, event-loop delay (a busy or stalled loop is lag for
//   every player at once) and how many are playing;
// - on a signal: which one, after how long, with how many playing (a deploy
//   sends SIGTERM; anything else stopping the container sends one too);
// - on exit: the code. Uncaught exceptions are Colyseus's to print (it exits 1).

import { readFileSync } from 'node:fs';
import { monitorEventLoopDelay } from 'node:perf_hooks';
import { openRooms } from './multiplayer/CityRoom.ts';

const MINUTE = 60_000;
const startedAt = Date.now();

/** The commit this is (the deploy writes it), or "dev". */
export function revision(): string {
  try {
    return readFileSync(new URL('./REVISION', import.meta.url), 'utf8').trim().slice(0, 40) || 'dev';
  } catch {
    return 'dev';
  }
}

const uptime = (): string => {
  const m = (Date.now() - startedAt) / MINUTE;
  return m < 120 ? `${m.toFixed(1)} min` : `${(m / 60).toFixed(1)} h`;
};

function playing(): { rooms: number; players: number } {
  let rooms = 0;
  let players = 0;
  for (const room of openRooms()) {
    rooms++;
    players += room.clients.length;
  }
  return { rooms, players };
}

const mb = (bytes: number): number => Math.round(bytes / 1048576);

export function watchHealth(): void {
  console.log(`process: started ${revision()} on Node ${process.version}, pid ${process.pid}`);

  const loop = monitorEventLoopDelay({ resolution: 20 });
  loop.enable();
  setInterval(() => {
    const { rooms, players } = playing();
    const m = process.memoryUsage();
    const p99 = loop.percentile(99) / 1e6;
    const max = loop.max / 1e6;
    loop.reset();
    // Quiet when nobody's playing and nothing's wrong.
    if (!players && max < 200) return;
    console.log(
      `process: ${players} player(s) in ${rooms} room(s), rss ${mb(m.rss)} MB, heap ${mb(m.heapUsed)}/${mb(m.heapTotal)} MB, event loop p99 ${p99.toFixed(1)} ms, max ${max.toFixed(1)} ms, up ${uptime()}`,
    );
  }, MINUTE).unref();

  for (const signal of ['SIGTERM', 'SIGINT', 'SIGUSR2'] as const) {
    process.on(signal, () => {
      const { rooms, players } = playing();
      console.log(`process: ${signal} after ${uptime()}, ${players} player(s) in ${rooms} room(s): shutting down`);
    });
  }
  process.on('exit', (code) => console.log(`process: exit ${code} after ${uptime()}`));
  process.on('warning', (w) => console.warn(`process: warning: ${w.name}: ${w.message}`));
}
