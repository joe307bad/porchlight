// Missions: ink stories (https://www.inklestudios.com/ink/) in server/missions,
// compiled here and handed to the game, which runs them with the ink runtime.
// The files are read afresh on every request (compiled once per change), so a
// new or edited mission needs no game build: the sidebar's Refresh picks it up.
//
//   GET /mp/missions        { missions: [{ id, title, summary, error? }] }
//   GET /mp/missions/:id    { id, title, summary, story }   story: the compiled ink JSON, as text
//
// A mission names itself with tags at the top of its file:
//   # title: Moving day
//   # summary: Three neighbours need boxes run across town.
// The game's side of the story (its EXTERNAL functions and @ choices) is in
// server/missions/README.md.

import { readdir, readFile, stat } from 'node:fs/promises';
import type { IncomingMessage, ServerResponse } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Compiler, CompilerOptions, Story } from 'inkjs/full';

const PREFIX = '/mp/missions';
const ID = /^[a-z0-9][a-z0-9-]*$/;

export const DEFAULT_MISSIONS_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'missions');

export interface Mission {
  id: string;
  title: string;
  summary: string;
  /** The compiled story (ink's JSON), or null if it didn't compile. */
  story: string | null;
  /** Why it didn't compile (ink's own messages). */
  error: string | null;
}

/** Compile one mission's ink. Never throws: a broken file comes back with its errors. */
export function compileMission(id: string, source: string): Mission {
  const errors: string[] = [];
  let story: string | null = null;
  try {
    // ink's ErrorType: 0 author note, 1 warning, 2 error.
    const options = new CompilerOptions(null, [], false, (message: string, type: number) => {
      if (type === 2) errors.push(message);
    });
    story = new Compiler(source, options).Compile()?.ToJson() ?? null;
  } catch (err) {
    if (errors.length === 0) errors.push(err instanceof Error ? err.message : String(err));
  }
  if (errors.length > 0) story = null;
  const tags = new Map<string, string>();
  if (story) {
    for (const tag of new Story(story).globalTags ?? []) {
      const at = tag.indexOf(':');
      if (at > 0) tags.set(tag.slice(0, at).trim().toLowerCase(), tag.slice(at + 1).trim());
    }
  }
  return {
    id,
    title: tags.get('title') || id,
    summary: tags.get('summary') || '',
    story,
    error: errors.length > 0 ? errors.join('\n') : null,
  };
}

/** Reads and compiles the .ink files in a folder, keeping each compile until its file changes. */
export class MissionStore {
  private readonly cache = new Map<string, { modified: number; mission: Mission }>();
  readonly dir: string;

  constructor(dir: string) {
    this.dir = dir;
  }

  async list(): Promise<Mission[]> {
    let names: string[];
    try {
      names = await readdir(this.dir);
    } catch {
      return [];
    }
    const ids = names.filter((n) => n.endsWith('.ink')).map((n) => n.slice(0, -4)).filter((id) => ID.test(id)).sort();
    const missions = await Promise.all(ids.map((id) => this.get(id)));
    return missions.filter((m): m is Mission => m !== null);
  }

  async get(id: string): Promise<Mission | null> {
    if (!ID.test(id)) return null;
    const file = path.join(this.dir, `${id}.ink`);
    let modified: number;
    try {
      modified = (await stat(file)).mtimeMs;
    } catch {
      this.cache.delete(id);
      return null;
    }
    const cached = this.cache.get(id);
    if (cached && cached.modified === modified) return cached.mission;
    const mission = compileMission(id, await readFile(file, 'utf8'));
    if (mission.error) console.warn(`mission ${id} didn't compile:\n${mission.error}`);
    this.cache.set(id, { modified, mission });
    return mission;
  }
}

/** Handles the mission routes; false if the request isn't one of them. */
export function createMissionRoutes(store: MissionStore) {
  return async (req: IncomingMessage, res: ServerResponse): Promise<boolean> => {
    const route = (req.url ?? '/').split('?')[0];
    if (route !== PREFIX && !route.startsWith(`${PREFIX}/`)) return false;
    const send = (status: number, body: unknown): true => {
      res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      res.end(JSON.stringify(body));
      return true;
    };
    try {
      if (req.method !== 'GET') return send(405, { error: 'Only GET.' });
      if (route === PREFIX) {
        const missions = await store.list();
        return send(200, { missions: missions.map(({ id, title, summary, error }) => ({ id, title, summary, ...(error ? { error } : {}) })) });
      }
      const mission = await store.get(decodeURIComponent(route.slice(PREFIX.length + 1)));
      if (!mission) return send(404, { error: 'No such mission.' });
      if (!mission.story) return send(422, { error: `This mission has errors:\n${mission.error}` });
      return send(200, { id: mission.id, title: mission.title, summary: mission.summary, story: mission.story });
    } catch (err) {
      console.error(err);
      return send(500, { error: 'internal error' });
    }
  };
}
