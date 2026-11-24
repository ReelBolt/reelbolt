using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.PublicApi;

namespace ReelBolt.Inference.Api.Middleware;

/// <summary>
/// <c>PublicApi:Enabled</c> off means the whole public prefix answers 404 with an empty body, before
/// authentication (a disabled surface must not even reveal that its keys are checked). Every public response
/// also carries <c>X-Request-Id</c>, including that 404.
/// </summary>
public sealed class PublicApiFlagMiddleware(RequestDelegate next)
{
    public const string Prefix = "/api/public";

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<PublicApiOptions> options)
    {
        if (!context.Request.Path.StartsWithSegments(Prefix))
        {
            await next(context);
            return;
        }

        context.Response.Headers[PublicApiRequestContext.HeaderName] = PublicApiRequestContext.RequestId(context);
        if (!options.CurrentValue.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}

public static class PublicApiFlagMiddlewareExtensions
{
    /// <summary>Register before <c>UseAuthentication</c>.</summary>
    public static IApplicationBuilder UsePublicApiFlag(this IApplicationBuilder app) =>
        app.UseMiddleware<PublicApiFlagMiddleware>();
}
