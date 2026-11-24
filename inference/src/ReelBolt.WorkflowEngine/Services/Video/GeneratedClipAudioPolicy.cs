using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>The outcome of <see cref="GeneratedClipAudioPolicy.ApplyToSources"/>.</summary>
/// <param name="AmbienceSources">Source indices whose sound plays but is not dialogue for ducking.</param>
/// <param name="Node">Per generated source <c>{sourceIndex, audio}</c>; null when the compile has no generated source with sound.</param>
public sealed record GeneratedPlateAudio(IReadOnlySet<int> AmbienceSources, JsonArray? Node);

/// <summary>
/// <c>VideoCompileStepConfig.GeneratedClipAudio</c>: whether a generated plate's own sound plays as
/// dialogue (<c>Auto</c>, today), as ambience that does not duck the music (<c>Keep</c>), or not at
/// all (<c>Mute</c>) — and the per-clip report of which it was. <b>Pure.</b>
/// </summary>
public static class GeneratedClipAudioPolicy
{
    public const string Dialogue = "dialogue";
    public const string Ambience = "ambience";
    public const string Muted = "muted";

    public static string Treatment(GeneratedAudioMode mode, bool hasAudioStream) =>
        !hasAudioStream ? Muted : mode switch
        {
            GeneratedAudioMode.Keep => Ambience,
            GeneratedAudioMode.Mute => Muted,
            _ => Dialogue
        };

    /// <summary>
    /// Source clips the VideoGenerate step produced (<paramref name="isGeneratedSource"/>) that carry sound. <c>Mute</c> clears their entry in <paramref name="sourceHasAudio"/> (exactly what
    /// <c>MuteSourceAudio</c> does for every clip); <c>Keep</c> leaves it and returns them as ambience.
    /// </summary>
    public static GeneratedPlateAudio ApplyToSources(
        GeneratedAudioMode mode, IReadOnlyList<int> usedSourceIndices, Func<int, bool> isGeneratedSource,
        IDictionary<int, bool> sourceHasAudio)
    {
        var ambience = new HashSet<int>();
        var node = new JsonArray();
        foreach (int index in usedSourceIndices)
        {
            if (!isGeneratedSource(index) ||
                !sourceHasAudio.TryGetValue(index, out bool hasAudio) || !hasAudio)
                continue;

            string treatment = Treatment(mode, true);
            if (mode == GeneratedAudioMode.Mute)
                sourceHasAudio[index] = false;
            else if (mode == GeneratedAudioMode.Keep)
                ambience.Add(index);

            node.Add(new JsonObject { ["sourceIndex"] = index, ["audio"] = treatment });
        }

        return new GeneratedPlateAudio(ambience, node.Count > 0 ? node : null);
    }

    /// <summary>
    /// Whether the programme has dialogue the music should duck under. With no ambience sources this IS
    /// <paramref name="existing"/> (byte-identical); otherwise ambience sources do not count.
    /// </summary>
    public static bool HasDuckableDialogue(
        bool existing, IReadOnlyList<int> usedSourceIndices, IReadOnlyDictionary<int, bool> sourceHasAudio,
        IReadOnlySet<int> ambienceSources, bool isMultiSource)
    {
        if (ambienceSources.Count == 0)
            return existing;

        bool Dialogue(int i) => sourceHasAudio[i] && !ambienceSources.Contains(i);
        return isMultiSource ? usedSourceIndices.Any(Dialogue) : Dialogue(usedSourceIndices[0]);
    }

    /// <summary>
    /// Applies the mode to the placed cold open / end card (<c>Mute</c> replaces the clip's sound with
    /// silence) and adds a per-clip <c>clips</c> report to the generated-clips node. Cutaways are
    /// picture only, always. Returns <paramref name="plan"/> itself when nothing is placed.
    /// </summary>
    internal static VideoCompileStepExecutor.GeneratedClipsPlan ApplyToPlacedClips(
        VideoCompileStepExecutor.GeneratedClipsPlan plan, GeneratedAudioMode mode)
    {
        if (!plan.Any || plan.Node is null)
            return plan;

        var clips = new JsonArray();
        JsonObject Entry(VideoCompileStepExecutor.ResolvedGeneratedClip c) => new()
        {
            ["clipId"] = c.ClipId,
            ["purpose"] = c.Purpose,
            ["audio"] = Treatment(mode, c.HasAudio),
            ["hasAudioStream"] = c.HasAudio
        };

        foreach (var c in plan.ColdOpens.Concat(plan.EndCards))
            clips.Add(Entry(c));
        foreach (var c in plan.Cutaways)
        {
            JsonObject entry = Entry(c);
            entry["audio"] = Muted;
            entry["reason"] = "picture_only";
            clips.Add(entry);
        }

        plan.Node["clips"] = clips;
        if (mode != GeneratedAudioMode.Mute)
            return plan;

        return plan with
        {
            ColdOpens = plan.ColdOpens.Select(c => c with { HasAudio = false }).ToList(),
            EndCards = plan.EndCards.Select(c => c with { HasAudio = false }).ToList()
        };
    }
}
