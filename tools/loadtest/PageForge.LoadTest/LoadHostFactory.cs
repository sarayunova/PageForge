// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PageForge.Api.Data;
using PageForge.Api.Services;
using PageForge.Api.Services.Email;

namespace PageForge.LoadTest;

/// <summary>
/// Hermetic host for the load harness: swaps PostgreSQL for an in-memory EF
/// provider and MinIO for the in-memory blob store, with a single shared
/// <see cref="InMemoryDatabaseRoot"/> so every host sees the same data. The no-op
/// OCR processor runs so job flows complete deterministically with no native
/// engine/network. Content root is pinned to the <c>PageForge.Api</c> project
/// folder (WebApplicationFactory content-root resolution otherwise looks under
/// this harness's bin dir and throws DirectoryNotFoundException).
/// </summary>
public sealed class LoadHostFactory : WebApplicationFactory<global::Program>
{
    public const string TestJwtKey = "load-test-only-secret-key-of-adequate-length-0123456789";
    public const string Issuer = "PageForge";
    public const string Audience = "PageForge";

    private static readonly InMemoryDatabaseRoot _root = new();
    private static readonly string _dbName = "pageforge-loadtest";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(RepoRoot, "services", "PageForge.Api"));
        builder.UseEnvironment("Testing");

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureServices(services =>
        {
            ServiceDescriptor? db = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (db is not null) services.Remove(db);

            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_dbName, _root)
                    .ConfigureWarnings(w =>
                        w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            ServiceDescriptor? blob = services.SingleOrDefault(
                d => d.ServiceType == typeof(IBlobStorage));
            if (blob is not null) services.Remove(blob);
            services.AddSingleton<IBlobStorage, PageForge.Api.Tests.FakeBlobStorage>();
            builder.UseSetting("Sync:Endpoint", "");
            builder.UseSetting("Sync:AccessKey", "");
            builder.UseSetting("Sync:SecretKey", "");

            ServiceDescriptor? email = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailSender));
            if (email is not null) services.Remove(email);
            services.AddSingleton<IEmailSender, PageForge.Api.Tests.RecordingEmailSender>();
            builder.UseSetting("Email:Provider", "none");

            builder.UseSetting("Jwt:Key", TestJwtKey);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Audience);
            builder.UseSetting("Jwt:AccessExpiryMinutes", "30");
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");

            ServiceProvider sp = services.BuildServiceProvider();
            using IServiceScope scope = sp.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ctx.Database.EnsureCreated();
        });
    }
}
