namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// A single fully-resolved sound-effect cue, ready to be rendered into ffmpeg filter syntax by
/// <see cref="SfxMixFilterBuilder"/>. Every field here is already a safe, concrete value — a
/// locally-downloaded and ffprobe-validated clip file, a resolved linear gain, and an
/// output-timeline start second computed by mapping the cue's ANCHOR id through
/// <see cref="OutputTimeline.MapToOutputSec"/>. Nothing here is trusted from the model beyond the
/// opaque clip/anchor ids it chose (<see cref="SfxId"/>/<see cref="AnchorId"/>) — every number is
/// computed in C# by <c>VideoCompileStepExecutor.ResolveSfxAsync</c> from the model's enum-word
/// choices (Timing/Volume) or from workflow-author config. Mirrors <see cref="ResolvedMusic"/>'s
/// role for background music exactly.
/// </summary>
public sealed record ResolvedSfxCue(
    string SfxId,
    string AnchorId,
    Guid ProjectFileId,
    string ClipName,
    string LocalPath,
    /// <summary>Where on the compiled OUTPUT timeline this cue starts playing (seconds).</summary>
    double OutputStartSec,
    /// <summary>How long the cue plays — min(clip's own duration, MaxSfxCueSeconds, remaining output).</summary>
    double PlayDurationSec,
    double GainLinear,
    /// <summary>Declick fade-out at the end of the play window; already capped at half the window.</summary>
    double FadeOutSec,
    /// <summary>Timing alignment ("OnBeat", "Exact", "Lead", "Lag").</summary>
    string? Timing = null);

/// <summary>
/// A single fully-resolved voiceover line, ready to be rendered into ffmpeg filter syntax by
/// the voiceover mix builder. Every field here is already a safe, concrete value — a
/// locally-downloaded and ffprobe-validated clip file, and an output-timeline start second
/// computed by mapping the line's original time through <see cref="OutputTimeline.MapToOutputSec"/>.
/// Nothing here is trusted from the model — voiceover lines are read from a Voiceover step's
/// own output JSON, not a model decision. Mirrors <see cref="ResolvedSfxCue"/>'s role for
/// sound effects, but with full gain (no volume cap) and no duration cap.
/// </summary>
public sealed record ResolvedVoiceoverLine(
    string LineId,
    Guid ProjectFileId,
    string FileName,
    string LocalPath,
    /// <summary>Where on the compiled OUTPUT timeline this line starts playing (seconds).</summary>
    double OutputStartSec,
    /// <summary>How long the line plays (the line's original duration from the voiceover step).</summary>
    double PlayDurationSec,
    /// <summary>Full gain for voiceover (always 1.0, no attenuation).</summary>
    double GainLinear = 1.0);

/// <summary>
/// Builds the ffmpeg audio-filter fragments for resolved sound-effect cues (see
/// docs/video-editing.md "Sound effects"): each cue's own trim/format/gain/declick/delay branch
/// (<see cref="BuildCueBranch"/>), and the final <c>amix</c> stage that layers every cue over the
/// already-mixed base audio (<see cref="BuildMixStage"/>). Pure — no I/O, no ffmpeg. Mirrors
/// <see cref="MusicMixFilterBuilder"/>'s role for background music.
///
/// Deliberately NO ducking envelope here, unlike music: a cue is a short (bounded by
/// <c>MaxSfxCueSeconds</c>), deliberately-audible accent, not a continuous bed — ducking a 300ms
/// whoosh under dialogue would defeat its purpose, and the Volume word plus conservative default
/// gains are the loudness control instead. See docs/video-editing.md "Sound effects".
/// </summary>
public static class SfxMixFilterBuilder
{
    /// <summary>The internal label a cue branch outputs to: <c>[sfx0]</c>, <c>[sfx1]</c>, …</summary>
    public static string CueLabel(int index) => $"[sfx{index}]";

    /// <summary>
    /// One cue's own branch: <c>[{inputIndex}:a] … [sfx{k}]</c>. <c>asetpts=N/SR/TB</c> right
    /// after <c>atrim</c> normalizes the branch's PTS to 0 regardless of container start-time
    /// weirdness (the <see cref="MusicMixFilterBuilder.BuildMusicBranch"/> discipline);
    /// <c>aformat=…</c> is mandatory since <c>amix</c> requires matching sample rate/channel
    /// layout across inputs. <c>adelay</c> (integer milliseconds, one value per channel of the
    /// stereo layout the branch was just normalized to) is what places the cue at its resolved
    /// output-timeline moment — the one and only place a cue's timing enters the filtergraph,
    /// and it was computed entirely server-side from the anchor id.
    /// </summary>
    public static string BuildCueBranch(int inputIndex, int cueIndex, ResolvedSfxCue cue)
    {
        string dur = Num(cue.PlayDurationSec);
        string gain = Num(cue.GainLinear);
        long delayMs = (long)Math.Round(Math.Max(0, cue.OutputStartSec) * 1000.0);
        string fadeOut = Num(cue.FadeOutSec);
        string fadeOutStart = Num(Math.Max(0, cue.PlayDurationSec - cue.FadeOutSec));

        return
            $"[{inputIndex}:a]atrim=end={dur},asetpts=N/SR/TB," +
            "aformat=sample_rates=48000:channel_layouts=stereo," +
            $"volume={gain}," +
            $"afade=t=out:st={fadeOutStart}:d={fadeOut}," +
            $"adelay={delayMs}|{delayMs}{CueLabel(cueIndex)}";
    }

    /// <summary>
    /// The mix stage: <c>{baseLabel}[sfx0][sfx1]…amix=… {finalLabel}</c>. The same three
    /// load-bearing options as <see cref="MusicMixFilterBuilder.BuildMixStage"/>:
    /// <c>normalize=0</c> (without it <c>amix</c> divides every input's level by the input
    /// count, quietly reducing the dialogue/base itself), <c>duration=first</c> (pins the mixed
    /// output's length to the BASE input — the first label — so a cue near the end can never
    /// extend the file), and <c>dropout_transition=0</c> (no gain re-ramp when a cue's short
    /// branch ends long before the base does — which every cue's does).
    /// </summary>
    public static string BuildMixStage(string baseLabel, int cueCount, string finalLabel = "[aout]")
    {
        string cueLabels = string.Concat(Enumerable.Range(0, cueCount).Select(CueLabel));
        return $"{baseLabel}{cueLabels}amix=inputs={cueCount + 1}:duration=first:dropout_transition=0:normalize=0{finalLabel}";
    }

    private static string Num(double value) => FfmpegArgvFormat.Number(Math.Round(value, 5));
}

/// <summary>
/// Builds the ffmpeg audio-filter fragments for resolved voiceover lines (similar to
/// <see cref="SfxMixFilterBuilder"/>): each line's own trim/format/delay branch
/// (<see cref="BuildVoiceoverLineBranch"/>), and the final <c>amix</c> stage that layers every line
/// into the base audio (<see cref="BuildVoiceoverMixStage"/>). Pure — no I/O, no ffmpeg.
/// Mirrors <see cref="SfxMixFilterBuilder"/>'s role for sound effects.
/// </summary>
public static class VoiceoverMixFilterBuilder
{
    /// <summary>The internal label a voiceover line outputs to: <c>[vo0]</c>, <c>[vo1]</c>, …</summary>
    public static string LineLabel(int index) => $"[vo{index}]";

    /// <summary>
    /// One line's own branch: <c>[{inputIndex}:a] … [vo{k}]</c>. <c>asetpts=N/SR/TB</c> right
    /// after <c>atrim</c> normalizes the branch's PTS to 0 regardless of container start-time
    /// weirdness (same discipline as <see cref="SfxMixFilterBuilder"/>).
    /// <c>aformat=…</c> is mandatory since <c>amix</c> requires matching sample rate/channel
    /// layout across inputs. <c>adelay</c> (integer milliseconds, one value per channel of the
    /// stereo layout the branch was just normalized to) is what places the line at its resolved
    /// output-timeline moment — the one and only place a line's timing enters the filtergraph,
    /// and it was computed entirely server-side from the voiceover step's output.
    /// </summary>
    public static string BuildVoiceoverLineBranch(int inputIndex, int lineIndex, ResolvedVoiceoverLine line)
    {
        string dur = Num(line.PlayDurationSec);
        string gain = Num(line.GainLinear);
        long delayMs = (long)Math.Round(Math.Max(0, line.OutputStartSec) * 1000.0);

        return
            $"[{inputIndex}:a]atrim=end={dur},asetpts=N/SR/TB," +
            "aformat=sample_rates=48000:channel_layouts=stereo," +
            $"volume={gain}," +
            $"adelay={delayMs}|{delayMs}{LineLabel(lineIndex)}";
    }

    /// <summary>
    /// The mix stage: <c>{baseLabel}[vo0][vo1]…amix=… {finalLabel}</c>. The same three
    /// load-bearing options as <see cref="SfxMixFilterBuilder"/>:
    /// <c>normalize=0</c> (without it <c>amix</c> divides every input's level by the input
    /// count), <c>duration=first</c> (pins the mixed output's length to the BASE input — the
    /// first label), and <c>dropout_transition=0</c> (no gain re-ramp when a line's branch
    /// ends before the base does).
    /// </summary>
    public static string BuildVoiceoverMixStage(string baseLabel, int lineCount, string finalLabel = "[aout]")
    {
        string lineLabels = string.Concat(Enumerable.Range(0, lineCount).Select(LineLabel));
        return $"{baseLabel}{lineLabels}amix=inputs={lineCount + 1}:duration=first:dropout_transition=0:normalize=0{finalLabel}";
    }

    private static string Num(double value) => FfmpegArgvFormat.Number(Math.Round(value, 5));
}
