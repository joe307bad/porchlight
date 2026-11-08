CREATE TABLE "mission_records" (
	"town_key" text NOT NULL,
	"mission_id" text NOT NULL,
	"player_id" uuid NOT NULL,
	"character_id" uuid,
	"name" text NOT NULL,
	"seconds" real NOT NULL,
	"at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "mission_records_town_key_mission_id_player_id_pk" PRIMARY KEY("town_key","mission_id","player_id")
);
--> statement-breakpoint
ALTER TABLE "mission_records" ADD CONSTRAINT "mission_records_player_id_players_id_fk" FOREIGN KEY ("player_id") REFERENCES "public"."players"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "mission_records" ADD CONSTRAINT "mission_records_character_id_characters_id_fk" FOREIGN KEY ("character_id") REFERENCES "public"."characters"("id") ON DELETE set null ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "mission_records_board" ON "mission_records" USING btree ("town_key","mission_id","seconds");