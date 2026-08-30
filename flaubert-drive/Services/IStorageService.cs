using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Flaubert.Drive.Services
{
    public interface IStorageService
    {
        (string UploadUrl, string Key) GeneratePresignedUploadUrl(string tenantId, string contentType, double expireMinutes = 15);
        string GeneratePresignedDownloadUrl(string key, double expireMinutes = 15);
        Task DeleteAsync(string key);
        Task DeletePrefixAsync(string prefix);
        Task<List<string>> ListObjectsAsync(string prefix);
        Task CopyAsync(string sourceKey, string destinationKey);
        Task<bool> ExistsAsync(string key);
    }
}
