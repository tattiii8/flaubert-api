using System;
using System.Collections.Generic;
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
            _bucketName = configuration["AWS:BucketName"] ?? throw new InvalidOperationException("AWS:BucketName is required.");
        }

        public (string UploadUrl, string Key) GeneratePresignedUploadUrl(string tenantId, string contentType, double expireMinutes = 15)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            var fileId = Guid.NewGuid();
            var key = $"{tenantId.Trim('/')}/{fileId:D}";

            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.AddMinutes(expireMinutes),
                ContentType = contentType
            };

            return (_s3Client.GetPreSignedURL(request), key);
        }

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

        public async Task DeleteAsync(string key) => await _s3Client.DeleteObjectAsync(_bucketName, key);

        public async Task CopyAsync(string sourceKey, string destinationKey)
        {
            await _s3Client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucketName,
                SourceKey = sourceKey,
                DestinationBucket = _bucketName,
                DestinationKey = destinationKey,
                MetadataDirective = S3MetadataDirective.COPY
            });
        }

        public async Task<bool> ExistsAsync(string key)
        {
            try
            {
                await _s3Client.GetObjectMetadataAsync(_bucketName, key);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task DeletePrefixAsync(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return;
            var formattedPrefix = prefix.EndsWith("/") ? prefix : prefix + "/";
            var request = new ListObjectsV2Request { BucketName = _bucketName, Prefix = formattedPrefix };

            do
            {
                var response = await _s3Client.ListObjectsV2Async(request);
                if (response.S3Objects.Count > 0)
                {
                    await _s3Client.DeleteObjectsAsync(new DeleteObjectsRequest
                    {
                        BucketName = _bucketName,
                        Objects = response.S3Objects.Select(x => new KeyVersion { Key = x.Key }).ToList()
                    });
                }
                request.ContinuationToken = response.NextContinuationToken;
                if (!response.IsTruncated) break;
            } while (true);
        }

        public async Task<List<string>> ListObjectsAsync(string prefix)
        {
            var keys = new List<string>();
            if (string.IsNullOrWhiteSpace(prefix)) return keys;
            var request = new ListObjectsV2Request { BucketName = _bucketName, Prefix = prefix.EndsWith("/") ? prefix : prefix + "/" };
            do
            {
                var response = await _s3Client.ListObjectsV2Async(request);
                keys.AddRange(response.S3Objects.Select(x => x.Key));
                request.ContinuationToken = response.NextContinuationToken;
                if (!response.IsTruncated) break;
            } while (true);
            return keys;
        }
    }
}
