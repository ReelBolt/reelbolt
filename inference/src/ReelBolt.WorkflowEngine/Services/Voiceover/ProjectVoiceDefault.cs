using System.Globalization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Voiceover;

/// <summary>
/// The project's one selectable voice as the DEFAULT narrator (docs/voiceover.md "The project's one voice is the
/// default"). A Voiceover step that names no voice at all (no <c>ProjectVoiceId</c>, no <c>DefaultVoiceId</c>, and the
/// run brief named none) must not quietly speak in the provider's stock voice while the project owns a selectable one:
/// <list type="bullet">
/// <item>no selectable voice: <see cref="Applies"/> is false, nothing changes (provider default, as before);</item>
/// <item>exactly one: that voice narrates (<see cref="Single"/>);</item>
/// <item>several: the step cannot know which is meant, so it fails with an actionable message
/// (<see cref="Ambiguous"/>) unless voice casting picks one.</item>
/// </list>
/// Both sources count: a <see cref="ProjectVoiceSource.Cloned"/> voice and a <see cref="ProjectVoiceSource.Library"/>
/// voice are equally selectable. "Selectable" is the resolver's own rule
/// (<see cref="ProjectVoice.IsSelectable"/>), so a revoked, expired or unattested clone is never defaulted to.
/// </summary>
public sealed record ProjectVoiceDefault(IReadOnlyList<ResolvedProjectVoice> Candidates)
{
    public static readonly ProjectVoiceDefault None = new([]);

    public bool Applies => Candidates.Count > 0;
    public ResolvedProjectVoice? Single => Candidates.Count == 1 ? Candidates[0] : null;
    public bool Ambiguous => Candidates.Count > 1;

    /// <summary>True when the step names a voice of its own, so the project default never applies.</summary>
    public static bool NamesAVoice(VoiceoverStepConfig config) =>
        config.ProjectVoiceId is not null || !string.IsNullOrWhiteSpace(config.DefaultVoiceId);

    /// <summary>
    /// The candidates for <paramref name="config"/> (already carrying the run brief's voice). <see cref="None"/> when
    /// the step names a voice or there is no resolver. Reads the same <c>ListSelectableAsync</c> the casting uses.
    /// </summary>
    public static async Task<ProjectVoiceDefault> ResolveAsync(
        IProjectVoiceResolver? resolver, Guid projectId, VoiceoverStepConfig config, CancellationToken ct)
    {
        if (resolver is null || NamesAVoice(config))
            return None;

        IReadOnlyList<ResolvedProjectVoice> selectable = await resolver.ListSelectableAsync(projectId, ct);
        return selectable.Count == 0 ? None : new ProjectVoiceDefault(selectable);
    }

    /// <summary>
    /// The step-cache component: the default voice (or the candidate set) the step would use, so adding, revoking or
    /// deleting a voice changes the key and a stale lookup misses. Null when no default applies, which keeps every
    /// key of a project without a selectable voice byte-identical to what it was.
    /// </summary>
    public string? CacheKeyComponent() =>
        !Applies
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"{(Ambiguous ? "choose" : "single")}:{string.Join(",", Candidates.Select(c => c.Id.ToString("N")).Order(StringComparer.Ordinal))}");
}
