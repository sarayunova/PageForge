// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Api.Services;

namespace PageForge.Api.Tests;

/// <summary>In-memory <see cref="IBlobStorage"/> used by integration tests so the
/// sync flow runs hermetically with no MinIO/network.</summary>
public sealed class FakeBlobStorage : IBlobStorage
{
    private readonly Dictionary<string, byte[]> _store = new();
    public bool BucketCreated { get; private set; }

    public Task<bool> EnsureBucketAsync(CancellationToken ct)
    {
        BucketCreated = true;
        return Task.FromResult(true);
    }

    public Task<string> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        _store[$"{bucket}:{key}"] = ms.ToArray();
        return Task.FromResult(key);
    }

    public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct)
    {
        if (!_store.TryGetValue($"{bucket}:{key}", out byte[]? bytes))
            throw new KeyNotFoundException($"Blob {bucket}/{key} not found.");
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken ct)
    {
        _store.Remove($"{bucket}:{key}");
        return Task.CompletedTask;
    }
}