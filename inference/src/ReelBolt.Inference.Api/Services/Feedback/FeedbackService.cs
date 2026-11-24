using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;

namespace ReelBolt.Inference.Api.Services.Feedback;

/// <summary>Why a submission was refused, or that it was stored.</summary>
public enum FeedbackOutcome
{
    Created,
    InvalidKind,
    InvalidMessage,
    InvalidContext,
    ProjectNotFound,
    ExecutionNotFound,
    RateLimited
}

/// <summary>The result of one submission: the stored row's id and time, or the refusal.</summary>
public sealed record FeedbackSubmitResult(
    FeedbackOutcome Outcome, Guid? ReportId = null, DateTime? CreatedAt = null, string? Message = null);

/// <summary>
/// Validates and stores one in-product report (T-019). Constructed by the controller from the
/// request's own scoped services rather than registered in DI, so the composition root is untouched.
///
/// <para><b>Minimisation.</b> The stored <c>context_json</c> is built from the request and nothing
/// else: a field the client did not send is absent from the row, and no server-side fact (the caller's
/// id, the active project, the assistant thread) is ever folded in. The client shows the user exactly
/// what it will attach; this is where that promise is kept.</para>
///
/// <para><b>Tenancy.</b> A project id in the context is resolved through <see cref="IProjectAccessService"/>
/// and any non-Found outcome is a <c>404</c> — never a <c>403</c>, which would confirm the project
/// exists to a user with no right to know that. An execution id is looked up and its own project
/// checked the same way; when both are attached they must name the same project.</para>
///
/// <para><b>Rate limit.</b> A per-user sliding window counted off the report rows themselves (no
/// separate limiter table): a user may submit <see cref="MaxPerWindow"/> reports per
/// <see cref="Window"/>. A refused submission stores nothing and counts nothing.</para>
/// </summary>
public sealed class FeedbackService(InferenceApiDbContext db, IProjectAccessService projectAccess)
{
    /// <summary>At most this many characters in a message.</summary>
    public const int MaxMessageLength = 4000;

    /// <summary>At most this many assistant turns in an attached context.</summary>
    public const int MaxTurns = 20;

    /// <summary>At most this many characters in one attached turn.</summary>
    public const int MaxTurnLength = 2000;

    /// <summary>At most this many characters in the attached page path.</summary>
    public const int MaxPageLength = 300;

    /// <summary>Largest opt-in diagnostics object, in raw UTF-8 bytes.</summary>
    public const int MaxDiagnosticsBytes = 16384;

    /// <summary>Deepest nesting the diagnostics object may have (the root object is depth 1).</summary>
    public const int MaxDiagnosticsDepth = 8;

    /// <summary>Reports one user may submit per <see cref="Window"/>.</summary>
    public const int MaxPerWindow = 10;

    /// <summary>The rate-limit window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private static readonly string[] AllowedRoles = ["user", "assistant"];

    public async Task<FeedbackSubmitResult> SubmitAsync(
        Guid userId, Guid organizationId, FeedbackRequest request, CancellationToken ct)
    {
        string? kind = NormaliseKind(request.Kind);
        if (kind is null)
        {
            return new FeedbackSubmitResult(FeedbackOutcome.InvalidKind);
        }

        string message = (request.Message ?? string.Empty).Trim();
        if (message.Length == 0 || message.Length > MaxMessageLength)
        {
            return new FeedbackSubmitResult(FeedbackOutcome.InvalidMessage);
        }

        FeedbackContextDto context = request.Context ?? new FeedbackContextDto(null, null, null, null);

        // Resolve the attached project first: a foreign one is a 404 and nothing is stored.
        Guid? projectId = context.ProjectId is { } requested && requested != Guid.Empty ? requested : null;
        if (projectId is { } pid &&
            !(await projectAccess.GetAsync(pid, ProjectPermission.View, ct)).IsFound)
        {
            return new FeedbackSubmitResult(FeedbackOutcome.ProjectNotFound);
        }

        // An attached execution must be one the caller can see: it is looked up, and its own project
        // is checked through the same service. A foreign execution is refused, never silently dropped.
        Guid? executionId = context.ExecutionId is { } requestedExecution && requestedExecution != Guid.Empty
            ? requestedExecution
            : null;
        if (executionId is { } eid)
        {
            Guid? executionProject = await db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.Id == eid)
                .Select(e => (Guid?)e.ProjectId)
                .FirstOrDefaultAsync(ct);
            if (executionProject is null ||
                (projectId is { } attached && attached != executionProject) ||
                !(await projectAccess.GetAsync(executionProject.Value, ProjectPermission.View, ct)).IsFound)
            {
                return new FeedbackSubmitResult(FeedbackOutcome.ExecutionNotFound);
            }
        }

        if (!TryBuildContextJson(context, out string? contextJson, out string? contextError))
        {
            return new FeedbackSubmitResult(FeedbackOutcome.InvalidContext, Message: contextError);
        }

        DateTime now = DateTime.UtcNow;
        DateTime windowStart = now - Window;
        int recent = await db.FeedbackReports.CountAsync(r => r.UserId == userId && r.CreatedAt >= windowStart, ct);
        if (recent >= MaxPerWindow)
        {
            return new FeedbackSubmitResult(
                FeedbackOutcome.RateLimited,
                Message: $"You can send at most {MaxPerWindow} reports per hour. Try again later.");
        }

        var report = new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = organizationId,
            Kind = kind,
            Message = message,
            ContextJson = contextJson,
            CreatedAt = now
        };

        db.FeedbackReports.Add(report);
        await db.SaveChangesAsync(ct);

        return new FeedbackSubmitResult(FeedbackOutcome.Created, report.Id, report.CreatedAt);
    }

    /// <summary>The stored kind for a client value, or null when it is not one of the two.</summary>
    public static string? NormaliseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        FeedbackReport.BugKind => FeedbackReport.BugKind,
        FeedbackReport.FeedbackKind => FeedbackReport.FeedbackKind,
        _ => null
    };

    /// <summary>
    /// Serialises the attached context — only the fields that are present and in bounds. Returns false
    /// with a reason when a field is out of bounds; the caller answers 400 and stores nothing.
    /// </summary>
    private static bool TryBuildContextJson(FeedbackContextDto context, out string json, out string? error)
    {
        json = "{}";
        error = null;

        string? page = context.Page?.Trim();
        if (page is { Length: > MaxPageLength })
        {
            error = $"page is at most {MaxPageLength} characters.";
            return false;
        }

        JsonArray? turns = null;
        if (context.AssistantTurns is { Count: > 0 } attached)
        {
            if (attached.Count > MaxTurns)
            {
                error = $"at most {MaxTurns} assistant turns can be attached.";
                return false;
            }

            turns = [];
            foreach (FeedbackTurnDto turn in attached)
            {
                string? role = turn.Role?.Trim().ToLowerInvariant();
                if (role is null || !AllowedRoles.Contains(role))
                {
                    error = "an attached turn's role must be user or assistant.";
                    return false;
                }

                string content = turn.Content ?? string.Empty;
                if (content.Length > MaxTurnLength)
                {
                    error = $"an attached turn is at most {MaxTurnLength} characters.";
                    return false;
                }

                turns.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
        }

        // Diagnostics are best-effort: a bad payload is dropped with a reason and never costs the report.
        JsonNode? diagnostics = null;
        string? diagnosticsDropped = null;
        if (context.Diagnostics is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } diag)
        {
            string raw = diag.GetRawText();
            if (diag.ValueKind != JsonValueKind.Object)
            {
                diagnosticsDropped = "not_object";
            }
            else if (Encoding.UTF8.GetByteCount(raw) > MaxDiagnosticsBytes)
            {
                diagnosticsDropped = "too_large";
            }
            else if (ExceedsDepth(diag, MaxDiagnosticsDepth))
            {
                diagnosticsDropped = "too_deep";
            }
            else
            {
                try
                {
                    // Duplicate keys or any parse failure soft-drops. The stored form (escaped, compact)
                    // is what is measured, since it can be larger than the raw text.
                    diagnostics = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
                    if (diagnostics is null)
                    {
                        diagnosticsDropped = "invalid";
                    }
                    else if (Encoding.UTF8.GetByteCount(diagnostics.ToJsonString()) > MaxDiagnosticsBytes)
                    {
                        diagnostics = null;
                        diagnosticsDropped = "too_large";
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                {
                    diagnostics = null;
                    diagnosticsDropped = "invalid";
                }
            }
        }

        var node = new JsonObject();
        if (!string.IsNullOrEmpty(page))
        {
            node["page"] = page;
        }
        if (context.ProjectId is { } projectId && projectId != Guid.Empty)
        {
            node["projectId"] = projectId.ToString();
        }
        if (context.ExecutionId is { } executionId && executionId != Guid.Empty)
        {
            node["executionId"] = executionId.ToString();
        }
        if (turns is not null)
        {
            node["assistantTurns"] = turns;
        }

        if (diagnostics is not null)
        {
            node["diagnostics"] = diagnostics;
        }
        if (diagnosticsDropped is not null)
        {
            node["diagnosticsDropped"] = diagnosticsDropped;
        }

        json = node.ToJsonString();
        return true;
    }

    /// <summary>
    /// True when the value nests deeper than <paramref name="limit"/> (root = 1). Iterative and stops as
    /// soon as the limit is passed, so a hostile payload cannot drive deep recursion.
    /// </summary>
    private static bool ExceedsDepth(JsonElement root, int limit)
    {
        var stack = new Stack<(JsonElement Element, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            (JsonElement element, int depth) = stack.Pop();
            if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                continue;
            }
            if (depth > limit)
            {
                return true;
            }
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in element.EnumerateObject())
                {
                    stack.Push((p.Value, depth + 1));
                }
            }
            else
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    stack.Push((item, depth + 1));
                }
            }
        }
        return false;
    }
}
