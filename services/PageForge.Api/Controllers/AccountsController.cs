// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PageForge.Api.Models;
using PageForge.Api.Services;

namespace PageForge.Api.Controllers;

[ApiController]
[Route("api/v1/accounts")]
public sealed class AccountsController : ControllerBase
{
    private readonly AuthService _auth;

    public AccountsController(AuthService auth) => _auth = auth;

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        try
        {
            AuthResult result = await _auth.RegisterAsync(
                request.Email, request.DisplayName, request.Password);

            return Ok(new AuthResponse
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiresAt = result.ExpiresAt,
                User = MapUser(result.User)
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "EMAIL_EXISTS",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            AuthResult result = await _auth.LoginAsync(request.Email, request.Password);

            return Ok(new AuthResponse
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiresAt = result.ExpiresAt,
                User = MapUser(result.User)
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_CREDENTIALS",
                    Message = "Invalid email or password.",
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpGet("login/{provider}")]
    public IActionResult LoginWithProvider(string provider)
    {
        string? scheme = provider.ToLowerInvariant() switch
        {
            "google" => "Google",
            "microsoft" => "Microsoft",
            _ => null
        };

        if (scheme is null)
        {
            return BadRequest(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "UNSUPPORTED_PROVIDER",
                    Message = $"OAuth provider '{provider}' is not supported.",
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }

        string redirectUrl = Url.Action(nameof(LoginCallback), new { provider }) ?? "/";

        return Challenge(new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            RedirectUri = redirectUrl
        }, scheme);
    }

    [HttpGet("login/{provider}/callback")]
    public async Task<IActionResult> LoginCallback(string provider)
    {
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return Unauthorized(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "OAUTH_FAILED",
                    Message = "OAuth authentication failed.",
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }

        string externalId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        string email = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        string displayName = User.FindFirstValue(ClaimTypes.Name) ?? email;

        AuthResult result = await _auth.OAuthLoginAsync(
            provider.ToLowerInvariant(), externalId, email, displayName);

        return Ok(new AuthResponse
        {
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            ExpiresAt = result.ExpiresAt,
            User = MapUser(result.User)
        });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        try
        {
            AuthResult result = await _auth.RefreshAsync(request.RefreshToken);

            return Ok(new AuthResponse
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiresAt = result.ExpiresAt,
                User = MapUser(result.User)
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_REFRESH_TOKEN",
                    Message = "Invalid or expired refresh token.",
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        Guid userId = GetUserId();
        var user = await _auth.GetUserByIdAsync(userId);
        if (user is null)
            return NotFound();

        return Ok(MapUser(user));
    }

    private Guid GetUserId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub ?? throw new UnauthorizedAccessException());
    }

    private static UserResponse MapUser(Data.User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        DisplayName = user.DisplayName,
        AuthProvider = user.AuthProvider,
        CreatedAt = user.CreatedAt
    };
}
