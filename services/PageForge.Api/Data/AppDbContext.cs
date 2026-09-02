// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.EntityFrameworkCore;

namespace PageForge.Api.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<SignatureRequest> SignatureRequests => Set<SignatureRequest>();
    public DbSet<Signer> Signers => Set<Signer>();
    public DbSet<SignatureAuditEvent> SignatureAuditEvents => Set<SignatureAuditEvent>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<OcrJob> OcrJobs => Set<OcrJob>();
    public DbSet<OcrJobItem> OcrJobItems => Set<OcrJobItem>();
    public DbSet<OcrUsage> OcrUsages => Set<OcrUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => new { u.AuthProvider, u.ExternalId }).IsUnique()
                .HasFilter("\"ExternalId\" IS NOT NULL");
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.HasOne(t => t.Owner)
                .WithMany(u => u.OwnedTeams)
                .HasForeignKey(t => t.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TeamMember>(e =>
        {
            e.HasIndex(m => new { m.TeamId, m.UserId }).IsUnique();

            e.HasOne(m => m.Team)
                .WithMany(t => t.Members)
                .HasForeignKey(m => m.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(m => m.User)
                .WithMany(u => u.TeamMemberships)
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasOne(t => t.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Subscription>(e =>
        {
            e.HasIndex(s => s.StripeSubscriptionId).IsUnique();
            e.HasIndex(s => s.UserId).IsUnique();

            e.HasOne(s => s.User)
                .WithOne()
                .HasForeignKey<Subscription>(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.HasOne(d => d.Owner)
                .WithMany()
                .HasForeignKey(d => d.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(d => d.Team)
                .WithMany(t => t.Documents)
                .HasForeignKey(d => d.TeamId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(d => new { d.OwnerId, d.Name });
            e.HasIndex(d => d.TeamId);
        });

        modelBuilder.Entity<DocumentVersion>(e =>
        {
            e.HasIndex(v => new { v.DocumentId, v.VersionNumber }).IsUnique();
            e.HasIndex(v => new { v.DocumentId, v.IdempotencyKey }).IsUnique();
            e.HasIndex(v => v.BlobKey);

            e.HasOne(v => v.Document)
                .WithMany(d => d.Versions)
                .HasForeignKey(v => v.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // E-signature workflow (FR-ESIGN-01)
        modelBuilder.Entity<SignatureRequest>(e =>
        {
            e.HasIndex(r => r.OwnerId);
            e.HasIndex(r => r.IdempotencyKey);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.NextReminderAt);

            e.HasOne(r => r.Owner)
                .WithMany()
                .HasForeignKey(r => r.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.DocumentVersion)
                .WithMany()
                .HasForeignKey(r => r.DocumentVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Signer>(e =>
        {
            e.HasIndex(s => new { s.SignatureRequestId, s.Email });

            e.HasOne(s => s.SignatureRequest)
                .WithMany(r => r.Signers)
                .HasForeignKey(s => s.SignatureRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SignatureAuditEvent>(e =>
        {
            e.HasIndex(a => new { a.SignatureRequestId, a.CreatedAt });

            e.HasOne(a => a.SignatureRequest)
                .WithMany(r => r.AuditEvents)
                .HasForeignKey(a => a.SignatureRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Team review comments (FR-TEAM-01)
        modelBuilder.Entity<Comment>(e =>
        {
            e.HasIndex(c => new { c.DocumentVersionId, c.CreatedAt });
            e.HasIndex(c => new { c.DocumentVersionId, c.PageNumber });

            e.HasOne(c => c.DocumentVersion)
                .WithMany()
                .HasForeignKey(c => c.DocumentVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(c => c.Author)
                .WithMany()
                .HasForeignKey(c => c.AuthorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Batch OCR / conversion (FR-BATCH-01)
        modelBuilder.Entity<OcrJob>(e =>
        {
            e.HasIndex(j => j.OwnerId);
            e.HasIndex(j => j.IdempotencyKey);
            e.HasIndex(j => j.Status);

            e.HasOne(j => j.Owner)
                .WithMany()
                .HasForeignKey(j => j.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OcrJobItem>(e =>
        {
            e.HasIndex(i => new { i.OcrJobId, i.DocumentVersionId });
            e.HasIndex(i => i.Status);

            e.HasOne(i => i.OcrJob)
                .WithMany(j => j.Items)
                .HasForeignKey(i => i.OcrJobId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(i => i.DocumentVersion)
                .WithMany()
                .HasForeignKey(i => i.DocumentVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(i => i.OutputVersion)
                .WithMany()
                .HasForeignKey(i => i.OutputVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OcrUsage>(e =>
        {
            e.HasKey(u => u.UserId);
        });
    }
}
