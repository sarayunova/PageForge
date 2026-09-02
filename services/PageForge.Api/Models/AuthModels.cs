// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Models;

public sealed class RegisterRequest
{
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Password { get; init; }
}

public sealed class LoginRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }
}

public sealed class RefreshRequest
{
    public required string RefreshToken { get; init; }
}

public sealed class AuthResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required UserResponse User { get; init; }
}

public sealed class UserResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string AuthProvider { get; init; }
    public required DateTime CreatedAt { get; init; }
}
