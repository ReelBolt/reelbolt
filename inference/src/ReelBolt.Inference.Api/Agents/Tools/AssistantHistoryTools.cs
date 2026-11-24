using System.Text.Json;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The assistant's PROJECT HISTORY surface (project-versioning contract section 10): list the saved
/// versions, read what changed between two of them (or one and now), save a named version, and restore
/// a version (a few items by name, or the whole project) through propose/apply pairs.
/// </summary>
/// <remarks>
/// Authority is re-read as the first statement of every body and never remembered: View for the reads,
/// Edit for a save and for an item restore, Delete for a whole-project restore (the same rule as the REST
/// service). A refusal writes nothing. Every reply names versions by number and name and items by kind and
/// name; no id ever appears, so the reply sanitizer has nothing to strip.
/// </remarks>
public sealed class AssistantHistoryTools
{
    internal const string OpSaveVersion = "SaveProjectVersion";
    internal const string OpRestoreVersion = "RestoreProjectVersion";

    /// <summary>The MCP refusal: a whole-project restore needs the person's own click.</summary>
    internal const string McpProjectScopeMessage =
        "Restoring a whole project needs your click in the in-app assistant card or the History page; "
        + "over MCP only single items can be restored (scope \"item\").";

    private const int MaxListed = 50;
    private const int MaxChangesListed = 200;

    private static readonly string StaleTokenErrorJson =
        JsonSerializer.Serialize(new { error = "stale confirmationToken, re-propose and try again" });

    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly IProjectAccessService _projectAccess;
    private readonly IProjectHistoryService _history;
    private readonly IProjectHistoryRestoreService _restore;

    public AssistantHistoryTools(
        IConfirmationTokenService confirmationTokens,
        IProjectAccessService projectAccess,
        IProjectHistoryService history,
        IProjectHistoryRestoreService restore)
    {
        _confirmationTokens = confirmationTokens;
        _projectAccess = projectAccess;
        _history = history;
        _restore = restore;
    }

    /// <param name="context">This invocation's caller and page context (see <see cref="AssistantToolContext"/>).</param>
    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (int? limit = null, int? beforeVersion = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ListVersionsAsync(context, limit, beforeVersion, projectId, ct),
                name: "ListProjectVersions",
                description: "Lists the project's saved versions (its history), newest first: each version's number, name (if it was named), what caused it (named, auto, beforeRun, beforeAssistant, beforeRestore, idle), when, who, how many items and how big. Reads only. `limit` is optional (default 20, at most 50); `beforeVersion` pages back to versions older than that number. Refer to versions by number. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (int fromVersion, int? toVersion = null, Guid? projectId = null, CancellationToken ct = default) =>
                    GetChangesAsync(context, fromVersion, toVersion, projectId, ct),
                name: "GetProjectChanges",
                description: "Lists what changed between a version (fromVersion, by number) and the project as it is now, or between two versions when toVersion is given: each changed item by kind and name (file path, workflow name, timeline name, design system, pronunciation, project details) with how it changed. Names only, no content. Reads only. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (string name, Guid? projectId = null, CancellationToken ct = default) =>
                    ProposeSaveAsync(context, name, projectId, ct),
                name: "ProposeSaveProjectVersion",
                description: "Proposes saving the project's current state as a named version (a restore point), WITHOUT saving it. `name` is 1 to 80 characters. Returns a confirmationToken and a plain-language summary; the proposal's confirmationToken is accepted by ApplySaveProjectVersion. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (string name, string confirmationToken, Guid? projectId = null, CancellationToken ct = default) =>
                    ApplySaveAsync(context, name, confirmationToken, projectId, ct),
                name: "ApplySaveProjectVersion",
                description: "Saves the project's current state as a named version. name and confirmationToken are verified against the proposal; a fabricated or stale token is refused and nothing is saved. Returns the new version's number and name. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (int version, string scope, string[]? items = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ProposeRestoreAsync(context, version, scope, items, projectId, ct),
                name: "ProposeRestoreProjectVersion",
                description: "Proposes restoring a saved version, WITHOUT changing anything. `version` is the version number (from ListProjectVersions). `scope` is \"item\" to restore only the items named in `items`, or \"project\" to put the whole project back to that version (also removing what the version does not have). Each entry of `items` is an item's name: a file path, workflow name or timeline name, optionally prefixed with its kind (\"file: notes/intro.md\", \"workflow: Intro\", \"timeline: Main\"), or \"design system\", \"pronunciation\" or \"project details\". A name that matches more than one item is refused with the candidates listed. A safety version of the current state is saved first, so a restore can be undone. Returns a confirmationToken and a summary of what would change; the proposal's confirmationToken is accepted by ApplyRestoreProjectVersion. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (int version, string scope, string confirmationToken, string[]? items = null, Guid? projectId = null, CancellationToken ct = default) =>
                    ApplyRestoreAsync(context, version, scope, items, confirmationToken, projectId, ct),
                name: "ApplyRestoreProjectVersion",
                description: "Restores a saved version: the same version, scope and items as the proposal, and its confirmationToken, are verified; a fabricated or stale token is refused and nothing is changed. The user is always asked first, even in auto mode. A safety version of the current state is saved first. Returns what was restored, removed or skipped (with a reason), by name, and the safety version's number. " + AssistantProjectScope.ProjectIdNote),
        };
    }

    // ---------------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------------

    private async Task<string> ListVersionsAsync(
        AssistantToolContext context, int? limit, int? beforeVersion, Guid? projectId, CancellationToken ct)
    {
        Project? project = Resolve(context, ProjectPermission.View, projectId, out string error);
        if (project is null)
            return error;

        ProjectHistoryVersionsPage? page = await _history.ListVersionsAsync(
            project.Id, Math.Clamp(limit ?? 20, 1, MaxListed), beforeVersion, ct);
        if (page is null)
            return AssistantProjectScope.ProjectNotFoundErrorJson;

        return JsonSerializer.Serialize(new
        {
            projectName = project.Name,
            retentionDays = page.RetentionDays,
            versions = page.Versions.Select(VersionShape).ToList()
        });
    }

    private async Task<string> GetChangesAsync(
        AssistantToolContext context, int fromVersion, int? toVersion, Guid? projectId, CancellationToken ct)
    {
        Project? project = Resolve(context, ProjectPermission.View, projectId, out string error);
        if (project is null)
            return error;

        ProjectHistoryVersionInfo? from = await FindVersionAsync(project.Id, fromVersion, ct);
        if (from is null)
            return VersionNotFoundJson(fromVersion);

        ProjectHistoryVersionInfo? to = null;
        if (toVersion is { } toNumber)
        {
            to = await FindVersionAsync(project.Id, toNumber, ct);
            if (to is null)
                return VersionNotFoundJson(toNumber);
        }

        ProjectHistoryChanges? changes = await _history.ComputeChangesAsync(project.Id, from.Id, to?.Id, ct);
        if (changes is null)
            return VersionNotFoundJson(fromVersion);

        return JsonSerializer.Serialize(new
        {
            projectName = project.Name,
            from = VersionShape(from),
            to = to is null ? "now" : $"version {to.Number}",
            changes = changes.Changes.Take(MaxChangesListed).Select(c => new
            {
                kind = AssistantHistoryItems.KindLabel(c.Kind),
                name = AssistantHistoryItems.Label(c.Kind, c.Name),
                previousName = c.PreviousName,
                change = c.Change
            }).ToList(),
            unchangedCount = changes.UnchangedCount,
            truncated = changes.Truncated || changes.Changes.Count > MaxChangesListed
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // Save (propose / apply)
    // ---------------------------------------------------------------------------------------------------

    private Task<string> ProposeSaveAsync(
        AssistantToolContext context, string name, Guid? projectId, CancellationToken ct)
    {
        Project? project = Resolve(context, ProjectPermission.Edit, projectId, out string error);
        if (project is null)
            return Task.FromResult(error);

        if (!ProjectVersionName.IsValid(name))
            return Task.FromResult(InvalidNameJson());

        string clean = name.Trim();
        string token = _confirmationTokens.Compute(OpSaveVersion, SavePayload(project.Id, clean), context.UserId);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            operation = OpSaveVersion,
            confirmationToken = token,
            summary = $"Save the current state of the project \"{project.Name}\" as the version \"{clean}\".",
            projectName = project.Name,
            versionName = clean
        }));
    }

    private async Task<string> ApplySaveAsync(
        AssistantToolContext context, string name, string confirmationToken, Guid? projectId, CancellationToken ct)
    {
        Project? project = Resolve(context, ProjectPermission.Edit, projectId, out string error);
        if (project is null)
            return error;

        if (!ProjectVersionName.IsValid(name))
            return InvalidNameJson();

        string clean = name.Trim();
        if (!_confirmationTokens.Verify(OpSaveVersion, SavePayload(project.Id, clean), context.UserId, confirmationToken))
            return StaleTokenErrorJson;

        try
        {
            ProjectVersion? version = await _history.CaptureAsync(
                project.Id, ProjectVersionCause.Named, clean, context.UserId, ct);
            if (version is null)
                return AssistantProjectScope.ProjectNotFoundErrorJson;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                projectName = project.Name,
                versionNumber = version.Number,
                versionName = version.Name,
                itemCount = version.ItemCount
            });
        }
        catch (ProjectHistoryTooLargeException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, errorCode = ex.Code, error = ex.Message });
        }
        catch (ProjectHistoryNamedVersionLimitException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, errorCode = ex.Code, error = ex.Message });
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Restore (propose / apply)
    // ---------------------------------------------------------------------------------------------------

    private async Task<string> ProposeRestoreAsync(
        AssistantToolContext context, int version, string scope, string[]? items, Guid? projectId, CancellationToken ct)
    {
        // Resolve (and re-authorize) the project BEFORE validating the scope or the item names, so a
        // caller naming a project it cannot reach always gets the one "project not found" refusal,
        // never a validation error that would confirm the project exists (D5, AssistantAnyProjectTests).
        Project? project = Resolve(context, PermissionFor(ParseScope(scope)), projectId, out string error);
        if (project is null)
            return error;

        if (!TryScope(scope, out ProjectHistoryRestoreScope parsed, out string scopeError))
            return scopeError;

        if (parsed == ProjectHistoryRestoreScope.Project && context.Initiator == ExecutionInitiator.Mcp)
            return McpProjectScopeJson();

        PreparedRestore? prepared = await PrepareRestoreAsync(project.Id, version, parsed, items, ct);
        if (prepared is null)
            return VersionNotFoundJson(version);
        if (prepared.Error is not null)
            return prepared.Error;

        ProjectHistoryRestorePreview? preview = await _restore.PreviewAsync(
            project.Id, prepared.Version.Id, parsed, prepared.Refs, ct);
        if (preview is null)
            return VersionNotFoundJson(version);

        string token = _confirmationTokens.Compute(
            OpRestoreVersion, RestorePayload(project.Id, prepared.Version.Id, parsed, prepared.Refs), context.UserId);

        return JsonSerializer.Serialize(new
        {
            operation = OpRestoreVersion,
            confirmationToken = token,
            summary = RestoreSummary(project.Name, prepared.Version, parsed, prepared.Items),
            projectName = project.Name,
            versionNumber = prepared.Version.Number,
            versionName = prepared.Version.Name,
            scope = parsed == ProjectHistoryRestoreScope.Project ? "project" : "item",
            changes = preview.Changes.Take(MaxChangesListed).Select(c => new
            {
                kind = AssistantHistoryItems.KindLabel(c.Kind),
                name = AssistantHistoryItems.Label(c.Kind, c.Name),
                change = c.Change
            }).ToList(),
            blocked = preview.Blocked.Select(b => new
            {
                kind = AssistantHistoryItems.KindLabel(b.Kind),
                name = AssistantHistoryItems.Label(b.Kind, b.Name),
                reason = b.Reason
            }).ToList()
        });
    }

    private async Task<string> ApplyRestoreAsync(
        AssistantToolContext context, int version, string scope, string[]? items, string confirmationToken,
        Guid? projectId, CancellationToken ct)
    {
        // Same order as ProposeRestoreAsync: the project is resolved and re-authorized first, so a
        // foreign project answers "project not found" whatever the scope or items say.
        Project? project = Resolve(context, PermissionFor(ParseScope(scope)), projectId, out string error);
        if (project is null)
            return error;

        if (!TryScope(scope, out ProjectHistoryRestoreScope parsed, out string scopeError))
            return scopeError;

        if (parsed == ProjectHistoryRestoreScope.Project && context.Initiator == ExecutionInitiator.Mcp)
            return McpProjectScopeJson();

        PreparedRestore? prepared = await PrepareRestoreAsync(project.Id, version, parsed, items, ct);
        if (prepared is null)
            return VersionNotFoundJson(version);
        if (prepared.Error is not null)
            return prepared.Error;

        if (!_confirmationTokens.Verify(
                OpRestoreVersion, RestorePayload(project.Id, prepared.Version.Id, parsed, prepared.Refs),
                context.UserId, confirmationToken))
            return StaleTokenErrorJson;

        ProjectHistoryRestoreResult? result;
        try
        {
            result = await _restore.RestoreAsync(project.Id, prepared.Version.Id, parsed, prepared.Refs, context.UserId, ct);
        }
        catch (ProjectHistoryRestoreConflictException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, errorCode = ex.Code, error = ex.Message });
        }

        if (result is null)
            return VersionNotFoundJson(version);

        ProjectHistoryVersionInfo? safety = await _history.GetVersionInfoAsync(project.Id, result.SafetyVersionId, ct);
        Dictionary<(ProjectVersionItemKind, string), string?> names = prepared.Items
            .ToDictionary(i => (i.Kind, i.Key), i => i.Name);
        foreach (ProjectHistoryRestoreEntry entry in result.Restored.Concat(result.Removed))
            names[(entry.Kind, entry.Key)] = entry.Name;
        foreach (ProjectHistoryRestoreSkipped skipped in result.Skipped)
            names[(skipped.Kind, skipped.Key)] = skipped.Name ?? names.GetValueOrDefault((skipped.Kind, skipped.Key));

        return JsonSerializer.Serialize(new
        {
            ok = true,
            projectName = project.Name,
            versionNumber = prepared.Version.Number,
            versionName = prepared.Version.Name,
            safetyVersionNumber = safety?.Number,
            restored = result.Restored.Select(e => new { kind = AssistantHistoryItems.KindLabel(e.Kind), name = AssistantHistoryItems.Label(e.Kind, e.Name) }).ToList(),
            removed = result.Removed.Select(e => new { kind = AssistantHistoryItems.KindLabel(e.Kind), name = AssistantHistoryItems.Label(e.Kind, e.Name) }).ToList(),
            skipped = result.Skipped.Select(s => new { kind = AssistantHistoryItems.KindLabel(s.Kind), name = AssistantHistoryItems.Label(s.Kind, s.Name), reason = s.Reason }).ToList(),
            notices = result.Notices.Select(n => new
            {
                kind = AssistantHistoryItems.KindLabel(n.Kind),
                name = AssistantHistoryItems.Label(n.Kind, names.GetValueOrDefault((n.Kind, n.Key))),
                code = n.Code
            }).ToList()
        });
    }

    private sealed record PreparedRestore(
        ProjectHistoryVersionInfo Version,
        IReadOnlyList<ProjectHistoryItemInfo> Items,
        IReadOnlyList<ProjectHistoryRestoreItemRef> Refs,
        string? Error);

    /// <summary>Resolves the version by number and the items by name, server-side. Null when the version is gone.</summary>
    private async Task<PreparedRestore?> PrepareRestoreAsync(
        Guid projectId, int number, ProjectHistoryRestoreScope scope, string[]? items, CancellationToken ct)
    {
        ProjectHistoryVersionInfo? info = await FindVersionAsync(projectId, number, ct);
        if (info is null)
            return null;

        ProjectHistoryVersionDetail? detail = await _history.GetVersionAsync(projectId, info.Id, ct);
        if (detail is null)
            return null;

        if (scope == ProjectHistoryRestoreScope.Project)
            return new PreparedRestore(info, detail.Items, [], null);

        AssistantHistoryItems.Resolution resolution = AssistantHistoryItems.Resolve(detail.Items, items);
        return new PreparedRestore(info, resolution.Items, resolution.Refs, resolution.Error);
    }

    private static string RestoreSummary(
        string projectName, ProjectHistoryVersionInfo version, ProjectHistoryRestoreScope scope,
        IReadOnlyList<ProjectHistoryItemInfo> items)
    {
        string label = AssistantHistoryItems.VersionLabel(version);
        return scope == ProjectHistoryRestoreScope.Project
            ? $"Put the whole project \"{projectName}\" back to {label}. Anything the version does not have is removed. A safety version of the current state is saved first."
            : $"Restore {items.Count} item(s) of the project \"{projectName}\" from {label}. A safety version of the current state is saved first.";
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------

    private async Task<ProjectHistoryVersionInfo?> FindVersionAsync(Guid projectId, int number, CancellationToken ct)
    {
        if (number < 1 || number == int.MaxValue)
            return null;

        ProjectHistoryVersionsPage? page = await _history.ListVersionsAsync(projectId, 1, number + 1, ct);
        ProjectHistoryVersionInfo? first = page?.Versions.FirstOrDefault();
        return first is not null && first.Number == number ? first : null;
    }

    private static object VersionShape(ProjectHistoryVersionInfo v) => new
    {
        number = v.Number,
        name = v.Name,
        cause = v.Cause,
        createdAt = v.CreatedAt,
        createdBy = v.CreatedByName,
        itemCount = v.ItemCount,
        totalBytes = v.TotalBytes
    };

    private static ProjectPermission PermissionFor(ProjectHistoryRestoreScope scope) =>
        scope == ProjectHistoryRestoreScope.Project ? ProjectPermission.Delete : ProjectPermission.Edit;

    /// <summary>
    /// The permission the scope will need, decided <b>before</b> the scope is validated: an unusable
    /// scope is treated as <c>item</c>, the weaker request, so project resolution can happen first.
    /// </summary>
    private static ProjectHistoryRestoreScope ParseScope(string? scope) =>
        string.Equals(scope?.Trim(), "project", StringComparison.OrdinalIgnoreCase)
            ? ProjectHistoryRestoreScope.Project
            : ProjectHistoryRestoreScope.Item;

    private static bool TryScope(string? scope, out ProjectHistoryRestoreScope parsed, out string errorJson)
    {
        errorJson = string.Empty;
        switch (scope?.Trim().ToLowerInvariant())
        {
            case "item":
                parsed = ProjectHistoryRestoreScope.Item;
                return true;
            case "project":
                parsed = ProjectHistoryRestoreScope.Project;
                return true;
            default:
                parsed = ProjectHistoryRestoreScope.Item;
                errorJson = JsonSerializer.Serialize(new { error = "scope must be \"item\" or \"project\"" });
                return false;
        }
    }

    private static string McpProjectScopeJson() =>
        JsonSerializer.Serialize(new { ok = false, error = McpProjectScopeMessage });

    private static string InvalidNameJson() =>
        JsonSerializer.Serialize(new
        {
            error = $"a version name must be 1 to {ProjectVersionName.MaxLength} characters without control characters"
        });

    private static string VersionNotFoundJson(int number) =>
        JsonSerializer.Serialize(new
        {
            error = $"version {number} not found",
            hint = "Look the version up by its number with ListProjectVersions."
        });

    private static string SavePayload(Guid projectId, string name) =>
        JsonSerializer.Serialize(new { op = OpSaveVersion, projectId, name });

    private static string RestorePayload(
        Guid projectId, Guid versionId, ProjectHistoryRestoreScope scope, IReadOnlyList<ProjectHistoryRestoreItemRef> refs) =>
        JsonSerializer.Serialize(new
        {
            op = OpRestoreVersion,
            projectId,
            versionId,
            scope = scope.ToString(),
            items = refs.Select(r => $"{r.Kind}:{r.Key}").Order(StringComparer.Ordinal).ToList()
        });

    private Project? Resolve(AssistantToolContext context, ProjectPermission permission, Guid? projectId, out string errorJson) =>
        AssistantProjectScope.Resolve(_projectAccess, context, permission, projectId, out errorJson);
}

/// <summary>Item naming and server-side name resolution shared by the history tools and the approval card.</summary>
internal static class AssistantHistoryItems
{
    internal const string DesignSystemName = "design system";
    internal const string PronunciationName = "pronunciation";
    internal const string ProjectDetailsName = "project details";

    internal sealed record Resolution(
        IReadOnlyList<ProjectHistoryItemInfo> Items,
        IReadOnlyList<ProjectHistoryRestoreItemRef> Refs,
        string? Error);

    internal static string KindLabel(ProjectVersionItemKind kind) => kind switch
    {
        ProjectVersionItemKind.File => "file",
        ProjectVersionItemKind.Workflow => "workflow",
        ProjectVersionItemKind.Timeline => "timeline",
        ProjectVersionItemKind.DesignSystem => "design system",
        ProjectVersionItemKind.Pronunciation => "pronunciation",
        ProjectVersionItemKind.Project => "project details",
        _ => kind.ToString().ToLowerInvariant()
    };

    /// <summary>The item's display name; the singletons have a fixed one.</summary>
    internal static string Label(ProjectVersionItemKind kind, string? name) => kind switch
    {
        ProjectVersionItemKind.DesignSystem => DesignSystemName,
        ProjectVersionItemKind.Pronunciation => PronunciationName,
        ProjectVersionItemKind.Project => ProjectDetailsName,
        _ => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name
    };

    internal static string VersionLabel(ProjectHistoryVersionInfo v) =>
        string.IsNullOrWhiteSpace(v.Name) ? $"version {v.Number}" : $"version {v.Number} (\"{v.Name}\")";

    internal static string Describe(ProjectHistoryItemInfo item) =>
        item.Kind is ProjectVersionItemKind.File or ProjectVersionItemKind.Workflow or ProjectVersionItemKind.Timeline
            ? $"{KindLabel(item.Kind)}: {Label(item.Kind, item.Name)}"
            : Label(item.Kind, item.Name);

    internal static Resolution Resolve(IReadOnlyList<ProjectHistoryItemInfo> all, IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0 || requested.Count > 50)
        {
            return Fail("item scope needs 1 to 50 item names in `items`; use scope \"project\" to restore everything");
        }

        List<ProjectHistoryItemInfo> picked = new();
        foreach (string raw in requested)
        {
            (ProjectVersionItemKind? kind, string name) = Parse(raw);
            if (name.Length == 0)
                return Fail("an item name is empty");

            List<ProjectHistoryItemInfo> matches = all
                .Where(i => (kind is null || i.Kind == kind) && NameMatches(i, name))
                .ToList();
            if (matches.Count == 0 && (kind is null || kind == ProjectVersionItemKind.File))
            {
                matches = all
                    .Where(i => i.Kind == ProjectVersionItemKind.File
                                && i.Name is { } path
                                && path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (matches.Count == 0)
                return Fail($"no item named \"{name}\" in that version");
            if (matches.Count > 1)
            {
                string candidates = string.Join(", ", matches.Select(Describe).Distinct().Take(10).Select(c => $"\"{c}\""));
                return Fail($"\"{name}\" matches more than one item; say which one (prefix it with its kind): {candidates}");
            }

            if (!picked.Any(p => p.Kind == matches[0].Kind && p.Key == matches[0].Key))
                picked.Add(matches[0]);
        }

        return new Resolution(
            picked,
            picked.Select(i => new ProjectHistoryRestoreItemRef(i.Kind, i.Key)).ToList(),
            null);

        static Resolution Fail(string message) => new([], [], JsonSerializer.Serialize(new { error = message }));
    }

    private static bool NameMatches(ProjectHistoryItemInfo item, string name) =>
        string.Equals(Label(item.Kind, item.Name), name, StringComparison.OrdinalIgnoreCase);

    private static (ProjectVersionItemKind? Kind, string Name) Parse(string raw)
    {
        string text = (raw ?? string.Empty).Trim();
        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            ProjectVersionItemKind? kind = text[..colon].Trim().ToLowerInvariant() switch
            {
                "file" => ProjectVersionItemKind.File,
                "workflow" => ProjectVersionItemKind.Workflow,
                "timeline" => ProjectVersionItemKind.Timeline,
                _ => null
            };
            if (kind is not null)
                return (kind, text[(colon + 1)..].Trim());
        }

        return (null, text);
    }
}
