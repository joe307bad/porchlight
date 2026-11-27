import { createHandler } from './app.ts';
import { loadConfig } from './config.ts';
import { watchHealth } from './health.ts';
import { startGameServer } from './multiplayer/index.ts';
import { MemoryPersistence, PgPersistence } from './multiplayer/persistence.ts';
import { findWorker } from './sim/pool.ts';

const config = loadConfig();
watchHealth();
const persistence = config.databaseUrl ? new PgPersistence(config.databaseUrl) : new MemoryPersistence();
if (!config.databaseUrl) console.warn('DATABASE_URL is not set: multiplayer runs, but nothing is saved.');
persistence.start();

// On SIGTERM Colyseus saves every room and exits; never let a hung database hold up a deploy.
for (const signal of ['SIGTERM', 'SIGINT'] as const) {
  process.once(signal, () => setTimeout(() => process.exit(1), 10_000).unref());
}

// The towns' traffic and townsfolk: run here if there's a worker (sim/), else by the players in each.
const worker = findWorker();
console.log(worker ? `sim: ${[worker.command, ...worker.args].join(' ')}` : 'sim: none (SIM=off, or no build of sim/): the players run their towns');

await startGameServer({
  port: config.port,
  appUrl: config.appUrl,
  sessionSecret: config.sessionSecret,
  persistence,
  handler: createHandler(config),
  missionsDir: process.env.MISSIONS_DIR || undefined,
  sim: worker,
  simWorkers: Number(process.env.SIM_WORKERS) || 1,
});
console.log(`porchlight auth + game on :${config.port} for ${config.appUrl} (issuer ${config.issuer})`);
