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

            e.HasIndex(d => new { d.OwnerId, d.Name });
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
    }
}
