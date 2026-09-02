// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Models;

public sealed class ErrorResponse
{
    public required ErrorDetail Error { get; init; }
}

public sealed class ErrorDetail
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? TraceId { get; init; }
}
