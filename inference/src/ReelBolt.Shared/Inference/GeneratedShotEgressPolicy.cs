namespace ReelBolt.Shared.Inference;

/// <summary>
/// The outcome of <see cref="GeneratedShotEgressPolicy.Evaluate"/>: whether a planned shot may be
/// submitted, and — when it may not — the stable code and human-readable reason to report.
/// </summary>
/// <param name="Allowed"><c>true</c> when the shot may be submitted to the provider.</param>
/// <param name="Code">A stable machine-readable code, or <c>null</c> when allowed.</param>
/// <param name="Reason">Why the shot was refused, or <c>null</c> when allowed.</param>
public record EgressDecision(bool Allowed, string? Code, string? Reason);

/// <summary>
/// Decides whether a planned shot may send frames of the user's OWN footage to a third-party
/// generation provider as a first/last-frame keyframe.
/// </summary>
/// <remarks>
/// <para>
/// WHY this is a refusal and not a warning: a keyframe is not metadata about the user's media — it
/// IS the user's media, and once it has been posted to a provider's API it cannot be recalled.
/// Providers' terms differ materially (some claim broad rights over inputs), so sending footage
/// anywhere is a decision only the workflow author can make, and they make it explicitly with
/// <c>VideoGenerateStepConfig.AllowSourceMediaEgress</c>. Absent that explicit grant the answer is
/// no, and the plan's shot is refused with a reason naming exactly which frames were asked for, so
/// the operator can either grant consent or edit the plan — rather than discovering after the fact
/// that their footage left the building.
/// </para>
/// <para>
/// Pure decision function: it neither uploads nor extracts a frame, and makes no provider call.
/// The step honours the verdict; this type only states it.
/// </para>
/// </remarks>
public static class GeneratedShotEgressPolicy
{
    /// <summary>
    /// The stable code reported when a keyframe-requiring shot is refused for lack of egress
    /// consent. Callers surface it verbatim (the executor emits it in the step's failure payload)
    /// so operators and tests match on one constant instead of on prose.
    /// </summary>
    public const string EgressRefusedCode = "EGRESS_REFUSED";

    /// <summary>
    /// Evaluates one shot's keyframe request against the step's egress consent.
    /// </summary>
    /// <param name="firstFrame">The plan's <c>firstFrame</c> shot id, if any. Blank counts as absent.</param>
    /// <param name="lastFrame">The plan's <c>lastFrame</c> shot id, if any. Blank counts as absent.</param>
    /// <param name="allowSourceMediaEgress">
    /// The step's explicit consent to send source media to the provider.
    /// </param>
    /// <returns>
    /// <c>(true, null, null)</c> when the shot needs no keyframe, or needs one and consent was
    /// granted; otherwise <c>(false, EGRESS_REFUSED, reason)</c>.
    /// </returns>
    public static EgressDecision Evaluate(
        string? firstFrame, string? lastFrame, bool allowSourceMediaEgress)
    {
        // Blank is treated as absent rather than as an id: a whitespace-only frame reference is not
        // something a caller can resolve to a real frame, so counting it as "needs a keyframe" would
        // refuse a shot that in fact asks for nothing.
        bool first = !string.IsNullOrWhiteSpace(firstFrame);
        bool last = !string.IsNullOrWhiteSpace(lastFrame);
        bool needsKeyframes = first || last;

        if (!needsKeyframes || allowSourceMediaEgress)
        {
            return new EgressDecision(true, null, null);
        }

        var requested = new List<string>(2);
        if (first)
        {
            requested.Add($"firstFrame '{firstFrame!.Trim()}'");
        }

        if (last)
        {
            requested.Add($"lastFrame '{lastFrame!.Trim()}'");
        }

        string reason =
            $"This shot requests source-media keyframes ({string.Join(", ", requested)}). Sending "
            + "frames of the project's own footage to a third-party video generation provider "
            + "requires AllowSourceMediaEgress to be enabled on the step. No clip was submitted.";

        return new EgressDecision(false, EgressRefusedCode, reason);
    }
}
