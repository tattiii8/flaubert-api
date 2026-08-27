using Microsoft.Extensions.Diagnostics.HealthChecks;
using Flaubert.Drive.Data;

namespace Flaubert.Drive.Services
{
    public class DatabaseHealthCheck : IHealthCheck
    {
        private readonly DriveDbContext _dbContext;

        public DatabaseHealthCheck(DriveDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                if (await _dbContext.Database.CanConnectAsync(cancellationToken))
                {
                    return HealthCheckResult.Healthy("Database connection is OK.");
                }
                return HealthCheckResult.Unhealthy("Cannot connect to database.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy(exception: ex);
            }
        }
    }
}
