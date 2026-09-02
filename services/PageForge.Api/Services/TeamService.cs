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

    public async Task<Team> GetTeamAsync(Guid teamId)
    {
        return await _db.Teams
            .AsNoTracking()
            .Include(t => t.Owner)
            .SingleAsync(t => t.Id == teamId);
    }

    public async Task<User> GetUserAsync(Guid userId)
    {
        return await _db.Users
            .AsNoTracking()
            .SingleAsync(u => u.Id == userId);
    }

    /// <summary>List a team's members, joined to their User records for display.</summary>
    public async Task<IReadOnlyList<TeamMember>> ListMembersAsync(Guid teamId)
    {
        return await _db.TeamMembers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Include(m => m.User)
            .OrderBy(m => m.Role == "Owner" ? 0 : 1)
            .ThenBy(m => m.User.DisplayName)
            .ToListAsync();
    }

    /// <summary>Share a document with a team. Only the document owner may do so.</summary>
    public async Task<Document> ShareDocumentAsync(Guid documentId, Guid ownerId, Guid teamId)
    {
        Document document = await _db.Documents.FindAsync(documentId)
            ?? throw new KeyNotFoundException("Document not found.");

        if (document.OwnerId != ownerId)
            throw new UnauthorizedAccessException("Only the document owner can share it.");

        if (!await _db.Teams.AnyAsync(t => t.Id == teamId))
            throw new KeyNotFoundException("Team not found.");

        document.TeamId = teamId;
        document.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return document;
    }

    /// <summary>The team a document is shared with, or null when not shared. Returns the
    /// shared team for any caller, or returns the document's owner team scope.</summary>
    public async Task<Guid?> GetSharedTeamIdAsync(Guid documentId)
    {
        Guid? teamId = await _db.Documents
            .Where(d => d.Id == documentId)
            .Select(d => (Guid?)d.TeamId)
            .SingleOrDefaultAsync();
        return teamId;
    }
}
