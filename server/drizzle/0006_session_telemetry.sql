ALTER TABLE "sessions" ADD COLUMN "end_detail" text;--> statement-breakpoint
ALTER TABLE "sessions" ADD COLUMN "client" jsonb;--> statement-breakpoint
ALTER TABLE "sessions" ADD COLUMN "perf" jsonb;--> statement-breakpoint
ALTER TABLE "sessions" ADD COLUMN "drops" integer DEFAULT 0 NOT NULL;