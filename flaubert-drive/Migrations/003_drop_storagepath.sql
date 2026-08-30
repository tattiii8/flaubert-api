-- Run only after S3 migration + application verification.
BEGIN;
DO $$
DECLARE t record;
BEGIN
  FOR t IN SELECT "TenantId" FROM "public"."TenantSettings" LOOP
    EXECUTE format('ALTER TABLE %I."Files" DROP COLUMN IF EXISTS "StoragePath"', 'app_' || t."TenantId");
  END LOOP;
END $$;
COMMIT;
