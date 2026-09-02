// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PageForge.Api.Models;
using PageForge.Api.Services;

namespace PageForge.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/teams")]
public sealed class TeamsController : ControllerBase
{
    private readonly TeamService _teams;

    public TeamsController(TeamService teams) => _teams = teams;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTeamRequest request)
    {
        Guid userId = GetUserId();
        var team = await _teams.CreateTeamAsync(userId, request.Name);

        return CreatedAtAction(
            nameof(GetTeam),
            new { id = team.Id },
            new TeamResponse
            {
                Id = team.Id,
                Name = team.Name,
                Owner = new UserResponse
                {
                    Id = userId,
                    Email = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
                    DisplayName = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
                    AuthProvider = "local",
                    CreatedAt = DateTime.UtcNow
                },
                CreatedAt = team.CreatedAt
            });
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        Guid userId = GetUserId();
        var teams = await _teams.ListTeamsAsync(userId);

        return Ok(teams.Select(t => new TeamResponse
        {
            Id = t.Id,
            Name = t.Name,
            Owner = new UserResponse
            {
                Id = t.Owner.Id,
                Email = t.Owner.Email,
                DisplayName = t.Owner.DisplayName,
                AuthProvider = t.Owner.AuthProvider,
                CreatedAt = t.Owner.CreatedAt
            },
            CreatedAt = t.CreatedAt
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTeam(Guid id)
    {
        Guid userId = GetUserId();
        if (!await _teams.IsMemberAsync(id, userId))
            return NotFound();

        return Ok(new { id });
    }

    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddMemberRequest request)
    {
        Guid userId = GetUserId();
        if (!await _teams.IsMemberAsync(id, userId))
            return Forbid();

        try
        {
            var member = await _teams.AddMemberAsync(id, request.UserId, request.Role);
            return Ok(new TeamMemberResponse
            {
                UserId = member.UserId,
                Email = string.Empty,
                DisplayName = string.Empty,
                Role = member.Role,
                JoinedAt = member.JoinedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "ALREADY_MEMBER",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    private Guid GetUserId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub ?? throw new UnauthorizedAccessException());
    }
}
