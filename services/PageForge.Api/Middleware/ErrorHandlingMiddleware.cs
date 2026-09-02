// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Text.Json;
using PageForge.Api.Models;

namespace PageForge.Api.Middleware;

public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            await HandleErrorAsync(context, ex);
        }
    }

    private static async Task HandleErrorAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        (HttpStatusCode statusCode, string code, string message) = exception switch
        {
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "UNAUTHORIZED", "Authentication required."),
            KeyNotFoundException => (HttpStatusCode.NotFound, "NOT_FOUND", "The requested resource was not found."),
            InvalidOperationException => (HttpStatusCode.Conflict, "CONFLICT", exception.Message),
            ArgumentException => (HttpStatusCode.BadRequest, "BAD_REQUEST", exception.Message),
            _ => (HttpStatusCode.InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.")
        };

        context.Response.StatusCode = (int)statusCode;

        var response = new ErrorResponse
        {
            Error = new ErrorDetail
            {
                Code = code,
                Message = message,
                TraceId = context.TraceIdentifier
            }
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
    }
}
