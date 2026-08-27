using System;
using System.Threading.Tasks;
using Flaubert.Drive.Models;

namespace Flaubert.Drive.Services
{
    public interface ITenantPolicyService
    {
        Task<TenantSetting> GetOrCreateTenantSettingAsync(string tenantId);
        Task ValidateReadAccessAsync(string tenantId);
        Task ValidateWriteAccessAsync(string tenantId, long incomingByteSize = 0);
    }

    public class TenantSuspendedException : Exception
    {
        public TenantSuspendedException(string tenantId) 
            : base($"テナント '{tenantId}' は現在凍結（Suspended）されています。操作を実行できません。") { }
    }

    public class TenantReadOnlyException : Exception
    {
        public TenantReadOnlyException(string tenantId) 
            : base($"テナント '{tenantId}' は現在読み取り専用（ReadOnly）モードです。書き込み操作は許可されていません。") { }
    }

    public class QuotaExceededException : Exception
    {
        public QuotaExceededException(long currentBytes, long incomingBytes, long maxBytes) 
            : base($"ストレージ容量の上限を超過します。（現在: {currentBytes / (1024 * 1024)}MB, 追加: {incomingBytes / (1024 * 1024)}MB, 上限: {maxBytes / (1024 * 1024)}MB）") { }
    }

    public class FileSizeExceededException : Exception
    {
        public FileSizeExceededException(long incomingBytes, long maxBytes) 
            : base($"ファイルサイズが上限（{maxBytes / (1024 * 1024)}MB）を超過しています。（指定: {incomingBytes / (1024 * 1024)}MB）") { }
    }
}
