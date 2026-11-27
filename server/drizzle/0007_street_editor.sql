CREATE TABLE "notices" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"character_id" uuid NOT NULL,
	"text" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "town_drafts" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"town_id" uuid NOT NULL,
	"author" uuid NOT NULL,
	"base_rev" integer NOT NULL,
	"skeleton" jsonb NOT NULL,
	"commands" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "town_drafts_town_author" UNIQUE("town_id","author")
);
--> statement-breakpoint
CREATE TABLE "town_revisions" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"town_id" uuid NOT NULL,
	"rev" integer NOT NULL,
	"key" text NOT NULL,
	"skeleton" jsonb NOT NULL,
	"houses" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"committed_by" uuid,
	"message" text DEFAULT '' NOT NULL,
	"committed_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "town_revisions_town_rev" UNIQUE("town_id","rev")
);
--> statement-breakpoint
ALTER TABLE "notices" ADD CONSTRAINT "notices_character_id_characters_id_fk" FOREIGN KEY ("character_id") REFERENCES "public"."characters"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "town_drafts" ADD CONSTRAINT "town_drafts_town_id_towns_id_fk" FOREIGN KEY ("town_id") REFERENCES "public"."towns"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "town_drafts" ADD CONSTRAINT "town_drafts_author_players_id_fk" FOREIGN KEY ("author") REFERENCES "public"."players"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "town_revisions" ADD CONSTRAINT "town_revisions_town_id_towns_id_fk" FOREIGN KEY ("town_id") REFERENCES "public"."towns"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "town_revisions" ADD CONSTRAINT "town_revisions_committed_by_players_id_fk" FOREIGN KEY ("committed_by") REFERENCES "public"."players"("id") ON DELETE set null ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "notices_character" ON "notices" USING btree ("character_id");--> statement-breakpoint
CREATE INDEX "town_revisions_key" ON "town_revisions" USING btree ("key");