using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Middleware;

/// <summary>
/// Records <c>billing.credits_exhausted</c> (once per user) for every MVC <see cref="ObjectResult"/> with
/// status 402 whose body carries <c>code: "INSUFFICIENT_CREDITS"</c> (workflow run gate, composition
/// revisions, picture/graphic/design charges). The in-chat ExecuteWorkflow preflight refusal is not an HTTP
/// result and is emitted separately by <c>AssistantToolProvider</c>. Other tool-level refusals (for example
/// an MCP tool hold surfaced as tool output) are NOT covered. The same filter also appends
/// <c>billing.plan_gate_hit</c> (detail = the closed feature token) for every result body carrying
/// <c>code: "PLAN_NOT_ENTITLED"</c>, the one place every controller's plan refusal passes through.
/// Best-effort: the response is never altered.
/// </summary>
public sealed class CreditsExhaustedResultFilter(IProductEventEmitter events, ICurrentUser currentUser) : IAsyncResultFilter
{
    private const string Code = "INSUFFICIENT_CREDITS";
    private const string NotEntitledCode = "PLAN_NOT_ENTITLED";

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { StatusCode: StatusCodes.Status402PaymentRequired } result
            && IsInsufficientCredits(result.Value)
            && currentUser.IsAuthenticated)
        {
            await events.FirstAsync(
                currentUser.UserId, currentUser.OrganizationId, ProductEventNames.CreditsExhausted,
                context.HttpContext.RequestAborted);
        }

        // Plan refusals are 4xx (402/413/429): a success result's body is never touched or serialized.
        if (context.Result is ObjectResult { StatusCode: >= 400 } planResult
            && currentUser.IsAuthenticated
            && PlanGateFeature(planResult.Value) is string feature)
        {
            await events.AppendAsync(
                currentUser.UserId, currentUser.OrganizationId, ProductEventNames.PlanGateHit,
                ProductEventNames.ClosedToken(feature), context.HttpContext.RequestAborted);
        }

        await next();
    }

    /// <summary>The <c>feature</c> of a PLAN_NOT_ENTITLED body (empty when absent), or null for any other body.</summary>
    public static string? PlanGateFeature(object? value)
    {
        if (value is null) return null;
        try
        {
            using JsonDocument doc = JsonSerializer.SerializeToDocument(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("code", out JsonElement code)
                || code.ValueKind != JsonValueKind.String
                || code.GetString() != NotEntitledCode)
            {
                return null;
            }

            return doc.RootElement.TryGetProperty("feature", out JsonElement f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether a 402 body carries <c>code: "INSUFFICIENT_CREDITS"</c> (any body shape).</summary>
    public static bool IsInsufficientCredits(object? value)
    {
        if (value is null) return false;
        try
        {
            using JsonDocument doc = JsonSerializer.SerializeToDocument(value);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == Code;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
