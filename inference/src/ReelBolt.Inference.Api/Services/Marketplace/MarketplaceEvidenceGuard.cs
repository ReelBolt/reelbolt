using System.Text.Json.Nodes;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// CSAM evidence that could not be moved into <c>quarantine/</c> stays at its staging key and is recorded on the version as
/// <c>check_json.evidence = "staged"</c> with <c>evidenceKeys</c>. CONTRACT: every code path that deletes marketplace objects (prefix or key
/// deletes, the finalizer's stale-nonce cleanup, the sweeper, the safety sweep and the retry of an admin removal, account purge) must
/// consult this guard first and must neither delete a held version's objects nor any key in <see cref="HeldKeys"/>; a held version's
/// objects are released only by legal review. Quarantined keys are protected separately by <c>MarketplacePreviewKeys.IsQuarantine</c>.
/// </summary>
public static class MarketplaceEvidenceGuard
{
    public const string EvidenceKey = "evidence";
    public const string KeysKey = "evidenceKeys";
    public const string Staged = "staged";
    public const string Quarantined = "quarantined";

    /// <summary>True when the version's check json says evidence is held outside quarantine/.</summary>
    public static bool IsHeld(string? checkJson) =>
        string.Equals(Parse(checkJson)?[EvidenceKey]?.GetValue<string>(), Staged, StringComparison.Ordinal);

    /// <summary>The object keys held as evidence (empty when none).</summary>
    public static IReadOnlySet<string> HeldKeys(string? checkJson)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        if (Parse(checkJson)?[KeysKey] is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item?.GetValue<string>() is { Length: > 0 } key)
                    keys.Add(key);
            }
        }

        return keys;
    }

    private static JsonObject? Parse(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
