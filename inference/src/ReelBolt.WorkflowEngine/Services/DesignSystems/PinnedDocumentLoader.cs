using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Merge;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.DesignSystems;

/// <summary>
/// The documents a run's pin resolved to, with the version numbers the blocks were rendered from.
/// Either member may be null (missing, unusable, or absent from the pin).
/// </summary>
public sealed record LoadedProfile(
    JsonObject? Design,
    int DesignVersion,
    ProductMapDocument? ProductMap,
    int ProductVersion);

/// <summary>
/// Loads a run's pinned design and product versions from <c>design_system_versions</c> by id, and
/// verifies each row still hashes to the digest the pin froze (SPEC 3.5/16.1). For a design member
/// with a linked library the loader recomputes the EFFECTIVE document —
/// <see cref="DesignDocumentMerger.Inherit"/>(library, own), or the library's document when the
/// project has no approved design of its own — and verifies the pin's digest against that, so a
/// library that later approves a newer version does not change this run, while a changed pinned
/// document makes the member unusable. The digest is the only gate: the row's lifecycle
/// <c>State</c> is not consulted, so an Approved pin over a row that later became Draft stays usable
/// as long as the document is unchanged. Every failure is a warning and a null member — this loader
/// never throws and never fails a run.
/// </summary>
public sealed class PinnedDocumentLoader
{
    /// <summary>The V-1 canonical-size cap (256 KiB) a pinned document may not exceed.</summary>
    internal const int MaxCanonicalBytes = 262144;

    private const string UnusableEvent = "design_system.pin_unusable";

    private readonly WorkflowEngineDbContext _db;
    private readonly ILogger<PinnedDocumentLoader> _logger;

    public PinnedDocumentLoader(WorkflowEngineDbContext db, ILogger<PinnedDocumentLoader> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<LoadedProfile> LoadAsync(DesignSystemPin pin, CancellationToken ct)
    {
        (JsonObject? design, int designVersion) = await LoadDesignAsync(pin.Design, ct);
        (ProductMapDocument? product, int productVersion) = await LoadProductAsync(pin.ProductMap, ct);
        return new LoadedProfile(design, designVersion, product, productVersion);
    }

    private async Task<(JsonObject? Document, int VersionNumber)> LoadDesignAsync(
        DesignSystemPinMember? member,
        CancellationToken ct)
    {
        if (member is null)
            return (null, 0);

        try
        {
            (JsonObject? own, int ownNumber) = await LoadRowAsync(member.VersionId, ct);
            if (member.VersionId is not null && own is null)
                return Unusable<JsonObject>();

            (JsonObject? library, int libraryNumber) = await LoadRowAsync(member.LibraryVersionId, ct);
            if (member.LibraryVersionId is not null && library is null)
                return Unusable<JsonObject>();

            if (own is null && library is null)
                return Unusable<JsonObject>();

            // The effective document is the library's (parent) with the project's (child) layered on
            // it; a project with no own approved design is the library's document alone.
            JsonObject effective = library is null
                ? own!
                : own is null
                    ? library
                    : DesignDocumentMerger.Inherit(library, own);

            if (!VerifyEffective(effective, member.Digest))
                return Unusable<JsonObject>();

            int version = member.VersionNumber > 0
                ? member.VersionNumber
                : own is not null ? ownNumber : libraryNumber;

            return (effective, version);
        }
        catch
        {
            return Unusable<JsonObject>();
        }
    }

    private async Task<(ProductMapDocument? Document, int VersionNumber)> LoadProductAsync(
        DesignSystemPinMember? member,
        CancellationToken ct)
    {
        if (member?.VersionId is not { } versionId)
            return (null, 0);

        try
        {
            DesignSystemVersion? row = await _db.DesignSystemVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(version => version.Id == versionId, ct);

            if (row is null || !TryVerify(row, member.Digest, out _))
                return Unusable<ProductMapDocument>();

            ProductMapDocument? document = ProductMapJson.Parse(row.DocumentJson);
            if (document is null)
                return Unusable<ProductMapDocument>();

            return (document, row.VersionNumber);
        }
        catch
        {
            return Unusable<ProductMapDocument>();
        }
    }

    /// <summary>One version row's parsed document, or null when it is absent or unreadable.</summary>
    private async Task<(JsonObject? Document, int VersionNumber)> LoadRowAsync(
        Guid? versionId,
        CancellationToken ct)
    {
        if (versionId is not { } id)
            return (null, 0);

        DesignSystemVersion? row = await _db.DesignSystemVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(version => version.Id == id, ct);

        return row is null || ParseObject(row.DocumentJson) is not { } document
            ? (null, 0)
            : (document, row.VersionNumber);
    }

    private (T? Document, int VersionNumber) Unusable<T>()
    {
        _logger.LogWarning("{Event}", UnusableEvent);
        return (default, 0);
    }

    /// <summary>
    /// True when the recomputed effective document still hashes to the pin's digest and its canonical
    /// form stays within the size cap.
    /// </summary>
    private static bool VerifyEffective(JsonObject document, string digest)
    {
        try
        {
            if (!string.Equals(CanonicalJson.Digest(document), digest, StringComparison.OrdinalIgnoreCase))
                return false;

            return Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(document)) <= MaxCanonicalBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="row"/> still satisfies the pin: the stored digest matches, the
    /// document recomputes to that digest, and its canonical form stays within the size cap.
    /// </summary>
    private static bool TryVerify(DesignSystemVersion row, string digest, out JsonNode? node)
    {
        node = null;

        if (!string.Equals(row.DocumentDigest, digest, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            node = JsonNode.Parse(row.DocumentJson);
            if (node is null)
                return false;

            if (!string.Equals(CanonicalJson.Digest(node), digest, StringComparison.OrdinalIgnoreCase))
                return false;

            if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(node)) > MaxCanonicalBytes)
                return false;

            return true;
        }
        catch
        {
            node = null;
            return false;
        }
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
}
