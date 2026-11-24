using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Voiceover;

/// <summary>
/// The default <see cref="IVoiceoverAlignmentService"/>: resolve a transcription provider, download
/// the line's content-addressed WAV to scratch, re-transcribe it with
/// <c>wordTimestamps: true</c>, and map the returned <see cref="TranscriptWord"/> list to
/// <see cref="VoiceoverAlignedWord"/>.
///
/// <para>
/// <b>Never throws, except on cancellation.</b> Every failure path returns
/// <see cref="VoiceoverAlignmentResult.NotApplied"/> with a distinct member of
/// <see cref="VoiceoverAlignmentReasons"/>, because this runs inside the voiceover step's per-line
/// loop: an exception here would cost the line its audio, and word timings are an addition to a
/// line that is otherwise perfectly usable. The one thing that IS re-thrown is a genuine caller
/// cancellation (<c>catch (OperationCanceledException) when (ct.IsCancellationRequested)</c> — the
/// <c>GuardrailScreener.ScreenAsync</c> contract): recording a stopped run as "alignment did not
/// apply" would report an operator action as an infrastructure fact.
/// </para>
/// <para>
/// <b>Stateless and singleton-safe.</b> No field is written after construction and no state is
/// carried between calls; <see cref="VoiceoverStepExecutor"/> — which consumes this — is itself an
/// <c>AddSingleton</c>, so a per-execution cache or accumulator living here would leak across
/// executions (see that class's remarks on the in-memory artifact cache that was deleted for
/// exactly this reason).
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It does not pass a <c>language</c> hint — the WAV's
/// words are whatever the synthesizer was told to say, and a wrong hint degrades recognition — and
/// it does not filter, re-order, re-base or de-duplicate the recognized words. The word list is
/// reported as the recognizer produced it, so a downstream caption/fit consumer can see what was
/// actually heard rather than a cleaned-up version of it.
/// </para>
/// </summary>
public sealed class VoiceoverAlignmentService : IVoiceoverAlignmentService
{
    private readonly IInferenceProviderResolver _providerResolver;
    private readonly ITranscriptionClientFactory _transcriptionClientFactory;
    private readonly IProjectFileWorkspace _workspace;
    private readonly ILogger<VoiceoverAlignmentService> _logger;

    public VoiceoverAlignmentService(
        IInferenceProviderResolver providerResolver,
        ITranscriptionClientFactory transcriptionClientFactory,
        IProjectFileWorkspace workspace,
        ILogger<VoiceoverAlignmentService> logger)
    {
        _providerResolver = providerResolver;
        _transcriptionClientFactory = transcriptionClientFactory;
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<VoiceoverAlignmentResult> AlignAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        // A line with no object to align is a download failure by definition: the step only calls
        // this for a line whose storageKey is present, so reaching here means the caller's own
        // invariant broke, and "absent words with a reason" is still the only honest answer.
        if (string.IsNullOrWhiteSpace(storageKey))
            return VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.DownloadFailed);

        // Precedence is the documented one: no explicit id here, so the single enabled
        // Capability == Transcription && IsDefault row, and nothing else — deliberately no legacy
        // AzureOpenAI:* fallback, since those keys name a CHAT deployment and silently posting
        // audio to one would fail confusingly rather than degrade.
        ResolvedTranscriptionProvider? provider;
        try
        {
            provider = await _providerResolver.ResolveTranscriptionAsync(null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A resolver that throws is still "no provider available" from this caller's point of
            // view — the step needs a reason, not an exception.
            _logger.LogDebug(ex, "Transcription provider resolution failed while aligning a voiceover line");
            return VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.NoTranscriptionProvider);
        }

        if (provider is null)
        {
            _logger.LogDebug(
                "No Transcription provider is configured; voiceover lines will carry no word timings");
            return VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.NoTranscriptionProvider);
        }

        string scratchPath = Path.Combine(Path.GetTempPath(), $"voiceover_align_{Guid.NewGuid():N}.wav");
        try
        {
            try
            {
                await _workspace.DownloadStorageKeyToFileAsync(projectId, storageKey, scratchPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Distinct from TRANSCRIPTION_FAILED on purpose: "the object store did not give us
                // the file" and "the ASR server rejected the file" have different remedies.
                _logger.LogWarning(ex, "Could not download voiceover line artifact {StorageKey} for alignment", storageKey);
                return VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.DownloadFailed);
            }

            ITranscriptionClient client;
            TranscriptResult transcript;
            try
            {
                client = _transcriptionClientFactory.Get(provider);

                // A FRESH stream, owned by the SDK call: the transcription clients dispose whatever
                // stream they are handed, so there is nothing to reuse and nothing to keep open
                // across the call.
                await using FileStream wav = new(scratchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                transcript = await client.TranscribeAsync(
                    wav,
                    // The file name is what the SDKs use to infer the container format, and for the
                    // OpenAI-compatible path it is the multipart part's filename. The artifact's own
                    // last segment is already `<contentKey>.wav`, so use it verbatim rather than
                    // inventing a name that could disagree with the bytes.
                    ResolveFileName(storageKey),
                    language: null,
                    wordTimestamps: true,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Covers both a factory that refuses to build a client (an unsupported kind, a
                // blank/undecryptable key) and a client whose HTTP call failed. Both are "the ASR
                // backend did not answer", which is one operational fact.
                _logger.LogWarning(ex, "Transcription failed while aligning voiceover line artifact {StorageKey}", storageKey);
                return VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.TranscriptionFailed);
            }

            // Null-tolerant even though TranscriptResult declares the list non-null: this is a
            // cross-assembly contract whose implementations include test doubles and third-party
            // shapes, and an empty list is a legitimate NO_WORDS rather than a NullReferenceException
            // escaping into the step's per-line catch.
            IReadOnlyList<TranscriptWord> recognized = transcript.Words ?? [];

            if (recognized.Count == 0)
            {
                _logger.LogDebug(
                    "ASR returned no words for voiceover line artifact {StorageKey}; recording {Reason}",
                    storageKey, VoiceoverAlignmentReasons.NoWords);
                return VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.NoWords);
            }

            List<VoiceoverAlignedWord> words = new(recognized.Count);
            foreach (TranscriptWord word in recognized)
            {
                // Verbatim: the timings stay ASR-relative to the start of THIS line's audio (the
                // fields are named startSec/endSec, never startOffsetSec, precisely so nobody has to
                // guess which origin they are relative to), and nothing is dropped, merged or
                // re-ordered.
                words.Add(new VoiceoverAlignedWord(word.Text, word.StartSec, word.EndSec));
            }

            return VoiceoverAlignmentResult.Aligned(words);
        }
        finally
        {
            if (File.Exists(scratchPath))
                try { File.Delete(scratchPath); } catch { }
        }
    }

    /// <summary>
    /// The multipart file name for the ASR call: the storage key's own last segment (already
    /// <c>&lt;contentKey&gt;.wav</c>), falling back to a generic name when the key is unusable.
    /// </summary>
    private static string ResolveFileName(string storageKey)
    {
        string name = Path.GetFileName(storageKey);
        return string.IsNullOrWhiteSpace(name) ? "voiceover-line.wav" : name;
    }
}
