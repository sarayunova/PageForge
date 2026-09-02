// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PageForge.Api.Data;

namespace PageForge.Api.Services;

public sealed class AuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AuthService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string passwordHash)
    {
        string[] parts = passwordHash.Split('.');
        if (parts.Length != 2) return false;

        byte[] salt = Convert.FromBase64String(parts[0]);
        byte[] storedHash = Convert.FromBase64String(parts[1]);
        byte[] computedHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);

        return CryptographicOperations.FixedTimeEquals(storedHash, computedHash);
    }

    public async Task<AuthResult> RegisterAsync(string email, string displayName, string password)
    {
        if (await _db.Users.AnyAsync(u => u.Email == email))
            throw new InvalidOperationException("Email already registered.");

        var user = new User
        {
            Email = email,
            DisplayName = displayName,
            PasswordHash = HashPassword(password),
            AuthProvider = "local"
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return await GenerateTokensAsync(user);
    }

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        User? user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || user.PasswordHash is null || !VerifyPassword(password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid email or password.");

        return await GenerateTokensAsync(user);
    }

    public async Task<AuthResult> OAuthLoginAsync(string provider, string externalId, string email, string displayName)
    {
        User? user = await _db.Users.FirstOrDefaultAsync(
            u => u.AuthProvider == provider && u.ExternalId == externalId);

        if (user is null)
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is not null)
            {
                user.AuthProvider = provider;
                user.ExternalId = externalId;
            }
            else
            {
                user = new User
                {
                    Email = email,
                    DisplayName = displayName,
                    AuthProvider = provider,
                    ExternalId = externalId
                };
                _db.Users.Add(user);
            }
            await _db.SaveChangesAsync();
        }

        return await GenerateTokensAsync(user);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken)
    {
        string hash = HashToken(refreshToken);
        RefreshToken? stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && !t.IsRevoked);

        if (stored is null || stored.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Invalid or expired refresh token.");

        stored.IsRevoked = true;
        await _db.SaveChangesAsync();

        return await GenerateTokensAsync(stored.User);
    }

    public async Task<User?> GetUserByIdAsync(Guid userId)
    {
        return await _db.Users.FindAsync(userId);
    }

    private async Task<AuthResult> GenerateTokensAsync(User user)
    {
        string accessToken = GenerateAccessToken(user);
        (string refreshToken, DateTime expiresAt) = GenerateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = expiresAt
        });
        await _db.SaveChangesAsync();

        return new AuthResult
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            User = user
        };
    }

    private string GenerateAccessToken(User user)
    {
        byte[] key = Encoding.UTF8.GetBytes(
            _config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured."));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("auth_provider", user.AuthProvider)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(key),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(
                double.Parse(_config["Jwt:AccessExpiryMinutes"] ?? "15")),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static (string Token, DateTime ExpiresAt) GenerateRefreshToken()
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(64);
        string token = Convert.ToBase64String(randomBytes);
        DateTime expiresAt = DateTime.UtcNow.AddDays(7);
        return (token, expiresAt);
    }

    private static string HashToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hash);
    }
}

public sealed class AuthResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public User User { get; set; } = null!;
}
