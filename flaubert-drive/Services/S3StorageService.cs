using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace Flaubert.Drive.Services
{
    public class S3StorageService : IStorageService
    {
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;

        public S3StorageService(IAmazonS3 s3Client, IConfiguration configuration)
        {
            _s3Client = s3Client;
            _bucketName = configuration["AWS:BucketName"] ?? "my-default-bucket";
        }

        /// <summary>
        /// テナントIDごとのパス（{tenantId}/{Guid}_{fileName}）に保存するための署名付き URL と S3 Key を生成します。
        /// </summary>
        public (string UploadUrl, string Key) GeneratePresignedUploadUrl(string tenantId, string fileName, string contentType, double expireMinutes = 15)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            // パス・トラバーサル防止のためファイル名部分の純粋な名前のみを抽出
            var safeFileName = Path.GetFileName(fileName);

            // S3 の Key プレフィックス構造を構築: {tenantId}/{Guid}_{safeFileName}
            var key = $"{tenantId.Trim('/')}/{Guid.NewGuid()}_{safeFileName}";

            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.AddMinutes(expireMinutes),
                ContentType = contentType
            };

            var url = _s3Client.GetPreSignedURL(request);
            return (url, key);
        }

        /// <summary>
        /// ダウンロード用の署名付き URL を生成します。
        /// </summary>
        public string GeneratePresignedDownloadUrl(string key, double expireMinutes = 15)
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddMinutes(expireMinutes)
            };

            return _s3Client.GetPreSignedURL(request);
        }

        /// <summary>
        /// 指定された単一オブジェクトを削除します。
        /// </summary>
        public async Task DeleteAsync(string key)
        {
            await _s3Client.DeleteObjectAsync(_bucketName, key);
        }

        /// <summary>
        /// 指定されたプレフィックス（例: テナントID）配下のすべてのオブジェクトを一括削除します。
        /// </summary>
        public async Task DeletePrefixAsync(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return;
            }

            var formattedPrefix = prefix.EndsWith("/") ? prefix : $"{prefix}/";

            var listRequest = new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = formattedPrefix
            };

            ListObjectsV2Response listResponse;
            do
            {
                listResponse = await _s3Client.ListObjectsV2Async(listRequest);

                if (listResponse.S3Objects.Count > 0)
                {
                    var deleteRequest = new DeleteObjectsRequest
                    {
                        BucketName = _bucketName,
                        Objects = listResponse.S3Objects
                            .Select(obj => new KeyVersion { Key = obj.Key })
                            .ToList()
                    };

                    await _s3Client.DeleteObjectsAsync(deleteRequest);
                }

                listRequest.ContinuationToken = listResponse.NextContinuationToken;

            } while (listResponse.IsTruncated);
        }
    }
}
