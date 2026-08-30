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
            return !string.IsNullOrWhiteSpace(tenantId) && Regex.IsMatch(tenantId, @"^[a-zA-Z0-9_-]+$");
        }

        [HttpPost("{tenantId}/initialize")]
        public async Task<IActionResult> InitializeTenant(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。英数字、ハイフン、アンダースコアのみ使用できます。");
            }

            string schemaName = $"app_{tenantId}";

            #pragma warning disable EF1002
            // 1. 共通 TenantSettings テーブルの作成 (public スキーマ)
            var createTenantSettingsSql = @"
                CREATE TABLE IF NOT EXISTS ""public"".""TenantSettings"" (
                    ""TenantId"" VARCHAR(100) PRIMARY KEY,
                    ""MaxStorageBytes"" BIGINT NOT NULL DEFAULT 5368709120,
                    ""MaxFileSizeBytes"" BIGINT NOT NULL DEFAULT 524288000,
                    ""Status"" VARCHAR(50) NOT NULL DEFAULT 'Active',
                    ""UpdatedAt"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";
            await _dbContext.Database.ExecuteSqlRawAsync(createTenantSettingsSql);

            // 2. テナント専用スキーマの作成
            await _dbContext.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\";");

            // 3. Folders テーブルの作成
            var createFoldersSql = $@"
                CREATE TABLE IF NOT EXISTS ""{schemaName}"".""Folders"" (
                    ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""Name"" VARCHAR(255) NOT NULL,
                    ""ParentId"" UUID REFERENCES ""{schemaName}"".""Folders""(""Id"") ON DELETE CASCADE,
                    ""CreatedAt"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";
            await _dbContext.Database.ExecuteSqlRawAsync(createFoldersSql);

            // 4. Files テーブルの作成 (FolderId 外部キー含む)
            var createFilesSql = $@"
                CREATE TABLE IF NOT EXISTS ""{schemaName}"".""Files"" (
                    ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""FileName"" VARCHAR(255) NOT NULL,
                    ""ContentType"" VARCHAR(100),
                    ""ByteSize"" BIGINT NOT NULL DEFAULT 0,
                    ""StorageKey"" TEXT NOT NULL,
                    ""FolderId"" UUID REFERENCES ""{schemaName}"".""Folders""(""Id"") ON DELETE SET NULL,
                    ""CreatedAt"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";
            await _dbContext.Database.ExecuteSqlRawAsync(createFilesSql);

            // 5. AuditLogs テーブルの作成
            var createAuditLogsSql = $@"
                CREATE TABLE IF NOT EXISTS ""{schemaName}"".""AuditLogs"" (
                    ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""UserId"" VARCHAR(255),
                    ""Action"" VARCHAR(100) NOT NULL,
                    ""TargetId"" UUID,
                    ""TargetName"" VARCHAR(255),
                    ""ByteSize"" BIGINT,
                    ""IpAddress"" VARCHAR(100),
                    ""CreatedAt"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";
            await _dbContext.Database.ExecuteSqlRawAsync(createAuditLogsSql);
            #pragma warning restore EF1002

            // 6. デフォルト設定レコードの作成
            await _policyService.GetOrCreateTenantSettingAsync(tenantId);

            return Ok(new { message = $"Drive schema '{schemaName}' initialized successfully." });
        }

        [HttpDelete("{tenantId}")]
        public async Task<IActionResult> DeleteTenant(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。英数字、ハイフン、アンダースコアのみ使用できます。");
            }

            // 1. S3 オブジェクトの削除
            try
            {
                await _storageService.DeletePrefixAsync(tenantId);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"S3データの削除中にエラーが発生しました: {ex.Message}" });
            }

            // 2. DB スキーマの削除
            string schemaName = $"app_{tenantId}";
            #pragma warning disable EF1002
            await _dbContext.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE;");
            #pragma warning restore EF1002

            // 3. TenantSettings レコードの削除
            var setting = await _dbContext.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
            if (setting != null)
            {
                _dbContext.TenantSettings.Remove(setting);
                await _dbContext.SaveChangesAsync();
            }

            return NoContent();
        }

        /// <summary>
        /// テナントごとのストレージ使用量・統計情報を取得する (Management Console 用)
        /// </summary>
        [HttpGet("{tenantId}/stats")]
        public async Task<IActionResult> GetTenantStats(string tenantId)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            var setting = await _policyService.GetOrCreateTenantSettingAsync(tenantId);
            string schemaName = $"app_{tenantId}";

            try
            {
                using var conn = _dbContext.Database.GetDbConnection();
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT 
                        COUNT(*)::int AS total_files,
                        COALESCE(SUM(""ByteSize""), 0)::bigint AS total_bytes
                    FROM ""{schemaName}"".""Files"";";

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

                // フォルダ数取得
                cmd.CommandText = $@"SELECT COUNT(*)::int FROM ""{schemaName}"".""Folders"";";
                int totalFolders = 0;
                var folderCountObj = await cmd.ExecuteScalarAsync();
                if (folderCountObj != null && folderCountObj != DBNull.Value)
                {
                    totalFolders = Convert.ToInt32(folderCountObj);
                }

                // MIME種別ごとの内訳
                cmd.CommandText = $@"
                    SELECT COALESCE(""ContentType"", 'unknown') AS content_type, COALESCE(SUM(""ByteSize""), 0)::bigint AS bytes
                    FROM ""{schemaName}"".""Files""
                    GROUP BY ""ContentType"";";

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
                return StatusCode(500, new { error = $"統計情報の取得に失敗しました: {ex.Message}" });
            }
        }

        /// <summary>
        /// 全テナントの設定一覧を取得する (Management Console 用)
        /// </summary>
        [HttpGet("settings")]
        public async Task<IActionResult> GetAllTenantSettings()
        {
            var settings = await _dbContext.TenantSettings.ToListAsync();
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

            var setting = await _policyService.GetOrCreateTenantSettingAsync(tenantId);
            return Ok(setting);
        }

        /// <summary>
        /// テナントの設定（クォータ・ステータス: Active/ReadOnly/Suspended）を更新する (Management Console 用)
        /// </summary>
        [HttpPut("{tenantId}/settings")]
        public async Task<IActionResult> UpdateTenantSetting(string tenantId, [FromBody] UpdateTenantSettingRequest request)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            var setting = await _policyService.GetOrCreateTenantSettingAsync(tenantId);

            if (request.MaxStorageBytes.HasValue)
            {
                setting.MaxStorageBytes = request.MaxStorageBytes.Value;
            }

            if (request.MaxFileSizeBytes.HasValue)
            {
                setting.MaxFileSizeBytes = request.MaxFileSizeBytes.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                var normalizedStatus = request.Status.Trim();
                if (normalizedStatus.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                    normalizedStatus.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase) ||
                    normalizedStatus.Equals("Suspended", StringComparison.OrdinalIgnoreCase))
                {
                    setting.Status = char.ToUpper(normalizedStatus[0]) + normalizedStatus.Substring(1).ToLower();
                }
                else
                {
                    return BadRequest("ステータスは 'Active', 'ReadOnly', 'Suspended' のいずれかを指定してください。");
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
                // 1. S3 オブジェクトキーの一覧取得
                var s3Keys = await _storageService.ListObjectsAsync(tenantId);
                var s3KeySet = new HashSet<string>(s3Keys);

                // 2. DB 内のファイルメタデータ一覧取得
                var dbFiles = new List<FileMetadata>();
                using (var conn = _dbContext.Database.GetDbConnection())
                {
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"
                        SELECT ""Id"", ""FileName"", ""ContentType"", ""ByteSize"", ""StorageKey"", ""FolderId"", ""CreatedAt""
                        FROM ""{schemaName}"".""Files"";";

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        dbFiles.Add(new FileMetadata
                        {
                            Id = reader.GetGuid(0),
                            FileName = reader.GetString(1),
                            ContentType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            ByteSize = reader.GetInt64(3),
                            StorageKey = reader.GetString(4),
                            FolderId = reader.IsDBNull(5) ? null : reader.GetGuid(5),
                            CreatedAt = reader.GetDateTime(6)
                        });
                    }
                }

                var dbKeySet = new HashSet<string>(dbFiles.Select(f => f.StorageKey));

                // 3. 不整合の検出
                // S3にあるがDBにないもの
                var danglingInS3 = s3Keys.Where(k => !dbKeySet.Contains(k)).ToList();
                // DBにあるがS3にないもの
                var missingInS3 = dbFiles.Where(f => !s3KeySet.Contains(f.StorageKey)).ToList();

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
                return StatusCode(500, new { error = $"孤立ファイル検出中にエラーが発生しました: {ex.Message}" });
            }
        }

        /// <summary>
        /// 孤立ファイルのクリーンアップを実行する
        /// </summary>
        [HttpPost("{tenantId}/orphans/cleanup")]
        public async Task<IActionResult> CleanupOrphans(string tenantId, [FromBody] CleanupOrphansRequest request)
        {
            if (!IsValidTenantId(tenantId))
            {
                return BadRequest("無効なテナントID形式です。");
            }

            var orphanResultObj = await DetectOrphans(tenantId);
            if (orphanResultObj is not OkObjectResult okResult || okResult.Value is not OrphanDetectionResult orphans)
            {
                return StatusCode(500, new { error = "孤立ファイルの特定に失敗しました。" });
            }

            int purgedS3Count = 0;
            int removedDbCount = 0;

            // 1. S3 孤立オブジェクトのパージ
            if (request.PurgeDanglingS3Objects && orphans.DanglingS3Objects.Count > 0)
            {
                foreach (var key in orphans.DanglingS3Objects)
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
            if (request.RemoveMissingDbRecords && orphans.MissingS3Objects.Count > 0)
            {
                string schemaName = $"app_{tenantId}";
                var idsToRemove = orphans.MissingS3Objects.Select(f => $"'{f.Id}'").ToList();
                var inClause = string.Join(",", idsToRemove);

                #pragma warning disable EF1002
                removedDbCount = await _dbContext.Database.ExecuteSqlRawAsync(
                    $@"DELETE FROM ""{schemaName}"".""Files"" WHERE ""Id"" IN ({inClause});");
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
        /// テナントの操作監査ログを取得する (Management Console 用)
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
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 200) pageSize = 50;

            try
            {
                var logs = new List<AuditLog>();
                using (var conn = _dbContext.Database.GetDbConnection())
                {
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();

                    string filterSql = "";
                    if (!string.IsNullOrWhiteSpace(action))
                    {
                        var sanitizedAction = Regex.Replace(action, @"[^a-zA-Z0-9_-]", "");
                        filterSql = $@"WHERE LOWER(""Action"") LIKE '%{sanitizedAction.ToLower()}%'";
                    }

                    int offset = (page - 1) * pageSize;
                    cmd.CommandText = $@"
                        SELECT ""Id"", ""UserId"", ""Action"", ""TargetId"", ""TargetName"", ""ByteSize"", ""IpAddress"", ""CreatedAt""
                        FROM ""{schemaName}"".""AuditLogs""
                        {filterSql}
                        ORDER BY ""CreatedAt"" DESC
                        LIMIT {pageSize} OFFSET {offset};";

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        logs.Add(new AuditLog
                        {
                            Id = reader.GetGuid(0),
                            UserId = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Action = reader.GetString(2),
                            TargetId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                            TargetName = reader.IsDBNull(4) ? null : reader.GetString(4),
                            ByteSize = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                            IpAddress = reader.IsDBNull(6) ? null : reader.GetString(6),
                            CreatedAt = reader.GetDateTime(7)
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
                return StatusCode(500, new { error = $"監査ログ取得中にエラーが発生しました: {ex.Message}" });
            }
        }
    }
}
