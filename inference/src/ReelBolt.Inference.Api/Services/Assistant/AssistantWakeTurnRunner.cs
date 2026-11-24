using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Runs a wake turn as a NORMAL assistant turn: the real <see cref="AssistantChatController.ChatStream"/> in a fresh DI
/// scope, as a synthetic principal built from the database (current role, current admin flag), with no browser on the
/// other end (the response goes to a discarding stream). The reply and its persistence go through the same turn hub,
/// so the web re-attach works unchanged. It follows the user's own approval setting; nothing here approves anything.
/// </summary>
public sealed class AssistantWakeTurnRunner(IServiceScopeFactory scopes) : IAssistantWakeTurnRunner
{
    public async Task<WakeOutcome> RunAsync(
        AssistantRunWatch watch, WakeIdentity identity, string wakeText, AssistantWakeRun run, CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", identity.UserId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, identity.UserId.ToString()),
            new Claim(ClaimTypes.Email, identity.Email),
            new Claim(AuthClaims.Organization, identity.OrganizationId.ToString()),
            new Claim(AuthClaims.OrgRole, identity.Role.ToString()),
            new Claim(AuthClaims.PlatformAdmin, identity.IsPlatformAdmin ? "true" : "false"),
            new Claim(AuthClaims.IsAdminAlias, identity.IsPlatformAdmin ? "true" : "false"),
            new Claim(AuthClaims.EmailVerified, identity.EmailVerified ? "true" : "false")
        ], "RunWake"));

        var http = new DefaultHttpContext { User = principal, RequestServices = sp, RequestAborted = ct };
        http.Response.Body = Stream.Null;
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        // The project must still be viewable at the user's CURRENT role.
        IProjectAccessService access = sp.GetRequiredService<IProjectAccessService>();
        ProjectAccessResult project = await access.GetAsync(watch.ProjectId, ProjectPermission.View, ct);
        if (!project.IsFound)
            return WakeOutcome.Cancelled("project_gone");

        AssistantChatController controller = ActivatorUtilities.CreateInstance<AssistantChatController>(sp);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };

        var request = new AssistantChatRequest(watch.ThreadId, wakeText, watch.ProjectId)
        {
            Wake = new AssistantWakeInfo(run.ExecutionId, AssistantRunWakeText.Word(run.Status), run.RunLabel)
        };

        await controller.ChatStream(request, ct);

        int status = http.Response.StatusCode;
        if (status == StatusCodes.Status404NotFound)
            return WakeOutcome.Cancelled("thread_gone");
        if (status != StatusCodes.Status200OK)
            return WakeOutcome.Retry(status == StatusCodes.Status429TooManyRequests ? "rate_limited" : "turn_refused");

        // Fired only when the thread actually holds an answer after the wake message.
        InferenceApiDbContext db = sp.GetRequiredService<InferenceApiDbContext>();
        using IDisposable system = sp.GetService<ITenantContext>().System();
        // Matched by parsed content, not text: jsonb does not keep the text it was given.
        var candidates = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.ThreadId == watch.ThreadId && m.Role == "user" && m.MetadataJson != null)
            .OrderByDescending(m => m.Sequence)
            .Select(m => new { m.Id, m.Sequence, m.MetadataJson })
            .Take(20)
            .ToListAsync(ct);
        var wake = candidates.FirstOrDefault(m => request.Wake!.Matches(m.MetadataJson));
        if (wake is null)
            return WakeOutcome.Retry("no_wake_message");
        bool answered = await db.AssistantMessages.AsNoTracking()
            .AnyAsync(m => m.ThreadId == watch.ThreadId && m.Role == "assistant" && m.Sequence > wake.Sequence, ct);
        return answered ? WakeOutcome.Fired(wake.Id) : WakeOutcome.Retry("no_reply");
    }
}
