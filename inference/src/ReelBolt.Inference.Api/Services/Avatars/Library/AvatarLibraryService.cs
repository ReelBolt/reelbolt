using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

/// <summary>
/// The workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library (global avatars)",
/// decisions AO-5 .. AO-7): a person or an object that every project of ONE organization may import. This class is the
/// library's one implementation, shared by the REST routes (<c>AvatarLibraryController</c>, the project import route),
/// the retention sweep and, later, the assistant tools, and the ONLY writer of
/// <c>organizations/{organizationId:D}/avatar-library/{itemId:D}/…</c> (<see cref="AvatarLibraryStorage"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> A library item is the master record and holds its own copies of the media. An import is an ordinary
/// <see cref="ProjectAvatar"/> (<see cref="ProjectAvatar.LibraryItemId"/> set) plus ordinary project files copied from
/// the library, so the Avatars tab, the pickers, the assistant and the engine resolver see a normal project avatar. A
/// linked import FOLLOWS its item: name, description, the consent columns, retention and revoke are copied to every
/// import by this class whenever the item changes.
/// </para>
/// <para>
/// <b>What the caller has already decided.</b> Who may call is the route's (or the tool's) business: publish, rename,
/// reconfirm, revoke and delete are for an Owner or Admin of the active organization, import for a member with Edit on
/// the project and a verified email. This class is handed the organization (or the already-authorized project) and
/// checks everything else itself, so no caller can forget it: the kind's switch (an item of a kind whose switch is off
/// does not exist), the kind's plan feature, the statement, usability, the limits and the storage quota.
/// </para>
/// <para>
/// <b>Bounds and atomicity.</b> An item has at most <see cref="AvatarLibraryLimits.MaxImportsPerItem"/> imports, so
/// every propagation loop is bounded. Rename, reconfirm and revoke write the item and all its imports in ONE save (one
/// transaction). Every mutation of an organization's library runs under that organization's storage guard
/// (<see cref="OrganizationStorageLock"/>), so an import can never slip in beside a revoke or a delete, and two
/// publishes cannot both take the last free place. Bytes are copied outside the guard.
/// </para>
/// <para>
/// <b>Events</b> go through <see cref="ProjectAvatarEvents"/>: counts and outcome words only, never the subject's name
/// or media.
/// </para>
/// </remarks>
public sealed partial class AvatarLibraryService
{
    private const int MaxAttempts = 3;

    private readonly InferenceApiDbContext _db;
    private readonly IAvatarLibraryStore _store;
    private readonly IFileStorageService _storage;
    private readonly ProjectAvatarDeletion _deletion;
    private readonly AvatarOptions _options;
    private readonly ILogger<AvatarLibraryService> _logger;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly IPlanGateService? _planGate;
    private readonly OrganizationStorageLock _storageLock;
    private readonly ICsmaHashMatcher? _csam;
    private readonly IContentModerationEnqueuer? _moderation;
    private readonly IPublishEndpoint? _publish;

    public AvatarLibraryService(
        InferenceApiDbContext db,
        IAvatarLibraryStore store,
        IFileStorageService storage,
        ProjectAvatarDeletion deletion,
        IOptions<AvatarOptions> options,
        ILogger<AvatarLibraryService> logger,
        IOrganizationEntitlements? entitlements = null,
        IPlanGateService? planGate = null,
        OrganizationStorageLock? storageLock = null,
        ICsmaHashMatcher? csam = null,
        IContentModerationEnqueuer? moderation = null,
        IPublishEndpoint? publish = null)
    {
        _db = db;
        _store = store;
        _storage = storage;
        _deletion = deletion;
        _options = options.Value;
        _logger = logger;
        _entitlements = entitlements;
        _planGate = planGate;
        _storageLock = storageLock ?? new OrganizationStorageLock();
        _csam = csam;
        _moderation = moderation;
        _publish = publish;
    }

    /// <summary>Whether the library exists in this deployment at all: either avatar switch is on.</summary>
    public bool Enabled => _options.AnyEnabled;

    // ------------------------------------------------------------------ read

    /// <summary>
    /// The items of <paramref name="organizationId"/>'s library, by name, each with what the list shows about it. An
    /// item of a kind whose switch is off is left out. With <paramref name="projectId"/> (a project of the same
    /// organization the caller may see), each item also says whether that project already holds an import.
    /// </summary>
    public async Task<IReadOnlyList<AvatarLibraryItemView>> ListAsync(Guid organizationId, Guid? projectId, CancellationToken ct)
    {
        bool people = _options.Enabled;
        bool objects = _options.Objects.Enabled;
        if (!people && !objects) return [];

        List<AvatarLibraryItem> items = await _db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId
                        && ((people && i.SubjectKind == AvatarSubjectKind.Person) || (objects && i.SubjectKind == AvatarSubjectKind.Object)))
            .OrderBy(i => i.Name)
            .ThenBy(i => i.CreatedAt)
            .ToListAsync(ct);
        return await ToViewsAsync(items, projectId, ct);
    }

    /// <summary>
    /// Whether item <paramref name="itemId"/> exists for the organization. With <paramref name="anyKind"/> false an item
    /// of a kind whose switch is off does not exist (every action but delete); with true it does (delete always works).
    /// </summary>
    public async Task<bool> ExistsAsync(Guid organizationId, Guid itemId, bool anyKind, CancellationToken ct)
    {
        AvatarSubjectKind? kind = await _db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.Id == itemId && i.OrganizationId == organizationId)
            .Select(i => (AvatarSubjectKind?)i.SubjectKind)
            .FirstOrDefaultAsync(ct);
        return kind is { } found && (anyKind || _options.IsEnabled(found));
    }

    /// <summary>One item of the organization, or null when there is none (or its kind is switched off).</summary>
    public async Task<AvatarLibraryItemView?> GetAsync(Guid organizationId, Guid itemId, Guid? projectId, CancellationToken ct)
    {
        AvatarLibraryItem? item = await FindAsync(organizationId, itemId, tracked: false, ct);
        return item is null ? null : (await ToViewsAsync([item], projectId, ct))[0];
    }

    /// <summary>
    /// Opens one PICTURE of an item for streaming (the list's thumbnail and the import picker). Null when the item or
    /// the reference does not exist for the organization, the kind is switched off, the reference is not a picture
    /// (videos and recordings are never served from the library), or the object is gone.
    /// </summary>
    public async Task<AvatarLibraryPicture?> OpenPictureAsync(Guid organizationId, Guid itemId, Guid referenceId, CancellationToken ct)
    {
        AvatarLibraryItem? item = await FindAsync(organizationId, itemId, tracked: false, ct);
        if (item is null) return null;
        // A person variant of a revoked or expired original is not served either.
        if (item.SubjectKind == AvatarSubjectKind.Person && item.VariantOfItemId is { } originalId
            && (await _db.AvatarLibraryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == originalId && i.OrganizationId == organizationId, ct))
                ?.UnusableReason(DateTime.UtcNow) is not null)
            return null;

        AvatarLibraryReference? reference = await _db.AvatarLibraryReferences.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == referenceId && r.LibraryItemId == itemId, ct);
        if (reference is null || reference.Kind != AvatarReferenceKind.Image) return null;
        // The key is the row's, and still must be an object of THIS item: a row never vouches for a foreign key.
        if (AvatarLibraryStorage.Parse(reference.StorageKey) != (organizationId, itemId)) return null;

        // A held copy is not served: the same uniform answer as a missing picture.
        if ((await AvatarLibraryHoldGate.ReadAsync(_db, organizationId, [reference], _logger, ct)).Holds(reference)) return null;

        Stream? content = await _store.OpenReadAsync(reference.StorageKey, ct);
        return content is null ? null : new AvatarLibraryPicture(content, reference.MimeType, reference.FileName, reference.SizeBytes);
    }

    /// <summary>
    /// The item <paramref name="itemId"/> of <paramref name="organizationId"/>, or null when there is none OR its kind
    /// is switched off. The organization predicate is explicit, beside the table's tenant filter: a caller that runs
    /// under a system scope (the sweep, the assistant) is held to the same boundary.
    /// </summary>
    private async Task<AvatarLibraryItem?> FindAsync(Guid organizationId, Guid itemId, bool tracked, CancellationToken ct)
    {
        IQueryable<AvatarLibraryItem> query = tracked ? _db.AvatarLibraryItems : _db.AvatarLibraryItems.AsNoTracking();
        AvatarLibraryItem? item = await query.FirstOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == organizationId, ct);
        return item is not null && _options.IsEnabled(item.SubjectKind) ? item : null;
    }

    private async Task<List<AvatarLibraryItemView>> ToViewsAsync(IReadOnlyList<AvatarLibraryItem> items, Guid? projectId, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        Guid[] ids = [.. items.Select(i => i.Id)];
        List<AvatarLibraryReference> references = await _db.AvatarLibraryReferences.AsNoTracking()
            .Where(r => ids.Contains(r.LibraryItemId))
            .ToListAsync(ct);
        var imports = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.LibraryItemId != null && ids.Contains(a.LibraryItemId.Value))
            .Select(a => new { ItemId = a.LibraryItemId!.Value, a.ProjectId })
            .ToListAsync(ct);
        // How many variants each listed item has (a variant itself has none: one level only). The organization
        // predicate is explicit beside the tenant filter, as in FindAsync.
        Guid organizationId = items[0].OrganizationId;
        Dictionary<Guid, int> variantCounts = (await _db.AvatarLibraryItems.AsNoTracking()
                .Where(i => i.OrganizationId == organizationId && i.VariantOfItemId != null && ids.Contains(i.VariantOfItemId.Value))
                .Select(i => i.VariantOfItemId!.Value)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        // A copy whose content is held (CSAM or moderation verdict on any file of the organization with the same bytes)
        // shows as unusable, with the same word a held reference has on a project avatar.
        AvatarLibraryHoldGate.HoldState hold = await AvatarLibraryHoldGate.ReadAsync(
            _db, organizationId, references, _logger, ct);

        // A person variant is the original's person: it is unusable whenever the original is revoked or expired.
        Guid[] originalIds = [.. items.Where(i => i.SubjectKind == AvatarSubjectKind.Person && i.VariantOfItemId != null).Select(i => i.VariantOfItemId!.Value).Distinct()];
        Dictionary<Guid, AvatarLibraryItem> originals = originalIds.Length == 0 ? [] :
            await _db.AvatarLibraryItems.AsNoTracking().Where(i => i.OrganizationId == organizationId && originalIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        DateTime now = DateTime.UtcNow;
        List<AvatarLibraryItemView> views = new(items.Count);
        foreach (AvatarLibraryItem item in items)
        {
            List<AvatarLibraryReference> own = [.. references.Where(r => r.LibraryItemId == item.Id).OrderBy(r => r.Ordinal)];
            views.Add(new AvatarLibraryItemView(
                item,
                item.UnusableReason(now)
                ?? (item.VariantOfItemId is { } originalId && originals.TryGetValue(originalId, out AvatarLibraryItem? original) ? original.UnusableReason(now) : null)
                ?? (hold.HoldsAny(own) ? ProjectAvatar.ReasonReferenceHeld : null),
                own.Count(r => r.Kind == AvatarReferenceKind.Image),
                own.Count(r => r.Kind == AvatarReferenceKind.Video),
                own.Count(r => r.Kind == AvatarReferenceKind.Audio),
                PrimaryOf(item.SubjectKind, own),
                imports.Count(i => i.ItemId == item.Id),
                projectId is { } pid && imports.Any(i => i.ItemId == item.Id && i.ProjectId == pid),
                variantCounts.GetValueOrDefault(item.Id)));
        }

        return views;
    }

    private static Guid? PrimaryOf(AvatarSubjectKind kind, List<AvatarLibraryReference> ordered)
    {
        AvatarReferenceRole primary = kind == AvatarSubjectKind.Object ? AvatarReferenceRole.Front : AvatarReferenceRole.Face;
        return ordered.FirstOrDefault(r => r.Kind == AvatarReferenceKind.Image && r.Role == primary)?.Id
               ?? ordered.FirstOrDefault(r => r.Kind == AvatarReferenceKind.Image)?.Id;
    }

    // ------------------------------------------------------------------ shared checks

    /// <summary>The plan gate of the kind: <c>avatars</c> for a person, <c>avatar_objects</c> for an object.</summary>
    private async Task<AvatarRefusal?> PlanRefusalAsync(Guid organizationId, AvatarSubjectKind kind, CancellationToken ct)
    {
        if (_entitlements is null) return null;
        OrganizationEntitlementSet set = await _entitlements.GetAsync(organizationId, ct);
        PlanGateViolation? violation = kind == AvatarSubjectKind.Object ? PlanGate.CheckAvatarObjects(set) : PlanGate.CheckAvatars(set);
        return violation is null ? null : FromViolation(violation);
    }

    /// <summary>The plan's storage quota: would <paramref name="bytes"/> more still fit?</summary>
    private async Task<AvatarRefusal?> QuotaRefusalAsync(Guid organizationId, long bytes, CancellationToken ct)
    {
        if (_planGate is null || bytes <= 0) return null;
        PlanGateViolation? violation = await _planGate.CheckUploadAsync(organizationId, bytes, ct);
        return violation is null ? null : FromViolation(violation);
    }

    /// <summary>A plan refusal in the avatar routes' shape, with the plan gate's own fields beside the code and message.</summary>
    private static AvatarRefusal FromViolation(PlanGateViolation violation) =>
        new(violation.StatusCode, violation.Code, violation.Message, new Dictionary<string, object?>
        {
            ["feature"] = violation.Feature,
            ["limit"] = violation.Limit,
            ["current"] = violation.Current,
            ["incoming"] = violation.Incoming,
            ["plan"] = violation.Plan
        });

    /// <summary>
    /// The WORKSPACE statement of <paramref name="kind"/> for the locale, verbatim; for a person also the adult tick and
    /// the keep-for range. An object takes neither (<c>400 field_not_for_objects</c>). A statement refusal carries the
    /// required text, its version and both language twins, like the project routes' refusals.
    /// </summary>
    private static AvatarRefusal? AttestationRefusal(AvatarSubjectKind kind, AvatarLibraryAttestation attestation, out int retentionDays)
    {
        retentionDays = attestation.RetentionDays ?? ProjectAvatarLimits.DefaultRetentionDays;
        if (kind == AvatarSubjectKind.Object)
        {
            if (attestation.SubjectIsAdult) return NotForObjects("subjectIsAdult");
            if (attestation.RetentionDays is not null) return NotForObjects("retentionDays");
            if (AvatarWorkspaceObjectStatement.Matches(attestation.ConsentLocale, attestation.ConsentStatement)) return null;

            return AvatarRefusal.BadRequest("object_statement_required",
                "Saving an object to the workspace library requires the workspace object statement, verbatim, in the language it was shown in.",
                StatementExtra(
                    AvatarWorkspaceObjectStatement.For(attestation.ConsentLocale), attestation.ConsentLocale,
                    AvatarWorkspaceObjectStatement.Version, AvatarWorkspaceObjectStatement.English, AvatarWorkspaceObjectStatement.Italian));
        }

        if (!AvatarWorkspaceConsentStatement.Matches(attestation.ConsentLocale, attestation.ConsentStatement))
        {
            return AvatarRefusal.BadRequest("consent_statement_required",
                "Saving a person to the workspace library requires the workspace consent statement, verbatim, in the language it was shown in.",
                StatementExtra(
                    AvatarWorkspaceConsentStatement.For(attestation.ConsentLocale), attestation.ConsentLocale,
                    AvatarWorkspaceConsentStatement.Version, AvatarWorkspaceConsentStatement.English, AvatarWorkspaceConsentStatement.Italian));
        }

        if (!attestation.SubjectIsAdult)
            return AvatarRefusal.BadRequest("subject_must_be_adult", "Avatars can only be made of people who are 18 or older.");
        return retentionDays < ProjectAvatarLimits.MinRetentionDays || retentionDays > ProjectAvatarLimits.MaxRetentionDays
            ? AvatarRefusal.BadRequest("retention_out_of_range",
                $"Keep the avatar between {ProjectAvatarLimits.MinRetentionDays} and {ProjectAvatarLimits.MaxRetentionDays} days.")
            : null;
    }

    private static Dictionary<string, object?> StatementExtra(string? forLocale, string? locale, string version, string english, string italian) => new()
    {
        ["requiredStatement"] = forLocale ?? english,
        ["statementVersion"] = version,
        ["locale"] = forLocale is null ? "en" : locale,
        ["statements"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = english, ["it"] = italian }
    };

    private static AvatarRefusal NotForObjects(string field) =>
        AvatarRefusal.BadRequest("field_not_for_objects",
            $"\"{field}\" belongs to an avatar of a person and cannot be sent for an object.",
            new Dictionary<string, object?> { ["field"] = field });

    /// <summary>
    /// Read inside the organization's critical section: is the project still in <paramref name="organizationId"/>? A
    /// transfer takes the same guard, so a project that moved before the guard was granted reads as moved here, and a
    /// publish or import never writes a library copy for a project that left the organization.
    /// </summary>
    private async Task<bool> ProjectStillInAsync(Guid projectId, Guid organizationId, CancellationToken ct) =>
        await _db.Set<Project>().IgnoreQueryFilters().AsNoTracking().AnyAsync(p => p.Id == projectId && p.OrganizationId == organizationId, ct);

    private static AvatarRefusal Changed() =>
        AvatarRefusal.Conflict("AVATAR_CHANGED", "It was changed meanwhile. Reload it and try again.");

    private static string KindWord(AvatarSubjectKind kind) => kind == AvatarSubjectKind.Object ? "object" : "person";

    /// <summary>Counts per kind, for the audit events (numbers only).</summary>
    private static Dictionary<string, object> ReferenceCounts(IReadOnlyCollection<AvatarLibraryReference> references) => new()
    {
        ["references"] = references.Count,
        ["images"] = references.Count(r => r.Kind == AvatarReferenceKind.Image),
        ["videos"] = references.Count(r => r.Kind == AvatarReferenceKind.Video),
        ["audio"] = references.Count(r => r.Kind == AvatarReferenceKind.Audio)
    };

    /// <summary>The imports of an item, tracked, at most <see cref="AvatarLibraryLimits.MaxImportsPerItem"/> (plus a margin a race could leave).</summary>
    private Task<List<ProjectAvatar>> ImportsAsync(Guid itemId, CancellationToken ct) =>
        _db.ProjectAvatars
            .Where(a => a.LibraryItemId == itemId)
            .OrderBy(a => a.CreatedAt)
            .Take(AvatarLibraryLimits.MaxImportsPerItem * 2)
            .ToListAsync(ct);

    /// <summary>Removes library objects, best effort; answers how many are gone and how many stayed.</summary>
    private async Task<(int Deleted, int Failed)> DeleteLibraryObjectsAsync(IEnumerable<string> keys)
    {
        int deleted = 0, failed = 0;
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            try
            {
                // Uncancellable: these are objects no row names any more (or never will).
                await _store.DeleteAsync(key, CancellationToken.None);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Could not remove an avatar library object; the storage sweep removes an object whose item is gone");
            }
        }

        return (deleted, failed);
    }
}
