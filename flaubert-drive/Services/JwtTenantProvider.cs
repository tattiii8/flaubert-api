using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Flaubert.Drive.Services
{
    public class JwtTenantProvider : ITenantProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public JwtTenantProvider(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public string GetTenantId()
        {
            var user = _httpContextAccessor.HttpContext?.User;

            if (user?.Identity?.IsAuthenticated == true)
            {
                // flaubert-auth が付与するクレーム名（tenant_id または TenantId）を取得
                var tenantClaim = user.FindFirst("tenant_id") 
                               ?? user.FindFirst("TenantId") 
                               ?? user.FindFirst(ClaimTypes.GroupSid);

                if (tenantClaim != null && !string.IsNullOrWhiteSpace(tenantClaim.Value))
                {
                    return tenantClaim.Value;
                }
            }

            return "default";
        }
    }
}
