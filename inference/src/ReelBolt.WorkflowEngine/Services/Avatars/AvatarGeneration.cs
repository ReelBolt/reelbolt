using ReelBolt.Shared.Avatars;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

/// <summary>Who may generate a clip with an avatar in this deployment (avatars phase 2). Stored by name; append-only.</summary>
public enum AvatarGenerationMode
{
    /// <summary>Nobody: a step with an avatar fails <c>AVATAR_GENERATION_DISABLED</c> before anything is reserved. Default.</summary>
    Off,

    /// <summary>
    /// Only runs started by a platform admin: the live smoke run of docs/design/avatars.md "Phased delivery", made
    /// before the plan feature is shown to customers. Every other run fails <c>AVATAR_GENERATION_DISABLED</c>.
    /// </summary>
    PlatformAdminOnly,

    /// <summary>Every run whose project has a usable avatar (the launch setting).</summary>
    On
}

/// <summary>
/// The engine's avatar switches, section <c>Avatars</c> (the same section and <c>Enabled</c> key the Inference API's
/// kill switch reads, so one <c>AVATARS_ENABLED</c> turns avatars of people off everywhere). Generating with a PERSON
/// needs BOTH <see cref="Enabled"/> and a <see cref="Generation"/> mode other than <see cref="AvatarGenerationMode.Off"/>.
/// Generating with an OBJECT (docs/design/avatar-objects.md, decision AO-4) needs only <see cref="ObjectsEnabled"/>.
/// </summary>
public sealed class AvatarGenerationOptions
{
    public const string SectionName = "Avatars";

    /// <summary>The feature kill switch (env <c>AVATARS_ENABLED</c>, default false).</summary>
    public bool Enabled { get; set; }

    /// <summary>Who may generate with an avatar (env <c>AVATARS_GENERATION</c>, default <see cref="AvatarGenerationMode.Off"/>).</summary>
    public AvatarGenerationMode Generation { get; set; } = AvatarGenerationMode.Off;

    /// <summary>The configuration key <see cref="ObjectsEnabled"/> is read from (it is nested, so it is bound by hand).</summary>
    public const string ObjectsEnabledKey = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ObjectsEnabledKey;

    /// <summary>
    /// The object switch (<c>Avatars:Objects:Enabled</c>, env <c>AVATAR_OBJECTS_ENABLED</c>, default false): a clip
    /// that shows an object avatar may be generated. Independent of <see cref="Enabled"/> and <see cref="Generation"/>,
    /// which gate people.
    /// </summary>
    public bool ObjectsEnabled { get; set; }

    /// <summary>
    /// Whether a subject of <paramref name="subjectKind"/> may be generated at all in this deployment (the
    /// platform-admin smoke mode of a person is a further check on the run's initiator).
    /// </summary>
    public bool AllowsGeneration(AvatarSubjectKind subjectKind) =>
        subjectKind == AvatarSubjectKind.Object ? ObjectsEnabled : Enabled && Generation != AvatarGenerationMode.Off;

    /// <summary>
    /// Sets <paramref name="options"/> from the three switches, the way the Inference API reads them: a value that cannot
    /// be read is off (a bool is false, the mode is <see cref="AvatarGenerationMode.Off"/>), a number is not a mode, nothing
    /// throws.
    /// </summary>
    public static void Apply(AvatarGenerationOptions options, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        options.Enabled = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.EnabledKey);
        options.ObjectsEnabled = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, ObjectsEnabledKey);
        options.Generation = Enum.Parse<AvatarGenerationMode>(ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadGeneration(configuration));
    }
}

/// <summary>
/// The identity of an avatar AS IT IS NOW, for every "may this old clip be reused" decision (docs/design/avatars.md
/// "Step cache and reuse"): the avatar id, the moment consent was attested (a re-attestation is a new consent) and
/// every reference's kind, role, order and attested digest. A revoked, expired or changed avatar never produces the
/// fingerprint a clip was made under, so the request hash, the step-cache key and the reuse guard all miss.
/// <para>
/// <b>A person's fingerprint never moves</b> (<c>AvatarFingerprintStabilityTests</c> pins it): the subject kind joins
/// the hashed fields ONLY for an object (<c>kind=object</c>), so every clip, ledger row and cache entry made with a
/// person before objects existed still matches.
/// </para>
/// </summary>
public static class AvatarFingerprint
{
    /// <summary>Hex SHA-256 over length-prefixed fields; never contains the subject's name or a storage key.</summary>
    public static string Compute(ResolvedAvatar avatar)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        var sb = new StringBuilder();
        Append(sb, "avatar", avatar.Id.ToString("N"));
        if (avatar.SubjectKind == AvatarSubjectKind.Object)
            Append(sb, "kind", "object");
        // An object's identity facts join only when it has some, so every fingerprint made before they existed still matches.
        if (avatar.SubjectKind == AvatarSubjectKind.Object && avatar.IdentityFacts?.ToJson() is { } facts)
            Append(sb, "facts", facts);
        Append(sb, "consent", avatar.ConsentAttestedAt?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "-");
        Append(sb, "disclosure", avatar.Disclosure.ToString());
        foreach (ResolvedAvatarReference r in avatar.References.OrderBy(r => r.Ordinal).ThenBy(r => r.Id))
        {
            Append(sb, "ref", $"{r.Ordinal}:{r.Kind}:{r.Role}:{r.ContentSha256}");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder sb, string name, string value) =>
        sb.Append(name.Length).Append(':').Append(name).Append('=').Append(value.Length).Append(':').Append(value).Append('\n');
}

/// <summary>
/// The avatar facts a <c>VideoGenerate</c> result records (<c>meta.avatar</c> in the output and the manifest, and
/// <c>avatarFingerprint</c> on each clip), and the one reader of them, so the writer and every reuse check agree.
/// </summary>
public static class AvatarOutputFacts
{
    public const string MetaKey = "avatar";
    public const string ClipFingerprintKey = "avatarFingerprint";

    /// <summary>The avatar id and fingerprint a recorded VideoGenerate output was made under, or null when it used none.</summary>
    /// <returns>(WellFormed, AvatarId, Fingerprint): a non-blank output that is not a JSON object is not well formed.</returns>
    public static (bool WellFormed, Guid? AvatarId, string? Fingerprint) Read(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return (true, null, null);
        try
        {
            if (JsonNode.Parse(outputJson) is not JsonObject root)
                return (false, null, null);
            if (root["meta"] is not JsonObject meta || meta[MetaKey] is not JsonObject avatar)
                return (true, null, null);
            Guid? id = Guid.TryParse(avatar["avatarId"]?.GetValue<string>(), out Guid parsed) ? parsed : null;
            string? fingerprint = avatar["fingerprint"]?.GetValue<string>();
            return id is null || string.IsNullOrEmpty(fingerprint) ? (false, null, null) : (true, id, fingerprint);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return (false, null, null);
        }
    }
}
