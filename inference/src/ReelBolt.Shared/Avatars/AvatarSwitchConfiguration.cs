using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ReelBolt.Shared.Avatars;

/// <summary>
/// The one reader of the three avatar operator switches, used by BOTH the Inference API and the workflow engine so the
/// two cannot disagree about a value that is not what it should be (docs/design/avatar-objects.md "Deviations recorded
/// at build time"). The rule: a switch that cannot be read is OFF. An unreadable bool is <c>false</c>; an unreadable
/// <c>Avatars:Generation</c> is <c>Off</c> (generating clips of people refused). A number is not a member of the
/// generation list (<c>2</c> is not <c>On</c>), and <c>yes</c> or <c>1</c> is not a bool. Nothing here throws, so a
/// mistyped environment variable can never turn a picture operation into a 500; <see cref="LogProblems"/> says once, at
/// startup, which key was not understood and what it accepts.
/// </summary>
public static class AvatarSwitchConfiguration
{
    /// <summary>The people switch (env <c>AVATARS_ENABLED</c>).</summary>
    public const string EnabledKey = "Avatars:Enabled";

    /// <summary>The objects switch (env <c>AVATAR_OBJECTS_ENABLED</c>).</summary>
    public const string ObjectsEnabledKey = "Avatars:Objects:Enabled";

    /// <summary>Who may generate a clip that shows a person (env <c>AVATARS_GENERATION</c>).</summary>
    public const string GenerationKey = "Avatars:Generation";

    /// <summary>The values <see cref="GenerationKey"/> accepts (letters only; a number is refused).</summary>
    public static readonly IReadOnlyList<string> GenerationValues = ["Off", "PlatformAdminOnly", "On"];

    /// <summary>The values a bool switch accepts.</summary>
    public static readonly IReadOnlyList<string> BoolValues = ["true", "false"];

    /// <summary>The bool at <paramref name="key"/>; unset or unreadable is false.</summary>
    public static bool ReadBool(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? raw = configuration[key];
        return bool.TryParse(raw, out bool value) && value;
    }

    /// <summary>The canonical name (one of <see cref="GenerationValues"/>) at <see cref="GenerationKey"/>; unset or unreadable is <c>Off</c>.</summary>
    public static string ReadGeneration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? raw = configuration[GenerationKey]?.Trim();
        if (string.IsNullOrEmpty(raw)) return GenerationValues[0];
        foreach (string accepted in GenerationValues)
        {
            if (string.Equals(raw, accepted, StringComparison.OrdinalIgnoreCase)) return accepted;
        }

        return GenerationValues[0];
    }

    /// <summary>One sentence per switch whose value is set but cannot be read (empty when every switch is fine or unset).</summary>
    public static IReadOnlyList<string> Problems(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        List<string> problems = [];
        foreach (string key in new[] { EnabledKey, ObjectsEnabledKey })
        {
            string? raw = configuration[key];
            if (!string.IsNullOrWhiteSpace(raw) && !bool.TryParse(raw, out _))
            {
                problems.Add($"{key} is \"{raw}\", which is not a yes/no value. Accepted: {string.Join(", ", BoolValues)}. It is treated as false (off).");
            }
        }

        string? generation = configuration[GenerationKey];
        if (!string.IsNullOrWhiteSpace(generation)
            && !GenerationValues.Any(v => string.Equals(generation.Trim(), v, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"{GenerationKey} is \"{generation}\", which is not a known mode. Accepted: {string.Join(", ", GenerationValues)} (not a number). It is treated as Off: clips of people are refused.");
        }

        return problems;
    }

    /// <summary>Logs every <see cref="Problems"/> sentence at Error. Called once, at startup, by each host.</summary>
    public static void LogProblems(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        foreach (string problem in Problems(configuration)) logger.LogError("Avatar switch not understood: {Problem}", problem);
    }
}
