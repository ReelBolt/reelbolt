namespace ReelBolt.Shared.Inference;

/// <summary>
/// The optional voice-cloning half of a speech-synthesis backend (see docs/voiceover.md "Voices and
/// consent"). Implemented by the Fish Audio client (<c>POST /model</c> / <c>DELETE /model/{id}</c>);
/// a backend without a cloning API simply does not implement it, and the API answers
/// <c>400 cloning_not_supported</c> rather than guessing at a vendor endpoint. Deliberately a
/// separate interface from <see cref="ISpeechSynthesisClient"/>: synthesis is a per-line, per-step
/// operation any authenticated user triggers through a workflow, while creating a clone is a
/// consent-gated, project-owner action that never happens inside a step.
/// </summary>
public interface IVoiceCloningClient
{
    /// <summary>Creates a voice from one or more reference recordings and returns the vendor's id for it.</summary>
    Task<CreatedVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct);

    /// <summary>
    /// Deletes the vendor-side voice model. Returns true when the vendor confirmed the deletion
    /// (2xx or an already-gone 404), false when it refused; never throws for a vendor error, so the
    /// local row can still be removed and the outcome reported honestly.
    /// </summary>
    Task<bool> DeleteVoiceAsync(string remoteVoiceId, CancellationToken ct);

    /// <summary>
    /// What this backend needs from a clone request, asked before any recording is read so the
    /// API can refuse an unsatisfiable request with a <c>400</c>. The default is the hosted Fish
    /// Audio API's: up to 20 recordings of any audio container, no transcript. A self-hosted
    /// fish-speech server answers differently (see <see cref="FishAudioSpeechSynthesisClient"/>).
    /// May make a network call (a one-time probe of the endpoint).
    /// </summary>
    Task<VoiceCloningRequirements> GetRequirementsAsync(CancellationToken ct) =>
        Task.FromResult(VoiceCloningRequirements.Hosted);
}

/// <summary>
/// The constraints a cloning backend puts on a request: how many recordings it takes, whether each
/// must come with a transcript of what is said in it, and whether it must be a WAV file.
/// </summary>
public sealed record VoiceCloningRequirements(int MaxReferenceRecordings, bool RequiresReferenceTranscript, bool RequiresWav)
{
    /// <summary>The hosted Fish Audio API: 1–20 recordings, any container, the vendor transcribes them itself.</summary>
    public static VoiceCloningRequirements Hosted { get; } = new(20, RequiresReferenceTranscript: false, RequiresWav: false);

    /// <summary>
    /// A self-hosted fish-speech server's reference library: exactly one recording per reference
    /// id, stored as <c>sample.wav</c> next to a <c>sample.lab</c> transcript the server cannot
    /// produce itself.
    /// </summary>
    public static VoiceCloningRequirements SelfHostedFishSpeech { get; } = new(1, RequiresReferenceTranscript: true, RequiresWav: true);
}

/// <summary>
/// A reference recording for a clone: the bytes, a file name (the vendor infers the container from
/// it), its content type, and — only for a backend whose <see cref="VoiceCloningRequirements.RequiresReferenceTranscript"/>
/// is true — what is said in it.
/// </summary>
public sealed record VoiceReferenceAudio(byte[] Bytes, string FileName, string ContentType, string? Transcript = null);

/// <summary>
/// What a clone is built from. <paramref name="Title"/> is the only free text that reaches the
/// vendor and is length-capped by the API; the recordings are the project's own files.
/// </summary>
public sealed record VoiceCloneRequest(
    string Title,
    IReadOnlyList<VoiceReferenceAudio> References,
    string? Description = null);

/// <summary>The vendor's answer: its id for the new voice plus the raw state word it reported, if any.</summary>
public sealed record CreatedVoice(string RemoteVoiceId, string? State);
