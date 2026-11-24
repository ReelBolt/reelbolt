using System.Globalization;
using System.Text.Json;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

// The workspace avatar library from the assistant and MCP (docs/design/avatar-objects.md "The workspace avatar library"):
// the list by name, and a linked import into a project through AvatarLibraryService, the one library implementation.
// Publishing, renaming, confirming again, withdrawing and deleting stay in the app.
public sealed partial class AssistantAvatarTools
{
    internal const string OpImportLibraryAvatar = "ImportLibraryAvatar";

    internal const string LibraryNote =
        "Saving to the workspace library, renaming, confirming again, withdrawing and deleting are done by the workspace's owners and admins in the app (Settings, Avatar library).";

    /// <summary>What both halves of <c>ImportLibraryAvatar</c> decided, and the token payload; the card reads it too.</summary>
    internal sealed record ImportPrepared(Project Project, AvatarLibraryItemView Item, string Payload);

    /// <summary>The plain words of a library item's reason (a person only: withdrawn, or past its keep-until date).</summary>
    internal static string LibraryReasonWords(string reason) => reason switch
    {
        ProjectAvatar.ReasonConsentRevoked => "Consent was withdrawn.",
        ProjectAvatar.ReasonRetentionExpired => "Its keep-until date has passed.",
        _ => "It cannot be used right now."
    };

    // ------------------------------------------------------------------ ListAvatarLibrary

    private async Task<string> ListAvatarLibraryAsync(AssistantToolContext context, Guid? projectId, CancellationToken ct)
    {
        // With a project (named, or open on the page) the list also says what it already holds; it is re-authorized now,
        // before the switches, so a foreign project reads exactly like a missing one.
        Project? project = null;
        if (AssistantProjectScope.IsExplicit(projectId) || context.ProjectId is { } page && page != Guid.Empty)
        {
            project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.View, projectId, out string error);
            if (project is null)
                return error;
        }

        if ((!PeopleOn && !ObjectsOn) || _library is null)
            return JsonSerializer.Serialize(new { available = false, message = NotAvailableMessage }, Json);

        Guid? organizationId = project?.OrganizationId ?? _currentUser?.OrganizationId;
        if (organizationId is not { } org)
            return Err("No workspace is active for this call.", "no_workspace");

        IReadOnlyList<AvatarLibraryItemView> items = await _library.ListAsync(org, project?.Id, ct);
        // A variant is shown under its original's name (one level only; an original the list does not hold reads as none).
        Dictionary<Guid, string> names = items.ToDictionary(v => v.Item.Id, v => v.Item.Name);
        return JsonSerializer.Serialize(new
        {
            available = true,
            projectName = project?.Name,
            items = items.Select(v => new
            {
                name = v.Item.Name,
                kind = KindWord(v.Item.SubjectKind),
                description = v.Item.Description,
                usable = v.UnusableReason is null,
                unusableReason = v.UnusableReason is { } reason ? LibraryReasonWords(reason) : null,
                references = new { images = v.Images, videos = v.Videos, audio = v.Audio },
                keepUntil = v.Item.SubjectKind == AvatarSubjectKind.Object
                    ? null
                    : v.Item.RetentionUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                usedInProjects = v.ImportCount,
                inThisProject = project is null ? (bool?)null : v.Imported,
                variantOf = v.Item.VariantOfItemId is { } original && names.TryGetValue(original, out string? originalName) ? originalName : null,
                variantLabel = v.Item.VariantLabel,
                variantDescription = v.Item.VariantDescription,
                variants = v.VariantCount > 0 ? v.VariantCount : (int?)null,
            }).ToArray(),
            note = (items.Count == 0 ? "The workspace library is empty. " : string.Empty) + LibraryNote,
        }, Json);
    }

    // ------------------------------------------------------------------ ImportLibraryAvatar

    /// <summary>
    /// Everything both halves of <c>ImportLibraryAvatar</c> decide the same way: a switch, Edit (re-read now), a verified
    /// email, the item by name in the project's workspace (of a kind switched on), that it is usable and not imported yet.
    /// The plan, the limits, the quota and the copy are <see cref="AvatarLibraryService.ImportAsync"/>'s.
    /// </summary>
    internal async Task<(ImportPrepared? Prepared, string? Answer)> PrepareImportAsync(
        AssistantToolContext context, string? name, Guid? projectId, CancellationToken ct)
    {
        // The project first, so a foreign project reads exactly like a missing one whatever the switches say.
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string scopeError);
        if (project is null)
            return (null, scopeError);
        if ((!PeopleOn && !ObjectsOn) || _library is null)
            return (null, Err(NotAvailableMessage, NotAvailableCode));

        if (EmailRefusal() is { } unverified)
            return (null, unverified);

        string wanted = name?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
            return (null, Err("name is the library item's name, as ListAvatarLibrary gives it.", "name_required"));

        IReadOnlyList<AvatarLibraryItemView> items = await _library.ListAsync(project.OrganizationId, project.Id, ct);
        List<AvatarLibraryItemView> matches = [.. items.Where(v => string.Equals(v.Item.Name, wanted, StringComparison.Ordinal))];
        if (matches.Count == 0)
            matches = [.. items.Where(v => string.Equals(v.Item.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))];
        string listed = items.Count == 0
            ? " The workspace library is empty."
            : " Library items: " + string.Join(", ", items.Take(ProjectSubjectNames.MaxNamesListed).Select(v => $"\"{v.Item.Name}\" ({KindWord(v.Item.SubjectKind)})")) + ".";
        if (matches.Count == 0)
            return (null, Err($"no item called \"{wanted}\" is in the workspace avatar library." + listed, "library_item_not_found"));
        if (matches.Count > 1)
            return (null, Err($"\"{wanted}\" could mean more than one library item; use the exact name." + listed, "library_item_ambiguous"));

        AvatarLibraryItemView item = matches[0];
        if (item.UnusableReason is { } reason)
            return (null, Err($"\"{item.Item.Name}\" cannot be imported: " + LibraryReasonWords(reason), "LIBRARY_ITEM_NOT_USABLE"));
        if (item.Imported)
        {
            return (null, JsonSerializer.Serialize(new
            {
                alreadyImported = true,
                name = item.Item.Name,
                kind = KindWord(item.Item.SubjectKind),
                projectName = project.Name,
                hint = "The project already holds this library item; nothing was written.",
            }, Json));
        }

        string payload = Payload(new { op = OpImportLibraryAvatar, projectId = project.Id, item = item.Item.Id, name = item.Item.Name });
        return (new ImportPrepared(project, item, payload), null);
    }

    private async Task<string> ProposeImportLibraryAvatarAsync(AssistantToolContext context, string? name, Guid? projectId, CancellationToken ct)
    {
        (ImportPrepared? prepared, string? answer) = await PrepareImportAsync(context, name, projectId, ct);
        if (prepared is null)
            return answer!;

        return JsonSerializer.Serialize(new
        {
            operation = OpImportLibraryAvatar,
            confirmationToken = Mint(OpImportLibraryAvatar, prepared.Payload, context),
            summary = ImportSummary(prepared),
            projectName = prepared.Project.Name,
            name = prepared.Item.Item.Name,
            kind = KindWord(prepared.Item.Item.SubjectKind),
            credits = "Free",
        }, Json);
    }

    private async Task<string> ApplyImportLibraryAvatarAsync(
        AssistantToolContext context, string? name, string? confirmationToken, Guid? projectId, CancellationToken ct)
    {
        (ImportPrepared? prepared, string? answer) = await PrepareImportAsync(context, name, projectId, ct);
        if (prepared is null)
            return answer!;
        if (!Verify(OpImportLibraryAvatar, prepared.Payload, context, confirmationToken))
            return Err(StaleTokenMessage);

        AvatarLibraryResult<ProjectAvatar> imported = await _library!.ImportAsync(prepared.Project, prepared.Item.Item.Id, context.UserId, ct);
        if (imported.Refusal is { } refused)
            return FromRefusal(refused);
        if (imported.Value is not { } avatar)
            return Err("That item is no longer in the workspace avatar library.", "library_item_not_found");

        return JsonSerializer.Serialize(new
        {
            imported = true,
            name = avatar.Name,
            kind = KindWord(avatar.SubjectKind),
            fromLibrary = true,
            projectName = prepared.Project.Name,
            hint = "It is now an avatar of the project, linked to the library item: changes made to the item in the library apply here too.",
        }, Json);
    }

    internal static string ImportSummary(ImportPrepared prepared)
    {
        AvatarLibraryItemView view = prepared.Item;
        string files = $"{view.Images} picture{(view.Images == 1 ? "" : "s")}"
                       + (view.Videos > 0 ? $", {view.Videos} video{(view.Videos == 1 ? "" : "s")}" : "")
                       + (view.Audio > 0 ? $", {view.Audio} recording{(view.Audio == 1 ? "" : "s")}" : "");
        string consent = view.Item.SubjectKind == AvatarSubjectKind.Person
            ? " The person's consent was given in the app when they were saved to the workspace library."
            : string.Empty;
        return $"Bring \"{view.Item.Name}\" ({KindWord(view.Item.SubjectKind)}) from the workspace avatar library into the project \"{prepared.Project.Name}\": " +
               $"its {files} are copied into the project's files, and it follows the library item.{consent} Free.";
    }
}
