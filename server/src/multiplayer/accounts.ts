// Who's asking, for the game: the signed-in account (from the same sealed
// session cookie the auth routes set), and the routes for its characters.
//
//   GET    /mp/characters        this account's characters
//   POST   /mp/characters        { name, look } → a new one
//   DELETE /mp/characters/:id    delete one (not while it's being played)
//
// One account plays as any of its characters, several at once: a parent and
// child on one sign-in each play their own.

import type { IncomingMessage, ServerResponse } from 'node:http';
import { SESSION_COOKIE, type SessionPayload } from '../app.ts';
import { parseCookies, unseal } from '../cookies.ts';
import { CharacterError, cleanLook, cleanName, type Persistence } from './persistence.ts';

export interface GameDeps {
  persistence: Persistence;
  sessionSecret: string;
  appUrl: string;
  /** Is this character in a room right now? */
  isOnline(characterId: string): boolean;
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

/** Handles the character routes; false if the request isn't one of them. */
export function createCharacterRoutes(deps: GameDeps) {
  return async (req: IncomingMessage, res: ServerResponse): Promise<boolean> => {
    const path = (req.url ?? '/').split('?')[0];
    if (path !== PREFIX && !path.startsWith(`${PREFIX}/`)) return false;
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

      if (req.method === 'GET' && path === PREFIX) {
        return send(200, { characters: await deps.persistence.listCharacters(account.id) });
      }
      if (req.method === 'POST' && path === PREFIX) {
        // JSON only: a cross-site form can't send it without a preflight.
        if (!req.headers['content-type']?.startsWith('application/json')) throw new CharacterError('Send JSON.', 415);
        const body = (await readJson(req)) as { name?: unknown; look?: unknown };
        const name = cleanName(body.name);
        if (!name) throw new CharacterError('Names are 1 to 20 characters.');
        const created = await deps.persistence.createCharacter(account.id, name, cleanLook(body.look));
        return send(201, created);
      }
      if (req.method === 'DELETE' && path.startsWith(`${PREFIX}/`)) {
        const id = decodeURIComponent(path.slice(PREFIX.length + 1));
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
