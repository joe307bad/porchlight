// Who's asking, for the game: the signed-in account (from the same sealed
// session cookie the auth routes set), and the routes for its characters, the
// towns and what it's earned.
//
//   GET    /mp/characters?tab=       this account's characters (where each lives, the town each is playing in, if any, and whether someone other than this tab has them)
//   POST   /mp/characters            { name, look } → a new one
//   DELETE /mp/characters/:id        delete one (not while it's being played)
//   PUT    /mp/characters/:id/town   { town } move them to a town (out of any house they had)
//   PUT    /mp/characters/:id/home   { x, y } move them into the house at this tile of their town
//   GET    /mp/towns                 the towns this account can live in (Porchlight, everyone's; and its own), and how many more it can make
//   POST   /mp/towns                 { name, key? } → a new one, from the town editor's key or a fresh seed (MAX_TOWNS at most: delete one to make another)
//   DELETE /mp/towns/:id             delete one of this account's (not while anyone's playing there)
//   GET    /mp/towns/:id/homes       the houses people live in there
//   GET    /mp/progress              the missions and unlocks, and which this account has earned
//   GET    /mp/players/:id           where a character (anyone's, by the id the room shows) is playing right now: { town, session, x, z, name }, or 404
//   GET    /mp/records?town=&mission=          a mission's best times in a town (its key), and this account's
//   POST   /mp/records               { town, mission, character, seconds } keep a time (if it's the account's best there) → the board
//
// One account plays as any of its characters, several at once: a parent and
// child on one sign-in each play their own. What's earned is the account's, so
// all of them have it, in every town. A town an account makes is its own: only
// the towns that come with the game (Porchlight) are shared between accounts.

import type { IncomingMessage, ServerResponse } from 'node:http';
import { SESSION_COOKIE, type SessionPayload } from '../app.ts';
import { parseCookies, unseal } from '../cookies.ts';
import type { Mission } from '../missions.ts';
import { CharacterError, cleanLook, cleanName, cleanSeconds, cleanTownKey, MAX_TOWNS, TOWN_NAME_MAX, type Persistence } from './persistence.ts';
import { lookAllowed, UNLOCK_NAMES, UNLOCKS } from './protocol.ts';

export interface GameDeps {
  persistence: Persistence;
  sessionSecret: string;
  appUrl: string;
  /** Is this character in a room right now? */
  isOnline(characterId: string): boolean;
  /** This character right now, as this tab sees it. */
  presence(characterId: string, tab: string): { town: string | null; session: string | null; inUse: boolean };
  /** An account made or deleted a town: its rooms work out their ground again. */
  countyChanged?(account: string): void;
  /** How many characters are playing in this town (its key) right now. */
  playingIn(townKey: string): number;
  /** Where a character is playing right now (their room's key, session, and spot in its terms), or null. */
  whereIs?(characterId: string): { town: string; session: string; x: number; z: number; name: string } | null;
  /** The missions on offer. */
  missions: { list(): Promise<Mission[]>; get(id: string): Promise<Mission | null> };
}

/**
 * The session from the cookie, or from a bearer token holding the same sealed
 * value (scripts like the load-test bots, which can't set cookies on a
 * WebSocket). Either way it's the HttpOnly cookie's content, which page script
 * never sees.
 */
export function readSession(secret: string, cookieHeader: string | null | undefined, token?: string | null): SessionPayload | null {
  const fromCookie = unseal<SessionPayload>(secret, parseCookies(cookieHeader ?? undefined).get(SESSION_COOKIE));
  if (fromCookie) return fromCookie;
  return token ? unseal<SessionPayload>(secret, token) : null;
}

/** A request from another site (only checked when the browser says where it's from). */
export const foreignOrigin = (appUrl: string, origin: string | null | undefined): boolean => !!origin && origin !== appUrl;

const PREFIX = '/mp/characters';
const TOWNS = '/mp/towns';
const PROGRESS = '/mp/progress';
const RECORDS = '/mp/records';
const PLAYERS = '/mp/players';

async function readJson(req: IncomingMessage, limit = 1024): Promise<unknown> {
  let size = 0;
  const chunks: Buffer[] = [];
  for await (const chunk of req) {
    size += (chunk as Buffer).length;
    if (size > limit) throw new CharacterError('Too big.', 413);
    chunks.push(chunk as Buffer);
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
  } catch {
    throw new CharacterError('Not JSON.');
  }
}

/** A tile of a town: towns are at most 1024 across. */
const tile = (v: unknown): v is number => Number.isInteger(v) && (v as number) >= 0 && (v as number) < 1024;

/** Handles the character routes; false if the request isn't one of them. */
export function createCharacterRoutes(deps: GameDeps) {
  return async (req: IncomingMessage, res: ServerResponse): Promise<boolean> => {
    const path = (req.url ?? '/').split('?')[0];
    const under = (prefix: string) => path === prefix || path.startsWith(`${prefix}/`);
    if (!under(PREFIX) && !under(TOWNS) && !under(PLAYERS) && path !== PROGRESS && path !== RECORDS) return false;
    const send = (status: number, body: unknown): true => {
      res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      res.end(JSON.stringify(body));
      return true;
    };
    try {
      const session = readSession(deps.sessionSecret, req.headers.cookie);
      if (!session) return send(401, { error: 'Not signed in.' });
      if (foreignOrigin(deps.appUrl, req.headers.origin)) return send(403, { error: 'Wrong origin.' });
      if (!deps.persistence.ready) return send(503, { error: 'The game is starting up. Try again in a moment.' });
      const account = await deps.persistence.account(session.sub, session.name);
      if (account.banned) return send(403, { error: 'This account is banned.' });

      const json = async <T>(): Promise<T> => {
        // JSON only: a cross-site form can't send it without a preflight.
        if (!req.headers['content-type']?.startsWith('application/json')) throw new CharacterError('Send JSON.', 415);
        return (await readJson(req)) as T;
      };
      const parts = path.split('/').slice(1).map(decodeURIComponent); // ['mp', 'towns', id, 'homes']

      if (parts[1] === 'players') {
        if (req.method !== 'GET') return send(405, { error: 'Method not allowed.' });
        if (parts.length !== 3 || !parts[2] || parts[2].length > 64) return send(404, { error: 'Not found.' });
        const where = deps.whereIs?.(parts[2]) ?? null;
        return where ? send(200, where) : send(404, { error: 'Not playing right now.' });
      }

      if (path === PROGRESS) {
        if (req.method !== 'GET') return send(405, { error: 'Method not allowed.' });
        const [earned, missions] = await Promise.all([deps.persistence.progress(account.id), deps.missions.list()]);
        const done = new Map(earned.completions.map((c) => [c.missionId, c]));
        const had = new Map(earned.unlocks.map((u) => [u.what, u.at]));
        return send(200, {
          missions: missions
            .filter((m) => !m.error)
            .map((m) => {
              const c = done.get(m.id);
              return { id: m.id, title: m.title, summary: m.summary, unlocks: m.unlocks, times: c?.times ?? 0, firstAt: c?.firstAt ?? null, lastAt: c?.lastAt ?? null, firstCharacter: c?.firstCharacter ?? null };
            }),
          unlocks: UNLOCKS.map((id) => ({ id, name: UNLOCK_NAMES[id], at: had.get(id) ?? null, mission: missions.find((m) => m.unlocks === id)?.title ?? null })),
        });
      }

      if (path === RECORDS) {
        const mission = async (id: unknown) => {
          // Only real missions: nothing's kept for any other name a client makes up.
          if (typeof id !== 'string' || !(await deps.missions.get(id))) throw new CharacterError('No such mission.', 404);
          return id;
        };
        if (req.method === 'GET') {
          const query = new URL(req.url ?? '/', 'http://x').searchParams;
          const town = cleanTownKey(query.get('town'));
          if (!town) throw new CharacterError("That isn't a town.");
          return send(200, await deps.persistence.records(account.id, town, await mission(query.get('mission'))));
        }
        if (req.method === 'POST') {
          const body = await json<{ town?: unknown; mission?: unknown; character?: unknown; seconds?: unknown }>();
          const town = cleanTownKey(body.town);
          if (!town) throw new CharacterError("That isn't a town.");
          const seconds = cleanSeconds(body.seconds);
          if (seconds === null) throw new CharacterError("That isn't a time.");
          if (typeof body.character !== 'string') throw new CharacterError('Who ran it?');
          return send(200, await deps.persistence.keepRecord(account.id, body.character, town, await mission(body.mission), seconds));
        }
        return send(405, { error: 'Method not allowed.' });
      }

      if (under(TOWNS)) {
        if (req.method === 'GET' && path === TOWNS) {
          const towns = await deps.persistence.listTowns(account.id);
          // `account`: the north room (your own part of the county) is named for it.
          return send(200, { towns: towns.map((t) => ({ ...t, playing: deps.playingIn(t.key) })), made: towns.filter((t) => t.mine).length, max: MAX_TOWNS, account: account.id });
        }
        if (req.method === 'POST' && path === TOWNS) {
          const body = await json<{ name?: unknown; key?: unknown }>();
          const name = cleanName(body.name, TOWN_NAME_MAX);
          if (!name) throw new CharacterError(`Town names are 1 to ${TOWN_NAME_MAX} characters.`);
          const key = body.key === undefined ? undefined : cleanTownKey(body.key);
          if (key === null) throw new CharacterError("That isn't a town.");
          const town = await deps.persistence.createTown(account.id, name, key);
          deps.countyChanged?.(account.id);
          return send(201, { ...town, playing: 0 });
        }
        if (req.method === 'DELETE' && parts.length === 3) {
          const key = await deps.persistence.ownTownKey(account.id, parts[2]);
          if (deps.playingIn(key) > 0) throw new CharacterError('Someone is playing in that town right now.', 409);
          await deps.persistence.deleteTown(account.id, parts[2]);
          deps.countyChanged?.(account.id);
          return send(200, { ok: true });
        }
        if (req.method === 'GET' && parts.length === 4 && parts[3] === 'homes') {
          return send(200, { homes: await deps.persistence.listHomes(account.id, parts[2]) });
        }
        return send(405, { error: 'Method not allowed.' });
      }

      if (req.method === 'PUT' && parts.length === 4 && parts[3] === 'town') {
        const town = (await json<{ town?: unknown }>()).town;
        if (typeof town !== 'string') throw new CharacterError('Which town?');
        await deps.persistence.moveToTown(account.id, parts[2], town);
        return send(200, await deps.persistence.character(account.id, parts[2]));
      }
      if (req.method === 'PUT' && parts.length === 4 && parts[3] === 'home') {
        const { x, y } = await json<{ x?: unknown; y?: unknown }>();
        if (!tile(x) || !tile(y)) throw new CharacterError('Which house?');
        await deps.persistence.claimHome(account.id, parts[2], x, y);
        return send(200, await deps.persistence.character(account.id, parts[2]));
      }

      if (req.method === 'GET' && path === PREFIX) {
        const characters = await deps.persistence.listCharacters(account.id);
        const tab = new URLSearchParams((req.url ?? '').split('?')[1] ?? '').get('tab')?.slice(0, 64) ?? '';
        return send(200, { characters: characters.map((c) => ({ ...c, ...deps.presence(c.id, tab) })) });
      }
      if (req.method === 'POST' && path === PREFIX) {
        const body = await json<{ name?: unknown; look?: unknown }>();
        const name = cleanName(body.name);
        if (!name) throw new CharacterError('Names are 1 to 20 characters.');
        // Only what the account has unlocked: no animals until a mission's earned them.
        const look = cleanLook(body.look);
        const created = await deps.persistence.createCharacter(account.id, name, look && lookAllowed(look, await deps.persistence.unlocks(account.id)) ? look : null);
        return send(201, created);
      }
      if (req.method === 'DELETE' && path.startsWith(`${PREFIX}/`)) {
        const id = parts[2];
        if (deps.isOnline(id)) throw new CharacterError('That character is playing right now.', 409);
        const gone = await deps.persistence.deleteCharacter(account.id, id);
        return send(gone ? 200 : 404, gone ? { ok: true } : { error: 'No such character.' });
      }
      return send(405, { error: 'Method not allowed.' });
    } catch (err) {
      if (err instanceof CharacterError) return send(err.status, { error: err.message });
      console.error('characters:', err);
      return send(503, { error: "The game's database isn't answering. Try again in a moment." });
    }
  };
}
