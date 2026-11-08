ALTER TABLE "towns" ADD COLUMN "slot" integer;--> statement-breakpoint
-- The towns people have made go in their places north of Porchlight, in the order they were made.
UPDATE "towns" SET "slot" = ranked."n" - 1
FROM (SELECT "id", row_number() OVER (PARTITION BY "created_by" ORDER BY "created_at", "id") AS "n" FROM "towns" WHERE "created_by" IS NOT NULL) AS ranked
WHERE "towns"."id" = ranked."id";--> statement-breakpoint
ALTER TABLE "towns" ADD CONSTRAINT "towns_slot" UNIQUE("created_by","slot");
