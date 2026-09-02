// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PageForge.Api.Data;
using PageForge.Api.Middleware;
using PageForge.Api.Services;
using PageForge.Api.Services.Email;

var builder = WebApplication.CreateBuilder(args);

// EF Core + PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Authentication
string jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme);

// OAuth providers are added only when their client credentials are configured;
// an empty ClientId would throw on first use and break every request. This
// keeps local/dev/test hosts healthy with no external accounts registered.
string googleClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "";
if (!string.IsNullOrEmpty(googleClientId))
{
    builder.Services.AddAuthentication().AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
        {
            options.ClientId = googleClientId;
            options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "";
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        });
}

string msClientId = builder.Configuration["Authentication:Microsoft:ClientId"] ?? "";
if (!string.IsNullOrEmpty(msClientId))
{
    builder.Services.AddAuthentication().AddMicrosoftAccount(options =>
        {
            options.ClientId = msClientId;
            options.ClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"] ?? "";
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        });
}

builder.Services.AddAuthorization();

// Services
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<TeamService>();
builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection(StripeOptions.SectionName));
builder.Services.AddScoped<BillingService>();

// Sync / versioning (FR-SYNC-01/02)
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.AddSingleton<IBlobStorage, BlobStorageService>();
builder.Services.AddScoped<SyncService>();

// Email delivery (FR-ESIGN-01 reminders + completion certificates). Provider is
// selected from config so dev/test hosts use the no-op sink with no SMTP.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddSingleton<IEmailSender>(EmailSenderFactory.Create);

// E-signature workflow (FR-ESIGN-01)
builder.Services.AddScoped<EsignService>();

// Team review (FR-TEAM-01)
builder.Services.AddScoped<TeamReviewService>();

// Batch OCR / conversion (FR-BATCH-01)
builder.Services.Configure<OcrOptions>(builder.Configuration.GetSection(OcrOptions.SectionName));
if (bool.TryParse(builder.Configuration["Ocr:EnableNativeEngine"], out bool nativeEngine) && nativeEngine)
{
    // Real MuPDF+Tesseract processor: fetches the source blob, runs local OCR, and
    // returns a searchable PDF. Requires pageforge_mupdf.dll + tessdata on output.
    builder.Services.AddSingleton<IOcrJobProcessor, MuPdfOcrJobProcessor>();
}
else
{
    // Deterministic no-op so the job lifecycle is testable without the native engine.
    builder.Services.AddSingleton<IOcrJobProcessor, NoopOcrJobProcessor>();
}
builder.Services.AddSingleton<OcrJobWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OcrJobWorker>());
builder.Services.AddScoped<OcrJobsService>();

// Controllers + OpenAPI
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "PageForge API",
        Version = "v1",
        Description = "PageForge document platform API (AGPL-3.0-only)"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Middleware
app.UseMiddleware<ErrorHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Apply EF migrations at startup only when explicitly enabled (normally via
// `dotnet ef database update` or the hosted deploy pipeline). Off by default so
// tests and local `run` without a live PostgreSQL need no schema side effect.
if (bool.TryParse(builder.Configuration["Database:AutoMigrate"], out bool migrate) && migrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();

/// <summary>Entry-point type exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
