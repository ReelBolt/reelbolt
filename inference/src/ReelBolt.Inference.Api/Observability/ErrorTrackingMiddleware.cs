using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace ReelBolt.Inference.Api.Observability;

/// <summary>
/// Records an unhandled request exception as an error signal (alpha-launch T-018), then lets the
/// request fail exactly as it did before: the exception is rethrown, so the response the caller sees
/// is unchanged and no user's error is hidden or replaced. Reporting only.
/// </summary>
public sealed class ErrorTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _environment;

    public ErrorTrackingMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _environment = FirstNonBlank(
            configuration["OTEL_DEPLOYMENT_ENVIRONMENT"],
            configuration["ASPNETCORE_ENVIRONMENT"],
            configuration["DOTNET_ENVIRONMENT"]) ?? "Production";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            ErrorTrackingDiagnostics.Record(exception, "unhandled", _environment);
            throw;
        }
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Registers <see cref="ErrorTrackingMiddleware"/>.</summary>
public static class ErrorTrackingMiddlewareExtensions
{
    public static IApplicationBuilder UseErrorTracking(this IApplicationBuilder app)
        => app.UseMiddleware<ErrorTrackingMiddleware>();
}
