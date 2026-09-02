// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Services;

/// <summary>
/// Abstraced blob store for document-version bytes. The production
/// implementation writes to MinIO/S3; tests substitute an in-memory fake so the
/// sync flow is exercised hermetically with no network.
/// </summary>
public interface IBlobStorage
{
    Task<bool> EnsureBucketAsync(CancellationToken ct);
    Task<string> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream> GetAsync(string bucket, string key, CancellationToken ct);
    Task DeleteAsync(string bucket, string key, CancellationToken ct);
}