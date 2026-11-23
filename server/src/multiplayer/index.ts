// The game server: Colyseus on the same HTTP server (and port) as the auth
// routes. Everything multiplayer is under /mp on the app's own origin, so the
// session cookie comes along:
//
//   /mp/characters...          the account's characters (accounts.ts)
//   /mp/towns..., /mp/progress the towns, and what the account has earned (accounts.ts)
//   /mp/missions...            the missions, compiled from server/missions (../missions.ts)
//   /mp/traffic/...            the traffic view's county map and live feed (feed.ts)
//   /mp/sim/where              where a car or townsperson is right now (the simulation's: ../sim/)
//   /mp/matchmake/...          Colyseus matchmaking (POST, then a WebSocket)
//   /mp/<process>/<room>       the room's WebSocket
//
// Anything else goes to the auth handler (/auth/*).

import { createServer, type IncomingMessage, type Server as HttpServer, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { Server, matchMaker } from '@colyseus/core';
import { WebSocketTransport } from '@colyseus/ws-transport';
import { createMissionRoutes, DEFAULT_MISSIONS_DIR, MissionStore } from '../missions.ts';
import { createCharacterRoutes, type GameDeps } from './accounts.ts';
import { TrafficFeed } from './feed.ts';
import { CityRoom, configureCityRoom, countyChanged, isOnline, playingIn, presence, tell, whereIs } from './CityRoom.ts';
import type { Persistence } from './persistence.ts';
import { CITY_ROOM } from './protocol.ts';
import { SimPool, type SimCommand } from '../sim/pool.ts';
import { SimLink, setSim } from '../sim/link.ts';
import { openRooms } from './CityRoom.ts';

export interface GameServerOptions {
  port: number;
  appUrl: string;
  sessionSecret: string;
  persistence: Persistence;
  /** Everything that isn't the game's (the auth routes). */
  handler: (req: IncomingMessage, res: ServerResponse) => Promise<void>;
  /** Let Colyseus handle SIGTERM / SIGINT (save every room, then exit). Off in tests. */
  handleSignals?: boolean;
  /** Where the missions' .ink files are (default server/missions). */
  missionsDir?: string;
  /** The towns' traffic and townsfolk, run here (sim/pool.ts's findWorker), with this many workers; none: the players run them. */
  sim?: SimCommand | null;
  simWorkers?: number;
}

export interface GameServer {
  server: Server;
  httpServer: HttpServer;
  port: number;
  traffic: TrafficFeed;
  sim: SimLink | null;
}

export async function startGameServer(opts: GameServerOptions): Promise<GameServer> {
  const store = new MissionStore(opts.missionsDir ?? DEFAULT_MISSIONS_DIR);
  const deps: GameDeps = { persistence: opts.persistence, sessionSecret: opts.sessionSecret, appUrl: opts.appUrl, isOnline, presence, playingIn, whereIs, tell, countyChanged, missions: store };
  configureCityRoom(deps);
  const characters = createCharacterRoutes(deps);
  const missions = createMissionRoutes(store);
  const simLink = opts.sim ? new SimLink(new SimPool(opts.sim, opts.simWorkers ?? 1), deps, openRooms) : null;
  setSim(simLink);
  const traffic = new TrafficFeed(deps, undefined, undefined, simLink);

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
        // A rejection nobody catches ends the process: one bad request mustn't take everyone's game with it.
        void (async () => {
          if (!(await missions(req, res)) && !(await traffic.routes(req, res)) && !(await characters(req, res))) await opts.handler(req, res);
        })().catch((err) => {
          console.error(`${req.method} ${req.url}:`, err);
          if (!res.headersSent) res.writeHead(500, { 'content-type': 'application/json' });
          res.end(JSON.stringify({ error: 'internal error' }));
        });
      });
    },
  });
  // Colyseus reflects any Origin by default (with credentials); only the app's own will do.
  matchMaker.controller.getCorsHeaders = () => ({ 'Access-Control-Allow-Origin': opts.appUrl });
  server.define(CITY_ROOM, CityRoom).filterBy(['town']);
  server.onShutdown(() => {
    traffic.close();
    simLink?.close();
    setSim(null);
    return opts.persistence.close();
  });

  await server.listen(opts.port);
  // Colyseus's routes are at /matchmake/...; ours are under /mp. (Its WebSocket
  // URLs are matched on their last two segments, so those need nothing.)
  httpServer.prependListener('request', (req: IncomingMessage) => {
    if (req.url?.startsWith('/mp/matchmake/')) req.url = req.url.slice('/mp'.length);
  });
  simLink?.start();
  return { server, httpServer, port: (httpServer.address() as AddressInfo).port, traffic, sim: simLink };
}
