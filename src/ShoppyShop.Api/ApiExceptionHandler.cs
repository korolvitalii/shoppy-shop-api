using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using ShoppyShop.Application;

namespace ShoppyShop.Api;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    private static readonly Action<ILogger, string, string, Exception?> LogUnhandledException =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1, nameof(LogUnhandledException)),
            "Unhandled exception for {Method} {Path}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            AppValidationException => (StatusCodes.Status400BadRequest, "Request validation failed"),
            AppUnauthorizedException => (StatusCodes.Status401Unauthorized, "Authentication failed"),
            AppNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            AppConflictException => (StatusCodes.Status409Conflict, "Request conflict"),
            AppUnprocessableException => (StatusCodes.Status422UnprocessableEntity, "Request could not be processed"),
            // 503 is the textbook status for a feature switched off, but every 5xx here is treated
            // as a fault: logged with a stack trace, and its Detail stripped before it ships. The
            // client enforces the same rule (app-error.ts deliberately never surfaces a 5xx detail),
            // so a 503 would reach the user as generic, retryable server-error copy. A disabled
            // feature is neither a fault nor worth retrying, so it stays in the 4xx range where the
            // explanation survives; the distinct title keeps it separable in logs and monitoring.
            AppServiceUnavailableException => (StatusCodes.Status422UnprocessableEntity, "Feature unavailable"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
        };

        if (status >= 500)
        {
            LogUnhandledException(logger, httpContext.Request.Method, httpContext.Request.Path, exception);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= 500 ? null : exception.Message,
            Instance = httpContext.Request.Path,
        };
        if (exception is AppValidationException validation && validation.Errors.Count > 0)
        {
            problem.Extensions["errors"] = validation.Errors;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}