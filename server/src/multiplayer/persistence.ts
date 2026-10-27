// Everything multiplayer keeps in Postgres (Drizzle; the tables are in
// tables.ts, the migrations in server/drizzle): accounts, their characters,
// where each character was, and sessions. Never touched from the room's tick: rooms
// read a character once on join and write in batches (every 30 s, on leave, on
// shutdown), and a failed write just leaves the data dirty for next time.
//
// MemoryPersistence is the same thing in memory: tests, and a server started
// without DATABASE_URL (multiplayer works, nothing outlives the process).

import { fileURLToPath } from 'node:url';
import { and, count, eq, isNull, sql } from 'drizzle-orm';
import { drizzle, type NodePgDatabase } from 'drizzle-orm/node-postgres';
import { migrate } from 'drizzle-orm/node-postgres/migrator';
import pg from 'pg';
import { characterState, characters, players, sessions } from './tables.ts';

export interface Account {
  id: string;
  banned: boolean;
}

export interface CharacterInfo {
  id: string;
  name: string;
  /** Character model, if they've been given one. */
  look: string | null;
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
const NAME_MAX = 20;

export class CharacterError extends Error {
  readonly status: number;
  constructor(message: string, status = 400) {
    super(message);
    this.status = status;
  }
}

/** A character name: printable, single-spaced, trimmed, 1-20 characters; or null. */
export function cleanName(raw: unknown): string | null {
  if (typeof raw !== 'string') return null;
  // Control characters out, runs of whitespace to one space.
  const name = raw.replace(/\p{C}/gu, '').replace(/\s+/gu, ' ').trim();
  return name.length >= 1 && name.length <= NAME_MAX ? name : null;
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
        this.ready = true;
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

  async listCharacters(accountId: string): Promise<CharacterInfo[]> {
    return this.db
      .select({ id: characters.id, name: characters.name, look: lookOf })
      .from(characters)
      .leftJoin(characterState, eq(characterState.characterId, characters.id))
      .where(eq(characters.playerId, accountId))
      .orderBy(characters.createdAt);
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
      return { id, name, look };
    });
  }

  async deleteCharacter(accountId: string, characterId: string): Promise<boolean> {
    if (!isUuid(characterId)) return false;
    const gone = await this.db
      .delete(characters)
      .where(and(eq(characters.id, characterId), eq(characters.playerId, accountId)))
      .returning({ id: characters.id });
    return gone.length > 0;
  }

  async character(accountId: string, characterId: string): Promise<CharacterInfo | null> {
    if (!isUuid(characterId)) return null;
    const [row] = await this.db
      .select({ id: characters.id, name: characters.name, look: lookOf })
      .from(characters)
      .leftJoin(characterState, eq(characterState.characterId, characters.id))
      .where(and(eq(characters.id, characterId), eq(characters.playerId, accountId)));
    return row ?? null;
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
  readonly characters = new Map<string, { id: string; accountId: string; name: string }>();
  readonly states = new Map<string, SavedState & { playSeconds: number }>();
  readonly sessions = new Map<string, { characterId: string; reason: EndReason | null }>();
  /** Set to make every call fail, as if the database were down. */
  down = false;

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
      .map((c) => ({ id: c.id, name: c.name, look: this.states.get(c.id)?.look ?? null }));
  }

  async createCharacter(accountId: string, name: string, look: string | null): Promise<CharacterInfo> {
    const mine = await this.listCharacters(accountId);
    if (mine.length >= MAX_CHARACTERS) throw new CharacterError(`You can have up to ${MAX_CHARACTERS} characters.`, 409);
    if (mine.some((c) => c.name === name)) throw new CharacterError(`You already have a character called ${name}.`, 409);
    const id = crypto.randomUUID();
    this.characters.set(id, { id, accountId, name });
    this.states.set(id, { town: '', x: 0, y: 0, z: 0, rotY: 0, look, version: 0, playSeconds: 0 });
    return { id, name, look };
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
    return { id: c.id, name: c.name, look: this.states.get(c.id)?.look ?? null };
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
