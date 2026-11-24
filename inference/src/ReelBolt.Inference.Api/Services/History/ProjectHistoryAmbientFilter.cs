using Microsoft.AspNetCore.Mvc.Filters;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Publishes the request-scoped <see cref="IProjectHistoryService"/> (and the project authority
/// checker) on the async-local scope <see cref="ProjectHistoryAutoCapture"/> reads.
/// </summary>
/// <remarks>
/// The run-start gate is instantiated by its controllers and the assistant tool interceptor by the
/// tool provider, neither of which takes a history dependency, so an MVC action filter is the one
/// place that can hand both the same request-scoped service without a per-call-site constructor
/// change. The scope is disposed when the action completes; a tool loop that runs inside the action
/// inherits it on its async flow.
/// </remarks>
internal sealed class ProjectHistoryAmbientFilter(
    IProjectHistoryService history,
    IProjectAccessService projectAccess,
    ICurrentUser currentUser) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The filter is global, so it also runs for anonymous routes (/api/v1/health, output and review
        // links, marketing). There is no user and nothing to capture there; reading UserId would throw
        // and turn the container healthcheck into a 500, so the release never reached the engine.
        if (!currentUser.IsAuthenticated)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // The chat stream names the page's project in its body (AssistantChatRequest.ProjectId).
        Guid? pageProjectId = null;
        foreach (object? argument in context.ActionArguments.Values)
        {
            if (argument?.GetType().GetProperty("ProjectId")?.GetValue(argument) is Guid id && id != Guid.Empty)
            {
                pageProjectId = id;
                break;
            }
        }

        using IDisposable scope = ProjectHistoryAutoCapture.Use(history, projectAccess, pageProjectId, currentUser.UserId);
        await next().ConfigureAwait(false);
    }
}
