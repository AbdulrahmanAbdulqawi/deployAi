using DeployAI.Core.Exceptions;
using System.Net;
using System.Text.Json;

namespace DeployAI.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
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
        catch (DeployAIException ex)
        {
            context.Response.StatusCode = ex.ErrorCode switch
            {
                "unauthorized" or "github_auth_expired" => (int)HttpStatusCode.Unauthorized,
                "project_not_found" or "not_found" => (int)HttpStatusCode.NotFound,
                "provider_token_invalid" or "invalid_credential" => (int)HttpStatusCode.BadRequest,
                "credential_in_use" => (int)HttpStatusCode.Conflict,
                _ => (int)HttpStatusCode.BadRequest
            };

            await WriteErrorAsync(context, ex.ErrorCode, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteErrorAsync(context, "provider_not_found", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await WriteErrorAsync(context, "internal_error", "Something went wrong on our side. Try again in a moment.");
        }
    }

    private static Task WriteErrorAsync(HttpContext context, string code, string message)
    {
        context.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new
        {
            error = new { code, message }
        });
        return context.Response.WriteAsync(payload);
    }
}
