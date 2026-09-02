// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.EntityFrameworkCore;
using PageForge.Api.Data;

namespace PageForge.Api.Services;

public sealed class TeamService
{
    private readonly AppDbContext _db;

    public TeamService(AppDbContext db) => _db = db;

    public async Task<Team> CreateTeamAsync(Guid ownerId, string name)
    {
        var team = new Team
        {
            Name = name,
            OwnerId = ownerId
        };

        _db.Teams.Add(team);

        _db.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = ownerId,
            Role = "Owner"
        });

        await _db.SaveChangesAsync();
        return team;
    }

    public async Task<IReadOnlyList<Team>> ListTeamsAsync(Guid userId)
    {
        return await _db.TeamMembers
            .Where(m => m.UserId == userId)
            .Include(m => m.Team)
                .ThenInclude(t => t.Owner)
            .Select(m => m.Team)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<TeamMember> AddMemberAsync(Guid teamId, Guid userId, string role = "Member")
    {
        Team team = await _db.Teams.FindAsync(teamId)
            ?? throw new KeyNotFoundException("Team not found.");

        if (await _db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == userId))
            throw new InvalidOperationException("User is already a member of this team.");

        var member = new TeamMember
        {
            TeamId = teamId,
            UserId = userId,
            Role = role
        };

        _db.TeamMembers.Add(member);
        await _db.SaveChangesAsync();

        return member;
    }

    public async Task<bool> IsMemberAsync(Guid teamId, Guid userId)
    {
        return await _db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == userId);
    }
}
