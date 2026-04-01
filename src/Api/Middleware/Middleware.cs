using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RideSharing.Application.Services;

namespace RideSharing.Api.Middleware;

/// <summary>
/// Global exception handler middleware — catches all unhandled exceptions
/// and returns appropriate HTTP responses with error details.
/// 
/// Ordering: Must be FIRST in the middleware pipeline (earliest registered).
/// app.UseMiddleware<GlobalExceptionHandler>();
/// </summary>
public class GlobalExceptionHandler
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(RequestDelegate next, ILogger<GlobalExceptionHandler> logger)
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
            await HandleExceptionAsync(context, ex, _logger);
        }
    }

    private static Task HandleExceptionAsync(HttpContext context, Exception exception, ILogger logger)
    {
        logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        var response = context.Response;
        response.ContentType = "application/json";

        var errorResponse = new ErrorResponse
        {
            Message = exception.Message,
            TraceId = context.TraceIdentifier,
            Type = exception.GetType().Name
        };

        // Map known exception types to HTTP status codes
        response.StatusCode = exception switch
        {
            // 400 — Bad Request
            ArgumentNullException => (int)HttpStatusCode.BadRequest,
            ArgumentException => (int)HttpStatusCode.BadRequest,
            ValidationException => (int)HttpStatusCode.BadRequest,

            // 401 — Unauthorized
            UnauthorizedAccessException => (int)HttpStatusCode.Unauthorized,

            // 404 — Not Found
            KeyNotFoundException => (int)HttpStatusCode.NotFound,

            // 503 — Service Unavailable (before generic InvalidOperationException)
            InvalidOperationException ex when ex.Message.Contains("unavailable") => 
                (int)HttpStatusCode.ServiceUnavailable,

            // 409 — Conflict
            InvalidOperationException => (int)HttpStatusCode.Conflict,

            // 500 — Internal Server Error (default)
            _ => (int)HttpStatusCode.InternalServerError
        };

        if (exception is RideNotFoundException rideEx)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            errorResponse.Message = rideEx.Message;
        }

        if (exception is InvalidRideTransitionException transEx)
        {
            response.StatusCode = (int)HttpStatusCode.Conflict;
            errorResponse.Message = transEx.Message;
        }

        return response.WriteAsJsonAsync(errorResponse);
    }
}

/// <summary>
/// Request and response logging middleware — logs HTTP method, path, status code, and duration.
/// Useful for debugging and monitoring.
/// 
/// Ordering: Should be after authentication but before business logic handlers.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestStartTime = DateTime.UtcNow;
        var userId = context.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "anonymous";

        _logger.LogInformation(
            "→ {Method} {Path} from {RemoteIP} (user: {UserId})",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress,
            userId);

        try
        {
            await _next(context);

            var duration = DateTime.UtcNow - requestStartTime;
            _logger.LogInformation(
                "← {Method} {Path} {StatusCode} ({DurationMs}ms)",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                duration.TotalMilliseconds);
        }
        catch
        {
            var duration = DateTime.UtcNow - requestStartTime;
            _logger.LogError(
                "← {Method} {Path} ERROR ({DurationMs}ms)",
                context.Request.Method,
                context.Request.Path,
                duration.TotalMilliseconds);
            throw;
        }
    }
}

/// <summary>
/// Standard error response format — returned by GlobalExceptionHandler.
/// </summary>
public class ErrorResponse
{
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>
/// Custom validation exception — thrown by validation logic.
/// Produces 400 Bad Request.
/// </summary>
public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}
