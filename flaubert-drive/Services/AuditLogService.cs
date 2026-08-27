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
        private readonly DriveDbContext _dbContext;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AuditLogService(DriveDbContext dbContext, IHttpContextAccessor httpContextAccessor)
        {
            _dbContext = dbContext;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task LogActionAsync(string action, Guid? targetId = null, string? targetName = null, long? byteSize = null)
        {
            try
            {
                var httpContext = _httpContextAccessor.HttpContext;
                var userId = httpContext?.User?.FindFirst("sub")?.Value 
                          ?? httpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                          ?? "system";

                var ipAddress = httpContext?.Connection?.RemoteIpAddress?.ToString() 
                             ?? httpContext?.Request?.Headers["X-Forwarded-For"].FirstOrDefault() 
                             ?? "unknown";

                var auditLog = new AuditLog
                {
                    UserId = userId,
                    Action = action,
                    TargetId = targetId,
                    TargetName = targetName,
                    ByteSize = byteSize,
                    IpAddress = ipAddress,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.AuditLogs.Add(auditLog);
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                // 監査ログ書き込みの失敗がメインのビジネスロジックを阻害しないよう例外をキャッチ
            }
        }

        public async Task<List<AuditLog>> GetAuditLogsAsync(int page = 1, int pageSize = 50, string? actionFilter = null)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 200) pageSize = 50;

            var query = _dbContext.AuditLogs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(actionFilter))
            {
                query = query.Where(a => a.Action.ToLower().Contains(actionFilter.ToLower()));
            }

            return await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }
    }
}
