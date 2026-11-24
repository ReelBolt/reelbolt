namespace ReelBolt.Shared.Workflows;

/// <summary>
/// One refusal of a <c>RegionEffects</c> list: a specific machine-readable <see cref="Code"/> and a
/// message that names the offending entry and the bound it broke. Deliberately not a single blanket
/// error — a customer who configured nine effects and a customer who typed a 300-character target
/// need different sentences.
/// </summary>
/// <param name="Code">One of <see cref="RegionEffectRules"/>'s code constants (SCREAMING_SNAKE, matched to <c>FailureClassifier</c>).</param>
/// <param name="Message">Human sentence naming the entry index, the value and the allowed bound.</param>
public sealed record RegionEffectViolation(string Code, string Message);

/// <summary>
/// The one place <see cref="VideoCompileStepConfig.RegionEffects"/> is validated, shared by the API's
/// save path and the engine's compile-time check so both refuse the same list for the same reason.
/// Pure: no I/O, no exceptions, every rule produces its own code.
///
/// <para>
/// <b>All-or-nothing, never a silent drop.</b> A list with nine entries is refused outright: applying
/// the first eight and quietly ignoring the ninth would publish a picture whose effect the customer
/// asked for is missing, which is exactly the "silently ships the wrong output" failure the
/// prompted-region-effects initiative is written against.
/// </para>
/// </summary>
public static class RegionEffectRules
{
    /// <summary>Most effect entries one compile may carry — each is one mask input and one filter chain.</summary>
    public const int MaxEffects = 8;

    /// <summary>Longest allowed <see cref="VideoRegionEffect.Target"/>, in characters.</summary>
    public const int MaxTargetChars = 120;

    /// <summary>Smallest allowed <see cref="VideoRegionEffect.Strength"/>.</summary>
    public const double MinStrength = 0;

    /// <summary>Largest allowed <see cref="VideoRegionEffect.Strength"/>.</summary>
    public const double MaxStrength = 2;

    /// <summary>Largest allowed <see cref="VideoRegionEffect.FeatherPx"/>, at output resolution.</summary>
    public const double MaxFeatherPx = 64;

    /// <summary>Smallest allowed <see cref="VideoRegionEffect.Opacity"/>.</summary>
    public const double MinOpacity = 0;

    /// <summary>Largest allowed <see cref="VideoRegionEffect.Opacity"/>.</summary>
    public const double MaxOpacity = 1;

    /// <summary>More than <see cref="MaxEffects"/> entries (never truncated to fit).</summary>
    public const string LimitExceededCode = "EFFECT_LIMIT_EXCEEDED";

    /// <summary><see cref="VideoRegionEffect.Effect"/> is not a defined <see cref="RegionEffect"/> member.</summary>
    public const string InvalidCode = "EFFECT_INVALID";

    /// <summary>A blank target, or one longer than <see cref="MaxTargetChars"/>.</summary>
    public const string TargetInvalidCode = "EFFECT_TARGET_INVALID";

    /// <summary><see cref="VideoRegionEffect.Strength"/> outside <see cref="MinStrength"/>..<see cref="MaxStrength"/>/NaN.</summary>
    public const string StrengthOutOfRangeCode = "EFFECT_STRENGTH_OUT_OF_RANGE";

    /// <summary><see cref="VideoRegionEffect.FeatherPx"/> outside 0..<see cref="MaxFeatherPx"/>/NaN.</summary>
    public const string FeatherOutOfRangeCode = "EFFECT_FEATHER_OUT_OF_RANGE";

    /// <summary><see cref="VideoRegionEffect.Opacity"/> outside <see cref="MinOpacity"/>..<see cref="MaxOpacity"/>/NaN.</summary>
    public const string OpacityOutOfRangeCode = "EFFECT_OPACITY_OUT_OF_RANGE";

    /// <summary>Effects configured on a <see cref="VideoCompileMode.StreamCopy"/> compile, which has no video filtergraph.</summary>
    public const string RequiresReencodeCode = "EFFECT_REQUIRES_REENCODE";

    /// <summary><see cref="VideoRegionEffect.FromEventId"/> or <see cref="VideoRegionEffect.ToEventId"/> is not an event id (e0, e12, ...).</summary>
    public const string WindowEventInvalidCode = "EFFECT_WINDOW_EVENT_INVALID";

    /// <summary>
    /// Every violation of <paramref name="effects"/> under <paramref name="mode"/>, in entry order then
    /// rule order. Empty means the list is valid (and an absent/null list is valid: it is the
    /// byte-identical default).
    /// </summary>
    public static IReadOnlyList<RegionEffectViolation> Validate(
        IReadOnlyList<VideoRegionEffect>? effects,
        VideoCompileMode mode)
    {
        if (effects is not { Count: > 0 }) return [];

        List<RegionEffectViolation> violations = [];

        if (effects.Count > MaxEffects)
        {
            violations.Add(new RegionEffectViolation(
                LimitExceededCode,
                $"RegionEffects has {effects.Count} entries; at most {MaxEffects} are allowed "
                + $"(each one is a mask input and a filter chain). Remove {effects.Count - MaxEffects}."));
        }

        for (int i = 0; i < effects.Count; i++)
        {
            VideoRegionEffect? effect = effects[i];
            if (effect is null)
            {
                violations.Add(new RegionEffectViolation(
                    InvalidCode, $"RegionEffects[{i}] is null; every entry must name a target and an effect."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(effect.Target))
            {
                violations.Add(new RegionEffectViolation(
                    TargetInvalidCode,
                    $"RegionEffects[{i}].target is blank; name a g{{n}} track id or the prompt label it was tracked under."));
            }
            else if (effect.Target.Length > MaxTargetChars)
            {
                violations.Add(new RegionEffectViolation(
                    TargetInvalidCode,
                    $"RegionEffects[{i}].target is {effect.Target.Length} characters; at most {MaxTargetChars} are allowed."));
            }

            if (!Enum.IsDefined(effect.Effect))
            {
                violations.Add(new RegionEffectViolation(
                    InvalidCode,
                    $"RegionEffects[{i}].effect is {(int)effect.Effect}, which is not a defined effect. "
                    + $"Use one of: {string.Join(", ", Enum.GetNames<RegionEffect>())}."));
            }

            if ((effect.FromEventId is not null && !EventMoveRequest.IsEventId(effect.FromEventId)) ||
                (effect.ToEventId is not null && !EventMoveRequest.IsEventId(effect.ToEventId)))
            {
                violations.Add(new RegionEffectViolation(
                    WindowEventInvalidCode,
                    $"RegionEffects[{i}] fromEventId/toEventId must look like e0 or e12 (an event id from the analysis)."));
            }

            if (double.IsNaN(effect.Strength) || effect.Strength < MinStrength || effect.Strength > MaxStrength)
            {
                violations.Add(new RegionEffectViolation(
                    StrengthOutOfRangeCode,
                    $"RegionEffects[{i}].strength is {effect.Strength}; it must be between {MinStrength} and {MaxStrength}."));
            }

            if (double.IsNaN(effect.FeatherPx) || effect.FeatherPx < 0 || effect.FeatherPx > MaxFeatherPx)
            {
                violations.Add(new RegionEffectViolation(
                    FeatherOutOfRangeCode,
                    $"RegionEffects[{i}].featherPx is {effect.FeatherPx}; it must be between 0 and {MaxFeatherPx} pixels."));
            }

            if (double.IsNaN(effect.Opacity) || effect.Opacity < MinOpacity || effect.Opacity > MaxOpacity)
            {
                violations.Add(new RegionEffectViolation(
                    OpacityOutOfRangeCode,
                    $"RegionEffects[{i}].opacity is {effect.Opacity}; it must be between {MinOpacity} and {MaxOpacity}."));
            }
        }

        // Evaluated last so a structurally broken list reports the structural problem first; it is a
        // hard config error for the same reason CENSOR_REQUIRES_REENCODE is — stream-copy cannot draw
        // into the picture, and publishing it un-effected would be the silent wrong output.
        if (mode == VideoCompileMode.StreamCopy)
        {
            violations.Add(new RegionEffectViolation(
                RequiresReencodeCode,
                $"RegionEffects requires Mode=Reencode — masking an effect onto the picture has no stream-copy equivalent "
                + $"({effects.Count} entr{(effects.Count == 1 ? "y" : "ies")} configured)."));
        }

        return violations;
    }
}
