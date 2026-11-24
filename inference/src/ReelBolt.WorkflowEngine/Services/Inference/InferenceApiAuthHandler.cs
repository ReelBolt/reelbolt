using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services.Inference;

/// <summary>Name of the one <see cref="HttpClient"/> every engine → Inference API call must use.</summary>
public static class InferenceApiClient
{
    public const string Name = "InferenceApi";
}

/// <summary>The identity an execution acts as: its initiator, in the execution's organization.</summary>
public sealed record ExecutionIdentity(
    Guid UserId, string Email, Guid OrganizationId, OrganizationRole OrgRole, Guid? ApiKeyId = null);

/// <summary>
/// Resolves the identity an execution acts as, from the database: the initiator, the execution's own
/// organization and the initiator's CURRENT role in it. Null when the execution, user or membership is
/// gone (fail closed: no token is minted for a user who is no longer a member).
/// </summary>
public interface IExecutionUserResolver
{
    Task<ExecutionIdentity?> ResolveAsync(Guid executionId, CancellationToken ct);
}

public sealed class ExecutionUserResolver(IServiceScopeFactory scopes) : IExecutionUserResolver
{
    public async Task<ExecutionIdentity?> ResolveAsync(Guid executionId, CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        var execution = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId)
            .Select(e => new { e.InitiatedByUserId, e.OrganizationId, e.ApiKeyId })
            .FirstOrDefaultAsync(ct);
        if (execution?.InitiatedByUserId is not { } userId)
            return null;

        // S-M3/S-M5: a key-started run acts for its key, so the key must still be active. Once it is revoked
        // (or expired or gone) no on-behalf-of token is minted: the engine cannot keep working under a key
        // the workspace withdrew. The key id also rides on the token so the Inference API can enforce the
        // key's project list on the callback.
        if (execution.ApiKeyId is { } apiKeyId)
        {
            DateTime now = DateTime.UtcNow;
            var key = await db.ApiKeys.AsNoTracking()
                .Where(k => k.Id == apiKeyId && k.RevokedAt == null
                    && (k.ExpiresAt == null || k.ExpiresAt > now))
                .Select(k => new { k.OrganizationId })
                .FirstOrDefaultAsync(ct);
            // M7/M8: the org delete is a soft delete (deleted_at set by the Go API), so the key row can
            // outlive the workspace. No token is minted once the org is gone.
            bool organizationDeleted = key is not null && await db.Organizations.AsNoTracking()
                .AnyAsync(o => o.Id == key.OrganizationId && o.DeletedAt != null, ct);
            if (key is null || organizationDeleted || key.OrganizationId != execution.OrganizationId)
                return null;
        }

        string? email = await db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (email is null)
            return null;
        OrganizationRole? role = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == execution.OrganizationId && m.UserId == userId)
            .Select(m => (OrganizationRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        return role is null
            ? null
            : new ExecutionIdentity(userId, email, execution.OrganizationId, role.Value, execution.ApiKeyId);
    }
}

/// <summary>
/// Attaches a short-lived on-behalf-of bearer (the execution initiator's own identity) to every
/// Inference API call. With no ambient execution or initiator it sends NO token — the call fails
/// 401 as before — and logs a warning; it never falls back to an admin/service identity.
/// </summary>
public sealed class InferenceApiAuthHandler(
    IWorkflowExecutionContextAccessor accessor,
    IExecutionUserResolver users,
    OnBehalfOfTokenIssuer issuer,
    ILogger<InferenceApiAuthHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        WorkflowExecutionContext? context = accessor.Current;
        if (context is null)
        {
            logger.LogWarning("Inference API call {Path} has no workflow execution context; sending no token.", request.RequestUri?.AbsolutePath);
        }
        else
        {
            try
            {
                var user = await users.ResolveAsync(context.ExecutionId, ct);
                if (user is null)
                    logger.LogWarning("Execution {ExecutionId} has no resolvable initiator, the initiator is no longer a member of its organization, or the API key that started it is no longer active; sending no token.", context.ExecutionId);
                else
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer", issuer.Issue(user.UserId, user.Email, user.OrganizationId, user.OrgRole,
                            context.ExecutionId, apiKeyId: user.ApiKeyId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not mint on-behalf-of token for execution {ExecutionId}; sending no token.", context.ExecutionId);
            }
        }
        return await base.SendAsync(request, ct);
    }
}
