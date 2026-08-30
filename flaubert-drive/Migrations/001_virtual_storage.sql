-- Flaubert Drive: virtual filesystem / UUID-only S3 key migration
-- PostgreSQL / per-tenant schema design.
-- Run once before deploying the new API.

BEGIN;

-- Existing tenants are registered in public.TenantSettings.
DO $$
DECLARE
    t record;
BEGIN
    FOR t IN SELECT "TenantId" FROM "public"."TenantSettings" LOOP
        EXECUTE format('ALTER TABLE %I."Files" ADD COLUMN IF NOT EXISTS "TenantId" varchar(100)', 'app_' || t."TenantId");
        EXECUTE format('ALTER TABLE %I."Files" ADD COLUMN IF NOT EXISTS "StorageKey" text', 'app_' || t."TenantId");
        EXECUTE format('ALTER TABLE %I."Files" ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP', 'app_' || t."TenantId");
        EXECUTE format('ALTER TABLE %I."Folders" ADD COLUMN IF NOT EXISTS "TenantId" varchar(100)', 'app_' || t."TenantId");

        EXECUTE format('UPDATE %I."Files" SET "TenantId" = $1 WHERE "TenantId" IS NULL', 'app_' || t."TenantId") USING t."TenantId";
        EXECUTE format('UPDATE %I."Folders" SET "TenantId" = $1 WHERE "TenantId" IS NULL', 'app_' || t."TenantId") USING t."TenantId";
        EXECUTE format('UPDATE %I."Files" SET "StorageKey" = "StoragePath" WHERE "StorageKey" IS NULL', 'app_' || t."TenantId");

        EXECUTE format('ALTER TABLE %I."Files" ALTER COLUMN "TenantId" SET NOT NULL', 'app_' || t."TenantId");
        EXECUTE format('ALTER TABLE %I."Files" ALTER COLUMN "StorageKey" SET NOT NULL', 'app_' || t."TenantId");
        EXECUTE format('ALTER TABLE %I."Folders" ALTER COLUMN "TenantId" SET NOT NULL', 'app_' || t."TenantId");

        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I."Files" ("TenantId", "FolderId")', 'IX_Files_TenantId_FolderId', 'app_' || t."TenantId");
        EXECUTE format('CREATE UNIQUE INDEX IF NOT EXISTS %I ON %I."Files" ("TenantId", "StorageKey")', 'UX_Files_TenantId_StorageKey', 'app_' || t."TenantId");
        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I."Folders" ("TenantId", "ParentId")', 'IX_Folders_TenantId_ParentId', 'app_' || t."TenantId");
    END LOOP;
END $$;

COMMIT;

-- IMPORTANT:
-- StoragePath is intentionally retained during rollout for rollback/audit.
-- After S3 migration has been verified, run 003_drop_storagepath.sql.
