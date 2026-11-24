using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using ReelBolt.Inference.Api.Controllers.PublicApi;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// The two answers the framework would otherwise give outside the public error envelope: the authentication
/// challenge of a request with no (or no usable) credential, and the 400 of a failed model binding. Both are
/// scoped to <c>/api/public/</c> routes and public controllers; every dashboard route keeps its own format.
/// </summary>
public static class PublicApiAuthResponses
{
    private const string PublicPrefix = "/api/public/";

    public static bool IsPublicPath(PathString path) => path.StartsWithSegments(PublicPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// JwtBearer's challenge: on a public route it answers 401 <c>api_key_required</c> in the envelope, with
    /// <c>WWW-Authenticate</c> and <c>X-Request-Id</c>. Any other route is left to the default (empty-body 401).
    /// </summary>
    public static async Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        if (!IsPublicPath(context.Request.Path)) return;

        context.HandleResponse();
        HttpResponse response = context.Response;
        response.Headers[PublicApiRequestContext.HeaderName] = PublicApiRequestContext.RequestId(context.HttpContext);
        response.Headers.WWWAuthenticate = "Bearer";
        await PublicApiEnvelope.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized,
            PublicApiErrorCodes.ApiKeyRequired, PublicApiErrorCodes.ApiKeyRequiredMessage);
    }

    /// <summary>
    /// The <c>InvalidModelStateResponseFactory</c>: a public controller gets <c>400 invalid_request</c> with
    /// <c>error.details</c> (field to messages); every other controller gets the framework's own
    /// <see cref="ValidationProblemDetails"/> exactly as before.
    /// </summary>
    public static IActionResult InvalidModelState(ActionContext context)
    {
        bool isPublic = context.ActionDescriptor is ControllerActionDescriptor descriptor
            && typeof(PublicApiControllerBase).IsAssignableFrom(descriptor.ControllerTypeInfo);
        if (!isPublic)
        {
            ProblemDetailsFactory factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
            ValidationProblemDetails problem = factory.CreateValidationProblemDetails(
                context.HttpContext, context.ModelState, statusCode: StatusCodes.Status400BadRequest);
            return new BadRequestObjectResult(problem)
            {
                ContentTypes = { "application/problem+json", "application/problem+xml" }
            };
        }

        Dictionary<string, string[]> details = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value!.Errors
                    .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "The value is not valid." : e.ErrorMessage)
                    .ToArray());
        return PublicApiEnvelope.Result(StatusCodes.Status400BadRequest, PublicApiErrorCodes.InvalidRequest,
            "The request is not valid. See error.details.", new { details });
    }
}
