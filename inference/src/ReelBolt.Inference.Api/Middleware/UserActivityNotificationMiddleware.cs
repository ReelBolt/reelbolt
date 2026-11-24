using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Middleware;

/// <summary>
/// Reports thumbnail-graphic edits to the notification pipeline (docs/notifications.md). It runs
/// AFTER the request: only a successful (2xx) mutating request to the image-document endpoints
/// fires, so a failed edit mails nothing and a read (GET) never does. Like every
/// notification, this is best-effort — a failure here is logged and never reaches the caller.
/// </summary>
public sealed class UserActivityNotificationMiddleware
{
    private const string RoutePrefix = "/api/v1/projects/";
    private const string DocumentPrefix = "/image-documents/";

    private readonly RequestDelegate _next;
    private readonly ILogger<UserActivityNotificationMiddleware> _logger;

    public UserActivityNotificationMiddleware(RequestDelegate next, ILogger<UserActivityNotificationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    private static readonly HashSet<string> MutatingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        if (!context.Request.Path.StartsWithSegments(RoutePrefix))
        {
            return;
        }
        if (!MutatingMethods.Contains(context.Request.Method))
        {
            return;
        }
        if (context.Response.StatusCode is < 200 or >= 300)
        {
            return;
        }

        // The image-document endpoints (documents, layers, operations, graphics) all share one
        // prefix. Two of them are plumbing, not an edit, and are excluded.
        var path = context.Request.Path.Value!;
        if (!path.Contains(DocumentPrefix))
        {
            return;
        }
        if (path.EndsWith("/exports") || path.EndsWith("/assets/upload-url"))
        {
            return;
        }

        var (projectId, subject) = await ResolveAsync(context);
        if (projectId is null)
        {
            return;
        }

        var current = context.RequestServices.GetRequiredService<ICurrentUser>();
        if (!current.IsAuthenticated)
        {
            return;
        }

        var notifier = context.RequestServices.GetRequiredService<IUserActivityNotifier>();
        await notifier.GraphicsEditedAsync(projectId.Value, current.UserId, subject);
    }

    private async Task<(Guid? ProjectId, string Subject)> ResolveAsync(HttpContext context)
    {
        if (!Guid.TryParse(SplitSegment(context.Request.Path.Value!, 3), out var projectId))
        {
            return (null, string.Empty);
        }

        var subject = "thumbnail graphic";
        var documentId = DocumentSegment(context.Request.Path.Value!);
        if (documentId is not null)
        {
            var db = context.RequestServices.GetRequiredService<InferenceApiDbContext>();
            var name = await db.EditImageDocuments
                .Where(d => d.Id == documentId)
                .Select(d => d.Name)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(name))
            {
                subject = name;
            }
        }
        return (projectId, subject);
    }

    /// <summary>The third path segment is the project id in /api/v1/projects/{id}/…</summary>
    private static string SplitSegment(string path, int index)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index] : string.Empty;
    }

    /// <summary>
    /// The document id in /api/v1/projects/{p}/image-documents/{documentId}/…, when the path
    /// names one (operations, layers, graphics all do; a bare create does not).
    /// </summary>
    private static Guid? DocumentSegment(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i] == "image-documents" && Guid.TryParse(parts[i + 1], out var id))
            {
                return id;
            }
        }
        return null;
    }
}

/// <summary>Registers <see cref="UserActivityNotificationMiddleware"/>.</summary>
public static class UserActivityNotificationMiddlewareExtensions
{
    public static IApplicationBuilder UseUserActivityNotifications(this IApplicationBuilder app)
        => app.UseMiddleware<UserActivityNotificationMiddleware>();
}
