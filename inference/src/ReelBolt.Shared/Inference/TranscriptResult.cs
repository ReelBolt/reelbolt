namespace ReelBolt.Shared.Inference;

/// <summary>
/// A single transcribed word with its absolute (never chunk-relative) timestamp bounds, in
/// seconds from the start of the original audio track. <c>Speaker</c> is the provider's opaque
/// speaker id (only meaningful within one provider response); <c>null</c> means unlabeled.
/// </summary>
public sealed record TranscriptWord(string Text, double StartSec, double EndSec, string? Speaker = null);

/// <summary>
/// A single transcribed segment (sentence/phrase granularity) with its absolute timestamp
/// bounds, in seconds from the start of the original audio track. The three optional confidence
/// figures are the recogniser's own (Whisper-family <c>verbose_json</c>: <c>no_speech_prob</c>,
/// <c>avg_logprob</c>, <c>compression_ratio</c>) when the backend reports them, else <c>null</c>
/// — <c>VideoAnalyze</c> uses them to discard segments recognised from music or silence (see
/// docs/video-editing.md "Transcripts from clips with no speech").
/// </summary>
public sealed record TranscriptSegment(
    string Text,
    double StartSec,
    double EndSec,
    double? NoSpeechProb = null,
    double? AvgLogprob = null,
    double? CompressionRatio = null,
    string? Speaker = null);

/// <summary>
/// The full result of transcribing one audio track. When the source audio was chunked (see
/// <c>TranscriptChunkPlanner</c>), <see cref="Segments"/> and <see cref="Words"/> have already
/// been offset back to absolute time and concatenated across chunks — callers never see
/// chunk-relative timestamps.
/// </summary>
public sealed record TranscriptResult(
    string Text,
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<TranscriptWord> Words,
    string? Language);
