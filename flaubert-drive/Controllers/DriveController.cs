
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
        /// ファイル一覧を取得
        ///
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
        /// S3アップロード用Presigned URLを発行
        ///
        /// POST /api/drive/object
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

            await _policy.ValidateWriteAccessAsync(
                tenantId,
                request.ByteSize);

            // 指定された仮想フォルダが同一テナントに存在することを確認
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
            //
            // 仮想フォルダ階層はDBで管理するため、
            // S3キーにはフォルダパスを含めない。
            var (uploadUrl, key) =
                _storage.GeneratePresignedUploadUrl(
                    tenantId,
                    contentType);

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

            var virtualPath =
                await _vfs.BuildVirtualPathAsync(
                    tenantId,
                    file);

            // objectIdを明示的に返却。
            //
            // テストクライアント等から
            // GET /api/drive/object/{objectId}
            // を呼び出せるようにする。
            return Ok(new
            {
                objectId = file.Id,
                uploadUrl,
                file,
                virtualPath
            });
        }

        /// <summary>
        /// ファイルダウンロード用Presigned URLを発行
        ///
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
        /// ファイル削除
        ///
        /// DELETE /api/drive/object/{id}
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

            await _policy.ValidateWriteAccessAsync(tenantId);

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

            if (!string.IsNullOrWhiteSpace(file.StorageKey))
            {
                await _storage.DeleteAsync(
                    file.StorageKey);
            }

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
        /// フォルダ一覧取得
        ///
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

            await _policy.ValidateReadAccessAsync(tenantId);

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
        /// 仮想フォルダ作成
        ///
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

            await _policy.ValidateWriteAccessAsync(tenantId);

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
        /// 仮想フォルダ更新
        ///
        /// PUT /api/drive/folders/{id}
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

            await _policy.ValidateWriteAccessAsync(tenantId);

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

                if (newParentId == id)
                {
                    return BadRequest(new
                    {
                        error = "自分自身を親フォルダに設定できません。"
                    });
                }

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
        /// 仮想フォルダ削除
        ///
        /// DELETE /api/drive/folders/{id}
        ///
        /// 対象フォルダ以下の、
        ///   - ファイル
        ///   - 子フォルダ
        /// を削除する。
        ///
        /// S3上の物理オブジェクトも削除する。
        ///
        /// EF CoreのChangeTrackerによる
        /// Folderインスタンス競合を避けるため、
        /// 削除処理ではExecuteDeleteAsync / ExecuteUpdateAsync
        /// を使用する。
        /// </summary>
        [HttpDelete("folders/{id:guid}")]
        public async Task<IActionResult> DeleteFolder(Guid id)
        {
            var tenantId = TenantId();

            if (tenantId == "default")
            {
                return BadRequest(new
                {
                    error = "有効なテナントIDが取得できませんでした。"
                });
            }

            await _policy.ValidateWriteAccessAsync(tenantId);

            // --------------------------------------------------------
            // 対象テナントのフォルダをChangeTrackerに登録せず取得
            // --------------------------------------------------------

            var allFolders =
                await _db.Folders
                    .AsNoTracking()
                    .Where(x => x.TenantId == tenantId)
                    .ToListAsync();

            var targetFolder =
                allFolders.FirstOrDefault(
                    x => x.Id == id);

            if (targetFolder == null)
            {
                return NotFound(new
                {
                    error = "指定されたフォルダが見つかりません。"
                });
            }

            // --------------------------------------------------------
            // 対象フォルダ + すべての子孫フォルダ
            // --------------------------------------------------------

            var folderIds =
                allFolders
                    .Where(x =>
                        x.Id == id ||
                        IsChild(
                            allFolders,
                            x.Id,
                            id))
                    .Select(x => x.Id)
                    .ToHashSet();

            // --------------------------------------------------------
            // 配下ファイル取得
            //
            // S3削除にStorageKeyが必要なので、
            // 削除前に読み込んでおく。
            // --------------------------------------------------------

            var files =
                await _db.Files
                    .AsNoTracking()
                    .Where(x =>
                        x.TenantId == tenantId &&
                        x.FolderId.HasValue &&
                        folderIds.Contains(
                            x.FolderId.Value))
                    .ToListAsync();

            // --------------------------------------------------------
            // S3物理オブジェクト削除
            // --------------------------------------------------------

            foreach (var file in files)
            {
                if (!string.IsNullOrWhiteSpace(file.StorageKey))
                {
                    await _storage.DeleteAsync(
                        file.StorageKey);
                }
            }

            // --------------------------------------------------------
            // Files削除
            //
            // ExecuteDeleteAsyncなのでChangeTrackerを使用しない。
            // --------------------------------------------------------

            if (folderIds.Count > 0)
            {
                await _db.Files
                    .Where(x =>
                        x.TenantId == tenantId &&
                        x.FolderId.HasValue &&
                        folderIds.Contains(
                            x.FolderId.Value))
                    .ExecuteDeleteAsync();
            }

            // --------------------------------------------------------
            // Folder自己参照FK解除
            //
            // 削除対象フォルダのParentIdをNULLにする。
            //
            // これにより、
            //
            // root
            // └── child
            //
            // のような自己参照FKが存在していても、
            // 親削除時のFK制約を回避できる。
            // --------------------------------------------------------

            if (folderIds.Count > 0)
            {
                await _db.Folders
                    .Where(x =>
                        x.TenantId == tenantId &&
                        folderIds.Contains(x.Id))
                    .ExecuteUpdateAsync(setters =>
                        setters.SetProperty(
                            x => x.ParentId,
                            (Guid?)null));
            }

            // --------------------------------------------------------
            // 深いフォルダから順番に削除
            //
            // ChangeTrackerを一切使用しない。
            // --------------------------------------------------------

            var foldersToDelete =
                allFolders
                    .Where(x =>
                        folderIds.Contains(x.Id))
                    .OrderByDescending(
                        x => GetDepth(
                            allFolders,
                            x.Id))
                    .ToList();

            foreach (var folderToDelete in foldersToDelete)
            {
                await _db.Folders
                    .Where(x =>
                        x.TenantId == tenantId &&
                        x.Id == folderToDelete.Id)
                    .ExecuteDeleteAsync();
            }

            // --------------------------------------------------------
            // Audit
            // --------------------------------------------------------

            await _audit.LogActionAsync(
                "FolderDeleted",
                targetFolder.Id,
                targetFolder.Name);

            return NoContent();
        }

        // ============================================================
        // Helper methods
        // ============================================================

        /// <summary>
        /// idがancestorの子孫フォルダか判定する。
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
                if (!visited.Add(current.Id))
                {
                    // 循環参照がある場合は安全側に倒す
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

        /// <summary>
        /// フォルダの階層深度を取得する。
        /// 深いフォルダから削除するために使用する。
        /// </summary>
        private static int GetDepth(
            System.Collections.Generic.List<Folder> all,
            Guid id)
        {
            var depth = 0;

            var visited =
                new System.Collections.Generic.HashSet<Guid>();

            var current =
                all.FirstOrDefault(
                    x => x.Id == id);

            while (current?.ParentId is Guid parentId)
            {
                if (!visited.Add(current.Id))
                {
                    break;
                }

                depth++;

                current =
                    all.FirstOrDefault(
                        x => x.Id == parentId);
            }

            return depth;
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
        /// 基本的にはJWT / ITenantProviderから取得する。
        /// </summary>
        public string? TenantId { get; set; }
    }
}