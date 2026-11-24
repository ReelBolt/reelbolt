using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Merge;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// Reads the project's <c>design_systems</c> rows joined to their <c>current_version</c> and pins the
/// ones that are <see cref="DesignVersionState.Approved"/>. When the project's design system descends
/// from an organization library (SPEC 16.1), the member additionally freezes the library's approved
/// version id and the digest of the <em>effective</em> (library + project) document, so the run stays
/// reproducible even after the library approves a newer version.
/// </summary>
public sealed class DesignSystemPinService(InferenceApiDbContext db) : IDesignSystemPinService
{
    /// <inheritdoc />
    public async Task<string?> BuildPinJsonAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            DesignSystem? designSystem = await db.DesignSystems
                .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.Design, ct);
            DesignSystemVersion? ownDesign = await CurrentApprovedAsync(designSystem, ct);
            LinkedLibraryVersion? library = await LoadLinkedLibraryAsync(designSystem, ct);

            DesignSystem? productSystem = await db.DesignSystems
                .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.ProductMap, ct);
            DesignSystemVersion? ownProductMap = await CurrentApprovedAsync(productSystem, ct);

            DesignSystemPinMember? design = BuildDesignMember(ownDesign, library);
            DesignSystemPinMember? productMap = ownProductMap is null
                ? null
                : new DesignSystemPinMember(
                    ownProductMap.Id,
                    ownProductMap.VersionNumber,
                    ownProductMap.DocumentDigest,
                    LibraryVersionId: null,
                    Source: DesignSystemPinSources.Project);

            if (design is null && productMap is null)
            {
                return null;
            }

            return DesignSystemPinJson.Serialize(new DesignSystemPin(1, design, productMap));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The pin is a snapshot, never a gate on queueing: a read failure degrades to "no pin"
            // rather than stopping the run. Never throw.
            return null;
        }
    }

    /// <summary>
    /// Builds the design member: without a linked library the project's own approved version is the
    /// whole document and its stored digest is the effective digest; with one, the effective document
    /// is <see cref="DesignDocumentMerger.Inherit"/> of the library's approved document (parent) and
    /// the project's (child), and the member freezes that recomputed digest plus both version ids.
    /// A library approved version alone is a <c>library</c>-sourced member with no own version id.
    /// </summary>
    private static DesignSystemPinMember? BuildDesignMember(
        DesignSystemVersion? own, LinkedLibraryVersion? library)
    {
        if (own is null && library is null)
            return null;

        JsonObject? ownDocument = own is null ? null : ParseObject(own.DocumentJson);
        JsonObject? libraryDocument = library is null ? null : ParseObject(library.DocumentJson);

        if (library is null || libraryDocument is null)
        {
            // No library (or an unreadable library document): the project's own approved version is
            // the whole document, exactly as in Phase 2.
            return own is null || ownDocument is null
                ? null
                : new DesignSystemPinMember(
                    own.Id, own.VersionNumber, own.DocumentDigest, null, DesignSystemPinSources.Project);
        }

        if (own is null || ownDocument is null)
        {
            // Library only: the effective document is the library's own, so its recomputed digest is
            // the pin's (never a possibly stale stored digest).
            return new DesignSystemPinMember(
                null,
                library.VersionNumber,
                CanonicalJson.Digest(libraryDocument),
                library.VersionId,
                DesignSystemPinSources.Library);
        }

        JsonObject effective = DesignDocumentMerger.Inherit(libraryDocument, ownDocument);
        return new DesignSystemPinMember(
            own.Id,
            own.VersionNumber,
            CanonicalJson.Digest(effective),
            library.VersionId,
            DesignSystemPinSources.ProjectAndLibrary);
    }

    /// <summary>The system's current version only while it is Approved, else null.</summary>
    private async Task<DesignSystemVersion?> CurrentApprovedAsync(DesignSystem? system, CancellationToken ct)
    {
        if (system?.CurrentVersionId is not { } versionId)
            return null;

        return await db.DesignSystemVersions
            .FirstOrDefaultAsync(
                v => v.Id == versionId && v.DesignSystemId == system.Id && v.State == DesignVersionState.Approved,
                ct);
    }

    /// <summary>
    /// The linked library's own approved version, resolved from the library row's document container.
    /// Null when the system names no library or the library has no approved version.
    /// </summary>
    private async Task<LinkedLibraryVersion?> LoadLinkedLibraryAsync(DesignSystem? system, CancellationToken ct)
    {
        if (system?.ParentLibraryId is not { } libraryId)
            return null;

        var row = await (
            from l in db.DesignSystemLibraries
            join c in db.DesignSystems on l.DesignSystemId equals c.Id
            join v in db.DesignSystemVersions on c.CurrentVersionId equals (Guid?)v.Id
            where l.Id == libraryId && v.State == DesignVersionState.Approved
            select new { v.Id, v.VersionNumber, v.DocumentJson })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new LinkedLibraryVersion(row.Id, row.VersionNumber, row.DocumentJson);
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One linked library's approved version, as the pin needs it.</summary>
    private sealed record LinkedLibraryVersion(Guid VersionId, int VersionNumber, string DocumentJson);
}

/// <summary>SPEC 16.1: which document(s) a pinned member's effective document came from.</summary>
internal static class DesignSystemPinSources
{
    public const string Project = "project";
    public const string Library = "library";
    public const string ProjectAndLibrary = "project+library";
}
