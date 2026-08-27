using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Data;
using Flaubert.Drive.Services;

namespace Flaubert.Drive.Controllers
{
    [ApiController]
    [AllowAnonymous] // 👈 内部管理APIはアクセストークン不要
    [Route("internal/tenants")]
    public class TenantInternalController : ControllerBase
    {
        private readonly DriveDbContext _dbContext;
        private readonly IStorageService _storageService; // 👈 S3操作サービスの追加

        public TenantInternalController(DriveDbContext dbContext, IStorageService storageService) // 👈 DIでIStorageServiceを受け取る
        {
            _dbContext = dbContext;
            _storageService = storageService;
        }

        [HttpPost("{tenantId}/initialize")]
        public async Task<IActionResult> InitializeTenant(string tenantId)
        {
            // SQLインジェクション（パストラバーサル含む）を防ぐための入力検証
            if (string.IsNullOrWhiteSpace(tenantId) || !Regex.IsMatch(tenantId, @"^[a-zA-Z0-9_-]+$"))
            {
                return BadRequest("無効なテナントID形式です。英数字、ハイフン、アンダースコアのみ使用できます");
            }

            string schemaName = $"app_{tenantId}";

            #pragma warning disable EF1002 // パラメータ化できないDDL識別子のため、事前サニタイズの上で警告を抑制
            await _dbContext.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\";");

            var createFilesSql = $@"
                CREATE TABLE IF NOT EXISTS ""{schemaName}"".""Files"" (
                    ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""FileName"" VARCHAR(255) NOT NULL,
                    ""ContentType"" VARCHAR(100),
                    ""ByteSize"" BIGINT NOT NULL DEFAULT 0,
                    ""StoragePath"" TEXT NOT NULL,
                    ""CreatedAt"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";
            await _dbContext.Database.ExecuteSqlRawAsync(createFilesSql);

            var createFoldersSql = $@"
                CREATE TABLE IF NOT EXISTS ""{schemaName}"".""Folders"" (
                    ""Id"" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""Name"" VARCHAR(255) NOT NULL,
                    ""ParentId"" UUID REFERENCES ""{schemaName}"".""Folders""(""Id"") ON DELETE CASCADE,
                    ""CreatedAt"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );";
            await _dbContext.Database.ExecuteSqlRawAsync(createFoldersSql);
            #pragma warning restore EF1002

            return Ok(new { message = $"Drive schema '{schemaName}' initialized successfully." });
        }

        [HttpDelete("{tenantId}")]
        public async Task<IActionResult> DeleteTenant(string tenantId)
        {
            // SQLインジェクションを防ぐための入力検証
            if (string.IsNullOrWhiteSpace(tenantId) || !Regex.IsMatch(tenantId, @"^[a-zA-Z0-9_-]+$"))
            {
                return BadRequest("無効なテナントID形式です。英数字、ハイフン、アンダースコアのみ使用できます。");
            }

            // 1. S3 内の対象テナント用フォルダ（{tenantId}/...）のファイルを一括削除
            try
            {
                await _storageService.DeletePrefixAsync(tenantId);
            }
            catch (Exception ex)
            {
                // 必要に応じてログを出力
                return StatusCode(500, new { error = $"S3データの削除中にエラーが発生しました: {ex.Message}" });
            }

            // 2. DBのアプリケーションスキーマを削除
            string schemaName = $"app_{tenantId}";

            #pragma warning disable EF1002
            await _dbContext.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE;");
            #pragma warning restore EF1002

            return NoContent();
        }
    }
}
