namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The operation a <see cref="StepType.DesignSystem"/> step performs. Append-only: a value is
/// persisted by name alongside the step's config JSON.
/// </summary>
public enum DesignSystemOp
{
    /// <summary>Reads the project's design sources (and, optionally, its product map) into a static candidate artifact.</summary>
    Parse = 0,

    /// <summary>Merges the static candidate with optional LLM fallbacks into the run's design system artifact.</summary>
    Compose = 1,

    /// <summary>Fails the run unless the requested design system members are present and usable.</summary>
    Require = 2
}

/// <summary>
/// The allowed design-source file globs for <see cref="DesignSystemParseOptions.ExtraGlobs"/>, in
/// addition to the first-party defaults.
/// </summary>
public sealed record DesignSystemParseOptions(
    bool IncludeDesign = true,
    bool IncludeProductMap = true,
    int MaxFiles = 3000,
    int MaxBytesPerFile = 262144,
    int MaxTotalBytes = 25165824,
    IReadOnlyList<string>? ExtraGlobs = null);

/// <summary>
/// How a <see cref="DesignSystemOp.Compose"/> step resolves the design system: which parse artifact
/// it reads, and which Agent steps provide the LLM fallbacks when the static candidate is missing a
/// member.
/// </summary>
public sealed record DesignSystemComposeOptions(
    int ParseStepOrder,
    IReadOnlyList<int>? DesignFallbackStepOrders = null,
    IReadOnlyList<int>? ProductFallbackStepOrders = null,
    bool Ingest = true);

/// <summary>The members a <see cref="DesignSystemOp.Require"/> step insists on before the run continues.</summary>
public sealed record DesignSystemRequireOptions(bool RequireDesign = true, bool RequireProductMap = true);

/// <summary>
/// JSON configuration for a <see cref="StepType.DesignSystem"/> workflow step, deserialised from
/// <c>WorkflowStep.DesignSystemConfigJson</c>.
/// </summary>
[StepCard(
    "Builds the run's design system from the project's design sources, or requires the approved one.",
    ["Design sources in the project, or an approved design system"],
    "The run's design system",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["Agents and VideoCompile that read the design"],
    PairsWith = ["Agent: the authoring agents read the design"])]
public sealed record DesignSystemStepConfig(
    DesignSystemOp Op,
    int Version = 1,
    DesignSystemParseOptions? Parse = null,
    DesignSystemComposeOptions? Compose = null,
    DesignSystemRequireOptions? Require = null)
{
    public const int CurrentVersion = 1;

    public const int MinFiles = 1;
    public const int MaxFiles = 5000;
    public const int MinBytesPerFile = 1024;
    public const int MaxBytesPerFile = 1048576;
    public const int MinTotalBytes = 1048576;
    public const int MaxTotalBytes = 67108864;
    public const int MaxExtraGlobs = 20;
    public const int MaxExtraGlobLength = 100;

    private const string GlobChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_./*-";

    /// <summary>
    /// Every reason this config may not be saved or run, in field order; empty means valid. Written
    /// so a 400 body can list all of them at once. Never throws.
    /// </summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (Version != CurrentVersion)
            errors.Add($"version must be {CurrentVersion}, found {Version}.");

        if (!Enum.IsDefined(Op))
            errors.Add($"op '{Op}' is not a known design system operation.");

        switch (Op)
        {
            case DesignSystemOp.Parse:
                if (Parse is null)
                    errors.Add("parse options are required when op is Parse.");
                if (Compose is not null || Require is not null)
                    errors.Add("parse steps may not carry compose or require options.");
                break;
            case DesignSystemOp.Compose:
                if (Compose is null)
                    errors.Add("compose options are required when op is Compose.");
                if (Parse is not null || Require is not null)
                    errors.Add("compose steps may not carry parse or require options.");
                break;
            case DesignSystemOp.Require:
                if (Require is null)
                    errors.Add("require options are required when op is Require.");
                if (Parse is not null || Compose is not null)
                    errors.Add("require steps may not carry parse or compose options.");
                break;
        }

        if (Parse is { } parse)
        {
            if (parse.MaxFiles < MinFiles || parse.MaxFiles > MaxFiles)
                errors.Add($"parse.maxFiles must be between {MinFiles} and {MaxFiles}, found {parse.MaxFiles}.");
            if (parse.MaxBytesPerFile < MinBytesPerFile || parse.MaxBytesPerFile > MaxBytesPerFile)
                errors.Add($"parse.maxBytesPerFile must be between {MinBytesPerFile} and {MaxBytesPerFile}, found {parse.MaxBytesPerFile}.");
            if (parse.MaxTotalBytes < MinTotalBytes || parse.MaxTotalBytes > MaxTotalBytes)
                errors.Add($"parse.maxTotalBytes must be between {MinTotalBytes} and {MaxTotalBytes}, found {parse.MaxTotalBytes}.");

            if (parse.ExtraGlobs is { } globs)
            {
                if (globs.Count > MaxExtraGlobs)
                    errors.Add($"parse.extraGlobs may hold at most {MaxExtraGlobs} entries, found {globs.Count}.");
                for (int i = 0; i < globs.Count; i++)
                {
                    string glob = globs[i] ?? string.Empty;
                    if (glob.Length is < 1 or > MaxExtraGlobLength)
                        errors.Add($"parse.extraGlobs[{i}] must be between 1 and {MaxExtraGlobLength} characters.");
                    else if (glob.Any(c => !GlobChars.Contains(c)))
                        errors.Add($"parse.extraGlobs[{i}] may only contain letters, digits and the characters _./*-.");
                }
            }
        }

        if (Compose is { } compose)
        {
            if (compose.ParseStepOrder < 1)
                errors.Add($"compose.parseStepOrder must be at least 1, found {compose.ParseStepOrder}.");
            if (compose.DesignFallbackStepOrders is { } designOrders && designOrders.Any(order => order < 1))
                errors.Add("compose.designFallbackStepOrders entries must be at least 1.");
            if (compose.ProductFallbackStepOrders is { } productOrders && productOrders.Any(order => order < 1))
                errors.Add("compose.productFallbackStepOrders entries must be at least 1.");
        }

        if (Require is { } require && !require.RequireDesign && !require.RequireProductMap)
            errors.Add("require.requireDesign and require.requireProductMap may not both be false.");

        return errors;
    }
}
