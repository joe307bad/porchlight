import { createHandler } from './app.ts';
import { loadConfig } from './config.ts';
import { startGameServer } from './multiplayer/index.ts';
import { MemoryPersistence, PgPersistence } from './multiplayer/persistence.ts';

const config = loadConfig();
const persistence = config.databaseUrl ? new PgPersistence(config.databaseUrl) : new MemoryPersistence();
if (!config.databaseUrl) console.warn('DATABASE_URL is not set: multiplayer runs, but nothing is saved.');
persistence.start();

// On SIGTERM Colyseus saves every room and exits; never let a hung database hold up a deploy.
for (const signal of ['SIGTERM', 'SIGINT'] as const) {
  process.once(signal, () => setTimeout(() => process.exit(1), 10_000).unref());
}

await startGameServer({
  port: config.port,
  appUrl: config.appUrl,
  sessionSecret: config.sessionSecret,
  persistence,
  handler: createHandler(config),
  missionsDir: process.env.MISSIONS_DIR || undefined,
});
console.log(`porchlight auth + game on :${config.port} for ${config.appUrl} (issuer ${config.issuer})`);
