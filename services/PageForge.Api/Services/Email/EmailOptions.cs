// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Services.Email;

/// <summary>
/// Strongly-typed email configuration read from the "Email" section of
/// appsettings.json / environment. Never logged or returned to clients.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Delivery backend: "none" (no-op/probe) or "smtp".</summary>
    public string Provider { get; set; } = "none";

    public string FromAddress { get; set; } = "no-reply@pageforge.test";
    public string FromName { get; set; } = "PageForge";

    // SMTP endpoint, only used when Provider == "smtp".
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>True when a delivery backend was explicitly selected.</summary>
    public bool IsConfigured => !string.Equals(Provider, "none", StringComparison.OrdinalIgnoreCase);
}