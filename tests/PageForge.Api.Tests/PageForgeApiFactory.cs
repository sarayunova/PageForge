// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using PageForge.Api.Data;
using PageForge.Api.Services;
using PageForge.Api.Services.Email;

namespace PageForge.Api.Tests;

/// <summary>
/// WebApplicationFactory for the hosted API that swaps the PostgreSQL
/// <see cref="AppDbContext"/> for an in-memory provider, so integration tests
/// run hermetically with no external database. The JWT signing key is forced to
/// a fixed test value so self-issued tokens remain valid across the lifetime of
/// the factory.
/// </summary>
public sealed class PageForgeApiFactory : WebApplicationFactory<Program>
{
    public const string TestJwtKey = "test-only-secret-key-of-adequate-length-0123456789";
    public const string Issuer = "PageForge";
    public const string Audience = "PageForge";

    // A fixed database name with a shared InMemoryDatabaseRoot so every DbContext
    // instance (the app's scoped one AND the factory's EnsureCreated context) sees
    // the same data. Static so all factory instances reuse one in-memory store and
    // one EF service provider instead of spawning one provider per test host.
    private static readonly InMemoryDatabaseRoot _root = new();
    private static readonly string _dbName = "pageforge-tests";

    /// <summary>Captured outbound email for this factory instance (e-sign reminders/certificates).</summary>
    public RecordingEmailSender Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            ServiceDescriptor? db = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (db is not null) services.Remove(db);

            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_dbName, _root)
                    .ConfigureWarnings(w =>
                        w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            // Substitute the real MinIO-backed blob storage with an in-memory fake
            // so sync integration tests need no MinIO/network, and disable the Sync
            // config section (which the real app reads from appsettings.json).
            ServiceDescriptor? blob = services.SingleOrDefault(
                d => d.ServiceType == typeof(IBlobStorage));
            if (blob is not null) services.Remove(blob);
            services.AddSingleton<IBlobStorage, FakeBlobStorage>();
            builder.UseSetting("Sync:Endpoint", "");
            builder.UseSetting("Sync:AccessKey", "");
            builder.UseSetting("Sync:SecretKey", "");

            // Replace the config-selected email sender with a capture sink so
            // e-sign tests can assert reminders/certificates without SMTP.
            ServiceDescriptor? email = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailSender));
            if (email is not null) services.Remove(email);
            services.AddSingleton<IEmailSender>(Email);
            builder.UseSetting("Email:Provider", "none");

            builder.UseSetting("Jwt:Key", TestJwtKey);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Audience);
            builder.UseSetting("Jwt:AccessExpiryMinutes", "30");
            builder.UseSetting("Database:AutoMigrate", "false");

            ServiceProvider sp = services.BuildServiceProvider();
            using IServiceScope scope = sp.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ctx.Database.EnsureCreated();
        });

        builder.UseSetting("detailedErrors", "true");
    }
}
