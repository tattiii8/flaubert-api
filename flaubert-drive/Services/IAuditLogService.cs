using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flaubert.Drive.Models;

namespace Flaubert.Drive.Services
{
    public interface IAuditLogService
    {
        Task LogActionAsync(string action, Guid? targetId = null, string? targetName = null, long? byteSize = null);
        Task<List<AuditLog>> GetAuditLogsAsync(int page = 1, int pageSize = 50, string? actionFilter = null);
    }
}
