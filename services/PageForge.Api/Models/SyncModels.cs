// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Models;

public sealed class CreateDocumentRequest
{
    public required string Name { get; init; }
}

public sealed class DocumentResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int LatestVersion { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public sealed class DocumentSummaryResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int LatestVersion { get; init; }
    public string LatestSha256 { get; init; } = string.Empty;
    public DateTime? LatestVersionCreatedAt { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public sealed class DocumentListResponse
{
    public required IReadOnlyList<DocumentResponse> Items { get; init; }
    public int? NextCursor { get; init; }
}

public sealed class VersionResponse
{
    public required Guid Id { get; init; }
    public required int VersionNumber { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public sealed class LatestVersionResponse
{
    public VersionResponse? Latest { get; init; }
    public required int DocumentLatestVersion { get; init; }
    /// <summary>Present when the caller's local version differs from the server head.</summary>
    public VersionConflictDetail? Conflict { get; init; }
}

public sealed class VersionConflictDetail
{
    public required int LatestVersion { get; init; }
    public required string LatestSha256 { get; init; }
}