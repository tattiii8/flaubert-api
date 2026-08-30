using System;
using System.Collections.Generic;

namespace Flaubert.Drive.Models
{
    public class FileMetadata
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string TenantId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long ByteSize { get; set; }
        public string StorageKey { get; set; } = string.Empty;
        public Guid? FolderId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Folder
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string TenantId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Guid? ParentId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TenantSetting
    {
        public string TenantId { get; set; } = string.Empty;
        public long MaxStorageBytes { get; set; } = 5368709120;
        public long MaxFileSizeBytes { get; set; } = 524288000;
        public string Status { get; set; } = "Active";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class AuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string? UserId { get; set; }
        public string Action { get; set; } = string.Empty;
        public Guid? TargetId { get; set; }
        public string? TargetName { get; set; }
        public long? ByteSize { get; set; }
        public string? IpAddress { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TenantStatsDto
    {
        public string TenantId { get; set; } = string.Empty;
        public int TotalFiles { get; set; }
        public int TotalFolders { get; set; }
        public long TotalBytes { get; set; }
        public double TotalMegaBytes => Math.Round((double)TotalBytes / (1024 * 1024), 2);
        public double TotalGigaBytes => Math.Round((double)TotalBytes / (1024 * 1024 * 1024), 2);
        public long QuotaBytes { get; set; }
        public double UsedPercentage => QuotaBytes > 0 ? Math.Round((double)TotalBytes / QuotaBytes * 100, 2) : 0;
        public string Status { get; set; } = "Active";
        public Dictionary<string, long> ContentTypeBreakdown { get; set; } = new();
    }

    public class UpdateTenantSettingRequest
    {
        public long? MaxStorageBytes { get; set; }
        public long? MaxFileSizeBytes { get; set; }
        public string? Status { get; set; }
    }

    public class OrphanDetectionResult
    {
        public string TenantId { get; set; } = string.Empty;
        public List<string> DanglingS3Objects { get; set; } = new();
        public List<FileMetadata> MissingS3Objects { get; set; } = new();
        public int DanglingS3Count => DanglingS3Objects.Count;
        public int MissingS3Count => MissingS3Objects.Count;
    }

    public class CleanupOrphansRequest
    {
        public bool PurgeDanglingS3Objects { get; set; } = true;
        public bool RemoveMissingDbRecords { get; set; } = false;
    }

    public class FolderCreateRequest
    {
        public string Name { get; set; } = string.Empty;
        public Guid? ParentId { get; set; }
    }

    public class FolderUpdateRequest
    {
        public string? Name { get; set; }
        public Guid? ParentId { get; set; }
    }
}
