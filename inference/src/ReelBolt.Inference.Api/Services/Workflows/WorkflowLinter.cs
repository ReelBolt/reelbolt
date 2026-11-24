using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Inference.Api.Agents.Tools;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>What to lint: the steps (with agent definitions loaded), the optional brief, and where the facts come from.</summary>
/// <param name="OrganizationId">The caller's organization (plan and provider facts).</param>
/// <param name="ProjectId">The project (file, voice and design facts).</param>
/// <param name="Steps">The steps, any order. Agent definitions should be loaded; a missing one means "unknown agent".</param>
/// <param name="Brief">The run brief, when the caller has one.</param>
/// <param name="WorkflowId">The saved workflow, when there is one (the app-manifest check reads it).</param>
/// <param name="Facts">Facts to use; null = read them from the database.</param>
/// <param name="Manifest">The app manifest of a draft; null = the saved workflow's stored one.</param>
/// <param name="AtRunStart">
/// True only for the run-start gate: an open ProjectFile video slot is then judged against the brief alone (the engine
/// fills it from the brief's video files and never from the project), so it can be refused before anything is queued.
/// </param>
public sealed record WorkflowLintRequest(
    Guid? OrganizationId,
    Guid ProjectId,
    IReadOnlyList<WorkflowStep> Steps,
    RunBrief? Brief = null,
    Guid? WorkflowId = null,
    IWorkflowLintFacts? Facts = null,
    AppManifest? Manifest = null,
    bool AtRunStart = false);

/// <summary>The findings of one lint, most serious first.</summary>
public sealed record WorkflowLintReport(IReadOnlyList<LintFinding> Findings)
{
    /// <summary>The findings that would fail the run.</summary>
    public IReadOnlyList<LintFinding> Errors => [.. Findings.Where(f => f.Severity == LintSeverity.Error)];

    /// <summary>True when at least one finding is an error.</summary>
    public bool HasErrors => Findings.Any(f => f.Severity == LintSeverity.Error);
}

/// <summary>The pre-run linter (docs/workflow-linter.md).</summary>
public interface IWorkflowLinter
{
    /// <summary>Lints one workflow. Never throws for a malformed config; an unreadable fact is "unknown" and silent.</summary>
    Task<WorkflowLintReport> LintAsync(WorkflowLintRequest request, CancellationToken ct);
}

/// <summary>WL018's seam: the orphans of the workflow's app manifest against its steps.</summary>
public interface IAppManifestOrphanSource
{
    /// <summary>The orphans of the manifest (the draft's, else the stored one) against the steps being linted.</summary>
    Task<IReadOnlyList<AppManifestOrphan>> FindOrphansAsync(WorkflowLintRequest request, CancellationToken ct);

    /// <summary>The manifest itself (the draft's, else the stored one), for the app-input rules WL028 to WL033.</summary>
    Task<AppManifest?> ManifestAsync(WorkflowLintRequest request, CancellationToken ct) => Task.FromResult(request.Manifest);
}

/// <summary>No manifest source: nothing is orphaned (unit tests of the other rules).</summary>
public sealed class NoAppManifestOrphans : IAppManifestOrphanSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AppManifestOrphan>> FindOrphansAsync(WorkflowLintRequest request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AppManifestOrphan>>([]);
}

/// <summary>
/// WL018 for real: wraps <see cref="AppManifestValidator.FindOrphans"/>. The manifest is the one in the request (a
/// draft) or else the stored one of the saved workflow, read scoped to the project.
/// </summary>
public sealed class AppManifestOrphanSource(InferenceApiDbContext db) : IAppManifestOrphanSource
{
    /// <inheritdoc />
    public async Task<AppManifest?> ManifestAsync(WorkflowLintRequest request, CancellationToken ct)
    {
        AppManifest? manifest = request.Manifest;
        if (manifest is null && request.WorkflowId is { } id)
        {
            string? json = await db.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.Id == id && w.ProjectId == request.ProjectId)
                .Select(w => w.AppManifestJson)
                .FirstOrDefaultAsync(ct);
            manifest = AppManifestJson.TryParse(json);
        }

        return manifest;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppManifestOrphan>> FindOrphansAsync(WorkflowLintRequest request, CancellationToken ct)
    {
        AppManifest? manifest = await ManifestAsync(request, ct);

        return AppManifestValidator.FindOrphans(
            manifest, [.. request.Steps.Select(s => AppStepFacts.From(s.StepType, s.AgentDefinition?.AgentType, s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson))]);
    }
}

/// <summary>
/// Composes the existing save validators, <see cref="StepGraphLinter"/> (the pure graph rules, built on
/// <c>WorkflowStepDependencyGraph</c>'s step model) and the readiness facts into rules WL001 to WL022.
/// The rule table (<see cref="StepCompatibilityRules"/>) is the single source of ids, severities and messages.
/// </summary>
public sealed partial class WorkflowLinter(
    IWorkflowLintFactsSource factsSource,
    IAppManifestOrphanSource? manifestOrphans = null,
    StepProviderOwnershipValidator? providerOwnership = null,
    IAppInputModelResolverSource? models = null,
    IAppInputSpendPricer? spendPricer = null) : IWorkflowLinter
{
    private readonly IAppManifestOrphanSource _manifestOrphans = manifestOrphans ?? new NoAppManifestOrphans();

    /// <summary>Rule id of the save-validator passthrough (a problem the editor would refuse to save).</summary>
    public const string SaveCheckRuleId = "WL000";

    /// <inheritdoc />
    public async Task<WorkflowLintReport> LintAsync(WorkflowLintRequest request, CancellationToken ct)
    {
        List<WorkflowStep> steps = [.. request.Steps.OrderBy(s => s.StepOrder)];
        IWorkflowLintFacts facts = request.Facts ?? factsSource.Create(request.OrganizationId, request.ProjectId);
        List<LintFinding> findings = [.. StepGraphLinter.Evaluate(steps)];

        findings.AddRange(SaveChecks(steps));
        if (providerOwnership is not null && request.OrganizationId is { } org)
            findings.AddRange(await OwnershipChecksAsync(org, request.ProjectId, steps, ct));

        findings.AddRange(EgressChecks(steps, request.Brief));
        findings.AddRange(await ProviderChecksAsync(steps, facts, ct));
        findings.AddRange(await PlanChecksAsync(steps, request.Brief, facts, ct));
        findings.AddRange(await VoiceChecksAsync(steps, request.Brief, facts, ct));
        findings.AddRange(await AvatarChecksAsync(steps, request.Brief, facts, request.AtRunStart, ct));
        findings.AddRange(await SubjectModelChecksAsync(steps, request.Brief, facts, ct));
        findings.AddRange(await MediaChecksAsync(steps, request.Brief, facts, request.AtRunStart, ct));
        findings.AddRange(await DesignSystemChecksAsync(steps, request.Brief, facts, ct));
        findings.AddRange(BriefChecks(steps, request.Brief));
        findings.AddRange(await LengthChecksAsync(steps, request.Brief, facts, ct));
        findings.AddRange(await ManifestChecksAsync(request, ct));
        (IEnumerable<LintFinding> appInputFindings, IReadOnlySet<int>? reach) = await AppInputChecksAsync(request, steps, ct);
        findings.AddRange(appInputFindings);
        // The app's fields choose the branch: a step this run never reaches does not ask for the permission to send media.
        if (reach is not null)
            findings.RemoveAll(f => f.RuleId == "WL015" && f.StepOrder is { } order && !reach.Contains(order));

        // A pair of rules can describe the same defect on one step (a voiceover that cannot work is both
        // WL006 and WL001, say); keep the first of identical (rule, step, message).
        IReadOnlyList<LintFinding> ordered =
        [
            .. findings
                .DistinctBy(f => (f.RuleId, f.StepOrder, f.Message))
                .OrderBy(f => f.Severity)
                .ThenBy(f => f.StepOrder ?? int.MaxValue)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
        ];
        return new WorkflowLintReport(ordered);
    }

    // ------------------------------------------------------------------
    // The existing save validators (WL000 / WL006)
    // ------------------------------------------------------------------

    private static IEnumerable<LintFinding> SaveChecks(List<WorkflowStep> steps)
    {
        IReadOnlyList<string> errors;
        try
        {
            errors = StepConfigSaveValidator.Validate(steps.Select(AssistantStepShapes.FromStep));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            yield break;
        }

        foreach (string error in errors)
            yield return new LintFinding(SaveCheckRuleId, LintSeverity.Warning, StepOrderOf(error), error);
    }

    private async Task<IEnumerable<LintFinding>> OwnershipChecksAsync(
        Guid organizationId, Guid projectId, List<WorkflowStep> steps, CancellationToken ct)
    {
        IReadOnlyList<string> errors;
        try
        {
            errors = await providerOwnership!.ValidateAsync(
                organizationId, projectId, [.. steps.Select(AssistantStepShapes.FromStep)], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }

        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL006");
        return errors.Select(error => error.Contains("inference provider", StringComparison.OrdinalIgnoreCase)
            ? new LintFinding(rule.Id, rule.Severity, StepOrderOf(error),
                new LintMessage(
                    $"{error[..error.IndexOf(':', StringComparison.Ordinal)]}: the provider chosen on this step is not available to your organization. It would stop when it runs.",
                    LintArgs.Of(
                        ("stepOrder", StepOrderOf(error) is { } order ? Num(order) : null),
                        ("stepLabel", StepLabelOf(steps, StepOrderOf(error))),
                        ("detail", error[..error.IndexOf(':', StringComparison.Ordinal)]))),
                new LintFix(LintFixKind.ChooseProvider, "Choose a provider you can use", LabelId: "WL006.chooseAvailableProvider"),
                messageKey: Variant(rule, "providerOwnership"))
            : new LintFinding(SaveCheckRuleId, LintSeverity.Warning, StepOrderOf(error), error));
    }

    [GeneratedRegex(@"^Step (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StepPrefix();

    private static int? StepOrderOf(string error) =>
        StepPrefix().Match(error) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int order) ? order : null;

    private static string? StepLabelOf(IReadOnlyList<WorkflowStep> steps, int? order) =>
        order is { } value ? steps.FirstOrDefault(s => s.StepOrder == value)?.Label : null;

    private static string Num(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The finding key of a variant sentence: <c>&lt;ruleId&gt;.&lt;variant&gt;</c> (the client prefixes <c>lint.</c>).</summary>
    private static string Variant(StepCompatibilityRule rule, string variant) => rule.Id + "." + variant;

    /// <summary>
    /// The message key of WL010's run-start-only variant: an open video slot and no video in the run's brief. The run-start
    /// gate refuses on it whatever <c>Workflows:Lint:Enforce</c> says (see <see cref="WorkflowLintGate.StartBlockers"/>).
    /// </summary>
    public const string OpenVideoSlotKey = "WL010.openVideoSlot";

    /// <summary>WL027's run-start-only variant: the step makes an avatar say its narration and no avatar is chosen.</summary>
    public const string NoAvatarChosenKey = "WL027.noAvatarChosen";

    /// <summary>WL006's variant: the step's model is not one its resolved video provider supports.</summary>
    public const string ModelUnsupportedKey = "WL006.modelUnsupported";

    /// <summary>WL006's variant: a talking-head step and no lip-sync model is set up (a start blocker, like <see cref="ModelUnsupportedKey"/>).</summary>
    public const string LipSyncModelNotSetKey = "WL006.lipSyncModelNotSet";

    // ------------------------------------------------------------------
    // WL015 egress-permission-missing
    // ------------------------------------------------------------------

    private static IEnumerable<LintFinding> EgressChecks(List<WorkflowStep> steps, RunBrief? brief)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL015");

        // A run whose brief gives the permission (the app's own field, docs/design/app-inputs.md "Permission to send media
        // as an app field") has nothing to report: the run gate decides whether that caller may give it.
        if (MediaEgressPermission.IsRequested(brief?.Inputs))
            yield break;

        // The need is the ONE shared definition (MediaEgressNeed): the linter, the app's field and the run gate agree.
        MediaEgressBriefAvatar avatar = brief?.Avatar?.AvatarId is { } chosen && chosen != System.Guid.Empty
            ? MediaEgressBriefAvatar.Chosen
            : MediaEgressBriefAvatar.None;
        foreach (WorkflowStep step in steps)
        {
            MediaEgressStepNeed need = MediaEgressNeed.For(AppBindingStep.From(step), avatar);
            if (need is not { Level: MediaEgressNeedLevel.Required, StoredOn: false })
                continue;

            // Args: stepOrder, stepLabel?. The fix is the app's own field, filled in on the app page.
            LintMessage message = rule.Format(("step", StepGraphLinter.StepPart(step)));
            yield return new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                new LintMessage(message.Text + $" It {MediaEgressNeed.Sends(need.Reason)}.",
                    LintArgs.Of([.. message.Args.Select(a => (a.Key, (string?)a.Value)), ("inputId", MediaEgressPermission.InputId)])),
                new LintFix(LintFixKind.FillInput, "Allow sending your media on the app page", LabelId: "WL015.allowOnAppPage"),
                messageKey: Variant(rule, need.Reason!));
        }
    }

    // ------------------------------------------------------------------
    // WL006 provider-missing-for-capability
    // ------------------------------------------------------------------

    private static string CapabilityName(InferenceProviderCapability capability) => capability switch
    {
        InferenceProviderCapability.SpeechSynthesis => "a voice (speech) provider",
        InferenceProviderCapability.VideoGeneration => "a video generation provider",
        InferenceProviderCapability.MusicGeneration => "a music generation provider",
        InferenceProviderCapability.SoundEffectGeneration => "a sound effect generation provider",
        InferenceProviderCapability.Transcription => "a transcription provider",
        InferenceProviderCapability.Vision => "a vision (image understanding) provider",
        InferenceProviderCapability.AudioIsolation => "an audio isolation provider",
        InferenceProviderCapability.VideoUpscale => "a video upscaling provider",
        InferenceProviderCapability.FrameInterpolation => "a frame interpolation provider",
        InferenceProviderCapability.VideoOutpaint => "a video extension provider",
        InferenceProviderCapability.LipSync => "a lip-sync provider",
        InferenceProviderCapability.ImageGeneration => "an image generation provider",
        InferenceProviderCapability.ImageEdit => "an image editing provider",
        _ => capability.ToString()
    };

    /// <summary>The token form of <see cref="CapabilityName"/>: the enum name in camelCase.</summary>
    private static string CapabilityCode(InferenceProviderCapability capability) => capability switch
    {
        InferenceProviderCapability.SpeechSynthesis => "speechSynthesis",
        InferenceProviderCapability.VideoGeneration => "videoGeneration",
        InferenceProviderCapability.MusicGeneration => "musicGeneration",
        InferenceProviderCapability.SoundEffectGeneration => "soundEffectGeneration",
        InferenceProviderCapability.Transcription => "transcription",
        InferenceProviderCapability.Vision => "vision",
        InferenceProviderCapability.AudioIsolation => "audioIsolation",
        InferenceProviderCapability.VideoUpscale => "videoUpscale",
        InferenceProviderCapability.FrameInterpolation => "frameInterpolation",
        InferenceProviderCapability.VideoOutpaint => "videoOutpaint",
        InferenceProviderCapability.LipSync => "lipSync",
        InferenceProviderCapability.ImageGeneration => "imageGeneration",
        InferenceProviderCapability.ImageEdit => "imageEdit",
        _ => capability.ToString()
    };

    /// <summary>A VideoGenerate config with avatar audio is a talking head (<c>VideoGenerateStepConfig.HasAvatarAudio</c>).</summary>
    private static bool IsTalkingHead(JsonObject? videoConfig) =>
        videoConfig is not null && (Child(videoConfig, "avatarAudio") is not null || Guid(videoConfig, "avatarAudioProjectFileId") is not null);

    private static IEnumerable<(InferenceProviderCapability Capability, Guid? Explicit)> CapabilitiesNeeded(WorkflowStep step)
    {
        switch (step.StepType)
        {
            case StepType.Voiceover:
                yield return (InferenceProviderCapability.SpeechSynthesis, Guid(Json(step.VoiceoverConfigJson), "providerId"));
                break;
            case StepType.VideoGenerate:
                {
                    JsonObject? video = Json(step.VideoGenerateConfigJson);
                    // A talking head (avatar audio) runs on a LipSync row, never a VideoGeneration one.
                    yield return (IsTalkingHead(video) ? InferenceProviderCapability.LipSync : InferenceProviderCapability.VideoGeneration,
                        Guid(video, "providerId"));
                    break;
                }
            case StepType.AudioGenerate:
                {
                    JsonObject? config = Json(step.AudioGenerateConfigJson);
                    string? kind = EnumName(Child(config, "kind"), ["Music", "SoundEffect"]);
                    if (kind is not null)
                    {
                        yield return (kind == "Music" ? InferenceProviderCapability.MusicGeneration : InferenceProviderCapability.SoundEffectGeneration,
                            Guid(config, "providerId"));
                    }

                    break;
                }
            case StepType.AudioCleanup:
                {
                    JsonObject? config = Json(step.AudioCleanupConfigJson);
                    if (EnumName(Child(config, "target"), ["VoiceBasic", "Hum", "Wind", "Hiss", "Echo", "IsolateVoice", "RemoveMusic"]) is "IsolateVoice" or "RemoveMusic")
                        yield return (InferenceProviderCapability.AudioIsolation, Guid(config, "providerId"));
                    break;
                }
            case StepType.VideoEnhance:
                {
                    JsonObject? config = Json(step.VideoEnhanceConfigJson);
                    if (EnumName(Child(config, "engine"), ["Local", "Hosted"]) == "Hosted")
                    {
                        string? op = EnumName(Child(config, "op"), ["Upscale", "Interpolate", "Denoise", "MotionBlur", "Extend"]);
                        if (op == "Upscale")
                            yield return (InferenceProviderCapability.VideoUpscale, Guid(config, "providerId"));
                        else if (op == "Interpolate")
                            yield return (InferenceProviderCapability.FrameInterpolation, Guid(config, "providerId"));
                        else if (op == "Extend")
                            yield return (InferenceProviderCapability.VideoOutpaint, Guid(config, "providerId"));
                    }

                    break;
                }
            case StepType.ImageGenerate:
                {
                    JsonObject? config = Json(step.ImageGenerateConfigJson);
                    string? mode = EnumName(Child(config, "mode"), ["Generate", "Edit"]);
                    if (mode is not null)
                    {
                        yield return (mode == "Edit" ? InferenceProviderCapability.ImageEdit : InferenceProviderCapability.ImageGeneration,
                            Guid(config, "providerId"));
                    }

                    break;
                }
            case StepType.VideoAnalyze:
                {
                    JsonObject? config = Json(step.VideoAnalyzeConfigJson);
                    if (EnumName(Child(config, "transcription"), ["Off", "Optional", "Required"]) == "Required")
                        yield return (InferenceProviderCapability.Transcription, Guid(config, "transcriptionProviderId"));
                    if (EnumName(Child(config, "vision"), ["Off", "Optional", "Required"]) == "Required")
                        yield return (InferenceProviderCapability.Vision, Guid(config, "visionProviderId"));
                    break;
                }
        }
    }

    private static async Task<IEnumerable<LintFinding>> ProviderChecksAsync(List<WorkflowStep> steps, IWorkflowLintFacts facts, CancellationToken ct)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL006");
        List<LintFinding> findings = [];
        foreach (WorkflowStep step in steps)
        {
            bool lipSync = step.StepType == StepType.VideoGenerate && IsTalkingHead(Json(step.VideoGenerateConfigJson));
            bool lipSyncNotSet = false;
            if (lipSync && Guid(Json(step.VideoGenerateConfigJson), "providerId") is null
                && await facts.ProviderAvailableAsync(InferenceProviderCapability.LipSync, null, ct) is false)
            {
                // No lip-sync model resolves and none is chosen on the step: the engine fails the step the same way.
                lipSyncNotSet = true;
                // Args: stepOrder, stepLabel?.
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    new LintMessage(
                        $"The {StepGraphLinter.StepPart(step).Text} makes an avatar talk, and no lip-sync model is set up. Ask a workspace admin to add one under Lip-sync.",
                        LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label))),
                    new LintFix(LintFixKind.ChooseProvider, "Connect or choose a provider", LabelId: "WL006.chooseProvider"),
                    messageKey: LipSyncModelNotSetKey));
            }

            // A model the step's resolved video provider does not know fails the step ("Model ... is not supported").
            // A talking head is judged on the LipSync row it would run and on the settings it would really send: a step with no
            // provider of its own runs the default LipSync model with that model's own resolution, so only a bound row can fail.
            if (step.StepType == StepType.VideoGenerate && Json(step.VideoGenerateConfigJson) is { } videoConfig
                && (lipSync
                    ? await facts.LipSyncStepAcceptedAsync(videoConfig.ToJsonString(), ct) is false
                    : Child(videoConfig, "model") is { } plainNode && plainNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                        && plainNode.GetValue<string>() is { Length: > 0 } plainModel
                        && await facts.VideoModelSupportedAsync(Guid(videoConfig, "providerId"), plainModel, ct) is false))
            {
                string model = Child(videoConfig, "model") is { } modelNode && modelNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? modelNode.GetValue<string>()
                    : string.Empty;
                // Args: stepOrder, stepLabel?, model.
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    new LintMessage(
                        $"The {StepGraphLinter.StepPart(step).Text} uses the model {model}, which the video provider it would run on does not support. Choose a model that provider supports, or connect a provider that has it.",
                        LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label), ("model", model))),
                    new LintFix(LintFixKind.ChooseProvider, "Connect or choose a provider", LabelId: "WL006.chooseProvider"),
                    messageKey: ModelUnsupportedKey));
            }

            foreach ((InferenceProviderCapability capability, Guid? explicitId) in CapabilitiesNeeded(step))
            {
                if (await facts.ProviderAvailableAsync(capability, explicitId, ct) is not false)
                    continue;
                // The specific lip-sync finding above already says it.
                if (lipSyncNotSet && capability == InferenceProviderCapability.LipSync && explicitId is null)
                    continue;
                string problem = explicitId is null
                    ? "no provider is set as the default for it"
                    : "the provider chosen on the step is not available to your organization";
                string problemCode = explicitId is null ? "noDefault" : "providerUnavailable";
                // Args: stepOrder, stepLabel?, capability, problem.
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    rule.Format(
                        ("step", StepGraphLinter.StepPart(step)),
                        ("capability", LintPart.Of(CapabilityName(capability), ("capability", CapabilityCode(capability)))),
                        ("problem", LintPart.Of(problem, ("problem", problemCode)))),
                    new LintFix(LintFixKind.ChooseProvider, "Connect or choose a provider", LabelId: "WL006.chooseProvider")));
            }
        }

        return findings;
    }

    // ------------------------------------------------------------------
    // WL007 plan-not-entitled
    // ------------------------------------------------------------------

    private static async Task<IEnumerable<LintFinding>> PlanChecksAsync(List<WorkflowStep> steps, RunBrief? brief, IWorkflowLintFacts facts, CancellationToken ct)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL007");
        List<LintFinding> findings = [];
        foreach (WorkflowStep step in steps)
        {
            if (PlanGate.FeatureFor(step.StepType) is null || await facts.StepEntitlementAsync(step.StepType, ct) is null)
                continue;
            // Args: feature, stepOrder, stepLabel?.
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                rule.Format(("what", LintPart.Of(
                    $"{FeatureName(step.StepType)} ({StepGraphLinter.Describe(step)})",
                    ("feature", FeatureCode(step.StepType)), ("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label)))),
                new LintFix(LintFixKind.ChangePlanOrChoice, "Choose a different step or change your plan", LabelId: "WL007.changePlan"),
                messageKey: Variant(rule, "stepFeature")));
        }

        if (brief?.Resolution is { } resolution && OutputResolution.TryParse(resolution, out int? shortSide) && shortSide is { } side
            && await facts.ResolutionEntitlementAsync(side, ct) is not null)
        {
            // Args: resolution.
            findings.Add(new LintFinding(rule.Id, rule.Severity, null,
                rule.Format(("what", LintPart.Of(
                    $"The {OutputResolution.DisplayName(side)} resolution", ("resolution", OutputResolution.DisplayName(side))))),
                new LintFix(LintFixKind.ChangePlanOrChoice, "Choose a lower resolution or change your plan", LabelId: "WL007.changeResolution"),
                messageKey: Variant(rule, "resolution")));
        }

        return findings;
    }

    private static string FeatureName(StepType type) => PlanGate.FeatureFor(type) switch
    {
        PlanGate.RoomsFeature => "Multi-agent rooms",
        PlanGate.AudioGenerationFeature => "Audio generation",
        _ => "Video generation"
    };

    /// <summary>The token form of <see cref="FeatureName"/>.</summary>
    private static string FeatureCode(StepType type) => PlanGate.FeatureFor(type) switch
    {
        PlanGate.RoomsFeature => "rooms",
        PlanGate.AudioGenerationFeature => "audioGeneration",
        _ => "videoGeneration"
    };

    // ------------------------------------------------------------------
    // WL027 avatar-unusable
    // ------------------------------------------------------------------

    /// <summary>
    /// The avatars a VideoGenerate step will show (people and objects): its own <c>subjects</c> or <c>avatarId</c>, or
    /// (as the executor does) the run brief's on a plain inline step that names none and no first/last frame. Empty when
    /// the step shows no one and nothing.
    /// </summary>
    private static IReadOnlyList<Guid> EffectiveSubjectIds(WorkflowStep step, RunBrief? brief) =>
        EffectiveSubjectConfig(step, brief)?.EffectiveSubjects.Select(s => s.AvatarId).Where(id => id != System.Guid.Empty).Distinct().ToList() ?? [];

    /// <summary>
    /// The step's config as the executor will run it (the run brief's avatar filled in where the engine fills it), or
    /// null when it cannot be read or shows no avatar.
    /// </summary>
    private static VideoGenerateStepConfig? EffectiveSubjectConfig(WorkflowStep step, RunBrief? brief)
    {
        if (string.IsNullOrWhiteSpace(step.VideoGenerateConfigJson))
            return null;
        try
        {
            VideoGenerateStepConfig? config = System.Text.Json.JsonSerializer.Deserialize<VideoGenerateStepConfig>(
                step.VideoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions);
            if (config is null)
                return null;
            if (!config.HasAvatar && brief?.Avatar?.AvatarId is { } briefAvatar && briefAvatar != System.Guid.Empty
                && config is { HasFrameInputs: false, PlanSource: VideoGeneratePlanSource.Inline })
                config.AvatarId = briefAvatar;
            return config.HasAvatar ? config : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<IEnumerable<LintFinding>> AvatarChecksAsync(List<WorkflowStep> steps, RunBrief? brief, IWorkflowLintFacts facts, bool atRunStart, CancellationToken ct)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL027");
        List<LintFinding> findings = [];
        foreach (WorkflowStep step in steps.Where(s => s.StepType == StepType.VideoGenerate))
        {
            IReadOnlyList<Guid> subjectIds = EffectiveSubjectIds(step, brief);
            // The step names the avatar's speech but the run chooses no avatar (the step's own or the brief's): the engine
            // refuses it ("no avatar was chosen") on every attempt. Asked only at run start; the editor has no run yet.
            if (atRunStart && subjectIds.Count == 0 && Json(step.VideoGenerateConfigJson) is { } avatarConfig
                && (Child(avatarConfig, "avatarAudio") is not null || Guid(avatarConfig, "avatarAudioProjectFileId") is not null))
            {
                // Args: stepOrder, stepLabel?.
                findings.Add(new LintFinding(rule.Id, LintSeverity.Error, step.StepOrder,
                    new LintMessage(
                        $"The {StepGraphLinter.StepPart(step).Text} makes an avatar say its narration, and no avatar was chosen for this run. Choose an avatar picture for the run and run it again; nothing was spent.",
                        LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label))),
                    new LintFix(LintFixKind.ChangeSetting, "Choose an avatar that is ready", LabelId: "WL027.chooseAvatar"),
                    messageKey: NoAvatarChosenKey));
                continue;
            }

            // Every subject of the step is judged, a person and an object alike.
            foreach (Guid avatarId in subjectIds)
            {
                if (await facts.ProjectAvatarAsync(avatarId, ct) is not { } avatar || avatar.Usable)
                    continue;

                (string reason, string reasonCode) = avatar.UnusableReason switch
                {
                    "consent_missing" => ("its consent is not on record", "consentMissing"),
                    "consent_revoked" => ("its consent was withdrawn", "consentRevoked"),
                    "retention_expired" => ("its keep-until date has passed", "retentionExpired"),
                    _ => ("it is not an avatar of this project", "notInProject")
                };
                // The avatar's display name is the user's own label (never the consent subject's name).
                string name = avatar.Name is { Length: > 0 } ? $"\"{avatar.Name}\"" : "this step chose";
                string token = avatar.Name is { Length: > 0 } ? avatar.Name : "youChose";
                // Args: stepOrder, stepLabel?, avatar (the user's name, or the code youChose), reason.
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    rule.Format(
                        ("step", StepGraphLinter.StepPart(step)),
                        ("avatar", LintPart.Of(name, ("avatar", token))),
                        ("reason", LintPart.Of(reason, ("reason", reasonCode)))),
                    new LintFix(LintFixKind.ChangeSetting, "Choose an avatar that is ready", LabelId: "WL027.chooseAvatar")));
            }
        }

        return findings;
    }

    // ------------------------------------------------------------------
    // WL034 subject-model-unsupported
    // ------------------------------------------------------------------

    /// <summary>
    /// A step that shows an avatar or an object on a model that takes no pictures at all (docs/design/avatar-objects.md,
    /// decision AO-3): the run would refuse it before anything is charged, so the linter says so first and names the
    /// image-capable model of the same family. Judged from the model's own declarations
    /// (<see cref="ReelBolt.Shared.Inference.VideoSubjectModels"/>); an unknown provider or model is never a finding.
    /// </summary>
    private static async Task<IEnumerable<LintFinding>> SubjectModelChecksAsync(List<WorkflowStep> steps, RunBrief? brief, IWorkflowLintFacts facts, CancellationToken ct)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL034");
        List<LintFinding> findings = [];
        foreach (WorkflowStep step in steps.Where(s => s.StepType == StepType.VideoGenerate))
        {
            // A talking head (a step with avatar speech) resolves through the LipSync capability, never VideoGeneration:
            // master's lip-sync rules (WL006.lipSyncModelNotSet and the settings check) judge it, not this one.
            if (EffectiveSubjectConfig(step, brief) is not { } config
                || ReelBolt.Shared.Workflows.LipSyncStepResolution.IsLipSync(config)
                || await facts.VideoGenerationProviderKindAsync(config.ProviderId, ct) is not { } kind
                || !ReelBolt.Shared.Inference.VideoSubjectModels.TryDeclared(kind, config.Model, out var references, out var images)
                || references.AcceptsAvatar
                || images != ReelBolt.Shared.Inference.VideoImageInputSupport.None)
                continue;

            string? sibling = ReelBolt.Shared.Inference.VideoSubjectSiblings.For(kind, config.Model);
            string hint = sibling is null ? "Choose a model that takes pictures." : $"Use '{sibling}' instead.";
            // Two sentences: WL034.useSibling names the image-capable model of the same family, WL034.noSibling (a
            // text-only model with no known sibling) asks for any model that takes pictures.
            // Args: stepOrder, stepLabel?, model, sibling (useSibling only).
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                rule.Format(
                    ("step", StepGraphLinter.StepPart(step)),
                    ("model", LintPart.Of($"'{config.Model}'", ("model", config.Model))),
                    ("hint", LintPart.Of(hint, ("sibling", sibling)))),
                new LintFix(LintFixKind.ChooseModel, "Choose a model that takes pictures", LabelId: "WL034.chooseModel"),
                messageKey: sibling is null ? "WL034.noSibling" : "WL034.useSibling"));
        }

        return findings;
    }

    // ------------------------------------------------------------------
    // WL008 voice-consent-missing, WL009 voice-language-mismatch
    // ------------------------------------------------------------------

    private static async Task<IEnumerable<LintFinding>> VoiceChecksAsync(List<WorkflowStep> steps, RunBrief? brief, IWorkflowLintFacts facts, CancellationToken ct)
    {
        StepCompatibilityRule consent = StepCompatibilityRules.Get("WL008");
        StepCompatibilityRule language = StepCompatibilityRules.Get("WL009");
        List<LintFinding> findings = [];
        string? filmLanguage = brief?.Language;

        List<(int? Step, Guid VoiceId)> chosen = [];
        foreach (WorkflowStep step in steps.Where(s => s.StepType == StepType.Voiceover))
        {
            if (Guid(Json(step.VoiceoverConfigJson), "projectVoiceId") is { } id)
                chosen.Add((step.StepOrder, id));
        }

        // The brief's voice only matters when a Voiceover step will speak it.
        if (brief?.Voice?.ProjectVoiceId is { } briefVoice && steps.Any(s => s.StepType == StepType.Voiceover))
            chosen.Add((null, briefVoice));

        foreach ((int? stepOrder, Guid voiceId) in chosen.DistinctBy(c => (c.Step, c.VoiceId)))
        {
            if (await facts.ProjectVoiceAsync(voiceId, ct) is not { } voice)
                continue;

            string name = voice.Name is { Length: > 0 } ? $"\"{voice.Name}\"" : "you chose";
            string voiceToken = voice.Name is { Length: > 0 } ? voice.Name : "youChose";
            if (!voice.Exists || !voice.Selectable)
            {
                (string Reason, string ReasonCode) = voice.UnselectableReason switch
                {
                    "consent_missing" => ("consent has not been recorded for it", "consentMissing"),
                    "consent_revoked" => ("its consent was withdrawn", "consentRevoked"),
                    "retention_expired" => ("its retention period ended", "retentionExpired"),
                    _ => ("it is not in this project", "notInProject")
                };
                // A voice named on a step fails that step (error). One named only in the brief is a warning: it is
                // the run's default for the Voiceover step, which can still carry its own voice.
                // Args: voice (the user's name, or the code youChose), reason.
                findings.Add(new LintFinding(consent.Id, stepOrder is null ? LintSeverity.Warning : consent.Severity, stepOrder,
                    consent.Format(
                        ("voice", LintPart.Of(name, ("voice", voiceToken))),
                        ("reason", LintPart.Of(Reason, ("reason", ReasonCode)))),
                    new LintFix(LintFixKind.ChooseVoice, "Choose a voice with consent on record", LabelId: "WL008.chooseVoice")));
                continue;
            }

            if (filmLanguage is not null && voice.Languages is { Count: > 0 } spoken && !SpeaksLanguage(spoken, filmLanguage))
            {
                // Args: language (BCP-47), languageCount, language1…languageN.
                findings.Add(new LintFinding(language.Id, language.Severity, stepOrder,
                    language.Format(
                        ("language", LintPart.Of(filmLanguage, ("language", filmLanguage))),
                        ("languages", LintPart.Of(string.Join(", ", spoken), LintArgs.Indexed("language", spoken)))),
                    new LintFix(LintFixKind.ChooseVoice, "Choose a voice that speaks the film's language", LabelId: "WL009.chooseVoice")));
            }
        }

        return findings;
    }

    private static bool SpeaksLanguage(IReadOnlyList<string> spoken, string language)
    {
        string primary = PrimarySubtag(language);
        return spoken.Any(s => string.Equals(PrimarySubtag(s), primary, StringComparison.OrdinalIgnoreCase));
    }

    private static string PrimarySubtag(string tag)
    {
        int dash = tag.IndexOfAny(['-', '_']);
        return dash < 0 ? tag : tag[..dash];
    }

    // ------------------------------------------------------------------
    // WL010 source-media-missing
    // ------------------------------------------------------------------

    private static string RoleName(RunBriefFileRole role) => role switch
    {
        RunBriefFileRole.Video => "video footage",
        RunBriefFileRole.Code => "source code",
        RunBriefFileRole.Music => "a music track",
        RunBriefFileRole.Sfx => "sound effect files",
        RunBriefFileRole.Image => "images",
        RunBriefFileRole.InsertContent => "a picture or video for the screen",
        RunBriefFileRole.Overlay => "pictures or videos to show over the film",
        _ => "documents"
    };

    /// <summary>The token form of <see cref="RoleName"/>: the enum name in camelCase.</summary>
    private static string RoleCode(RunBriefFileRole role) => char.ToLowerInvariant(role.ToString()[0]) + role.ToString()[1..];

    private static async Task<IEnumerable<LintFinding>> MediaChecksAsync(List<WorkflowStep> steps, RunBrief? brief, IWorkflowLintFacts facts, bool atRunStart, CancellationToken ct)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL010");
        List<LintFinding> findings = [];
        IReadOnlyDictionary<RunBriefFileRole, int>? counts = await facts.FileCountsAsync(ct);
        int archives = counts is null ? 0 : await facts.ArchiveFilesAsync(ct);
        int unpacking = archives > 0 && counts!.GetValueOrDefault(RunBriefFileRole.Code) == 0 ? await facts.UnpackingArchivesAsync(ct) : 0;
        HashSet<RunBriefFileRole> briefRoles =
        [
            .. (brief?.SourceFiles ?? []).Where(s => s.Role is not null && s.FileIds is { Count: > 0 }).Select(s => s.Role!.Value)
        ];
        HashSet<RunBriefFileRole> reported = [];
        HashSet<int> openSlotSteps = [];

        // One question for every file id the workflow names (SEC-WAB-LINT-1): never a query per id.
        await facts.WarmFilesAsync(NamedFileIds(steps), ct);

        // The engine soft-degrades missing music and sound effects (the film is made without them), so those are
        // warnings; missing footage or code fails or empties the run, so those stay errors.
        static LintSeverity SeverityOf(RunBriefFileRole role) =>
            role is RunBriefFileRole.Music or RunBriefFileRole.Sfx ? LintSeverity.Warning : LintSeverity.Error;

        async Task NeedAsync(WorkflowStep step, RunBriefFileRole role)
        {
            if (counts is null || briefRoles.Contains(role) || !reported.Add(role))
                return;
            // The overlay role names project pictures/videos: with no named set the analyzer offers every
            // usable image and video, so those two counts are what satisfies it (as InsertContent already does).
            int projectCount = counts.TryGetValue(role, out int exact)
                ? exact
                : role == RunBriefFileRole.Overlay
                    ? counts.GetValueOrDefault(RunBriefFileRole.Image) + counts.GetValueOrDefault(RunBriefFileRole.Video)
                    : 0;
            if (projectCount > 0)
                return;
            // Only archives and no readable code: say we cannot read the zip rather than "there is none".
            // Args: stepOrder, stepLabel?.
            if (role == RunBriefFileRole.Code && archives > 0 && unpacking > 0)
            {
                // The zip is still being unpacked: its code is on the way, so say wait rather than "we can't read it".
                // Args: stepOrder, stepLabel?.
                findings.Add(new LintFinding(rule.Id, SeverityOf(role), step.StepOrder,
                    new LintMessage(
                        $"The {StepGraphLinter.StepPart(step).Text} needs source code, but your zip is still being unpacked. Its files appear in the project in a moment; try again shortly.",
                        LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label))),
                    new LintFix(
                        LintFixKind.UploadFiles,
                        $"Upload {RoleName(role)} to the project",
                        LabelId: "WL010.upload",
                        Args: LintArgs.Of(("role", RoleCode(role)))),
                    messageKey: Variant(rule, "unpacking")));
                return;
            }

            if (role == RunBriefFileRole.Code && archives > 0)
            {
                findings.Add(new LintFinding(rule.Id, SeverityOf(role), step.StepOrder,
                    new LintMessage(
                        $"The {StepGraphLinter.StepPart(step).Text} needs source code it can read, but the project only has archives (zip, tar). Upload the source as a .zip without node_modules, connect GitHub, or sync a folder.",
                        LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label))),
                    new LintFix(
                        LintFixKind.UploadFiles,
                        $"Upload {RoleName(role)} to the project",
                        LabelId: "WL010.upload",
                        Args: LintArgs.Of(("role", RoleCode(role)))),
                    messageKey: Variant(rule, "archiveOnly")));
                return;
            }

            // Args: stepOrder, stepLabel?, role.
            findings.Add(new LintFinding(rule.Id, SeverityOf(role), step.StepOrder,
                rule.Format(
                    ("step", StepGraphLinter.StepPart(step)),
                    ("role", LintPart.Of(RoleName(role), ("role", RoleCode(role))))),
                new LintFix(
                    LintFixKind.UploadFiles,
                    $"Upload {RoleName(role)} to the project",
                    LabelId: "WL010.upload",
                    Args: LintArgs.Of(("role", RoleCode(role)))),
                messageKey: Variant(rule, "missingMedia")));
            await Task.CompletedTask;
        }

        async Task SlotAsync(
            WorkflowStep step, Guid? fileId, string slot, string slotCode, LintSeverity severity = LintSeverity.Error)
        {
            if (fileId is not { } id || await facts.ProjectFileExistsAsync(id, ct) is not false)
                return;
            // Args: stepOrder, stepLabel?, slot.
            findings.Add(new LintFinding(rule.Id, severity, step.StepOrder,
                new LintMessage(
                    $"Step {step.StepOrder} points at {slot}, but that file is not in this project. The step would stop.",
                    LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label), ("slot", slotCode))),
                new LintFix(LintFixKind.UploadFiles, "Pick a file that is in the project", LabelId: "WL010.pickFile"),
                messageKey: Variant(rule, "missingFile")));
        }

        foreach (WorkflowStep step in steps)
        {
            if (step.StepType == StepType.Agent)
            {
                AgentType? agent = step.AgentDefinition?.AgentType;
                if (agent == AgentType.CodeStructureAnalyzer)
                    await NeedAsync(step, RunBriefFileRole.Code);
                else if (agent == AgentType.MusicSupervisor)
                    await NeedAsync(step, RunBriefFileRole.Music);
                else if (agent == AgentType.SoundDesigner)
                    await NeedAsync(step, RunBriefFileRole.Sfx);
            }
            else if (step.StepType == StepType.VideoAnalyze)
            {
                JsonObject? config = Json(step.VideoAnalyzeConfigJson);
                List<JsonObject> sources = [];
                // The engine (and VideoSourceGate) read `sources` when it is non-empty and `source` only otherwise.
                if (Child(config, "sources") is JsonArray { Count: > 0 } many)
                    sources.AddRange(many.OfType<JsonObject>());
                else if (Child(config, "source") is JsonObject one)
                    sources.Add(one);
                foreach (JsonObject source in sources)
                {
                    if (EnumName(Child(source, "kind"), ["ProjectFile", "StepOutput", "PreviousStepOutput", "GeneratedClips", "StepResult"]) != "ProjectFile")
                        continue;
                    if (Guid(source, "projectFileId") is { } fileId)
                        await SlotAsync(step, fileId, "a video file", "videoFile");
                    else if (atRunStart && !briefRoles.Contains(RunBriefFileRole.Video))
                    {
                        // The engine fills an open slot from the brief's video files only (never from the project), so a
                        // run that names none fails this step with SOURCE_UNRESOLVED: refused at the start, whatever the
                        // project holds (and whatever facts are known). One finding per step.
                        if (openSlotSteps.Add(step.StepOrder))
                        {
                            findings.Add(new LintFinding(rule.Id, LintSeverity.Error, step.StepOrder,
                                new LintMessage(
                                    $"The {StepGraphLinter.StepPart(step).Text} needs a video, and none was chosen for this run. Choose a video and run it again; nothing was spent.",
                                    LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label))),
                                new LintFix(
                                    LintFixKind.UploadFiles,
                                    "Choose a video for the run",
                                    LabelId: "WL010.upload",
                                    Args: LintArgs.Of(("role", RoleCode(RunBriefFileRole.Video)))),
                                messageKey: OpenVideoSlotKey));
                        }
                    }
                    else
                        await NeedAsync(step, RunBriefFileRole.Video);
                }

                if (Bool(config, "offerMusicTracks"))
                    await NeedAsync(step, RunBriefFileRole.Music);

                // The analyze step feeds an OverlayPlanner: with no pictures or videos in the brief or the
                // project, the planner has nothing to place and the film would have no pictures to show.
                if (Bool(config, "emitOverlayOffers"))
                    await NeedAsync(step, RunBriefFileRole.Overlay);
            }
            else if (step.StepType == StepType.VideoCompile)
            {
                JsonObject? config = Json(step.VideoCompileConfigJson);
                if (Bool(config, "enableMusic"))
                    await SlotAsync(step, Guid(config, "musicTrackProjectFileId"), "a music track", "musicTrack", LintSeverity.Warning);
                await SlotAsync(step, Guid(config, "insertContentProjectFileId"), "the file to insert", "insert");
                await MediaOverlayChecksAsync(step, config, steps, facts, findings, ct);
                MediaOverlaySpanChecks(step, config, brief, findings);

                // A whole-source screen insert with no plan and no content of its own gets its content from the run
                // (the brief's insertContent file). Judged only when a brief is in play; the run would otherwise fail.
                if (brief is not null && Bool(config, "enableInserts") && Bool(config, "keepWholeSources")
                    && Child(config, "graphicsPlan") is null && !Bool(config, "enableGeneratedClips")
                    && Child(config, "insertContentProjectFileId") is null
                    && Child(config, "insertContentRef") is null
                    && !(Child(config, "insertContents") is JsonArray contents && contents.Count > 0)
                    && !briefRoles.Contains(RunBriefFileRole.InsertContent))
                {
                    RunBriefFileRole role = RunBriefFileRole.InsertContent;
                    findings.Add(new LintFinding(rule.Id, LintSeverity.Error, step.StepOrder,
                        rule.Format(
                            ("step", StepGraphLinter.StepPart(step)),
                            ("role", LintPart.Of(RoleName(role), ("role", RoleCode(role))))),
                        new LintFix(
                            LintFixKind.UploadFiles,
                            $"Choose {RoleName(role)}",
                            LabelId: "WL010.upload",
                            Args: LintArgs.Of(("role", RoleCode(role)))),
                        messageKey: Variant(rule, "missingMedia")));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// WL023: every configured <c>mediaOverlays[].content</c> must be usable — a project picture/video
    /// that is present and not quarantined, or an earlier step that can produce one.
    /// </summary>
    private static async Task MediaOverlayChecksAsync(
        WorkflowStep step,
        JsonObject? config,
        List<WorkflowStep> steps,
        IWorkflowLintFacts facts,
        List<LintFinding> findings,
        CancellationToken ct)
    {
        if (Child(config, "mediaOverlays") is not JsonArray overlays)
            return;

        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL023");

        void Add(string variant, string text, IReadOnlyDictionary<string, string> args, string fixLabel, string fixKey)
        {
            findings.Add(new LintFinding(
                rule.Id,
                LintSeverity.Error,
                step.StepOrder,
                new LintMessage(text, args),
                new LintFix(LintFixKind.UploadFiles, fixLabel, LabelId: fixKey),
                messageKey: Variant(rule, variant)));
        }

        foreach (JsonObject overlay in overlays.OfType<JsonObject>())
        {
            if (Child(overlay, "content") is not JsonObject content)
                continue;

            string? from = EnumName(Child(content, "from"), ["ProjectFile", "Step", "StepResult"]);
            if (from == "ProjectFile")
            {
                Guid? fileId = Guid(content, "projectFileId");
                if (fileId is null)
                {
                    Add("missingFile",
                        $"Step {step.StepOrder} is set to show a picture or video, but it does not name a file. The step would stop.",
                        LintArgs.Of(("stepOrder", Num(step.StepOrder))),
                        "Pick a picture or video in this project",
                        "WL023.pickFile");
                    continue;
                }

                LintMediaVerdict? verdict = await facts.ProjectMediaVerdictAsync(fileId.Value, ct);
                if (verdict is null or LintMediaVerdict.Usable)
                    continue;

                (string variant, string problem) = verdict switch
                {
                    LintMediaVerdict.Missing => ("missingFile", "that file is not in this project"),
                    LintMediaVerdict.WrongKind => ("wrongKind", "that file is not an image or a video"),
                    _ => ("quarantined", "that file is quarantined")
                };

                Add(variant,
                    $"Step {step.StepOrder} is set to show a picture or video, but {problem}. The step would stop.",
                    LintArgs.Of(("stepOrder", Num(step.StepOrder))),
                    "Pick a picture or video in this project",
                    "WL023.pickFile");
            }
            else if (from == "Step")
            {
                int? sourceOrder = Child(content, "stepOrder") is JsonValue orderValue
                    && orderValue.TryGetValue(out int parsedOrder) ? parsedOrder : null;
                if (sourceOrder is not { } order)
                    continue;

                WorkflowStep? target = steps.FirstOrDefault(s => s.StepOrder == order);
                bool usable = target is not null
                    && order < step.StepOrder
                    && InsertContentRefSaveValidator.CanProducePlayableOutput(target.StepType);
                if (usable)
                    continue;

                Add("badStep",
                    $"Step {step.StepOrder} is set to show a picture from step {order}, but that step does not come earlier or cannot produce a picture or video. The step would stop.",
                    LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("sourceOrder", Num(order))),
                    "Point the picture at an earlier step that makes a video or image",
                    "WL023.earlierStep");
            }
        }
    }

    /// <summary>
    /// WL024/WL025: a configured span must be a real moment on the timeline, and a full-screen card must
    /// not outstay its welcome. No database fact is read: the only extra input is the brief's film length.
    /// </summary>
    private static void MediaOverlaySpanChecks(WorkflowStep step, JsonObject? config, RunBrief? brief, List<LintFinding> findings)
    {
        if (Child(config, "mediaOverlays") is not JsonArray overlays)
            return;

        double? filmLength = brief?.LengthSec is { } length && length > 0 ? length : null;

        foreach (JsonObject overlay in overlays.OfType<JsonObject>())
        {
            double? start = Number(overlay, "startSec");
            double? end = Number(overlay, "endSec");

            bool badMoment = !IsValidOverlayMoment(start)
                || !IsValidOverlayMoment(end)
                || (end is { } endValue && endValue <= (start ?? 0))
                || (filmLength is { } known && end is { } past && past > known + 0.5)
                || (filmLength is { } knownStart && start is { } startValue && startValue > knownStart + 0.5);
            if (badMoment)
            {
                StepCompatibilityRule rule = StepCompatibilityRules.Get("WL024");
                // Args: stepOrder, stepLabel?, start (seconds or firstFrame), end (seconds or lastFrame).
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    rule.Format(
                        ("step", StepGraphLinter.StepPart(step)),
                        ("start", MomentPart(start, "start", "the first frame", "firstFrame")),
                        ("end", MomentPart(end, "end", "the last frame", "lastFrame"))),
                    new LintFix(LintFixKind.ChangeSetting, "Change the overlay's timing", LabelId: "WL024.changeSpan")));
            }

            if (EnumName(Child(overlay, "form"), ["PictureInPicture", "Card"]) != "Card")
                continue;

            // A card with no end runs to the last frame, so it is only measurable when the film length is known.
            double? cardEnd = end ?? filmLength;
            if (cardEnd is not { } finish)
                continue;
            double duration = finish - (start ?? 0);
            if (!double.IsFinite(duration) || duration <= 0)
                continue;

            bool tooLong = duration > 30 || (filmLength is { } film && duration > film * 0.25);
            if (!tooLong)
                continue;

            StepCompatibilityRule cardRule = StepCompatibilityRules.Get("WL025");
            // Args: stepOrder, stepLabel?, seconds.
            findings.Add(new LintFinding(cardRule.Id, cardRule.Severity, step.StepOrder,
                cardRule.Format(
                    ("step", StepGraphLinter.StepPart(step)),
                    ("seconds", Seconds(duration))),
                new LintFix(LintFixKind.ChangeSetting, "Shorten the card or make it an inset", LabelId: "WL025.shortenCard")));
        }

        static bool IsValidOverlayMoment(double? value) =>
            value is not { } moment || (double.IsFinite(moment) && moment >= 0 && moment <= VideoSourceRef.MaxWindowSec);

        static LintPart MomentPart(double? value, string name, string unsetText, string unsetCode) =>
            value is { } moment
                ? LintPart.Of($"{Seconds(moment)} seconds", (name, Seconds(moment)))
                : LintPart.Of(unsetText, (name, unsetCode));

        static string Seconds(double value) =>
            value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Every project file id the steps name in the places WL010 checks (distinct, bounded by the caller).</summary>
    internal static List<System.Guid> NamedFileIds(IEnumerable<WorkflowStep> steps)
    {
        HashSet<System.Guid> ids = [];
        foreach (WorkflowStep step in steps)
        {
            if (step.StepType == StepType.VideoAnalyze)
            {
                JsonObject? config = Json(step.VideoAnalyzeConfigJson);
                List<JsonObject> sources = [];
                if (Child(config, "source") is JsonObject one)
                    sources.Add(one);
                if (Child(config, "sources") is JsonArray many)
                    sources.AddRange(many.OfType<JsonObject>());
                foreach (JsonObject source in sources)
                {
                    if (Guid(source, "projectFileId") is { } id)
                        ids.Add(id);
                }
            }
            else if (step.StepType == StepType.VideoCompile)
            {
                JsonObject? config = Json(step.VideoCompileConfigJson);
                if (Guid(config, "musicTrackProjectFileId") is { } music)
                    ids.Add(music);
                if (Guid(config, "insertContentProjectFileId") is { } insert)
                    ids.Add(insert);

                // WL023 names the same files through mediaOverlays[].content, so they must count
                // against the controller's reference cap and be warmed with the rest.
                if (Child(config, "mediaOverlays") is JsonArray overlays)
                {
                    foreach (JsonObject overlay in overlays.OfType<JsonObject>())
                    {
                        if (Child(overlay, "content") is JsonObject content
                            && Guid(content, "projectFileId") is { } overlayFile)
                        {
                            ids.Add(overlayFile);
                        }
                    }
                }
            }
        }

        return [.. ids];
    }

    // ------------------------------------------------------------------
    // WL012 design-system-missing
    // ------------------------------------------------------------------

    /// <summary>A Require step needs the approved design unless it asks for the product map only (requireDesign: false).</summary>
    private static bool RequiresDesign(JsonObject? config)
    {
        if (EnumName(Child(config, "op"), ["Parse", "Compose", "Require"]) != "Require")
            return false;
        return !(Child(Child(config, "require") as JsonObject, "requireDesign") is JsonValue v && v.TryGetValue(out bool wanted) && !wanted);
    }

    private static async Task<IEnumerable<LintFinding>> DesignSystemChecksAsync(List<WorkflowStep> steps, RunBrief? brief, IWorkflowLintFacts facts, CancellationToken ct)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL012");
        List<WorkflowStep> requiring = [.. steps.Where(s => s.StepType == StepType.DesignSystem
            && RequiresDesign(Json(s.DesignSystemConfigJson)))];
        bool briefExpects = string.Equals(brief?.DesignSystem, RunBrief.DesignSystemProject, StringComparison.OrdinalIgnoreCase);
        if ((requiring.Count == 0 && !briefExpects) || await facts.HasApprovedDesignSystemAsync(ct) is not false)
            return [];

        List<LintFinding> findings = [];
        foreach (WorkflowStep step in requiring)
        {
            // Args: stepOrder, stepLabel?.
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                rule.Format(("what", StepGraphLinter.StepPart(step))),
                new LintFix(LintFixKind.ApproveDesignSystem, "Approve the project's design system", LabelId: "WL012.approveDesign"),
                messageKey: Variant(rule, "step")));
        }

        // The brief alone does not fail a run (a run without a pin proceeds), so that variant only warns.
        if (requiring.Count == 0)
        {
            findings.Add(new LintFinding(rule.Id, LintSeverity.Warning, null,
                rule.Format(("what", LintPart.Of("Your brief"))),
                new LintFix(LintFixKind.ApproveDesignSystem, "Approve the project's design system", LabelId: "WL012.approveDesign"),
                messageKey: Variant(rule, "brief")));
        }

        return findings;
    }

    // ------------------------------------------------------------------
    // WL011 brief-field-unread, WL016 silent-output
    // ------------------------------------------------------------------

    /// <summary>
    /// The brief fields that are set but that no step of the workflow reads, in plain words ("language", "length", "style or
    /// tone"): WL011's own test, shared with the run card so it can say "Not used by this app" with the linter's answer.
    /// </summary>
    public static IReadOnlyList<string> UnreadBriefFields(IReadOnlyCollection<WorkflowStep> steps, RunBrief brief) =>
        [.. UnreadBriefFieldTokens(steps, brief).Select(f => f.Name)];

    /// <summary>The same unread fields, each with its token code (<c>language</c>, <c>sourceLanguage</c>, …) for WL011.</summary>
    private static IReadOnlyList<(string Name, string Code)> UnreadBriefFieldTokens(IReadOnlyCollection<WorkflowStep> steps, RunBrief brief)
    {
        (LintBriefField Field, string Name, string Code, bool IsSet)[] fields =
        [
            (LintBriefField.Language, "language", "language", brief.Language is not null),
            (LintBriefField.SourceLanguage, "source language", "sourceLanguage", brief.SourceLanguage is not null),
            (LintBriefField.LengthSec, "length", "lengthSec", brief.LengthSec is not null),
            (LintBriefField.Voice, "voice choice", "voice",
                brief.Voice is { } v && (v.Mode == RunBriefVoiceMode.Narrated || v.ProjectVoiceId is not null || v.LibraryVoiceId is not null)),
            (LintBriefField.Style, "style or tone", "style",
                brief.StylePreset is not null || brief.StyleId is not null || brief.Style is not null || brief.Tone is not null),
            (LintBriefField.Format, "format", "format", brief.Format is not null && !string.Equals(brief.Format, "Original", StringComparison.OrdinalIgnoreCase)),
            (LintBriefField.Resolution, "resolution", "resolution", brief.Resolution is not null && !string.Equals(brief.Resolution, "Auto", StringComparison.OrdinalIgnoreCase)),
            (LintBriefField.DesignSystem, "design system choice", "designSystem", brief.DesignSystem is not null),
            (LintBriefField.SourceFiles, "source files", "sourceFiles", brief.SourceFiles is { Count: > 0 }),
            (LintBriefField.Avatar, "avatar", "avatar", brief.Avatar?.AvatarId is not null)
        ];
        return [.. fields.Where(f => f.IsSet && !steps.Any(s => StepCompatibilityRules.StepReads(s, f.Field))).Select(f => (f.Name, f.Code))];
    }

    private static IEnumerable<LintFinding> BriefChecks(List<WorkflowStep> steps, RunBrief? brief)
    {
        if (brief is null)
            yield break;

        StepCompatibilityRule unread = StepCompatibilityRules.Get("WL011");
        foreach ((string name, string code) in UnreadBriefFieldTokens(steps, brief))
        {
            // Args: field.
            yield return new LintFinding(unread.Id, unread.Severity, null,
                unread.Format(("field", LintPart.Of(name, ("field", code)))));
        }

        // WL016: narrated, but nothing speaks.
        bool narrated = brief.Voice is { } voice
            && (voice.Mode == RunBriefVoiceMode.Narrated || voice.ProjectVoiceId is not null || voice.LibraryVoiceId is not null);
        if (narrated && !steps.Any(s => s.StepType == StepType.Voiceover))
        {
            StepCompatibilityRule silent = StepCompatibilityRules.Get("WL016");
            yield return new LintFinding(silent.Id, silent.Severity, null, silent.Format(),
                new LintFix(LintFixKind.AddStep, "Add a voiceover step", "addVoiceover", LabelId: "WL016.addVoiceover"));
        }
    }

    // ------------------------------------------------------------------
    // WL017 length-unreachable
    // ------------------------------------------------------------------

    private static async Task<IEnumerable<LintFinding>> LengthChecksAsync(List<WorkflowStep> steps, RunBrief? brief, IWorkflowLintFacts facts, CancellationToken ct)
    {
        if (brief?.LengthSec is not { } length || !steps.Any(s => s.StepType == StepType.VideoAnalyze)
            || await facts.SourceVideoSecondsAsync(ct) is not { } available || length <= available)
            return [];

        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL017");
        // Args: length, available (seconds, as digits).
        return
        [
            new LintFinding(rule.Id, rule.Severity, null,
                rule.Format(("length", length), ("available", (int)Math.Round(available))),
                new LintFix(LintFixKind.ChangeSetting, "Ask for a shorter film or add more footage", LabelId: "WL017.shorterFilm"))
        ];
    }

    // ------------------------------------------------------------------
    // WL018 manifest-orphan
    // ------------------------------------------------------------------

    private async Task<IEnumerable<LintFinding>> ManifestChecksAsync(WorkflowLintRequest request, CancellationToken ct)
    {
        IReadOnlyList<AppManifestOrphan> orphans;
        try
        {
            orphans = await _manifestOrphans.FindOrphansAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }

        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL018");
        return orphans.Select(orphan =>
        {
            // Args: input (the user's label), inputIndex, role? (the file variant only).
            (string Key, IReadOnlyDictionary<string, string> Args) = OrphanTokens(orphan);
            return new LintFinding(rule.Id, rule.Severity, null, new LintMessage(orphan.Message, Args),
                new LintFix(LintFixKind.RemoveReference, "Remove the control or point it at an existing step", LabelId: "WL018.removeReference"),
                messageKey: Key);
        });
    }

    /// <summary>
    /// WL018 has two sentences (a brief field nothing reads, a file role no step takes). <see cref="AppManifestOrphan"/>
    /// is deliberately left as it is (its <c>Code</c> stays <c>WL018</c> for the save paths), so the variant is read
    /// from the validator's own sentence: only the file variant names the role ("takes &lt;role&gt; files").
    /// </summary>
    private static (string Key, IReadOnlyDictionary<string, string> Args) OrphanTokens(AppManifestOrphan orphan)
    {
        Match file = FileRoleOrphan().Match(orphan.Message);
        return file.Success
            ? ($"WL018.unusedRole", LintArgs.Of(
                ("input", orphan.InputLabel), ("inputIndex", Num(orphan.InputIndex)), ("role", file.Groups["role"].Value)))
            : ($"WL018.unreadField", LintArgs.Of(("input", orphan.InputLabel), ("inputIndex", Num(orphan.InputIndex))));
    }

    [GeneratedRegex(@"takes (?<role>\S+) files", RegexOptions.CultureInvariant)]
    private static partial Regex FileRoleOrphan();

    // ------------------------------------------------------------------
    // Draft support: request steps to steps
    // ------------------------------------------------------------------

    /// <summary>
    /// A draft (the <c>steps</c> body of the lint endpoint) as the <see cref="WorkflowStep"/> list the rules read.
    /// <paramref name="agents"/> maps an agent definition id to its definition; an unknown id stays unresolved
    /// (an "unknown agent", which no rule judges).
    /// </summary>
    public static List<WorkflowStep> FromRequests(
        IEnumerable<CreateWorkflowStepRequest> requests, IReadOnlyDictionary<Guid, AgentDefinition> agents) =>
        [.. requests.Select(r => new WorkflowStep
        {
            AgentDefinitionId = r.AgentDefinitionId,
            AgentDefinition = agents.GetValueOrDefault(r.AgentDefinitionId)!,
            StepOrder = r.StepOrder,
            EdgeConditionJson = r.EdgeConditionJson,
            Label = r.Label,
            StepType = Enum.TryParse(r.StepType, out StepType type) ? type : StepType.Agent,
            ConditionExpression = r.ConditionExpression,
            LoopSourceExpression = r.LoopSourceExpression,
            LoopTargetStepOrder = r.LoopTargetStepOrder,
            MaxIterations = r.MaxIterations ?? 3,
            MinScore = r.MinScore,
            InputMappingJson = r.InputMappingJson,
            AgentInputContextMode = Enum.TryParse(r.AgentInputContextMode, out AgentInputContextMode mode) ? mode : null,
            SelectedPriorStepOrdersJson = r.SelectedPriorStepOrdersJson,
            TrueBranchStepOrder = r.TrueBranchStepOrder,
            FalseBranchStepOrder = r.FalseBranchStepOrder,
            ParallelAgentIdsJson = r.ParallelAgentIdsJson,
            ExtractConfigJson = r.ExtractConfigJson,
            VideoAnalyzeConfigJson = r.VideoAnalyzeConfigJson,
            VideoCompileConfigJson = r.VideoCompileConfigJson,
            VideoGenerateConfigJson = r.VideoGenerateConfigJson,
            EditRoomConfigJson = r.EditRoomConfigJson,
            GraphicsRoomConfigJson = r.GraphicsRoomConfigJson,
            ColorGradeRoomConfigJson = r.ColorGradeRoomConfigJson,
            ConditionalConfigJson = r.ConditionalConfigJson,
            ReviewLoopConfigJson = r.ReviewLoopConfigJson,
            VoiceoverConfigJson = r.VoiceoverConfigJson,
            ThumbnailGenerateConfigJson = r.ThumbnailGenerateConfigJson,
            TimelineRenderConfigJson = r.TimelineRenderConfigJson,
            DailiesRoomConfigJson = r.DailiesRoomConfigJson,
            DesignSystemConfigJson = r.DesignSystemConfigJson,
            ExtractFrameConfigJson = r.ExtractFrameConfigJson,
            AudioGenerateConfigJson = r.AudioGenerateConfigJson,
            PinInsertConfigJson = r.PinInsertConfigJson,
            AudioCleanupConfigJson = r.AudioCleanupConfigJson,
            VideoEnhanceConfigJson = r.VideoEnhanceConfigJson,
            ImageGenerateConfigJson = r.ImageGenerateConfigJson,
            ContinueCompositionJson = r.ContinueCompositionJson,
            RegisterOutputAsProjectFile = r.RegisterOutputAsProjectFile,
            CacheMode = Enum.TryParse(r.CacheMode, out StepCacheMode cache) ? cache : null
        })];

    // ------------------------------------------------------------------
    // Tolerant JSON helpers (same reading rules as the engine)
    // ------------------------------------------------------------------

    private static JsonObject? Json(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Child(JsonObject? node, string name)
    {
        if (node is null)
            return null;
        foreach (KeyValuePair<string, JsonNode?> property in node)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static bool Bool(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static double? Number(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out double number) ? number : null;

    // Strict "D" form (dashed, no braces): the same form the controller's reference cap counts, so a payload cannot
    // name ids in a shape the cap does not see.
    private static Guid? Guid(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out string? text)
            && System.Guid.TryParseExact(text, "D", out Guid id) && id != System.Guid.Empty
            ? id
            : null;

    private static string? EnumName(JsonNode? node, string[] names)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        if (value.TryGetValue(out int ordinal) && ordinal >= 0 && ordinal < names.Length)
            return names[ordinal];
        return null;
    }
}
