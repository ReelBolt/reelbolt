using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

/// <summary>
/// The one rule that says whether a library copy is held (docs/design/avatar-objects.md, "Holds reach library
/// copies"). A library copy lives outside <c>project_files</c>, so no CSAM, moderation or output-hold path can reach it
/// by row. A copy is held when EITHER
/// <list type="bullet">
/// <item>its durable marker <see cref="AvatarLibraryReference.HeldAt"/> is set (never cleared by a delete), or</item>
/// <item>any project file of the SAME organization with the same SHA-256 is quarantined by CSAM matching or moderation,
/// or is a platform-made output at or before its project's content-quarantine marker. The digest of a file is read from
/// the file row and from the avatar reference rows that attested it.</item>
/// </list>
/// The live rule is also what STAMPS the marker: a hold the gate sees is written down, and
/// <see cref="AvatarLibraryHoldMarker"/> writes it in the same save that removes a held source row, so a delete can
/// never clear a hold. A read that fails holds EVERYTHING (fail closed). Every library read path calls this class
/// (the picture route, the list and item read, import); <c>AvatarLibraryHoldTests</c> fails if one stops.
/// </summary>
internal static class AvatarLibraryHoldGate
{
    /// <summary>The answer: a set of held digests, or "everything is held" when the read failed.</summary>
    internal sealed record HoldState(bool All, IReadOnlySet<string> Digests)
    {
        public bool Holds(AvatarLibraryReference reference) =>
            All || reference.HeldAt is not null || (!string.IsNullOrEmpty(reference.ContentSha256) && Digests.Contains(reference.ContentSha256.ToLowerInvariant()));

        public bool HoldsAny(IEnumerable<AvatarLibraryReference> references) => All || references.Any(Holds);
    }

    /// <summary>Which of <paramref name="references"/> are held in <paramref name="organizationId"/>. Cancellation propagates.</summary>
    internal static async Task<HoldState> ReadAsync(
        DbContext db, Guid organizationId, IReadOnlyCollection<AvatarLibraryReference> references, ILogger logger, CancellationToken ct)
    {
        string[] wanted = [.. references.Where(r => r.HeldAt is null && !string.IsNullOrEmpty(r.ContentSha256)).Select(r => r.ContentSha256.ToLowerInvariant()).Distinct()];
        if (wanted.Length == 0) return new HoldState(false, new HashSet<string>());
        try
        {
            Dictionary<string, string> held = await AvatarLibraryHoldMarker.LiveHeldDigestsAsync(db, organizationId, wanted, ct);
            if (held.Count > 0) await AvatarLibraryHoldMarker.StampAsync(db, organizationId, held, logger, ct);
            return new HoldState(false, new HashSet<string>(held.Keys, StringComparer.Ordinal));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the holds of avatar library copies of organization {OrganizationId}; holding them all", organizationId);
            return new HoldState(true, new HashSet<string>());
        }
    }
}
