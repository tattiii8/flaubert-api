using System;
using System.IO;
using System.Threading.Tasks;

namespace Flaubert.Drive.Services
{
    public interface IStorageService
    {
        // テナントIDを受け取れるように第一引数に tenantId を追加
        (string UploadUrl, string Key) GeneratePresignedUploadUrl(string tenantId, string fileName, string contentType, double expireMinutes = 15);

        // 署名付きダウンロード URL を返す（引数の key は "テナントID/GUID_ファイル名" の完全なパスが入ります）
        string GeneratePresignedDownloadUrl(string key, double expireMinutes = 15);

        // 単一オブジェクトの削除
        Task DeleteAsync(string key);

        // テナント削除時などに特定プレフィックス（"tenantId/"）配下のオブジェクトを一括削除
        Task DeletePrefixAsync(string prefix);

        // 指定されたプレフィックス配下のすべてのオブジェクトキーを取得
        Task<System.Collections.Generic.List<string>> ListObjectsAsync(string prefix);
    }
}
