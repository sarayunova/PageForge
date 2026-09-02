// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Models;

public sealed class CreateTeamRequest
{
    public required string Name { get; init; }
}

public sealed class TeamResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required UserResponse Owner { get; init; }
    public required DateTime CreatedAt { get; init; }
}

/// <summary>Full team detail including its resolved member roster.</summary>
public sealed class TeamDetailResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required UserResponse Owner { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required IReadOnlyList<TeamMemberResponse> Members { get; init; }
}

public sealed class AddMemberRequest
{
    public required Guid UserId { get; init; }
    public string Role { get; init; } = "Member";
}

public sealed class TeamMemberResponse
{
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required DateTime JoinedAt { get; init; }
}
