// The street editor's routes (../../../docs/street-editor-api.md): Manage's edit screen keeps a draft of a
// town's streets here, has the draft worker (../sim/draft.ts) grow it as it goes, and commits it: a new
// revision of the town, which everyone playing there is moved over to.
//
//   GET    /mp/towns/skeleton?key=   the skeleton committed as this key (the game fetches it before growing a town with a `rev`)
//   GET    /mp/edit/towns            the towns this account can see, and which it can edit
//   GET    /mp/edit/draft?town=      its draft of the town (a new one, from the town as it is, if it has none)
//   PUT    /mp/edit/draft            { town, baseRev, skeleton, commands } keep it
//   DELETE /mp/edit/draft?town=      throw it away
//   POST   /mp/edit/plan             { town, skeleton, seq } the town grown with this skeleton, against the town as it is
//   POST   /mp/edit/day              { town, skeleton } a day of the draft's traffic, and of the town as it is
//   POST   /mp/edit/commit           { town, skeleton, message } the new revision (409 { problems } if it has errors)
//   GET    /mp/edit/history?town=    its revisions, newest first
//
// An editor of a town is whoever made it, or, for the towns that come with the game, an account in EDITORS
// (account ids, comma-separated; `*`: anyone signed in).
//
// Committing: the worker grows the town from scratch with every check (and refuses it with errors), then
// says where its houses are; in one transaction the revision's kept, the town's key takes its `rev`, and
// households whose house is gone are moved to the nearest free one (each told, by a notice, the next time
// they play: MSG_NOTICE on joining). Then each room of the old key says the town's rebuilding and, after
// `rebuildSeconds`, saves everyone and sends them to the new key (CLOSE_TOWN_REBUILT); the simulation and
// the county's maps follow the new key.

import type { IncomingMessage, ServerResponse } from 'node:http';
import { foreignOrigin, readJson, readSession, type GameDeps } from './accounts.ts';
import { openRooms } from './CityRoom.ts';
import { CharacterError, revOf, type EditTown } from './persistence.ts';
import type { DraftAnswer, DraftClient } from '../sim/draft.ts';
import { sim } from '../sim/link.ts';

/** How long players are given between the warning and the move (s). */
export const REBUILD_SECONDS = 10;
/** The most a skeleton and its commands may weigh (a town's every main road, pinned, is a few hundred KB at most). */
const BODY_LIMIT = 4 * 1024 * 1024;
const MESSAGE_MAX = 200;

/** EDITORS: who may edit the towns that come with the game. */
export function editorsFrom(raw: string | undefined): '*' | Set<string> {
  const list = (raw ?? '').split(',').map((s) => s.trim()).filter(Boolean);
  return list.includes('*') ? '*' : new Set(list);
}

export interface EditOptions {
  /** The draft worker; none: the routes that need it answer 503. */
  draft: DraftClient | null;
  /** The worker that runs days (a minute or more each), so plans never wait behind one; the draft worker if none. */
  days?: DraftClient | null;
  editors: '*' | Set<string>;
  rebuildSeconds?: number;
}

/** A skeleton as sent: an object (empty for none). */
const skeletonOf = (raw: unknown): Record<string, unknown> => {
  if (raw === null || raw === undefined) return {};
  if (typeof raw !== 'object' || Array.isArray(raw)) throw new CharacterError("That isn't a skeleton.");
  return raw as Record<string, unknown>;
};

export function createEditRoutes(deps: GameDeps, opts: EditOptions) {
  const committing = new Set<string>();
  const rebuildSeconds = opts.rebuildSeconds ?? REBUILD_SECONDS;
  const mayEdit = (town: EditTown, accountId: string): boolean =>
    town.createdBy === accountId || (town.createdBy === null && (opts.editors === '*' || opts.editors.has(accountId)));

  return async (req: IncomingMessage, res: ServerResponse): Promise<boolean> => {
    const url = new URL(req.url ?? '/', 'http://x');
    const path = url.pathname;
    if (path !== '/mp/towns/skeleton' && !path.startsWith('/mp/edit/')) return false;
    const send = (status: number, body: unknown): true => {
      if (status >= 500) console.warn(`${req.method} ${path} → ${status}: ${(body as { error?: string })?.error ?? ''}`);
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
      const p = deps.persistence;
      const method = req.method ?? 'GET';
      const body = async <T>(): Promise<T> => {
        if (!req.headers['content-type']?.startsWith('application/json')) throw new CharacterError('Send JSON.', 415);
        return (await readJson(req, BODY_LIMIT)) as T;
      };
      const worker = (): DraftClient => {
        if (!opts.draft) throw new CharacterError("There's no draft worker on this server.", 503);
        return opts.draft;
      };
      /** The town (by any revision's key) if this account may edit it; throws otherwise. */
      const editable = async (key: unknown): Promise<EditTown> => {
        if (typeof key !== 'string' || !key || key.length > 512) throw new CharacterError('Which town?');
        const town = await p.townOf(key);
        if (!town || !(await p.mayEnter(account.id, town.key))) throw new CharacterError('No such town.', 404);
        if (!mayEdit(town, account.id)) throw new CharacterError("You can't edit this town.", 403);
        return town;
      };
      /** The town's revision now, and its skeleton (null at revision 0). */
      const current = async (town: EditTown): Promise<{ rev: number; skeleton: unknown }> => {
        const rev = revOf(town.key);
        return { rev, skeleton: rev > 0 ? ((await p.skeletonOf(town.key))?.skeleton ?? null) : null };
      };

      if (path === '/mp/towns/skeleton') {
        if (method !== 'GET') return send(405, { error: 'Method not allowed.' });
        const key = url.searchParams.get('key') ?? '';
        const found = key ? await p.skeletonOf(key) : null;
        // Someone else's town's skeleton is as private as the town.
        if (!found || (found.createdBy !== null && found.createdBy !== account.id)) return send(404, { error: 'No skeleton for that key.' });
        return send(200, { key, rev: found.rev, skeleton: found.skeleton });
      }

      if (path === '/mp/edit/towns') {
        if (method !== 'GET') return send(405, { error: 'Method not allowed.' });
        const towns = await p.listTowns(account.id);
        return send(
          200,
          towns.map((t) => ({ key: t.key, name: t.name, rev: revOf(t.key), mine: t.mine, editable: t.mine || (t.place !== null && (opts.editors === '*' || opts.editors.has(account.id))) })),
        );
      }

      if (path === '/mp/edit/draft') {
        if (method === 'GET') {
          const town = await editable(url.searchParams.get('town'));
          const now = await current(town);
          let draft = await p.draft(town.id, account.id);
          if (!draft) {
            const skeleton = now.skeleton ?? { v: 1 };
            draft = { baseRev: now.rev, skeleton, commands: [], updatedAt: await p.saveDraft(town.id, account.id, now.rev, skeleton, []) };
          }
          return send(200, { town: town.key, baseRev: draft.baseRev, skeleton: draft.skeleton, commands: draft.commands, updatedAt: draft.updatedAt, stale: draft.baseRev !== now.rev, current: now });
        }
        if (method === 'PUT') {
          const b = await body<{ town?: unknown; baseRev?: unknown; skeleton?: unknown; commands?: unknown }>();
          const town = await editable(b.town);
          if (!Number.isInteger(b.baseRev) || (b.baseRev as number) < 0) throw new CharacterError('Which revision is it a draft of?');
          if (b.commands !== undefined && !Array.isArray(b.commands)) throw new CharacterError('Commands are a list.');
          const updatedAt = await p.saveDraft(town.id, account.id, b.baseRev as number, skeletonOf(b.skeleton), (b.commands as unknown[] | undefined) ?? []);
          return send(200, { updatedAt });
        }
        if (method === 'DELETE') {
          const town = await editable(url.searchParams.get('town'));
          await p.deleteDraft(town.id, account.id);
          return send(200, {});
        }
        return send(405, { error: 'Method not allowed.' });
      }

      if (path === '/mp/edit/history') {
        if (method !== 'GET') return send(405, { error: 'Method not allowed.' });
        const town = await editable(url.searchParams.get('town'));
        return send(200, await p.history(town.id));
      }

      if (method !== 'POST') return send(405, { error: 'Method not allowed.' });

      if (path === '/mp/edit/plan') {
        const b = await body<{ town?: unknown; skeleton?: unknown; seq?: unknown }>();
        const town = await editable(b.town);
        const now = await current(town);
        const answer = await worker().request(
          { op: 'plan', key: town.key, skeleton: skeletonOf(b.skeleton), base: { key: town.key, skeleton: now.skeleton } },
          `plan\n${account.id}\n${town.id}`,
        );
        return send(200, { ...answer, id: undefined, seq: b.seq ?? null });
      }

      if (path === '/mp/edit/day') {
        const b = await body<{ town?: unknown; skeleton?: unknown }>();
        const town = await editable(b.town);
        const now = await current(town);
        const w = opts.days ?? worker();
        // A second press while the first still waits replaces it.
        const [draft, live] = await Promise.all([
          w.request({ op: 'day', key: town.key, skeleton: skeletonOf(b.skeleton) }, `day-draft:${account.id}:${town.key}`),
          w.request({ op: 'day', key: town.key, skeleton: now.skeleton }, `day-live:${account.id}:${town.key}`),
        ]);
        const failed = [draft, live].find((a) => !a.ok);
        if (failed) return send(502, { error: failed.error ?? 'The day could not be run.' });
        return send(200, { draft: draft.day, live: live.day });
      }

      if (path === '/mp/edit/commit') {
        const b = await body<{ town?: unknown; skeleton?: unknown; message?: unknown }>();
        const town = await editable(b.town);
        const skeleton = skeletonOf(b.skeleton);
        const message = typeof b.message === 'string' ? b.message.replace(/\p{C}/gu, '').replace(/\s+/gu, ' ').trim().slice(0, MESSAGE_MAX) : '';
        if (committing.has(town.id)) return send(409, { error: 'Someone is committing this town right now.' });
        committing.add(town.id);
        try {
          const w = worker();
          const plan = await w.request({ op: 'plan', key: town.key, skeleton, full: true });
          if (!plan.ok) return send(400, { error: plan.error ?? 'The town could not be grown.' });
          const problems = (plan.problems as { level: string }[] | undefined) ?? [];
          if (problems.some((x) => x.level === 'error')) return send(409, { problems });
          const houses = await w.request({ op: 'houses', key: town.key, skeleton });
          if (!houses.ok || !Array.isArray(houses.houses)) return send(502, { error: houses.error ?? "The town's houses could not be found." });
          const done = await p.commit({ townId: town.id, key: town.key, skeleton, houses: houses.houses as [number, number][], message, by: account.id });
          await p.deleteDraft(town.id, account.id).catch(() => {});
          const players = deps.playingIn(town.key);
          rebuild(town, done.key, session.name);
          console.log(`edit: ${town.name} is now ${done.key} (${done.rehoused} household(s) rehoused, ${players} playing)`);
          return send(200, { key: done.key, rev: done.rev, rehoused: done.rehoused, players });
        } finally {
          committing.delete(town.id);
        }
      }

      return send(404, { error: 'Not found.' });
    } catch (err) {
      if (err instanceof CharacterError) return send(err.status, { error: err.message });
      console.error('edit:', err);
      return send(503, { error: "The game's database isn't answering. Try again in a moment." });
    }
  };

  /** The old key's rooms send everyone over; the county's rooms, the simulation and the maps take the new key. */
  function rebuild(town: EditTown, key: string, by: string): void {
    const rooms = [...openRooms()].filter((r) => r.town === town.key);
    for (const r of rooms) void r.rebuild({ name: town.name, by, key }, rebuildSeconds);
    // Nobody playing: where anyone last stood carries over now.
    if (!rooms.length) void deps.persistence.movePlaces(town.key, key).catch((err: Error) => console.error('edit: places:', err.message));
    for (const r of openRooms()) if (r.countyAccount === town.createdBy && r.town !== town.key) void r.loadCounty();
    void sim()?.refreshTowns();
  }
}

/** Plan answers as Manage gets them: the worker's own id left out. */
export type PlanAnswer = DraftAnswer & { seq: unknown };
