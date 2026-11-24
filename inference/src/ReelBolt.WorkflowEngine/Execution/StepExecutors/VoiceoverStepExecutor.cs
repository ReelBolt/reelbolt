using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Styles;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.Voiceover"/> steps: deterministic, non-LLM text-to-speech synthesis
/// of voiceover lines with byte-budget guardrails, content-addressed per-line artifacts, and
/// never-throws discipline.
///
/// Behavior (8 numbered):
/// 1. Bad config -> Failed
/// 2. Inline uses Source.Lines; ScriptScenes resolves the Scriptwriter step's scenes;
///    NarrationPlan resolves the NarrationWriter step's <c>NarrationPlanOutput</c>, taking each
///    line's text and its opaque cut-anchor id (and, for NarrationPlan only, DECLARING the line
///    positions itself — see the placement paragraph below)
/// 3. BEFORE synthesis, sanitize all lines (maxLength=int.MaxValue), apply the project pronunciation glossary to get the
///    text that will be SPOKEN, sum UTF-8 bytes of it (the larger of written and spoken), > MaxBytes -> fail, client never
///    invoked; content moderation screens the spoken text (and the written text when they differ)
/// 4. Resolve via ResolveSpeechSynthesisAsync(config.ProviderId, ct), null -> Failed
/// 5. Per line: an empty SANITIZED line is skipped (never synthesized, never billed) but still occupies
///    its slot in the output list; otherwise the artifact is content-addressed as
///    <c>voiceover/lines/{contentKey}.wav</c>, where <c>contentKey</c> is lowercase SHA-256 hex over
///    <see cref="ComputeLineContentKey"/>'s length-prefixed material — the tag
///    <c>voiceover-line-v2</c>, then text, effective voice id, EFFECTIVE model, provider id and
///    endpoint, each of the last five written as <c>length + ":" + value</c> and joined by <c>\n</c>
///    — and the object store itself is the cache — a HEAD hit means "these exact bytes already
///    exist", so a repeat run pays only a download + ffprobe and never a second Fish Audio request.
///    Before an artifact is uploaded the payload must pass BOTH a RIFF/WAVE container check and a
///    positive-duration/audio-codec probe, because under a content-addressed key a bad object is a
///    permanently poisoned cache entry
/// 6. Per-line try/catch never escapes -> {"id":"vo{n}","startSec":...,"status":"failed"} on error, else "status":"skipped" or "ok" (+ full storageKey)
/// 7. Every `ok` line is then ALIGNED through <see cref="IVoiceoverAlignmentService"/> — the line's own
///    content-addressed WAV is re-transcribed with word timestamps, so the line gains
///    <c>words:[{text,startSec,endSec}]</c> when alignment applied, or
///    <c>alignmentReason:&lt;reason&gt;</c> when it did not. Both are OMITTED (never null) when absent.
///    Alignment never fails a line and never fails the step; a `skipped` or `failed` line is never
///    aligned at all
/// 8. Every `ok`/`skipped` line then gains <c>fit</c> — the deterministic
///    <see cref="NarrationFitSolver"/> verdict on whether its narration overruns the DECLARED window
///    it was written for — and <c>meta</c> gains the report's counters plus the promoted integers
///    <c>meta.overflowWords</c>/<c>meta.overflowLineCount</c>. A `failed` line carries no <c>fit</c>
///    at all. No model computes a duration anywhere on this path
///
/// Output: {"view":{"lines":[...],"meta":{"totalLines":N,"failedLines":M,"skippedLines":K,"allLinesFailed":bool,"emptyReason":"narration_plan_empty","alignment":{"applied":bool,"alignedLines":A,"unalignedLines":U,"reasons":{"&lt;REASON&gt;":count}},"fit":{"measuredLineCount":n,"overflowLineCount":n,"overflowWords":n,"overflowTotalSec":s,"maxOverflowSec":s,"unknownWindowLineCount":n},"overflowWords":n,"overflowLineCount":n}}}
/// <c>meta.emptyReason</c> is present ONLY on the empty-plan path — a well-formed
/// <see cref="VoiceoverSourceKind.NarrationPlan"/> whose resolved line list is empty, which is a
/// successful, empty step rather than a failure. Its value tells the PROVENANCE of that emptiness:
/// <see cref="NarrationPlanEmptyReason"/> (<c>narration_plan_empty</c>) when the plan declared ZERO
/// entries — nobody asked for narration — and <see cref="NarrationPlanLinesDiscardedReason"/>
/// (<c>narration_plan_lines_discarded</c>) when the plan declared one or more entries and every one
/// was discarded as blank/whitespace text — narration was asked for and the producer handed over
/// nothing speakable. <c>meta.declaredLines</c> rides beside it on that same path, carrying the
/// number of entries in the plan's own <c>lines</c> list counted BEFORE the blank-text filter, so
/// declared-and-discarded is distinguishable from never-declared without inference. Both keys are
/// absent on every other envelope, so a consumer distinguishes "nobody asked for narration" from
/// "narration was asked for and every line was lost" by a key check, never by reading a reason string.
/// <c>meta.allLinesFailed</c> is true only when at least one line was emitted and every emitted line
/// failed — the render will have no narration, so the executor also logs at Error. The step still
/// reports Completed: "never throw, degrade one line, never fail the whole step" is the contract.
/// <c>meta.alignment</c> covers the <c>ok</c> lines ONLY: <c>alignedLines + unalignedLines</c> is the
/// <c>ok</c>-line count, and every unaligned <c>ok</c> line contributes exactly one entry to
/// <c>reasons</c>, so <c>sum(reasons) == unalignedLines</c>. Skipped and failed lines have no audio
/// to align and are counted in neither.
/// ArtifactStorageKey at voiceover/{executionId}/artifact.json — the EXECUTION id, not a step-result
/// id: this executor builds the path from <c>context.Execution.Id</c> so every line and the artifact
/// document of one run share that run's prefix.
/// Overall Completed if guardrail+resolution succeeded.
/// Top-level try/catch -> Failed on any unexpected exception, except a caller cancellation, which is
/// re-thrown so a stopped run is never reported as an executor defect.
///
/// <para>
/// <b>Alignment is deliberately NOT part of the step-cache key.</b> The step's cache stores the whole
/// output JSON, and alignment's result is part of that JSON — so a cache hit already carries the
/// words computed by the run that was cached, and re-aligning on a hit would spend an ASR round trip
/// to recompute the same value. Adding the transcription provider's identity to the key would be
/// worse than useless: it would invalidate every cached voiceover output whenever an unrelated ASR
/// row was edited, and still would not make a hit's words "current" for anything but that one input.
/// </para>
///
/// <para>
/// <b>The fit window is the DECLARED narration window, and that is a different measurement from the
/// compile's.</b> For <see cref="VoiceoverSourceKind.Inline"/> and
/// <see cref="VoiceoverSourceKind.ScriptScenes"/> alike, line <c>i</c>'s window is
/// <c>[StartSec_i, StartSec_{i+1})</c> — the slot the script itself declares, ending where the next
/// line was declared to begin. The last declared line has no successor and therefore NO window; it is
/// reported as <see cref="NarrationFitSolver.WindowSourceNone"/> and counted in
/// <c>unknownWindowLineCount</c> rather than being given an invented duration, and a windowless line
/// never contributes overflow and always counts as fitting. <c>VideoCompileStepExecutor</c> measures
/// something else on purpose — how much of the SHOT a line overruns once the edit has placed it, after
/// seam overlaps — and that measurement is media's; this executor does not read it, require it, or
/// reimplement it, and the two must not be conflated by a consumer.
/// </para>
/// <para>
/// <b>Fit rides in the output JSON, so a step-cache hit carries it too.</b> Like the alignment words,
/// it is part of the cached document rather than a key input: a hit re-serves the fit computed by the
/// run it cached, and re-deriving it on a hit would be a second implementation of the same arithmetic
/// for no new information.
/// </para>
/// <para>
/// <b>A NarrationPlan line has no declared start of its own, so this executor DECLARES one:
/// narration-first placement.</b> <c>NarrationPlanOutput</c> is structurally incapable of carrying a
/// start time (every property is a plain string — see <c>NarrationPlanOutputInvariantTests</c>), and
/// this step holds no picture timeline to place a line against, so the post-loop pass assigns each
/// line a start back-to-back in plan order: line 0 at <c>0.0</c>, each next line at the previous
/// line's start plus its measured <c>playDurationSec</c> (<c>0</c> for a skipped or failed line,
/// which synthesized nothing). The result is deterministic and never overlapping, and it is what the
/// emitted <c>view.lines[].startSec</c> carries. It is a DECLARATION, not a picture-fit measurement:
/// the narration is laid out first and the picture's fit against it is measured by the compile step
/// (media's <c>narrationFitFacts</c>) once the edit has actually placed the lines. This executor does
/// NOT read the analysis step, require one, or reimplement that measurement — and because the
/// declared window is <c>[start_i, start_{i+1})</c>, a NarrationPlan line's window is exactly its own
/// measured duration, so its declared-window overflow is structurally zero and the compile-side fact
/// is the one that reports an overrun. Inline and ScriptScenes are untouched by this pass: their
/// starts come from the config/script, and their windows and fit numbers stay byte-identical.
/// </para>
/// <para>
/// <b>Why there is no in-memory per-line cache here any more.</b> This executor is registered
/// <c>AddSingleton</c>. It used to hold a process-wide <c>ConcurrentDictionary</c> keyed on the
/// old <c>text|voiceId|model</c> concatenation, and a hit skipped BOTH the synthesis and the
/// artifact upload — while the artifact key was built from <c>Execution.Id</c>. So the second
/// execution of the same lines emitted output naming objects that belonged to the first execution's
/// prefix (and, for a line the new run's own upstream had never produced, no object at all).
/// Compile then dropped every line as <c>line_file_not_found</c> and the workflow still reported
/// Completed. The execution id is also deliberately absent from the step-cache key, so a step-cache
/// hit reopened the same hole.
/// </para>
/// <para>
/// The remedy is not to scope that dictionary but to delete it, because the key was the wrong shape.
/// Making the artifact key content-addressed makes the OBJECT STORE the cache: a hit means a
/// previous run uploaded exactly these bytes, verified by HEAD within the current execution, so no
/// decision can leak across executions and the singleton holds no mutable state at all.
/// </para>
/// <para>
/// <b>The finance argument, stated plainly:</b> a repeat run therefore pays only a HEAD, a download
/// and one ffprobe per line — never a Fish Audio request. Re-synthesis is bought only when something
/// that changes the audio genuinely changed: the content, the effective voice, the EFFECTIVE model
/// (the provider row's <c>ModelName</c> unless the step config overrides it), or the provider itself
/// (its id or its endpoint — re-pointing the step at another backend must not reuse the old one's
/// bytes). The emitted <c>storageKey</c> always names an object this execution has verified exists.
/// </para>
/// </summary>
public class VoiceoverStepExecutor : IStepExecutor, IChangeRequestAwareStepExecutor
{
    // The step config's own options are the single source shared with the save-time validator
    // (StepConfigSaveValidator.ValidateVoiceoverConfig reads a Voiceover config with the very same
    // instance), so the two can never read `kind`/`narrationWriterStepOrder` differently. The same
    // options double as this executor's upstream-OUTPUT reader: the output schemas it deserializes
    // (ScriptwriterOutput, NarrationPlanOutput) are plain camelCase records with no enums, for which
    // Web defaults are exactly right.
    /// <summary>
    /// An upstream agent step's output as the JSON object it carries. Agent output is stored as the
    /// model returned it, so it can arrive wrapped in a markdown fence or with prose around it (a
    /// styled run's narration writer once answered with a ```json block); every other consumer
    /// extracts with <see cref="RobustJsonExtractor.ExtractLastJsonObject"/> — the same extractor
    /// AgentStepExecutor validated the output with — and so must these. Falls back to the raw text
    /// so a genuinely malformed answer still fails with its own parse error.
    /// </summary>
    internal static string UpstreamJson(string output) =>
        RobustJsonExtractor.ExtractLastJsonObject(output ?? string.Empty) ?? output ?? string.Empty;

    private static readonly JsonSerializerOptions ConfigJsonOptions = VoiceoverStepConfig.JsonOptions;

    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Category segment of every per-line artifact key — matches the default of <see cref="IProjectFileWorkspace.UploadArtifactAsync"/>.</summary>
    private const string ArtifactCategory = "agentFiles";

    /// <summary>
    /// Named reason a per-line artifact is refused because the synthesized bytes are not a RIFF/WAVE
    /// container. Distinct from <see cref="NotDecodableReason"/> so an operator can tell "the vendor
    /// sent something that is not audio at all" from "the container looks right but cannot be decoded".
    /// </summary>
    private const string NotWavReason = "VOICEOVER_PAYLOAD_NOT_WAV";

    /// <summary>Named reason a per-line artifact is refused because ffprobe found no positive duration / no audio codec in it.</summary>
    private const string NotDecodableReason = "VOICEOVER_PAYLOAD_NOT_DECODABLE";

    /// <summary>
    /// The value of <c>meta.emptyReason</c> on a step that Completed because its
    /// <see cref="VoiceoverSourceKind.NarrationPlan"/> source was well-formed and DECLARED zero
    /// entries. The key is present only on the empty-plan path; its value is
    /// <c>narration_plan_empty</c> when the plan declared zero entries and
    /// <see cref="NarrationPlanLinesDiscardedReason"/> (<c>narration_plan_lines_discarded</c>) when
    /// the plan declared entries whose text was all blank, and <c>meta.declaredLines</c> carries the
    /// declared count so declared-and-discarded is distinguishable from never-declared without
    /// inference.
    ///
    /// <para>
    /// <b>Why this marker exists at all.</b> "Nobody asked for narration" and "narration was asked
    /// for and every line was lost" are different outcomes, and a consumer must be able to tell
    /// them apart without inferring one from a reason string. On the step's own envelope the
    /// difference is already carried twice — this step is <see cref="StepStatus.Completed"/> with
    /// <c>meta.allLinesFailed:false</c>, whereas a declared-and-lost run is <c>Failed</c> or
    /// Completed with <c>meta.allLinesFailed:true</c> — but neither of those keys says the empty
    /// list was a DECISION rather than an absence, which is exactly what the downstream
    /// review gate needs and exactly what a human operator asks first.
    /// </para>
    /// <para>
    /// Present ONLY on this path. It is absent — not null, not false — on every other Completed
    /// envelope, so "the writer chose silence" is a key existence check rather than a value
    /// comparison, and adding it changes no existing key.
    /// </para>
    /// </summary>
    internal const string NarrationPlanEmptyReason = "narration_plan_empty";

    /// <summary>
    /// The value of <c>meta.emptyReason</c> when a <see cref="VoiceoverSourceKind.NarrationPlan"/>
    /// DECLARED one or more entries and every one was discarded as blank/whitespace text. Still a
    /// Completed, empty step — there was nothing narratable, and the compile stays inert — but the
    /// provenance is a producer (or model) problem rather than an editorial decision, and the one key
    /// created to carry provenance must not report it as "nobody asked for narration".
    /// <c>meta.declaredLines</c> carries the declared count beside it.
    /// </summary>
    internal const string NarrationPlanLinesDiscardedReason = "narration_plan_lines_discarded";

    private readonly ISpeechSynthesisClientFactory _speechFactory;
    private readonly IInferenceProviderResolver _providerResolver;
    private readonly IMediaProbe _mediaProbe;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IVoiceoverAlignmentService _alignmentService;
    private readonly ILogger<VoiceoverStepExecutor> _logger;
    private readonly IProjectVoiceResolver? _voiceResolver;
    private readonly Services.Avatars.IAvatarResolver? _avatars;
    private readonly IProjectPronunciationResolver? _glossaryResolver;
    private readonly IDecisionGate? _decisionGate;
    private readonly IVideoToolRunner? _toolRunner;
    private readonly INarrationLineShortener? _shortener;

    /// <summary>Finds the step change request this run carries out (docs/design/change-requests.md); optional so every pre-existing construction site keeps compiling.</summary>
    private readonly IStepChangeRequestContext? _changeRequests;

    /// <summary>
    /// T-006: optional so every pre-moderation construction site keeps compiling. When present
    /// (production DI supplies T-005's singleton) every sanitized line is screened once before the
    /// first <c>SynthesizeAsync</c>; a Block fails the step and the speech client is never invoked.
    /// </summary>
    private readonly IContentModerationGate? _moderation;

    /// <summary>The client's per-call text cap; narration is moderated in chunks of this size.</summary>
    private readonly int _moderationMaxChars;

    /// <summary>
    /// The per-step spend bound on words admitted to synthesis
    /// (<c>Voiceover:MaxAdmittedNarrationWords</c>). <c>MaxBytes</c> bounds request size, not the
    /// number of words; this bounds what one step may send to the provider.
    /// </summary>
    private readonly int _maxAdmittedNarrationWords;

    /// <summary>Failure code of a step that speaks in the avatar's voice when there is none to speak in.</summary>
    internal const string AvatarVoiceMissingCode = "AVATAR_VOICE_MISSING";

    /// <summary>
    /// The project voice linked to the run brief's avatar, through the one avatar rule (an unusable avatar fails with its
    /// own code). Never a fallback: no avatar, no resolver or no linked voice answers a failure.
    /// </summary>
    private async Task<(Guid? Voice, string? Code, string? Why)> AvatarVoiceAsync(StepExecutionContext context, RunBriefState? brief)
    {
        if (brief?.Brief.Avatar?.AvatarId is not Guid avatarId || avatarId == Guid.Empty)
        {
            return (null, AvatarVoiceMissingCode,
                "This step speaks in the voice of the run's avatar, and the run shows no avatar. Choose one for the run.");
        }

        if (_avatars is null)
        {
            return (null, AvatarVoiceMissingCode, "This engine cannot read avatars, so the avatar's voice cannot be used.");
        }

        try
        {
            Services.Avatars.ResolvedAvatar avatar = await _avatars.ResolveRequiredAsync(context.Execution.ProjectId, avatarId, context.CancellationToken);
            return avatar.ProjectVoiceId is { } voice
                ? (voice, null, null)
                : (null, AvatarVoiceMissingCode,
                    $"The avatar '{avatar.Name}' has no linked voice. Link one of the project's voices on the Avatars tab, or choose a voice for the run.");
        }
        catch (Services.Avatars.AvatarUnusableException unusable)
        {
            return (null, unusable.Code, $"The run's avatar cannot be used ({unusable.Reason}), so neither can its voice.");
        }
    }

    /// <summary>The decision-gate site every voice-casting question is recorded under.</summary>
    internal const string VoiceCastingSite = "VoiceCasting";

    public VoiceoverStepExecutor(
        ISpeechSynthesisClientFactory speechFactory,
        IInferenceProviderResolver providerResolver,
        IMediaProbe mediaProbe,
        IProjectFileWorkspace workspace,
        IVoiceoverAlignmentService alignmentService,
        ILogger<VoiceoverStepExecutor> logger,
        // Phase 4 (docs/voiceover.md "Voices and consent" / "Voice casting"). Optional so every
        // pre-phase-4 construction site keeps compiling; DI supplies both. Without a resolver a
        // step that names a ProjectVoiceId FAILS (never falls back to an unguarded voice), and
        // without a gate voice casting is simply not performed.
        IProjectVoiceResolver? voiceResolver = null,
        IDecisionGate? decisionGate = null,
        IContentModerationGate? moderation = null,
        IOptions<ContentModerationOptions>? moderationOptions = null,
        // Narration fit (docs/voiceover.md "Fit"): without a tool runner a line that overruns its scene
        // cannot be sped up and goes straight to the clear failure; the shortener is the T-003b seam.
        IVideoToolRunner? toolRunner = null,
        INarrationLineShortener? shortener = null,
        // Project pronunciation glossary (docs/voiceover.md): written-accent respellings substituted into the
        // text sent to the speech provider only. Null = no glossary.
        IProjectPronunciationResolver? glossaryResolver = null,
        // Step change requests (docs/design/change-requests.md). Null = no change request can apply.
        IStepChangeRequestContext? changeRequests = null,
        // Narration spend bound (docs/voiceover.md "Fit"): the per-step word ceiling on what may be
        // synthesized, independent of the per-line 4.0 words/s hard ceiling. Optional so every
        // pre-existing construction site keeps compiling; absent = the generous default.
        IOptions<VoiceoverFitOptions>? fitOptions = null,
        // Avatars phase 3: VoiceFromAvatar reads the run's avatar's linked voice. Null = such a step fails closed.
        Services.Avatars.IAvatarResolver? avatars = null)
    {
        _avatars = avatars;
        _toolRunner = toolRunner;
        _shortener = shortener;
        _glossaryResolver = glossaryResolver;
        _changeRequests = changeRequests;
        _speechFactory = speechFactory;
        _providerResolver = providerResolver;
        _mediaProbe = mediaProbe;
        _workspace = workspace;
        _alignmentService = alignmentService;
        _logger = logger;
        _voiceResolver = voiceResolver;
        _decisionGate = decisionGate;
        _moderation = moderation;
        _moderationMaxChars = moderationOptions?.Value.MaxTextChars ?? 8000;
        int configuredAdmittedWords = fitOptions?.Value.MaxAdmittedNarrationWords
            ?? VoiceoverFitOptions.DefaultMaxAdmittedNarrationWords;
        _maxAdmittedNarrationWords = configuredAdmittedWords > 0
            ? configuredAdmittedWords
            : VoiceoverFitOptions.DefaultMaxAdmittedNarrationWords;
    }

    public StepType StepType => StepType.Voiceover;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // (1) Parse and validate config
            VoiceoverStepConfig? config;
            try
            {
                if (string.IsNullOrWhiteSpace(context.Step.VoiceoverConfigJson))
                    return Failure(context, sw, "CONFIG_INVALID", "Voiceover step has no VoiceoverConfigJson configured.");

                config = VoiceoverStepConfig.Parse(context.Step.VoiceoverConfigJson);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", $"VoiceoverConfigJson is not valid JSON: {ex.Message}");
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "VoiceoverConfigJson deserialized to null.");

            // Pronunciation overrides are re-checked here (the save path is not the only way a
            // config reaches the engine): a violation fails BEFORE any speech request.
            IReadOnlyList<string> configErrors = config.Validate();
            if (configErrors.Count > 0)
                return Failure(context, sw, "CONFIG_INVALID", "VoiceoverConfigJson " + string.Join(" ", configErrors));

            // The run brief (docs/run-brief.md): `voice.mode: none` makes this a skipped step with a
            // plain reason, never a failure; `language` and an explicit voice fill the step's own
            // Language / ProjectVoiceId. The voice then goes through the consent guard below like any
            // other, so a foreign or unusable voice fails closed and never falls back.
            RunBriefState? runBrief = RunBriefAmbient.Current;
            if (RunBriefOverrides.IsSilent(runBrief))
            {
                _logger.LogInformation("Voiceover step {StepOrder} skipped: the run brief says the film is not narrated.", context.Step.StepOrder);
                return new StepExecutionResult
                {
                    Output = JsonSerializer.Serialize(new { skipped = true, reason = SilentRunReason }),
                    NextStepIndex = context.CurrentStepIndex + 1,
                    NewIterationCount = context.IterationCount,
                    DurationMs = sw.ElapsedMilliseconds,
                    Status = StepStatus.Skipped,
                    ErrorDetails = SilentRunReason
                };
            }

            config = RunBriefOverrides.ApplyToVoiceover(config, runBrief);

            // Avatars phase 3: the voice linked to the run's avatar, unless the brief names a voice itself. The voice
            // then passes the consent guard below like any other; nothing falls back to another voice.
            if (config.VoiceFromAvatar && RunBriefOverrides.VoiceIdOf(runBrief?.Voice) is null)
            {
                (Guid? avatarVoice, string? code, string? why) = await AvatarVoiceAsync(context, runBrief);
                if (avatarVoice is not { } linked)
                    return Failure(context, sw, code!, why!);
                config = config with { ProjectVoiceId = linked };
            }

            // (2) Resolve lines from source kind
            IReadOnlyList<VoiceoverLine> lines;

            if (config.Source.Kind == VoiceoverSourceKind.Inline)
            {
                lines = config.Source.Lines ?? [];
                if (lines.Count == 0)
                    return Failure(context, sw, "NO_LINES", "No voiceover lines provided.");
            }
            else if (config.Source.Kind == VoiceoverSourceKind.ScriptScenes)
            {
                // Resolve lines from upstream Scriptwriter step
                if (config.Source.ScriptwriterStepOrder is null)
                    return Failure(context, sw, "SCRIPT_SOURCE_INVALID", "ScriptScenes source requires ScriptwriterStepOrder.");

                var scriptwriterEntry = context.StepOutputHistory.FirstOrDefault(
                    h => h.StepOrder == config.Source.ScriptwriterStepOrder.Value);

                if (scriptwriterEntry is null)
                    return Failure(context, sw, "SCRIPTWRITER_NOT_FOUND",
                        $"No step output found for ScriptwriterStepOrder {config.Source.ScriptwriterStepOrder}.");

                // Deserialize the scriptwriter output
                ScriptwriterOutput? scriptwriterOutput;
                try
                {
                    scriptwriterOutput = JsonSerializer.Deserialize<ScriptwriterOutput>(
                        UpstreamJson(scriptwriterEntry.Output),
                        ScriptwriterOutputJson.Options);
                }
                catch (JsonException ex)
                {
                    return Failure(context, sw, "SCRIPTWRITER_OUTPUT_INVALID",
                        $"Failed to deserialize scriptwriter output: {ex.Message}");
                }

                if (scriptwriterOutput is null)
                    return Failure(context, sw, "SCRIPTWRITER_OUTPUT_INVALID",
                        "Scriptwriter output deserialized to null.");

                // Convert ScriptScenes to VoiceoverLines, filtering empty voiceovers
                var voiceoverLines = new List<VoiceoverLine>();
                foreach (var scene in scriptwriterOutput.Scenes)
                {
                    // Only include scenes with non-empty voiceover text
                    if (!string.IsNullOrWhiteSpace(scene.Voiceover))
                    {
                        voiceoverLines.Add(new VoiceoverLine(
                            Text: scene.Voiceover,
                            StartSec: (double)scene.StartTime, // Convert int seconds to double
                            VoiceId: null)); // No voice ID specified by scriptwriter
                    }
                }

                if (voiceoverLines.Count == 0)
                    return Failure(context, sw, "NO_LINES", "No non-empty voiceover scenes found in scriptwriter output.");

                lines = voiceoverLines.AsReadOnly();
            }
            else if (config.Source.Kind == VoiceoverSourceKind.NarrationPlan)
            {
                // Resolve lines from the upstream NarrationWriter step. Each plan line contributes
                // exactly two things: the words to speak and the opaque cut-anchor id it was written
                // for. The plan has no start of its own (it structurally cannot), so StartSec is
                // declared later by the post-loop placement pass rather than read from here.
                if (config.Source.NarrationWriterStepOrder is null)
                    return Failure(context, sw, "NARRATION_SOURCE_INVALID", "NarrationPlan source requires NarrationWriterStepOrder.");

                var narrationEntry = context.StepOutputHistory.FirstOrDefault(
                    h => h.StepOrder == config.Source.NarrationWriterStepOrder.Value);

                // A DISTINCT code from SOURCE_INVALID, deliberately: "the step you named is not in
                // this run's history" is an operator-visible wiring mistake (wrong step order, or the
                // named step sits AFTER this one), and reporting it as "unknown source kind" would
                // send whoever triages it looking at the enum instead of at the workflow. The
                // ScriptScenes arm's SCRIPTWRITER_NOT_FOUND is the same code for the same reason.
                if (narrationEntry is null)
                    return Failure(context, sw, "NARRATION_STEP_NOT_FOUND",
                        $"No step output found for NarrationWriterStepOrder {config.Source.NarrationWriterStepOrder}.");

                NarrationPlanOutput? narrationPlan;
                try
                {
                    narrationPlan = JsonSerializer.Deserialize<NarrationPlanOutput>(
                        UpstreamJson(narrationEntry.Output),
                        ConfigJsonOptions);
                }
                catch (JsonException ex)
                {
                    return Failure(context, sw, "NARRATION_OUTPUT_INVALID",
                        $"Failed to deserialize narration plan output: {ex.Message}");
                }

                if (narrationPlan is null)
                    return Failure(context, sw, "NARRATION_OUTPUT_INVALID",
                        "Narration plan output deserialized to null.");

                // Two shapes the `JsonException` catch above structurally cannot name, because the
                // document PARSES perfectly and is simply not a plan. A property initializer only
                // runs when the member is ABSENT, so an explicit JSON `null` for `lines` — or for
                // one of its entries — leaves the list (or the element) null; without these checks
                // the walk below dereferences it, the NullReferenceException escapes past this
                // arm's own catch to the outer one, and a model-authored shape is reported as
                // UNEXPECTED_ERROR, a code that says "this executor is broken". Every other
                // malformed shape on this arm already has its own code, so these get it too.
                if (narrationPlan.Lines is null)
                    return Failure(context, sw, "NARRATION_OUTPUT_INVALID",
                        "Narration plan output carries an explicit null `lines` list.");

                // Empty/whitespace lines are dropped here (never synthesized, never billed), exactly
                // as the ScriptScenes arm drops an empty scene voiceover — the sanitizer would skip
                // them anyway and the slot they would occupy carries no words to declare. The
                // anchor id is carried through even when the plan's own text is unusable, because
                // the id is what makes a surviving line's fit attributable.
                var narrationLines = new List<VoiceoverLine>();
                foreach (NarrationLine? planLine in narrationPlan.Lines)
                {
                    if (planLine is null)
                        return Failure(context, sw, "NARRATION_OUTPUT_INVALID",
                            "Narration plan output carries an explicit null entry in its `lines` list.");

                    if (string.IsNullOrWhiteSpace(planLine.Text))
                        continue;

                    narrationLines.Add(new VoiceoverLine(
                        Text: planLine.Text,
                        // Declared by the post-loop placement pass for this source kind; 0.0 here is
                        // a placeholder, never what the output ends up carrying.
                        StartSec: 0.0,
                        VoiceId: null,
                        AnchorId: planLine.AnchorId));
                }

                // A WELL-FORMED plan with ZERO lines is a SUCCESSFUL, EMPTY step — never a failure.
                //
                // The planner is the only thing that knows whether an edit wants narration, and both
                // `NarrationWriterAgent.DefaultPrompt` ("if the edit needs no narration at all,
                // output an EMPTY `lines` list rather than filling the silence") and
                // `NarrationPlanOutput`'s own remark say so. The step did its job and narrates
                // nothing; failing it here made a model that obeyed its prompt redden a correct run,
                // and after `MaxStepRetries` fail the whole execution.
                //
                // `NO_LINES` therefore stays exactly where it belongs: the `Inline` and
                // `ScriptScenes` arms above, where an empty resolved list is an author
                // misconfiguration rather than a decision, and every malformed-plan shape below,
                // which `NARRATION_OUTPUT_INVALID` covers. Phase 1's semantics for those two kinds
                // are deliberately NOT weakened.
                if (narrationLines.Count == 0)
                    return EmptyNarrationPlanSuccess(context, sw, declaredLines: narrationPlan.Lines.Count);

                lines = narrationLines.AsReadOnly();
            }
            else if (config.Source.Kind == VoiceoverSourceKind.PickupPlan)
            {
                // Pickups (docs/voiceover.md "Pickups"): lines from the PickupPlanner step's
                // PickupPlanOutput — the corrected text plus the transcript-segment id it repairs as
                // the anchor. Same shape of arm as NarrationPlan, same empty-plan contract: a clean
                // take with zero pickups is a SUCCESSFUL, empty step. Positions are declared by the
                // post-loop placement pass here; the compile step ignores them and places each
                // pickup over its segment's own window.
                if (config.Source.PickupPlannerStepOrder is null)
                    return Failure(context, sw, "PICKUP_SOURCE_INVALID", "PickupPlan source requires PickupPlannerStepOrder.");

                var pickupEntry = context.StepOutputHistory.FirstOrDefault(
                    h => h.StepOrder == config.Source.PickupPlannerStepOrder.Value);
                if (pickupEntry is null)
                    return Failure(context, sw, "PICKUP_STEP_NOT_FOUND",
                        $"No step output found for PickupPlannerStepOrder {config.Source.PickupPlannerStepOrder}.");

                PickupPlanOutput? pickupPlan;
                try
                {
                    pickupPlan = JsonSerializer.Deserialize<PickupPlanOutput>(UpstreamJson(pickupEntry.Output), ConfigJsonOptions);
                }
                catch (JsonException ex)
                {
                    return Failure(context, sw, "PICKUP_OUTPUT_INVALID", $"Failed to deserialize pickup plan output: {ex.Message}");
                }

                if (pickupPlan is null || pickupPlan.Pickups is null)
                    return Failure(context, sw, "PICKUP_OUTPUT_INVALID", "Pickup plan output deserialized to null or carries a null `pickups` list.");

                var pickupLines = new List<VoiceoverLine>();
                foreach (PickupLine? pickup in pickupPlan.Pickups)
                {
                    if (pickup is null)
                        return Failure(context, sw, "PICKUP_OUTPUT_INVALID", "Pickup plan output carries a null entry in its `pickups` list.");
                    if (string.IsNullOrWhiteSpace(pickup.Text) || string.IsNullOrWhiteSpace(pickup.SegmentId))
                        continue;
                    pickupLines.Add(new VoiceoverLine(pickup.Text, 0.0, null, pickup.SegmentId));
                }

                if (pickupLines.Count == 0)
                    return EmptyNarrationPlanSuccess(context, sw, declaredLines: pickupPlan.Pickups.Count);

                lines = pickupLines.AsReadOnly();
            }
            else
            {
                return Failure(context, sw, "SOURCE_INVALID", $"Unknown VoiceoverSourceKind: {config.Source.Kind}");
            }

            // (2b) Step change request (docs/design/change-requests.md): when this run carries one for this
            // step, apply its overrides by line id and work out which lines are carried over untouched.
            StepChangeRequestScope? changeScope = _changeRequests is null
                ? null
                : await _changeRequests.FindAsync(context.Execution, context.Step, context.CancellationToken);
            var lineRespellings = new Dictionary<int, List<GlossaryEntry>>();
            HashSet<int> carriedIndexes = [];
            // Lines whose wording the owner wrote in this request: never rewritten by the narration shortener.
            HashSet<int> textOverridden = [];
            Dictionary<string, VoiceoverOutputLine>? previousLines = null;
            if (changeScope is { Malformed: true })
                return Failure(context, sw, "CHANGE_NOT_APPLIED", StepChangeRequestScope.MalformedRefusal, FailureClass.UserInput);
            if (changeScope is not null)
            {
                ChangePlan changePlan = PlanChange(changeScope.Payload, lines.Count);
                if (changePlan.Matched == 0)
                {
                    return Failure(context, sw, "CHANGE_NOT_APPLIED",
                        "The change request names no line of this narration, so nothing was changed. Choose a line (or all lines) and ask again.",
                        FailureClass.UserInput);
                }

                // Lines the request does not name are carried over from the step's previous output. With no previous output
                // (the source is no longer available) they cannot be, and re-synthesizing them would spend on lines the
                // owner did not ask for: refuse before any side effect, as the other executors do. "*" names every line.
                if (string.IsNullOrWhiteSpace(changeScope.PreviousOutputJson)
                    && Enumerable.Range(0, lines.Count).Any(changePlan.IsCarriedOver))
                {
                    return Failure(context, sw, "CHANGE_NOT_APPLIED",
                        "The narration this change starts from is not available, so nothing was changed.",
                        FailureClass.UserInput);
                }

                var changed = new List<VoiceoverLine>(lines.Count);
                for (int i = 0; i < lines.Count; i++)
                {
                    VoiceoverLine line = lines[i];
                    IReadOnlyDictionary<string, JsonElement>? ov = changePlan.OverridesFor(i);
                    if (ov is not null)
                    {
                        if (ov.TryGetValue("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                        {
                            line = line with { Text = text.GetString() ?? string.Empty };
                            textOverridden.Add(i);
                        }

                        if (ov.TryGetValue("speed", out JsonElement speed) && speed.ValueKind == JsonValueKind.Number)
                        {
                            double rate = speed.GetDouble();
                            if (!(rate >= 0.5 && rate <= 2.0))
                                return Failure(context, sw, "CONFIG_INVALID", $"Line vo{i}: speed must be between 0.5 and 2.0.");
                            line = line with { Speed = rate };
                        }

                        if (ov.TryGetValue("voiceId", out JsonElement voice) && voice.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(voice.GetString()))
                        {
                            string requested = voice.GetString()!.Trim();
                            if (Guid.TryParse(requested, out Guid requestedProjectVoice))
                            {
                                // A project voice goes through the same consent guard as the step's own
                                // ProjectVoiceId: unusable fails, never falls back to another voice.
                                if (_voiceResolver is null)
                                    return Failure(context, sw, "VOICE_RESOLVER_UNAVAILABLE", "This engine cannot resolve project voices, so a change naming one cannot run.");
                                ResolvedProjectVoice resolved = await _voiceResolver.ResolveAsync(
                                    context.Execution.ProjectId, requestedProjectVoice, context.CancellationToken);
                                if (!resolved.Usable)
                                {
                                    string code = resolved.Reason == "voice_not_found" ? "VOICE_NOT_FOUND" : "VOICE_CONSENT_MISSING";
                                    return Failure(context, sw, code,
                                        $"Project voice {requestedProjectVoice} cannot be used: {resolved.Reason}. A cloned voice is usable only while its consent is attested, not revoked and within retention.");
                                }

                                line = line with { VoiceId = resolved.RemoteVoiceId };
                            }
                            else
                            {
                                // A vendor id is checked against the project voice rows with every other
                                // effective vendor id at step (4a') below.
                                line = line with { VoiceId = requested };
                            }
                        }

                        if (ov.TryGetValue("pronunciation", out JsonElement pron) && pron.ValueKind == JsonValueKind.Array)
                        {
                            // A way of writing the word so the voice stresses it right (`Novara` -> `Novàra`), the one
                            // mechanism measured to work for Italian on Fish: same rules as the project glossary.
                            List<GlossaryEntry> entries = [];
                            foreach (JsonElement entry in pron.EnumerateArray())
                            {
                                string? term = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("term", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                                string? spelling = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("spelling", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                                IReadOnlyList<string> pronErrors = PronunciationGlossaryValidator.Validate(term, spelling);
                                if (pronErrors.Count > 0)
                                    return Failure(context, sw, "CONFIG_INVALID", $"Line vo{i}: " + string.Join(" ", pronErrors));
                                entries.Add(new GlossaryEntry(
                                    PronunciationGlossaryValidator.Normalize(term), PronunciationGlossaryValidator.Normalize(spelling), false));
                            }

                            if (entries.Count > 0)
                                lineRespellings[i] = entries;
                        }
                    }

                    if (changePlan.IsCarriedOver(i))
                        carriedIndexes.Add(i);
                    changed.Add(line);
                }

                lines = changed;
                previousLines = ParsePreviousLines(changeScope.PreviousOutputJson);
            }

            // (3) Sanitize lines and check byte budget BEFORE synthesizing
            var sanitizedLines = new List<(int Index, VoiceoverLine Original, string Sanitized)>();
            long totalBytes = 0;

            foreach (var (i, line) in lines.Select((l, i) => (i, l)))
            {
                string sanitized = VoiceoverTextSanitizer.Sanitize(line.Text, int.MaxValue);
                long lineBytes = Encoding.UTF8.GetByteCount(sanitized);
                totalBytes += lineBytes;

                sanitizedLines.Add((i, line, sanitized));
            }

            // New wording that is empty once cleaned (only a link, only tags) would silently make that line silence.
            foreach (int overriddenIndex in textOverridden)
            {
                if (string.IsNullOrWhiteSpace(sanitizedLines[overriddenIndex].Sanitized))
                    return Failure(context, sw, "CONFIG_INVALID", $"Line vo{overriddenIndex}: the new wording has no words left once links and markup are removed.");
            }

            // (3c) Project glossary: loaded once per run and applied HERE, before the byte budget and content
            // moderation, so both of them judge the text the provider will actually speak. Not being able to read
            // it fails the step (a platform failure, refunded) rather than speaking the script without the
            // author's pronunciations. Terms the step's own overrides already name are left to those overrides.
            IReadOnlyList<GlossaryEntry> glossary = [];
            // Digest of the entries READ for this run (before step overrides are subtracted): the run service files the
            // result in the step cache under exactly this, never under a digest it re-reads after the step has run.
            string? glossaryDigest = null;
            if (_glossaryResolver is not null)
            {
                try
                {
                    IReadOnlyList<GlossaryEntry> all = await _glossaryResolver.GetEntriesAsync(
                        context.Execution.ProjectId, context.CancellationToken);
                    glossaryDigest = PronunciationRespeller.Digest(all);
                    if (config.PronunciationOverrides is { Count: > 0 } stepOverrides)
                    {
                        var covered = new HashSet<string>(
                            stepOverrides.Select(o => o.Term.Normalize(NormalizationForm.FormC)),
                            StringComparer.OrdinalIgnoreCase);
                        all = all.Where(g => !covered.Contains(g.Term.Normalize(NormalizationForm.FormC))).ToList();
                    }
                    glossary = all;
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Project pronunciation glossary could not be read for project {ProjectId}", context.Execution.ProjectId);
                    return Failure(context, sw, "GLOSSARY_UNAVAILABLE", "The project's pronunciation glossary could not be read, so the narration was not generated.");
                }
            }

            // A carried-over line is copied only when its previous entry is a usable ok/skipped one that
            // spoke exactly this text; anything else (missing, failed, text moved upstream) is made normally.
            var carriedLines = new Dictionary<int, VoiceoverOutputLine>();
            var voiceByIndex = new Dictionary<int, string>();
            string? previousMetaVoice = PreviousMetaVoice(changeScope?.PreviousOutputJson);
            // A line the request leaves alone is still made again when the project's pronunciations changed how it is
            // spoken since that run: carrying it over would keep the old pronunciation (docs/change-requests.md).
            IReadOnlySet<string> staleLines = changeScope is null
                ? new HashSet<string>()
                : NarrationGlossaryStaleness.StaleLineIds(
                    changeScope.PreviousOutputJson, new NarrationGlossaryStaleness.CurrentGlossary(glossary, glossaryDigest));
            if (staleLines.Count > 0)
                _logger.LogInformation(
                    "Change request on execution {ExecutionId}: {Count} untouched narration line(s) are made again because the project's pronunciations changed",
                    context.Execution.Id, staleLines.Count);
            foreach (int carriedIndex in carriedIndexes)
            {
                if (previousLines is not null
                    && !staleLines.Contains($"vo{carriedIndex}")
                    && previousLines.TryGetValue($"vo{carriedIndex}", out VoiceoverOutputLine? previous)
                    && CanCarry(previous, sanitizedLines[carriedIndex].Sanitized))
                {
                    // The earlier run may have shortened this line to fit its scene: what it spoke is what is kept,
                    // so the fit checks below judge the words that were really said.
                    if (previous.Status == "ok" && !string.IsNullOrWhiteSpace(previous.Text))
                        sanitizedLines[carriedIndex] = (carriedIndex, sanitizedLines[carriedIndex].Original, previous.Text);
                    if (previous.Status == "ok")
                    {
                        // The voice the line was spoken in: recorded on the line, else the earlier run's
                        // resolved voice when the line named none of its own; otherwise it cannot be
                        // checked and the line is made again.
                        string? spoken = !string.IsNullOrWhiteSpace(previous.VoiceId)
                            ? previous.VoiceId
                            : sanitizedLines[carriedIndex].Original.VoiceId is null ? previousMetaVoice : null;
                        if (string.IsNullOrWhiteSpace(spoken))
                            continue;
                        voiceByIndex[carriedIndex] = spoken;
                        carriedLines[carriedIndex] = previous with { VoiceId = spoken };
                    }
                    else
                    {
                        carriedLines[carriedIndex] = previous;
                    }
                }
            }

            // The narration shortener (a model) may rewrite a line only when the script wrote it: never wording the owner
            // typed into a change request, and never a line carried over with the audio it already has.
            bool Shortenable(int lineIndex) => !textOverridden.Contains(lineIndex) && !carriedLines.ContainsKey(lineIndex);

            string? fitLanguage = runBrief?.Language ?? config.Language;

            // (3a) Spend bound, BEFORE any provider call — including the shortener's model call below.
            // MaxBytes caps the request bytes, never the number of words synthesized, and the 4.0
            // words/s hard ceiling bounds each line but not how many lines a pathological script can
            // declare. The step's total admitted words are therefore capped by
            // Voiceover:MaxAdmittedNarrationWords; exceeding it is a config/validation failure, not a
            // fit failure, and nothing is bought. It is checked on the pre-shortened text (shortening
            // only reduces the count) and exists under every NarrationFitPolicy. Lines a change
            // request carries over are not synthesized again and are not counted.
            {
                int admittedWords = 0;
                for (int i = 0; i < sanitizedLines.Count; i++)
                {
                    if (carriedLines.ContainsKey(i))
                        continue;
                    admittedWords += NarrationFitSolver.CountWords(sanitizedLines[i].Sanitized);
                }

                if (!NarrationFitSolver.IsSynthesisAdmissible(admittedWords, _maxAdmittedNarrationWords))
                {
                    string tooMany = NarrationFitMessages.TooManyWords(fitLanguage, admittedWords, _maxAdmittedNarrationWords);
                    return Failure(context, sw, NarrationFitMessages.TooManyWordsCode, tooMany, FailureClass.UserInput, tooMany);
                }
            }

            // (3b) Narration fit, BEFORE any synthesis: it refuses only lines above the 4.0 words/s
            // hard ceiling, which no voice can speak in their window at any supported tempo, so no
            // paid call is wasted on them. Everything else is synthesized and judged on its measured
            // play duration below. Only Inline and ScriptScenes lines have a scene to fit; plan
            // sources occupy their own slot.
            // A sequential Inline block (an app's "speak exactly this" field) declares no slots of its own, like a plan.
            bool enforceFit = config.NarrationFitPolicy == NarrationFitPolicy.Enforce
                && (config.Source.Kind is VoiceoverSourceKind.Inline && !config.Source.Sequential
                    || config.Source.Kind is VoiceoverSourceKind.ScriptScenes);

            double? FitWindowSec(int i)
            {
                double start = sanitizedLines[i].Original.StartSec;
                double? end = i + 1 < sanitizedLines.Count
                    ? sanitizedLines[i + 1].Original.StartSec
                    : enforceFit && runBrief?.LengthSec is int filmLength ? filmLength : null;
                return end is double e && double.IsFinite(e) && double.IsFinite(start) && e > start ? e - start : null;
            }

            if (enforceFit)
            {
                var unrescuable = new List<NarrationLineOverflow>();
                for (int i = 0; i < sanitizedLines.Count; i++)
                {
                    string text = sanitizedLines[i].Sanitized;
                    if (string.IsNullOrWhiteSpace(text) || FitWindowSec(i) is not double window)
                        continue;
                    int words = NarrationFitSolver.CountWords(text);
                    if (NarrationFitSolver.IsUnrescuable(words, window))
                        unrescuable.Add(new NarrationLineOverflow(i, text, words, window, NarrationFitSolver.WordBudget(window)));
                }

                List<NarrationLineOverflow> shortenableNow = unrescuable.Where(o => Shortenable(o.Index)).ToList();
                if (shortenableNow.Count > 0 && _shortener is not null)
                {
                    IReadOnlyDictionary<int, string>? replacements =
                        await _shortener.ShortenAsync(shortenableNow, fitLanguage, context.CancellationToken);
                    if (replacements is not null)
                    {
                        var stillBad = new List<NarrationLineOverflow>();
                        foreach (NarrationLineOverflow o in unrescuable)
                        {
                            string shorter = Shortenable(o.Index) && replacements.TryGetValue(o.Index, out string? r)
                                ? VoiceoverTextSanitizer.Sanitize(r, int.MaxValue)
                                : string.Empty;
                            int shorterWords = NarrationFitSolver.CountWords(shorter);
                            if (shorterWords == 0 || shorterWords > o.WordBudget)
                            {
                                stillBad.Add(o);
                                continue;
                            }

                            sanitizedLines[o.Index] = (o.Index, sanitizedLines[o.Index].Original, shorter);
                            totalBytes += Encoding.UTF8.GetByteCount(shorter) - Encoding.UTF8.GetByteCount(o.Text);
                        }

                        unrescuable = stillBad;
                    }
                }

                if (unrescuable.Count > 0)
                {
                    return Failure(context, sw, NarrationFitMessages.FailureCode,
                        NarrationFitMessages.Build(fitLanguage, unrescuable),
                        NarrationFitFailureClass(config.Source.Kind, unrescuable.Any(o => !Shortenable(o.Index))),
                        NarrationFitMessages.Build(fitLanguage, unrescuable));
                }
            }

            var glossaryApplied = new Dictionary<string, (string Spelling, int Count)>(StringComparer.Ordinal);
            // What the provider will speak, per line index (the sanitized text itself when no entry matches).
            var spokenByIndex = new Dictionary<int, string>();
            long spokenBytes = 0;

            // Recomputed whenever a line's sanitized text changes (the narration shortener can replace a line after
            // synthesis), so the spoken text, the report and the byte count always follow the final written text.
            void ApplyGlossaryToLines()
            {
                glossaryApplied.Clear();
                spokenByIndex.Clear();
                spokenBytes = 0;
                foreach (var (lineIndex, _, lineSanitized) in sanitizedLines)
                {
                    string spoken = lineSanitized;
                    // A change request's per-line respellings come first and win over the project glossary for a term.
                    IReadOnlyList<GlossaryEntry> lineGlossary = lineRespellings.TryGetValue(lineIndex, out List<GlossaryEntry>? own)
                        ? [.. own, .. glossary.Where(g => !own.Any(o => string.Equals(o.Term, g.Term.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)))]
                        : glossary;
                    if (lineGlossary.Count > 0 && !string.IsNullOrWhiteSpace(lineSanitized))
                    {
                        var (respelled, applied) = PronunciationRespeller.Apply(lineSanitized, lineGlossary);
                        spoken = respelled;
                        foreach (AppliedEntry a in applied)
                            glossaryApplied[a.Term] = (a.Spelling, glossaryApplied.TryGetValue(a.Term, out var prev) ? prev.Count + a.Count : a.Count);
                    }
                    spokenByIndex[lineIndex] = spoken;
                    spokenBytes += Encoding.UTF8.GetByteCount(spoken);
                }
            }

            ApplyGlossaryToLines();
            // A respelling can be longer or shorter than the word, so the budget is judged on the larger of the two.
            totalBytes = Math.Max(totalBytes, spokenBytes);

            if (totalBytes > config.MaxBytes)
            {
                return Failure(
                    context,
                    sw,
                    "BYTE_BUDGET_EXCEEDED",
                    $"Total synthesized text would be {totalBytes} bytes, exceeding MaxBytes {config.MaxBytes}. Client never invoked.");
            }

            // (3b) Content moderation (T-006): every sanitized line is screened in ONE call before
            // the provider is resolved or the speech client is touched. Review and Unavailable
            // proceed (this surface fails open); only a Block fails the step.
            // The text screened is the text the client will synthesise: the respelled narration. When a glossary
            // entry changed anything, the written narration (what captions are cut from) is screened as well.
            string spokenModerationText = string.Join(
                "\n", sanitizedLines.Where(l => !carriedLines.ContainsKey(l.Index)).Select(l => spokenByIndex[l.Index]).Where(t => !string.IsNullOrWhiteSpace(t)));
            string writtenModerationText = string.Join(
                "\n", sanitizedLines.Where(l => !carriedLines.ContainsKey(l.Index)).Select(l => l.Sanitized).Where(t => !string.IsNullOrWhiteSpace(t)));
            var moderationTexts = new List<string> { spokenModerationText };
            if (!string.Equals(spokenModerationText, writtenModerationText, StringComparison.Ordinal))
                moderationTexts.Add(writtenModerationText);

            foreach (string moderationText in moderationTexts)
            {
                // Every chunk is classified, so the text the client synthesises is exactly the text
                // that was screened. More chunks than the bound is refused, never partially checked.
                if (!ModerationTextChunker.TryChunk(moderationText, _moderationMaxChars, out IReadOnlyList<string> moderationChunks))
                {
                    return Failure(context, sw, "INPUT_TOO_LARGE",
                        "The narration is too long to check without leaving part of it unclassified.",
                        FailureClass.UserInput);
                }

                foreach (string chunk in moderationChunks)
                {
                    string? blockedMessage = await ScreenGeneratedTextAsync(context, chunk);
                    if (blockedMessage is not null)
                    {
                        return Failure(context, sw, "CONTENT_BLOCKED", blockedMessage, FailureClass.UserInput);
                    }
                }
            }

            // (4) Resolve speech synthesis provider
            ResolvedSpeechSynthesisProvider? provider;
            try
            {
                provider = await _providerResolver.ResolveSpeechSynthesisAsync(config.ProviderId, context.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve speech synthesis provider");
                provider = null;
            }

            if (provider is null)
                return Failure(context, sw, "PROVIDER_NOT_RESOLVED", "Speech synthesis provider could not be resolved.");

            // Provider health, surfaced to the customer as C1 warnings (docs/voiceover.md "Which
            // voice service narrates"): the resolver may have passed over a default whose connection
            // test failed, or may have had nothing healthier to use.
            List<StepOutputWarning> providerWarnings = ProviderHealthWarnings(provider);
            if (SpeechLanguageSupport.Check(provider.Kind, config.Language) == SpeechLanguageVerdict.NotListed)
            {
                providerWarnings.Add(new StepOutputWarning(
                    VoiceLanguageUnlistedCode,
                    "The voice service used for this narration does not list the requested language among the ones it speaks, "
                    + "so the narration may sound wrong. Choose a voice service that supports the language, or check the result."));
            }

            ISpeechSynthesisClient client = _speechFactory.Get(provider);

            // (4a) Voice — the consent guard and voice casting (docs/voiceover.md "Voices and
            // consent" / "Voice casting"). A ProjectVoiceId is resolved WITHIN this project through
            // the same rule the API's Selectable flag applies; an unusable voice fails the step with
            // a code naming why, and never falls back to DefaultVoiceId — a step that asked for a
            // cloned voice must not quietly speak in another one. Casting asks the Decision provider
            // ONE Choice over the project's selectable voices and uses the answer only when the gate
            // accepted it and it names an offered voice; anything else keeps the configured voice.
            string? castVoiceId = null;
            object voiceNode;
            {
                string? projectVoiceRemoteId = null;
                Guid? projectVoiceId = config.ProjectVoiceId;
                if (projectVoiceId is Guid requestedVoiceId)
                {
                    if (_voiceResolver is null)
                        return Failure(context, sw, "VOICE_RESOLVER_UNAVAILABLE", "This engine cannot resolve project voices, so a step naming a ProjectVoiceId cannot run.");

                    ResolvedProjectVoice resolvedVoice = await _voiceResolver.ResolveAsync(context.Execution.ProjectId, requestedVoiceId, context.CancellationToken);
                    if (!resolvedVoice.Usable)
                    {
                        string code = resolvedVoice.Reason == "voice_not_found" ? "VOICE_NOT_FOUND" : "VOICE_CONSENT_MISSING";
                        return Failure(context, sw, code,
                            $"Project voice {requestedVoiceId} cannot be used: {resolvedVoice.Reason}. A cloned voice is usable only while its consent is attested, not revoked and within retention.");
                    }

                    projectVoiceRemoteId = resolvedVoice.RemoteVoiceId;
                    castVoiceId = projectVoiceRemoteId;
                }

                // The project's one selectable voice is the default narrator when the step names no voice at all:
                // one selectable voice (Cloned or Library) speaks; several are a choice only the person (or voice
                // casting) can make; a step never quietly speaks in the provider's stock voice while the project
                // owns a selectable voice (docs/voiceover.md "The project's one voice is the default").
                ProjectVoiceDefault projectDefault = await ProjectVoiceDefault.ResolveAsync(
                    _voiceResolver, context.Execution.ProjectId, config, context.CancellationToken);
                bool defaultedToProjectVoice = false;
                if (projectDefault.Single is { } defaultVoice)
                {
                    projectVoiceId = defaultVoice.Id;
                    projectVoiceRemoteId = defaultVoice.RemoteVoiceId;
                    castVoiceId = projectVoiceRemoteId;
                    defaultedToProjectVoice = true;
                }

                object casting = new { mode = config.VoiceCasting.ToString(), applied = false };
                // A voice the brief named is the person's choice: casting never replaces it.
                if (config.VoiceCasting == VoiceCastingMode.Decision && RunBriefOverrides.VoiceIdOf(runBrief?.Voice) is null)
                {
                    // A change request keeps the voice the earlier run cast, so a re-made line sounds like its neighbours.
                    string? keptCast = castVoiceId is null ? PreviousCastVoice(changeScope?.PreviousOutputJson) : null;
                    if (keptCast is not null)
                    {
                        castVoiceId = keptCast;
                        casting = new { mode = config.VoiceCasting.ToString(), applied = true, reason = "kept_from_previous_run" };
                    }
                    else
                    {
                        (casting, castVoiceId) = await CastVoiceAsync(context, config, sanitizedLines, castVoiceId);
                    }
                }

                // (A line that carries its own voice id needs no step-level voice; only a line without one would speak in the stock voice.)
                if (projectDefault.Ambiguous && castVoiceId is null
                    && sanitizedLines.Any(l => l.Sanitized.Length > 0 && string.IsNullOrWhiteSpace(l.Original.VoiceId)))
                {
                    string names = string.Join(", ", projectDefault.Candidates.Take(5).Select(c => $"\"{c.Name}\""));
                    return Failure(context, sw, "VOICE_CHOICE_REQUIRED",
                        $"This project has {projectDefault.Candidates.Count} voices ({names}) and the narration step does not say which one to use. "
                        + "Choose one in the step's voice setting, or name it in the run brief.",
                        FailureClass.UserInput);
                }

                string? remoteVoiceId = castVoiceId ?? config.DefaultVoiceId;
                string source = projectVoiceRemoteId is not null ? "project_voice" : castVoiceId is not null ? "cast" : config.DefaultVoiceId is not null ? "default_voice_id" : "provider_default";
                // `defaulted` is written only when the project's one selectable voice was chosen for the step, so every other
                // output keeps the node it always had.
                voiceNode = defaultedToProjectVoice
                    ? new { projectVoiceId, defaulted = true, remoteVoiceId, source, casting }
                    : new { projectVoiceId, remoteVoiceId, source, casting };
            }

            // (4a') Vendor-id consent. A cloned voice's VENDOR id can reach synthesis by routes other than
            // ProjectVoiceId — DefaultVoiceId, a line's own VoiceId, the cast voice — and the content-addressed
            // object store would then serve its audio after a revoke. So EVERY effective vendor id is matched
            // against the project voice rows (VendorVoiceIdRule: this project's own row decides; otherwise a clone of
            // ANOTHER project is refused) BEFORE any synthesis or object-store reuse, and a refusal fails the step
            // with VOICE_CONSENT_MISSING. An id matching no row is a raw vendor/library id and stays allowed
            // (a deleted clone's vendor id is indistinguishable from one). With ids present and
            // no resolver the step fails closed, like a ProjectVoiceId does.
            {
                var effectiveVendorIds = new HashSet<string>(StringComparer.Ordinal);
                // Carried-over lines are re-checked NOW, with every other effective voice: a revoked, deleted or
                // foreign-project voice must not survive in a line that was not re-made.
                foreach (string? id in sanitizedLines.Select(l => l.Original.VoiceId ?? castVoiceId ?? config.DefaultVoiceId).Append(config.DefaultVoiceId).Append(castVoiceId).Concat(voiceByIndex.Values))
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        effectiveVendorIds.Add(id);
                }

                if (effectiveVendorIds.Count > 0)
                {
                    if (_voiceResolver is null)
                        return Failure(context, sw, "VOICE_RESOLVER_UNAVAILABLE", "This engine cannot resolve project voices, so a step naming a voice id cannot run.");

                    IReadOnlyList<string> refused = await _voiceResolver.FindRefusedVendorVoiceIdsAsync(
                        context.Execution.ProjectId, effectiveVendorIds, context.CancellationToken);
                    if (refused.Count > 0)
                        return Failure(context, sw, "VOICE_CONSENT_MISSING", "Project " + VendorVoiceIdRule.Message(refused[0]));
                }
            }

            // (4b) Pronunciation dictionary (docs/voiceover.md "Pronunciation dictionary"): the
            // candidate terms come from the code-analysis outputs already in this run's history,
            // the phonemes from a first-party table, and an entry is sent only for a line whose text
            // contains the term. Off (default) builds nothing and leaves every request and every
            // content key byte-identical to phase 1.
            IReadOnlySet<string> analysisTerms = config.PronunciationDictionary == PronunciationDictionaryMode.Auto
                ? PronunciationDictionaryBuilder.ExtractAnalysisTerms(context.StepOutputHistory.Select(h => h.Output))
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // One shortening attempt per overflowing batch after synthesis: on a hit the ladder below is
            // re-run from here, and unchanged lines are served by the content-addressed object store.
            bool shortenedAfterSynthesis = false;
            // Line objects THIS attempt newly created (never ones that already existed). A refunded failure
            // deletes them so the spend cannot be reused later as free cached audio.
            var newlyCreatedKeys = new List<string>();
        retryAfterShortening:
            var pronunciationByLineId = new Dictionary<string, IReadOnlyList<PronunciationEntry>>(StringComparer.Ordinal);


            // (5) Per line: content-addressed artifact, object store as the cache, never-throws discipline
            var outputLines = new List<VoiceoverOutputLine>();
            int failedCount = 0;
            int skippedCount = 0;

            foreach (var (index, originalLine, sanitized) in sanitizedLines)
            {
                string id = $"vo{index}";

                // Carried over verbatim from the run being changed: no object-store probe, no speech
                // call, no alignment call. Fit is recomputed below over the final durations.
                if (carriedLines.TryGetValue(index, out VoiceoverOutputLine? carried))
                {
                    if (carried.Status == "skipped")
                        skippedCount++;
                    outputLines.Add(carried);
                    continue;
                }

                // An empty sanitized line is NOT synthesized and NOT billed. It is still emitted as
                // an entry, and THIS IS LOAD-BEARING: ReactRemotionSandboxTools.StageVoiceoverAudio
                // pairs non-empty Scriptwriter scenes with voiceover lines POSITIONALLY, so dropping
                // the entry would shift the mapping and stage the wrong WAV for a later scene.
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    _logger.LogDebug("Line {Id} is empty after sanitization; skipping synthesis", id);
                    skippedCount++;
                    outputLines.Add(new VoiceoverOutputLine(
                        Id: id,
                        StartSec: originalLine.StartSec,
                        Status: "skipped",
                        DurationSec: 0,
                        StorageKey: null,
                        // A skipped line synthesized nothing, so there is no audio to align and no
                        // alignment failure to report — both fields are omitted rather than filled
                        // with a reason that would read as "alignment broke".
                        Words: null,
                        AlignmentReason: null));
                    continue;
                }

                // The voice ACTUALLY sent, not originalLine.VoiceId: content-addressing has to key
                // on what is synthesized, or two configs differing only in DefaultVoiceId would hash
                // to one key and the second would reuse the first's voice. A per-line voice still
                // wins; then the resolved project voice / cast voice; then the config default.
                string? effectiveVoiceId = originalLine.VoiceId ?? castVoiceId ?? config.DefaultVoiceId;
                if (!string.IsNullOrWhiteSpace(effectiveVoiceId))
                    voiceByIndex[index] = effectiveVoiceId;

                // The model ACTUALLY sent, resolved through the client's own helper rather than
                // re-deriving the precedence here: config.Model defaults to null, so the model that
                // synthesizes is normally the provider row's ModelName. Keying on config.Model alone
                // would reuse the previous backend's audio after that row's model was edited.
                string effectiveModel = provider.Kind == InferenceProviderKind.OpenAICompatible
                    ? OpenAICompatibleSpeechSynthesisClient.ResolveEffectiveModel(config.Model, provider.ModelName)
                    : FishAudioSpeechSynthesisClient.ResolveEffectiveModel(config.Model, provider.ModelName);

                // Provider identity is part of the key for the same reason: the same words on the
                // same voice are DIFFERENT audio from a different provider row or endpoint, so
                // re-pointing the step must miss the cache rather than serve the old bytes. Its
                // name, kind, timeout and API key are deliberately NOT in the material — none of
                // them changes the audio, and the key must stay free of secrets.
                // The dictionary changes the audio, so it is part of the content key — but ONLY when
                // non-empty, so a line with no entries keeps the exact phase-1 key and its cached object.
                IReadOnlyList<PronunciationEntry> pronunciation = analysisTerms.Count > 0 || config.PronunciationOverrides is { Count: > 0 }
                    ? PronunciationDictionaryBuilder.BuildForLineWithOverrides(
                        sanitized, analysisTerms, config.MaxPronunciationEntries, config.PronunciationOverrides)
                    : [];
                if (pronunciation.Count > 0)
                    pronunciationByLineId[id] = pronunciation;

                // Glossary respelling: the provider hears this text; everything else keeps `sanitized`.
                string spokenText = spokenByIndex[index];

                string contentKey = ComputeLineContentKey(
                    spokenText,
                    effectiveVoiceId,
                    effectiveModel,
                    provider.ProviderId?.ToString("D"),
                    provider.Endpoint,
                    pronunciation,
                    provider.Kind == InferenceProviderKind.OpenAICompatible ? null : FishLanguageTag.From(config.Language),
                    originalLine.Speed);

                string relativeArtifactPath = $"voiceover/lines/{contentKey}.wav";

                // The FULL key, prefixed with projects/{projectId}/agentFiles/ — consumers hand it
                // straight to DownloadStorageKeyToFileAsync, which enforces that scope.
                string storageKey = ProjectFilePath.BuildStorageKey(
                    context.Execution.ProjectId,
                    ArtifactCategory,
                    directoryPath: null,
                    storageFileName: relativeArtifactPath);

                try
                {
                    if (await _workspace.ArtifactExistsAsync(
                            context.Execution.ProjectId, storageKey, context.CancellationToken))
                    {
                        // The object store IS the per-line cache. Because the key is derived from
                        // the content being produced, a hit means a previous run uploaded exactly
                        // these bytes; the duration still has to be re-measured for THIS execution's
                        // output, so the object is downloaded to scratch and probed.
                        _logger.LogDebug("Reusing existing content-addressed artifact for line {Id}", id);

                        outputLines.Add(await EmitFromExistingArtifactAsync(
                            context.Execution.ProjectId,
                            id,
                            originalLine.StartSec,
                            storageKey,
                            context.CancellationToken));
                        continue;
                    }

                    var request = new SpeechRequest(
                        Text: spokenText,
                        VoiceId: effectiveVoiceId,
                        ModelOverride: config.Model,
                        Pronunciation: pronunciation.Count > 0 ? pronunciation : null,
                        Language: config.Language,
                        Speed: originalLine.Speed);

                    var synthesis = await client.SynthesizeAsync(request, context.CancellationToken);

                    // Write to temp file for validating, probing and uploading
                    string tempPath = Path.Combine(Path.GetTempPath(), $"voiceover_{Guid.NewGuid()}.wav");
                    try
                    {
                        await using (var file = File.Create(tempPath))
                        {
                            await synthesis.Wav.CopyToAsync(file, context.CancellationToken);
                        }

                        // Validate before probe/upload. A bad payload must never reach the object
                        // store: with a CONTENT-ADDRESSED key, an object that IS written is an
                        // object every FUTURE execution will find by HEAD and trust as the real
                        // audio for those words, so a bad body is a permanently poisoned cache
                        // entry rather than the harmless one-execution mistake it was when the key
                        // was execution-scoped. Two independent gates, both required:
                        //   (a) the container really is a RIFF/WAVE stream, and
                        //   (b) ffprobe really can decode it to a positive duration with an audio
                        //       codec — the same two facts VideoCompileStepExecutor re-checks
                        //       before it will mix a line, so a line that passes here is a line the
                        //       compile can actually use.
                        if (!HasRiffWaveHeader(tempPath))
                        {
                            throw new InvalidOperationException(
                                $"{NotWavReason}: the synthesized payload is not a RIFF/WAVE stream; refusing to store it.");
                        }

                        MediaProbeResult probeResult = await _mediaProbe.ProbeAsync(tempPath, context.CancellationToken);

                        if (probeResult.DurationSec <= 0 || probeResult.AudioCodec is null)
                        {
                            throw new InvalidOperationException(
                                $"{NotDecodableReason}: the synthesized payload has no decodable audio "
                                + $"(durationSec={probeResult.DurationSec}, audioCodec={probeResult.AudioCodec ?? "null"}); refusing to store it.");
                        }

                        double durationSec = probeResult.DurationSec;

                        string uploadedKey = await _workspace.UploadArtifactAsync(
                            context.Execution.ProjectId,
                            tempPath,
                            relativeArtifactPath,
                            "audio/wav",
                            context.CancellationToken);
                        // The HEAD above said it was absent, so this attempt created it.
                        newlyCreatedKeys.Add(uploadedKey);

                        outputLines.Add(await BuildOkLineAsync(
                            context.Execution.ProjectId,
                            id,
                            originalLine.StartSec,
                            durationSec,
                            uploadedKey,
                            context.CancellationToken));
                    }
                    finally
                    {
                        if (File.Exists(tempPath))
                            try { File.Delete(tempPath); } catch { }
                    }
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    // A CANCELLED RUN IS NOT A FAILED LINE. Recording it as one would report an
                    // operator action as an infrastructure fact — the same reasoning
                    // GuardrailScreener applies to its own OperationCanceledException clause. The
                    // `when` guard is what keeps this narrow: a provider TIMEOUT arrives as a
                    // TaskCanceledException with our own token UNCANCELLED, and still degrades the
                    // line exactly as before.
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to synthesize line {Id}", id);
                    failedCount++;
                    outputLines.Add(new VoiceoverOutputLine(
                        Id: id,
                        StartSec: originalLine.StartSec,
                        Status: "failed",
                        DurationSec: 0,
                        StorageKey: null,
                        // A failed line has no artifact, so alignment never ran on it: both fields
                        // stay omitted, exactly as for a skipped line.
                        Words: null,
                        AlignmentReason: null));
                }
            }

            // Condition 5 (producer layer): the step was asked to narrate and produced NOTHING
            // usable. The step is still Completed — that contract is deliberate and unchanged (see
            // the class summary: never throw, degrade one line, never fail the whole step) — but a
            // green step that synthesized nothing must not be the only thing an operator sees.
            bool allLinesFailed = outputLines.Count > 0 && outputLines.All(l => l.Status == "failed");

            if (allLinesFailed)
            {
                _logger.LogError(
                    "Voiceover step produced no usable narration: all {FailedLineCount} line(s) failed for execution {ExecutionId}. "
                    + "The step reports Completed by contract, but the render will have no voiceover.",
                    failedCount, context.Execution.Id);
            }

            // Alignment statistics are DERIVED from the emitted lines rather than accumulated
            // alongside them. That is not a style choice: the three `ok`-producing paths (fresh
            // synthesis, the object-store cache hit, and — in a future wave — any other) each build
            // their own line, and a counter incremented at each site is a fourth place to forget.
            // Reading the finished list makes the invariant unbreakable by construction:
            // alignedLines + unalignedLines is exactly the number of `ok` lines, and every
            // unaligned `ok` line contributes exactly one entry to reasons.
            int alignedLines = outputLines.Count(l => l.Status == "ok" && l.Words is not null);
            int unalignedLines = outputLines.Count(l => l.Status == "ok" && l.Words is null);

            var alignmentReasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (VoiceoverOutputLine line in outputLines)
            {
                if (line.Status == "ok" && line.Words is null && line.AlignmentReason is not null)
                {
                    alignmentReasons[line.AlignmentReason] =
                        alignmentReasons.GetValueOrDefault(line.AlignmentReason) + 1;
                }
            }

            // (7b) Declared positions for a NarrationPlan source. The plan carries no start time by
            // construction and this step has no picture timeline to place a line against, so the
            // positions are DECLARED here — narration-first placement: line 0 at 0.0, each next line
            // at the previous line's declared start plus its measured play duration (0 for a skipped
            // or failed line). Deterministic, never overlapping, and feeding BOTH the emitted
            // startSec and the declared window the fit pass reads (one assignment, so the two cannot
            // disagree). It is deliberately not a picture-fit measurement: how the narration lands
            // against the SHOTS is measured by VideoCompileStepExecutor once the edit has placed it.
            //
            // A no-op for Inline and ScriptScenes, whose starts come from the config/script and whose
            // fit numbers therefore stay byte-identical.
            // A sequential Inline block (docs/design/app-inputs.md 27) is placed the same way: one block from 0 s.
            if (config.Source.Kind == VoiceoverSourceKind.NarrationPlan || config.Source.Kind == VoiceoverSourceKind.PickupPlan
                || config.Source is { Kind: VoiceoverSourceKind.Inline, Sequential: true })
                AssignNarrationPlanStarts(outputLines, sanitizedLines);

            // (8) Fit — the deterministic narration-fit report, computed AFTER the loop because a
            // line's play duration is only known once it has been synthesized or probed, and
            // attached from ONE pass over the finished list rather than at each emit site. That is
            // the same argument the alignment counters above make: three (now four) emit sites each
            // remembering to contribute a fact is a place to forget, whereas a single pass over the
            // result cannot disagree with itself.
            //
            // The input is built from the DECLARED line list, not from the lines that survived: a
            // failed line still occupies its declared slot, so the window of the line BEFORE it still
            // ends where that failure was declared to begin. A failed line is itself excluded — it
            // synthesized nothing, so there is no audio to measure and no fit to report, and counting
            // it would inflate measuredLineCount with a line that was never measured.
            // (8a) Tempo fit (Enforce): a line overrunning its scene by more than the tolerance is sped
            // up (pitch-preserving atempo, at most MaxTempoFit) into a NEW content-addressed object; the
            // original is never overwritten. Re-measured below, so the report states the FINAL state.
            var tempoByLine = new Dictionary<string, double>(StringComparer.Ordinal);
            if (enforceFit && _toolRunner is not null)
            {
                for (int i = 0; i < outputLines.Count && i < sanitizedLines.Count; i++)
                {
                    VoiceoverOutputLine current = outputLines[i];
                    if (current.Status != "ok" || current.StorageKey is null || FitWindowSec(i) is not double window)
                        continue;
                    if (NarrationFitSolver.PlanTempo(current.DurationSec, window) is not double tempo)
                        continue;
                    VoiceoverOutputLine? retimed = await TryRetimeAsync(context.Execution.ProjectId, current, tempo, newlyCreatedKeys, context.CancellationToken);
                    if (retimed is null)
                        continue;
                    outputLines[i] = retimed;
                    tempoByLine[retimed.Id] = tempo;
                }
            }

            var fitLines = new List<NarrationFitLine>(outputLines.Count);

            // outputLines is 1:1 with sanitizedLines and in the same order — every branch of the loop
            // above appends exactly one entry — which is the same positional invariant the skipped
            // line's slot depends on. The bound is a guard, not an expectation.
            for (int i = 0; i < outputLines.Count && i < sanitizedLines.Count; i++)
            {
                VoiceoverOutputLine emitted = outputLines[i];
                if (emitted.Status == "failed")
                    continue;

                double? windowStartSec = null;
                double? windowEndSec = null;
                // The DECLARED narration window: [this line's start, the next declared line's start).
                // Both ends are null on the last declared line, which has no successor to end at.
                if (i + 1 < sanitizedLines.Count)
                {
                    windowStartSec = sanitizedLines[i].Original.StartSec;
                    windowEndSec = sanitizedLines[i + 1].Original.StartSec;
                }
                else if (enforceFit && FitWindowSec(i) is double lastWindow)
                {
                    // The LAST line's window runs to the end of the film when the run brief declares it.
                    windowStartSec = sanitizedLines[i].Original.StartSec;
                    windowEndSec = windowStartSec + lastWindow;
                }

                fitLines.Add(new NarrationFitLine(
                    Id: emitted.Id,
                    // The anchor the line was written for, when its source kind can declare one.
                    // Only VoiceoverSourceKind.NarrationPlan can: an Inline or ScriptScenes line
                    // carries no anchor at all, and this stays null rather than being invented —
                    // a fabricated id would make the fit facts unattributable in the one direction
                    // that matters (claiming an anchor the writer never named).
                    AnchorId: sanitizedLines[i].Original.AnchorId,
                    WindowStartSec: windowStartSec,
                    WindowEndSec: windowEndSec,
                    PlayDurationSec: emitted.DurationSec,
                    // The SANITIZED text is what was spoken, so it is what the word count counts —
                    // never the raw config text, which can carry emotion tags and URLs the
                    // synthesizer never reads out.
                    WordCount: NarrationFitSolver.CountWords(sanitizedLines[i].Sanitized),
                    AlignedWordCount: emitted.Words?.Count));
            }

            NarrationFitReport fitReport = NarrationFitSolver.Solve(fitLines);

            // (8b) Still overflowing after the ladder: fail clearly, never emit two voices at once.
            if (enforceFit)
            {
                var overflowing = new List<NarrationLineOverflow>();
                foreach (NarrationFitLineResult r in fitReport.Lines)
                {
                    if (r.WindowSec is not double rw || r.OverflowSec <= NarrationFitSolver.OverflowToleranceSec)
                        continue;
                    int li = outputLines.FindIndex(o => o.Id == r.Id);
                    overflowing.Add(new NarrationLineOverflow(
                        li, sanitizedLines[li].Sanitized, r.WordCount, rw, NarrationFitSolver.WordBudget(rw), r.PlayDurationSec));
                }

                List<NarrationLineOverflow> shortenableAfter = overflowing.Where(o => Shortenable(o.Index)).ToList();
                if (shortenableAfter.Count > 0 && _shortener is not null && !shortenedAfterSynthesis)
                {
                    shortenedAfterSynthesis = true;
                    IReadOnlyDictionary<int, string>? shorter =
                        await _shortener.ShortenAsync(shortenableAfter, fitLanguage, context.CancellationToken);
                    bool changed = false;
                    foreach (NarrationLineOverflow o in shortenableAfter)
                    {
                        if (shorter is null || !shorter.TryGetValue(o.Index, out string? candidate))
                            continue;
                        string clean = VoiceoverTextSanitizer.Sanitize(candidate, int.MaxValue);
                        int cleanWords = NarrationFitSolver.CountWords(clean);
                        if (cleanWords == 0 || cleanWords > o.WordBudget || clean == o.Text)
                            continue;
                        // The model wrote this text, so it is screened like every other generated line.
                        if (await ScreenGeneratedTextAsync(context, clean) is string blocked)
                            // The text was written by OUR shortener, not the user: platform-caused, so refunded.
                            return await FailRefundedAsync(context, sw, newlyCreatedKeys, "CONTENT_BLOCKED", blocked, FailureClass.PlatformInternal);
                        sanitizedLines[o.Index] = (o.Index, sanitizedLines[o.Index].Original, clean);
                        // The respelled form of the new line is what will be spoken, so it is screened too.
                        ApplyGlossaryToLines();
                        if (!string.Equals(spokenByIndex[o.Index], clean, StringComparison.Ordinal)
                            && await ScreenGeneratedTextAsync(context, spokenByIndex[o.Index]) is string blockedSpoken)
                            return await FailRefundedAsync(context, sw, newlyCreatedKeys, "CONTENT_BLOCKED", blockedSpoken, FailureClass.PlatformInternal);
                        changed = true;
                    }

                    if (changed)
                    {
                        failedCount = 0;
                        skippedCount = 0;
                        goto retryAfterShortening;
                    }
                }

                if (overflowing.Count > 0)
                {
                    FailureClass fitClass = NarrationFitFailureClass(config.Source.Kind, overflowing.Any(o => !Shortenable(o.Index)));
                    string fitMessage = NarrationFitMessages.Build(fitLanguage, overflowing);
                    // Inline keeps its spend (and so its cache); a refunded class must not leave free audio behind.
                    return fitClass == FailureClass.UserInput
                        ? Failure(context, sw, NarrationFitMessages.FailureCode, fitMessage, fitClass, fitMessage)
                        : await FailRefundedAsync(context, sw, newlyCreatedKeys, NarrationFitMessages.FailureCode, fitMessage, fitClass, fitMessage);
                }
            }

            var fitById = new Dictionary<string, NarrationFitLineResult>(StringComparer.Ordinal);
            foreach (NarrationFitLineResult fitLine in fitReport.Lines)
                fitById[fitLine.Id] = fitLine;

            for (int i = 0; i < outputLines.Count; i++)
            {
                // Built from the non-failed lines only, so a failed line finds no entry here, keeps
                // Fit == null and therefore serializes WITHOUT a `fit` key at all.
                if (fitById.TryGetValue(outputLines[i].Id, out NarrationFitLineResult? fit))
                {
                    // Report keeps the measure-only shape: no budget, no tempo members.
                    fit = enforceFit
                        ? fit with { Tempo = tempoByLine.TryGetValue(outputLines[i].Id, out double t) ? t : null }
                        : fit with { WordBudget = null };
                    outputLines[i] = outputLines[i] with { Fit = fit };
                }

                // The spoken text and anchor ride on the same non-failed lines (see the record):
                // one pass, same positional invariant as the fit attachment above.
                if (outputLines[i].Status == "ok" && voiceByIndex.TryGetValue(i, out string? spokenVoice))
                    outputLines[i] = outputLines[i] with { VoiceId = spokenVoice };

                if (outputLines[i].Status != "failed" && i < sanitizedLines.Count)
                {
                    outputLines[i] = outputLines[i] with
                    {
                        Text = sanitizedLines[i].Sanitized,
                        AnchorId = sanitizedLines[i].Original.AnchorId,
                        // What the PROJECT glossary made of the line (not a per-line respelling asked for in a change
                        // request), so a later change compares like with like. A carried line keeps what it had.
                        Spoken = carriedLines.TryGetValue(i, out VoiceoverOutputLine? keptLine)
                            ? keptLine.Spoken
                            : GlossarySpokenForm(sanitizedLines[i].Sanitized, glossary)
                    };
                }
            }

            // Upload artifact (as a bare artifact, no ProjectWorkspaceFile row)
            var output = new
            {
                view = new
                {
                    lines = outputLines
                },
                meta = new
                {
                    totalLines = sanitizedLines.Count,
                    failedLines = failedCount,
                    skippedLines = skippedCount,
                    allLinesFailed,
                    // Always present once lines are produced, never null: a consumer can read
                    // meta.alignment.alignedLines without a null check, and "no words" is visible
                    // as alignedLines == 0 plus the reason that caused it rather than as a missing key.
                    alignment = new
                    {
                        applied = alignedLines > 0,
                        alignedLines,
                        unalignedLines,
                        reasons = alignmentReasons
                    },
                    // Always present once lines are produced, never null: this is the fact a
                    // ReviewLoop gates on, and "nothing overflowed" has to be readable as zeros
                    // rather than as an absent key. A step that produced NO lines at all never
                    // reaches here — it returns through Failure(), whose envelope is unchanged.
                    fit = enforceFit
                        ? (object)new
                        {
                            measuredLineCount = fitReport.MeasuredLineCount,
                            overflowLineCount = fitReport.OverflowLineCount,
                            overflowWords = fitReport.OverflowWords,
                            overflowTotalSec = fitReport.OverflowTotalSec,
                            maxOverflowSec = fitReport.MaxOverflowSec,
                            unknownWindowLineCount = fitReport.UnknownWindowLineCount,
                            retimedLineCount = tempoByLine.Count,
                            maxTempoApplied = tempoByLine.Count > 0 ? tempoByLine.Values.Max() : 1.0
                        }
                        : new
                        {
                            measuredLineCount = fitReport.MeasuredLineCount,
                            overflowLineCount = fitReport.OverflowLineCount,
                            overflowWords = fitReport.OverflowWords,
                            overflowTotalSec = fitReport.OverflowTotalSec,
                            maxOverflowSec = fitReport.MaxOverflowSec,
                            unknownWindowLineCount = fitReport.UnknownWindowLineCount
                        },
                    // The same two integers, PROMOTED to meta itself so a Conditional step can gate
                    // on a bare scalar comparison instead of walking view.lines[]. Deliberately
                    // duplicated: the nested object stays the complete report, and this pair is the
                    // one that has to be cheap to read.
                    overflowWords = fitReport.OverflowWords,
                    overflowLineCount = fitReport.OverflowLineCount,
                    // Pronunciation dictionary provenance: which analysis terms were sent for which
                    // lines. Always present; Off reports mode "Off" with zero entries.
                    pronunciation = new
                    {
                        mode = config.PronunciationDictionary.ToString(),
                        analysisTermCount = analysisTerms.Count,
                        overrideCount = config.PronunciationOverrides?.Count ?? 0,
                        entryCount = pronunciationByLineId.Values.Sum(v => v.Count),
                        glossaryCount = glossary.Count,
                        glossaryDigest,
                        glossaryApplied = glossaryApplied
                            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => new { term = kv.Key, spelling = kv.Value.Spelling, count = kv.Value.Count })
                            .ToList(),
                        lines = pronunciationByLineId
                            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => new { lineId = kv.Key, keys = kv.Value.Select(e => e.Key).ToList() })
                            .ToList()
                    },
                    // Dub provenance (phase 4): the language this track narrates in, when declared.
                    language = config.Language ?? RunStyleOverrides.Style(context.Execution)?.Config.Language,
                    // Voice provenance (phase 4): which voice spoke, resolved through the consent guard.
                    voice = voiceNode
                }
            };

            string serializedOutput = JsonSerializer.Serialize(output, OutputJsonOptions);
            if (changeScope is not null)
            {
                serializedOutput = WithChangeRequestMeta(
                    serializedOutput,
                    changeScope.RequestId,
                    [.. sanitizedLines.Where(l => !carriedLines.ContainsKey(l.Index)).Select(l => $"vo{l.Index}")],
                    carriedLines.Count);
            }

            string outputJson = WithWarnings(serializedOutput, providerWarnings);
            string artifactLocalPath = Path.Combine(Path.GetTempPath(), $"voiceover_artifact_{Guid.NewGuid()}.json");
            string artifactStorageKey = string.Empty;

            try
            {
                await File.WriteAllTextAsync(artifactLocalPath, outputJson, context.CancellationToken);
                string artifactFileName = $"voiceover/{context.Execution.Id}/artifact.json";
                artifactStorageKey = await _workspace.UploadArtifactAsync(
                    context.Execution.ProjectId,
                    artifactLocalPath,
                    artifactFileName,
                    "application/json",
                    context.CancellationToken);
            }
            finally
            {
                if (File.Exists(artifactLocalPath))
                    try { File.Delete(artifactLocalPath); } catch { }
            }

            sw.Stop();

            return new StepExecutionResult
            {
                NewIterationCount = context.IterationCount,
                Output = outputJson,
                NextStepIndex = context.CurrentStepIndex + 1,
                Status = StepStatus.Completed,
                ArtifactStorageKey = artifactStorageKey,
                DurationMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Same rule as the per-line clause above, one level up: the runner's cancellation is
            // reported as a cancellation (the executor rethrows and ExecutionCancellationRegistry
            // reports the recorded provenance), never laundered into "UNEXPECTED_ERROR" — which would
            // read as a defect in this executor and send whoever triages it looking in the wrong
            // place. VideoCompileStepExecutor and VideoAnalyzeStepExecutor already do exactly this.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception in VoiceoverStepExecutor");
            return Failure(context, sw, "UNEXPECTED_ERROR", $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Voice casting (docs/voiceover.md "Voice casting"): ONE <c>Choice</c> question to the Decision
    /// gate over the project's selectable voice ids, with the lines' text and each voice's own
    /// description as the state. Returns the casting node plus the voice to speak in: the chosen
    /// voice's vendor id when the gate ACCEPTED an answer naming an offered voice, otherwise the
    /// voice the caller already had. Soft-failure throughout — no resolver, no gate, no selectable
    /// voices, a gate that declined, or a throw all keep the configured voice and say why.
    /// </summary>
    private async Task<(object Node, string? VoiceId)> CastVoiceAsync(
        StepExecutionContext context,
        VoiceoverStepConfig config,
        List<(int Index, VoiceoverLine Original, string Sanitized)> sanitizedLines,
        string? currentVoiceId)
    {
        const string mode = nameof(VoiceCastingMode.Decision);
        if (_voiceResolver is null || _decisionGate is null)
            return (new { mode, applied = false, reason = "casting_unavailable" }, currentVoiceId);

        try
        {
            IReadOnlyList<ResolvedProjectVoice> offered = await _voiceResolver.ListSelectableAsync(context.Execution.ProjectId, context.CancellationToken);
            if (offered.Count == 0)
                return (new { mode, applied = false, reason = "no_selectable_voices", offeredCount = 0 }, currentVoiceId);
            if (offered.Count == 1)
                return (new { mode, applied = true, reason = "single_candidate", offeredCount = 1, chosenVoiceId = offered[0].Id }, offered[0].RemoteVoiceId);

            // Bounded state: the words to be spoken plus each candidate's own description. Ids are
            // the options; the model never sees a vendor id.
            string lines = string.Join("\n", sanitizedLines.Select(l => l.Sanitized).Where(t => t.Length > 0).Take(12));
            if (lines.Length > 2000)
                lines = lines[..2000];
            string candidates = string.Join("\n", offered.Select(v => $"{v.Id:D}: {v.Name}" + (string.IsNullOrWhiteSpace(v.Description) ? string.Empty : $" — {v.Description}")));
            var state = new DecisionState($"Narration lines:\n{lines}\n\nCandidate voices:\n{candidates}");
            var question = new DecisionGateQuestion(
                VoiceCastingSite, "voice", DecisionKind.Choice,
                "Which candidate voice should narrate these lines? Answer with the voice id that best fits the words' tone and audience.",
                offered.Select(v => v.Id.ToString("D")).ToList(),
                SystemTwoAnswer: null);

            DecisionGateOutcome outcome = await _decisionGate.DecideAsync(
                new DecisionGateRequest(
                    context.Execution.Id, context.Step.StepOrder, AgentType.VideoTransform, VoiceCastingSite,
                    state, [question], new DecisionGateConfig(Mode: DecisionGateMode.Gate)),
                context.CancellationToken);

            if (!outcome.HasDecision || !outcome.Accepted || outcome.Result is null ||
                !outcome.Result.Answers.TryGetValue("voice", out DecisionAnswer? answer) || answer.Choice is null)
            {
                return (new { mode, applied = false, reason = outcome.HasDecision ? "gate_declined" : "no_decision", offeredCount = offered.Count }, currentVoiceId);
            }

            ResolvedProjectVoice? chosen = offered.FirstOrDefault(v => string.Equals(v.Id.ToString("D"), answer.Choice, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
                return (new { mode, applied = false, reason = "answer_not_offered", offeredCount = offered.Count }, currentVoiceId);

            return (new { mode, applied = true, offeredCount = offered.Count, chosenVoiceId = chosen.Id, confidence = answer.Confidence }, chosen.RemoteVoiceId);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice casting failed; keeping the configured voice");
            return (new { mode, applied = false, reason = "casting_failed" }, currentVoiceId);
        }
    }

    /// <summary>
    /// Declares the position of every line of a <see cref="VoiceoverSourceKind.NarrationPlan"/>-sourced
    /// step, back-to-back in plan order: line 0 at <c>0.0</c> and each next line at the previous
    /// line's declared start plus that line's measured play duration (0 for a skipped or failed line,
    /// which synthesized nothing).
    ///
    /// <para>
    /// <b>Why the executor declares this rather than reading it.</b> <c>NarrationPlanOutput</c> has no
    /// numeric or time-bearing property at all, so the plan cannot say where a line begins, and this
    /// step holds no picture timeline to place one against. Laying the narration out first and
    /// measuring the picture against it afterwards is the whole contract: the compile step owns the
    /// picture fit (<c>narrationFitFacts</c>), this method owns the declaration, and neither reads the
    /// other. Deliberately NOT an analysis-step lookup — this step has no analysis-step reference in
    /// its config, and inventing one would be a new cross-step dependency for a number the compile
    /// already produces.
    /// </para>
    /// <para>
    /// <b>One walk, two consumers.</b> The emitted <c>view.lines[].startSec</c> and the DECLARED
    /// window <c>[start_i, start_{i+1})</c> the fit pass reads are written from the same assignment,
    /// so they cannot disagree. A consequence worth stating plainly: because the next line's start is
    /// the previous line's start plus its measured duration, a NarrationPlan line's declared window
    /// is exactly its own duration and its declared-window overflow is structurally zero. That is not
    /// a bug to fix here — the declared-window number answers "did the line overrun the slot it
    /// declared", and a narration-first line declares exactly the slot it occupies. Whether the
    /// narration overruns the SHOT it lands in is the compile's measurement.
    /// </para>
    /// <para>
    /// The positional contract with the caller is the same one the fit pass already relies on:
    /// <paramref name="outputLines"/> is 1:1 with <paramref name="sanitizedLines"/> and in the same
    /// order, because every branch of the synthesis loop appends exactly one entry to each. The
    /// bounds check is a guard, not an expectation.
    /// </para>
    /// </summary>
    private static void AssignNarrationPlanStarts(
        List<VoiceoverOutputLine> outputLines,
        List<(int Index, VoiceoverLine Original, string Sanitized)> sanitizedLines)
    {
        double startSec = 0.0;

        for (int i = 0; i < outputLines.Count; i++)
        {
            double durationSec = outputLines[i].DurationSec;

            outputLines[i] = outputLines[i] with { StartSec = startSec };

            if (i < sanitizedLines.Count)
            {
                sanitizedLines[i] = (
                    sanitizedLines[i].Index,
                    sanitizedLines[i].Original with { StartSec = startSec },
                    sanitizedLines[i].Sanitized);
            }

            // A non-finite or negative duration cannot advance a position (and a non-finite one
            // cannot even be serialized into the output JSON), so it advances by zero exactly like a
            // skipped or failed line — the same normalization NarrationFitSolver applies to its own
            // duration input, which keeps the declared positions consistent with the fit report.
            startSec += double.IsFinite(durationSec) && durationSec > 0 ? durationSec : 0.0;
        }
    }

    /// <summary>
    /// The Completed envelope for a well-formed <see cref="VoiceoverSourceKind.NarrationPlan"/>
    /// whose resolved line list is empty: an empty <c>view.lines</c>, <c>meta</c> counters that are
    /// consistent with "nothing speakable was produced" (<c>totalLines:0</c> — it counts RESOLVED
    /// lines — <c>allLinesFailed:false</c>), <c>meta.declaredLines</c> (the plan's own entry count
    /// before the blank-text filter) and <c>meta.emptyReason</c> — see
    /// <see cref="NarrationPlanEmptyReason"/> / <see cref="NarrationPlanLinesDiscardedReason"/>,
    /// which is what makes this distinguishable from a total failure rather than merely quieter
    /// than one, and distinguishes a plan that declared nothing from one whose declared lines were
    /// all discarded.
    ///
    /// <para>
    /// <b>The envelope shape is deliberately the same one the non-empty path emits</b>, with every
    /// count at zero: <c>alignment</c> and <c>fit</c> are present rather than omitted, because a
    /// Conditional step or a consumer reading <c>meta.fit.overflowLineCount</c> must not have to
    /// special-case the empty plan — "nothing overflowed" has to be readable as zeros here for the
    /// same reason it is on a step that produced lines.
    /// </para>
    /// <para>
    /// <b>No artifact is uploaded.</b> The artifact exists to carry a line list too large for an
    /// inline step result; there is no line list here, and the envelope above is its own complete
    /// record. Nor is a speech provider resolved, a line synthesized or a file written — the step
    /// completes by doing nothing, which is the entire point.
    /// </para>
    /// </summary>
    private static StepExecutionResult EmptyNarrationPlanSuccess(StepExecutionContext context, Stopwatch sw, int declaredLines)
    {
        sw.Stop();

        var output = new
        {
            view = new
            {
                lines = Array.Empty<object>()
            },
            meta = new
            {
                totalLines = 0,
                failedLines = 0,
                skippedLines = 0,
                allLinesFailed = false,
                // The marker: absent on every other Completed envelope. Its VALUE is the provenance
                // of the emptiness — zero declared entries is a decision, N declared entries that
                // all came out blank is a producer problem — and declaredLines carries the count so
                // the two cases are distinguishable without inference.
                emptyReason = declaredLines == 0 ? NarrationPlanEmptyReason : NarrationPlanLinesDiscardedReason,
                declaredLines,
                alignment = new
                {
                    applied = false,
                    alignedLines = 0,
                    unalignedLines = 0,
                    reasons = new SortedDictionary<string, int>(StringComparer.Ordinal)
                },
                fit = new
                {
                    measuredLineCount = 0,
                    overflowLineCount = 0,
                    overflowWords = 0,
                    overflowTotalSec = 0.0,
                    maxOverflowSec = 0.0,
                    unknownWindowLineCount = 0
                },
                overflowWords = 0,
                overflowLineCount = 0
            }
        };

        return new StepExecutionResult
        {
            NewIterationCount = context.IterationCount,
            Output = JsonSerializer.Serialize(output, OutputJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            Status = StepStatus.Completed,
            DurationMs = (int)sw.ElapsedMilliseconds
        };
    }

    /// <summary>One entry of the C1 <c>warnings</c> array: a stable code and a sentence a customer understands.</summary>
    internal sealed record StepOutputWarning(string Code, string Message);

    internal const string VoiceProviderFallbackCode = "voice_provider_fallback";
    internal const string VoiceProviderUnhealthyCode = "voice_provider_unhealthy";
    internal const string VoiceLanguageUnlistedCode = "voice_language_unlisted";

    /// <summary>The plain reason a Voiceover step is skipped when the run brief says the film is not narrated.</summary>
    internal const string SilentRunReason = "This run is not narrated (the run brief set the voice to none), so no voiceover was made.";

    /// <summary>
    /// The warnings the resolved provider's health calls for — empty for a healthy (or never-tested)
    /// provider, so a normal run's output stays byte-identical.
    /// </summary>
    internal static List<StepOutputWarning> ProviderHealthWarnings(ResolvedSpeechSynthesisProvider provider)
    {
        var warnings = new List<StepOutputWarning>();

        if (provider.SelectionReason == ProviderSelectionReason.HealthyFallback)
        {
            warnings.Add(new StepOutputWarning(
                VoiceProviderFallbackCode,
                "Your default voice service failed its last connection check, so another voice service that is "
                + "working narrated this video instead. The voice may sound different from earlier videos."));
        }

        if (provider.LastTestOk == false)
        {
            warnings.Add(new StepOutputWarning(
                VoiceProviderUnhealthyCode,
                "The voice service used for this narration failed its last connection check, so some or all of "
                + "the narration may be missing. Ask your administrator to re-test it on the AI providers page."));
        }

        return warnings;
    }

    /// <summary>
    /// Removes the two provider-health warnings (<see cref="VoiceProviderFallbackCode"/>, <see cref="VoiceProviderUnhealthyCode"/>)
    /// from a RECORDED output that is being replayed from the step-result cache. Those warnings describe the provider
    /// the narration was synthesized against at the moment it ran; a cache hit synthesizes nothing, so replaying them
    /// shows a "failed its last connection check" notice long after the provider was re-tested and recovered. Every
    /// other warning (e.g. <see cref="VoiceLanguageUnlistedCode"/>) is about the narration itself and stays. Returns
    /// the input unchanged (same string) when there is nothing to remove or it is not a JSON object.
    /// </summary>
    internal static string? WithoutProviderHealthWarnings(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return outputJson;

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(outputJson) is not System.Text.Json.Nodes.JsonObject node
                || node["warnings"] is not System.Text.Json.Nodes.JsonArray warnings)
                return outputJson;

            bool removed = false;
            for (int i = warnings.Count - 1; i >= 0; i--)
            {
                string? code = warnings[i]?["code"]?.GetValue<string>();
                if (code is VoiceProviderFallbackCode or VoiceProviderUnhealthyCode)
                {
                    warnings.RemoveAt(i);
                    removed = true;
                }
            }

            if (!removed)
                return outputJson;

            if (warnings.Count == 0)
                node.Remove("warnings");
            return node.ToJsonString(OutputJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return outputJson;
        }
    }

    /// <summary>
    /// Adds the top-level C1 <c>warnings</c> array to a serialized output object — ONLY when there is
    /// at least one, so an output without warnings is byte-identical to what it always was.
    /// </summary>
    internal static string WithWarnings(string outputJson, IReadOnlyList<StepOutputWarning> warnings)
    {
        if (warnings.Count == 0)
            return outputJson;

        var node = System.Text.Json.Nodes.JsonNode.Parse(outputJson)!.AsObject();
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (StepOutputWarning warning in warnings)
            array.Add(new System.Text.Json.Nodes.JsonObject { ["code"] = warning.Code, ["message"] = warning.Message });
        node["warnings"] = array;
        return node.ToJsonString(OutputJsonOptions);
    }

    /// <summary>
    /// T-006: screens the sanitized narration (one call) before speech synthesis. Returns the plain
    /// user message on a Block, or null to proceed; Review and an unavailable call proceed because
    /// generated text fails open. A gate that is not wired is a no-op.
    /// </summary>
    private async Task<string?> ScreenGeneratedTextAsync(StepExecutionContext context, string text)
    {
        if (_moderation is null || string.IsNullOrWhiteSpace(text))
            return null;

        var attribution = new ModerationAttribution(
            context.Execution.OrganizationId,
            context.Execution.ProjectId,
            context.Execution.InitiatedByUserId,
            context.Execution.Id,
            context.StepResultId,
            ModerationSurface.GeneratedText,
            ModerationContentKind.Text);

        ModerationDecision decision = await _moderation.ModerateAsync(
            new ModerationInput(text, [], ModerationContentKind.Text), attribution,
            context.CancellationToken);

        if (decision.Action == ModerationAction.Block)
        {
            return string.IsNullOrWhiteSpace(decision.UserMessage)
                ? "This narration cannot be synthesized."
                : decision.UserMessage;
        }

        if (!decision.Available)
        {
            _logger.LogWarning(
                "Content moderation was unavailable for voiceover at step {StepOrder}; proceeding because generated text fails open",
                context.Step.StepOrder);
        }

        return null;
    }

    /// <summary>
    /// Who pays for a narration that does not fit. The user's own Inline text keeps the spend (UserInput):
    /// the audio already synthesized stays in the content-addressed cache and a re-run would get it free,
    /// so refunding it would hand out free TTS. Text our script step wrote (ScriptScenes) is platform-caused
    /// and refunded. NarrationPlan and PickupPlan lines occupy their own slot and never reach this check.
    /// The explicit class on the result wins over the static FailureClassifier table (default PlatformInternal).
    /// </summary>
    internal static FailureClass NarrationFitFailureClass(VoiceoverSourceKind kind, bool anyLineIsUserWording = false) =>
        kind == VoiceoverSourceKind.Inline || anyLineIsUserWording ? FailureClass.UserInput : FailureClass.PlatformInternal;

    /// <summary>
    /// A refunded failure after synthesis: delete the line objects this attempt newly created, so the refunded
    /// spend is not left in the content-addressed cache as free audio for a later run. A delete that fails is
    /// logged and never changes the failure.
    /// </summary>
    private async Task<StepExecutionResult> FailRefundedAsync(
        StepExecutionContext context, System.Diagnostics.Stopwatch sw, List<string> newlyCreatedKeys,
        string code, string message, FailureClass failureClass, string? userMessage = null)
    {
        foreach (string key in newlyCreatedKeys.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await _workspace.DeleteArtifactAsync(context.Execution.ProjectId, key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete voiceover line object {StorageKey} after a refunded failure", key);
            }
        }

        return Failure(context, sw, code, message, failureClass, userMessage);
    }

    private static StepExecutionResult Failure(
        StepExecutionContext context,
        Stopwatch sw,
        string code,
        string message,
        FailureClass failureClass = FailureClass.None,
        string? userMessage = null)
    {
        sw.Stop();
        var errorOutput = JsonSerializer.Serialize(
            new { view = new { lines = new List<object>() }, meta = new { totalLines = 0, failedLines = 0, skippedLines = 0, allLinesFailed = false } },
            OutputJsonOptions);

        return new StepExecutionResult
        {
            FailureClass = failureClass,
            FailureCode = failureClass == FailureClass.None ? null : code,
            UserMessage = userMessage,
            NewIterationCount = context.IterationCount,
            Output = errorOutput,
            NextStepIndex = context.CurrentStepIndex + 1,
            Status = StepStatus.Failed,
            ErrorDetails = $"{code}: {message}",
            DurationMs = (int)sw.ElapsedMilliseconds,
            Retryable = !DeterministicFailureCodes.Contains(code)
        };
    }

    /// <summary>
    /// Which lines a change request touches. A line is "changed" when its id (or <c>*</c>) carries overrides and
    /// "in scope" when named by the item scope; a request with neither names the whole step. A line neither in
    /// scope nor changed by a <c>*</c> override is carried over untouched.
    /// </summary>
    private sealed class ChangePlan(
        int lineCount, HashSet<string> explicitIds, IReadOnlyDictionary<string, JsonElement>? star,
        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> perLine)
    {
        private readonly bool _wholeStep = explicitIds.Count == 0 && star is not { Count: > 0 };

        /// <summary>How many of the step's lines the request matched (0 means <c>CHANGE_NOT_APPLIED</c>).</summary>
        public int Matched { get; } = star is { Count: > 0 } || explicitIds.Count == 0
            ? lineCount
            : explicitIds.Count(id => id.StartsWith("vo", StringComparison.Ordinal)
                && int.TryParse(id.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
                && n < lineCount && id == $"vo{n}");

        public IReadOnlyDictionary<string, JsonElement>? OverridesFor(int index)
        {
            perLine.TryGetValue($"vo{index}", out IReadOnlyDictionary<string, JsonElement>? own);
            if (star is not { Count: > 0 })
                return own;
            if (own is null)
                return star;
            Dictionary<string, JsonElement> merged = new(star, StringComparer.Ordinal);
            foreach ((string key, JsonElement value) in own)
                merged[key] = value;
            return merged;
        }

        public bool IsCarriedOver(int index) =>
            !_wholeStep && star is not { Count: > 0 } && !explicitIds.Contains($"vo{index}");
    }

    private static ChangePlan PlanChange(ChangeRequestPayload payload, int lineCount)
    {
        HashSet<string> explicitIds = new(StringComparer.Ordinal);
        foreach (string id in payload.ItemIds ?? [])
            explicitIds.Add(id);

        IReadOnlyDictionary<string, JsonElement>? star = null;
        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> perLine = new(StringComparer.Ordinal);
        foreach ((string key, IReadOnlyDictionary<string, JsonElement> keys) in payload.Overrides ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>())
        {
            if (key == "*")
            {
                star = keys;
            }
            else
            {
                perLine[key] = keys;
                explicitIds.Add(key);
            }
        }

        return new ChangePlan(lineCount, explicitIds, star, perLine);
    }

    private static Dictionary<string, VoiceoverOutputLine>? ParsePreviousLines(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (!doc.RootElement.TryGetProperty("view", out JsonElement view)
                || !view.TryGetProperty("lines", out JsonElement array)
                || array.ValueKind != JsonValueKind.Array)
                return null;

            Dictionary<string, VoiceoverOutputLine> result = new(StringComparer.Ordinal);
            foreach (JsonElement element in array.EnumerateArray())
            {
                VoiceoverOutputLine? line = element.Deserialize<VoiceoverOutputLine>(OutputJsonOptions);
                if (line is not null && !string.IsNullOrEmpty(line.Id))
                    result[line.Id] = line;
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A previous entry is carried only if it is an ok line with its audio, or a skipped empty one. A line outside the
    /// request has the same source text as in the run being changed (everything before the target is a copied result),
    /// so its previous wording is kept as it was said, even when the earlier run shortened it to fit.
    /// </summary>
    private static string? GlossarySpokenForm(string sanitized, IReadOnlyList<GlossaryEntry> glossary)
    {
        if (glossary.Count == 0 || string.IsNullOrWhiteSpace(sanitized))
            return null;
        string respelled = PronunciationRespeller.Apply(sanitized, glossary).Text;
        return string.Equals(respelled, sanitized, StringComparison.Ordinal) ? null : respelled;
    }

    private static bool CanCarry(VoiceoverOutputLine previous, string sanitized) => previous.Status switch
    {
        "ok" => !string.IsNullOrWhiteSpace(previous.StorageKey) && !string.IsNullOrWhiteSpace(sanitized),
        "skipped" => string.IsNullOrWhiteSpace(sanitized),
        _ => false
    };

    private static string? PreviousMetaVoice(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.TryGetProperty("meta", out JsonElement meta)
                && meta.TryGetProperty("voice", out JsonElement voice)
                && voice.TryGetProperty("remoteVoiceId", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? PreviousCastVoice(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.TryGetProperty("meta", out JsonElement meta)
                && meta.TryGetProperty("voice", out JsonElement voice)
                && voice.TryGetProperty("source", out JsonElement source) && source.GetString() == "cast"
                && voice.TryGetProperty("remoteVoiceId", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string WithChangeRequestMeta(string outputJson, Guid requestId, IReadOnlyList<string> redoneLineIds, int reusedLineCount)
    {
        System.Text.Json.Nodes.JsonNode? root = System.Text.Json.Nodes.JsonNode.Parse(outputJson);
        if (root?["meta"] is not System.Text.Json.Nodes.JsonObject meta)
            return outputJson;
        ChangeRequestEngineDiagnostics.Record(nameof(StepType.Voiceover), redoneLineIds.Count, reusedLineCount);
        meta["changeRequest"] = new System.Text.Json.Nodes.JsonObject
        {
            ["requestId"] = requestId.ToString("D"),
            ["redoneLineIds"] = new System.Text.Json.Nodes.JsonArray([.. redoneLineIds.Select(id => (System.Text.Json.Nodes.JsonNode?)id)]),
            ["reusedLineCount"] = reusedLineCount
        };
        return root.ToJsonString();
    }

    /// <summary>Pure input/config validation codes: the same input fails identically, so a retry only burns attempts. TTS/network/provider failures are not listed and stay retryable.</summary>
    private static readonly HashSet<string> DeterministicFailureCodes = new(StringComparer.Ordinal)
    {
        "CONFIG_INVALID", "VOICE_CHOICE_REQUIRED", "NO_LINES", "SCRIPT_SOURCE_INVALID", "SCRIPTWRITER_NOT_FOUND",
        "SCRIPTWRITER_OUTPUT_INVALID", "NARRATION_SOURCE_INVALID", "NARRATION_STEP_NOT_FOUND",
        "NARRATION_OUTPUT_INVALID", "PICKUP_SOURCE_INVALID", "PICKUP_STEP_NOT_FOUND",
        "PICKUP_OUTPUT_INVALID", "SOURCE_INVALID", "BYTE_BUDGET_EXCEEDED", "INPUT_TOO_LARGE",
        "CONTENT_BLOCKED", "CHANGE_NOT_APPLIED", "VOICE_NOT_FOUND", "VOICE_CONSENT_MISSING", "VOICE_RESOLVER_UNAVAILABLE",
        // A fit failure repeats identically on a retry (and would re-buy the synthesis): never retried.
        "NARRATION_DOES_NOT_FIT",
        // The spend bound is a pure validation of the step's declared words: same input, same answer.
        "NARRATION_TOO_MANY_WORDS"
    };

    /// <summary>
    /// Builds an <c>ok</c> line for an artifact that already exists in the object store, by
    /// downloading it to scratch and probing it. Throws on any failure, exactly like the synthesis
    /// path, so the caller's per-line catch records it as a failed line rather than silently
    /// emitting a duration nobody measured.
    /// </summary>
    private async Task<VoiceoverOutputLine> EmitFromExistingArtifactAsync(
        Guid projectId,
        string id,
        double startSec,
        string storageKey,
        CancellationToken ct)
    {
        string scratchPath = Path.Combine(Path.GetTempPath(), $"voiceover_probe_{Guid.NewGuid()}.wav");
        try
        {
            await _workspace.DownloadStorageKeyToFileAsync(projectId, storageKey, scratchPath, ct);

            var probeResult = await _mediaProbe.ProbeAsync(scratchPath, ct);

            return await BuildOkLineAsync(projectId, id, startSec, probeResult.DurationSec, storageKey, ct);
        }
        finally
        {
            if (File.Exists(scratchPath))
                try { File.Delete(scratchPath); } catch { }
        }
    }

    /// <summary>
    /// Speeds one line up by <paramref name="tempo"/> with ffmpeg <c>atempo</c> (first-party argv, no
    /// model-supplied value) and stores the result under a NEW content-addressed key derived from the
    /// original's (<c>{key}-t{tempo}.wav</c>). Returns the rebuilt line (new duration, key and words), or
    /// null when anything goes wrong, leaving the line as it was.
    /// </summary>
    private async Task<VoiceoverOutputLine?> TryRetimeAsync(Guid projectId, VoiceoverOutputLine line, double tempo, List<string> newlyCreatedKeys, CancellationToken ct)
    {
        string inPath = Path.Combine(Path.GetTempPath(), $"voiceover_in_{Guid.NewGuid()}.wav");
        string outPath = Path.Combine(Path.GetTempPath(), $"voiceover_out_{Guid.NewGuid()}.wav");
        try
        {
            await _workspace.DownloadStorageKeyToFileAsync(projectId, line.StorageKey!, inPath, ct);
            string tempoText = tempo.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            string[] args =
            [
                "-hide_banner", "-nostdin", "-y", "-i", inPath, "-vn",
                "-filter:a", "atempo=" + tempoText, "-c:a", "pcm_s16le", outPath
            ];
            VideoToolResult run = await _toolRunner!.RunFfmpegAsync(args, TimeSpan.FromSeconds(60), ct);
            if (!run.Succeeded)
                return null;
            MediaProbeResult probe = await _mediaProbe.ProbeAsync(outPath, ct);
            if (probe.DurationSec <= 0 || probe.AudioCodec is null)
                return null;

            string name = Path.GetFileNameWithoutExtension(line.StorageKey!);
            string relative = $"voiceover/lines/{name}-t{tempoText.Replace('.', 'p')}.wav";
            string retimedKey = ProjectFilePath.BuildStorageKey(projectId, ArtifactCategory, directoryPath: null, storageFileName: relative);
            bool existed = await _workspace.ArtifactExistsAsync(projectId, retimedKey, ct);
            string key = await _workspace.UploadArtifactAsync(projectId, outPath, relative, "audio/wav", ct);
            if (!existed)
                newlyCreatedKeys.Add(key);
            return await BuildOkLineAsync(projectId, line.Id, line.StartSec, probe.DurationSec, key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retime line {Id}; leaving it as synthesized", line.Id);
            return null;
        }
        finally
        {
            foreach (string p in new[] { inPath, outPath })
                if (File.Exists(p))
                    try { File.Delete(p); } catch { }
        }
    }

    /// <summary>
    /// The single place an <c>ok</c> line is built, for BOTH paths that produce one — a freshly
    /// synthesized line and an object-store cache hit. Alignment lives here rather than at each call
    /// site for a reason the feature's own acceptance criteria name explicitly: a repeat run that
    /// hits the cache must not silently lose its words, and the only way to guarantee that is to
    /// make the two paths share the construction.
    ///
    /// <para>
    /// The alignment service owns its own download of the same object, because its contract is
    /// <c>AlignAsync(projectId, storageKey, ct)</c>. On the cache-hit path that means the WAV is
    /// fetched twice (once here to probe, once inside the service to transcribe). Accepted: the
    /// contract is what makes the service independently testable and reusable by the captions/fit
    /// work in later waves, and a second GET of a small WAV is cheaper than threading a scratch path
    /// through an interface whose whole point is the storage key.
    /// </para>
    /// </summary>
    private async Task<VoiceoverOutputLine> BuildOkLineAsync(
        Guid projectId,
        string id,
        double startSec,
        double durationSec,
        string storageKey,
        CancellationToken ct)
    {
        VoiceoverAlignmentResult alignment = await _alignmentService.AlignAsync(projectId, storageKey, ct);

        return new VoiceoverOutputLine(
            Id: id,
            StartSec: startSec,
            Status: "ok",
            DurationSec: durationSec,
            StorageKey: storageKey,
            // Exactly one of the two is emitted. `words` present means alignment applied;
            // `alignmentReason` present means it did not; both absent never happens for an `ok`
            // line, and the pair is written from ONE branch so the two can never disagree.
            Words: alignment.Applied ? alignment.Words : null,
            AlignmentReason: alignment.Applied ? null : alignment.Reason);
    }

    /// <summary>
    /// True when the file starts with a canonical RIFF/WAVE container header — ASCII "RIFF" in
    /// bytes 0..3 and "WAVE" in bytes 8..11. This is the container check only: whether the stream
    /// inside it is actually decodable is <see cref="IMediaProbe"/>'s job, and both are required
    /// before an artifact is uploaded under a content-addressed key.
    /// </summary>
    private static bool HasRiffWaveHeader(string path)
    {
        Span<byte> header = stackalloc byte[12];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < header.Length)
            return false;

        stream.ReadExactly(header);

        return header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E';
    }

    /// <summary>
    /// The per-line content address: lowercase SHA-256 hex over a length-prefixed material, used as
    /// the <c>voiceover/lines/{contentKey}.wav</c> path segment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The material is EXACTLY, in order: the literal tag <c>voiceover-line-v2</c>, then the text,
    /// the effective voice id, the effective model, the provider id and the endpoint — the last five
    /// each written as <c>value.Length + ":" + value</c> (UTF-16 code-unit count), and all six
    /// fields joined by <c>\n</c>. The prefix is not length-prefixed itself; it is a fixed literal.
    /// </para>
    /// <para>
    /// Length-prefixing is what makes the encoding injective, and that is not theoretical: the
    /// previous material was <c>$"{text}|{voiceId}|{model}|"</c>, where the separator is a legal
    /// character in every one of those fields. Both fields are workflow-config values, so
    /// <c>text="Hello|world", voiceId="abc"</c> and <c>text="Hello", voiceId="world|abc"</c> hashed
    /// identically — the second line was never synthesized and narrated the first line's words.
    /// With a length prefix no two distinct field tuples can produce the same string.
    /// </para>
    /// <para>
    /// The tag versions the formula. It is bumped to <c>v2</c> by this change because the key covers
    /// more inputs (effective model, provider id, endpoint) than the old one did; changing the
    /// material without a tag would make "same key, different meaning" invisible in the store. A
    /// consequence, accepted deliberately: every per-line WAV written under the old formula is now a
    /// cache miss and is re-synthesized once. Do NOT add a fallback lookup for the old key.
    /// </para>
    /// <para>
    /// <c>internal</c> rather than private so the test project (which this assembly exposes through
    /// <c>InternalsVisibleTo</c>) can pin the material directly instead of re-deriving it.
    /// </para>
    /// </remarks>
    internal static string ComputeLineContentKey(
        string text,
        string? voiceId,
        string? model,
        string? providerId,
        string? endpoint,
        IReadOnlyList<PronunciationEntry>? pronunciation = null,
        string? fishLanguage = null,
        double? speed = null)
    {
        static string Field(string? value)
        {
            string v = value ?? string.Empty;
            return $"{v.Length}:{v}";
        }

        string material = string.Join(
            '\n',
            "voiceover-line-v2",
            Field(text),
            Field(voiceId),
            Field(model),
            Field(providerId),
            Field(endpoint));

        // A pronunciation dictionary changes the audio, so it joins the material — appended under
        // its own tag and ONLY when non-empty, so every line without one keeps the exact v2 key
        // (and the object a phase-1 run already uploaded). Each entry is length-prefixed like every
        // other field, so the join stays injective.
        if (pronunciation is { Count: > 0 })
        {
            material += "\npronunciation-v1";
            foreach (PronunciationEntry entry in pronunciation)
                material += "\n" + Field(entry.Key) + "\n" + Field(entry.Value) + "\n" + (entry.CaseSensitive ? "1" : "0");
        }

        // The language tag Fish was sent changes number/date reading, so it joins the material under its own
        // marker and ONLY when non-null: lines without one keep their previous key.
        if (fishLanguage is not null)
            material += "\nlanguage-v1\n" + Field(fishLanguage);

        // A speaking-rate override changes the audio, so it joins the material under its own tag and ONLY
        // when set: every line without one keeps its existing key (and the object already uploaded).
        if (speed is double rate)
            material += "\nspeed-v1\n" + Field(rate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            // Lowercase: this hex string is a URL path segment (voiceover/lines/{contentKey}.wav),
            // and lowercasing keeps the key byte-identical whatever casing any future consumer
            // compares with.
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    private sealed record VoiceoverOutputLine(
        string Id,
        double StartSec,
        string Status,
        double DurationSec,
        // Omitted (not null) when there is no artifact, so "the field is absent" is exactly the
        // signal a consumer uses to fall back to the legacy voiceover/{executionId}/{lineId}.wav
        // key for step results persisted before this change.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StorageKey,
        // The per-word timings, present ONLY when alignment applied. Omitted rather than null, for
        // the same reason StorageKey is: "field absent" has to keep meaning exactly one thing, so a
        // consumer can distinguish "this line has no words to give" from "this line has an empty
        // word list". Serialized as [{text,startSec,endSec}] by OutputJsonOptions' web defaults.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<VoiceoverAlignedWord>? Words,
        // Why alignment did not apply, present ONLY when it did not. The mirror image of Words:
        // exactly one of the two is ever written, and neither is ever written as null.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AlignmentReason,
        // The deterministic fit verdict for this line, present ONLY on the statuses that have one to
        // report (`ok` and `skipped`) and OMITTED — never written as null — on a `failed` line, whose
        // audio does not exist and was therefore never measured. Same convention as StorageKey, and
        // set from ONE pass over the finished list rather than at each emit site; see behaviour (8)
        // in the class summary for why the window is the DECLARED one, not the compile's.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NarrationFitLineResult? Fit = null,
        // The SANITIZED text this line actually spoke — what the compile step burns as a caption and
        // scores WER against. Never the raw config/plan text: a caption that says words the
        // narrator never spoke is a correctness bug. Present on every `ok`/`skipped` line (a skipped
        // line's text is empty), OMITTED on a `failed` line. Appended for phase 3; a step output
        // persisted before it simply lacks the key and contributes no caption.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        // The opaque cut-anchor id the line was written for — present ONLY for a NarrationPlan or
        // PickupPlan line, where the compile step needs it to place a pickup over the transcript
        // segment it repairs. Omitted otherwise.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AnchorId = null,
        // The vendor voice id the line was actually spoken in, on `ok` lines whose voice is known. A change
        // request re-checks it (consent, project scope) before carrying the line over; a previous output
        // without it is re-derived or the line is made again. Appended last, additive.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VoiceId = null,
        // What the speech provider was sent for the line when the project's pronunciations respelled it (the written
        // `text` stays the caption's words). Omitted when it equals `text`. A later change request compares it with the
        // glossary as it stands then, to remake lines whose pronunciation changed. Appended last, additive.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Spoken = null);
}
