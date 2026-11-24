using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>
/// The surfaces whose call may say <c>allowMediaEgress: true</c> for one call (docs/design/avatar-objects.md, the
/// defaults under the decisions table). <b>APPEND ONLY</b>: the value names the surface's own route in
/// <see cref="MediaEgressCallPermission.IsOwnRoute"/>.
/// </summary>
public enum MediaEgressSurface
{
    /// <summary><c>POST .../image-documents/{id}/operations</c> with <c>subjectIds</c> (the picture editor), and the assistant's picture tool.</summary>
    PictureOperation,

    /// <summary><c>POST .../subject-clips</c> and its <c>/estimate</c> (the editor's "Generate clip"), and <c>GenerateSubjectClip</c>.</summary>
    SubjectClip,

    /// <summary><c>POST .../avatars/{id}/variants</c> (a variant made by the picture model), and <c>CreateAvatarVariant</c>. Appended.</summary>
    AvatarVariant,

    /// <summary><c>POST .../avatars/extract-object</c> (an object pulled out of a video), and <c>ExtractObjectFromVideo</c>. Appended.</summary>
    ObjectExtraction,

    /// <summary>
    /// The assistant's and MCP's <c>ExecuteWorkflow</c> with <c>brief.inputs.allowMediaEgress</c> (docs/design/app-inputs.md
    /// 30). Appended. It has no route of its own here: the app's own run start reads <see cref="MediaEgressCallers.AppSession"/>
    /// in the run gate, so a request is never granted through this surface.
    /// </summary>
    WorkflowRun,

    /// <summary>
    /// The assistant's and MCP's workflow edits (<c>ApplyWorkflow</c>, <c>ApplyPatchWorkflow</c>) that switch a step's
    /// <c>allowSourceMediaEgress</c> from off to on. Appended. Like <see cref="WorkflowRun"/> it has no route: only the
    /// chat card the person allowed grants it; the MCP setting never does (it allows one run at a time).
    /// </summary>
    WorkflowEdit
}

/// <summary>How a call reached the surface.</summary>
public enum MediaEgressVia
{
    /// <summary>An HTTP request; it counts only on the surface's own route, and the principal decides who may give it.</summary>
    Request,

    /// <summary>A tool the platform assistant runs in a chat turn: only an approval card that said so, shown and allowed.</summary>
    AssistantChat,

    /// <summary>A tool called over MCP: only while the user's own "let connected tools allow sending my media" is on.</summary>
    Mcp
}

/// <summary>
/// One call that says <c>allowMediaEgress: true</c>: where it was made, how it came in, for whom and for which project.
/// </summary>
/// <param name="Surface">The surface the word was sent on.</param>
/// <param name="Via">How the call came in.</param>
/// <param name="UserId">The account the call acts for (the transport's, never a model's argument).</param>
/// <param name="ProjectId">The project whose media would be sent.</param>
/// <param name="HttpContext">The request being served (<see cref="MediaEgressVia.Request"/> only); null when there is none.</param>
/// <param name="ChatApproved">The tool runs in the in-app chat (<see cref="AssistantToolContext.ChatApproved"/>), <see cref="MediaEgressVia.AssistantChat"/> only.</param>
public sealed record MediaEgressCall(
    MediaEgressSurface Surface,
    MediaEgressVia Via,
    Guid UserId,
    Guid ProjectId,
    HttpContext? HttpContext = null,
    bool ChatApproved = false)
{
    /// <summary>The call of an HTTP request to a surface's route.</summary>
    public static MediaEgressCall FromRequest(MediaEgressSurface surface, Guid userId, Guid projectId, HttpContext? httpContext) =>
        new(surface, MediaEgressVia.Request, userId, projectId, httpContext);

    /// <summary>
    /// The call of an assistant or MCP tool: over MCP when the context says so (provenance), else the chat, where only
    /// a context the chat controller built (<see cref="AssistantToolContext.ChatApproved"/>) can count.
    /// </summary>
    public static MediaEgressCall FromTool(MediaEgressSurface surface, AssistantToolContext context, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new MediaEgressCall(
            surface,
            context.Initiator == ExecutionInitiator.Mcp ? MediaEgressVia.Mcp : MediaEgressVia.AssistantChat,
            context.UserId,
            projectId,
            null,
            context.ChatApproved);
    }
}

/// <summary>
/// THE ONE rule for "this one call may send the project's avatar and object media (or a run's media) to an AI
/// provider" (docs/design/avatar-objects.md: automated callers may allow sending media for one call, safely; the rule
/// is the one of the app field <c>allowMediaEgress</c>, docs/design/app-inputs.md 30.6). Every surface asks it: the
/// picture editor's operations, the editor's subject clips, a variant made by the picture model, an object pulled out
/// of a video, and the assistant and MCP tools of each, plus the assistant's and MCP's <c>ExecuteWorkflow</c>.
/// <para>
/// It is asked only for a call that says <c>allowMediaEgress: true</c> while the project's own consent
/// (<c>projects.subject_media_consent_at</c>) is not recorded; the value comes from the call, a grantor gives the
/// ability, never the value. It answers for ONE call: it never records the project's consent and never switches a
/// stored step flag on. Anything it cannot place is refused (fail closed).
/// </para>
/// </summary>
public interface IMediaEgressCallPermission
{
    /// <summary>
    /// Who gives the permission for <paramref name="call"/> (<see cref="MediaEgressPermission.ByApp"/>,
    /// <see cref="MediaEgressPermission.ByApiKey"/>, <see cref="MediaEgressPermission.ByMcp"/> or
    /// <see cref="MediaEgressPermission.ByAssistantCard"/>), or null when nobody may.
    /// </summary>
    ValueTask<string?> GrantorAsync(MediaEgressCall call, CancellationToken ct);
}

/// <summary>Helpers over <see cref="IMediaEgressCallPermission"/>.</summary>
public static class MediaEgressCallPermissionExtensions
{
    /// <summary>Whether somebody may give the permission for <paramref name="call"/>.</summary>
    public static async ValueTask<bool> MayAllowAsync(this IMediaEgressCallPermission permission, MediaEgressCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return await permission.GrantorAsync(call, ct) is not null;
    }
}

/// <summary>
/// <see cref="IMediaEgressCallPermission"/>, the rule of docs/design/app-inputs.md 30.6 for every caller kind:
/// <list type="bullet">
/// <item><b>The app session</b> (<see cref="MediaEgressCallers.AppSession"/>: authenticated, no API key, no
/// <c>scope</c> claim, no engine <c>act</c> claim) on the surface's OWN route. The assistant runs the picture editor's
/// controller inside its chat request, so a session token alone never counts: the route must be the surface's.</item>
/// <item><b>A public API key</b> on the surface's own route, only with the scope <c>media.egress</c>, read from the
/// key's own row on this call (a key that is not found holds none).</item>
/// <item><b>MCP</b> (stdio and OAuth alike), only while the user's <c>application_users.mcp_media_egress_enabled</c> is
/// on, read from the database on every call (a missing row is off).</item>
/// <item><b>The assistant chat</b>, only under an approval card that stated the media is sent, shown and allowed
/// (<see cref="AssistantMediaEgressApproval"/>, set by the gate; never in auto mode), in a context the chat controller
/// built.</item>
/// <item><b>Anyone else</b>: an OAuth token on a route, the engine acting on someone's behalf, an unauthenticated call,
/// a call with no request at all (a background worker), the workflow-assistant route (no card), and a call of a kind
/// this rule does not know.</item>
/// </list>
/// </summary>
public sealed class MediaEgressCallPermission : IMediaEgressCallPermission
{
    private readonly InferenceApiDbContext? _db;

    /// <summary>The rule over the request's database (the MCP switch and an API key's scopes are read from it).</summary>
    public MediaEgressCallPermission(InferenceApiDbContext db)
    {
        _db = db;
    }

    /// <summary>The rule with no database: an MCP call and an API key are refused (fail closed); the rest is unchanged.</summary>
    internal MediaEgressCallPermission()
    {
    }

    /// <inheritdoc />
    public async ValueTask<string?> GrantorAsync(MediaEgressCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Via switch
        {
            MediaEgressVia.AssistantChat =>
                call.ChatApproved && AssistantMediaEgressApproval.IsApproved ? MediaEgressPermission.ByAssistantCard : null,
            MediaEgressVia.Mcp => call.Surface != MediaEgressSurface.WorkflowEdit && await McpSwitchOnAsync(call.UserId, ct) ? MediaEgressPermission.ByMcp : null,
            MediaEgressVia.Request => await RequestGrantorAsync(call, ct),
            _ => null
        };
    }

    private async ValueTask<string?> RequestGrantorAsync(MediaEgressCall call, CancellationToken ct)
    {
        if (call.HttpContext is not { } http) return null;

        ClaimsPrincipal user = http.User;
        if (user.Identity?.IsAuthenticated != true) return null;

        // The engine acting for the run's initiator is automation, not a person and not a key.
        if (user.HasClaim(c => c.Type == OnBehalfOfTokenIssuer.ActorClaim)) return null;

        // Only the surface's own route: an in-process call from a chat or tool-call request never counts here.
        if (!IsOwnRoute(call.Surface, http.Request)) return null;

        if (McpScopeGuard.IsApiKey(user))
            return await ApiKeyHoldsMediaEgressAsync(user, ct) ? MediaEgressPermission.ByApiKey : null;

        // A scoped principal that is not a key (an MCP OAuth token) is not a session: AppSession answers null for it.
        return MediaEgressCallers.AppSession(user);
    }

    private async ValueTask<bool> McpSwitchOnAsync(Guid userId, CancellationToken ct)
    {
        if (_db is null || userId == Guid.Empty) return false;
        return await _db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.McpMediaEgressEnabled)
            .FirstOrDefaultAsync(ct) ?? false;
    }

    private async ValueTask<bool> ApiKeyHoldsMediaEgressAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (_db is null) return false;
        if (!Guid.TryParse(user.FindFirst(AuthClaims.ApiKeyId)?.Value, out Guid keyId) || keyId == Guid.Empty) return false;
        string[]? scopes = await _db.ApiKeys.AsNoTracking()
            .Where(k => k.Id == keyId)
            .Select(k => k.Scopes)
            .FirstOrDefaultAsync(ct);
        return scopes?.Contains(ApiScopes.MediaEgress, StringComparer.Ordinal) == true;
    }

    /// <summary>
    /// Whether the request being served is the surface's own route (method and path; a trailing slash and the letter case
    /// do not matter). The assistant's picture tool runs the editor's controller inside the chat (or MCP tool-call)
    /// request, whose path is not this one.
    /// </summary>
    internal static bool IsOwnRoute(MediaEgressSurface surface, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpMethods.IsPost(request.Method)) return false;

        string[] segments = (request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool project = segments.Length >= 5
                       && Is(segments[0], "api") && Is(segments[1], "v1") && Is(segments[2], "projects") && Guid.TryParse(segments[3], out _);
        if (!project) return false;

        return surface switch
        {
            // /api/v1/projects/{projectId}/image-documents/{documentId}/operations
            MediaEgressSurface.PictureOperation =>
                segments.Length == 7 && Is(segments[4], "image-documents") && Guid.TryParse(segments[5], out _) && Is(segments[6], "operations"),
            // /api/v1/projects/{projectId}/subject-clips and /api/v1/projects/{projectId}/subject-clips/estimate
            MediaEgressSurface.SubjectClip =>
                segments.Length is 5 or 6 && Is(segments[4], "subject-clips") && (segments.Length == 5 || Is(segments[5], "estimate")),
            // /api/v1/projects/{projectId}/avatars/{avatarId}/variants
            MediaEgressSurface.AvatarVariant =>
                segments.Length == 7 && Is(segments[4], "avatars") && Guid.TryParse(segments[5], out _) && Is(segments[6], "variants"),
            // /api/v1/projects/{projectId}/avatars/extract-object
            MediaEgressSurface.ObjectExtraction =>
                segments.Length == 6 && Is(segments[4], "avatars") && Is(segments[5], "extract-object"),
            // The run start reads its grantor in the run gate; nothing is granted to a request through this surface.
            MediaEgressSurface.WorkflowRun => false,
            MediaEgressSurface.WorkflowEdit => false,
            _ => false
        };
    }

    private static bool Is(string segment, string expected) => string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);
}
