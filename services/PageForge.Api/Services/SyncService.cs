// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PageForge.Api.Data;

namespace PageForge.Api.Services;

/// <summary>Raised when a pushed version derives from a stale base (FR-SYNC-02).</summary>
public sealed class VersionConflictException : Exception
{
    public VersionConflictException(int latestVersion, string latestSha256)
        : base($"Version conflict: the document has advanced to version {latestVersion}.")
    {
        LatestVersion = latestVersion;
        LatestSha256 = latestSha256;
    }

    public int LatestVersion { get; }
    public string LatestSha256 { get; }
}

/// <summary>A single document-version row plus its raw stream.</summary>
public sealed record VersionContent(DocumentVersion Version, Stream Content);

/// <summary>Output of a successful version push.</summary>
public sealed record PushedVersion(DocumentVersion Version);

/// <summary>
/// FR-SYNC-01/02: cross-device document sync with immutable version history,
/// full-file push/pull, and explicit conflict detection. The original file stays
/// an ordinary client file; each push stores a byte-addressed blob and a
/// monotonic version under the document. A push whose <c>baseVersionNumber</c>
/// is older than head is rejected (409) rather than silently overwriting —
/// the client surfaces the <see cref="VersionConflictException"/> payload so the
/// user can resolve (FR-SYNC-02).
/// </summary>
public sealed class SyncService
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly SyncOptions _options;

    public SyncService(AppDbContext db, IBlobStorage blobs, IOptions<SyncOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _options = options.Value;
    }

    // --- Documents ----------------------------------------------------------

    public async Task<Document> CreateDocumentAsync(Guid ownerId, string name)
    {
        var doc = new Document
        {
            OwnerId = ownerId,
            Name = name.Trim()
        };

        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();
        return doc;
    }

    public async Task<(IReadOnlyList<Document> Items, int? NextCursor)> ListDocumentsAsync(
        Guid ownerId, int? cursor, int limit, CancellationToken ct)
    {
        int after = cursor is null ? 0 : cursor.Value;

        var query = _db.Documents
            .Where(d => d.OwnerId == ownerId)
            .OrderByDescending(d => d.UpdatedAt);

        List<Document> items = await query.Skip(after).Take(limit + 1).ToListAsync(ct);
        int? next = items.Count > limit ? after + limit : null;

        return (items.Take(limit).ToList(), next);
    }

    public async Task<Document?> GetDocumentAsync(Guid ownerId, Guid docId, CancellationToken ct)
    {
        return await _db.Documents
            .Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.Id == docId && d.OwnerId == ownerId, ct);
    }

    // --- Versions -----------------------------------------------------------

    public async Task<PushedVersion> PushVersionAsync(
        Guid ownerId, Guid docId, int baseVersionNumber, string name,
        byte[] content, string contentType, string? idempotencyKey, CancellationToken ct)
    {
        Document doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == docId && d.OwnerId == ownerId, ct)
            ?? throw new KeyNotFoundException("Document not found.");

        DocumentVersion? versioned = await _db.DocumentVersions
            .Where(v => v.DocumentId == docId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);

        // Idempotent retry: if this Idempotency-Key already committed a version, return it.
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            DocumentVersion? existing = await _db.DocumentVersions
                .FirstOrDefaultAsync(
                    v => v.DocumentId == docId && v.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return new PushedVersion(existing);
        }

        int head = versioned?.VersionNumber ?? doc.LatestVersion;

        // Conflict detection (FR-SYNC-02): never silently discard either version.
        if (baseVersionNumber < head)
            throw new VersionConflictException(head, versioned?.Sha256 ?? string.Empty);

        string sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        int nextNumber = head + 1;
        string blobKey = $"{docId:N}/v{nextNumber}";

        await _blobs.EnsureBucketAsync(ct);
        await _blobs.PutAsync(_options.Bucket, blobKey, new MemoryStream(content), contentType, ct);

        var version = new DocumentVersion
        {
            DocumentId = docId,
            VersionNumber = nextNumber,
            BlobKey = blobKey,
            IdempotencyKey = idempotencyKey,
            Sha256 = sha,
            SizeBytes = content.Length,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow
        };

        _db.DocumentVersions.Add(version);
        doc.LatestVersion = nextNumber;
        doc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new PushedVersion(version);
    }

    public async Task<DocumentVersion?> GetLatestVersionAsync(Guid ownerId, Guid docId, CancellationToken ct)
    {
        Document doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == docId && d.OwnerId == ownerId, ct)
            ?? throw new KeyNotFoundException("Document not found.");

        return await _db.DocumentVersions
            .Where(v => v.DocumentId == docId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentVersion>> ListVersionsAsync(
        Guid ownerId, Guid docId, CancellationToken ct)
    {
        bool owns = await _db.Documents.AnyAsync(d => d.Id == docId && d.OwnerId == ownerId, ct);
        if (!owns) throw new KeyNotFoundException("Document not found.");

        return await _db.DocumentVersions
            .Where(v => v.DocumentId == docId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
    }

    public async Task<VersionContent> GetVersionContentAsync(
        Guid ownerId, Guid docId, int versionNumber, CancellationToken ct)
    {
        DocumentVersion? version = await _db.DocumentVersions
            .Join(_db.Documents,
                v => v.DocumentId,
                d => d.Id,
                (v, d) => new { v, d })
            .Where(x => x.v.DocumentId == docId
                        && x.v.VersionNumber == versionNumber
                        && x.d.OwnerId == ownerId)
            .Select(x => x.v)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Version not found.");

        Stream content = await _blobs.GetAsync(_options.Bucket, version.BlobKey, ct);
        return new VersionContent(version, content);
    }
}