// Everything multiplayer keeps in Postgres (Drizzle; the tables are in
// tables.ts, the migrations in server/drizzle): accounts and what they've earned,
// their characters, where each character lives and was, towns, and sessions. Never touched from the room's tick: rooms
// read a character once on join and write in batches (every 30 s, on leave, on
// shutdown), and a failed write just leaves the data dirty for next time.
//
// MemoryPersistence is the same thing in memory: tests, and a server started
// without DATABASE_URL (multiplayer works, nothing outlives the process).

import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { NORTH, type Place } from '../county.ts';
import { and, asc, count, eq, gte, lt, inArray, isNotNull, isNull, ne, notInArray, or, sql } from 'drizzle-orm';
import { drizzle, type NodePgDatabase } from 'drizzle-orm/node-postgres';
import { migrate } from 'drizzle-orm/node-postgres/migrator';
import pg from 'pg';
import type { ClientInfo, SessionEnd } from './telemetry.ts';
import { accountUnlocks, characterState, characters, homes, missionCompletions, missionRecords, notices, players, sessions, townDrafts, townRevisions, towns } from './tables.ts';

export interface Account {
  id: string;
  banned: boolean;
}

export interface CharacterInfo {
  id: string;
  name: string;
  /** Character model, if they've been given one. */
  look: string | null;
  /** The town they live in, once they've chosen. */
  home: { townId: string; townName: string; townKey: string; house: { x: number; y: number } | null } | null;
}

export interface TownInfo {
  id: string;
  name: string;
  key: string;
  /** This account made it (and can delete it). */
  mine: boolean;
  /** How many characters live there. */
  residents: number;
  /** Where it is in the county (county.ts): a town that comes with the game's place, or one of the account's own slots (0-2). */
  place: Place | null;
  slot: number | null;
}

/** A house someone lives in, by a tile of its building. */
export interface HomeInfo {
  x: number;
  y: number;
  mine: boolean;
  residents: string[];
}

/** What an account has earned. */
export interface Progress {
  completions: { missionId: string; times: number; firstAt: string; lastAt: string; firstCharacter: string | null }[];
  unlocks: { what: string; at: string }[];
}

/** One of a town's best times for a mission: one per account, fastest first. */
export interface RecordRow {
  name: string;
  seconds: number;
  at: string;
  /** This account's. */
  mine: boolean;
}

/** A mission's board in one town: its fastest times, and this account's own best there. */
export interface Board {
  best: RecordRow[];
  mine: number | null;
}

/** How many times a board shows. */
export const BOARD_SIZE = 10;
/** A time to keep: a real number of seconds, from a few to a few hours. */
export const cleanSeconds = (raw: unknown): number | null =>
  typeof raw === 'number' && Number.isFinite(raw) && raw >= 1 && raw <= 6 * 3600 ? Math.round(raw * 100) / 100 : null;

export interface SavedState {
  town: string;
  x: number;
  y: number;
  z: number;
  rotY: number;
  look: string | null;
  version: number;
}

/** One character's saveable state at a moment. */
export interface Snapshot {
  characterId: string;
  town: string;
  x: number;
  y: number;
  z: number;
  rotY: number;
  look: string;
  /** Must exceed the stored version, or the write is skipped as stale. */
  version: number;
}

export type EndReason = 'consented' | 'timeout' | 'kicked' | 'shutdown' | 'crash' | 'rebuilt';

export const MAX_CHARACTERS = 8;
/** How many towns an account can have made at once: to make another, delete one. */
export const MAX_TOWNS = 3;
/** How many characters can live in a house. */
export const MAX_RESIDENTS = 2;
const NAME_MAX = 20;
export const TOWN_NAME_MAX = 24;
/** A town that comes with the game: only the game can generate it, so `npm run town` saves its houses in a file (towns/). */
export interface StarterTown {
  name: string;
  /** Where it is in the county: porchlight, east, south or west. */
  place: Place;
  key: string;
  /** A tile of each house. */
  houses: [number, number][];
}

/**
 * The towns that come with the game, the same for everyone (and nobody can delete them): Porchlight,
 * as big and built up as the map settings go, with no water and few cul-de-sacs, and the three cities
 * of the county round it, Lanternport (east), Ember Crossing (south) and Juniper Falls (west).
 */
export const STARTER_TOWNS: StarterTown[] = ['porchlight', 'lanternport', 'ember-crossing', 'juniper-falls'].map((f) =>
  JSON.parse(readFileSync(new URL(`../../towns/${f}.json`, import.meta.url), 'utf8')),
);

/** The revision a town's key names (its `rev`; docs/street-editor-api.md), or 0. */
export const revOf = (key: string): number => {
  const m = /(?:^|&)rev=(\d+)(?:&|$)/.exec(key);
  return m ? Number(m[1]) : 0;
};
/** The key without its `rev`: the town as its seed grows it, before any edit. */
export const stripRev = (key: string): string => key.split('&').filter((p) => p && !p.startsWith('rev=')).join('&');
/** The key with its `rev` set (left out at 0), at the end, as the game's Skeletons.WithRev writes it. */
export const withRev = (key: string, rev: number): string => (rev > 0 ? `${stripRev(key)}&rev=${rev}` : stripRev(key));

/** Each town that comes with the game's key now (its file's key, with the `rev` of its latest edit), by its file's key. */
const starterKeys = new Map<string, string>();
/** The towns that come with the game as they are now: an edited one's key has its `rev`. */
export const starterTowns = (): StarterTown[] => STARTER_TOWNS.map((t) => ({ ...t, key: starterKeys.get(t.key) ?? t.key }));
/** The town that comes with the game whose key this is (any revision of it), or null. */
export const starterOf = (key: string): StarterTown | null => STARTER_TOWNS.find((t) => t.key === stripRev(key)) ?? null;
/** A town that comes with the game has this key now (it was edited, or settled on starting). */
export function setStarterKey(fileKey: string, key: string): void {
  if (key === fileKey) starterKeys.delete(fileKey);
  else starterKeys.set(fileKey, key);
}

const placeOf = (key: string): Place | null => starterOf(key)?.place ?? null;
/** The first of an account's three places that's free (or null: all taken). */
const freeSlot = (taken: (number | null)[]): number | null => {
  for (let s = 0; s < MAX_TOWNS; s++) if (!taken.includes(s)) return s;
  return null;
};
const newTownKey = (): string => `seed=${crypto.randomUUID().replaceAll('-', '').slice(0, 12)}`;
const tooManyTowns = () => new CharacterError(`You can have up to ${MAX_TOWNS} towns. Delete one to make another.`, 409);
const houseTaken = () => new CharacterError('Someone else lives in that house.', 409);
const houseFull = () => new CharacterError(`That house already has ${MAX_RESIDENTS} people living in it.`, 409);

export class CharacterError extends Error {
  readonly status: number;
  constructor(message: string, status = 400) {
    super(message);
    this.status = status;
  }
}

/** A character name: printable, single-spaced, trimmed, 1-20 characters; or null. */
export function cleanName(raw: unknown, max = NAME_MAX): string | null {
  if (typeof raw !== 'string') return null;
  // Control characters out, runs of whitespace to one space.
  const name = raw.replace(/\p{C}/gu, '').replace(/\s+/gu, ' ').trim();
  return name.length >= 1 && name.length <= max ? name : null;
}

/** A character model as the app names them (e.g. `characters/character-female-a`), or null. */
export function cleanLook(raw: unknown): string | null {
  return typeof raw === 'string' && /^[a-z0-9][a-z0-9/_-]{0,63}$/.test(raw) && !raw.includes('..') ? raw : null;
}

/**
 * A town's key as the game writes it (its seed, then the settings that differ
 * from the defaults, form-encoded), or null. The game grows the town from it; here it's only a name.
 */
export function cleanTownKey(raw: unknown): string | null {
  return typeof raw === 'string' && raw.length <= 512 && /^seed=[A-Za-z0-9*._%+-]+(&[A-Za-z0-9]+=[0-9.eE+-]+)*$/.test(raw) ? raw : null;
}

/** Someone has this very town. What it's called is theirs to know, unless it's yours or comes with the game. */
const townExists = (name: string | null) => new CharacterError(name ? `That town already exists: it's called ${name}.` : 'That town has been made already. Try another seed.', 409);
/** Where a town comes with the game, its houses are known: only the tile each goes by will do (so a house can't be claimed twice, by two of its tiles). */
const starterHouses = new Map(STARTER_TOWNS.map((t) => [t.key, new Set(t.houses.map(([x, y]) => `${x},${y}`))]));
/** An edited town's houses are its revision's (`revised`: the draft worker's, kept with it). */
const checkHouse = (townKey: string, x: number, y: number, revised: [number, number][] | null = null): void => {
  if (revOf(townKey) > 0) {
    if (revised && !revised.some(([hx, hy]) => hx === x && hy === y)) throw new CharacterError("There's no house there.", 404);
    return;
  }
  if (starterHouses.get(townKey)?.has(`${x},${y}`) === false) throw new CharacterError("There's no house there.", 404);
};

/** A town as the street editor sees it. */
export interface EditTown {
  id: string;
  name: string;
  key: string;
  createdBy: string | null;
}

/** An editor's work in progress on a town (docs/street-editor-api.md). */
export interface TownDraft {
  baseRev: number;
  skeleton: unknown;
  commands: unknown[];
  updatedAt: string;
}

export interface Revision {
  rev: number;
  message: string;
  /** Who committed it (their account's name), or null if they've gone. */
  by: string | null;
  at: string;
}

export interface Commit {
  townId: string;
  /** The key it was planned against: if the town's changed since, the commit's turned down. */
  key: string;
  skeleton: unknown;
  /** Each house's tile in the new revision (the draft worker's `houses`). */
  houses: [number, number][];
  message: string;
  by: string;
}

export interface Committed {
  key: string;
  rev: number;
  /** Households moved to another house (or, with none free, out of theirs). */
  rehoused: number;
  /** The characters whose house moved (each told by a notice). */
  moved: string[];
}

/** What a character whose house moved is told the next time they play. */
export const movedNotice = (town: string, to: [number, number] | null): string =>
  to
    ? `The streets of ${town} were changed and your house is gone: you live in the nearest free one now.`
    : `The streets of ${town} were changed and your house is gone, and there was no house free: choose a new one.`;

/**
 * Where each household goes when a town's houses change: those whose tile is still a house stay; each of
 * the rest, north to south, goes to the nearest house still free, or nowhere if
 * none is left. Returns the households that move, by their key, with their new tile (null: none).
 */
export function rehouse<K>(households: { key: K; x: number; y: number }[], houses: [number, number][]): Map<K, [number, number] | null> {
  const tile = (x: number, y: number) => `${x},${y}`;
  const isHouse = new Set(houses.map(([x, y]) => tile(x, y)));
  const staying = new Set(households.filter((h) => isHouse.has(tile(h.x, h.y))).map((h) => tile(h.x, h.y)));
  const free = houses.filter(([x, y]) => !staying.has(tile(x, y)));
  const moving = households.filter((h) => !isHouse.has(tile(h.x, h.y))).sort((a, b) => a.y - b.y || a.x - b.x);
  const out = new Map<K, [number, number] | null>();
  for (const h of moving) {
    let best = -1;
    let bestD = Infinity;
    for (let i = 0; i < free.length; i++) {
      const d = (free[i][0] - h.x) ** 2 + (free[i][1] - h.y) ** 2;
      if (d < bestD || (d === bestD && (free[i][1] < free[best][1] || (free[i][1] === free[best][1] && free[i][0] < free[best][0])))) {
        bestD = d;
        best = i;
      }
    }
    out.set(h.key, best >= 0 ? free.splice(best, 1)[0] : null);
  }
  return out;
}

export interface Persistence {
  /** Migrated and reachable at least once: until then joins are refused. */
  readonly ready: boolean;
  /** Get ready (migrations, sessions a crash left open), retrying until it can. */
  start(): void;
  account(sub: string, name: string): Promise<Account>;
  listCharacters(accountId: string): Promise<CharacterInfo[]>;
  createCharacter(accountId: string, name: string, look: string | null): Promise<CharacterInfo>;
  deleteCharacter(accountId: string, characterId: string): Promise<boolean>;
  /** The character, if it's this account's. */
  character(accountId: string, characterId: string): Promise<CharacterInfo | null>;
  loadState(characterId: string): Promise<SavedState | null>;
  save(rows: Snapshot[]): Promise<void>;
  /** What the account has unlocked, in the order it did. */
  unlocks(accountId: string): Promise<string[]>;
  /** Keep that the account has unlocked `what` (once). */
  unlock(accountId: string, what: string): Promise<void>;
  /** Count a mission played to its end. */
  completeMission(accountId: string, characterId: string, missionId: string): Promise<void>;
  progress(accountId: string): Promise<Progress>;
  /** The mission's board in this town (its key). */
  records(accountId: string, townKey: string, missionId: string): Promise<Board>;
  /** Keep a time the character ran, if it's the account's best in this town; the board after, and the account's best before. */
  keepRecord(accountId: string, characterId: string, townKey: string, missionId: string, seconds: number): Promise<Board & { before: number | null }>;
  /** The towns this account can live in: the ones that come with the game (anyone's), and the ones it made (only its own). */
  listTowns(accountId: string): Promise<TownInfo[]>;
  /** Every town in a county (those that come with the game, and each account's placed in one of its slots): the server runs their traffic (sim/). */
  countyTownKeys(): Promise<string[]>;
  /** Whether the account may play in the town with this key: one that comes with the game, one it made, or one no account has made. */
  mayEnter(accountId: string, townKey: string): Promise<boolean>;
  /** The town's key if this account made it; throws otherwise (or if it's gone). */
  ownTownKey(accountId: string, townId: string): Promise<string>;
  /**
   * Whose county a room (a town's key, or `north:<account>`) is in, and that account's own towns
   * by slot: null account for the towns that come with the game; null for a town nobody's saved.
   */
  countyFor(room: string): Promise<{ account: string | null; mine: { key: string; slot: number }[] } | null>;
  /** A new town: this key (from the town editor), or a fresh seed's. */
  createTown(accountId: string, name: string, key?: string): Promise<TownInfo>;
  /** Everyone who lived there starts again from choosing a town (what they've earned stays). */
  deleteTown(accountId: string, townId: string): Promise<void>;
  listHomes(accountId: string, townId: string): Promise<HomeInfo[]>;
  /** Move the character to this town, out of any house they had in another. */
  moveToTown(accountId: string, characterId: string, townId: string): Promise<void>;
  /** Move the character into the house at this tile of their town. */
  claimHome(accountId: string, characterId: string, x: number, y: number): Promise<void>;
  /** A session starts: which room, and what it's played on (telemetry.ts). */
  openSession(characterId: string, roomId: string, client?: ClientInfo): Promise<string>;
  /** It ends (once: a second close is ignored): why, more on why, and how it ran. */
  closeSession(sessionId: string, characterId: string, reason: EndReason, playSeconds: number, end?: SessionEnd): Promise<void>;

  // The street editor (docs/street-editor-api.md; edit.ts).
  /** The town this key is any revision of (its key now may name a later one), or null. */
  townOf(key: string): Promise<EditTown | null>;
  /** The skeleton committed as this very key, and whose town it is; null if none was. */
  skeletonOf(key: string): Promise<{ rev: number; skeleton: unknown; createdBy: string | null } | null>;
  draft(townId: string, author: string): Promise<TownDraft | null>;
  /** Keep an editor's draft; when it was kept. */
  saveDraft(townId: string, author: string, baseRev: number, skeleton: unknown, commands: unknown[]): Promise<string>;
  deleteDraft(townId: string, author: string): Promise<void>;
  /** The town's revisions, newest first. */
  history(townId: string): Promise<Revision[]>;
  /**
   * In one go, the town's row locked: the next revision, the town's key with its `rev`, and each household whose
   * house is gone moved to the nearest free one (rehouse), each of them told by a notice.
   */
  commit(c: Commit): Promise<Committed>;
  /** Where characters last stood in the town as one key is where they stand in it as another (a revision on). */
  movePlaces(from: string, to: string): Promise<void>;
  /** What the character's to be told, now told (gone). */
  takeNotices(characterId: string): Promise<string[]>;
  close(): Promise<void>;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isUuid = (v: unknown): v is string => typeof v === 'string' && UUID.test(v);

/** Where drizzle-kit writes the migrations (server/drizzle), applied on start. */
export const MIGRATIONS = fileURLToPath(new URL('../../drizzle', import.meta.url));

/** A character's look, read out of the appearance JSON. */
const lookOf = sql<string | null>`${characterState.appearance}->>'look'`;
/** Everything CharacterInfo is made of, from the query `characterRows` starts. */
const characterColumns = {
  id: characters.id,
  name: characters.name,
  look: lookOf,
  townId: towns.id,
  townName: towns.name,
  townKey: towns.key,
  houseX: homes.x,
  houseY: homes.y,
};

interface CharacterRow {
  id: string;
  name: string;
  look: string | null;
  townId: string | null;
  townName: string | null;
  townKey: string | null;
  houseX: number | null;
  houseY: number | null;
}

const toCharacter = (r: CharacterRow): CharacterInfo => ({
  id: r.id,
  name: r.name,
  look: r.look,
  home: r.townId
    ? { townId: r.townId, townName: r.townName!, townKey: r.townKey!, house: r.houseX !== null && r.houseY !== null ? { x: r.houseX, y: r.houseY } : null }
    : null,
});

const noSuchCharacter = () => new CharacterError('No such character.', 404);
const noSuchTown = () => new CharacterError('No such town.', 404);

export class PgPersistence implements Persistence {
  ready = false;
  private readonly pool: pg.Pool;
  private readonly db: NodePgDatabase;
  private closed = false;

  constructor(url: string) {
    // Its own small pool, so the game can't starve anything else of connections.
    this.pool = new pg.Pool({
      connectionString: url,
      max: 5,
      connectionTimeoutMillis: 3_000,
      idleTimeoutMillis: 30_000,
      statement_timeout: 5_000,
    });
    this.pool.on('error', (err) => console.error('game db:', err.message));
    this.db = drizzle(this.pool);
  }

  start(): void {
    const attempt = async (delay: number): Promise<void> => {
      if (this.closed) return;
      try {
        await migrate(this.db, { migrationsFolder: MIGRATIONS });
        // One process, so any session still open is from one that died.
        const crashed = await this.db
          .update(sessions)
          .set({ endedAt: sql`now()`, endReason: 'crash' })
          .where(isNull(sessions.endedAt))
          .returning({ id: sessions.id });
        if (crashed.length) console.log(`game db: closed ${crashed.length} session(s) left open by a crash`);
        for (const town of STARTER_TOWNS) await this.settleStarterTown(town);
        // Only starter towns have no maker: any that no longer come with the game go, and those who lived there choose again.
        await this.db.delete(towns).where(and(isNull(towns.createdBy), notInArray(towns.name, STARTER_TOWNS.map((t) => t.name))));
        this.ready = true;
        console.log('game db: ready');
      } catch (err) {
        console.error(`game db not ready (${(err as Error).message}); retrying in ${delay / 1000}s`);
        setTimeout(() => void attempt(Math.min(delay * 2, 60_000)), delay).unref();
      }
    };
    void attempt(2_000);
  }

  async account(sub: string, name: string): Promise<Account> {
    const [row] = await this.db
      .insert(players)
      .values({ authSub: sub, name })
      .onConflictDoUpdate({ target: players.authSub, set: { name } })
      .returning({ id: players.id, bannedAt: players.bannedAt });
    return { id: row.id, banned: row.bannedAt !== null };
  }

  private characterRows() {
    return this.db
      .select(characterColumns)
      .from(characters)
      .leftJoin(characterState, eq(characterState.characterId, characters.id))
      .leftJoin(towns, eq(towns.id, characters.townId))
      .leftJoin(homes, eq(homes.id, characters.homeId));
  }

  async listCharacters(accountId: string): Promise<CharacterInfo[]> {
    const rows = await this.characterRows().where(eq(characters.playerId, accountId)).orderBy(characters.createdAt);
    return rows.map(toCharacter);
  }

  async createCharacter(accountId: string, name: string, look: string | null): Promise<CharacterInfo> {
    return this.db.transaction(async (tx) => {
      // Lock the account row so two creates at once can't both slip under the limit.
      await tx.select({ id: players.id }).from(players).where(eq(players.id, accountId)).for('update');
      const [{ n }] = await tx.select({ n: count() }).from(characters).where(eq(characters.playerId, accountId));
      if (n >= MAX_CHARACTERS) throw new CharacterError(`You can have up to ${MAX_CHARACTERS} characters.`, 409);
      const inserted = await tx
        .insert(characters)
        .values({ playerId: accountId, name })
        .onConflictDoNothing()
        .returning({ id: characters.id });
      if (!inserted.length) throw new CharacterError(`You already have a character called ${name}.`, 409);
      const id = inserted[0].id;
      await tx.insert(characterState).values({ characterId: id, appearance: look ? { look } : {} });
      return { id, name, look, home: null };
    });
  }

  async deleteCharacter(accountId: string, characterId: string): Promise<boolean> {
    if (!isUuid(characterId)) return false;
    return this.db.transaction(async (tx) => {
      const gone = await tx
        .delete(characters)
        .where(and(eq(characters.id, characterId), eq(characters.playerId, accountId)))
        .returning({ homeId: characters.homeId });
      if (!gone.length) return false;
      await PgPersistence.emptyHouse(tx, gone[0].homeId);
      return true;
    });
  }

  /** A house with nobody left in it is free for anyone again. */
  private static async emptyHouse(tx: Pick<NodePgDatabase, 'delete' | 'select'>, homeId: string | null): Promise<void> {
    if (!homeId) return;
    await tx.delete(homes).where(and(eq(homes.id, homeId), sql`not exists (select 1 from ${characters} where ${characters.homeId} = ${homeId})`));
  }

  async character(accountId: string, characterId: string): Promise<CharacterInfo | null> {
    if (!isUuid(characterId)) return null;
    const [row] = await this.characterRows().where(and(eq(characters.id, characterId), eq(characters.playerId, accountId)));
    return row ? toCharacter(row) : null;
  }

  async loadState(characterId: string): Promise<SavedState | null> {
    const [r] = await this.db
      .select({
        town: characterState.town,
        x: characterState.posX,
        y: characterState.posY,
        z: characterState.posZ,
        rotY: characterState.rotY,
        look: lookOf,
        version: characterState.version,
      })
      .from(characterState)
      .where(eq(characterState.characterId, characterId));
    return r ?? null;
  }

  async unlocks(accountId: string): Promise<string[]> {
    const rows = await this.db.select({ what: accountUnlocks.what }).from(accountUnlocks).where(eq(accountUnlocks.playerId, accountId)).orderBy(asc(accountUnlocks.unlockedAt));
    return rows.map((r) => r.what);
  }

  async unlock(accountId: string, what: string): Promise<void> {
    await this.db.insert(accountUnlocks).values({ playerId: accountId, what }).onConflictDoNothing();
  }

  async completeMission(accountId: string, characterId: string, missionId: string): Promise<void> {
    await this.db
      .insert(missionCompletions)
      .values({ playerId: accountId, missionId, firstCharacterId: characterId })
      .onConflictDoUpdate({
        target: [missionCompletions.playerId, missionCompletions.missionId],
        set: { times: sql`${missionCompletions.times} + 1`, lastAt: sql`now()` },
      });
  }

  async progress(accountId: string): Promise<Progress> {
    const completions = await this.db
      .select({ missionId: missionCompletions.missionId, times: missionCompletions.times, firstAt: missionCompletions.firstAt, lastAt: missionCompletions.lastAt, firstCharacter: characters.name })
      .from(missionCompletions)
      .leftJoin(characters, eq(characters.id, missionCompletions.firstCharacterId))
      .where(eq(missionCompletions.playerId, accountId));
    const unlocks = await this.db.select({ what: accountUnlocks.what, at: accountUnlocks.unlockedAt }).from(accountUnlocks).where(eq(accountUnlocks.playerId, accountId)).orderBy(asc(accountUnlocks.unlockedAt));
    return {
      completions: completions.map((c) => ({ ...c, firstAt: c.firstAt.toISOString(), lastAt: c.lastAt.toISOString() })),
      unlocks: unlocks.map((u) => ({ what: u.what, at: u.at.toISOString() })),
    };
  }

  async records(accountId: string, townKey: string, missionId: string): Promise<Board> {
    const where = and(eq(missionRecords.townKey, townKey), eq(missionRecords.missionId, missionId));
    const rows = await this.db
      .select({ name: missionRecords.name, seconds: missionRecords.seconds, at: missionRecords.at, playerId: missionRecords.playerId })
      .from(missionRecords)
      .where(where)
      .orderBy(asc(missionRecords.seconds), asc(missionRecords.at))
      .limit(BOARD_SIZE);
    const [mine] = await this.db.select({ seconds: missionRecords.seconds }).from(missionRecords).where(and(where, eq(missionRecords.playerId, accountId)));
    return {
      best: rows.map((r) => ({ name: r.name, seconds: r.seconds, at: r.at.toISOString(), mine: r.playerId === accountId })),
      mine: mine?.seconds ?? null,
    };
  }

  async keepRecord(accountId: string, characterId: string, townKey: string, missionId: string, seconds: number): Promise<Board & { before: number | null }> {
    const [who] = await this.db.select({ name: characters.name }).from(characters).where(and(eq(characters.id, characterId), eq(characters.playerId, accountId)));
    if (!who) throw noSuchCharacter();
    const before = (await this.records(accountId, townKey, missionId)).mine;
    await this.db
      .insert(missionRecords)
      .values({ townKey, missionId, playerId: accountId, characterId, name: who.name, seconds })
      .onConflictDoUpdate({
        target: [missionRecords.townKey, missionRecords.missionId, missionRecords.playerId],
        set: { seconds, characterId, name: who.name, at: sql`now()` },
        setWhere: lt(sql`excluded.seconds`, missionRecords.seconds),
      });
    return { ...(await this.records(accountId, townKey, missionId)), before };
  }

  async listTowns(accountId: string): Promise<TownInfo[]> {
    // Spelled out: in a one-table select drizzle writes columns without their table, which inside
    // the subquery made "id" the character's own, so every town counted 0 residents.
    const rows = await this.db
      .select({ id: towns.id, name: towns.name, key: towns.key, createdBy: towns.createdBy, slot: towns.slot, residents: sql<number>`(select count(*)::int from "characters" where "characters"."town_id" = "towns"."id")` })
      .from(towns)
      .where(or(isNull(towns.createdBy), eq(towns.createdBy, accountId)))
      .orderBy(sql`${towns.createdBy} is not null`, towns.createdAt, towns.name);
    return rows.map(({ createdBy, ...t }) => ({ ...t, mine: createdBy === accountId, place: createdBy === null ? placeOf(t.key) : null }));
  }

  async countyTownKeys(): Promise<string[]> {
    const rows = await this.db
      .select({ key: towns.key })
      .from(towns)
      .where(or(isNull(towns.createdBy), and(isNotNull(towns.slot), gte(towns.slot, 0), lt(towns.slot, MAX_TOWNS))))
      .orderBy(sql`${towns.createdBy} is not null`, towns.createdAt);
    return [...new Set(rows.map((r) => r.key))];
  }

  async mayEnter(accountId: string, townKey: string): Promise<boolean> {
    if (townKey.startsWith(`${NORTH}:`)) return townKey === `${NORTH}:${accountId}`;
    const [town] = await this.db.select({ createdBy: towns.createdBy }).from(towns).where(eq(towns.key, townKey));
    return !town || town.createdBy === null || town.createdBy === accountId;
  }

  /** Throws unless the town is there and this account may live in it (it comes with the game, or the account made it). */
  private async visibleTown(db: Pick<NodePgDatabase, 'select'>, accountId: string, townId: string): Promise<void> {
    const [town] = await db.select({ createdBy: towns.createdBy }).from(towns).where(eq(towns.id, townId));
    if (!town || (town.createdBy !== null && town.createdBy !== accountId)) throw noSuchTown();
  }

  async ownTownKey(accountId: string, townId: string): Promise<string> {
    if (!isUuid(townId)) throw noSuchTown();
    const [town] = await this.db.select({ key: towns.key, createdBy: towns.createdBy }).from(towns).where(eq(towns.id, townId));
    if (!town) throw noSuchTown();
    if (town.createdBy !== accountId) throw new CharacterError('Only whoever made a town can delete it.', 403);
    return town.key;
  }

  async countyFor(room: string): Promise<{ account: string | null; mine: { key: string; slot: number }[] } | null> {
    if (starterOf(room)) return { account: null, mine: [] };
    let account: string | null = null;
    if (room.startsWith(`${NORTH}:`)) account = room.slice(NORTH.length + 1);
    else {
      const [town] = await this.db.select({ createdBy: towns.createdBy }).from(towns).where(eq(towns.key, room));
      account = town?.createdBy ?? null;
    }
    if (!account || !isUuid(account)) return null;
    const rows = await this.db.select({ key: towns.key, slot: towns.slot }).from(towns).where(eq(towns.createdBy, account));
    return { account, mine: rows.filter((r): r is { key: string; slot: number } => r.slot !== null) };
  }

  async createTown(accountId: string, name: string, key?: string): Promise<TownInfo> {
    return this.db.transaction(async (tx) => {
      // Lock the account row so two creates at once can't both slip under the limit.
      await tx.select({ id: players.id }).from(players).where(eq(players.id, accountId)).for('update');
      const mine = await tx.select({ slot: towns.slot }).from(towns).where(eq(towns.createdBy, accountId));
      const slot = freeSlot(mine.map((t) => t.slot));
      if (mine.length >= MAX_TOWNS || slot === null) throw tooManyTowns();
      // A revision of a town is that town: its key (with or without a `rev`) is taken.
      if (key !== undefined) {
        if (revOf(key) > 0) throw townExists(null);
        const [edited] = await tx.select({ name: towns.name, createdBy: towns.createdBy }).from(towns).where(sql`starts_with(${towns.key}, ${`${key}&rev=`})`);
        if (edited) throw townExists(edited.createdBy === null || edited.createdBy === accountId ? edited.name : null);
      }
      const [town] = await tx.insert(towns).values({ name, key: key ?? newTownKey(), createdBy: accountId, slot }).onConflictDoNothing({ target: towns.key }).returning({ id: towns.id, name: towns.name, key: towns.key, slot: towns.slot });
      if (!town) {
        const [same] = await tx.select({ name: towns.name, createdBy: towns.createdBy }).from(towns).where(eq(towns.key, key ?? ''));
        throw townExists(same && (same.createdBy === null || same.createdBy === accountId) ? same.name : null);
      }
      return { ...town, mine: true, residents: 0, place: null };
    });
  }

  async deleteTown(accountId: string, townId: string): Promise<void> {
    if (!isUuid(townId)) throw noSuchTown();
    // Its houses go with it, and those who lived there have no town or house (the foreign keys do it).
    const gone = await this.db.delete(towns).where(and(eq(towns.id, townId), eq(towns.createdBy, accountId))).returning({ id: towns.id });
    if (!gone.length) throw noSuchTown();
  }

  async listHomes(accountId: string, townId: string): Promise<HomeInfo[]> {
    if (!isUuid(townId)) throw noSuchTown();
    await this.visibleTown(this.db, accountId, townId);
    const rows = await this.db
      .select({ x: homes.x, y: homes.y, playerId: homes.playerId, name: characters.name })
      .from(homes)
      .innerJoin(characters, eq(characters.homeId, homes.id))
      .where(eq(homes.townId, townId))
      .orderBy(homes.x, homes.y, characters.createdAt);
    const byTile = new Map<string, HomeInfo>();
    for (const r of rows) {
      const key = `${r.x},${r.y}`;
      const home = byTile.get(key) ?? { x: r.x, y: r.y, mine: r.playerId === accountId, residents: [] };
      home.residents.push(r.name);
      byTile.set(key, home);
    }
    return [...byTile.values()];
  }

  async moveToTown(accountId: string, characterId: string, townId: string): Promise<void> {
    if (!isUuid(characterId)) throw noSuchCharacter();
    if (!isUuid(townId)) throw noSuchTown();
    await this.db.transaction(async (tx) => {
      const [c] = await tx.select({ townId: characters.townId, homeId: characters.homeId }).from(characters).where(and(eq(characters.id, characterId), eq(characters.playerId, accountId))).for('update');
      if (!c) throw noSuchCharacter();
      if (c.townId === townId) return;
      await this.visibleTown(tx, accountId, townId);
      await tx.update(characters).set({ townId, homeId: null }).where(eq(characters.id, characterId));
      await PgPersistence.emptyHouse(tx, c.homeId);
    });
  }

  /**
   * On starting, nobody's playing: a town that comes with the game, up to date with its file. If it's
   * been regenerated, those who lived there move to a new house each (a household together, wherever
   * the house falls), and where anyone was standing there no longer means anything.
   *
   * A town an editor's changed (its key has a `rev`) is never put back as its file has it: with the same
   * file key it stays as it is (its houses are its revision's, not the file's); if the file's key changed,
   * the new key takes the edit's `rev` and skeleton, and its households are rehoused as above.
   */
  async settleStarterTown(town: StarterTown): Promise<void> {
    const layout = createHash('sha256').update(JSON.stringify([town.key, town.houses])).digest('hex');
    let now = town.key;
    await this.db.transaction(async (tx) => {
      const [was] = await tx.select({ id: towns.id, key: towns.key, layout: towns.layout }).from(towns).where(and(isNull(towns.createdBy), eq(towns.name, town.name))).for('update');
      const rev = was ? revOf(was.key) : 0;
      if (was && rev > 0 && stripRev(was.key) === town.key) {
        now = was.key;
        if (was.layout !== layout) await tx.update(towns).set({ layout }).where(eq(towns.id, was.id));
        return;
      }
      if (was?.layout === layout) {
        now = was.key;
        return;
      }
      const key = withRev(town.key, rev);
      const [clash] = await tx.select({ name: towns.name }).from(towns).where(was ? and(eq(towns.key, key), ne(towns.id, was.id)) : eq(towns.key, key));
      if (clash) {
        // A player's already made this very town: keep what we have rather than not start.
        console.error(`game db: can't regenerate ${town.name}: its key is already the town ${clash.name}`);
        if (was) now = was.key;
        return;
      }
      now = key;
      if (!was) {
        await tx.insert(towns).values({ name: town.name, key, layout });
        return;
      }
      await tx.update(towns).set({ key, layout }).where(eq(towns.id, was.id));
      // The edit carries over: its skeleton fetched by the new key, its houses the file's (as they're rehoused below).
      if (rev > 0) await tx.update(townRevisions).set({ key, houses: town.houses }).where(and(eq(townRevisions.townId, was.id), eq(townRevisions.rev, rev)));
      const residents = await tx.select({ character: characters.id, home: homes.id, playerId: homes.playerId }).from(characters).innerJoin(homes, eq(characters.homeId, homes.id)).where(eq(homes.townId, was.id));
      const households = new Map<string, { playerId: string; characters: string[] }>();
      for (const r of residents) {
        const h = households.get(r.home) ?? { playerId: r.playerId, characters: [] };
        h.characters.push(r.character);
        households.set(r.home, h);
      }
      await tx.delete(homes).where(eq(homes.townId, was.id));
      const free = [...town.houses];
      for (let i = free.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [free[i], free[j]] = [free[j], free[i]];
      }
      let housed = 0;
      for (const h of households.values()) {
        const tile = free.pop();
        if (!tile) break; // more households than houses: the rest choose again
        const [home] = await tx.insert(homes).values({ townId: was.id, x: tile[0], y: tile[1], playerId: h.playerId }).returning({ id: homes.id });
        await tx.update(characters).set({ homeId: home.id }).where(inArray(characters.id, h.characters));
        housed++;
      }
      await tx.update(characterState).set({ town: '' }).where(inArray(characterState.town, [was.key, key]));
      console.log(`game db: ${town.name} regenerated: ${housed} of ${households.size} household(s) rehoused`);
    });
    setStarterKey(town.key, now);
  }

  async claimHome(accountId: string, characterId: string, x: number, y: number): Promise<void> {
    if (!isUuid(characterId)) throw noSuchCharacter();
    await this.db.transaction(async (tx) => {
      // Lock the account row so its characters move in one at a time: a house
      // belongs to one account, so nobody else can be filling this one.
      await tx.select({ id: players.id }).from(players).where(eq(players.id, accountId)).for('update');
      const [c] = await tx.select({ townId: characters.townId, homeId: characters.homeId }).from(characters).where(and(eq(characters.id, characterId), eq(characters.playerId, accountId)));
      if (!c) throw noSuchCharacter();
      if (!c.townId) throw new CharacterError('Choose a town first.', 409);
      const [town] = await tx.select({ key: towns.key }).from(towns).where(eq(towns.id, c.townId));
      if (town) {
        const [revised] = revOf(town.key) > 0 ? await tx.select({ houses: townRevisions.houses }).from(townRevisions).where(eq(townRevisions.key, town.key)) : [];
        checkHouse(town.key, x, y, revised?.houses ?? null);
      }
      await tx.insert(homes).values({ townId: c.townId, x, y, playerId: accountId }).onConflictDoNothing();
      const [home] = await tx.select({ id: homes.id, playerId: homes.playerId }).from(homes).where(and(eq(homes.townId, c.townId), eq(homes.x, x), eq(homes.y, y)));
      // Gone already (its last resident moved out between those two lines): they can try again.
      if (!home) throw houseTaken();
      if (home.playerId !== accountId) throw houseTaken();
      if (home.id === c.homeId) return;
      const [{ n }] = await tx.select({ n: count() }).from(characters).where(eq(characters.homeId, home.id));
      if (n >= MAX_RESIDENTS) throw houseFull();
      await tx.update(characters).set({ homeId: home.id }).where(eq(characters.id, characterId));
      await PgPersistence.emptyHouse(tx, c.homeId);
    });
  }

  /** One statement for the lot; rows whose stored version is newer are left alone. */
  async save(rows: Snapshot[]): Promise<void> {
    if (!rows.length) return;
    await this.db
      .insert(characterState)
      .values(
        rows.map((r) => ({
          characterId: r.characterId,
          town: r.town,
          posX: r.x,
          posY: r.y,
          posZ: r.z,
          rotY: r.rotY,
          appearance: r.look ? { look: r.look } : {},
          version: r.version,
        })),
      )
      .onConflictDoUpdate({
        target: characterState.characterId,
        set: {
          town: sql`excluded.town`,
          posX: sql`excluded.pos_x`,
          posY: sql`excluded.pos_y`,
          posZ: sql`excluded.pos_z`,
          rotY: sql`excluded.rot_y`,
          // Merged, so a save without a look keeps the one they have.
          appearance: sql`${characterState.appearance} || excluded.appearance`,
          version: sql`excluded.version`,
          updatedAt: sql`now()`,
        },
        setWhere: sql`${characterState.version} < excluded.version`,
      });
  }

  async openSession(characterId: string, roomId: string, client?: ClientInfo): Promise<string> {
    const [row] = await this.db.insert(sessions).values({ characterId, roomId, client: client ?? null }).returning({ id: sessions.id });
    return row.id;
  }

  async closeSession(sessionId: string, characterId: string, reason: EndReason, playSeconds: number, end: SessionEnd = {}): Promise<void> {
    await this.db.transaction(async (tx) => {
      const closed = await tx
        .update(sessions)
        .set({ endedAt: sql`now()`, endReason: reason, endDetail: end.detail ?? null, perf: end.perf ?? null, drops: end.drops ?? 0 })
        .where(and(eq(sessions.id, sessionId), isNull(sessions.endedAt)))
        .returning({ id: sessions.id });
      if (!closed.length) return; // already closed: don't count its time twice
      await tx
        .update(characterState)
        .set({ playSeconds: sql`${characterState.playSeconds} + ${Math.max(0, Math.round(playSeconds))}` })
        .where(eq(characterState.characterId, characterId));
    });
  }

  async townOf(key: string): Promise<EditTown | null> {
    const base = stripRev(key);
    const [town] = await this.db
      .select({ id: towns.id, name: towns.name, key: towns.key, createdBy: towns.createdBy })
      .from(towns)
      .where(or(eq(towns.key, base), sql`starts_with(${towns.key}, ${`${base}&rev=`})`));
    return town ?? null;
  }

  async skeletonOf(key: string): Promise<{ rev: number; skeleton: unknown; createdBy: string | null } | null> {
    const [row] = await this.db
      .select({ rev: townRevisions.rev, skeleton: townRevisions.skeleton, createdBy: towns.createdBy })
      .from(townRevisions)
      .innerJoin(towns, eq(townRevisions.townId, towns.id))
      .where(eq(townRevisions.key, key));
    return row ?? null;
  }

  async draft(townId: string, author: string): Promise<TownDraft | null> {
    const [d] = await this.db.select().from(townDrafts).where(and(eq(townDrafts.townId, townId), eq(townDrafts.author, author)));
    return d ? { baseRev: d.baseRev, skeleton: d.skeleton, commands: (d.commands as unknown[]) ?? [], updatedAt: d.updatedAt.toISOString() } : null;
  }

  async saveDraft(townId: string, author: string, baseRev: number, skeleton: unknown, commands: unknown[]): Promise<string> {
    const [d] = await this.db
      .insert(townDrafts)
      .values({ townId, author, baseRev, skeleton, commands })
      .onConflictDoUpdate({ target: [townDrafts.townId, townDrafts.author], set: { baseRev, skeleton, commands, updatedAt: sql`now()` } })
      .returning({ updatedAt: townDrafts.updatedAt });
    return d.updatedAt.toISOString();
  }

  async deleteDraft(townId: string, author: string): Promise<void> {
    await this.db.delete(townDrafts).where(and(eq(townDrafts.townId, townId), eq(townDrafts.author, author)));
  }

  async history(townId: string): Promise<Revision[]> {
    const rows = await this.db
      .select({ rev: townRevisions.rev, message: townRevisions.message, by: players.name, at: townRevisions.committedAt })
      .from(townRevisions)
      .leftJoin(players, eq(townRevisions.committedBy, players.id))
      .where(eq(townRevisions.townId, townId))
      .orderBy(sql`${townRevisions.rev} desc`);
    return rows.map((r) => ({ rev: r.rev, message: r.message, by: r.by ?? null, at: r.at.toISOString() }));
  }

  async commit(c: Commit): Promise<Committed> {
    let starter: string | null = null;
    const done = await this.db.transaction(async (tx) => {
      const [town] = await tx.select({ key: towns.key, name: towns.name, createdBy: towns.createdBy }).from(towns).where(eq(towns.id, c.townId)).for('update');
      if (!town) throw noSuchTown();
      if (town.key !== c.key) throw new CharacterError('Someone changed this town first: look at it again.', 409);
      const rev = revOf(town.key) + 1;
      const key = withRev(town.key, rev);
      await tx.insert(townRevisions).values({ townId: c.townId, rev, key, skeleton: c.skeleton, houses: c.houses, committedBy: c.by, message: c.message });
      await tx.update(towns).set({ key }).where(eq(towns.id, c.townId));
      const households = await tx.select({ key: homes.id, x: homes.x, y: homes.y }).from(homes).where(eq(homes.townId, c.townId));
      const moves = rehouse(households, c.houses);
      const moved: string[] = [];
      for (const [home, to] of moves) {
        const living = await tx.select({ id: characters.id }).from(characters).where(eq(characters.homeId, home));
        // Out of theirs and into the nearest free one, a household together; with none free, out (they choose again).
        if (to) await tx.update(homes).set({ x: to[0], y: to[1] }).where(eq(homes.id, home));
        else await tx.delete(homes).where(eq(homes.id, home));
        for (const l of living) moved.push(l.id);
        if (living.length) await tx.insert(notices).values(living.map((l) => ({ characterId: l.id, text: movedNotice(town.name, to) })));
      }
      if (town.createdBy === null) starter = stripRev(town.key);
      return { key, rev, rehoused: moves.size, moved };
    });
    if (starter) {
      const file = starterOf(starter);
      if (file) setStarterKey(file.key, done.key);
    }
    return done;
  }

  async movePlaces(from: string, to: string): Promise<void> {
    await this.db.update(characterState).set({ town: to }).where(eq(characterState.town, from));
  }

  async takeNotices(characterId: string): Promise<string[]> {
    if (!isUuid(characterId)) return [];
    const rows = await this.db.delete(notices).where(eq(notices.characterId, characterId)).returning({ text: notices.text, at: notices.createdAt });
    return rows.sort((a, b) => a.at.getTime() - b.at.getTime()).map((r) => r.text);
  }

  async close(): Promise<void> {
    this.closed = true;
    await this.pool.end();
  }
}

export class MemoryPersistence implements Persistence {
  ready = true;
  readonly accounts = new Map<string, { id: string; name: string; banned: boolean }>();
  readonly characters = new Map<string, { id: string; accountId: string; name: string; townId: string | null; house: { x: number; y: number } | null }>();
  readonly states = new Map<string, SavedState & { playSeconds: number }>();
  readonly towns = new Map<string, { id: string; name: string; key: string; createdBy: string | null; slot: number | null }>();
  readonly accountUnlocks = new Map<string, { what: string; at: string }[]>();
  readonly completions = new Map<string, Progress['completions']>();
  /** Each town and mission's times (`town\nmission`), by account. */
  readonly boards = new Map<string, Map<string, { name: string; seconds: number; at: string }>>();
  readonly sessions = new Map<string, { characterId: string; reason: EndReason | null; client?: ClientInfo; end?: SessionEnd }>();
  /** The street editor's: each town's revisions, drafts (`town\nauthor`), and what each character's to be told. */
  readonly revisions: { townId: string; rev: number; key: string; skeleton: unknown; houses: [number, number][]; by: string; message: string; at: string }[] = [];
  readonly drafts = new Map<string, TownDraft>();
  readonly notices = new Map<string, string[]>();
  /** Set to make every call fail, as if the database were down. */
  down = false;

  constructor() {
    for (const t of STARTER_TOWNS) {
      const id = crypto.randomUUID();
      this.towns.set(id, { id, name: t.name, key: t.key, createdBy: null, slot: null });
    }
  }

  start(): void {}

  private check(): void {
    if (this.down) throw new Error('database is down');
  }

  async account(sub: string, name: string): Promise<Account> {
    this.check();
    let a = this.accounts.get(sub);
    if (!a) this.accounts.set(sub, (a = { id: crypto.randomUUID(), name, banned: false }));
    return { id: a.id, banned: a.banned };
  }

  async listCharacters(accountId: string): Promise<CharacterInfo[]> {
    this.check();
    return [...this.characters.values()]
      .filter((c) => c.accountId === accountId)
      .map((c) => this.info(c));
  }

  private info(c: { id: string; name: string; townId: string | null; house: { x: number; y: number } | null }): CharacterInfo {
    const town = c.townId ? this.towns.get(c.townId) : null;
    return {
      id: c.id,
      name: c.name,
      look: this.states.get(c.id)?.look ?? null,
      home: town ? { townId: town.id, townName: town.name, townKey: town.key, house: c.house } : null,
    };
  }

  async createCharacter(accountId: string, name: string, look: string | null): Promise<CharacterInfo> {
    const mine = await this.listCharacters(accountId);
    if (mine.length >= MAX_CHARACTERS) throw new CharacterError(`You can have up to ${MAX_CHARACTERS} characters.`, 409);
    if (mine.some((c) => c.name === name)) throw new CharacterError(`You already have a character called ${name}.`, 409);
    const id = crypto.randomUUID();
    this.characters.set(id, { id, accountId, name, townId: null, house: null });
    this.states.set(id, { town: '', x: 0, y: 0, z: 0, rotY: 0, look, version: 0, playSeconds: 0 });
    return { id, name, look, home: null };
  }

  async deleteCharacter(accountId: string, characterId: string): Promise<boolean> {
    this.check();
    const c = this.characters.get(characterId);
    if (!c || c.accountId !== accountId) return false;
    this.characters.delete(characterId);
    this.states.delete(characterId);
    return true;
  }

  async character(accountId: string, characterId: string): Promise<CharacterInfo | null> {
    this.check();
    const c = this.characters.get(characterId);
    if (!c || c.accountId !== accountId) return null;
    return this.info(c);
  }

  async loadState(characterId: string): Promise<SavedState | null> {
    this.check();
    const s = this.states.get(characterId);
    return s ? { ...s } : null;
  }

  async save(rows: Snapshot[]): Promise<void> {
    this.check();
    for (const r of rows) {
      const s = this.states.get(r.characterId);
      if (s && s.version >= r.version) continue;
      this.states.set(r.characterId, {
        town: r.town,
        x: r.x,
        y: r.y,
        z: r.z,
        rotY: r.rotY,
        look: r.look || s?.look || null,
        version: r.version,
        playSeconds: s?.playSeconds ?? 0,
      });
    }
  }

  async unlocks(accountId: string): Promise<string[]> {
    this.check();
    return (this.accountUnlocks.get(accountId) ?? []).map((u) => u.what);
  }

  async unlock(accountId: string, what: string): Promise<void> {
    this.check();
    const mine = this.accountUnlocks.get(accountId) ?? [];
    if (!mine.some((u) => u.what === what)) this.accountUnlocks.set(accountId, [...mine, { what, at: new Date().toISOString() }]);
  }

  async completeMission(accountId: string, characterId: string, missionId: string): Promise<void> {
    this.check();
    const now = new Date().toISOString();
    const mine = this.completions.get(accountId) ?? [];
    const done = mine.find((c) => c.missionId === missionId);
    if (done) {
      done.times++;
      done.lastAt = now;
    } else {
      mine.push({ missionId, times: 1, firstAt: now, lastAt: now, firstCharacter: this.characters.get(characterId)?.name ?? null });
    }
    this.completions.set(accountId, mine);
  }

  async progress(accountId: string): Promise<Progress> {
    this.check();
    return { completions: this.completions.get(accountId) ?? [], unlocks: this.accountUnlocks.get(accountId) ?? [] };
  }

  async records(accountId: string, townKey: string, missionId: string): Promise<Board> {
    this.check();
    const board = this.boards.get(`${townKey}\n${missionId}`) ?? new Map();
    const best = [...board.entries()]
      .sort((a, b) => a[1].seconds - b[1].seconds || a[1].at.localeCompare(b[1].at))
      .slice(0, BOARD_SIZE)
      .map(([id, r]) => ({ ...r, mine: id === accountId }));
    return { best, mine: board.get(accountId)?.seconds ?? null };
  }

  async keepRecord(accountId: string, characterId: string, townKey: string, missionId: string, seconds: number): Promise<Board & { before: number | null }> {
    this.check();
    const c = this.characters.get(characterId);
    if (!c || c.accountId !== accountId) throw noSuchCharacter();
    const key = `${townKey}\n${missionId}`;
    const board = this.boards.get(key) ?? new Map();
    this.boards.set(key, board);
    const had = board.get(accountId);
    if (!had || seconds < had.seconds) board.set(accountId, { name: c.name, seconds, at: new Date().toISOString() });
    return { ...(await this.records(accountId, townKey, missionId)), before: had?.seconds ?? null };
  }

  async listTowns(accountId: string): Promise<TownInfo[]> {
    this.check();
    const residents = (id: string) => [...this.characters.values()].filter((c) => c.townId === id).length;
    return [...this.towns.values()]
      .filter((t) => t.createdBy === null || t.createdBy === accountId)
      .sort((a, b) => Number(a.createdBy !== null) - Number(b.createdBy !== null))
      .map((t) => ({ id: t.id, name: t.name, key: t.key, mine: t.createdBy === accountId, residents: residents(t.id), place: t.createdBy === null ? placeOf(t.key) : null, slot: t.slot }));
  }

  async countyTownKeys(): Promise<string[]> {
    this.check();
    const placed = [...this.towns.values()].filter((t) => t.createdBy === null || (t.slot !== null && t.slot >= 0 && t.slot < MAX_TOWNS));
    placed.sort((a, b) => Number(a.createdBy !== null) - Number(b.createdBy !== null));
    return [...new Set(placed.map((t) => t.key))];
  }

  async mayEnter(accountId: string, townKey: string): Promise<boolean> {
    this.check();
    if (townKey.startsWith(`${NORTH}:`)) return townKey === `${NORTH}:${accountId}`;
    const town = [...this.towns.values()].find((t) => t.key === townKey);
    return !town || town.createdBy === null || town.createdBy === accountId;
  }

  /** Throws unless the town is there and this account may live in it. */
  private visibleTown(accountId: string, townId: string): void {
    const town = this.towns.get(townId);
    if (!town || (town.createdBy !== null && town.createdBy !== accountId)) throw noSuchTown();
  }

  async ownTownKey(accountId: string, townId: string): Promise<string> {
    this.check();
    const town = this.towns.get(townId);
    if (!town) throw noSuchTown();
    if (town.createdBy !== accountId) throw new CharacterError('Only whoever made a town can delete it.', 403);
    return town.key;
  }

  async countyFor(room: string): Promise<{ account: string | null; mine: { key: string; slot: number }[] } | null> {
    this.check();
    if (starterOf(room)) return { account: null, mine: [] };
    const account = room.startsWith(`${NORTH}:`) ? room.slice(NORTH.length + 1) : ([...this.towns.values()].find((t) => t.key === room)?.createdBy ?? null);
    if (!account) return null;
    const mine = [...this.towns.values()].filter((t) => t.createdBy === account && t.slot !== null).map((t) => ({ key: t.key, slot: t.slot! }));
    return { account, mine };
  }

  async createTown(accountId: string, name: string, key?: string): Promise<TownInfo> {
    this.check();
    const mine = [...this.towns.values()].filter((t) => t.createdBy === accountId);
    const slot = freeSlot(mine.map((t) => t.slot));
    if (mine.length >= MAX_TOWNS || slot === null) throw tooManyTowns();
    if (key !== undefined && revOf(key) > 0) throw townExists(null);
    const same = [...this.towns.values()].find((t) => t.key === key || (key !== undefined && t.key.startsWith(`${key}&rev=`)));
    if (same) throw townExists(same.createdBy === null || same.createdBy === accountId ? same.name : null);
    const town = { id: crypto.randomUUID(), name, key: key ?? newTownKey(), createdBy: accountId, slot };
    this.towns.set(town.id, town);
    return { id: town.id, name, key: town.key, mine: true, residents: 0, place: null, slot };
  }

  async deleteTown(accountId: string, townId: string): Promise<void> {
    this.check();
    const town = this.towns.get(townId);
    if (!town || town.createdBy !== accountId) throw noSuchTown();
    this.towns.delete(townId);
    for (const c of this.characters.values()) {
      if (c.townId !== townId) continue;
      c.townId = null;
      c.house = null;
    }
  }

  async listHomes(accountId: string, townId: string): Promise<HomeInfo[]> {
    this.check();
    this.visibleTown(accountId, townId);
    const byTile = new Map<string, HomeInfo>();
    for (const c of this.characters.values()) {
      if (c.townId !== townId || !c.house) continue;
      const key = `${c.house.x},${c.house.y}`;
      const home = byTile.get(key) ?? { ...c.house, mine: c.accountId === accountId, residents: [] };
      home.residents.push(c.name);
      byTile.set(key, home);
    }
    return [...byTile.values()];
  }

  async moveToTown(accountId: string, characterId: string, townId: string): Promise<void> {
    this.check();
    const c = this.characters.get(characterId);
    if (!c || c.accountId !== accountId) throw noSuchCharacter();
    if (c.townId === townId) return;
    this.visibleTown(accountId, townId);
    c.townId = townId;
    c.house = null;
  }

  async claimHome(accountId: string, characterId: string, x: number, y: number): Promise<void> {
    this.check();
    const c = this.characters.get(characterId);
    if (!c || c.accountId !== accountId) throw noSuchCharacter();
    if (!c.townId) throw new CharacterError('Choose a town first.', 409);
    const town = this.towns.get(c.townId);
    if (town) checkHouse(town.key, x, y, this.revisions.find((r) => r.key === town.key)?.houses ?? null);
    if (c.house?.x === x && c.house.y === y) return;
    const inside = [...this.characters.values()].filter((o) => o.townId === c.townId && o.house?.x === x && o.house.y === y);
    if (inside.some((o) => o.accountId !== accountId)) throw houseTaken();
    if (inside.length >= MAX_RESIDENTS) throw houseFull();
    c.house = { x, y };
  }

  async openSession(characterId: string, _roomId: string, client?: ClientInfo): Promise<string> {
    this.check();
    const id = crypto.randomUUID();
    this.sessions.set(id, { characterId, reason: null, client });
    return id;
  }

  async closeSession(sessionId: string, characterId: string, reason: EndReason, playSeconds: number, end: SessionEnd = {}): Promise<void> {
    this.check();
    const s = this.sessions.get(sessionId);
    if (!s || s.reason) return;
    s.reason = reason;
    s.end = end;
    const state = this.states.get(characterId);
    if (state) state.playSeconds += playSeconds;
  }

  async townOf(key: string): Promise<EditTown | null> {
    this.check();
    const base = stripRev(key);
    const t = [...this.towns.values()].find((t) => stripRev(t.key) === base);
    return t ? { id: t.id, name: t.name, key: t.key, createdBy: t.createdBy } : null;
  }

  async skeletonOf(key: string): Promise<{ rev: number; skeleton: unknown; createdBy: string | null } | null> {
    this.check();
    const r = this.revisions.find((r) => r.key === key);
    const town = r && this.towns.get(r.townId);
    return r && town ? { rev: r.rev, skeleton: r.skeleton, createdBy: town.createdBy } : null;
  }

  async draft(townId: string, author: string): Promise<TownDraft | null> {
    this.check();
    return this.drafts.get(`${townId}\n${author}`) ?? null;
  }

  async saveDraft(townId: string, author: string, baseRev: number, skeleton: unknown, commands: unknown[]): Promise<string> {
    this.check();
    const updatedAt = new Date().toISOString();
    this.drafts.set(`${townId}\n${author}`, { baseRev, skeleton, commands, updatedAt });
    return updatedAt;
  }

  async deleteDraft(townId: string, author: string): Promise<void> {
    this.check();
    this.drafts.delete(`${townId}\n${author}`);
  }

  async history(townId: string): Promise<Revision[]> {
    this.check();
    const nameOf = (id: string) => [...this.accounts.values()].find((a) => a.id === id)?.name ?? null;
    return this.revisions
      .filter((r) => r.townId === townId)
      .sort((a, b) => b.rev - a.rev)
      .map((r) => ({ rev: r.rev, message: r.message, by: nameOf(r.by), at: r.at }));
  }

  async commit(c: Commit): Promise<Committed> {
    this.check();
    const town = this.towns.get(c.townId);
    if (!town) throw noSuchTown();
    if (town.key !== c.key) throw new CharacterError('Someone changed this town first: look at it again.', 409);
    const rev = revOf(town.key) + 1;
    const key = withRev(town.key, rev);
    this.revisions.push({ townId: c.townId, rev, key, skeleton: c.skeleton, houses: c.houses, by: c.by, message: c.message, at: new Date().toISOString() });
    town.key = key;
    // A household is those of one account in one house.
    const living = [...this.characters.values()].filter((ch) => ch.townId === c.townId && ch.house);
    const households = new Map<string, { key: string; x: number; y: number }>();
    for (const ch of living) households.set(`${ch.house!.x},${ch.house!.y}`, { key: `${ch.house!.x},${ch.house!.y}`, x: ch.house!.x, y: ch.house!.y });
    const moves = rehouse([...households.values()], c.houses);
    const moved: string[] = [];
    for (const ch of living) {
      const tile = `${ch.house!.x},${ch.house!.y}`;
      if (!moves.has(tile)) continue;
      const to = moves.get(tile)!;
      ch.house = to ? { x: to[0], y: to[1] } : null;
      moved.push(ch.id);
      this.notices.set(ch.id, [...(this.notices.get(ch.id) ?? []), movedNotice(town.name, to)]);
    }
    if (town.createdBy === null) {
      const file = starterOf(key);
      if (file) setStarterKey(file.key, key);
    }
    return { key, rev, rehoused: moves.size, moved };
  }

  async movePlaces(from: string, to: string): Promise<void> {
    this.check();
    for (const s of this.states.values()) if (s.town === from) s.town = to;
  }

  async takeNotices(characterId: string): Promise<string[]> {
    this.check();
    const list = this.notices.get(characterId) ?? [];
    this.notices.delete(characterId);
    return list;
  }

  async close(): Promise<void> {}
}
