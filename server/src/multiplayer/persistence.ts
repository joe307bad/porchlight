// Everything multiplayer keeps in Postgres (Drizzle; the tables are in
// tables.ts, the migrations in server/drizzle): accounts and what they've earned,
// their characters, where each character lives and was, towns, and sessions. Never touched from the room's tick: rooms
// read a character once on join and write in batches (every 30 s, on leave, on
// shutdown), and a failed write just leaves the data dirty for next time.
//
// MemoryPersistence is the same thing in memory: tests, and a server started
// without DATABASE_URL (multiplayer works, nothing outlives the process).

import { fileURLToPath } from 'node:url';
import { and, asc, count, eq, isNull, sql } from 'drizzle-orm';
import { drizzle, type NodePgDatabase } from 'drizzle-orm/node-postgres';
import { migrate } from 'drizzle-orm/node-postgres/migrator';
import pg from 'pg';
import { accountUnlocks, characterState, characters, homes, missionCompletions, players, sessions, towns } from './tables.ts';

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

export type EndReason = 'consented' | 'timeout' | 'kicked' | 'shutdown' | 'crash';

export const MAX_CHARACTERS = 8;
/** How many towns an account can have made at once: to make another, delete one. */
export const MAX_TOWNS = 3;
/** How many characters can live in a house. */
export const MAX_RESIDENTS = 2;
const NAME_MAX = 20;
export const TOWN_NAME_MAX = 24;
/** The towns that come with the game, there for everyone (and nobody can delete them). */
export const STARTER_TOWNS = [
  { name: 'Maple Hollow', key: 'seed=maple-hollow' },
  { name: 'Harbour Bend', key: 'seed=harbour-bend' },
  { name: 'Cedar Falls', key: 'seed=cedar-falls' },
];
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
  listTowns(accountId: string): Promise<TownInfo[]>;
  /** The town's key if this account made it; throws otherwise (or if it's gone). */
  ownTownKey(accountId: string, townId: string): Promise<string>;
  createTown(accountId: string, name: string): Promise<TownInfo>;
  /** Everyone who lived there starts again from choosing a town (what they've earned stays). */
  deleteTown(accountId: string, townId: string): Promise<void>;
  listHomes(accountId: string, townId: string): Promise<HomeInfo[]>;
  /** Move the character to this town, out of any house they had in another. */
  moveToTown(accountId: string, characterId: string, townId: string): Promise<void>;
  /** Move the character into the house at this tile of their town. */
  claimHome(accountId: string, characterId: string, x: number, y: number): Promise<void>;
  openSession(characterId: string, roomId: string): Promise<string>;
  closeSession(sessionId: string, characterId: string, reason: EndReason, playSeconds: number): Promise<void>;
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
        await this.db.insert(towns).values(STARTER_TOWNS).onConflictDoNothing({ target: towns.key });
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

  async listTowns(accountId: string): Promise<TownInfo[]> {
    const rows = await this.db
      .select({ id: towns.id, name: towns.name, key: towns.key, createdBy: towns.createdBy, residents: sql<number>`(select count(*)::int from ${characters} where ${characters.townId} = ${towns.id})` })
      .from(towns)
      .orderBy(sql`${towns.createdBy} is not null`, towns.createdAt, towns.name);
    return rows.map(({ createdBy, ...t }) => ({ ...t, mine: createdBy === accountId }));
  }

  async ownTownKey(accountId: string, townId: string): Promise<string> {
    if (!isUuid(townId)) throw noSuchTown();
    const [town] = await this.db.select({ key: towns.key, createdBy: towns.createdBy }).from(towns).where(eq(towns.id, townId));
    if (!town) throw noSuchTown();
    if (town.createdBy !== accountId) throw new CharacterError('Only whoever made a town can delete it.', 403);
    return town.key;
  }

  async createTown(accountId: string, name: string): Promise<TownInfo> {
    return this.db.transaction(async (tx) => {
      // Lock the account row so two creates at once can't both slip under the limit.
      await tx.select({ id: players.id }).from(players).where(eq(players.id, accountId)).for('update');
      const [{ n }] = await tx.select({ n: count() }).from(towns).where(eq(towns.createdBy, accountId));
      if (n >= MAX_TOWNS) throw tooManyTowns();
      const [town] = await tx.insert(towns).values({ name, key: newTownKey(), createdBy: accountId }).returning({ id: towns.id, name: towns.name, key: towns.key });
      return { ...town, mine: true, residents: 0 };
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
      const [town] = await tx.select({ id: towns.id }).from(towns).where(eq(towns.id, townId));
      if (!town) throw noSuchTown();
      await tx.update(characters).set({ townId, homeId: null }).where(eq(characters.id, characterId));
      await PgPersistence.emptyHouse(tx, c.homeId);
    });
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

  async openSession(characterId: string, roomId: string): Promise<string> {
    const [row] = await this.db.insert(sessions).values({ characterId, roomId }).returning({ id: sessions.id });
    return row.id;
  }

  async closeSession(sessionId: string, characterId: string, reason: EndReason, playSeconds: number): Promise<void> {
    await this.db.transaction(async (tx) => {
      const closed = await tx
        .update(sessions)
        .set({ endedAt: sql`now()`, endReason: reason })
        .where(and(eq(sessions.id, sessionId), isNull(sessions.endedAt)))
        .returning({ id: sessions.id });
      if (!closed.length) return; // already closed: don't count its time twice
      await tx
        .update(characterState)
        .set({ playSeconds: sql`${characterState.playSeconds} + ${Math.max(0, Math.round(playSeconds))}` })
        .where(eq(characterState.characterId, characterId));
    });
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
  readonly towns = new Map<string, { id: string; name: string; key: string; createdBy: string | null }>();
  readonly accountUnlocks = new Map<string, { what: string; at: string }[]>();
  readonly completions = new Map<string, Progress['completions']>();
  readonly sessions = new Map<string, { characterId: string; reason: EndReason | null }>();
  /** Set to make every call fail, as if the database were down. */
  down = false;

  constructor() {
    for (const t of STARTER_TOWNS) {
      const id = crypto.randomUUID();
      this.towns.set(id, { id, ...t, createdBy: null });
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

  async listTowns(accountId: string): Promise<TownInfo[]> {
    this.check();
    const residents = (id: string) => [...this.characters.values()].filter((c) => c.townId === id).length;
    return [...this.towns.values()]
      .sort((a, b) => Number(a.createdBy !== null) - Number(b.createdBy !== null))
      .map((t) => ({ id: t.id, name: t.name, key: t.key, mine: t.createdBy === accountId, residents: residents(t.id) }));
  }

  async ownTownKey(accountId: string, townId: string): Promise<string> {
    this.check();
    const town = this.towns.get(townId);
    if (!town) throw noSuchTown();
    if (town.createdBy !== accountId) throw new CharacterError('Only whoever made a town can delete it.', 403);
    return town.key;
  }

  async createTown(accountId: string, name: string): Promise<TownInfo> {
    this.check();
    if ([...this.towns.values()].filter((t) => t.createdBy === accountId).length >= MAX_TOWNS) throw tooManyTowns();
    const town = { id: crypto.randomUUID(), name, key: newTownKey(), createdBy: accountId };
    this.towns.set(town.id, town);
    return { id: town.id, name, key: town.key, mine: true, residents: 0 };
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
    if (!this.towns.has(townId)) throw noSuchTown();
    c.townId = townId;
    c.house = null;
  }

  async claimHome(accountId: string, characterId: string, x: number, y: number): Promise<void> {
    this.check();
    const c = this.characters.get(characterId);
    if (!c || c.accountId !== accountId) throw noSuchCharacter();
    if (!c.townId) throw new CharacterError('Choose a town first.', 409);
    if (c.house?.x === x && c.house.y === y) return;
    const inside = [...this.characters.values()].filter((o) => o.townId === c.townId && o.house?.x === x && o.house.y === y);
    if (inside.some((o) => o.accountId !== accountId)) throw houseTaken();
    if (inside.length >= MAX_RESIDENTS) throw houseFull();
    c.house = { x, y };
  }

  async openSession(characterId: string): Promise<string> {
    this.check();
    const id = crypto.randomUUID();
    this.sessions.set(id, { characterId, reason: null });
    return id;
  }

  async closeSession(sessionId: string, characterId: string, reason: EndReason, playSeconds: number): Promise<void> {
    this.check();
    const s = this.sessions.get(sessionId);
    if (!s || s.reason) return;
    s.reason = reason;
    const state = this.states.get(characterId);
    if (state) state.playSeconds += playSeconds;
  }

  async close(): Promise<void> {}
}
