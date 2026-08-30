using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flaubert.Drive.Models;

namespace Flaubert.Drive.Services
{
    public interface IVirtualFileSystemService
    {
        Task<FileMetadata?> GetFileAsync(string tenantId, Guid id);
        Task<Folder?> GetFolderAsync(string tenantId, Guid id);
        Task<bool> FolderExistsAsync(string tenantId, Guid id);
        Task<bool> IsDescendantAsync(string tenantId, Guid folderId, Guid possibleAncestorId);
        Task<string> BuildVirtualPathAsync(string tenantId, FileMetadata file);
        Task<string> BuildFolderPathAsync(string tenantId, Folder folder);
        Task<List<FileMetadata>> GetFilesAsync(string tenantId, Guid? folderId, bool rootOnly);
        Task<List<Folder>> GetFoldersAsync(string tenantId, Guid? parentId);
    }
}
