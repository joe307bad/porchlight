// The game server: Colyseus on the same HTTP server (and port) as the auth
// routes. Everything multiplayer is under /mp on the app's own origin, so the
// session cookie comes along:
//
//   /mp/characters...          the account's characters (accounts.ts)
//   /mp/matchmake/...          Colyseus matchmaking (POST, then a WebSocket)
//   /mp/<process>/<room>       the room's WebSocket
//
// Anything else goes to the auth handler (/auth/*).

import { createServer, type IncomingMessage, type Server as HttpServer, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { Server, matchMaker } from '@colyseus/core';
import { WebSocketTransport } from '@colyseus/ws-transport';
import { createCharacterRoutes, type GameDeps } from './accounts.ts';
import { CityRoom, configureCityRoom, isOnline } from './CityRoom.ts';
import type { Persistence } from './persistence.ts';
import { CITY_ROOM } from './protocol.ts';

export interface GameServerOptions {
  port: number;
  appUrl: string;
  sessionSecret: string;
  persistence: Persistence;
  /** Everything that isn't the game's (the auth routes). */
  handler: (req: IncomingMessage, res: ServerResponse) => Promise<void>;
  /** Let Colyseus handle SIGTERM / SIGINT (save every room, then exit). Off in tests. */
  handleSignals?: boolean;
}

export interface GameServer {
  server: Server;
  httpServer: HttpServer;
  port: number;
}

export async function startGameServer(opts: GameServerOptions): Promise<GameServer> {
  const deps: GameDeps = { persistence: opts.persistence, sessionSecret: opts.sessionSecret, appUrl: opts.appUrl, isOnline };
  configureCityRoom(deps);
  const characters = createCharacterRoutes(deps);

  const httpServer = createServer();
  const server = new Server({
    // 4 KB is plenty for any message the game sends.
    transport: new WebSocketTransport({ server: httpServer, maxPayload: 4 * 1024 }),
    greet: false,
    gracefullyShutdown: opts.handleSignals ?? true,
    // Colyseus answers its own routes and hands everything else to this app.
    express: (app) => {
      app.disable('x-powered-by');
      app.use((req: IncomingMessage, res: ServerResponse) => {
        void (async () => {
          if (!(await characters(req, res))) await opts.handler(req, res);
        })();
      });
    },
  });
  // Colyseus reflects any Origin by default (with credentials); only the app's own will do.
  matchMaker.controller.getCorsHeaders = () => ({ 'Access-Control-Allow-Origin': opts.appUrl });
  server.define(CITY_ROOM, CityRoom).filterBy(['town']);
  server.onShutdown(() => opts.persistence.close());

  await server.listen(opts.port);
  // Colyseus's routes are at /matchmake/...; ours are under /mp. (Its WebSocket
  // URLs are matched on their last two segments, so those need nothing.)
  httpServer.prependListener('request', (req: IncomingMessage) => {
    if (req.url?.startsWith('/mp/matchmake/')) req.url = req.url.slice('/mp'.length);
  });
  return { server, httpServer, port: (httpServer.address() as AddressInfo).port };
}
