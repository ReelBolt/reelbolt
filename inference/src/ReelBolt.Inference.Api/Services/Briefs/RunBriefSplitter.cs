using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Services.Briefs;

/// <summary>
/// What a run was asked for once the brief is split: the resolved <see cref="RunChoices"/> (with the stored
/// brief) and the run's effective request text, or the plain-language reason it could not be resolved.
/// </summary>
public sealed record RunBriefResolution(
    RunChoices? Choices,
    string? UserRequest,
    string? Error,
    string? ErrorCode = null,
    AppInputGateResult? AppInputs = null,
    string? ErrorInputId = null)
{
    public static RunBriefResolution Fail(string error, string? code = null, string? inputId = null) =>
        new(null, null, error, code, ErrorInputId: inputId);

    /// <summary>
    /// The 400 body of a refusal: <c>{ message }</c> as before, plus <c>code</c> only when the refusal has one (the app-input
    /// codes), and <c>inputId</c> when an app-input refusal names one input, so every existing refusal answers byte-identically.
    /// </summary>
    public object ErrorBody() => ErrorCode is { } code
        ? ErrorInputId is { } inputId ? new { code, message = Error, inputId } : new { code, message = Error }
        : new { message = Error };

    /// <summary>
    /// What the run-start gate prices, lints and gates: the bound, reachable steps when the app uses app inputs
    /// (docs/design/app-inputs.md 8.5, 9.3), else <paramref name="otherwise"/>.
    /// </summary>
    public RunStartPricing PricingOr(RunStartPricing otherwise) =>
        AppInputs is { Applies: true, BoundSteps: { } bound, ReachableStepOrders: { } reachable }
            ? RunStartPricing.Charged(bound, reachable) with { AgentModels = AppInputs.AgentModels, StepModels = AppInputs.StepModels }
            : otherwise;
}

/// <summary>
/// The app-input part of a run start (docs/design/app-inputs.md 6.2): the validator and the workflow being
/// started. A caller that starts no saved workflow passes null, and <c>brief.inputs</c> is then refused (nothing binds them).
/// </summary>
/// <param name="MediaEgressGrantedBy">
/// Who may give the permission to send media on this run start (<see cref="ReelBolt.Shared.Workflows.Apps.MediaEgressPermission.Grantors"/>),
/// or null when this caller may not: <c>inputs.allowMediaEgress = true</c> is then refused (fail closed). Each caller
/// decides it from its own rule (the app session, a key's <c>media.egress</c> scope, the user's MCP setting, an approved
/// chat card); it is never inferred here.
/// </param>
public sealed record AppInputRunStart(AppInputValueValidator Validator, Guid? OrganizationId, Guid WorkflowId, string? MediaEgressGrantedBy = null);

/// <summary>
/// The one place a <see cref="RunBrief"/> is split into the run's homes (docs/run-brief.md): style, format and
/// resolution through <see cref="IRunStyleResolver"/> into their existing columns, tone folded into the style
/// snapshot, notes into the run's request text, and everything else into a <see cref="StoredRunBrief"/>. REST,
/// the assistant's REST twin and the assistant tool all call this, BEFORE the credit preflight. With no brief
/// it is exactly the resolver call every path made before briefs existed.
/// </summary>
public static class RunBriefSplitter
{
    /// <summary>The refusal of <c>brief.inputs</c> when the caller starts no saved workflow, so there is no app to bind them.</summary>
    public const string InputsNotAvailable = "This app has no inputs of that kind yet: remove 'inputs' from the brief.";

    public static async Task<RunBriefResolution> ResolveAsync(
        IRunStyleResolver resolver,
        InferenceApiDbContext db,
        Guid userId,
        Guid projectId,
        LegacyRunInputs legacy,
        RunBrief? brief,
        CancellationToken ct,
        AppInputRunStart? appInputs = null)
    {
        // A caller that starts no saved workflow has no app: brief.inputs is refused below.
        bool inputsOn = appInputs is not null;

        if (brief is null)
        {
            RunChoicesResolution plain = await resolver.ResolveAsync(
                userId, projectId, legacy.StyleId, legacy.StylePresetKey, legacy.Style, legacy.OutputFormat, legacy.OutputResolution, ct);
            if (!inputsOn || plain.Error is not null)
                return new RunBriefResolution(plain.Choices, legacy.UserRequest, plain.Error);

            // An app with inputs is checked with no values (defaults, required inputs) and composes its request template.
            AppInputGateResult noValues = await appInputs!.Validator.CheckAsync(
                appInputs.OrganizationId, projectId, appInputs.WorkflowId, null, null, null, legacy.UserRequest, ct,
                mediaEgress: new MediaEgressGate(false, appInputs.MediaEgressGrantedBy));
            if (noValues.Refused)
                return RunBriefResolution.Fail(noValues.Message!, noValues.Code, noValues.InputId);
            if (!noValues.Applies)
                return new RunBriefResolution(plain.Choices, legacy.UserRequest, plain.Error);

            RunChoices withInputs = noValues.Inputs is null
                ? plain.Choices ?? new RunChoices(null, null)
                : (plain.Choices ?? new RunChoices(null, null)) with { Brief = new StoredRunBrief(Inputs: noValues.Inputs) };
            return new RunBriefResolution(withInputs.IsEmpty ? null : withInputs, noValues.UserRequest, null, AppInputs: noValues);
        }

        string? invalid = RunBriefValidator.Validate(brief) ?? RunBriefValidator.FindLegacyConflict(brief, legacy);
        if (invalid is not null)
            return RunBriefResolution.Fail(invalid);

        // With no app to bind them (docs/design/app-inputs.md 15), values are refused rather than stored unbound or silently
        // dropped. Otherwise they are checked against the manifest below, after the brief's own checks.
        if (!inputsOn && brief.Inputs is { Count: > 0 })
            return RunBriefResolution.Fail(InputsNotAvailable);

        if (await FindMissingFileAsync(db, projectId, brief, ct))
            return RunBriefResolution.Fail("One or more source files were not found in this project.");

        if (await HasWrongMediaAsync(db, projectId, brief, RunBriefFileRole.InsertContent, ct))
            return RunBriefResolution.Fail("The file for 'insertContent' must be a picture or a video.");

        if (await HasWrongMediaAsync(db, projectId, brief, RunBriefFileRole.Overlay, ct))
            return RunBriefResolution.Fail("Files for 'overlay' must be pictures or videos.");

        if (await AvatarProblemAsync(db, projectId, brief, ct) is { } avatarProblem)
            return RunBriefResolution.Fail(avatarProblem);

        RunChoicesResolution resolved = await resolver.ResolveAsync(
            userId,
            projectId,
            legacy.StyleId ?? brief.StyleId,
            string.IsNullOrWhiteSpace(legacy.StylePresetKey) ? brief.StylePreset : legacy.StylePresetKey,
            legacy.Style ?? brief.Style,
            string.IsNullOrWhiteSpace(legacy.OutputFormat) ? brief.Format : legacy.OutputFormat,
            string.IsNullOrWhiteSpace(legacy.OutputResolution) ? brief.Resolution : legacy.OutputResolution,
            ct);
        if (resolved.Error is not null)
            return RunBriefResolution.Fail(resolved.Error);

        RunChoices choices = resolved.Choices ?? new RunChoices(null, null);
        RunStyleSnapshot? style = FoldTone(choices.Style, brief.Tone);
        StoredRunBrief? stored = ToStored(brief);
        string? userRequest = string.IsNullOrWhiteSpace(legacy.UserRequest) ? brief.Notes : legacy.UserRequest;

        // App inputs (docs/design/app-inputs.md 6.2): checked against the manifest, dry-run bound, then stored in their
        // canonical form; the request template is composed with the notes. A refusal is a 400 that costs nothing.
        AppInputGateResult? gate = null;
        if (inputsOn)
        {
            gate = await appInputs!.Validator.CheckAsync(
                appInputs.OrganizationId, projectId, appInputs.WorkflowId, null, null, brief.Inputs, userRequest, ct,
                mediaEgress: new MediaEgressGate(brief.Avatar?.AvatarId is not null, appInputs.MediaEgressGrantedBy));
            if (gate.Refused)
                return RunBriefResolution.Fail(gate.Message!, gate.Code, gate.InputId);
            if (gate.Applies)
            {
                userRequest = gate.UserRequest;
                if (gate.Inputs is not null)
                    stored = (stored ?? new StoredRunBrief()) with { Inputs = gate.Inputs, AllowMediaEgressBy = gate.MediaEgressBy };
            }
        }

        RunChoices result = choices with { Style = style, Brief = stored };
        return new RunBriefResolution(result.IsEmpty ? null : result, userRequest, null, AppInputs: gate is { Applies: true } ? gate : null);
    }

    /// <summary>
    /// Tone is one axis, not two: it is written into the run's style snapshot, overriding the preset's or saved
    /// style's tone, so the run has one tone, one nudge sentence and one cache-key component. With no style at
    /// all, a tone alone creates a one-axis snapshot named "Custom".
    /// </summary>
    internal static RunStyleSnapshot? FoldTone(RunStyleSnapshot? style, StyleTone? tone)
    {
        if (tone is not { } t)
            return style;
        if (style is null)
            return new RunStyleSnapshot("Custom", null, null, new StyleConfig(Tone: t));
        return style with { Config = style.Config with { Tone = t } };
    }

    /// <summary>The brief fields that have no column of their own, normalized; null when none is set.</summary>
    internal static StoredRunBrief? ToStored(RunBrief brief)
    {
        RunBriefVoice? voice = brief.Voice is { Mode: null, ProjectVoiceId: null, LibraryVoiceId: null } ? null : brief.Voice;
        IReadOnlyList<RunBriefSourceFiles>? files = brief.SourceFiles is { Count: > 0 } g
            ? g.Select(x => new RunBriefSourceFiles(x.Role, x.FileIds!.Distinct().ToList())).ToList()
            : null;

        string? designSystem = RunBriefValidator.NormalizeDesignSystem(brief.DesignSystem);
        StoredRunBrief stored = new(
            Language: RunBriefValidator.TryNormalizeLanguage(brief.Language, out string lang) ? lang : null,
            SourceLanguage: RunBriefValidator.TryNormalizeLanguage(brief.SourceLanguage, out string src) ? src : null,
            LengthSec: brief.LengthSec,
            // "project" is today's behaviour, so only the departure is stored.
            DesignSystem: designSystem == RunBrief.DesignSystemNone ? designSystem : null,
            Voice: voice,
            SourceFiles: files,
            OverlayCardAudio: RunBriefValidator.NormalizeOverlayCardAudio(brief.OverlayCardAudio),
            Avatar: brief.Avatar?.AvatarId is null ? null : brief.Avatar,
            Pace: RunBriefValidator.NormalizePace(brief.Pace));
        return stored.IsEmpty ? null : stored;
    }

    /// <summary>
    /// The project checks of a brief's files and avatar, in the run start's order and words (shared with a change request
    /// that changes run settings, docs/design/app-inputs.md 29): the refusal sentence, or null.
    /// </summary>
    internal static async Task<string?> ProjectProblemAsync(InferenceApiDbContext db, Guid projectId, RunBrief brief, CancellationToken ct)
    {
        if (await FindMissingFileAsync(db, projectId, brief, ct))
            return "One or more source files were not found in this project.";
        if (await HasWrongMediaAsync(db, projectId, brief, RunBriefFileRole.InsertContent, ct))
            return "The file for 'insertContent' must be a picture or a video.";
        if (await HasWrongMediaAsync(db, projectId, brief, RunBriefFileRole.Overlay, ct))
            return "Files for 'overlay' must be pictures or videos.";
        return await AvatarProblemAsync(db, projectId, brief, ct);
    }

    /// <summary>
    /// Why the brief's avatar cannot be used, or null: it must be an avatar of THIS project (another project's id is
    /// "not found") whose consent is on record, not withdrawn and not expired (the consent half of the one rule; the
    /// engine hashes the reference files at run time).
    /// </summary>
    private static async Task<string?> AvatarProblemAsync(InferenceApiDbContext db, Guid projectId, RunBrief brief, CancellationToken ct)
    {
        if (brief.Avatar?.AvatarId is not Guid avatarId)
            return null;

        ProjectAvatar? avatar = await db.ProjectAvatars.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == projectId, ct);
        // The brief may name a person or an object (docs/design/avatar-objects.md). An avatar of a kind that is
        // switched off is treated as absent, exactly as the avatar routes treat it.
        if (avatar is null || !ReelBolt.Shared.Workflows.Apps.AppStepFacts.AvatarKindEnabled(avatar.SubjectKind))
            return "The avatar was not found in this project.";

        string? reason = avatar.UnselectableReason(DateTime.UtcNow, [new AvatarReferenceState(AvatarReferenceKind.Image, true, false)],
            await ReelBolt.Shared.Avatars.AvatarVariantOriginals.LoadAsync(db, projectId, [avatar], ct));
        return reason is null ? null : $"The avatar cannot be used ({reason}).";
    }

    /// <summary>True when a file named for <paramref name="role"/> is neither an image nor a video.</summary>
    private static async Task<bool> HasWrongMediaAsync(
        InferenceApiDbContext db, Guid projectId, RunBrief brief, RunBriefFileRole role, CancellationToken ct)
    {
        List<Guid> ids = brief.SourceFiles?.Where(g => g.Role == role).SelectMany(g => g.FileIds ?? []).Distinct().ToList() ?? [];
        if (ids.Count == 0)
            return false;
        int ok = await db.ProjectFiles.AsNoTracking().CountAsync(
            f => f.ProjectId == projectId && ids.Contains(f.Id) && (f.MimeType.StartsWith("image/") || f.MimeType.StartsWith("video/")), ct);
        return ok != ids.Count;
    }

    private static async Task<bool> FindMissingFileAsync(InferenceApiDbContext db, Guid projectId, RunBrief brief, CancellationToken ct)
    {
        if (brief.SourceFiles is not { Count: > 0 } groups)
            return false;

        List<Guid> ids = groups.SelectMany(g => g.FileIds ?? []).Distinct().ToList();
        int found = await db.ProjectFiles.AsNoTracking().CountAsync(f => f.ProjectId == projectId && ids.Contains(f.Id)
                 && f.CsamStatus != CsamScanStatus.Quarantined, ct);
        return found != ids.Count;
    }
}
