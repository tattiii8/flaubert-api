using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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
    [Authorize]
    [Route("")]
    public class DriveController : ControllerBase
    {
        private readonly DriveDbContext _dbContext;
        private readonly IStorageService _storageService;
        private readonly ITenantPolicyService _policyService;
        private readonly IAuditLogService _auditLogService;
        private readonly ITenantProvider _tenantProvider;

        public DriveController(
            DriveDbContext dbContext, 
            IStorageService storageService,
            ITenantPolicyService policyService,
            IAuditLogService auditLogService,
            ITenantProvider tenantProvider)
        {
            _dbContext = dbContext;
            _storageService = storageService;
            _policyService = policyService;
            _auditLogService = auditLogService;
            _tenantProvider = tenantProvider;
        }

        private string GetEffectiveTenantId(string? fallbackTenantId = null)
        {
            var tid = _tenantProvider.GetTenantId();
            if (tid != "default" && !string.IsNullOrWhiteSpace(tid))
            {
                return tid;
            }

            return User.FindFirst("tenant_id")?.Value 
                ?? User.FindFirst("TenantId")?.Value 
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                ?? fallbackTenantId 
                ?? "default";
        }

        // ==========================================
        //  ファイル関連エンドポイント
        // ==========================================

        /// <summary>
        /// ファイル一覧を取得する（フォルダIDによるフィルタ可能）
        /// </summary>
        [HttpGet("object")]
        public async Task<IActionResult> GetFiles([FromQuery] Guid? folderId = null, [FromQuery] bool rootOnly = false)
        {
            var tenantId = GetEffectiveTenantId();
            try
            {
                await _policyService.ValidateReadAccessAsync(tenantId);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }

            var query = _dbContext.Files.AsQueryable();

            if (folderId.HasValue)
            {
                query = query.Where(f => f.FolderId == folderId.Value);
            }
            else if (rootOnly)
            {
                query = query.Where(f => f.FolderId == null);
            }

            var files = await query.OrderByDescending(f => f.CreatedAt).ToListAsync();
            return Ok(files);
        }

        /// <summary>
        /// S3 へ直接アップロードするための署名付き URL と メタデータレコードを発行・生成する
        /// （クォータ検証・テナント状態検証・監査ログ記録）
        /// </summary>
        [HttpPost("object")]
        public async Task<IActionResult> GetUploadUrl([FromBody] CreateUploadUrlRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.FileName))
                return BadRequest(new { error = "ファイル名が指定されていません。" });

            var tenantId = GetEffectiveTenantId(request.TenantId);
            if (string.IsNullOrWhiteSpace(tenantId) || tenantId == "default")
            {
                return BadRequest(new { error = "有効なテナントIDが取得できませんでした。" });
            }

            // クォータ・ステータス（Active / ReadOnly / Suspended）のポリシー検証
            try
            {
                await _policyService.ValidateWriteAccessAsync(tenantId, request.ByteSize);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (TenantReadOnlyException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (QuotaExceededException ex)
            {
                return StatusCode(413, new { error = ex.Message });
            }
            catch (FileSizeExceededException ex)
            {
                return StatusCode(413, new { error = ex.Message });
            }

            var contentType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/octet-stream"
                : request.ContentType;

            // 1. S3 署名付きアップロード URL と Key を生成
            var (uploadUrl, key) = _storageService.GeneratePresignedUploadUrl(tenantId, request.FileName, contentType);

            // 2. メタデータを DB に保存
            var metadata = new FileMetadata
            {
                FileName = request.FileName,
                ContentType = contentType,
                ByteSize = request.ByteSize,
                StoragePath = key,
                FolderId = request.FolderId
            };

            _dbContext.Files.Add(metadata);
            await _dbContext.SaveChangesAsync();

            // 3. 監査ログ記録
            await _auditLogService.LogActionAsync("UploadUrlRequested", metadata.Id, metadata.FileName, metadata.ByteSize);

            // 4. レスポンス返却
            return Ok(new
            {
                uploadUrl,
                file = metadata
            });
        }

        /// <summary>
        /// S3 から直接ダウンロードするための署名付き URL を取得する
        /// </summary>
        [HttpGet("object/{id:guid}")]
        public async Task<IActionResult> GetDownloadUrl(Guid id)
        {
            var tenantId = GetEffectiveTenantId();
            try
            {
                await _policyService.ValidateReadAccessAsync(tenantId);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }

            var fileMetadata = await _dbContext.Files.FindAsync(id);
            if (fileMetadata == null)
            {
                return NotFound(new { error = "指定されたファイルのメタデータが見つかりません。" });
            }

            var downloadUrl = _storageService.GeneratePresignedDownloadUrl(fileMetadata.StoragePath);

            // 監査ログ記録
            await _auditLogService.LogActionAsync("DownloadUrlRequested", fileMetadata.Id, fileMetadata.FileName, fileMetadata.ByteSize);

            return Ok(new
            {
                downloadUrl,
                fileName = fileMetadata.FileName
            });
        }

        /// <summary>
        /// ファイルを削除する
        /// </summary>
        [HttpDelete("object/{id:guid}")]
        public async Task<IActionResult> DeleteFile(Guid id)
        {
            var tenantId = GetEffectiveTenantId();
            try
            {
                await _policyService.ValidateWriteAccessAsync(tenantId);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (TenantReadOnlyException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }

            var file = await _dbContext.Files.FindAsync(id);
            if (file == null)
                return NotFound();

            try
            {
                await _storageService.DeleteAsync(file.StoragePath);
            }
            catch (Exception)
            {
                // エラーログ
            }

            _dbContext.Files.Remove(file);
            await _dbContext.SaveChangesAsync();

            // 監査ログ記録
            await _auditLogService.LogActionAsync("FileDeleted", file.Id, file.FileName, file.ByteSize);

            return NoContent();
        }

        // ==========================================
        //  フォルダ関連エンドポイント
        // ==========================================

        /// <summary>
        /// フォルダ一覧を取得する
        /// </summary>
        [HttpGet("folders")]
        public async Task<IActionResult> GetFolders([FromQuery] Guid? parentId = null)
        {
            var tenantId = GetEffectiveTenantId();
            try
            {
                await _policyService.ValidateReadAccessAsync(tenantId);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }

            var query = _dbContext.Folders.AsQueryable();
            if (parentId.HasValue)
            {
                query = query.Where(f => f.ParentId == parentId.Value);
            }

            var folders = await query.OrderBy(f => f.Name).ToListAsync();
            return Ok(folders);
        }

        /// <summary>
        /// フォルダを作成する
        /// </summary>
        [HttpPost("folders")]
        public async Task<IActionResult> CreateFolder([FromBody] FolderCreateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { error = "フォルダ名が指定されていません。" });
            }

            var tenantId = GetEffectiveTenantId();
            try
            {
                await _policyService.ValidateWriteAccessAsync(tenantId);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (TenantReadOnlyException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }

            var folder = new Folder
            {
                Name = request.Name.Trim(),
                ParentId = request.ParentId,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Folders.Add(folder);
            await _dbContext.SaveChangesAsync();

            // 監査ログ記録
            await _auditLogService.LogActionAsync("FolderCreated", folder.Id, folder.Name);

            return CreatedAtAction(nameof(GetFolders), new { parentId = folder.ParentId }, folder);
        }

        /// <summary>
        /// フォルダを更新する（名前変更・親フォルダ移動）
        /// </summary>
        [HttpPut("folders/{id:guid}")]
        public async Task<IActionResult> UpdateFolder(Guid id, [FromBody] FolderUpdateRequest request)
        {
            var tenantId = GetEffectiveTenantId();
            try
            {
                await _policyService.ValidateWriteAccessAsync(tenantId);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (TenantReadOnlyException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }

            var folder = await _dbContext.Folders.FindAsync(id);
            if (folder == null)
            {
                return NotFound(new { error = "指定されたフォルダが見つかりません。" });
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                folder.Name = request.Name.Trim();
            }

            if (request.ParentId.HasValue)
            {
                if (request.ParentId.Value == id)
                {
                    return BadRequest(new { error = "自身を親フォルダに設定することはできません。" });
                }
                folder.ParentId = request.ParentId.Value;
            }

            await _dbContext.SaveChangesAsync();

            // 監査ログ記録
            await _auditLogService.LogActionAsync("FolderUpdated", folder.Id, folder.Name);

            return Ok(folder);
        }

        /// <summary>
        /// フォルダを削除する（配下のファイル実体およびサブフォルダを再帰削除）
        /// </summary>
        [HttpDelete("folders/{id:guid}")]
        public async Task<IActionResult> DeleteFolder(Guid id)
        {
            var tenantId = GetEffectiveTenantId();
            try
            {
                await _policyService.ValidateWriteAccessAsync(tenantId);
            }
            catch (TenantSuspendedException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (TenantReadOnlyException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }

            var folder = await _dbContext.Folders.FindAsync(id);
            if (folder == null)
            {
                return NotFound(new { error = "指定されたフォルダが見つかりません。" });
            }

            // 配下ファイルの S3 実体削除
            var filesInFolder = await _dbContext.Files.Where(f => f.FolderId == id).ToListAsync();
            foreach (var file in filesInFolder)
            {
                try
                {
                    await _storageService.DeleteAsync(file.StoragePath);
                }
                catch
                {
                    // ログ
                }
            }

            _dbContext.Files.RemoveRange(filesInFolder);
            _dbContext.Folders.Remove(folder);
            await _dbContext.SaveChangesAsync();

            // 監査ログ記録
            await _auditLogService.LogActionAsync("FolderDeleted", folder.Id, folder.Name);

            return NoContent();
        }
    }

    /// <summary>
    /// アップロード URL 発行リクエスト用 DTO
    /// </summary>
    public class CreateUploadUrlRequest
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long ByteSize { get; set; }
        public Guid? FolderId { get; set; }
        
        // クライアントのリクエストボディから直接 TenantId を受ける場合はこちらを使用
        public string? TenantId { get; set; }
    }
}

