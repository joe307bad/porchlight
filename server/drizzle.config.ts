// drizzle-kit: `npm run db:generate` diffs src/multiplayer/tables.ts against
// the last snapshot and writes the next migration into ./drizzle. The server
// applies pending migrations itself on start (persistence.ts).
import { defineConfig } from 'drizzle-kit';

export default defineConfig({
  dialect: 'postgresql',
  schema: './src/multiplayer/tables.ts',
  out: './drizzle',
});
