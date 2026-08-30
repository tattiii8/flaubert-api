-- Use this schema definition in TenantInternalController.InitializeTenant.
-- The controller should create these columns for new tenants.

CREATE TABLE IF NOT EXISTS "{schema}"."Folders" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" varchar(100) NOT NULL,
    "Name" varchar(255) NOT NULL,
    "ParentId" uuid NULL REFERENCES "{schema}"."Folders"("Id") ON DELETE CASCADE,
    "CreatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS "{schema}"."Files" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" varchar(100) NOT NULL,
    "FileName" varchar(255) NOT NULL,
    "ContentType" varchar(100),
    "ByteSize" bigint NOT NULL DEFAULT 0,
    "StorageKey" text NOT NULL,
    "FolderId" uuid NULL REFERENCES "{schema}"."Folders"("Id") ON DELETE SET NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS "IX_Files_TenantId_FolderId" ON "{schema}"."Files" ("TenantId", "FolderId");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Files_TenantId_StorageKey" ON "{schema}"."Files" ("TenantId", "StorageKey");
CREATE INDEX IF NOT EXISTS "IX_Folders_TenantId_ParentId" ON "{schema}"."Folders" ("TenantId", "ParentId");
