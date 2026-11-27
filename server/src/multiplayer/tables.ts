// The game database's tables (Drizzle). Only durable data lives here; the live
// game is in memory (CityRoom). Change this file, then run
// `npm run db:generate` to write the migration into server/drizzle, which the
// server applies on start.
//
// One account (one Google sign-in) can have several characters, and play more
// than one at once: a parent and child sharing an account each play their own.
// What's earned (missions finished, unlocks) belongs to the account, so every
// character has it, in every town. Where someone lives belongs to the character.

import { sql } from 'drizzle-orm';
import { type AnyPgColumn, bigint, index, integer, jsonb, pgTable, primaryKey, real, text, timestamp, unique, uuid } from 'drizzle-orm/pg-core';

const at = (name: string) => timestamp(name, { withTimezone: true });

export const players = pgTable('players', {
  id: uuid('id').primaryKey().defaultRandom(),
  /** The Keycloak subject (Google behind it). Sign-in is Keycloak's job, so no passwords here. */
  authSub: text('auth_sub').notNull().unique(),
  name: text('name').notNull(),
  createdAt: at('created_at').notNull().defaultNow(),
  bannedAt: at('banned_at'),
});

/** A town people can live in. `key` is its seed and params: also what its room is called. */
export const towns = pgTable('towns', {
  id: uuid('id').primaryKey().defaultRandom(),
  name: text('name').notNull(),
  key: text('key').notNull().unique(),
  /** Who made it (they can delete it); null for the towns that come with the game. */
  createdBy: uuid('created_by').references(() => players.id, { onDelete: 'cascade' }),
  /** For a town that comes with the game: which generation of it this is, so a new one can rehouse everyone. */
  layout: text('layout'),
  /** For a town someone made: which of their three places north of Porchlight it's in (0-2; see county.ts). */
  slot: integer('slot'),
  createdAt: at('created_at').notNull().defaultNow(),
}, (t) => [unique('towns_slot').on(t.createdBy, t.slot)]);

/**
 * A house someone lives in. It's named by a tile of the building, not the
 * building's id: ids are generation order, which any change to the generator
 * can renumber. A house belongs to one account, and goes when nobody lives there.
 */
export const homes = pgTable(
  'homes',
  {
    id: uuid('id').primaryKey().defaultRandom(),
    townId: uuid('town_id')
      .notNull()
      .references(() => towns.id, { onDelete: 'cascade' }),
    x: integer('x').notNull(),
    y: integer('y').notNull(),
    playerId: uuid('player_id')
      .notNull()
      .references(() => players.id, { onDelete: 'cascade' }),
    createdAt: at('created_at').notNull().defaultNow(),
  },
  (t) => [unique('homes_town_tile_key').on(t.townId, t.x, t.y)],
);

export const characters = pgTable(
  'characters',
  {
    id: uuid('id').primaryKey().defaultRandom(),
    playerId: uuid('player_id')
      .notNull()
      .references(() => players.id, { onDelete: 'cascade' }),
    name: text('name').notNull(),
    /** The town they live in, once they've chosen; none if it's been deleted. */
    townId: uuid('town_id').references((): AnyPgColumn => towns.id, { onDelete: 'set null' }),
    homeId: uuid('home_id').references((): AnyPgColumn => homes.id, { onDelete: 'set null' }),
    createdAt: at('created_at').notNull().defaultNow(),
  },
  (t) => [unique('characters_player_id_name_key').on(t.playerId, t.name)],
);

export const characterState = pgTable('character_state', {
  characterId: uuid('character_id')
    .primaryKey()
    .references(() => characters.id, { onDelete: 'cascade' }),
  /**
   * The town the position is in (its seed and params). Towns are generated, so
   * a position only means something in the town it was saved in.
   */
  town: text('town').notNull().default(''),
  posX: real('pos_x').notNull().default(0),
  posY: real('pos_y').notNull().default(0),
  posZ: real('pos_z').notNull().default(0),
  rotY: real('rot_y').notNull().default(0),
  appearance: jsonb('appearance').$type<{ look?: string }>().notNull().default({}),
  stats: jsonb('stats').notNull().default({}),
  playSeconds: bigint('play_seconds', { mode: 'number' }).notNull().default(0),
  /** Monotonic per character: a late write never overwrites a newer one. */
  version: bigint('version', { mode: 'number' }).notNull().default(0),
  updatedAt: at('updated_at').notNull().defaultNow(),
});

export const sessions = pgTable(
  'sessions',
  {
    id: uuid('id').primaryKey().defaultRandom(),
    characterId: uuid('character_id')
      .notNull()
      .references(() => characters.id, { onDelete: 'cascade' }),
    roomId: text('room_id'),
    startedAt: at('started_at').notNull().defaultNow(),
    endedAt: at('ended_at'),
    /** consented | timeout | kicked | shutdown | crash */
    endReason: text('end_reason'),
    /** More on why it ended: the kick's reason, or the close code ("close 1006"). */
    endDetail: text('end_detail'),
    /** What it was played on (telemetry.ts ClientInfo): platform, OS, build, GPU, an anonymous install id. Nothing personal. */
    client: jsonb('client'),
    /** How it ran (telemetry.ts PerfSummary): frame rate, slow frames, round trips. */
    perf: jsonb('perf'),
    /** Times the connection dropped and came back within the reconnection window. */
    drops: integer('drops').notNull().default(0),
  },
  (t) => [index('sessions_character_open').on(t.characterId).where(sql`${t.endedAt} IS NULL`)],
);

/** What an account has earned: once, for all its characters, in every town. */
export const accountUnlocks = pgTable(
  'account_unlocks',
  {
    playerId: uuid('player_id')
      .notNull()
      .references(() => players.id, { onDelete: 'cascade' }),
    what: text('what').notNull(),
    unlockedAt: at('unlocked_at').notNull().defaultNow(),
  },
  (t) => [primaryKey({ columns: [t.playerId, t.what] })],
);

/** The missions an account has finished, and how often (they can be played again). */
export const missionCompletions = pgTable(
  'mission_completions',
  {
    playerId: uuid('player_id')
      .notNull()
      .references(() => players.id, { onDelete: 'cascade' }),
    missionId: text('mission_id').notNull(),
    times: integer('times').notNull().default(1),
    firstAt: at('first_at').notNull().defaultNow(),
    lastAt: at('last_at').notNull().defaultNow(),
    /** Who finished it first (gone if they've been deleted; the mission stays finished). */
    firstCharacterId: uuid('first_character_id').references(() => characters.id, { onDelete: 'set null' }),
  },
  (t) => [primaryKey({ columns: [t.playerId, t.missionId] })],
);

/**
 * A mission's best times in a town (its key: seed and params), one per account: the fastest it has
 * run, and which of its characters ran it. Marathon Runner keeps these, so every seeded town has
 * its own board.
 */
export const missionRecords = pgTable(
  'mission_records',
  {
    townKey: text('town_key').notNull(),
    missionId: text('mission_id').notNull(),
    playerId: uuid('player_id')
      .notNull()
      .references(() => players.id, { onDelete: 'cascade' }),
    /** Who ran it (gone if they've been deleted; the time stays). */
    characterId: uuid('character_id').references(() => characters.id, { onDelete: 'set null' }),
    /** The name they ran under, kept so the board still says it if the character goes. */
    name: text('name').notNull(),
    seconds: real('seconds').notNull(),
    at: at('at').notNull().defaultNow(),
  },
  (t) => [primaryKey({ columns: [t.townKey, t.missionId, t.playerId] }), index('mission_records_board').on(t.townKey, t.missionId, t.seconds)],
);

/**
 * A town's committed street skeletons (docs/street-editor-api.md): revision `rev` is the town grown from its
 * key with `rev=N` and this skeleton laid over it. `key` is that key in full (so an old revision's skeleton can
 * still be fetched by it), and `houses` the tile each of its houses goes by (the draft worker's `houses`): what
 * a character may claim there, as a starter town's file says for revision 0.
 */
export const townRevisions = pgTable(
  'town_revisions',
  {
    id: uuid('id').primaryKey().defaultRandom(),
    townId: uuid('town_id')
      .notNull()
      .references(() => towns.id, { onDelete: 'cascade' }),
    rev: integer('rev').notNull(),
    key: text('key').notNull(),
    skeleton: jsonb('skeleton').notNull(),
    houses: jsonb('houses').$type<[number, number][]>().notNull().default([]),
    committedBy: uuid('committed_by').references(() => players.id, { onDelete: 'set null' }),
    message: text('message').notNull().default(''),
    committedAt: at('committed_at').notNull().defaultNow(),
  },
  (t) => [unique('town_revisions_town_rev').on(t.townId, t.rev), index('town_revisions_key').on(t.key)],
);

/** An editor's work in progress on a town: one each, kept as they go. */
export const townDrafts = pgTable(
  'town_drafts',
  {
    id: uuid('id').primaryKey().defaultRandom(),
    townId: uuid('town_id')
      .notNull()
      .references(() => towns.id, { onDelete: 'cascade' }),
    author: uuid('author')
      .notNull()
      .references(() => players.id, { onDelete: 'cascade' }),
    baseRev: integer('base_rev').notNull(),
    skeleton: jsonb('skeleton').notNull(),
    commands: jsonb('commands').notNull().default([]),
    updatedAt: at('updated_at').notNull().defaultNow(),
  },
  (t) => [unique('town_drafts_town_author').on(t.townId, t.author)],
);

/** Something to tell a character the next time they play (their house moved when the streets changed); gone once told. */
export const notices = pgTable(
  'notices',
  {
    id: uuid('id').primaryKey().defaultRandom(),
    characterId: uuid('character_id')
      .notNull()
      .references(() => characters.id, { onDelete: 'cascade' }),
    text: text('text').notNull(),
    createdAt: at('created_at').notNull().defaultNow(),
  },
  (t) => [index('notices_character').on(t.characterId)],
);
