// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>Env var that flips this factory into hosted-CI mode ("1" = the `api-hosted` lane).</summary>
    public const string HostedCiEnvVar = "PAGEFORGE_HOSTED_CI";

    // A fixed database name with a shared InMemoryDatabaseRoot so every DbContext
    // instance (the app's scoped one AND the factory's EnsureCreated context) sees
    // the same data. Static so all factory instances reuse one in-memory store and
    // one EF service provider instead of spawning one provider per test host.
    private static readonly InMemoryDatabaseRoot _root = new();
    private static readonly string _dbName = "pageforge-tests";

    /// <summary>Captured outbound email for this factory instance (e-sign reminders/certificates).</summary>
    public RecordingEmailSender Email { get; } = new();

    /// <summary>True when the <see cref="HostedCiEnvVar"/> gate is "1" (CI `api-hosted` job).</summary>
    public static bool HostedCiEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(HostedCiEnvVar) ?? "0", "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Hosted-lane capture of the real PostgreSQL connection string — set ONLY in
    /// the hosted <see cref="ConfigureWebHost"/> branch (hermetic never enters it),
    /// so this stays byte-neutral for hermetic. The standalone schema-provision
    /// seam in <see cref="InitializeAsync"/> reads it to build the real schema on a
    /// standalone <see cref="AppDbContext"/> WITHOUT touching <see cref="Services"/>
    /// (the <see cref="Services"/> getter deterministically starts the whole host,
    /// which fires <c>OcrJobWorker</c>'s startup sweep against an EMPTY real
    /// Postgres → <c>Npgsql.PostgresException 42P01</c> <i>before</i> any migration
    /// can run — the hosted lane's first-order residual). This field is hermetic-
    /// neutral: hermetic never reads or writes it.
    /// </summary>
    private static string? HostedDefaultConnection { get; set; }

    /// <summary>Serializes the hosted lane's one-time schema migration across the
    /// per-class hosts, which all share a single real database.</summary>
    private static readonly object HostedSchemaGate = new();

    private static bool _hostedSchemaReady;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        bool hosted = HostedCiEnabled;

        builder.ConfigureServices(services =>
        {
            if (!hosted)
            {
                // Hermetic (default): swap the real Npgsql context for the in-memory
                // provider and the real MinIO-backed blob storage for a fake, so
                // integration tests run hermetically with no external database.
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
            }
            else
            {
                // Hosted CI (`PAGEFORGE_HOSTED_CI=1`, the `api-hosted` job): KEEP the
                // real Npgsql <see cref="AppDbContext"/> and the real MinIO-backed
                // <see cref="IBlobStorage"/> exactly as the real Program.cs registers
                // them (the job's service containers provide Postgres + MinIO), and
                // turn on EF auto-migration so `MigrateAsync` provisions the schema —
                // the same path the deployed hosted API takes. All other seams below
                // (email capture, fixed JWT) are unchanged so the 46 e-identity and
                // e-sign assertions behave identically in both modes.
                builder.UseSetting("Database:AutoMigrate", "true");

                // ...but do not rely on it. Database:AutoMigrate drives a block in
                // Program.cs that sits between builder.Build() and app.Run(), and
                // WebApplicationFactory captures the host at build time and never
                // runs the rest of the entry point. That block therefore does not
                // execute here, the schema was never created, and every hosted test
                // failed with 42P01 "relation OcrJobItems does not exist" - which is
                // exactly what the lane did the first time it got far enough to run.
                //
                // The hermetic branch below has always provisioned its own schema
                // for this reason. The hosted branch now does the same, with Migrate
                // rather than EnsureCreated so it applies the real migrations - the
                // same ones a deployment applies.
                //
                // Once per process: every test class builds its own host against one
                // shared database, and migrating the same database concurrently
                // races.
                lock (HostedSchemaGate)
                {
                    if (!_hostedSchemaReady)
                    {
                        ServiceProvider hostedSp = services.BuildServiceProvider();
                        using IServiceScope hostedScope = hostedSp.CreateScope();
                        var hostedDb = hostedScope.ServiceProvider.GetRequiredService<AppDbContext>();
                        hostedDb.Database.Migrate();
                        _hostedSchemaReady = true;
                    }
                }
            }

            // Replace the config-selected email sender with a capture sink so
            // e-sign tests can assert reminders/certificates without SMTP.
            // Every test class builds its own host, and they all share one static
            // in-memory database - but each host has its OWN recording email sender.
            // With the start-up sweep on, a host starting up would pick up another
            // host's queued OCR item, process it in its own scope, and deliver the
            // completion email to its own sender instead of the owning test's. The
            // job read Completed and the email was nowhere, so
            // Submit_job_completes_and_notifies_owner failed about one run in three.
            // Tests enqueue in-process at submit time, so nothing here needs the sweep.
            builder.UseSetting("Ocr:SweepQueuedOnStart", "false");

            ServiceDescriptor? email = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailSender));
            if (email is not null) services.Remove(email);
            services.AddSingleton<IEmailSender>(Email);
            builder.UseSetting("Email:Provider", "none");

            builder.UseSetting("Jwt:Key", TestJwtKey);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Audience);
            builder.UseSetting("Jwt:AccessExpiryMinutes", "30");

            if (!hosted)
            {
                builder.UseSetting("Database:AutoMigrate", "false");

                ServiceProvider sp = services.BuildServiceProvider();
                using IServiceScope scope = sp.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                ctx.Database.EnsureCreated();
            }
        });

        builder.UseSetting("detailedErrors", "true");
    }

    /// <summary>
    /// Hosted-lane teardown seam: deterministically stop <see cref="OcrJobWorker"/>'s
    /// background sweep BEFORE the host's DI provider is disposed, so its final
    /// Npgsql/MinIO I/O no longer lands against a DISPOSED
    /// <see cref="IServiceProvider"/> — the <c>ObjectDisposedException</c> at
    /// <c>ServiceLookup.ThrowHelper</c> the hosted lane echoes during teardown
    /// (hermetic never reaches it; hermetic keeps its own synchronous in-memory
    /// worker sweep that finishes before dispose). Hermetic gate off → this whole
    /// branch is byte-neutral and unreachable hermetic; nothing here is asserted.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (HostedCiEnabled && HostedDefaultConnection is not null)
        {
            try
            {
                // Host is deterministically ALREADY started in the hosted lane (every
                // hosted test hit `Services`), so this getter is a no-op reacquire, not
                // a lazy start. Draining the sweep before teardown silences the
                // disposed-provider echo without touching any hermetic byte.
                var provider = Services;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var service in provider.GetServices<IHostedService>())
                {
                    if (service is OcrJobWorker worker)
                        await worker.StopAsync(cts.Token);
                }
            }
            catch (ObjectDisposedException)
            {
                // Teardown already in flight; residual is infrastructure-only and
                // assertion-neutral. Hermetic never enters here.
            }
            catch
            {
                // Teardown hygiene only — no assertion surface, hermetic-neutral.
            }
        }

        await base.DisposeAsync();
    }
}
