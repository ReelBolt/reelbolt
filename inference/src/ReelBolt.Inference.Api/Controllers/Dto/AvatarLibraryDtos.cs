using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>How many files of each kind a library item holds.</summary>
public sealed record AvatarLibraryReferenceCountsResponse(int Images, int Videos, int Audio);

/// <summary>
/// One item of the workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library (global
/// avatars)"). No storage key, no file name and no consent text: the list shows what a member needs to pick one.
/// </summary>
/// <param name="SubjectKind"><c>Person</c> or <c>Object</c>.</param>
/// <param name="Usable">Whether it may be imported and used right now.</param>
/// <param name="UnusableReason"><c>consent_revoked</c> or <c>retention_expired</c> (a person only); null when usable.</param>
/// <param name="PrimaryReferenceId">The picture to show for it (<c>GET /{id}/references/{referenceId}/content</c>), or null.</param>
/// <param name="RetentionUntil">When a person's item is deleted whether or not anyone asks; null for an object.</param>
/// <param name="ImportCount">How many projects hold an import of it.</param>
/// <param name="Imported">Whether the project named in the call (<c>?projectId=</c>) already holds an import; false without one.</param>
/// <param name="CanManage">The caller is an Owner or Admin of the organization: rename, confirm again, withdraw consent, delete.</param>
/// <param name="VariantOfItemId">
/// The library item this one is a variant of (docs/design/avatar-objects.md "Avatar variants"); null for an original,
/// and once the original is deleted (its variants stay, ungrouped).
/// </param>
/// <param name="VariantLabel">A variant's short label ("Red"), kept when it is ungrouped; null for an original.</param>
/// <param name="VariantDescription">What sets a variant apart from its original; null for an original.</param>
/// <param name="VariantCount">How many items of the library are variants of this one (always 0 for a variant: one level only).</param>
public sealed record AvatarLibraryItemResponse(
    Guid Id,
    string SubjectKind,
    string Name,
    string? Description,
    bool Usable,
    string? UnusableReason,
    AvatarLibraryReferenceCountsResponse References,
    Guid? PrimaryReferenceId,
    DateTime? RetentionUntil,
    int ImportCount,
    bool Imported,
    bool CanManage,
    Guid? VariantOfItemId = null,
    string? VariantLabel = null,
    string? VariantDescription = null,
    int VariantCount = 0)
{
    public static AvatarLibraryItemResponse From(AvatarLibraryItemView view, bool canManage) => new(
        view.Item.Id,
        view.Item.SubjectKind.ToString(),
        view.Item.Name,
        view.Item.Description,
        view.UnusableReason is null,
        view.UnusableReason,
        new AvatarLibraryReferenceCountsResponse(view.Images, view.Videos, view.Audio),
        view.PrimaryReferenceId,
        view.Item.SubjectKind == AvatarSubjectKind.Object ? null : view.Item.RetentionUntil,
        view.ImportCount,
        view.Imported,
        canManage,
        view.Item.VariantOfItemId,
        view.Item.VariantLabel,
        view.Item.VariantDescription,
        view.VariantCount);
}

/// <summary>
/// What the library flows need from the server, never typed in the client: the two WORKSPACE statements (a person's
/// consent and an object's statement for every project of the workspace), verbatim per locale, with their versions.
/// Served as <c>library</c> by <c>GET .../avatars/requirements</c> and by the library list.
/// </summary>
/// <param name="Enabled">The library exists in this deployment (either avatar switch is on).</param>
/// <param name="CanManage">The caller is an Owner or Admin of the active organization.</param>
/// <param name="MaxItemsPerKind">The most items of one kind (people, or objects) the library holds.</param>
public sealed record AvatarLibraryRequirementsResponse(
    bool Enabled,
    bool CanManage,
    string ConsentStatementVersion,
    IReadOnlyDictionary<string, string> ConsentStatements,
    string ObjectStatementVersion,
    IReadOnlyDictionary<string, string> ObjectStatements,
    int MaxItemsPerKind)
{
    public static AvatarLibraryRequirementsResponse For(bool enabled, bool canManage) => new(
        enabled,
        canManage,
        AvatarWorkspaceConsentStatement.Version,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = AvatarWorkspaceConsentStatement.English,
            ["it"] = AvatarWorkspaceConsentStatement.Italian
        },
        AvatarWorkspaceObjectStatement.Version,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = AvatarWorkspaceObjectStatement.English,
            ["it"] = AvatarWorkspaceObjectStatement.Italian
        },
        AvatarLibraryLimits.MaxItemsPerKind);
}

/// <summary>
/// What <c>GET /api/v1/avatar-library</c> answers: the items, and the library requirements, so the workspace page
/// needs no project to read the statements.
/// </summary>
public sealed record AvatarLibraryListResponse(IReadOnlyList<AvatarLibraryItemResponse> Items, AvatarLibraryRequirementsResponse Library);

/// <summary>
/// Saves a project avatar to the workspace library. <paramref name="ConsentStatement"/> must equal the WORKSPACE
/// statement of the avatar's kind for <paramref name="ConsentLocale"/> (<c>en</c> or <c>it</c>) verbatim, as served in
/// <c>library.consentStatements</c> (a person) or <c>library.objectStatements</c> (an object). A person also sends the
/// adult tick and may send the keep-for days (default <see cref="ProjectAvatarLimits.DefaultRetentionDays"/>); for an
/// object both are refused (<c>400 field_not_for_objects</c>).
/// </summary>
public sealed record PublishToAvatarLibraryRequest(
    Guid ProjectId,
    Guid AvatarId,
    string? ConsentStatement,
    string? ConsentLocale,
    bool SubjectIsAdult = false,
    int? RetentionDays = null);

/// <summary>Name and description of a library item; a null field is left as it is, <c>""</c> clears the description. Every import follows.</summary>
public sealed record UpdateAvatarLibraryItemRequest(string? Name = null, string? Description = null);

/// <summary>Confirms a library item again: the workspace statement, and for a person the adult tick and the keep-for days. The files never change.</summary>
public sealed record ReconfirmAvatarLibraryItemRequest(
    string? ConsentStatement,
    string? ConsentLocale,
    bool SubjectIsAdult = false,
    int? RetentionDays = null);

/// <summary>One part of a library delete report.</summary>
public sealed record AvatarLibraryDeleteCountsResponse(int Deleted, int Failed);

/// <summary>What deleting a library item removed: its imports in the projects, then its stored files.</summary>
public sealed record DeleteAvatarLibraryItemResponse(AvatarLibraryDeleteCountsResponse Imports, AvatarLibraryDeleteCountsResponse Objects);

/// <summary>Imports a library item into a project (<c>POST .../avatars/import</c>).</summary>
public sealed record ImportLibraryAvatarRequest(Guid LibraryItemId);
