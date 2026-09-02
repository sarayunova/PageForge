// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Services;

/// <summary>
/// Strongly-typed configuration for the MinIO (S3-compatible) document blob
/// store, read from the "Sync" section of appsettings.json / environment.
/// </summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public string Endpoint { get; init; } = string.Empty;
    public int Port { get; init; } = 9000;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public bool UseSsl { get; init; }
    public string Bucket { get; init; } = "pageforge-documents";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Endpoint) &&
        !string.IsNullOrEmpty(AccessKey) &&
        !string.IsNullOrEmpty(SecretKey);
}