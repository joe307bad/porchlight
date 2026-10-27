// The game database's tables (Drizzle). Only durable data lives here; the live
// game is in memory (CityRoom). Change this file, then run
// `npm run db:generate` to write the migration into server/drizzle, which the
// server applies on start.
//
// One account (one Google sign-in) can have several characters, and play more
// than one at once: a parent and child sharing an account each play their own.

import { sql } from 'drizzle-orm';
import { bigint, index, jsonb, pgTable, real, text, timestamp, unique, uuid } from 'drizzle-orm/pg-core';

const at = (name: string) => timestamp(name, { withTimezone: true });

export const players = pgTable('players', {
  id: uuid('id').primaryKey().defaultRandom(),
  /** The Keycloak subject (Google behind it). Sign-in is Keycloak's job, so no passwords here. */
  authSub: text('auth_sub').notNull().unique(),
  name: text('name').notNull(),
  createdAt: at('created_at').notNull().defaultNow(),
  bannedAt: at('banned_at'),
});

export const characters = pgTable(
  'characters',
  {
    id: uuid('id').primaryKey().defaultRandom(),
    playerId: uuid('player_id')
      .notNull()
      .references(() => players.id, { onDelete: 'cascade' }),
    name: text('name').notNull(),
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
  },
  (t) => [index('sessions_character_open').on(t.characterId).where(sql`${t.endedAt} IS NULL`)],
);
