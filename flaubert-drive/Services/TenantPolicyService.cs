using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Data;
using Flaubert.Drive.Models;

namespace Flaubert.Drive.Services
{
    public class TenantPolicyService : ITenantPolicyService
    {
        private readonly DriveDbContext _dbContext;

        public TenantPolicyService(DriveDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<TenantSetting> GetOrCreateTenantSettingAsync(string tenantId)
        {
            var setting = await _dbContext.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
            if (setting == null)
            {
                setting = new TenantSetting
                {
                    TenantId = tenantId,
                    MaxStorageBytes = 5368709120, // 5 GB
                    MaxFileSizeBytes = 524288000, // 500 MB
                    Status = "Active",
                    UpdatedAt = DateTime.UtcNow
                };

                try
                {
                    _dbContext.TenantSettings.Add(setting);
                    await _dbContext.SaveChangesAsync();
                }
                catch
                {
                    // 競合等が発生した場合はリトライして取得
                    setting = await _dbContext.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId) 
                              ?? setting;
                }
            }

            return setting;
        }

        public async Task ValidateReadAccessAsync(string tenantId)
        {
            var setting = await GetOrCreateTenantSettingAsync(tenantId);
            if (string.Equals(setting.Status, "Suspended", StringComparison.OrdinalIgnoreCase))
            {
                throw new TenantSuspendedException(tenantId);
            }
        }

        public async Task ValidateWriteAccessAsync(string tenantId, long incomingByteSize = 0)
        {
            var setting = await GetOrCreateTenantSettingAsync(tenantId);

            if (string.Equals(setting.Status, "Suspended", StringComparison.OrdinalIgnoreCase))
            {
                throw new TenantSuspendedException(tenantId);
            }

            if (string.Equals(setting.Status, "ReadOnly", StringComparison.OrdinalIgnoreCase))
            {
                throw new TenantReadOnlyException(tenantId);
            }

            if (incomingByteSize > 0)
            {
                if (incomingByteSize > setting.MaxFileSizeBytes)
                {
                    throw new FileSizeExceededException(incomingByteSize, setting.MaxFileSizeBytes);
                }

                var currentBytes = await _dbContext.Files.SumAsync(f => (long?)f.ByteSize) ?? 0;
                if (currentBytes + incomingByteSize > setting.MaxStorageBytes)
                {
                    throw new QuotaExceededException(currentBytes, incomingByteSize, setting.MaxStorageBytes);
                }
            }
        }
    }
}
