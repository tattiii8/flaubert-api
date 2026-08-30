using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Data;
using Flaubert.Drive.Models;
using Flaubert.Drive.Services;

namespace Flaubert.Drive.Controllers
{
    [ApiController]
    [AllowAnonymous] // 内部管理 API (Management Console / バックエンド通信用)
    [Route("internal/tenants")]
    public class TenantInternalController : ControllerBase
    {
        private readonly DriveDbContext _dbContext;
        private readonly IStorageService _storageService;
        private readonly ITenantPolicyService _policyService;

        public TenantInternalController(
            DriveDbContext dbContext,
            IStorageService storageService,
            ITenantPolicyService policyService)
        {
            _dbContext = dbContext;
            _storageService = storageService;
            _policyService = policyService;
        }

        private static bool IsValidTenantId(string tenantId)
        {
            return !string.IsNullOrWhiteSpace(tenantId)
                && Regex.IsMatch(tenantId, @"^[a-zA-Z0-9_-]+$");
        }

        /// <summary>
        /// テナント専用DBスキーマを初期化する。
        ///
        /// 既存の app_{tenantId} スキーマは削除され、
        /// Folders / Files / AuditLogs が仮想ファイルシステム用の
        /// 最新構造で再作成される。
        ///
        /// 注意:
        /// この処理では既存DBデータがすべて削除される。
        /// </summary>
        [HttpPost("{tenantId}/initialize")]
        public async Task<IActionResult> InitializeTenant(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest(
                    "無効なテナントID形式です。英数字、ハイフン、アンダースコアのみ使用できます。");
            }

            string schemaName = $"app_{tenantId}";

            try
            {
#pragma warning disable EF1002

                // ============================================================
                // 1. 共通 TenantSettings テーブル
                // ============================================================

                var createTenantSettingsSql = @"
                    CREATE TABLE IF NOT EXISTS ""public"".""TenantSettings"" (
                        ""TenantId"" VARCHAR(100) PRIMARY KEY,
                        ""MaxStorageBytes"" BIGINT NOT NULL DEFAULT 5368709120,
                        ""MaxFileSizeBytes"" BIGINT NOT NULL DEFAULT 524288000,
                        ""Status"" VARCHAR(50) NOT NULL DEFAULT 'Active',
                        ""UpdatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                ";

                await _dbContext.Database.ExecuteSqlRawAsync(
                    createTenantSettingsSql);

                // ============================================================
                // 2. 既存テナントスキーマを完全削除
                //
                // 今回は既存データを破棄してよいため、
                // 古いDB構造を残さないようDROP CASCADEする。
                //
                // S3のデータはここでは削除しない。
                // ============================================================

                await _dbContext.Database.ExecuteSqlRawAsync(
                    $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE;");

                // ============================================================
                // 3. テナント専用スキーマ作成
                // ============================================================

                await _dbContext.Database.ExecuteSqlRawAsync(
                    $"CREATE SCHEMA \"{schemaName}\";");

                // ============================================================
                // 4. Folders
                //
                // 仮想ファイルシステムのフォルダ階層をDBで管理する。
                //
                // ParentId:
                //     親フォルダへの自己参照
                //
                // TenantId:
                //     テナント識別
                //
                // S3の物理キーにはフォルダ階層を持たせない。
                // ============================================================

                var createFoldersSql = $@"
                    CREATE TABLE ""{schemaName}"".""Folders"" (
                        ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),

                        ""TenantId"" VARCHAR(100) NOT NULL,

                        ""Name"" VARCHAR(255) NOT NULL,

                        ""ParentId"" UUID NULL
                            REFERENCES ""{schemaName}"".""Folders""(""Id"")
                            ON DELETE CASCADE,

                        ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL
                            DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX ""IX_Folders_TenantId_ParentId""
                        ON ""{schemaName}"".""Folders""
                        (""TenantId"", ""ParentId"");
                ";

                await _dbContext.Database.ExecuteSqlRawAsync(
                    createFoldersSql);

                // ============================================================
                // 5. Files
                //
                // ファイルの論理情報とS3上の物理オブジェクトを分離する。
                //
                // FolderId:
                //     仮想フォルダを参照
                //
                // StorageKey:
                //     S3上の物理キー
                //
                // 例:
                //     flaubert/550e8400-e29b-41d4-a716-446655440000
                //
                // 仮想パス:
                //     /Podcast/2026/episode01.mp3
                //
                // この2つは独立している。
                // ============================================================

                var createFilesSql = $@"
                    CREATE TABLE ""{schemaName}"".""Files"" (
                        ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),

                        ""TenantId"" VARCHAR(100) NOT NULL,

                        ""FileName"" VARCHAR(255) NOT NULL,

                        ""ContentType"" VARCHAR(100),

                        ""ByteSize"" BIGINT NOT NULL DEFAULT 0,

                        ""StorageKey"" TEXT NOT NULL,

                        ""FolderId"" UUID NULL
                            REFERENCES ""{schemaName}"".""Folders""(""Id"")
                            ON DELETE SET NULL,

                        ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL
                            DEFAULT CURRENT_TIMESTAMP,

                        ""UpdatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL
                            DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX ""IX_Files_TenantId_FolderId""
                        ON ""{schemaName}"".""Files""
                        (""TenantId"", ""FolderId"");

                    CREATE UNIQUE INDEX ""UX_Files_TenantId_StorageKey""
                        ON ""{schemaName}"".""Files""
                        (""TenantId"", ""StorageKey"");
                ";

                await _dbContext.Database.ExecuteSqlRawAsync(
                    createFilesSql);

                // ============================================================
                // 6. AuditLogs
                // ============================================================

                var createAuditLogsSql = $@"
                    CREATE TABLE ""{schemaName}"".""AuditLogs"" (
                        ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),

                        ""UserId"" VARCHAR(255),

                        ""Action"" VARCHAR(100) NOT NULL,

                        ""TargetId"" UUID,

                        ""TargetName"" VARCHAR(255),

                        ""ByteSize"" BIGINT,

                        ""IpAddress"" VARCHAR(100),

                        ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL
                            DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX ""IX_AuditLogs_CreatedAt""
                        ON ""{schemaName}"".""AuditLogs""
                        (""CreatedAt"" DESC);

                    CREATE INDEX ""IX_AuditLogs_Action""
                        ON ""{schemaName}"".""AuditLogs""
                        (""Action"");
                ";

                await _dbContext.Database.ExecuteSqlRawAsync(
                    createAuditLogsSql);

#pragma warning restore EF1002

                // ============================================================
                // 7. TenantSettingsのデフォルトレコード作成
                // ============================================================

                await _policyService.GetOrCreateTenantSettingAsync(tenantId);

                // ============================================================
                // 8. 初期化完了
                // ============================================================

                return Ok(new
                {
                    message = $"Drive schema '{schemaName}' initialized successfully.",
                    tenantId = tenantId,
                    schema = schemaName,
                    database = new
                    {
                        folders = $"{schemaName}.Folders",
                        files = $"{schemaName}.Files",
                        auditLogs = $"{schemaName}.AuditLogs"
                    },
                    virtualFileSystem = new
                    {
                        enabled = true,
                        folderHierarchy = "Folders.ParentId",
                        fileFolderRelation = "Files.FolderId",
                        storageKey = "Files.StorageKey"
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = "テナントDBの初期化に失敗しました。",
                    tenantId = tenantId,
                    schema = schemaName,
                    detail = ex.Message
                });
            }
        }

        /// <summary>
        /// テナントを完全削除する。
        /// S3オブジェクトとDBスキーマを削除する。
        /// </summary>
        [HttpDelete("{tenantId}")]
        public async Task<IActionResult> DeleteTenant(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest(
                    "無効なテナントID形式です。英数字、ハイフン、アンダースコアのみ使用できます。");
            }

            // 1. S3 オブジェクトの削除
            try
            {
                await _storageService.DeletePrefixAsync(tenantId);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = $"S3データの削除中にエラーが発生しました: {ex.Message}"
                });
            }

            // 2. DB スキーマの削除
            string schemaName = $"app_{tenantId}";

#pragma warning disable EF1002
            await _dbContext.Database.ExecuteSqlRawAsync(
                $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE;");
#pragma warning restore EF1002

            // 3. TenantSettings レコードの削除
            var setting = await _dbContext.TenantSettings
                .FirstOrDefaultAsync(s => s.TenantId == tenantId);

            if (setting != null)
            {
                _dbContext.TenantSettings.Remove(setting);
                await _dbContext.SaveChangesAsync();
            }

            return NoContent();
        }

        /// <summary>
        /// テナントごとのストレージ使用量・統計情報を取得する
        /// (Management Console 用)
        /// </summary>
        [HttpGet("{tenantId}/stats")]
        public async Task<IActionResult> GetTenantStats(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            var setting =
                await _policyService.GetOrCreateTenantSettingAsync(tenantId);

            string schemaName = $"app_{tenantId}";

            try
            {
                using var conn = _dbContext.Database.GetDbConnection();
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();

                // ファイル数・容量
                cmd.CommandText = $@"
                    SELECT
                        COUNT(*)::int AS total_files,
                        COALESCE(SUM(""ByteSize""), 0)::bigint AS total_bytes
                    FROM ""{schemaName}"".""Files"";
                ";

                int totalFiles = 0;
                long totalBytes = 0;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        totalFiles = reader.GetInt32(0);
                        totalBytes = reader.GetInt64(1);
                    }
                }

                // フォルダ数
                cmd.CommandText =
                    $@"SELECT COUNT(*)::int
                       FROM ""{schemaName}"".""Folders"";";

                int totalFolders = 0;

                var folderCountObj = await cmd.ExecuteScalarAsync();

                if (folderCountObj != null &&
                    folderCountObj != DBNull.Value)
                {
                    totalFolders = Convert.ToInt32(folderCountObj);
                }

                // MIME種別ごとの内訳
                cmd.CommandText = $@"
                    SELECT
                        COALESCE(""ContentType"", 'unknown') AS content_type,
                        COALESCE(SUM(""ByteSize""), 0)::bigint AS bytes
                    FROM ""{schemaName}"".""Files""
                    GROUP BY ""ContentType"";
                ";

                var breakdown = new Dictionary<string, long>();

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var ct = reader.GetString(0);
                        var bytes = reader.GetInt64(1);

                        breakdown[ct] = bytes;
                    }
                }

                var stats = new TenantStatsDto
                {
                    TenantId = tenantId,
                    TotalFiles = totalFiles,
                    TotalFolders = totalFolders,
                    TotalBytes = totalBytes,
                    QuotaBytes = setting.MaxStorageBytes,
                    Status = setting.Status,
                    ContentTypeBreakdown = breakdown
                };

                return Ok(stats);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = $"統計情報の取得に失敗しました: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 全テナントの設定一覧を取得する
        /// (Management Console 用)
        /// </summary>
        [HttpGet("settings")]
        public async Task<IActionResult> GetAllTenantSettings()
        {
            var settings =
                await _dbContext.TenantSettings.ToListAsync();

            return Ok(settings);
        }

        /// <summary>
        /// テナントの設定（クォータ・ステータス）を取得する
        /// </summary>
        [HttpGet("{tenantId}/settings")]
        public async Task<IActionResult> GetTenantSetting(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            var setting =
                await _policyService.GetOrCreateTenantSettingAsync(tenantId);

            return Ok(setting);
        }

        /// <summary>
        /// テナントの設定（クォータ・ステータス:
        /// Active/ReadOnly/Suspended）を更新する
        /// (Management Console 用)
        /// </summary>
        [HttpPut("{tenantId}/settings")]
        public async Task<IActionResult> UpdateTenantSetting(
            string tenantId,
            [FromBody] UpdateTenantSettingRequest request)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            var setting =
                await _policyService.GetOrCreateTenantSettingAsync(tenantId);

            if (request.MaxStorageBytes.HasValue)
            {
                setting.MaxStorageBytes =
                    request.MaxStorageBytes.Value;
            }

            if (request.MaxFileSizeBytes.HasValue)
            {
                setting.MaxFileSizeBytes =
                    request.MaxFileSizeBytes.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                var normalizedStatus =
                    request.Status.Trim();

                if (normalizedStatus.Equals(
                        "Active",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizedStatus.Equals(
                        "ReadOnly",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizedStatus.Equals(
                        "Suspended",
                        StringComparison.OrdinalIgnoreCase))
                {
                    setting.Status =
                        char.ToUpper(normalizedStatus[0]) +
                        normalizedStatus.Substring(1).ToLower();
                }
                else
                {
                    return BadRequest(
                        "ステータスは 'Active', 'ReadOnly', 'Suspended' のいずれかを指定してください。");
                }
            }

            setting.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            return Ok(setting);
        }

        /// <summary>
        /// S3とDBの不整合（孤立ファイル）を検出する
        /// </summary>
        [HttpGet("{tenantId}/orphans")]
        public async Task<IActionResult> DetectOrphans(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            string schemaName = $"app_{tenantId}";

            try
            {
                // 1. S3 オブジェクトの一覧取得
                var s3Keys =
                    await _storageService.ListObjectsAsync(tenantId);

                var s3KeySet =
                    new HashSet<string>(s3Keys);

                // 2. DB 内のファイルメタデータ一覧取得
                var dbFiles = new List<FileMetadata>();

                using (var conn =
                    _dbContext.Database.GetDbConnection())
                {
                    await conn.OpenAsync();

                    using var cmd = conn.CreateCommand();

                    cmd.CommandText = $@"
                        SELECT
                            ""Id"",
                            ""FileName"",
                            ""ContentType"",
                            ""ByteSize"",
                            ""StorageKey"",
                            ""FolderId"",
                            ""CreatedAt""
                        FROM ""{schemaName}"".""Files"";
                    ";

                    using var reader =
                        await cmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        dbFiles.Add(new FileMetadata
                        {
                            Id = reader.GetGuid(0),
                            FileName = reader.GetString(1),
                            ContentType =
                                reader.IsDBNull(2)
                                    ? ""
                                    : reader.GetString(2),
                            ByteSize = reader.GetInt64(3),
                            StorageKey = reader.GetString(4),
                            FolderId =
                                reader.IsDBNull(5)
                                    ? null
                                    : reader.GetGuid(5),
                            CreatedAt = reader.GetDateTime(6)
                        });
                    }
                }

                var dbKeySet =
                    new HashSet<string>(
                        dbFiles.Select(f => f.StorageKey));

                // 3. 不整合の検出
                var danglingInS3 =
                    s3Keys
                        .Where(k => !dbKeySet.Contains(k))
                        .ToList();

                var missingInS3 =
                    dbFiles
                        .Where(f => !s3KeySet.Contains(f.StorageKey))
                        .ToList();

                var result = new OrphanDetectionResult
                {
                    TenantId = tenantId,
                    DanglingS3Objects = danglingInS3,
                    MissingS3Objects = missingInS3
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error =
                        $"孤立ファイル検出中にエラーが発生しました: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 孤立ファイルのクリーンアップを実行する
        /// </summary>
        [HttpPost("{tenantId}/orphans/cleanup")]
        public async Task<IActionResult> CleanupOrphans(
            string tenantId,
            [FromBody] CleanupOrphansRequest request)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            var orphanResultObj =
                await DetectOrphans(tenantId);

            if (orphanResultObj is not OkObjectResult okResult ||
                okResult.Value is not OrphanDetectionResult orphans)
            {
                return StatusCode(
                    500,
                    new
                    {
                        error = "孤立ファイルの特定に失敗しました。"
                    });
            }

            int purgedS3Count = 0;
            int removedDbCount = 0;

            // 1. S3 孤立オブジェクトのパージ
            if (request.PurgeDanglingS3Objects &&
                orphans.DanglingS3Objects.Count > 0)
            {
                foreach (var key in
                         orphans.DanglingS3Objects)
                {
                    try
                    {
                        await _storageService.DeleteAsync(key);
                        purgedS3Count++;
                    }
                    catch
                    {
                        // ログ記録
                    }
                }
            }

            // 2. 実体のない DB メタデータの削除
            if (request.RemoveMissingDbRecords &&
                orphans.MissingS3Objects.Count > 0)
            {
                string schemaName = $"app_{tenantId}";

                var idsToRemove =
                    orphans.MissingS3Objects
                        .Select(f => $"'{f.Id}'")
                        .ToList();

                var inClause =
                    string.Join(",", idsToRemove);

#pragma warning disable EF1002

                removedDbCount =
                    await _dbContext.Database.ExecuteSqlRawAsync(
                        $@"
                            DELETE FROM ""{schemaName}"".""Files""
                            WHERE ""Id"" IN ({inClause});
                        ");

#pragma warning restore EF1002
            }

            return Ok(new
            {
                message = "クリーンアップが完了しました。",
                purgedS3Objects = purgedS3Count,
                removedDbRecords = removedDbCount
            });
        }

        /// <summary>
        /// テナントの操作監査ログを取得する
        /// (Management Console 用)
        /// </summary>
        [HttpGet("{tenantId}/audit-logs")]
        public async Task<IActionResult> GetTenantAuditLogs(
            string tenantId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? action = null)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            string schemaName = $"app_{tenantId}";

            if (page < 1)
            {
                page = 1;
            }

            if (pageSize < 1 || pageSize > 200)
            {
                pageSize = 50;
            }

            try
            {
                var logs = new List<AuditLog>();

                using (var conn =
                    _dbContext.Database.GetDbConnection())
                {
                    await conn.OpenAsync();

                    using var cmd = conn.CreateCommand();

                    string filterSql = "";

                    if (!string.IsNullOrWhiteSpace(action))
                    {
                        var sanitizedAction =
                            Regex.Replace(
                                action,
                                @"[^a-zA-Z0-9_-]",
                                "");

                        filterSql =
                            $@"WHERE LOWER(""Action"")
                               LIKE '%{sanitizedAction.ToLower()}%'";
                    }

                    int offset =
                        (page - 1) * pageSize;

                    cmd.CommandText = $@"
                        SELECT
                            ""Id"",
                            ""UserId"",
                            ""Action"",
                            ""TargetId"",
                            ""TargetName"",
                            ""ByteSize"",
                            ""IpAddress"",
                            ""CreatedAt""
                        FROM ""{schemaName}"".""AuditLogs""
                        {filterSql}
                        ORDER BY ""CreatedAt"" DESC
                        LIMIT {pageSize}
                        OFFSET {offset};
                    ";

                    using var reader =
                        await cmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        logs.Add(new AuditLog
                        {
                            Id = reader.GetGuid(0),
                            UserId =
                                reader.IsDBNull(1)
                                    ? null
                                    : reader.GetString(1),
                            Action = reader.GetString(2),
                            TargetId =
                                reader.IsDBNull(3)
                                    ? null
                                    : reader.GetGuid(3),
                            TargetName =
                                reader.IsDBNull(4)
                                    ? null
                                    : reader.GetString(4),
                            ByteSize =
                                reader.IsDBNull(5)
                                    ? null
                                    : reader.GetInt64(5),
                            IpAddress =
                                reader.IsDBNull(6)
                                    ? null
                                    : reader.GetString(6),
                            CreatedAt =
                                reader.GetDateTime(7)
                        });
                    }
                }

                return Ok(new
                {
                    page,
                    pageSize,
                    logs
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error =
                        $"監査ログ取得中にエラーが発生しました: {ex.Message}"
                });
            }
        }
    }
}