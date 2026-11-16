CREATE TABLE "town_traffic" (
	"town_key" text PRIMARY KEY NOT NULL,
	"config" jsonb NOT NULL,
	"version" integer DEFAULT 0 NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
