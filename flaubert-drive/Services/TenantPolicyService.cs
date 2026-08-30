using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Flaubert.Drive.Data;
using Flaubert.Drive.Models;

namespace Flaubert.Drive.Services
{
    public class TenantPolicyService : ITenantPolicyService
    {
        private readonly DriveDbContext _db;
        public TenantPolicyService(DriveDbContext db) => _db = db;
        public async Task<TenantSetting> GetOrCreateTenantSettingAsync(string tenantId)
        {
            var s = await _db.TenantSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId);
            if (s != null) return s;
            s = new TenantSetting { TenantId = tenantId };
            try { _db.TenantSettings.Add(s); await _db.SaveChangesAsync(); }
            catch { _db.Entry(s).State = EntityState.Detached; s = await _db.TenantSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId) ?? s; }
            return s;
        }
        public async Task ValidateReadAccessAsync(string tenantId)
        {
            var s = await GetOrCreateTenantSettingAsync(tenantId);
            if (s.Status.Equals("Suspended", StringComparison.OrdinalIgnoreCase)) throw new TenantSuspendedException(tenantId);
        }
        public async Task ValidateWriteAccessAsync(string tenantId, long incomingByteSize = 0)
        {
            var s = await GetOrCreateTenantSettingAsync(tenantId);
            if (s.Status.Equals("Suspended", StringComparison.OrdinalIgnoreCase)) throw new TenantSuspendedException(tenantId);
            if (s.Status.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)) throw new TenantReadOnlyException(tenantId);
            if (incomingByteSize <= 0) return;
            if (incomingByteSize > s.MaxFileSizeBytes) throw new FileSizeExceededException(incomingByteSize, s.MaxFileSizeBytes);
            var current = await _db.Files.Where(x => x.TenantId == tenantId).SumAsync(x => (long?)x.ByteSize) ?? 0;
            if (current + incomingByteSize > s.MaxStorageBytes) throw new QuotaExceededException(current, incomingByteSize, s.MaxStorageBytes);
        }
    }
}
