using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>
/// The operator switches of the avatar feature (docs/design/avatars.md, docs/design/avatar-objects.md "Avatar switches
/// and plan features"), bound from <c>Avatars</c>: <see cref="Enabled"/> for avatars of PEOPLE
/// (env <c>Avatars__Enabled</c>, compose <c>AVATARS_ENABLED</c>) and <see cref="AvatarObjectOptions.Enabled"/> for
/// avatars of OBJECTS (env <c>Avatars__Objects__Enabled</c>, compose <c>AVATAR_OBJECTS_ENABLED</c>). Two switches, so
/// objects can go live before the Terms bump that person avatars wait for (decision AO-4).
/// </summary>
/// <remarks>
/// <b>A switch that cannot be read is off</b> (<c>AVATARS_ENABLED=yes</c> is false, <c>AVATARS_GENERATION=true</c> or <c>2</c>
/// is <c>Off</c>): see <see cref="Apply"/>. <b>Both off by default.</b> While neither is on, every <c>/api/v1/projects/{projectId}/avatars/**</c> route answers
/// <c>404</c> before it reads or writes anything. With one on, a row of the OTHER kind is treated as absent (not
/// listed, <c>404</c> by id), except delete, which always works on a row that exists. The switches do NOT switch off
/// the safety around rows that already exist: a file an avatar holds still cannot be deleted
/// (<c>409 FILE_IN_USE_BY_AVATAR</c>), and the retention sweep still deletes expired and revoked avatars (deleting a
/// person's likeness on time is never paused by a product switch; only its staging switch,
/// <c>ProjectAvatars:RetentionSweepEnabled</c>, stops it).
/// </remarks>
public sealed class AvatarOptions
{
    public const string SectionName = "Avatars";

    /// <summary>The configuration key of the people switch, for callers that read configuration directly.</summary>
    public const string EnabledKey = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.EnabledKey;

    /// <summary>The configuration key of the objects switch, for callers that read configuration directly.</summary>
    public const string ObjectsEnabledKey = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ObjectsEnabledKey;

    /// <summary>Avatars of people. When false (the default) a person avatar is neither listed, read, created nor changed.</summary>
    public bool Enabled { get; set; }

    /// <summary>Avatars of objects (<c>Avatars:Objects</c>).</summary>
    public AvatarObjectOptions Objects { get; set; } = new();

    /// <summary>The configuration key of <see cref="Generation"/>, for callers that read configuration directly.</summary>
    public const string GenerationKey = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.GenerationKey;

    /// <summary>
    /// Who may GENERATE a clip that shows a person (<c>Avatars:Generation</c>, env <c>Avatars__Generation</c>, compose
    /// <c>AVATARS_GENERATION</c>; default <see cref="AvatarPersonGeneration.Off"/>). The workflow engine reads the same key
    /// and is the authority at run time; the API reads it only to refuse a subject clip the engine would refuse
    /// (<c>AVATAR_GENERATION_DISABLED</c>) before anything is queued. Objects need only <see cref="AvatarObjectOptions.Enabled"/>.
    /// </summary>
    public AvatarPersonGeneration Generation { get; set; } = AvatarPersonGeneration.Off;

    /// <summary>Whether either kind is on: the avatar routes exist at all only then.</summary>
    public bool AnyEnabled => Enabled || Objects.Enabled;

    /// <summary>Whether avatars of <paramref name="subjectKind"/> are switched on.</summary>
    public bool IsEnabled(AvatarSubjectKind subjectKind) =>
        subjectKind == AvatarSubjectKind.Object ? Objects.Enabled : Enabled;

    /// <summary>Whether either switch is on, read straight from <paramref name="configuration"/> (callers without the bound options).</summary>
    public static bool AnyEnabledIn(IConfiguration configuration) =>
        ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, EnabledKey)
        || ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, ObjectsEnabledKey);

    /// <summary>
    /// The options as the three switches read: a value that cannot be read is off (a bool is false, the generation mode is
    /// <see cref="AvatarPersonGeneration.Off"/>), never an exception, and a number is not a generation mode. The engine
    /// reads the same keys through the same helper, so API and engine cannot disagree
    /// (<see cref="ReelBolt.Shared.Avatars.AvatarSwitchConfiguration"/>).
    /// </summary>
    public static void Apply(AvatarOptions options, IConfiguration configuration)
    {
        options.Enabled = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, EnabledKey);
        options.Objects.Enabled = ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, ObjectsEnabledKey);
        options.Generation = Enum.Parse<AvatarPersonGeneration>(ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadGeneration(configuration));
    }
}

/// <summary>
/// Who may generate a clip that shows a person, the API's copy of the engine's <c>AvatarGenerationMode</c> (the same
/// names, bound from the same key <c>Avatars:Generation</c>). Stored by name; append-only.
/// </summary>
public enum AvatarPersonGeneration
{
    /// <summary>Nobody (the default): a clip that shows a person is refused with <c>AVATAR_GENERATION_DISABLED</c>.</summary>
    Off,

    /// <summary>Only platform admins: the live smoke run before the plan feature is shown to customers.</summary>
    PlatformAdminOnly,

    /// <summary>Everyone the other checks let through (the launch setting).</summary>
    On
}

/// <summary>The object half of <see cref="AvatarOptions"/> (section <c>Avatars:Objects</c>).</summary>
public sealed class AvatarObjectOptions
{
    /// <summary>
    /// When false (the default) an object avatar is neither listed, read, created nor changed
    /// (env <c>AVATAR_OBJECTS_ENABLED</c>).
    /// </summary>
    public bool Enabled { get; set; }
}
