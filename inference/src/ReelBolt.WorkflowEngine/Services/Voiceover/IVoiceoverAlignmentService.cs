namespace ReelBolt.WorkflowEngine.Services.Voiceover;

/// <summary>
/// Produces per-word timings for ONE already-synthesized voiceover line WAV, by re-transcribing
/// that WAV through the existing <c>ITranscriptionClient</c> seam (Local Whisper / any
/// OpenAI-compatible ASR backend).
///
/// <para>
/// <b>Why re-transcription and not Fish Audio's native timestamps.</b> Fish's
/// <c>POST /v1/tts/stream/with-timestamp</c> returns <c>404 {"statusCode":404,…}</c> on the
/// self-hosted <c>fish-tts</c> this whole local pipeline runs against — settled by measurement —
/// while <c>ITranscriptionClient</c> already returns a real <c>words[{text,startSec,endSec}]</c>
/// array on this stack. Fish's native endpoints are therefore NEVER called from here. If a future
/// wave wants them for hosted-Fish deployments, they are a fast path <i>behind</i> this interface,
/// never a replacement: the self-hosted case must keep working.
/// </para>
/// <para>
/// ASR word boundaries are the recognizer's, not the synthesizer's, and that is the right source:
/// a caption must match what a listener actually hears.
/// </para>
/// </summary>
public interface IVoiceoverAlignmentService
{
    /// <summary>
    /// Aligns the line WAV at <paramref name="storageKey"/> (a full
    /// <c>projects/{projectId}/agentFiles/voiceover/lines/{contentKey}.wav</c> key, exactly as the
    /// voiceover step emitted it).
    ///
    /// <para>
    /// <b>This method never fails the step.</b> A missing transcription provider, a failed
    /// download, a throwing client and an empty word list each return
    /// <see cref="VoiceoverAlignmentResult.Applied"/> = <c>false</c> with a distinct reason from
    /// <see cref="VoiceoverAlignmentReasons"/>. The ONE exception is a genuine caller
    /// cancellation, which is re-thrown rather than recorded as a degraded line — the same contract
    /// as <c>GuardrailScreener.ScreenAsync</c>: a cancelled run must not be reported as "alignment
    /// did not apply".
    /// </para>
    /// </summary>
    Task<VoiceoverAlignmentResult> AlignAsync(Guid projectId, string storageKey, CancellationToken ct);
}

/// <summary>
/// One recognized word with its bounds in seconds from the start of the line's own audio. The
/// shape the voiceover step's output emits as <c>{"text":…,"startSec":…,"endSec":…}</c>.
/// </summary>
public sealed record VoiceoverAlignedWord(string Text, double StartSec, double EndSec);

/// <summary>
/// The outcome of one <see cref="IVoiceoverAlignmentService.AlignAsync"/> call. Exactly one of the
/// two shapes is ever built: applied (<see cref="Words"/> populated, <see cref="Reason"/> null) or
/// not applied (<see cref="Words"/> empty, <see cref="Reason"/> a member of
/// <see cref="VoiceoverAlignmentReasons"/>). Use the factories rather than the constructor so that
/// invariant cannot be broken by a caller.
/// </summary>
public sealed record VoiceoverAlignmentResult(
    bool Applied,
    IReadOnlyList<VoiceoverAlignedWord> Words,
    string? Reason)
{
    /// <summary>Words were produced; there is no reason to record.</summary>
    public static VoiceoverAlignmentResult Aligned(IReadOnlyList<VoiceoverAlignedWord> words) =>
        new(true, words, null);

    /// <summary>
    /// No words were produced. <paramref name="reason"/> must be one of
    /// <see cref="VoiceoverAlignmentReasons"/> — the closed set the step output and its
    /// <c>meta.alignment.reasons</c> histogram are keyed by.
    /// </summary>
    public static VoiceoverAlignmentResult NotApplied(string reason) =>
        new(false, [], reason);
}

/// <summary>
/// The CLOSED set of named reasons alignment did not apply. Closed on purpose: these strings are
/// the keys of the step output's <c>meta.alignment.reasons</c> histogram, so an operator counts
/// them and a test asserts on them. Add a member here rather than inventing a string at a call
/// site, and never reuse one reason for two different causes — the whole point of naming them
/// separately is that "the ASR server rejected the audio" and "this deployment has no ASR at all"
/// require different responses from whoever reads the output.
/// </summary>
public static class VoiceoverAlignmentReasons
{
    /// <summary>No enabled <c>Capability == Transcription</c> default row resolves — expected on a deployment that never configured ASR.</summary>
    public const string NoTranscriptionProvider = "NO_TRANSCRIPTION_PROVIDER";

    /// <summary>The line's own WAV could not be fetched from the object store.</summary>
    public const string DownloadFailed = "DOWNLOAD_FAILED";

    /// <summary>The client could not be built, or the ASR call itself threw (network, HTTP error, malformed body).</summary>
    public const string TranscriptionFailed = "TRANSCRIPTION_FAILED";

    /// <summary>The ASR call succeeded but returned no words — e.g. silence, music, or a backend configured without word timestamps.</summary>
    public const string NoWords = "NO_WORDS";

    /// <summary>Every member, so a test can assert the set is exactly these four.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        NoTranscriptionProvider,
        DownloadFailed,
        TranscriptionFailed,
        NoWords
    ];
}
