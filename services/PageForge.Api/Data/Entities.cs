// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.ComponentModel.DataAnnotations;

namespace PageForge.Api.Data;

public sealed class User
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    [Required, MaxLength(50)]
    public string AuthProvider { get; set; } = "local";

    public string? ExternalId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Team> OwnedTeams { get; set; } = [];
    public ICollection<TeamMember> TeamMemberships { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}

public sealed class Team
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TeamMember> Members { get; set; } = [];

    public ICollection<Document> Documents { get; set; } = [];
}

public sealed class TeamMember
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    [Required, MaxLength(20)]
    public string Role { get; set; } = "Member";

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RefreshToken
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    [Required]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsRevoked { get; set; }
}
