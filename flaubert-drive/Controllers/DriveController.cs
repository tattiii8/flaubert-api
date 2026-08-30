using System;
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
        private readonly DriveDbContext _db;
        private readonly IStorageService _storage;
        private readonly ITenantPolicyService _policy;
        private readonly IAuditLogService _audit;
        private readonly ITenantProvider _tenant;
        private readonly IVirtualFileSystemService _vfs;

        public DriveController(DriveDbContext db, IStorageService storage, ITenantPolicyService policy,
            IAuditLogService audit, ITenantProvider tenant, IVirtualFileSystemService vfs)
        { _db = db; _storage = storage; _policy = policy; _audit = audit; _tenant = tenant; _vfs = vfs; }

        private string TenantId(string? fallback = null) =>
            (_tenant.GetTenantId() is { } t && t != "default" && !string.IsNullOrWhiteSpace(t)) ? t :
            User.FindFirst("tenant_id")?.Value ?? User.FindFirst("TenantId")?.Value ?? fallback ?? "default";

        [HttpGet("object")]
        public async Task<IActionResult> GetFiles([FromQuery] Guid? folderId = null, [FromQuery] bool rootOnly = false)
        {
            var tenantId = TenantId(); await _policy.ValidateReadAccessAsync(tenantId);
            return Ok(await _vfs.GetFilesAsync(tenantId, folderId, rootOnly));
        }

        [HttpPost("object")]
        public async Task<IActionResult> GetUploadUrl([FromBody] CreateUploadUrlRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.FileName)) return BadRequest(new { error = "ファイル名が指定されていません。" });
            var tenantId = TenantId(request.TenantId);
            if (tenantId == "default") return BadRequest(new { error = "有効なテナントIDが取得できませんでした。" });
            await _policy.ValidateWriteAccessAsync(tenantId, request.ByteSize);

            if (request.FolderId.HasValue && !await _vfs.FolderExistsAsync(tenantId, request.FolderId.Value))
                return BadRequest(new { error = "指定されたフォルダが存在しません。" });

            var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType;
            var (uploadUrl, key) = _storage.GeneratePresignedUploadUrl(tenantId, contentType);
            var file = new FileMetadata { TenantId = tenantId, FileName = request.FileName.Trim(), ContentType = contentType,
                ByteSize = request.ByteSize, StorageKey = key, FolderId = request.FolderId };
            _db.Files.Add(file); await _db.SaveChangesAsync();
            await _audit.LogActionAsync("UploadUrlRequested", file.Id, file.FileName, file.ByteSize);
            return Ok(new { uploadUrl, file, virtualPath = await _vfs.BuildVirtualPathAsync(tenantId, file) });
        }

        [HttpGet("object/{id:guid}")]
        public async Task<IActionResult> GetDownloadUrl(Guid id)
        {
            var tenantId = TenantId(); await _policy.ValidateReadAccessAsync(tenantId);
            var file = await _vfs.GetFileAsync(tenantId, id);
            if (file == null) return NotFound(new { error = "指定されたファイルが見つかりません。" });
            var url = _storage.GeneratePresignedDownloadUrl(file.StorageKey);
            await _audit.LogActionAsync("DownloadUrlRequested", file.Id, file.FileName, file.ByteSize);
            return Ok(new { downloadUrl = url, fileName = file.FileName, virtualPath = await _vfs.BuildVirtualPathAsync(tenantId, file) });
        }

        [HttpDelete("object/{id:guid}")]
        public async Task<IActionResult> DeleteFile(Guid id)
        {
            var tenantId = TenantId(); await _policy.ValidateWriteAccessAsync(tenantId);
            var file = await _vfs.GetFileAsync(tenantId, id); if (file == null) return NotFound();
            await _storage.DeleteAsync(file.StorageKey);
            _db.Files.Remove(file); await _db.SaveChangesAsync();
            await _audit.LogActionAsync("FileDeleted", file.Id, file.FileName, file.ByteSize);
            return NoContent();
        }

        [HttpGet("folders")]
        public async Task<IActionResult> GetFolders([FromQuery] Guid? parentId = null)
        {
            var tenantId = TenantId(); await _policy.ValidateReadAccessAsync(tenantId);
            if (parentId.HasValue && !await _vfs.FolderExistsAsync(tenantId, parentId.Value)) return NotFound();
            return Ok(await _vfs.GetFoldersAsync(tenantId, parentId));
        }

        [HttpPost("folders")]
        public async Task<IActionResult> CreateFolder([FromBody] FolderCreateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { error = "フォルダ名が指定されていません。" });
            var tenantId = TenantId(); await _policy.ValidateWriteAccessAsync(tenantId);
            if (request.ParentId.HasValue && !await _vfs.FolderExistsAsync(tenantId, request.ParentId.Value)) return BadRequest(new { error = "親フォルダが存在しません。" });
            var folder = new Folder { TenantId = tenantId, Name = request.Name.Trim(), ParentId = request.ParentId };
            _db.Folders.Add(folder); await _db.SaveChangesAsync();
            await _audit.LogActionAsync("FolderCreated", folder.Id, folder.Name);
            return CreatedAtAction(nameof(GetFolders), new { parentId = folder.ParentId }, folder);
        }

        [HttpPut("folders/{id:guid}")]
        public async Task<IActionResult> UpdateFolder(Guid id, [FromBody] FolderUpdateRequest request)
        {
            var tenantId = TenantId(); await _policy.ValidateWriteAccessAsync(tenantId);
            var folder = await _vfs.GetFolderAsync(tenantId, id); if (folder == null) return NotFound();
            if (!string.IsNullOrWhiteSpace(request.Name)) folder.Name = request.Name.Trim();
            if (request.ParentId.HasValue)
            {
                if (request.ParentId.Value == id || !await _vfs.FolderExistsAsync(tenantId, request.ParentId.Value)) return BadRequest(new { error = "無効な親フォルダです。" });
                if (await _vfs.IsDescendantAsync(tenantId, request.ParentId.Value, id)) return BadRequest(new { error = "子孫フォルダを親に設定できません。" });
                folder.ParentId = request.ParentId;
            }
            folder.CreatedAt = folder.CreatedAt; await _db.SaveChangesAsync();
            await _audit.LogActionAsync("FolderUpdated", folder.Id, folder.Name);
            return Ok(folder);
        }

        [HttpDelete("folders/{id:guid}")]
        public async Task<IActionResult> DeleteFolder(Guid id)
        {
            var tenantId = TenantId(); await _policy.ValidateWriteAccessAsync(tenantId);
            var folder = await _vfs.GetFolderAsync(tenantId, id); if (folder == null) return NotFound();
            var folders = await _db.Folders.Where(x => x.TenantId == tenantId).ToListAsync();
            var ids = folders.Where(x => x.Id == id || IsChild(folders, x.Id, id)).Select(x => x.Id).ToHashSet();
            var files = await _db.Files.Where(x => x.TenantId == tenantId && x.FolderId.HasValue && ids.Contains(x.FolderId.Value)).ToListAsync();
            foreach (var file in files) await _storage.DeleteAsync(file.StorageKey);
            _db.Files.RemoveRange(files); _db.Folders.RemoveRange(folders.Where(x => ids.Contains(x.Id)));
            await _db.SaveChangesAsync(); await _audit.LogActionAsync("FolderDeleted", folder.Id, folder.Name); return NoContent();
        }

        private static bool IsChild(System.Collections.Generic.List<Folder> all, Guid id, Guid ancestor)
        {
            var visited = new System.Collections.Generic.HashSet<Guid>();
            var current = all.FirstOrDefault(x => x.Id == id);
            while (current?.ParentId is Guid p)
            {
                if (!visited.Add(current.Id)) return false;
                if (p == ancestor) return true;
                current = all.FirstOrDefault(x => x.Id == p);
            }
            return false;
        }
    }

    public class CreateUploadUrlRequest
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long ByteSize { get; set; }
        public Guid? FolderId { get; set; }
        public string? TenantId { get; set; }
    }
}
