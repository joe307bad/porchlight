// The game server for the C# client's network check (GenCheck net): a real
// Colyseus server with nothing saved, on a spare port. Prints the port and a
// session cookie value for two test accounts, then runs until killed.
//
//   node unity/Tools~/net/server.ts

import { SESSION_COOKIE } from '../../../server/src/app.ts';
import { seal } from '../../../server/src/cookies.ts';
import { startGameServer } from '../../../server/src/multiplayer/index.ts';
import { MemoryPersistence } from '../../../server/src/multiplayer/persistence.ts';

const SECRET = 'x'.repeat(40);
const persistence = new MemoryPersistence();
const game = await startGameServer({
  port: 0,
  appUrl: 'http://localhost:5173',
  sessionSecret: SECRET,
  persistence,
  handleSignals: true,
  handler: async (_req, res) => {
    res.writeHead(404);
    res.end();
  },
});
const token = (sub: string) => seal(SECRET, { sub, name: `Person ${sub}`, email: null, idToken: 't' }, 3600);
console.log(JSON.stringify({ port: game.port, cookie: SESSION_COOKIE, a: token('unity-a'), b: token('unity-b') }));
