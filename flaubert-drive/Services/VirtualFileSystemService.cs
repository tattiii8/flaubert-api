using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Data;
using Flaubert.Drive.Models;

namespace Flaubert.Drive.Services
{
    public class VirtualFileSystemService : IVirtualFileSystemService
    {
        private readonly DriveDbContext _db;
        public VirtualFileSystemService(DriveDbContext db) => _db = db;

        public Task<FileMetadata?> GetFileAsync(string tenantId, Guid id) =>
            _db.Files.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        public Task<Folder?> GetFolderAsync(string tenantId, Guid id) =>
            _db.Folders.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        public Task<bool> FolderExistsAsync(string tenantId, Guid id) =>
            _db.Folders.AnyAsync(x => x.Id == id && x.TenantId == tenantId);

        public async Task<bool> IsDescendantAsync(string tenantId, Guid folderId, Guid possibleAncestorId)
        {
            var current = await GetFolderAsync(tenantId, folderId);
            var visited = new HashSet<Guid>();
            while (current?.ParentId is Guid parent)
            {
                if (!visited.Add(current.Id)) return true;
                if (parent == possibleAncestorId) return true;
                current = await GetFolderAsync(tenantId, parent);
            }
            return false;
        }

        public async Task<string> BuildVirtualPathAsync(string tenantId, FileMetadata file)
        {
            var parts = new List<string> { file.FileName };
            var folderId = file.FolderId;
            var visited = new HashSet<Guid>();
            while (folderId is Guid id)
            {
                if (!visited.Add(id)) throw new InvalidOperationException("Folder cycle detected.");
                var folder = await GetFolderAsync(tenantId, id) ?? throw new InvalidOperationException("Folder not found.");
                parts.Add(folder.Name);
                folderId = folder.ParentId;
            }
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        public async Task<string> BuildFolderPathAsync(string tenantId, Folder folder)
        {
            var parts = new List<string> { folder.Name };
            var parentId = folder.ParentId;
            var visited = new HashSet<Guid> { folder.Id };
            while (parentId is Guid id)
            {
                if (!visited.Add(id)) throw new InvalidOperationException("Folder cycle detected.");
                var parent = await GetFolderAsync(tenantId, id) ?? throw new InvalidOperationException("Parent folder not found.");
                parts.Add(parent.Name);
                parentId = parent.ParentId;
            }
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        public Task<List<FileMetadata>> GetFilesAsync(string tenantId, Guid? folderId, bool rootOnly)
        {
            var query = _db.Files.Where(x => x.TenantId == tenantId);
            if (folderId.HasValue) query = query.Where(x => x.FolderId == folderId.Value);
            else if (rootOnly) query = query.Where(x => x.FolderId == null);
            return query.OrderByDescending(x => x.CreatedAt).ToListAsync();
        }

        public Task<List<Folder>> GetFoldersAsync(string tenantId, Guid? parentId) =>
            _db.Folders.Where(x => x.TenantId == tenantId && x.ParentId == parentId).OrderBy(x => x.Name).ToListAsync();
    }
}
