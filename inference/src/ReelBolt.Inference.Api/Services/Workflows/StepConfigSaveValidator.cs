using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Save-time validation of step configs, shared by every path that persists workflow steps
/// (workflow create/update, the assistant Apply endpoint and ApplyWorkflow tool) and by
/// <see cref="WorkflowEditDiffService.ValidateProposedSteps"/>, so they all agree on every input.
///
/// <para>
/// Every step type that carries a config blob must carry one. The executors hard-fail a step whose
/// config JSON is missing or blank (<c>ExtractStepExecutor</c> and <c>VideoAnalyzeStepExecutor</c>
/// both return CONFIG_INVALID, <c>VideoCompileStepExecutor</c> returns "no VideoCompileConfigJson
/// configured", and the three room executors likewise), so such a step is guaranteed to fail at
/// execution time and must be rejected here instead. The workflow builder already upholds this by
/// writing a default config the moment a step is added; the assistant's propose/apply path did not,
/// and a workflow whose VideoCompile step had no config ran through ~82 minutes of analysis before
/// dying on its final step.
/// </para>
///
/// <para>
/// <see cref="StepType.VideoGenerate"/> additionally gets domain-aware validation: its config is
/// deserialized with the executor's own <see cref="VideoGenerateStepConfig.JsonOptions"/> and run
/// through the static, provider-free <see cref="VideoGenerateStepConfig.Validate()"/>. The
/// capability-aware overload needs the resolved provider and stays run-time only.
/// </para>
///
/// <para>
/// <see cref="StepType.Voiceover"/> gets a domain-aware arm too, and it is deliberately ADDITIVE:
/// only a config that deserializes to <see cref="VoiceoverSourceKind.NarrationPlan"/> is checked
/// further (that kind's <see cref="VoiceoverSource.NarrationWriterStepOrder"/> is required), while
/// <see cref="VoiceoverSourceKind.Inline"/> and <see cref="VoiceoverSourceKind.ScriptScenes"/> keep
/// exactly the three checks they had before — missing/blank, invalid JSON, not a JSON object — so
/// nothing that saves today starts failing. See <see cref="ValidateVoiceoverConfig"/>.
/// </para>
/// </summary>
public static class StepConfigSaveValidator
{
    /// <summary>
    /// Returns one "Step &lt;order&gt; (VideoGenerate): &lt;error&gt;" line per failure across
    /// <paramref name="steps"/>; empty when every step is savable. Step types are parsed exactly as
    /// the save paths parse them, so a step saved as VideoGenerate is always the one checked.
    /// </summary>
    /// <param name="steps">The steps to save.</param>
    /// <remarks>
    /// The reserved <c>{{input.</c> and the <c>appInputs</c> key (docs/design/app-inputs.md 15) are allowed only where
    /// <see cref="AppBindableCatalog"/> lists the setting.
    /// </remarks>
    public static IReadOnlyList<string> Validate(IEnumerable<CreateWorkflowStepRequest> steps)
    {
        var stepList = steps.ToList();
        var errors = new List<string>();
        foreach (CreateWorkflowStepRequest step in stepList)
        {
            errors.AddRange(ValidateStep(step));
        }

        errors.AddRange(ValidateDesignSystemComposeReferences(stepList));
        errors.AddRange(InsertContentRefSaveValidator.Validate(stepList));
        errors.AddRange(ValidateAppInputBindings(stepList));

        return errors;
    }

    /// <summary>
    /// The app-input binding rules of a save (docs/design/app-inputs.md 5.1, 5.2, 7): every <c>{{input.</c> must open a
    /// complete placeholder inside a setting the allowlist marks as text; every <c>appInputs</c> key must be a setting the
    /// allowlist marks as settable (or <c>@model</c> where a model can be chosen) mapped to an input id. Whether the ids
    /// are declared, and the types fit, is the manifest validator's (<see cref="AppManifestValidator"/>).
    /// </summary>
    public static IReadOnlyList<string> ValidateAppInputBindings(IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        IReadOnlyList<AppBindingStep> bindingSteps = BindingStepsOf(steps);
        AppBindingMap map = AppBindingAnalysis.Analyze(null, bindingSteps);
        return [.. map.Problems.Select(p => p.Message)];
    }

    /// <summary>The steps of a save request in the shape the shared app-input logic reads.</summary>
    public static IReadOnlyList<AppBindingStep> BindingStepsOf(IEnumerable<CreateWorkflowStepRequest> steps) =>
    [
        .. steps.Select(s =>
        {
            StepType type = TryParseStepType(s.StepType, out StepType parsed) ? parsed : StepType.Agent;
            string? json = type switch
            {
                // The three columns ConfigJsonFor leaves out (it answers "which config must be present").
                StepType.VideoGenerate => s.VideoGenerateConfigJson,
                StepType.Conditional => s.ConditionalConfigJson,
                StepType.ReviewLoop => s.ReviewLoopConfigJson,
                _ => ConfigJsonFor(s, type).ConfigJson
            };
            return new AppBindingStep(s.StepOrder, type, json, s.ConditionExpression, s.TrueBranchStepOrder,
                s.FalseBranchStepOrder, s.LoopTargetStepOrder, s.AgentDefinitionId, s.Label);
        })
    ];

    /// <summary>
    /// The formatted errors for a single step: empty when the step is savable. Covers a
    /// config-bearing type with no config, a config that is not a JSON object, and
    /// VideoGenerate's own domain rules.
    /// </summary>
    public static IReadOnlyList<string> ValidateStep(CreateWorkflowStepRequest step, bool allowSeededTargets = false) =>
        [.. StepOptionsSaveValidator.ValidateShape(step), .. ValidateStepConfig(step, allowSeededTargets)];

    /// <summary>
    /// A tapped target (<c>seedPoint</c>, or <c>noWords</c>) is refunded when it finds nothing and is capped only on the hidden
    /// object-analysis path, so a workflow a person or the assistant authors may not carry one.
    /// </summary>
    public static IReadOnlyList<string> ValidateNoSeededTargets(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "trackObjects", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement target in property.Value.EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.Object) continue;
                foreach (JsonProperty member in target.EnumerateObject())
                {
                    bool seeded = string.Equals(member.Name, "seedPoint", StringComparison.OrdinalIgnoreCase)
                            && member.Value.ValueKind != JsonValueKind.Null
                        || string.Equals(member.Name, "noWords", StringComparison.OrdinalIgnoreCase)
                            && member.Value.ValueKind == JsonValueKind.True;
                    if (seeded)
                        return ["trackObjects: a target cannot name an object by a tap (seedPoint, noWords) in a workflow; use the Objects panel."];
                }
            }
        }

        return [];
    }

    private static IReadOnlyList<string> ValidateStepConfig(CreateWorkflowStepRequest step, bool allowSeededTargets)
    {
        if (step.StepType == null || !Enum.TryParse<StepType>(step.StepType, out var stepType))
        {
            return [];
        }

        // VideoGenerate keeps its deeper, domain-aware validation (missing/blank, invalid JSON, a
        // JSON null, plus VideoGenerateStepConfig.Validate()).
        if (stepType == StepType.VideoGenerate)
        {
            return ValidateVideoGenerateConfig(step.VideoGenerateConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.VideoGenerate}): {error}")
                .ToList();
        }

        // TimelineRender is the other step type whose config carries human-authored numbers that
        // reach ffmpeg (dimensions, CRF, bitrate, loudness targets): the same deeper validation,
        // so an out-of-range value is a 400 at save time and never an argv.
        if (stepType == StepType.TimelineRender)
        {
            return ValidateTimelineRenderConfig(step.TimelineRenderConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.TimelineRender}): {error}")
                .ToList();
        }

        // DesignSystem carries a structured config with its own Validate() plus cross-step
        // references the workflow-aware pass below checks: the same deeper validation.
        if (stepType == StepType.DesignSystem)
        {
            return ValidateDesignSystemConfig(step.DesignSystemConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.DesignSystem}): {error}")
                .ToList();
        }

        // PinInsert (T-206): its pins name regions and content references; the same deep validation as run time.
        if (stepType == StepType.PinInsert)
        {
            return ValidatePinInsertConfig(step.PinInsertConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.PinInsert}): {error}")
                .ToList();
        }

        // AudioGenerate spends money per take: its config is checked against the audio model catalogue's ranges at
        // save time (the chosen model's own range again at run time, before any ledger row).
        if (stepType == StepType.AudioGenerate)
        {
            return ValidateAudioGenerateConfig(step.AudioGenerateConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.AudioGenerate}): {error}")
                .ToList();
        }

        // ImageGenerate spends money per picture: Prompt/Instruction, Source, Variations and size are checked here, so a bad
        // value is a 400 at save time (the executor re-checks with the same Validate and answers CONFIG_INVALID).
        if (stepType == StepType.ImageGenerate)
        {
            return ValidateImageGenerateConfig(step.ImageGenerateConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.ImageGenerate}): {error}")
                .ToList();
        }

        // ExtractFrame carries human-authored numbers that reach ffmpeg (time, crop, size): the same
        // deeper validation, so an out-of-range value is a 400 at save time and never an argv.
        if (stepType == StepType.ExtractFrame)
        {
            return ValidateExtractFrameConfig(step.ExtractFrameConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.ExtractFrame}): {error}")
                .ToList();
        }

        // AudioCleanup: the target, the source shape, the provider and the cap are checked here, so a bad value is a 400
        // at save time (the executor re-checks with the same Validate and answers CONFIG_INVALID).
        if (stepType == StepType.AudioCleanup)
        {
            return ValidateAudioCleanupConfig(step.AudioCleanupConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.AudioCleanup}): {error}")
                .ToList();
        }

        // VideoEnhance: the operation, engine, source shape, provider, cap and every parameter are checked here, so a bad
        // value is a 400 at save time naming the field (the executor re-checks with the same Validate: CONFIG_INVALID).
        if (stepType == StepType.VideoEnhance)
        {
            return ValidateVideoEnhanceConfig(step.VideoEnhanceConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.VideoEnhance}): {error}")
                .ToList();
        }

        // ThumbnailGenerate: every field the ffmpeg argv uses is checked here and named, so a bad value is a 400
        // at save time (the executor re-checks with the same Validate and answers CONFIG_INVALID).
        if (stepType == StepType.ThumbnailGenerate)
        {
            return ValidateThumbnailGenerateConfig(step.ThumbnailGenerateConfigJson)
                .Select(error => $"Step {step.StepOrder} ({StepType.ThumbnailGenerate}): {error}")
                .ToList();
        }

        (string? configJson, string propertyName) = ConfigJsonFor(step, stepType);
        if (propertyName.Length == 0)
        {
            // Agent, Conditional, ForEach, ReviewLoop and Parallel carry no config blob at all.
            return [];
        }

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return [$"Step {step.StepOrder} ({stepType}): {propertyName} is required."];
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(configJson);
        }
        catch (JsonException ex)
        {
            return [$"Step {step.StepOrder} ({stepType}): {propertyName} is not valid JSON: {ex.Message}"];
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [$"Step {step.StepOrder} ({stepType}): {propertyName} must be a JSON object."];
            }

            // The domain-aware arm runs only AFTER the three generic checks above have passed, so a
            // Voiceover config is reported for at most one reason at a time and the Inline/
            // ScriptScenes behaviour is provably untouched (those configs reach `return []` below
            // exactly as they did before this arm existed).
            if (stepType == StepType.Voiceover)
            {
                return ValidateVoiceoverConfig(configJson)
                    .Select(error => $"Step {step.StepOrder} ({stepType}): {error}")
                    .ToList();
            }

            // Keyframed reframing / subject safe area / absolute music gain: the same checks the compile
            // re-runs before it builds a graph (VideoReframe.CheckConfig for caps and ranges,
            // VideoCompileStepConfig.Validate for MusicGainDb's -40..+6 range). Only a config that sets
            // one of these is deserialized, so no older stored config starts failing.
            if (stepType == StepType.VideoCompile && MusicNeedsReencode(doc.RootElement) is { } musicRefusal)
            {
                // The compile executor refuses the same combination, but only after the paid steps before it ran.
                return [$"Step {step.StepOrder} ({stepType}): {musicRefusal}"];
            }
            if (stepType == StepType.VideoCompile && SetsCompileConstraints(doc.RootElement))
            {
                return ValidateVideoCompileReframing(configJson)
                    .Select(error => $"Step {step.StepOrder} ({stepType}): {error}")
                    .ToList();
            }

            // T-010: a StepResult source must carry its id. Only that kind is checked, so no stored
            // config of any older kind starts failing here.
            // T-021: and every property must be one this backend reads — a field the web offers and the
            // executor silently ignores is a control that does nothing (matchScope was exposed on the
            // form before its member existed), so an unknown property fails the SAVE rather than drifting
            // into a no-op at run time.
            if (stepType == StepType.VideoAnalyze)
            {
                return
                [
                    .. StepResultSourcesOf(configJson)
                        .Select(source => source.Validate())
                        .OfType<string>()
                        .Select(error => $"Step {step.StepOrder} ({stepType}): {error}"),
                    .. ValidateVideoAnalyzeProperties(doc.RootElement)
                        .Select(error => $"Step {step.StepOrder} ({stepType}): {error}"),
                    .. (allowSeededTargets ? [] : ValidateNoSeededTargets(doc.RootElement))
                        .Select(error => $"Step {step.StepOrder} ({stepType}): {error}"),
                    .. ValidateVideoAnalyzeInputProfiles(configJson)
                        .Select(error => $"Step {step.StepOrder} ({stepType}): {error}")
                ];
            }
        }

        return [];
    }

    private static readonly JsonSerializerOptions VideoCompileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>QA-P1-19 item 1: the config members the save path checks — the ones whose range the compile
    /// would otherwise only refuse at run time.</summary>
    /// <summary>log-footage-color: the SourceInputProfiles map of a VideoAnalyze config (ids or "auto"); empty when unset or not readable (the executor owns shape errors).</summary>
    private static IReadOnlyList<string> ValidateVideoAnalyzeInputProfiles(string configJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, "sourceInputProfiles", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Object) continue;
                Dictionary<int, string>? map = p.Value.Deserialize<Dictionary<int, string>>(SourceJsonOptions);
                return SourceInputProfileRules.Validate(map, "SourceInputProfiles");
            }
        }
        catch (JsonException)
        {
            return ["SourceInputProfiles must map a source number to a camera profile id or 'auto'."];
        }

        return [];
    }

    private static bool SetsCompileConstraints(JsonElement root) =>
        root.EnumerateObject().Any(p =>
            (string.Equals(p.Name, "reframes", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "followSubject", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "subjectSafeArea", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "musicGainDb", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "captionSource", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "captionTranslationStepOrder", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "mediaOverlays", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "mediaOverlayPlan", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "maxMediaOverlays", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "mediaOverlaySafeMargin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "sourceInputProfiles", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "rateControl", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, "videoBitrateKbps", StringComparison.OrdinalIgnoreCase)) &&
            p.Value.ValueKind != JsonValueKind.Null);

    /// <summary>
    /// Background music mixes into the encoded audio, so it needs <c>Mode = Reencode</c> and an audio codec that is not
    /// <c>copy</c>. Checked at save when <c>enableMusic</c> is true, and, because the run gate saves the config
    /// bound with the run page's values, again before any credit is held: a music switch on the page can never turn on a
    /// bed the compile would refuse after the paid shots were made. Null when the config is fine.
    /// </summary>
    public static string? MusicNeedsReencode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        bool music = false, streamCopy = false, copyAudio = false;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (string.Equals(p.Name, "enableMusic", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)
                music = true;
            else if (string.Equals(p.Name, "mode", StringComparison.OrdinalIgnoreCase))
                streamCopy = p.Value.ValueKind == JsonValueKind.String
                    ? string.Equals(p.Value.GetString(), nameof(VideoCompileMode.StreamCopy), StringComparison.OrdinalIgnoreCase)
                    : p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int mode) && mode == (int)VideoCompileMode.StreamCopy;
            else if (string.Equals(p.Name, "audioCodec", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                copyAudio = string.Equals(p.Value.GetString(), "copy", StringComparison.OrdinalIgnoreCase);
        }

        if (!music) return null;
        if (streamCopy)
            return "MUSIC_REQUIRES_REENCODE: background music needs Mode=Reencode; stream-copy has no audio filtergraph to mix into.";
        if (copyAudio)
            return "MUSIC_REQUIRES_AUDIO_REENCODE: background music is incompatible with AudioCodec=copy; the mixed audio must be encoded.";
        return null;
    }

    /// <summary>
    /// The raw (unprefixed) compile-config errors: <c>CODE: message</c> from
    /// <see cref="VideoReframe.CheckConfig"/>, and every entry of
    /// <see cref="VideoCompileStepConfig.Validate()"/> (QA-P1-19 item 1: MusicGainDb's range), or a shape
    /// error when it does not deserialize.
    /// </summary>
    public static IReadOnlyList<string> ValidateVideoCompileReframing(string configJson)
    {
        VideoCompileStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoCompileStepConfig>(configJson, VideoCompileJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return [$"VideoCompileConfigJson is not a valid compile config: {ex.Message}"];
        }

        if (config is null)
            return [];

        List<string> errors = [];
        VideoReframeValidation check = VideoReframe.CheckConfig(config);
        if (!check.IsValid)
            errors.Add($"{check.ErrorCode}: {check.Message}");

        // QA-P1-19 item 1: the same range the compile re-checks, so the save path answers 400 rather than
        // letting a run reach the executor and fail there.
        errors.AddRange(config.Validate());
        return errors;
    }

    /// <summary>The engine's own config options (<c>VideoAnalyzeStepExecutor.ConfigJsonOptions</c>): Web defaults, so property names are case-insensitive.</summary>
    private static readonly JsonSerializerOptions SourceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] SourceProperties = ["source", "sources"];

    /// <summary>
    /// The step type exactly as the save path will store it: <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>
    /// already accepts surrounding whitespace and numeric strings (<c>"6"</c>), and case is ignored too, so no
    /// spelling that ends up stored as a type escapes that type's save-time checks (S-03).
    /// </summary>
    internal static bool TryParseStepType(string? value, out StepType stepType)
    {
        stepType = default;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value.Trim(), ignoreCase: true, out stepType);
    }

    /// <summary>
    /// Every <see cref="VideoSourceKind.StepResult"/> source in a VideoAnalyze config's <c>source</c> and
    /// <c>sources</c> (T-010), read the way the engine reads them: property names case-insensitively (S-04), so
    /// <c>"Sources"</c> or <c>"SOURCE"</c> is checked like <c>"sources"</c>. Every property whose name matches is
    /// scanned — a superset of the one the engine binds when a name repeats in another case. A member that does
    /// not parse is skipped: the executor owns that failure.
    /// </summary>
    private static List<VideoSourceRef> StepResultSourcesOf(string configJson)
    {
        List<VideoSourceRef> found = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return found;
            }

            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (!SourceProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                JsonElement element = property.Value;
                IEnumerable<JsonElement> candidates = element.ValueKind == JsonValueKind.Array
                    ? element.EnumerateArray()
                    : [element];
                foreach (JsonElement candidate in candidates)
                {
                    if (candidate.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    try
                    {
                        VideoSourceRef? source = candidate.Deserialize<VideoSourceRef>(SourceJsonOptions);
                        if (source is { Kind: VideoSourceKind.StepResult })
                        {
                            found.Add(source);
                        }
                    }
                    catch (JsonException)
                    {
                        // Not a parseable source; the executor reports it at run time.
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON is reported by the generic checks in ValidateStep.
        }

        return found;
    }

    /// <summary>
    /// Project-scoped check of every <see cref="VideoSourceKind.StepResult"/> source across
    /// <paramref name="steps"/> (T-010): <paramref name="existsInProject"/> answers whether a step result id
    /// belongs to the workflow's project. A foreign id and a missing id get the SAME wording, so the
    /// response cannot be used to probe other projects. Lookups are made once per distinct id.
    /// </summary>
    public static Task<IReadOnlyList<string>> ValidateStepResultSourcesAsync(
        IEnumerable<CreateWorkflowStepRequest> steps,
        Func<Guid, CancellationToken, Task<bool>> existsInProject,
        CancellationToken ct) =>
        ValidateStepResultSourcesAsync(
            steps,
            async (id, token) => await existsInProject(id, token) ? StepResultSourceVerdict.Usable : StepResultSourceVerdict.NotFound,
            ct);

    /// <summary>
    /// The full save-time check (QA-P0-7): <paramref name="verdictOf"/> says whether a step result id is a usable
    /// source of the workflow's project (see <see cref="StepResultSourceVerdictOfAsync"/>). Foreign and missing
    /// share one message; a same-project result that has not finished successfully, or produced no video, gets
    /// its own — those are the caller's own results, so saying which leaks nothing.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateStepResultSourcesAsync(
        IEnumerable<CreateWorkflowStepRequest> steps,
        Func<Guid, CancellationToken, Task<StepResultSourceVerdict>> verdictOf,
        CancellationToken ct)
    {
        List<string> errors = [];
        Dictionary<Guid, StepResultSourceVerdict> verdicts = [];
        foreach ((CreateWorkflowStepRequest step, Guid id) in StepResultReferences(steps))
        {
            if (!verdicts.TryGetValue(id, out StepResultSourceVerdict verdict))
            {
                verdict = await verdictOf(id, ct);
                verdicts[id] = verdict;
            }

            if (Refusal(step, id, verdict) is { } error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    /// <summary>The synchronous twin of <see cref="ValidateStepResultSourcesAsync(IEnumerable{CreateWorkflowStepRequest}, Func{Guid, CancellationToken, Task{bool}}, CancellationToken)"/>, same rules and wording.</summary>
    public static IReadOnlyList<string> ValidateStepResultSources(
        IEnumerable<CreateWorkflowStepRequest> steps,
        Func<Guid, bool> existsInProject) =>
        ValidateStepResultSources(steps, id => existsInProject(id) ? StepResultSourceVerdict.Usable : StepResultSourceVerdict.NotFound);

    /// <summary>The synchronous twin of the verdict-based <c>ValidateStepResultSourcesAsync</c>, same rules and wording.</summary>
    public static IReadOnlyList<string> ValidateStepResultSources(
        IEnumerable<CreateWorkflowStepRequest> steps,
        Func<Guid, StepResultSourceVerdict> verdictOf)
    {
        List<string> errors = [];
        Dictionary<Guid, StepResultSourceVerdict> verdicts = [];
        foreach ((CreateWorkflowStepRequest step, Guid id) in StepResultReferences(steps))
        {
            if (!verdicts.TryGetValue(id, out StepResultSourceVerdict verdict))
            {
                verdict = verdictOf(id);
                verdicts[id] = verdict;
            }

            if (Refusal(step, id, verdict) is { } error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    /// <summary>
    /// Whether <paramref name="stepResultId"/> is a usable StepResult source of <paramref name="projectId"/>: its run
    /// belongs to the project (tenant filter dropped, project compared explicitly), it completed, and its object is
    /// a video at one of the project's addressable output keys (<see cref="ProjectOutputKeys.IsAddressableOutput"/>).
    /// </summary>
    public static async Task<StepResultSourceVerdict> StepResultSourceVerdictOfAsync(
        InferenceApiDbContext db, Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await StepResultRow(db, projectId, stepResultId).FirstOrDefaultAsync(ct);
        return row is null ? StepResultSourceVerdict.NotFound : VerdictOf(projectId, row.Status, row.OutputStorageKey);
    }

    /// <summary>The synchronous twin of <see cref="StepResultSourceVerdictOfAsync"/>.</summary>
    public static StepResultSourceVerdict StepResultSourceVerdictOf(InferenceApiDbContext db, Guid projectId, Guid stepResultId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = StepResultRow(db, projectId, stepResultId).FirstOrDefault();
        return row is null ? StepResultSourceVerdict.NotFound : VerdictOf(projectId, row.Status, row.OutputStorageKey);
    }

    private sealed record StepResultSourceRow(StepStatus Status, string? OutputStorageKey);

    private static IQueryable<StepResultSourceRow> StepResultRow(InferenceApiDbContext db, Guid projectId, Guid stepResultId) =>
        from r in db.WorkflowStepResults.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
        join e in db.WorkflowExecutions.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            on r.WorkflowExecutionId equals e.Id
        where r.Id == stepResultId && e.ProjectId == projectId
        select new StepResultSourceRow(r.Status, r.OutputStorageKey);

    private static StepResultSourceVerdict VerdictOf(Guid projectId, StepStatus status, string? key)
    {
        if (status != StepStatus.Completed)
        {
            return StepResultSourceVerdict.NotFinished;
        }

        if (!ProjectOutputKeys.IsAddressableOutput(projectId, key))
        {
            return StepResultSourceVerdict.NotVideo;
        }

        return ClassifyMedia(key);
    }

    /// <summary>
    /// QA-P0-21: the media sets the run-time resolver accepts, read from the one shared list
    /// (<see cref="StepResultMediaExtensions"/>) the resolver itself uses, so the save-time verdict and
    /// the run-time resolution cannot drift. A bare <c>video/*</c> MIME guess is not enough: it counts
    /// <c>.avi</c> as a video, which the resolver refuses, and it does not say which images a
    /// still-taking consumer takes.
    /// </summary>
    private static readonly IReadOnlySet<string> VideoExtensions = StepResultMediaExtensions.Video;

    private static readonly IReadOnlySet<string> ImageExtensions = StepResultMediaExtensions.Image;

    private static readonly IReadOnlySet<string> AudioExtensions = StepResultMediaExtensions.Audio;

    private static StepResultSourceVerdict ClassifyMedia(string? key)
    {
        string extension = Path.GetExtension(key ?? string.Empty);
        if (VideoExtensions.Contains(extension))
            return StepResultSourceVerdict.Usable;

        if (AudioExtensions.Contains(extension))
            return StepResultSourceVerdict.Audio;

        return ImageExtensions.Contains(extension) ? StepResultSourceVerdict.Image : StepResultSourceVerdict.NotVideo;
    }

    /// <summary>Every well-formed StepResult source id across <paramref name="steps"/>, with the step naming it.</summary>
    internal static IEnumerable<(CreateWorkflowStepRequest Step, Guid StepResultId)> StepResultReferences(
        IEnumerable<CreateWorkflowStepRequest> steps)
    {
        foreach (CreateWorkflowStepRequest step in steps)
        {
            // T-206: a PinInsert pin's content may name a step result of an earlier run too.
            if (TryParseStepType(step.StepType, out StepType pinType) && pinType == StepType.PinInsert)
            {
                foreach (InsertContentRef reference in InsertContentRefSaveValidator.PinRefsOf(step.PinInsertConfigJson))
                {
                    if (reference is { Kind: InsertContentRef.StepResultKind, StepResultId: { } pinResultId } && reference.Validate() is null)
                        yield return (step, pinResultId);
                }

                continue;
            }

            if (!TryParseStepType(step.StepType, out StepType stepType))
            {
                continue;
            }

            // T-106: a VideoCompile step's insert content may name a step result of an earlier run.
            if (stepType == StepType.VideoCompile)
            {
                foreach (InsertContentRef reference in InsertContentRefSaveValidator.RefsOf(step.VideoCompileConfigJson))
                {
                    if (reference is { Kind: InsertContentRef.StepResultKind, StepResultId: { } insertResultId } && reference.Validate() is null)
                        yield return (step, insertResultId);
                }

                continue;
            }

            // S-65: ExtractFrame takes one video, which may be an earlier run's step result. It is bound with the
            // engine's own options, so no step-type spelling or member case can save an unchecked foreign id.
            if (stepType == StepType.ExtractFrame)
            {
                if (ExtractFrameSourceId(step.ExtractFrameConfigJson) is { } frameSource)
                    yield return (step, frameSource);

                continue;
            }

            // AudioCleanup takes one audio file or video, which may be an earlier run's step result.
            if (stepType == StepType.AudioCleanup)
            {
                if (AudioCleanupSourceId(step.AudioCleanupConfigJson) is { } cleanupSource)
                    yield return (step, cleanupSource);

                continue;
            }

            // VideoEnhance takes one video, which may be an earlier run's step result (bound with its own options).
            if (stepType == StepType.VideoEnhance)
            {
                if (VideoEnhanceSourceId(step.VideoEnhanceConfigJson) is { } enhanceSource)
                    yield return (step, enhanceSource);

                continue;
            }

            // ImageGenerate Edit takes one picture, which may be an earlier run's step result.
            if (stepType == StepType.ImageGenerate)
            {
                if (ImageGenerateSourceId(step.ImageGenerateConfigJson) is { } imageSource)
                    yield return (step, imageSource);

                continue;
            }

            // QA-P2-4: a ThumbnailGenerate or VideoAnalyze Source may name a step result of an earlier run too.
            string? sourceConfig = stepType == StepType.ThumbnailGenerate ? step.ThumbnailGenerateConfigJson
                : stepType == StepType.VideoAnalyze ? step.VideoAnalyzeConfigJson
                : null;
            if (string.IsNullOrWhiteSpace(sourceConfig))
            {
                continue;
            }

            foreach (VideoSourceRef source in StepResultSourcesOf(sourceConfig))
            {
                if (source.Validate() is not null)
                {
                    continue; // already reported by ValidateStep
                }

                yield return (step, source.StepResultId!.Value);
            }
        }
    }

    /// <summary>
    /// The step orders whose config names a step result of some run: every StepResult source
    /// (<see cref="StepResultReferences"/>) plus the <c>analysisStepResultId</c> a ThumbnailGenerate or VideoCompile
    /// step may carry. Those ids are rows of the workflow's own project, so a copy in another project cannot use them.
    /// </summary>
    internal static IEnumerable<int> StepResultBoundStepOrders(IReadOnlyCollection<CreateWorkflowStepRequest> steps)
    {
        IEnumerable<int> analysisIds = steps.Where(step =>
            TryParseStepType(step.StepType, out StepType type)
            && (type == StepType.ThumbnailGenerate ? HasGuidProperty(step.ThumbnailGenerateConfigJson, "analysisStepResultId")
                : type == StepType.VideoCompile && HasGuidProperty(step.VideoCompileConfigJson, "analysisStepResultId")))
            .Select(step => step.StepOrder);
        return StepResultReferences(steps).Select(r => r.Step.StepOrder).Union(analysisIds);
    }

    private static bool HasGuidProperty(string? configJson, string name)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(p.Value.GetString(), out Guid id)
                    && id != Guid.Empty);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The step result id an ExtractFrame config names as its one source, bound exactly as the engine binds it
    /// (<see cref="ExtractFrameStepConfig.JsonOptions"/>). Null when the config is unreadable, names another kind,
    /// or is malformed — those are already reported by <see cref="ValidateExtractFrameConfig"/>.
    /// </summary>
    private static Guid? ExtractFrameSourceId(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            ExtractFrameStepConfig? config = JsonSerializer.Deserialize<ExtractFrameStepConfig>(configJson, ExtractFrameStepConfig.JsonOptions);
            return config?.Source is { Kind: VideoSourceKind.StepResult } source && source.Validate() is null
                ? source.StepResultId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The step result id an AudioCleanup config names as its one source (same binding as the engine); null otherwise.</summary>
    private static Guid? AudioCleanupSourceId(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            AudioCleanupStepConfig? config = JsonSerializer.Deserialize<AudioCleanupStepConfig>(configJson, AudioCleanupStepConfig.JsonOptions);
            return config?.Source is { Kind: VideoSourceKind.StepResult } source && source.Validate() is null
                ? source.StepResultId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The step result id a VideoEnhance config names as its one source (same binding as the engine); null otherwise.</summary>
    private static Guid? VideoEnhanceSourceId(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            VideoEnhanceStepConfig? config = JsonSerializer.Deserialize<VideoEnhanceStepConfig>(configJson, VideoEnhanceStepConfig.JsonOptions);
            return config?.Source is { Kind: VideoSourceKind.StepResult } source && source.Validate() is null
                ? source.StepResultId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? ImageGenerateSourceId(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            ImageGenerateStepConfig? config = JsonSerializer.Deserialize<ImageGenerateStepConfig>(configJson, ImageGenerateStepConfig.JsonOptions);
            return config?.Source is { Kind: VideoSourceKind.StepResult } source && source.Validate() is null
                ? source.StepResultId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Refusal(CreateWorkflowStepRequest step, Guid id, StepResultSourceVerdict verdict)
    {
        // The type as the save path stores it (any spelling), so the wording names the step the way the caller sees it.
        string label = TryParseStepType(step.StepType, out StepType stepType) ? stepType.ToString() : step.StepType;
        // QA-P0-21: the step types whose run-time resolver takes stills (allowImages: true): VideoCompile's
        // and PinInsert's insert content, and VideoAnalyze's own source. ExtractFrame and ThumbnailGenerate
        // resolve with allowImages: false, so an image is still refused for them.
        bool takesStills = stepType is StepType.VideoCompile or StepType.PinInsert or StepType.VideoAnalyze;
        // AudioCleanup takes one audio file or one video, so an audio result is a usable source for it alone.
        bool takesAudio = stepType == StepType.AudioCleanup;
        return verdict switch
        {
            StepResultSourceVerdict.Usable => null,
            StepResultSourceVerdict.Image when takesStills => null,
            StepResultSourceVerdict.Audio when takesAudio => null,
            StepResultSourceVerdict.NotFinished =>
                $"Step {step.StepOrder} ({label}): step result '{id}' has not finished successfully, so it cannot be used as a source.",
            StepResultSourceVerdict.NotVideo or StepResultSourceVerdict.Image or StepResultSourceVerdict.Audio =>
                $"Step {step.StepOrder} ({label}): step result '{id}' did not produce {(takesAudio ? "an audio file or a video" : "a video")}, so it cannot be used as a source.",
            // One message for "another project" and "does not exist": nothing in it says which.
            _ => $"Step {step.StepOrder} ({label}): step result '{id}' was not found in this project."
        };
    }

    /// <summary>
    /// The raw config JSON for a step type that requires one, plus the request property's own name
    /// (used verbatim in the error, so it always matches the field the caller must populate). An
    /// empty <c>PropertyName</c> means this step type carries no config blob.
    /// </summary>
    private static (string? ConfigJson, string PropertyName) ConfigJsonFor(
        CreateWorkflowStepRequest step, StepType stepType) => stepType switch
        {
            StepType.Extract => (step.ExtractConfigJson, nameof(CreateWorkflowStepRequest.ExtractConfigJson)),
            StepType.VideoAnalyze => (step.VideoAnalyzeConfigJson, nameof(CreateWorkflowStepRequest.VideoAnalyzeConfigJson)),
            StepType.VideoCompile => (step.VideoCompileConfigJson, nameof(CreateWorkflowStepRequest.VideoCompileConfigJson)),
            StepType.EditRoom => (step.EditRoomConfigJson, nameof(CreateWorkflowStepRequest.EditRoomConfigJson)),
            StepType.GraphicsRoom => (step.GraphicsRoomConfigJson, nameof(CreateWorkflowStepRequest.GraphicsRoomConfigJson)),
            StepType.ColorGradeRoom => (step.ColorGradeRoomConfigJson, nameof(CreateWorkflowStepRequest.ColorGradeRoomConfigJson)),
            StepType.DailiesRoom => (step.DailiesRoomConfigJson, nameof(CreateWorkflowStepRequest.DailiesRoomConfigJson)),
            StepType.Voiceover => (step.VoiceoverConfigJson, nameof(CreateWorkflowStepRequest.VoiceoverConfigJson)),
            StepType.ThumbnailGenerate => (step.ThumbnailGenerateConfigJson, nameof(CreateWorkflowStepRequest.ThumbnailGenerateConfigJson)),
            StepType.TimelineRender => (step.TimelineRenderConfigJson, nameof(CreateWorkflowStepRequest.TimelineRenderConfigJson)),
            StepType.DesignSystem => (step.DesignSystemConfigJson, nameof(CreateWorkflowStepRequest.DesignSystemConfigJson)),
            StepType.ExtractFrame => (step.ExtractFrameConfigJson, nameof(CreateWorkflowStepRequest.ExtractFrameConfigJson)),
            StepType.AudioGenerate => (step.AudioGenerateConfigJson, nameof(CreateWorkflowStepRequest.AudioGenerateConfigJson)),
            StepType.PinInsert => (step.PinInsertConfigJson, nameof(CreateWorkflowStepRequest.PinInsertConfigJson)),
            StepType.AudioCleanup => (step.AudioCleanupConfigJson, nameof(CreateWorkflowStepRequest.AudioCleanupConfigJson)),
            StepType.VideoEnhance => (step.VideoEnhanceConfigJson, nameof(CreateWorkflowStepRequest.VideoEnhanceConfigJson)),
            StepType.ImageGenerate => (step.ImageGenerateConfigJson, nameof(CreateWorkflowStepRequest.ImageGenerateConfigJson)),
            _ => (null, string.Empty)
        };

    /// <summary>
    /// The raw (unprefixed) errors for a VideoGenerate config: missing/blank, invalid JSON, a JSON
    /// null, or whatever <see cref="VideoGenerateStepConfig.Validate()"/> reports.
    /// </summary>
    /// <summary>
    /// The raw (unprefixed) errors for a TimelineRender config: missing/blank, invalid JSON, a JSON
    /// null, or whatever <see cref="TimelineRenderStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateTimelineRenderConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["TimelineRenderConfigJson is required."];
        }

        TimelineRenderStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<TimelineRenderStepConfig>(configJson, TimelineRenderJsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"TimelineRenderConfigJson is not valid JSON: {ex.Message}"];
        }

        if (config is null)
        {
            return ["TimelineRenderConfigJson must be a JSON object."];
        }

        return config.Validate();
    }

    private static readonly JsonSerializerOptions ThumbnailGenerateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The raw (unprefixed) errors for a ThumbnailGenerate config: missing/blank, invalid JSON, a JSON null, or
    /// whatever <see cref="ThumbnailGenerateStepConfig.Validate"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateThumbnailGenerateConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["ThumbnailGenerateConfigJson is required."];
        }

        ThumbnailGenerateStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ThumbnailGenerateStepConfig>(configJson, ThumbnailGenerateJsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"ThumbnailGenerateConfigJson is not valid JSON: {ex.Message}"];
        }

        return config is null ? ["ThumbnailGenerateConfigJson must be a JSON object."] : ThumbnailGenerateStepConfig.Validate(config);
    }

    /// <summary>
    /// The raw (unprefixed) errors for an ExtractFrame config: missing/blank, invalid JSON, a JSON null,
    /// or whatever <see cref="ExtractFrameStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateExtractFrameConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["ExtractFrameConfigJson is required."];
        }

        ExtractFrameStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ExtractFrameStepConfig>(configJson, ExtractFrameStepConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"ExtractFrameConfigJson is not valid JSON: {ex.Message}"];
        }

        return config is null ? ["ExtractFrameConfigJson must be a JSON object."] : config.Validate();
    }

    /// <summary>
    /// The raw (unprefixed) errors for an AudioGenerate config: missing/blank, invalid JSON, a JSON null, or whatever
    /// <see cref="AudioGenerateStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateAudioGenerateConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["AudioGenerateConfigJson is required."];
        }

        AudioGenerateStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AudioGenerateStepConfig>(configJson, AudioGenerateStepConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"AudioGenerateConfigJson is not valid JSON: {ex.Message}"];
        }

        return config is null ? ["AudioGenerateConfigJson must be a JSON object."] : config.Validate();
    }

    /// <summary>
    /// The raw (unprefixed) drift errors for a VideoAnalyze config (T-021): a property the config record
    /// does not carry, either at the top level or on one <c>trackObjects</c> entry, is refused at SAVE
    /// time. A shape/JSON error is reported by the shared validation before this runs — this check is
    /// specifically about a field the web offers and the backend would silently ignore.
    /// </summary>
    /// <param name="root">The config object, already proven to be a JSON object by the caller.</param>
    /// <returns>One line per unknown property; empty when every property is one the backend reads.</returns>
    public static IReadOnlyList<string> ValidateVideoAnalyzeProperties(JsonElement root)
    {
        List<string> errors = [];
        HashSet<string> known = SerializableMembers(typeof(VideoAnalyzeStepConfig));
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!known.Contains(property.Name) && !InertTemplateKeys.Contains(property.Name))
            {
                errors.Add($"unknown property '{property.Name}' (the backend does not read it)");
            }
        }

        if (root.TryGetProperty("trackObjects", out JsonElement targetsElement)
            && targetsElement.ValueKind == JsonValueKind.Array)
        {
            HashSet<string> targetMembers = SerializableMembers(typeof(ObjectTrackTarget));
            int index = 0;
            foreach (JsonElement target in targetsElement.EnumerateArray())
            {
                if (target.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in target.EnumerateObject())
                    {
                        if (!targetMembers.Contains(property.Name))
                        {
                            errors.Add(
                                $"track objects entry {index + 1}: unknown property '{property.Name}' (the backend does not read it)");
                        }
                    }
                }

                index++;
            }
        }

        return errors;
    }

    /// <summary>
    /// Keys a shipped template carries for the picker's own use and the backend deliberately ignores —
    /// <c>placeholders</c> names the field a picked template still needs filled (WorkflowTemplateCatalog).
    /// Everything else unknown is drift.
    /// </summary>
    private static readonly HashSet<string> InertTemplateKeys = new(StringComparer.Ordinal) { "placeholders", AppInputPlaceholders.AppInputsKey };

    /// <summary>The camelCase JSON names a record actually binds, so an unknown key can be named.</summary>
    private static HashSet<string> SerializableMembers(Type type) =>
    [
        .. type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
    ];

    /// <summary>
    /// The raw (unprefixed) errors for an AudioCleanup config: missing/blank, invalid JSON, a JSON null, or whatever
    /// <see cref="AudioCleanupStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateAudioCleanupConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["AudioCleanupConfigJson is required."];
        }

        AudioCleanupStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AudioCleanupStepConfig>(configJson, AudioCleanupStepConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"AudioCleanupConfigJson is not valid JSON: {ex.Message}"];
        }

        return config is null ? ["AudioCleanupConfigJson must be a JSON object."] : config.Validate();
    }

    /// The raw (unprefixed) errors for a VideoEnhance config: missing/blank, invalid JSON, a JSON null, or whatever
    /// <see cref="VideoEnhanceStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateVideoEnhanceConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["VideoEnhanceConfigJson is required."];
        }

        VideoEnhanceStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoEnhanceStepConfig>(configJson, VideoEnhanceStepConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"VideoEnhanceConfigJson is not valid JSON: {ex.Message}"];
        }

        return config is null ? ["VideoEnhanceConfigJson must be a JSON object."] : config.Validate();
    }

    /// <summary>
    /// The raw (unprefixed) errors for an ImageGenerate config: missing/blank, invalid JSON, a JSON null, or whatever
    /// <see cref="ImageGenerateStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateImageGenerateConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["ImageGenerateConfigJson is required."];
        }

        ImageGenerateStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ImageGenerateStepConfig>(configJson, ImageGenerateStepConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"ImageGenerateConfigJson is not valid JSON: {ex.Message}"];
        }

        return config is null ? ["ImageGenerateConfigJson must be a JSON object."] : config.Validate();
    }

    /// <summary>
    /// The raw (unprefixed) errors for a PinInsert config: missing/blank, invalid JSON, a JSON null, or whatever
    /// <see cref="PinInsertStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidatePinInsertConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["PinInsertConfigJson is required."];
        }

        PinInsertStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<PinInsertStepConfig>(configJson, PinInsertStepConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"PinInsertConfigJson is not valid JSON: {ex.Message}"];
        }

        return config is null ? ["PinInsertConfigJson must be a JSON object."] : config.Validate();
    }

    private static readonly JsonSerializerOptions TimelineRenderJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions DesignSystemJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The raw (unprefixed) errors for a DesignSystem config: missing/blank, invalid JSON, a JSON
    /// null, or whatever <see cref="DesignSystemStepConfig.Validate()"/> reports.
    /// </summary>
    public static IReadOnlyList<string> ValidateDesignSystemConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["DesignSystemConfigJson is required."];
        }

        DesignSystemStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<DesignSystemStepConfig>(configJson, DesignSystemJsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"DesignSystemConfigJson is not valid JSON: {ex.Message}"];
        }

        if (config is null)
        {
            return ["DesignSystemConfigJson must be a JSON object."];
        }

        return config.Validate();
    }

    /// <summary>
    /// The workflow-aware half of DesignSystem validation: a Compose step's parse step and every
    /// fallback step must exist, be an Agent step and come before the Compose step. A config that
    /// does not deserialize is skipped — <see cref="ValidateDesignSystemConfig"/> already reported it.
    /// </summary>
    private static IReadOnlyList<string> ValidateDesignSystemComposeReferences(
        IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        var errors = new List<string>();
        var byOrder = steps
            .GroupBy(s => s.StepOrder)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (CreateWorkflowStepRequest step in steps)
        {
            if (step.StepType == null
                || !Enum.TryParse<StepType>(step.StepType, out var stepType)
                || stepType != StepType.DesignSystem)
            {
                continue;
            }

            DesignSystemStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<DesignSystemStepConfig>(
                    step.DesignSystemConfigJson ?? string.Empty, DesignSystemJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (config is not { Op: DesignSystemOp.Compose, Compose: { } compose })
                continue;

            IEnumerable<int> fallbackOrders = (compose.DesignFallbackStepOrders ?? [])
                .Concat(compose.ProductFallbackStepOrders ?? []);
            foreach (int order in fallbackOrders.Distinct())
            {
                if (!byOrder.TryGetValue(order, out CreateWorkflowStepRequest? fallback))
                {
                    errors.Add($"Step {step.StepOrder} ({StepType.DesignSystem}): fallback step order {order} does not exist.");
                }
                else if (!Enum.TryParse<StepType>(fallback.StepType, out var fallbackType)
                    || fallbackType != StepType.Agent)
                {
                    errors.Add($"Step {step.StepOrder} ({StepType.DesignSystem}): fallback step order {order} is not an Agent step.");
                }
                else if (order >= step.StepOrder)
                {
                    errors.Add($"Step {step.StepOrder} ({StepType.DesignSystem}): fallback step order {order} must be before the Compose step.");
                }
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateVideoGenerateConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["VideoGenerateConfigJson is required."];
        }

        VideoGenerateStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(configJson, VideoGenerateStepConfig.JsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"VideoGenerateConfigJson is not valid JSON: {ex.Message}"];
        }

        if (config is null)
        {
            return ["VideoGenerateConfigJson must be a JSON object, not null."];
        }

        return config.Validate();
    }

    /// <summary>
    /// The raw (unprefixed) errors for a Voiceover config that has ALREADY passed the three generic
    /// checks (non-blank, valid JSON, a JSON object). This arm is ADDITIVE by construction:
    ///
    /// <list type="bullet">
    /// <item>Any config that does not deserialize to
    /// <see cref="VoiceoverSourceKind.NarrationPlan"/> — <see cref="VoiceoverSourceKind.Inline"/>,
    /// <see cref="VoiceoverSourceKind.ScriptScenes"/>, or an object this method cannot read at all —
    /// returns empty, exactly the answer the validator gave before this method existed. A config
    /// whose enum member is unparseable is left to the executor's own <c>SOURCE_INVALID</c> rather
    /// than becoming a new save-time rejection.</item>
    /// <item>Only a NarrationPlan config is checked further, because that is the one kind with a
    /// cross-step reference the executor hard-fails on: <c>VoiceoverStepExecutor</c> returns
    /// <c>NARRATION_SOURCE_INVALID</c> when <see cref="VoiceoverSource.NarrationWriterStepOrder"/> is
    /// absent, so saving such a step only defers the failure to a real execution run.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ValidateVoiceoverConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return ["VoiceoverConfigJson is required."];
        }

        VoiceoverStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VoiceoverStepConfig>(configJson, VoiceoverStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            // A JSON OBJECT that will not deserialize into the config type — a kind string the enum
            // has no member for, a wrong-typed member — is not this arm's business: the executor
            // reports it as SOURCE_INVALID / CONFIG_INVALID itself, and turning it into a new
            // save-time rejection would tighten validation for configs that save today.
            return [];
        }

        if (config?.Source is null)
        {
            // `{}` and `{"source":null}` both leave Source null rather than throwing, and the
            // executor's own CONFIG_INVALID/SOURCE_INVALID path owns that shape. Guarded here only
            // so this method cannot NullReferenceException on a config the generic arm accepted.
            return [];
        }

        IReadOnlyList<string> overrideErrors = config.Validate();
        if (overrideErrors.Count > 0)
        {
            return overrideErrors.Select(e => $"VoiceoverConfigJson {e}").ToList();
        }

        if (config.Source.Kind == VoiceoverSourceKind.PickupPlan)
        {
            return config.Source.PickupPlannerStepOrder is int pickupOrder && pickupOrder >= 1
                ? []
                : ["VoiceoverConfigJson source pickupPlannerStepOrder is required and must be at least 1 when the source kind is PickupPlan."];
        }

        if (config.Source.Kind != VoiceoverSourceKind.NarrationPlan)
        {
            return [];
        }

        if (config.Source.NarrationWriterStepOrder is not int order || order < 1)
        {
            return
            [
                "VoiceoverConfigJson source narrationWriterStepOrder is required and must be at least 1 "
                + "when the source kind is NarrationPlan."
            ];
        }

        return [];
    }

    /// <summary>The 400 body's <c>message</c>: the formatted errors, one per line.</summary>
    public static string FormatMessage(IReadOnlyList<string> errors) => string.Join("\n", errors);
}

/// <summary>What the save-time check found for one StepResult source (QA-P0-7).</summary>
public enum StepResultSourceVerdict
{
    /// <summary>A completed result of the workflow's project holding a video at an addressable output key.</summary>
    Usable,

    /// <summary>Missing, or a result of another project — deliberately one verdict.</summary>
    NotFound,

    /// <summary>The project's own result, but its step did not complete.</summary>
    NotFinished,

    /// <summary>The project's own completed result, but it holds no video at an addressable output key.</summary>
    NotVideo,

    /// <summary>
    /// A completed result of the workflow's project holding an image at an addressable output key: usable as VideoCompile
    /// insert content (which takes stills), not as a VideoAnalyze source.
    /// </summary>
    Image,

    /// <summary>A completed result of the workflow's project holding an audio file: usable as an AudioCleanup source only.</summary>
    Audio
}
