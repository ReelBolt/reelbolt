using System.Diagnostics.CodeAnalysis;

namespace ReelBolt.Shared.Editing.Color;

/// <summary>
/// The proxy derivative's variant index for a camera profile: 0 is the plain proxy, <c>1..N</c> is the
/// converted proxy for <c>CameraProfiles.All[variant - 1]</c>.
/// </summary>
/// <remarks>
/// The id order of <see cref="CameraProfiles.All"/> is FROZEN and append-only: a stored derivative
/// carries its profile in its spec (and the engine re-derives the variant from it), so reordering the
/// table would silently point old variant numbers at a different profile. A test pins the order.
/// </remarks>
public static class CameraProfileVariants
{
    /// <summary>The shared recipe version: bumped when the table or the conversion stage changes.</summary>
    public const string RecipeVersion = "camera-profile-v1";

    /// <summary>
    /// The recipe token folded into a converted proxy's spec. It names the profile <b>and</b> the decode
    /// range/matrix the stage selects, because those are baked into the proxy's argv: a profile whose
    /// published conventions change must re-key its row, not serve the old bytes under the same key.
    /// </summary>
    public static string RecipeToken(string profileId)
    {
        if (!CameraProfiles.TryGet(profileId, out CameraProfileDefinition? profile))
            throw new ArgumentException($"unknown camera profile '{profileId}'", nameof(profileId));
        return $"{RecipeVersion}:{profile.Id}:{profile.ExpectedRange}:{profile.ExpectedYuvMatrix}";
    }

    /// <summary>The variant index for a profile id, or 0 when null/unknown (the plain proxy).</summary>
    public static int VariantOf(string? profileId)
    {
        if (profileId is null) return 0;
        for (int i = 0; i < CameraProfiles.All.Count; i++)
            if (CameraProfiles.All[i].Id == profileId) return i + 1;
        return 0;
    }

    /// <summary>The profile id for a variant (<c>1..N</c>); false for 0 (plain) or out of range.</summary>
    public static bool TryProfileOf(int variant, [NotNullWhen(true)] out string? profileId)
    {
        profileId = variant >= 1 && variant <= CameraProfiles.All.Count ? CameraProfiles.All[variant - 1].Id : null;
        return profileId is not null;
    }
}
