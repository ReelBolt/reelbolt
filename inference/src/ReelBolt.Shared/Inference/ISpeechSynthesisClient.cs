namespace ReelBolt.Shared.Inference;

/// <summary>
/// ReelBolt's own abstraction over a text-to-speech (TTS) backend. Deliberately not a
/// passthrough of any vendor SDK: callers must not need to know the underlying implementation
/// details, making this step executor unit-testable with a mocked implementation.
/// </summary>
public interface ISpeechSynthesisClient
{
    /// <summary>
    /// Synthesizes speech from a text request and returns the audio as a WAV stream.
    /// </summary>
    Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct);
}

/// <summary>
/// A request to synthesize speech from text.
/// </summary>
/// <param name="Text">The SANITIZED words to speak.</param>
/// <param name="VoiceId">The provider's voice/reference id, or null for the provider default.</param>
/// <param name="ModelOverride">A per-request model, or null for the client's own.</param>
/// <param name="Pronunciation">
/// Optional pronunciation entries (see docs/voiceover.md "Pronunciation dictionary"): each maps a
/// substring of <paramref name="Text"/> to the phoneme form the engine should read it as. Built by
/// first-party code from a fixed table, never by a model; null/empty means none, which is the
/// byte-identical phase-1 request. APPENDED with a default so every existing call site compiles.
/// </param>
/// <param name="Language">
/// The language the text is in (BCP-47 tag from the step or the run brief), or null. The Fish Audio client sends it
/// (see <see cref="FishLanguageTag"/>) so Fish reads numbers and dates in that language; it does not change stress.
/// The OpenAI-compatible client ignores it. For Fish it also joins the per-line content key.
/// </param>
/// <param name="Speed">
/// Speaking rate multiplier (0.5..2.0) or null for the provider's normal rate. Only the Fish client uses it
/// (<c>prosody.speed</c>); null leaves the wire request byte-identical. APPENDED LAST with a null default.
/// </param>
public sealed record SpeechRequest(
    string Text,
    string? VoiceId,
    string? ModelOverride,
    IReadOnlyList<PronunciationEntry>? Pronunciation = null,
    string? Language = null,
    double? Speed = null);

/// <summary>One pronunciation-dictionary entry: the source substring and its phonetic (IPA) replacement.</summary>
/// <param name="Key">The source substring to match in the text.</param>
/// <param name="Value">The phonetic (IPA) form the engine reads instead.</param>
/// <param name="CaseSensitive">Match case-sensitively (short all-caps acronyms); false applies ASCII case folding.</param>
public sealed record PronunciationEntry(string Key, string Value, bool CaseSensitive = false);

/// <summary>
/// The result of a speech synthesis operation: a WAV audio stream and its content type.
/// </summary>
public sealed record SynthesizedSpeech(Stream Wav, string ContentType);
