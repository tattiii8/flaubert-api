using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Flaubert.Drive.Services;

namespace Flaubert.Drive.Data
{
    public class TenantModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            if (context is DriveDbContext driveContext)
            {
                var tenantProvider = driveContext.GetService<ITenantProvider>();
                return (context.GetType(), tenantProvider.GetTenantId(), designTime);
            }

            return context.GetType();
        }
    }
}
