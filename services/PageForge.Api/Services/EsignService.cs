// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.EntityFrameworkCore;
using PageForge.Api.Data;
using PageForge.Api.Services.Email;

namespace PageForge.Api.Services;

/// <summary>
/// FR-ESIGN-01 send-for-signature workflow: request lifecycle state machine
/// (Draft → Sent → Viewed → Completed/Declined), per-signer status, reminder
/// scheduler, and the completion certificate/audit trail. Email is delivered
/// through <see cref="IEmailSender"/> so dev/test hosts never need SMTP.
/// </summary>
public sealed class EsignService
{
    private readonly AppDbContext _db;
    private readonly IEmailSender _email;

    public EsignService(AppDbContext db, IEmailSender email)
    {
        _db = db;
        _email = email;
    }

    /// <summary>Creates a draft request and returns its id. Idempotent on <paramref name="idempotencyKey"/>.</summary>
    public async Task<SignatureRequest> CreateAsync(
        Guid ownerId, Guid documentVersionId, string title, IReadOnlyList<(string Email, string DisplayName)> signers,
        string? idempotencyKey, CancellationToken ct)
    {
        if (signers.Count == 0)
            throw new InvalidOperationException("A signature request needs at least one signer.");

        if (idempotencyKey is not null)
        {
            SignatureRequest? existing = await _db.SignatureRequests
                .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return existing;
        }

        var request = new SignatureRequest
        {
            OwnerId = ownerId,
            DocumentVersionId = documentVersionId,
            Title = title,
            Status = SignatureStatus.Draft,
            IdempotencyKey = idempotencyKey,
            Signers = signers.Select((s, i) => new Signer
            {
                Email = s.Email,
                DisplayName = s.DisplayName,
                Status = SignerStatus.Pending,
                Order = i + 1
            }).ToList()
        };

        request.AuditEvents.Add(new SignatureAuditEvent
        {
            ActorEmail = "system",
            Action = "request.created",
            Detail = $"Created draft for document version {documentVersionId}"
        });

        _db.SignatureRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        return request;
    }

    public async Task<SignatureRequest> GetAsync(Guid ownerId, Guid id, CancellationToken ct)
    {
        var request = await LoadAsync(ownerId, id, ct)
            ?? throw new KeyNotFoundException("Signature request not found.");
        return request;
    }

    public async Task<IReadOnlyList<SignatureRequest>> ListAsync(Guid ownerId, int? cursor, int limit, CancellationToken ct)
    {
        int bounded = Math.Clamp(limit, 1, 100);
        return await _db.SignatureRequests
            .Where(r => r.OwnerId == ownerId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip(cursor ?? 0)
            .Take(bounded + 1)
            .Include(r => r.Signers)
            .ToListAsync(ct);
    }

    /// <summary>Moves a draft to Sent, emails every signer, and arms the reminder scheduler.</summary>
    public async Task SendAsync(Guid ownerId, Guid id, int reminderDays, CancellationToken ct)
    {
        var request = await LoadAsync(ownerId, id, ct)
            ?? throw new KeyNotFoundException("Signature request not found.");

        if (request.Status != SignatureStatus.Draft)
            throw new InvalidOperationException("Only drafts can be sent.");

        request.Status = SignatureStatus.Sent;
        request.SentAt = DateTime.UtcNow;
        request.NextReminderAt = DateTime.UtcNow.AddDays(Math.Max(1, reminderDays));
        await AuditAsync(request, "request.sent", $"Sent to {request.Signers.Count} signer(s).");

        await _db.SaveChangesAsync(ct);

        foreach (Signer signer in request.Signers.OrderBy(s => s.Order))
            await SendSignerEmailAsync(request, signer, isReminder: false, reminderDays, ct);
    }

    /// <summary>Records that a signer viewed the document; sets the request to Viewed.</summary>
    public async Task MarkViewedAsync(Guid ownerId, Guid id, string email, CancellationToken ct)
    {
        var request = await LoadAsync(ownerId, id, ct)
            ?? throw new KeyNotFoundException("Signature request not found.");

        Signer signer = FindSigner(request, email);
        if (signer.Status == SignerStatus.Pending)
        {
            signer.Status = SignerStatus.Viewed;
            signer.ViewedAt = DateTime.UtcNow;

            if (request.Status == SignatureStatus.Sent)
            {
                request.Status = SignatureStatus.Viewed;
                await AuditAsync(request, "request.viewed", $"{email} viewed the document.");
            }
            await AuditAsync(request, "signer.viewed", $"{email} opened the request.");
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Signs on behalf of one signer; completes the request when all signers have signed.</summary>
    public async Task<SignatureRequest> SignAsync(Guid ownerId, Guid id, string email, CancellationToken ct)
    {
        var request = await LoadAsync(ownerId, id, ct)
            ?? throw new KeyNotFoundException("Signature request not found.");

        if (request.Status is SignatureStatus.Completed or SignatureStatus.Declined)
            throw new InvalidOperationException("This request is already closed.");

        Signer signer = FindSigner(request, email);
        if (signer.Status is SignerStatus.Signed or SignerStatus.Declined)
            throw new InvalidOperationException($"Signer {email} already made a decision.");

        signer.Status = SignerStatus.Signed;
        signer.SignedAt = DateTime.UtcNow;
        signer.ViewedAt ??= DateTime.UtcNow;

        await AuditAsync(request, "signer.signed", $"{email} signed.");

        await _db.SaveChangesAsync(ct);

        if (request.Signers.All(s => s.Status == SignerStatus.Signed))
        {
            request.Status = SignatureStatus.Completed;
            request.CompletedAt = DateTime.UtcNow;
            await AuditAsync(request, "request.completed", "All signers signed.");
            await _db.SaveChangesAsync(ct);

            await SendCompletionCertificateAsync(request, ct);
        }

        return request;
    }

    /// <summary>Declines the request; it moves to Declined and no further signing is allowed.</summary>
    public async Task<SignatureRequest> DeclineAsync(Guid ownerId, Guid id, string email, string reason, CancellationToken ct)
    {
        var request = await LoadAsync(ownerId, id, ct)
            ?? throw new KeyNotFoundException("Signature request not found.");

        if (request.Status is SignatureStatus.Completed or SignatureStatus.Declined)
            throw new InvalidOperationException("This request is already closed.");

        Signer signer = FindSigner(request, email);
        if (signer.Status is SignerStatus.Signed or SignerStatus.Declined)
            throw new InvalidOperationException($"Signer {email} already made a decision.");

        signer.Status = SignerStatus.Declined;
        signer.DeclineReason = reason;
        signer.ViewedAt ??= DateTime.UtcNow;

        request.Status = SignatureStatus.Declined;
        request.CompletedAt = DateTime.UtcNow;

        await AuditAsync(request, "signer.declined", $"{email} declined: {reason}");
        await AuditAsync(request, "request.declined", "Request closed as declined.");
        await _db.SaveChangesAsync(ct);

        return request;
    }

    /// <summary>Tries every pending signer for a reminder; called by the background reminder scheduler.</summary>
    public async Task RunReminderChecksAsync(int reminderDays, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        var due = await _db.SignatureRequests
            .Include(r => r.Signers)
            .Where(r => r.Status == SignatureStatus.Sent && r.NextReminderAt <= now)
            .ToListAsync(ct);

        foreach (SignatureRequest request in due)
        {
            foreach (Signer signer in request.Signers.Where(s => s.Status is SignerStatus.Pending or SignerStatus.Viewed).OrderBy(s => s.Order))
            {
                await SendSignerEmailAsync(request, signer, isReminder: true, reminderDays, ct);
                request.ReminderCount++;
            }

            request.NextReminderAt = now.AddDays(Math.Max(1, reminderDays));
            await AuditAsync(request, "request.reminder", $"Reminders sent to {request.Signers.Count(s => s.Status is not SignerStatus.Signed and not SignerStatus.Declined)} pending signers.");
        }

        if (due.Count > 0)
            await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SignatureAuditEvent>> GetAuditTrailAsync(Guid ownerId, Guid id, CancellationToken ct)
    {
        var request = await GetAsync(ownerId, id, ct);
        return request.AuditEvents.OrderBy(a => a.CreatedAt).ToList();
    }

    public async Task<SignatureRequest> GetCertificateAsync(Guid ownerId, Guid id, CancellationToken ct)
    {
        var request = await LoadAsync(ownerId, id, ct)
            ?? throw new KeyNotFoundException("Signature request not found.");
        return request;
    }

    // --- Internals ----------------------------------------------------------

    private async Task<SignatureRequest?> LoadAsync(Guid ownerId, Guid id, CancellationToken ct)
        => await _db.SignatureRequests
            .Where(r => r.Id == id && r.OwnerId == ownerId)
            .Include(r => r.Signers.OrderBy(s => s.Order))
            .Include(r => r.AuditEvents.OrderBy(a => a.CreatedAt))
            .FirstOrDefaultAsync(ct);

    private static Signer FindSigner(SignatureRequest request, string email)
        => request.Signers.FirstOrDefault(s =>
            string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Signer {email} is not part of this request.");

    private Task AuditAsync(SignatureRequest request, string action, string detail)
    {
        var evt = new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            ActorEmail = action.StartsWith("request.", StringComparison.Ordinal)
                ? "system"
                : request.Signers.FirstOrDefault()?.Email ?? "system",
            Action = action,
            Detail = detail
        };
        // Explicit DbSet.Add marks the new row Added. Adding to the parent's
        // navigation instead would let EF infer the client-generated Guid key as a
        // pre-existing (Modified) entity and throw a concurrency exception on save.
        _db.SignatureAuditEvents.Add(evt);
        return Task.CompletedTask;
    }

    private async Task SendSignerEmailAsync(
        SignatureRequest request, Signer signer, bool isReminder, int reminderDays, CancellationToken ct)
    {
        string subject = isReminder
            ? $"Reminder: \"{request.Title}\" is waiting for your signature"
            : $"Please sign \"{request.Title}\"";

        string body = isReminder
            ? $"{signer.DisplayName}, we're still waiting for your signature on \"{request.Title}\". "
              + "Open PageForge to view and sign."
            : $"{signer.DisplayName}, please review and sign \"{request.Title}\" in PageForge.";

        await _email.SendAsync(new EmailMessage
        {
            To = signer.Email,
            Subject = subject,
            PlainTextBody = body
        }, ct);
    }

    private async Task SendCompletionCertificateAsync(SignatureRequest request, CancellationToken ct)
    {
        string? ownerEmail = await _db.Users
            .Where(u => u.Id == request.OwnerId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);

        string body = $"All signers have signed \"{request.Title}\" (completed {request.CompletedAt:o}).";
        foreach (Signer s in request.Signers.OrderBy(s => s.Order))
            body += $"\n- {s.Email} signed {s.SignedAt:o}";

        await _email.SendAsync(new EmailMessage
        {
            To = string.IsNullOrEmpty(ownerEmail) ? request.Signers.FirstOrDefault()?.Email ?? string.Empty : ownerEmail,
            Subject = $"Completed: \"{request.Title}\"",
            PlainTextBody = body
        }, ct);
    }
}