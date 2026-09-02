// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace PageForge.Api.Services;

/// <summary>
/// MinIO (S3-compatible) blob store for document-version bytes. Skips the
/// local MinIO instance when the <see cref="SyncOptions"/> are unconfigured, so
/// the API still boots (and the sync-metadata endpoints work) without the blob
/// service running; only actual byte transfers require it.
/// </summary>
public sealed class BlobStorageService : IBlobStorage
{
    private readonly SyncOptions _options;
    private readonly IMinioClient _client;

    public BlobStorageService(IOptions<SyncOptions> options)
    {
        _options = options.Value;

        if (_options.IsConfigured)
        {
            _client = new MinioClient()
                .WithEndpoint(_options.Endpoint, _options.Port)
                .WithCredentials(_options.AccessKey, _options.SecretKey)
                .WithSSL(_options.UseSsl)
                .Build();
        }
        else
        {
            _client = null!;
        }
    }

    public async Task<bool> EnsureBucketAsync(CancellationToken ct)
    {
        if (_client is null) return false;

        bool exists = await _client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_options.Bucket), ct);
        if (!exists)
        {
            await _client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(_options.Bucket), ct);
        }
        return true;
    }

    public async Task<string> PutAsync(
        string bucket, string key, Stream content, string contentType, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Blob storage is not configured.");

        await _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(key)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType(contentType), ct);

        return key;
    }

    public async Task<Stream> GetAsync(string bucket, string key, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Blob storage is not configured.");

        var ms = new MemoryStream();
        await _client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(key)
            .WithCallbackStream(stream => stream.CopyTo(ms)), ct);
        ms.Position = 0;
        return ms;
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken ct)
    {
        if (_client is null) return;
        await _client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(bucket)
            .WithObject(key), ct);
    }
}