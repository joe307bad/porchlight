-- A town someone made is now theirs alone (only the towns that come with the game are everyone's):
-- anyone else's characters who lived in one move out, and choose a town again. What they've earned stays.
UPDATE "characters" SET "town_id" = NULL, "home_id" = NULL
FROM "towns"
WHERE "characters"."town_id" = "towns"."id" AND "towns"."created_by" IS NOT NULL AND "characters"."player_id" <> "towns"."created_by";--> statement-breakpoint
DELETE FROM "homes" USING "towns"
WHERE "homes"."town_id" = "towns"."id" AND "towns"."created_by" IS NOT NULL AND "homes"."player_id" <> "towns"."created_by";
