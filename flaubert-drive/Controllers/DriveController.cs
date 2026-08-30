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

        public DriveController(
            DriveDbContext db,
            IStorageService storage,
            ITenantPolicyService policy,
            IAuditLogService audit,
            ITenantProvider tenant,
            IVirtualFileSystemService vfs)
        {
            _db = db;
            _storage = storage;
            _policy = policy;
            _audit = audit;
            _tenant = tenant;
            _vfs = vfs;
        }

        private string TenantId(string? fallback = null)
        {
            var tenantId = _tenant.GetTenantId();

            if (!string.IsNullOrWhiteSpace(tenantId) &&
                tenantId != "default")
            {
                return tenantId;
            }

            var claimTenantId =
                User.FindFirst("tenant_id")?.Value ??
                User.FindFirst("TenantId")?.Value;

            if (!string.IsNullOrWhiteSpace(claimTenantId) &&
                claimTenantId != "default")
            {
                return claimTenantId;
            }

            return fallback ?? "default";
        }

        // ============================================================
        // Files / Objects
        // ============================================================

        /// <summary>
        /// ファイル一覧を取得する。
        ///
        /// エンドポイント:
        /// GET /api/drive/object
        /// </summary>
        [HttpGet("object")]
        public async Task<IActionResult> GetFiles(
            [FromQuery] Guid? folderId = null,
            [FromQuery] bool rootOnly = false)
        {
            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateReadAccessAsync(tenantId);

            var files =
                await _vfs.GetFilesAsync(
                    tenantId,
                    folderId,
                    rootOnly);

            return Ok(files);
        }

        /// <summary>
        /// S3へのアップロード用Presigned URLを発行する。
        ///
        /// エンドポイント:
        /// POST /api/drive/object
        ///
        /// DB:
        /// Files.FolderId -> 仮想フォルダ
        /// Files.StorageKey -> S3物理キー
        ///
        /// S3の物理キーにはフォルダパスを含めない。
        /// </summary>
        [HttpPost("object")]
        public async Task<IActionResult> GetUploadUrl(
            [FromBody] CreateUploadUrlRequest request)
        {
            if (request == null)
            {
                return BadRequest(new
                {
                    error = "リクエストが指定されていません。"
                });
            }

            if (string.IsNullOrWhiteSpace(request.FileName))
            {
                return BadRequest(new
                {
                    error = "ファイル名が指定されていません。"
                });
            }

            if (request.ByteSize < 0)
            {
                return BadRequest(new
                {
                    error = "ファイルサイズが不正です。"
                });
            }

            var tenantId = TenantId(request.TenantId);

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            // テナントの書き込み権限・容量制限を確認
            await _policy.ValidateWriteAccessAsync(
                tenantId,
                request.ByteSize);

            // FolderIdが指定されている場合、
            // 同じテナントに属するフォルダか確認
            if (request.FolderId.HasValue)
            {
                var folderExists =
                    await _vfs.FolderExistsAsync(
                        tenantId,
                        request.FolderId.Value);

                if (!folderExists)
                {
                    return BadRequest(new
                    {
                        error = "指定されたフォルダが存在しません。"
                    });
                }
            }

            var contentType =
                string.IsNullOrWhiteSpace(request.ContentType)
                    ? "application/octet-stream"
                    : request.ContentType.Trim();

            // S3物理キーを生成。
            // 仮想フォルダパスはここでは使用しない。
            var (uploadUrl, key) =
                _storage.GeneratePresignedUploadUrl(
                    tenantId,
                    contentType);

            // DB上のファイルメタデータを作成。
            //
            // FolderId:
            //     仮想フォルダとの関連
            //
            // StorageKey:
            //     S3上の物理オブジェクト
            var file = new FileMetadata
            {
                TenantId = tenantId,
                FileName = request.FileName.Trim(),
                ContentType = contentType,
                ByteSize = request.ByteSize,
                StorageKey = key,
                FolderId = request.FolderId
            };

            _db.Files.Add(file);

            await _db.SaveChangesAsync();

            await _audit.LogActionAsync(
                "UploadUrlRequested",
                file.Id,
                file.FileName,
                file.ByteSize);

            // DB上のFolderId / ParentIdから仮想パスを構築
            var virtualPath =
                await _vfs.BuildVirtualPathAsync(
                    tenantId,
                    file);

            // objectIdをトップレベルにも返す。
            //
            // これによりPowerShell等のクライアントは
            // response.objectId でIDを取得できる。
            return Ok(new
            {
                objectId = file.Id,
                uploadUrl,
                file,
                virtualPath
            });
        }

        /// <summary>
        /// ファイルのダウンロード用Presigned URLを発行する。
        ///
        /// エンドポイント:
        /// GET /api/drive/object/{id}
        /// </summary>
        [HttpGet("object/{id:guid}")]
        public async Task<IActionResult> GetDownloadUrl(Guid id)
        {
            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateReadAccessAsync(tenantId);

            // TenantId + FileIdで取得するため、
            // 他テナントのファイルにはアクセスできない。
            var file =
                await _vfs.GetFileAsync(
                    tenantId,
                    id);

            if (file == null)
            {
                return NotFound(new
                {
                    error = "指定されたファイルが見つかりません。"
                });
            }

            var url =
                _storage.GeneratePresignedDownloadUrl(
                    file.StorageKey);

            await _audit.LogActionAsync(
                "DownloadUrlRequested",
                file.Id,
                file.FileName,
                file.ByteSize);

            var virtualPath =
                await _vfs.BuildVirtualPathAsync(
                    tenantId,
                    file);

            return Ok(new
            {
                objectId = file.Id,
                downloadUrl = url,
                fileName = file.FileName,
                virtualPath
            });
        }

        /// <summary>
        /// ファイルを削除する。
        ///
        /// エンドポイント:
        /// DELETE /api/drive/object/{id}
        ///
        /// DBだけでなくS3の実体も削除する。
        /// </summary>
        [HttpDelete("object/{id:guid}")]
        public async Task<IActionResult> DeleteFile(Guid id)
        {
            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateWriteAccessAsync(
                tenantId);

            var file =
                await _vfs.GetFileAsync(
                    tenantId,
                    id);

            if (file == null)
            {
                return NotFound(new
                {
                    error = "指定されたファイルが見つかりません。"
                });
            }

            // S3物理オブジェクト削除
            await _storage.DeleteAsync(
                file.StorageKey);

            // DBメタデータ削除
            _db.Files.Remove(file);

            await _db.SaveChangesAsync();

            await _audit.LogActionAsync(
                "FileDeleted",
                file.Id,
                file.FileName,
                file.ByteSize);

            return NoContent();
        }

        // ============================================================
        // Folders
        // ============================================================

        /// <summary>
        /// フォルダ一覧を取得する。
        ///
        /// parentId=null:
        ///     ルートフォルダ
        ///
        /// parentId指定:
        ///     指定フォルダ直下
        ///
        /// エンドポイント:
        /// GET /api/drive/folders
        /// </summary>
        [HttpGet("folders")]
        public async Task<IActionResult> GetFolders(
            [FromQuery] Guid? parentId = null)
        {
            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateReadAccessAsync(
                tenantId);

            if (parentId.HasValue)
            {
                var exists =
                    await _vfs.FolderExistsAsync(
                        tenantId,
                        parentId.Value);

                if (!exists)
                {
                    return NotFound(new
                    {
                        error = "指定された親フォルダが存在しません。"
                    });
                }
            }

            return Ok(
                await _vfs.GetFoldersAsync(
                    tenantId,
                    parentId));
        }

        /// <summary>
        /// 仮想フォルダを作成する。
        ///
        /// エンドポイント:
        /// POST /api/drive/folders
        /// </summary>
        [HttpPost("folders")]
        public async Task<IActionResult> CreateFolder(
            [FromBody] FolderCreateRequest request)
        {
            if (request == null)
            {
                return BadRequest(new
                {
                    error = "リクエストが指定されていません。"
                });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new
                {
                    error = "フォルダ名が指定されていません。"
                });
            }

            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateWriteAccessAsync(
                tenantId);

            if (request.ParentId.HasValue)
            {
                var parentExists =
                    await _vfs.FolderExistsAsync(
                        tenantId,
                        request.ParentId.Value);

                if (!parentExists)
                {
                    return BadRequest(new
                    {
                        error = "親フォルダが存在しません。"
                    });
                }
            }

            var folder = new Folder
            {
                TenantId = tenantId,
                Name = request.Name.Trim(),
                ParentId = request.ParentId
            };

            _db.Folders.Add(folder);

            await _db.SaveChangesAsync();

            await _audit.LogActionAsync(
                "FolderCreated",
                folder.Id,
                folder.Name);

            return CreatedAtAction(
                nameof(GetFolders),
                new
                {
                    parentId = folder.ParentId
                },
                folder);
        }

        /// <summary>
        /// 仮想フォルダを変更する。
        ///
        /// フォルダ名やParentIdを変更しても、
        /// S3オブジェクトのStorageKeyは変更しない。
        /// </summary>
        [HttpPut("folders/{id:guid}")]
        public async Task<IActionResult> UpdateFolder(
            Guid id,
            [FromBody] FolderUpdateRequest request)
        {
            if (request == null)
            {
                return BadRequest(new
                {
                    error = "リクエストが指定されていません。"
                });
            }

            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateWriteAccessAsync(
                tenantId);

            var folder =
                await _vfs.GetFolderAsync(
                    tenantId,
                    id);

            if (folder == null)
            {
                return NotFound(new
                {
                    error = "指定されたフォルダが見つかりません。"
                });
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                folder.Name = request.Name.Trim();
            }

            if (request.ParentId.HasValue)
            {
                var newParentId =
                    request.ParentId.Value;

                // 自分自身を親にはできない
                if (newParentId == id)
                {
                    return BadRequest(new
                    {
                        error = "自分自身を親フォルダに設定できません。"
                    });
                }

                // 親フォルダが存在するか確認
                var parentExists =
                    await _vfs.FolderExistsAsync(
                        tenantId,
                        newParentId);

                if (!parentExists)
                {
                    return BadRequest(new
                    {
                        error = "指定された親フォルダが存在しません。"
                    });
                }

                // 子孫を親にすると循環するため禁止
                var isDescendant =
                    await _vfs.IsDescendantAsync(
                        tenantId,
                        newParentId,
                        id);

                if (isDescendant)
                {
                    return BadRequest(new
                    {
                        error = "子孫フォルダを親に設定できません。"
                    });
                }

                folder.ParentId = newParentId;
            }

            await _db.SaveChangesAsync();

            await _audit.LogActionAsync(
                "FolderUpdated",
                folder.Id,
                folder.Name);

            return Ok(folder);
        }

        /// <summary>
        /// 仮想フォルダを削除する。
        ///
        /// 配下の子フォルダとファイルを再帰的に削除する。
        /// S3上のファイル実体も削除する。
        /// </summary>
        [HttpDelete("folders/{id:guid}")]
        public async Task<IActionResult> DeleteFolder(
            Guid id)
        {
            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateWriteAccessAsync(
                tenantId);

            var folder =
                await _vfs.GetFolderAsync(
                    tenantId,
                    id);

            if (folder == null)
            {
                return NotFound(new
                {
                    error = "指定されたフォルダが見つかりません。"
                });
            }

            // 同一テナントのフォルダだけ取得
            var folders =
                await _db.Folders
                    .Where(x => x.TenantId == tenantId)
                    .ToListAsync();

            // 対象フォルダ + すべての子孫フォルダ
            var folderIds =
                folders
                    .Where(x =>
                        x.Id == id ||
                        IsChild(
                            folders,
                            x.Id,
                            id))
                    .Select(x => x.Id)
                    .ToHashSet();

            // 配下ファイル取得
            var files =
                await _db.Files
                    .Where(x =>
                        x.TenantId == tenantId &&
                        x.FolderId.HasValue &&
                        folderIds.Contains(
                            x.FolderId.Value))
                    .ToListAsync();

            // S3実体削除
            foreach (var file in files)
            {
                await _storage.DeleteAsync(
                    file.StorageKey);
            }

            // DBファイル削除
            _db.Files.RemoveRange(files);

            // DBフォルダ削除
            _db.Folders.RemoveRange(
                folders.Where(x =>
                    folderIds.Contains(x.Id)));

            await _db.SaveChangesAsync();

            await _audit.LogActionAsync(
                "FolderDeleted",
                folder.Id,
                folder.Name);

            return NoContent();
        }

        /// <summary>
        /// folderIdがancestorの配下に存在するか判定する。
        /// </summary>
        private static bool IsChild(
            System.Collections.Generic.List<Folder> all,
            Guid id,
            Guid ancestor)
        {
            var visited =
                new System.Collections.Generic.HashSet<Guid>();

            var current =
                all.FirstOrDefault(
                    x => x.Id == id);

            while (current?.ParentId is Guid parentId)
            {
                // 循環参照対策
                if (!visited.Add(current.Id))
                {
                    return false;
                }

                if (parentId == ancestor)
                {
                    return true;
                }

                current =
                    all.FirstOrDefault(
                        x => x.Id == parentId);
            }

            return false;
        }
    }

    /// <summary>
    /// S3アップロードURL発行リクエスト。
    /// </summary>
    public class CreateUploadUrlRequest
    {
        public string FileName { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public long ByteSize { get; set; }

        /// <summary>
        /// 仮想フォルダID。
        /// nullの場合はルート。
        /// </summary>
        public Guid? FolderId { get; set; }

        /// <summary>
        /// 互換用。
        ///
        /// 基本的にはJWT / ITenantProviderから取得する。
        /// </summary>
        public string? TenantId { get; set; }
    }
}