using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Data;
using Flaubert.Drive.Models;

namespace Flaubert.Drive.Services
{
    public class AuditLogService : IAuditLogService
    {
        private readonly DriveDbContext _db; private readonly IHttpContextAccessor _http;
        public AuditLogService(DriveDbContext db, IHttpContextAccessor http) { _db = db; _http = http; }
        public async Task LogActionAsync(string action, Guid? targetId = null, string? targetName = null, long? byteSize = null)
        {
            try {
                var c = _http.HttpContext; var user = c?.User?.FindFirst("sub")?.Value ?? c?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "system";
                var ip = c?.Connection.RemoteIpAddress?.ToString() ?? c?.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "unknown";
                _db.AuditLogs.Add(new AuditLog { UserId = user, Action = action, TargetId = targetId, TargetName = targetName, ByteSize = byteSize, IpAddress = ip });
                await _db.SaveChangesAsync();
            } catch { }
        }
        public Task<List<AuditLog>> GetAuditLogsAsync(int page = 1, int pageSize = 50, string? actionFilter = null)
        {
            page = Math.Max(1, page); pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;
            var q = _db.AuditLogs.AsQueryable(); if (!string.IsNullOrWhiteSpace(actionFilter)) q = q.Where(x => x.Action.ToLower().Contains(actionFilter.ToLower()));
            return q.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        }
    }
}
