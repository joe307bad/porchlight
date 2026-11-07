CREATE TABLE "account_unlocks" (
	"player_id" uuid NOT NULL,
	"what" text NOT NULL,
	"unlocked_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "account_unlocks_player_id_what_pk" PRIMARY KEY("player_id","what")
);
--> statement-breakpoint
CREATE TABLE "homes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"town_id" uuid NOT NULL,
	"x" integer NOT NULL,
	"y" integer NOT NULL,
	"player_id" uuid NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "homes_town_tile_key" UNIQUE("town_id","x","y")
);
--> statement-breakpoint
CREATE TABLE "mission_completions" (
	"player_id" uuid NOT NULL,
	"mission_id" text NOT NULL,
	"times" integer DEFAULT 1 NOT NULL,
	"first_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_at" timestamp with time zone DEFAULT now() NOT NULL,
	"first_character_id" uuid,
	CONSTRAINT "mission_completions_player_id_mission_id_pk" PRIMARY KEY("player_id","mission_id")
);
--> statement-breakpoint
CREATE TABLE "towns" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"name" text NOT NULL,
	"key" text NOT NULL,
	"created_by" uuid,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "towns_key_unique" UNIQUE("key")
);
--> statement-breakpoint
ALTER TABLE "characters" ADD COLUMN "town_id" uuid;--> statement-breakpoint
ALTER TABLE "characters" ADD COLUMN "home_id" uuid;--> statement-breakpoint
ALTER TABLE "account_unlocks" ADD CONSTRAINT "account_unlocks_player_id_players_id_fk" FOREIGN KEY ("player_id") REFERENCES "public"."players"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "homes" ADD CONSTRAINT "homes_town_id_towns_id_fk" FOREIGN KEY ("town_id") REFERENCES "public"."towns"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "homes" ADD CONSTRAINT "homes_player_id_players_id_fk" FOREIGN KEY ("player_id") REFERENCES "public"."players"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "mission_completions" ADD CONSTRAINT "mission_completions_player_id_players_id_fk" FOREIGN KEY ("player_id") REFERENCES "public"."players"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "mission_completions" ADD CONSTRAINT "mission_completions_first_character_id_characters_id_fk" FOREIGN KEY ("first_character_id") REFERENCES "public"."characters"("id") ON DELETE set null ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "towns" ADD CONSTRAINT "towns_created_by_players_id_fk" FOREIGN KEY ("created_by") REFERENCES "public"."players"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "characters" ADD CONSTRAINT "characters_town_id_towns_id_fk" FOREIGN KEY ("town_id") REFERENCES "public"."towns"("id") ON DELETE set null ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "characters" ADD CONSTRAINT "characters_home_id_homes_id_fk" FOREIGN KEY ("home_id") REFERENCES "public"."homes"("id") ON DELETE set null ON UPDATE no action;--> statement-breakpoint
-- Unlocks were each character's; now they're the account's: everything any of its characters had.
INSERT INTO "account_unlocks" ("player_id", "what")
SELECT DISTINCT c."player_id", u.what
FROM "character_state" cs
JOIN "characters" c ON c."id" = cs."character_id"
CROSS JOIN LATERAL jsonb_array_elements_text(coalesce(cs."stats"->'unlocks', '[]'::jsonb)) AS u(what)
ON CONFLICT DO NOTHING;--> statement-breakpoint
UPDATE "character_state" SET "stats" = "stats" - 'unlocks' WHERE "stats" ? 'unlocks';
