using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Ask for a change to one step of a finished run (docs/design/change-requests.md). Name the step by
/// <paramref name="StepOrder"/> or by <paramref name="StepResultId"/>. <paramref name="Overrides"/> is keyed
/// by item id or <c>"*"</c>, then by the step type's closed override keys. To change several steps with ONE
/// review and ONE replay run send <paramref name="Targets"/> (1..6, distinct steps) INSTEAD of the top-level
/// target fields; mixing the two is a 400.
/// <para>
/// <paramref name="Inputs"/> (docs/design/app-inputs.md 25) is
/// the other kind of request: new values for the app's own fields, <c>{inputId: value}</c> (<c>null</c> clears an
/// optional field). The steps that use a changed value are re-made and everything else is reused.
/// </para>
/// <para>
/// <paramref name="Brief"/> (docs/design/app-inputs.md 29) changes the run's settings: the fields set are the
/// new values (language, sourceLanguage, lengthSec, voice, designSystem, overlayCardAudio, avatar, notes; sourceFiles
/// replaces the roles it lists). Format, resolution, style and tone change the whole film and are refused
/// (<c>BRIEF_FIELD_NOT_CHANGEABLE</c>). <paramref name="Inputs"/> and <paramref name="Brief"/> may be
/// combined with step <paramref name="Targets"/> (never the single-target fields): ONE plan, ONE estimate, ONE hold, ONE run.
/// </para>
/// </summary>
public sealed record CreateStepChangeRequest(
    int? StepOrder,
    Guid? StepResultId,
    string? Instruction,
    IReadOnlyList<string>? ItemIds,
    Dictionary<string, Dictionary<string, JsonElement>>? Overrides,
    IReadOnlyList<StepChangeTargetRequest>? Targets = null,
    IReadOnlyDictionary<string, JsonElement>? Inputs = null,
    ReelBolt.Shared.Briefs.RunBrief? Brief = null);

/// <summary>One target of a multi-target change request: the same fields as a single-target body.</summary>
public sealed record StepChangeTargetRequest(
    int? StepOrder,
    Guid? StepResultId,
    string? Instruction,
    IReadOnlyList<string>? ItemIds,
    Dictionary<string, Dictionary<string, JsonElement>>? Overrides);

/// <summary>One target of a stored change request. <paramref name="Kind"/> is <c>step</c>, <c>film</c> (the film render, changed through the reviser) or <c>inputs</c> (a step re-made because an app field it uses changed; its instruction says which).</summary>
public sealed record StepChangeTargetResponse(
    int StepOrder,
    string StepLabel,
    string StepType,
    IReadOnlyList<string> ItemIds,
    string Instruction,
    string Kind);

/// <summary>What a change request is expected to cost, in whole credits (same shape as the revision panel's).</summary>
public sealed record StepChangeEstimateResponse(
    bool BillingEnabled,
    double LowCredits,
    double HighCredits,
    double RequiredCredits,
    double? BalanceCredits,
    bool Affordable,
    double? TargetExactCredits = null,
    string? TargetExactNote = null);

/// <summary>One step of the replay a change request runs: what is redone, reused or skipped.</summary>
/// <param name="Disposition">Reuse | Revise | Run | KeepIfCompatible | Skip.</param>
public sealed record StepChangeReplayStepResponse(int StepOrder, string Label, string Disposition, string Reason);

/// <summary>What the replay of a change request does, step by step.</summary>
public sealed record StepChangeReplayResponse(IReadOnlyList<StepChangeReplayStepResponse> Steps);

/// <summary>A change request: its row, its own run's status, and (when just made) its price and replay.</summary>
public sealed record StepChangeRequestResponse(
    Guid Id,
    Guid ExecutionId,
    Guid WorkflowDefinitionId,
    Guid SourceExecutionId,
    int TargetStepOrder,
    Guid? TargetStepResultId,
    string StepType,
    string Instruction,
    IReadOnlyList<string> ItemIds,
    string Status,
    double? CostCredits,
    DateTime CreatedAt,
    StepChangeEstimateResponse? Estimate = null,
    StepChangeReplayResponse? Replay = null,
    StepChangePlanSummaryResponse? Plan = null,
    Guid? SourceWorkflowDefinitionId = null,
    IReadOnlyList<StepChangeTargetResponse>? Targets = null,
    StepChangeFilmBaseResponse? FilmBase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StepChangeInputChangeResponse>? InputChanges = null);

/// <summary>
/// One app field an app-input change request changes (docs/design/app-inputs.md 25): its label and type, the value
/// before and after in words (a choice by its label, a switch by its on/off words, a model by its name), and the new
/// value as sent to the run (null when the field is cleared).
/// </summary>
public sealed record StepChangeInputChangeResponse(string InputId, string Label, string Type, string Before, string After, JsonElement? Value);

/// <summary>
/// What a finished app run accepts as an app-input change (docs/design/app-inputs.md 25): the app's own fields as the run
/// used them. <paramref name="WorkflowId"/> is the app the run was made from (for the model options route).
/// </summary>
public sealed record StepChangeAppInputsResponse(
    Guid? WorkflowId,
    IReadOnlyList<StepChangeAppInputFieldResponse> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StepChangeBriefFieldResponse>? BriefFields = null);

/// <summary>
/// One run setting the run's app page shows (docs/design/app-inputs.md 29): a brief field, the notes or a files slot, with
/// the run's value and whether a change request can change it.
/// </summary>
/// <param name="Key">The line key: <c>brief:language</c>, <c>brief:notes</c>, <c>brief:files:Video</c>.</param>
/// <param name="Kind"><c>brief</c>, <c>notes</c> or <c>files</c>, as on the page.</param>
/// <param name="Field">The brief field (kind <c>brief</c>), else null.</param>
/// <param name="Role">The files role as on the wire (<c>Video</c>), else null.</param>
/// <param name="Value">The run's value in the body's own shape (a language code, seconds, a voice object, file ids, the notes).</param>
/// <param name="ReasonCode">Why not: <c>BRIEF_FIELD_NOT_CHANGEABLE</c> or <c>BRIEF_FIELD_NOT_USED</c>.</param>
/// <param name="Input">The page's own entry (labels of the time), for the form.</param>
public sealed record StepChangeBriefFieldResponse(
    string Key,
    string Kind,
    string? Field,
    string? Role,
    string Label,
    JsonElement? Value,
    string ValueText,
    bool Supported,
    string? NotSupportedReason,
    string? ReasonCode,
    IReadOnlyList<string> UsedBy,
    AppManifestInput Input);

/// <summary>One field of <see cref="StepChangeAppInputsResponse"/>.</summary>
/// <param name="Value">The run's value (null when it had none).</param>
/// <param name="ValueText">The run's value in words.</param>
/// <param name="Supported">False when changing it cannot be done as a change (nothing on the run's path uses it, it picks a branch, or the app changed since).</param>
/// <param name="ReasonCode">Why not: <c>APP_INPUT_NOT_USED</c>, <c>INPUT_CHANGES_BRANCH</c> or <c>APP_INPUTS_STALE</c>.</param>
/// <param name="UsedBy">The labels of the steps on the run's path that use it (what a change re-makes, before what depends on them).</param>
/// <param name="DecidesBranch">True when a condition reads it: some values pick another branch and are refused.</param>
/// <param name="BranchChangingValues">The values that would pick another branch (empty when none; null when they cannot be listed).</param>
/// <param name="Input">The field as the run's own app page declared it (labels of the time), for the form.</param>
public sealed record StepChangeAppInputFieldResponse(
    string InputId,
    string Label,
    string Type,
    JsonElement? Value,
    string ValueText,
    bool Supported,
    string? NotSupportedReason,
    string? ReasonCode,
    IReadOnlyList<string> UsedBy,
    bool DecidesBranch,
    IReadOnlyList<JsonElement>? BranchChangingValues,
    AppManifestInput Input);

/// <summary>
/// The film version a change request with the film as a target builds on: the newest version of the video, as a
/// composition revision does. Version numbers only, never ids.
/// </summary>
/// <param name="VersionNumber">The version the film change starts from.</param>
/// <param name="RunVersionNumber">The version the source run itself made; null when unknown.</param>
/// <param name="NewerThanRun">True when the film was changed after this run, so the change builds on that newer version.</param>
/// <param name="Label">One plain sentence saying which version it builds on.</param>
public sealed record StepChangeFilmBaseResponse(int VersionNumber, int? RunVersionNumber, bool NewerThanRun, string Label);

/// <summary>How many steps a request redoes, reuses and skips.</summary>
public sealed record StepChangePlanSummaryResponse(int Redone, int Reused, int Skipped);

/// <summary>The requests made against a run (it as the source, or as the request's own run).</summary>
public sealed record StepChangeRequestListResponse(IReadOnlyList<StepChangeRequestResponse> Requests);

/// <summary>The price and replay of a change request, without writing or queuing anything.</summary>
public sealed record StepChangeEstimatePreviewResponse(
    StepChangeEstimateResponse? Estimate, StepChangeReplayResponse Replay, IReadOnlyList<StepChangeTargetResponse>? Targets = null,
    StepChangeFilmBaseResponse? FilmBase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StepChangeInputChangeResponse>? InputChanges = null);

/// <summary>One changeable item of a step, from its stored output.</summary>
public sealed record StepChangeItemResponse(string Id, string? Status, double? DurationSec, string? Text);

/// <summary>One entry of a step type's closed override catalogue.</summary>
/// <param name="Type">The <see cref="ReelBolt.Shared.Workflows.ChangeRequests.ChangeOverrideType"/> name: <c>Text</c>, <c>Number</c>, <c>PronunciationList</c>, <c>Flag</c> or <c>Enum</c>.</param>
/// <param name="Values">The accepted names of an <c>Enum</c> key; null for every other type.</param>
/// <param name="MultiLine">True for a <c>Text</c> key that may carry line breaks (at most <c>MaxEntries</c> lines): the form draws a text area and sends the text as given.</param>
public sealed record StepChangeOverrideResponse(string Key, string Type, double? Min, double? Max, int? MaxLength, int? MaxEntries, IReadOnlyList<string>? Values = null, bool MultiLine = false);

/// <summary>What a finished step result accepts as a change request.</summary>
/// <param name="Route">Set when the change is made through another route (<c>composition-revision</c> for the film step).</param>
/// <param name="Combinable">True for the film step: it can be one target of a multi-target request (never alone).</param>
/// <param name="AllowsInstructionOnly">True when an instruction naming at least one item is a valid request by itself (the script by scene); the form then needs no override.</param>
/// <param name="CostNote">A standing cost warning key the form and the card show; <c>script</c> = everything after the script is made again. Null for most steps.</param>
public sealed record StepChangeStepCapabilityResponse(
    int StepOrder,
    Guid StepResultId,
    string StepType,
    string Label,
    bool Supported,
    string? NotSupportedReason,
    string? Route,
    string ItemKind,
    IReadOnlyList<StepChangeItemResponse> Items,
    IReadOnlyList<StepChangeOverrideResponse> Overrides,
    string? CostNote = null,
    bool AllowsInstructionOnly = false,
    bool Combinable = false);

/// <summary>Per step result of a run: whether and how it can be changed.</summary>
public sealed record StepChangeCapabilitiesResponse(
    Guid ExecutionId,
    bool RunFinished,
    IReadOnlyList<StepChangeStepCapabilityResponse> Steps,
    int MaxTargets = StepChangeLimits.MaxTargets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StepChangeAppInputsResponse? AppInputs = null);

/// <summary>Limits of a multi-target change request.</summary>
public static class StepChangeLimits
{
    /// <summary>Most targets one request may carry.</summary>
    public const int MaxTargets = 6;
}
