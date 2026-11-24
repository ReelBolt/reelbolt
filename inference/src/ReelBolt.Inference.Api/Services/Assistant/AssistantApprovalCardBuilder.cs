using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>What the gate shows the user for one action, before it runs.</summary>
public sealed record ApprovalCard(
    string Kind,
    string Risk,
    string Title,
    string? Summary = null,
    IReadOnlyList<AssistantActionChange>? Changes = null,
    string? ProjectName = null,
    string? Cost = null,
    Guid? ProjectId = null,
    Guid? WorkflowId = null,
    Guid? DocumentId = null,
    double? CostCredits = null,
    bool CostUnestimated = false,
    // Appended: this action allows sending the customer's media to AI services for one run. Such a card is never
    // auto-approved (auto mode and a woken turn included), and approving it is the consent.
    bool AllowsMediaEgress = false,
    // Appended: a statement the card shows verbatim, which the person confirms by allowing the action (an object
    // avatar's object statement). Such a card is never auto-approved, and approving it is the tick.
    ApprovalStatement? Statement = null,
    // Appended: the step orders of a workflow edit whose footage-sharing switch this card lists as turned on; the gate hands
    // them to the call so Apply grants nothing the card did not list.
    IReadOnlyList<int>? GrantedStepOrders = null);

/// <summary>What a <c>Propose*</c> call returned, remembered so the matching <c>Apply*</c> can describe it.</summary>
/// <param name="ToolName">The proposing tool.</param>
/// <param name="Json">The proposal's result as a JSON object (a cloned element, safe to keep).</param>
public sealed record ProposalRecord(string ToolName, JsonElement Json);

/// <summary>
/// What the gate does with one gated call: ask the user (<see cref="Card"/>), answer a question in the tool's
/// place (<see cref="Answer"/>), or refuse it (<see cref="Refusal"/>). In the last two the tool does not run.
/// </summary>
/// <remarks>
/// There is deliberately no "no card, so run it" outcome. A builder that disagreed with the tool about which
/// target a call meant used to skip the card and let the tool act; and "the tool will only ask a question" is
/// a database read the tool repeats later, so a concurrent edit could turn it false. Now every call either
/// gets a card, or the gate answers or refuses it itself and the tool is never invoked.
/// </remarks>
public sealed record ApprovalVerdict(ApprovalCard? Card, string? Answer, string? Refusal)
{
    public static ApprovalVerdict Ask(ApprovalCard card) => new(card, null, null);

    /// <summary>
    /// The call has nothing to approve yet — it can only ask the model a question (e.g. "reuse or regenerate
    /// the paid clips?"). <paramref name="resultJson"/> is what the model is told; the tool is NOT run.
    /// </summary>
    public static ApprovalVerdict AnswerWith(string resultJson) => new(null, resultJson, null);

    /// <summary>The call names something the caller cannot reach; nothing runs.</summary>
    public static ApprovalVerdict Unreachable(string message = "not found") => new(null, null, message);
}

/// <summary>
/// Writes the words on a permission card. Everything the user reads is built HERE, from names read out of the
/// database and from the very arguments the tool will receive — never from the model's prose. A card that
/// says "Delete the project “Launch Teaser”" must be true. A name is looked up through the same access checks
/// the tool will make, so a card can never reveal something the caller cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <b>The card and the tool read ONE argument dictionary, by exact name.</b> The interceptor hands both the
/// same canonical copy (only the function's own parameter names survive), and <see cref="ToolArgs"/> matches
/// keys ordinally exactly as the function binder does. A differently-cased key therefore means nothing to
/// either of them — it can never make the card describe one action while the tool performs another.
/// </para>
/// <para>
/// A project is resolved at <c>View</c>, the weakest permission, so a call the builder cannot place is one the
/// tool would refuse too; the gate refuses it without running the tool (<see cref="ApprovalVerdict.Unreachable"/>).
/// </para>
/// </remarks>
public sealed partial class AssistantApprovalCardBuilder
{
    private const int MaxQuoted = 80;
    private const int MaxChanges = 12;
    private const int MaxCopyPrompts = 40;
    private const int MaxManyChanges = 40;
    private const int MaxRequestText = 400;

    private static readonly Regex GuidPattern = new(
        @"\s*[\(\[]?\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b[\)\]]?",
        RegexOptions.Compiled);

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _user;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IWorkflowExecutionPreflight _preflight;
    private readonly IGraphicDesignService? _graphics;
    private readonly IPlanGateService? _planGate;
    private readonly IOutputDescriber? _outputs;
    private readonly ICompositionRevisionService? _revisions;
    private readonly IStepChangeRequestService? _stepChanges;
    private readonly IDesignBuildEstimateGate _designBuildGate;
    private readonly long _importMaxBytes;
    private readonly IFileStorageService? _fileStorage;
    private readonly IObjectAnalysisService? _objectAnalysis;
    private readonly AudioCleanupService? _audioCleanup;
    private readonly ReelBolt.Inference.Api.Services.Voices.ProjectVoiceLibraryService? _voiceLibrary;

    private readonly AppInputValueValidator _appInputs;
    private readonly ReelBolt.Inference.Api.Services.History.IProjectHistoryService? _history;
    private readonly ReelBolt.Inference.Api.Services.History.IProjectHistoryRestoreService? _historyRestore;
    private readonly IVideoEnhanceService? _videoEnhance;
    private readonly ReelBolt.Inference.Api.Services.SocialPublishing.ISocialOutputResolver? _socialOutputs;
    private readonly IConfirmationTokenService? _confirmationTokens;

    /// <summary>The avatar tools, whose own Prepare* methods the avatar cards are built from; null where avatars are not wired.</summary>
    private readonly AssistantAvatarTools? _avatarTools;

    private readonly IOptionsSnapshot<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceOptions>? _marketplace;

    public AssistantApprovalCardBuilder(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        ICurrentUser user,
        IWorkflowCostEstimator? costEstimator = null,
        IWorkflowExecutionPreflight? preflight = null,
        IGraphicDesignService? graphics = null,
        IPlanGateService? planGate = null,
        IOutputDescriber? outputs = null,
        ICompositionRevisionService? revisions = null,
        IDesignBuildEstimateGate? designBuildGate = null,
        IOptions<UrlImportOptions>? importOptions = null,
        IFileStorageService? fileStorage = null,
        IStepChangeRequestService? stepChanges = null,
        IObjectAnalysisService? objectAnalysis = null,
        AudioCleanupService? audioCleanup = null,
        ReelBolt.Inference.Api.Services.Voices.ProjectVoiceLibraryService? voiceLibrary = null,
        AppInputValueValidator? appInputs = null,
        IVideoEnhanceService? videoEnhance = null,
        IOptionsSnapshot<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceOptions>? marketplace = null,
        ReelBolt.Inference.Api.Services.History.IProjectHistoryService? history = null,
        ReelBolt.Inference.Api.Services.History.IProjectHistoryRestoreService? historyRestore = null,
        ReelBolt.Inference.Api.Services.SocialPublishing.ISocialOutputResolver? socialOutputs = null,
        IConfirmationTokenService? confirmationTokens = null,
        AssistantAvatarTools? avatarTools = null)
    {
        _avatarTools = avatarTools;
        _confirmationTokens = confirmationTokens;
        _socialOutputs = socialOutputs;
        _history = history;
        _historyRestore = historyRestore;
        // docs/design/app-inputs.md 6.2: the card refuses exactly what the tool refuses; the fallback is off.
        _marketplace = marketplace;
        _appInputs = appInputs ?? new AppInputValueValidator(db);
        _voiceLibrary = voiceLibrary;
        _videoEnhance = videoEnhance;
        _objectAnalysis = objectAnalysis;
        _audioCleanup = audioCleanup;
        _fileStorage = fileStorage;
        _stepChanges = stepChanges;
        _importMaxBytes = importOptions?.Value.MaxBytes ?? new UrlImportOptions().MaxBytes;
        _outputs = outputs;
        _revisions = revisions;
        _designBuildGate = designBuildGate ?? new DesignBuildEstimateGate(db, costEstimator, null);
        _db = db;
        _projectAccess = projectAccess;
        _user = user;
        _costEstimator = costEstimator;
        _preflight = preflight ?? new WorkflowExecutionPreflight(db, costEstimator);
        _graphics = graphics;
        _planGate = planGate;
    }

    /// <summary>
    /// The card the gate asks with when <see cref="BuildAsync"/> threw (SEC-EF-10). A charged action
    /// (<c>ExecuteWorkflow</c> and every catalog entry whose risk is Paid) stays Paid and says its cost could
    /// not be worked out, unless the cost estimator says billing is off (the same rule the built cards use);
    /// every other tool gets the catalog's generic card.
    /// </summary>
    public ApprovalCard FallbackCard(string toolName)
    {
        ApprovalToolInfo info = AssistantApprovalCatalog.Describe(toolName);
        bool isRun = string.Equals(toolName, AssistantApprovalCatalog.ExecuteWorkflowTool, StringComparison.Ordinal);
        bool charged = isRun || info.Risk == AssistantActionRisk.Paid;
        if (!charged)
            return new ApprovalCard(info.Kind, info.Risk, info.FallbackTitle);

        if (_costEstimator is { BillingEnabled: false })
            return new ApprovalCard(info.Kind, AssistantActionRisk.Normal, info.FallbackTitle);

        return new ApprovalCard(
            info.Kind, AssistantActionRisk.Paid, info.FallbackTitle,
            Cost: isRun ? CreditCostWording.RunCostUnknown : CreditCostWording.CostUnknown,
            CostUnestimated: true);
    }

    public async Task<ApprovalVerdict> BuildAsync(
        string tool,
        AIFunctionArguments args,
        AssistantToolContext context,
        ProposalRecord? proposal,
        CancellationToken ct)
    {
        ApprovalToolInfo info = AssistantApprovalCatalog.Describe(tool);

        return tool switch
        {
            "ApplyWorkflow" => await WorkflowEditAsync(info, args, context, proposal, ct),
            AssistantApprovalCatalog.ExecuteWorkflowTool => await RunAsync(info, args, context, ct),
            "ApplyCreateProject" => CreateProject(info, args),
            "ApplyUpdateProject" => await UpdateProjectAsync(info, args, context, ct),
            "ApplySavePronunciation" => await SavePronunciationAsync(info, args, context, ct),
            "ApplyAddSoundEffect" => await AddSoundEffectAsync(info, args, context, ct),
            "ApplyDeleteProject" => await DeleteProjectAsync(info, args, context, ct),
            "ApplyWriteProjectFile" => await WriteFileAsync(info, args, context, proposal, ct),
            "ApplyEditProjectFile" or "ApplyInsertProjectFileLines" => await TargetedFileChangeAsync(tool, info, args, context, ct),
            "ApplyDeleteProjectFile" => await DeleteFileAsync(info, args, context, ct),
            "ApplyDeleteWorkflow" => await DeleteWorkflowAsync(info, args, context, ct),
            "ApplyStopExecution" => await StopExecutionAsync(info, args, context, ct),
            "ApplySaveStyle" => await SaveStyleAsync(info, args, context, ct),
            "ApplyAddTextLayer" or "ApplyEditPictureLayers" or "ApplyDeletePictureLayers"
                or "ApplyChangePictureCanvas" or "ApplyRunPictureTool" or "ApplyDesignGraphic"
                => await PictureAsync(tool, info, args, context, proposal, ct),
            "ApplyCreateInferenceProvider" => await ProviderCreateAsync(info, args, ct),
            "ApplyUpdateInferenceProvider" => await ProviderUpdateAsync(info, args, ct),
            "ApplyDeleteInferenceProvider" => await ProviderDeleteAsync(info, args, ct),
            "ApplyAssignAgentSkills" => await AgentSkillsAsync(info, args, ct),
            "ApplySaveOutputAsFile" => await SaveOutputAsync(info, args, context, ct),
            "ApplyUploadFile" => await UploadFileAsync(info, args, context, ct),
            "ApplyImportFileFromUrl" => await ImportFileAsync(info, args, context, ct),
            "ApplyReviseComposition" => await ReviseAsync(info, args, context, ct),
            "ApplyStepChange" => await StepChangeAsync(info, args, context, ct),
            "ApplyPatchWorkflow" => await PatchWorkflowAsync(info, args, context, proposal, ct),
            "ApplyCloneWorkflow" => await CloneWorkflowAsync(info, args, context, proposal, ct),
            "ApplyCreatePictureDocument" => await CreatePictureDocumentAsync(info, args, context, proposal, ct),
            "ApplyGreenScreenInsert" => await GreenScreenAsync(info, args, context, proposal, ct),
            "ApplyObjectEffect" => await ObjectEffectAsync(info, args, context, ct),
            "ApplyEnhanceVideo" => await VideoEnhanceAsync(info, args, context, ct),
            "ApplySetMediaOverlay" => await MediaOverlayAsync(info, args, context, ct),
            "ApplyCleanAudio" => await CleanAudioAsync(info, args, context, ct),
            "ApplyAddLibraryVoice" => await AddLibraryVoiceAsync(info, args, context, ct),
            "ApplyCreateFromTemplate" => await CreateFromTemplateAsync(info, args, context, proposal, ct),
            "ApplyCopyMarketplaceRecipe" => await CopyMarketplaceRecipeAsync(info, args, context, ct),
            "ApplySaveDesignSystem" => await SaveDesignSystemAsync(info, args, context, ct),
            "ApplyDecideDesignChange" => await DecideDesignChangeAsync(info, args, context, ct),
            "ApplySaveProjectVersion" => await SaveVersionAsync(info, args, context, ct),
            "ApplyRestoreProjectVersion" => await RestoreVersionAsync(info, args, context, ct),
            "ApplySchedulePost" => await SchedulePostAsync(info, args, context, ct),
            "ApplyCancelScheduledPost" => await CancelScheduledPostAsync(info, args, ct),
            "ApplyCreateObjectAvatar" or "ApplyExtractObjectFromVideo" or "ApplyCreateAvatarVariant"
                or "ApplyImportLibraryAvatar" or "ApplyGenerateSubjectClip" => await AvatarCardAsync(tool, info, args, context, ct),
            _ => ApprovalVerdict.Ask(new ApprovalCard(info.Kind, info.Risk, info.FallbackTitle))
        };
    }

    // ------------------------------------------------------------------ outputs, uploads, revisions

    private async Task<ApprovalVerdict> SaveOutputAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? outputId = ToolArgs.GetGuid(args, "outputId");
        if (project is null || outputId is null || _outputs is null)
            return ApprovalVerdict.Unreachable("render not found");

        OutputRender? render = await _outputs.FindRenderAsync(project.Id, outputId.Value, ct);
        if (render is null)
            return ApprovalVerdict.Unreachable("render not found");

        // The path the tool will really write, by the tool's own rules.
        if (!AssistantOutputTools.TryPrepareTarget(
                render, ToolArgs.GetString(args, "fileName"), ToolArgs.GetString(args, "directoryPath"),
                out AssistantOutputTools.SaveTarget? target, out _))
        {
            return ApprovalVerdict.Unreachable("invalid file path");
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, info.FallbackTitle,
            Summary: $"Copy {Quote(render.FileName)} ({FormatBytes(render.SizeBytes)}) into project {Quote(project.Name)} as {Quote(target!.Path)}. "
                + "The copy counts toward your workspace storage; the original render is not changed.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    private async Task<ApprovalVerdict> UploadFileAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        // The ticket names its project and the tool acts on THAT one (a call naming another is refused), so the
        // card resolves the ticket's project, never the page's.
        if (!AssistantUploadTools.TryUnpackUploadToken(ToolArgs.GetString(args, "uploadToken"), out DirectUploadTicketEcho? ticket)
            || ticket is null
            || (ToolArgs.GetGuid(args, "projectId") is { } named && named != Guid.Empty && named != ticket.ProjectId))
        {
            return ApprovalVerdict.Unreachable("upload not found");
        }

        Project? project = (await _projectAccess.GetAsync(ticket.ProjectId, ProjectPermission.View, ct)).Project;
        if (project is null)
            return ApprovalVerdict.Unreachable("upload not found");

        string? directory = ticket.RelativePath is { Length: > 0 } relative && relative.LastIndexOf('/') is var slash and > 0
            ? relative[..slash]
            : null;
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, info.FallbackTitle,
            Summary: $"Add {Short(ticket.FileName, MaxQuoted)} ({FormatBytes(ticket.SizeBytes)}) to {(directory is null ? "the project root" : Short(directory, MaxQuoted))} "
                + $"in {Short(project.Name, MaxQuoted)}. It counts toward your storage.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    private async Task<ApprovalVerdict> ImportFileAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        if (UrlImportTarget.ValidateUrl(ToolArgs.GetString(args, "url"), out Uri? uri) is not null
            || UrlImportTarget.ResolveTarget(uri!, ToolArgs.GetString(args, "fileName"), ToolArgs.GetString(args, "directoryPath"), out UrlImportPath? target) is not null)
        {
            return ApprovalVerdict.Unreachable("link not usable");
        }

        // The host only — a path or query can carry a signed secret and is never shown.
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, info.FallbackTitle,
            Summary: $"Download a file from {UrlImportTarget.Origin(uri!)} into {Short(target!.OriginalPath, MaxQuoted)} in {Short(project.Name, MaxQuoted)} "
                + $"(up to {FormatBytes(_importMaxBytes)}; counts toward your storage).",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>
    /// The frame ranges exactly as ApplyReviseComposition binds them (S-16): <see cref="AIFunctionFactory"/> reads a
    /// tool argument with <see cref="AIJsonUtilities.DefaultOptions"/> (web defaults: case-insensitive names, numbers
    /// as strings), so the card does too and never lists fewer ranges than the tool applies. <c>null</c> when the value
    /// does not bind, in which case the tool call fails and nothing is applied.
    /// </summary>
    private static RevisionFrameRangeArg[]? RevisionFrameRanges(AIFunctionArguments args)
    {
        if (ToolArgs.Get(args, "frameRanges") is not { ValueKind: JsonValueKind.Array } array)
            return [];

        try
        {
            return array.Deserialize<RevisionFrameRangeArg[]>(AIJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string FrameRangesLine(RevisionFrameRangeArg[] ranges)
    {
        string listed = string.Join(", ", ranges.Take(CompositionRevisionTargets.MaxFrameRanges).Select(r => $"{r.FromFrame}–{r.ToFrame}"));
        int rest = ranges.Length - CompositionRevisionTargets.MaxFrameRanges;
        return rest > 0 ? $"Only these frames: {listed} and {rest} more" : $"Only these frames: {listed}";
    }

    /// <summary>
    /// A credits figure in the same unit the required and balance figures are printed with (one decimal below 10,
    /// whole from 10, invariant culture). QA-P0-18: a shortfall is not a range end, so it is never rounded to the
    /// nearest whole credit — 15.1 credits reads "15.1", and the three figures on the card add up exactly.
    /// </summary>
    private static string Shown(double credits) => credits.ToString("0.#", CultureInfo.InvariantCulture);

    private async Task<ApprovalVerdict> ReviseAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? executionId = ToolArgs.GetGuid(args, "executionId");
        string? instruction = ToolArgs.GetString(args, "instruction");
        if (project is null || executionId is null || _revisions is null || string.IsNullOrWhiteSpace(instruction))
            return ApprovalVerdict.Unreachable("video not found");

        // The very history call the proposal made, so the figures on the card are the proposal's figures.
        CompositionRevisionHistoryResponse? history = await _revisions.GetHistoryAsync(project, executionId.Value, _user.OrganizationId, ct, ToolArgs.GetGuid(args, "fromVersionId"));
        if (history is null || !history.CanRevise)
            return ApprovalVerdict.Unreachable("this video cannot be changed");

        string workflowName = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.ProjectId == project.Id)
            .Join(_db.WorkflowDefinitions, e => e.WorkflowDefinitionId, w => w.Id, (_, w) => w.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        string[] scenes = (ToolArgs.GetStrings(args, "scenes") ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Short(s, MaxQuoted)).Distinct().ToArray();
        bool hasRanges = ToolArgs.Get(args, "frameRanges") is { ValueKind: JsonValueKind.Array } ranges && ranges.GetArrayLength() > 0;
        string where = scenes.Length > 0
            ? $"{(scenes.Length == 1 ? "the scene" : "the scenes")} {string.Join(", ", scenes.Select(s => Quote(s)))}"
            : hasRanges ? "the chosen parts" : "the whole film";

        string policy = ToolArgs.GetString(args, "cutPolicy") is { Length: > 0 } given
            && Enum.TryParse(given.Trim(), ignoreCase: true, out CompositionCutPolicy parsed) && Enum.IsDefined(parsed)
                ? parsed.ToString()
                : AssistantRevisionTools.DefaultCutPolicy;
        RevisionCostEstimateResponse? estimate = AssistantRevisionTools.PickEstimate(history, policy);

        var changes = new List<AssistantActionChange>();
        string? cost = null;
        double? costCredits = null;
        bool unestimated = false;
        bool paid = false;
        if (estimate is { BillingEnabled: true })
        {
            paid = true;
            costCredits = estimate.RequiredCredits;
            cost = CreditCostWording.RunNeeds(estimate.LowCredits, estimate.HighCredits, estimate.RequiredCredits, 0, 0) ?? CreditCostWording.RunCostUnknown;
            if (!estimate.Affordable)
            {
                changes.Add(new(AssistantChangeKind.Info, estimate.BalanceCredits is { } balance
                    ? $"You don't have enough credits to start this: it needs up to {CreditCostWording.Up(estimate.RequiredCredits)} available and you have {CreditShortfallMessage.Down(balance)}, "
                        + $"so {Shown(CreditShortfallMessage.MissingCreditsFromCredits(estimate.RequiredCredits, balance))} more are needed."
                    : "You don't have enough credits to start this."));
            }
        }
        else if (estimate is null && _costEstimator is { BillingEnabled: true })
        {
            // Billing is on but no estimate could be worked out: the run still charges and counts as paid.
            paid = true;
            unestimated = true;
            cost = "Cost unavailable — counted as a paid run";
        }

        // Everything the user is approving that the summary does not already say.
        if (instruction.Length > MaxRequestText)
        {
            changes.Add(new(AssistantChangeKind.Info,
                $"The instruction is shown shortened: the first {MaxRequestText} of {instruction.Length} characters; all {instruction.Length} will be applied."));
        }

        if (RevisionFrameRanges(args) is { Length: > 0 } frameRanges)
            changes.Add(new(AssistantChangeKind.Info, FrameRangesLine(frameRanges)));

        if (ToolArgs.GetGuid(args, "fromVersionId") is { } fromVersionId && fromVersionId != (history.HeadVersionId ?? history.CurrentVersionId))
        {
            int? number = history.Versions.Where(v => v.Id == fromVersionId).Select(v => (int?)v.VersionNumber).FirstOrDefault();
            changes.Add(new(AssistantChangeKind.Info, number is { } n ? $"Starts from version {n}, not the latest" : "Starts from an earlier version, not the latest"));
        }

        if (!policy.Equals(AssistantRevisionTools.DefaultCutPolicy, StringComparison.Ordinal))
            changes.Add(new(AssistantChangeKind.Info, "Re-edit the video too: the editing decisions are made again, not kept"));

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, paid ? AssistantActionRisk.Paid : AssistantActionRisk.Normal, info.FallbackTitle,
            Summary: $"Apply {Quote(Short(instruction, MaxRequestText), MaxRequestText)} to {where} of {Quote(workflowName)} in {Quote(project.Name)}. "
                + "This starts a new run and counts as one of your paid runs this hour.",
            Changes: changes.Count == 0 ? null : changes, ProjectName: project.Name, Cost: cost, ProjectId: project.Id,
            CostCredits: costCredits, CostUnestimated: unestimated));
    }

    private async Task<ApprovalVerdict> StepChangeAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? executionId = ToolArgs.GetGuid(args, "executionId");
        if (project is null || executionId is null || _stepChanges is null)
            return ApprovalVerdict.Unreachable("run not found");

        // docs/design/app-inputs.md 25: new values for an app run's own fields.
        // docs/design/app-inputs.md 29: run settings (`brief`) too, and either may come with `changes` (one card listing both).
        JsonElement? rawInputs = ToolArgs.Get(args, "inputs") is { ValueKind: JsonValueKind.Object } inputsArg ? inputsArg : null;
        JsonElement? rawBrief = ToolArgs.Get(args, "brief") is { ValueKind: JsonValueKind.Object } briefArg ? briefArg : null;
        if (rawInputs is not null || rawBrief is not null)
        {
            if (HasSingleStepArgs(args))
                return ApprovalVerdict.Unreachable("name the steps to change under changes when the app's fields or run settings change too");
            return await StepInputsAsync(info, rawInputs, rawBrief, ToolArgs.Get(args, "changes"), project, executionId.Value, ct);
        }

        if (ToolArgs.Get(args, "changes") is { } rawChanges)
        {
            // Apply refuses a call that carries both shapes, so no card is built for one: the user must not
            // approve something that cannot run.
            if (HasSingleStepArgs(args))
                return ApprovalVerdict.Unreachable("send either one step or several changes, not both");
            return await StepChangesAsync(info, rawChanges, project, executionId.Value, ct);
        }

        int? stepOrder = ToolArgs.Get(args, "stepOrder") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out int order) ? order : null;
        Guid? stepResultId = ToolArgs.GetGuid(args, "stepResultId");
        string? instruction = ToolArgs.GetString(args, "instruction");
        string[] itemIds = ToolArgs.GetStrings(args, "itemIds") ?? [];
        Dictionary<string, Dictionary<string, JsonElement>>? overrides = null;
        if (ToolArgs.Get(args, "overrides") is { ValueKind: JsonValueKind.Object } raw)
        {
            try
            {
                overrides = raw.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>();
            }
            catch (JsonException)
            {
                return ApprovalVerdict.Unreachable("the settings to change could not be read");
            }
        }

        CreateStepChangeRequest body = new(stepOrder, stepResultId, instruction, itemIds.Length > 0 ? itemIds : null, overrides);

        // The very calls the proposal made, so the card's step, items and figures are the proposal's.
        StepChangeCapabilitiesResponse? caps = await _stepChanges.GetCapabilitiesAsync(project, executionId.Value, ct);
        StepChangeStepCapabilityResponse? step = caps?.Steps.FirstOrDefault(s =>
            stepResultId is not null ? s.StepResultId == stepResultId : s.StepOrder == stepOrder);
        if (step is null || !step.Supported)
            return ApprovalVerdict.Unreachable("this step cannot be changed");

        StepChangeRequestResult? result = await _stepChanges.EstimateAsync(project, executionId.Value, _user.OrganizationId, body, ct);
        if (result?.Preview is not { } preview)
            return ApprovalVerdict.Unreachable(string.IsNullOrWhiteSpace(result?.Message) ? "this change cannot be made" : result.Message);

        var changes = new List<AssistantActionChange>();
        // At most a few item lines, so the new values and the "redone / reused" line always fit on the card.
        string[] shownIds = [.. itemIds.Distinct(StringComparer.Ordinal)];
        foreach (string id in shownIds.Take(4))
        {
            string? text = step.Items.FirstOrDefault(i => i.Id == id)?.Text;
            changes.Add(new(AssistantChangeKind.Info, text is { Length: > 0 } ? $"Item: {Quote(Short(text, MaxQuoted))}" : "Item: one of this step's items"));
        }
        if (shownIds.Length > 4)
            changes.Add(new(AssistantChangeKind.Info, $"…and {shownIds.Length - 4} more items."));

        if (!string.IsNullOrWhiteSpace(instruction))
            changes.Add(new(AssistantChangeKind.Info, $"Change: {Quote(Short(instruction.Trim(), MaxRequestText), MaxRequestText)}"));

        if (overrides is { Count: > 0 })
        {
            string[] valueLines = [.. AssistantChangeRequestTools.DescribeOverrides(
                overrides, id => step.Items.FirstOrDefault(i => i.Id == id)?.Text is { Length: > 0 } t ? Quote(Short(t, MaxQuoted)) : "One item")];
            foreach (string line in valueLines.Take(5))
                changes.Add(new(AssistantChangeKind.Info, line));
            if (valueLines.Length > 5)
                changes.Add(new(AssistantChangeKind.Info, $"…and changes to {valueLines.Length - 5} more items."));
        }

        string[] redo = preview.Replay.Steps.Where(s => s.Disposition is "Run" or "Revise").Select(s => s.Label).Distinct().ToArray();
        string[] reuse = preview.Replay.Steps.Where(s => s.Disposition is "Reuse" or "KeepIfCompatible").Select(s => s.Label).Distinct().ToArray();
        changes.Add(new(AssistantChangeKind.Info,
            $"Redone: {(redo.Length > 0 ? string.Join(", ", redo.Select(r => Short(r, MaxQuoted))) : "this step")}. "
            + (reuse.Length > 0 ? $"Reused at no cost: {string.Join(", ", reuse.Select(r => Short(r, MaxQuoted)))}." : "Nothing is reused.")));

        // Keyed on the capability's cost note (the scriptwriter agent); the old step-label match stays as the fallback.
        if (string.Equals(step.CostNote, ReelBolt.Shared.Workflows.ChangeRequests.StepChangeCapabilities.ScriptCostNote, StringComparison.Ordinal)
            || (string.Equals(step.StepType, nameof(StepType.Agent), StringComparison.OrdinalIgnoreCase)
                && step.Label.Contains("script", StringComparison.OrdinalIgnoreCase)))
            changes.Add(new(AssistantChangeKind.Info, "Changing the script is the most expensive option: everything after it is made again."));

        string? cost = null;
        double? costCredits = null;
        bool unestimated = false;
        bool paid = false;
        StepChangeEstimateResponse? estimate = preview.Estimate;
        if (estimate is { BillingEnabled: true })
        {
            paid = true;
            costCredits = estimate.RequiredCredits;
            cost = CreditCostWording.RunNeeds(estimate.LowCredits, estimate.HighCredits, estimate.RequiredCredits, 0, 0) ?? CreditCostWording.RunCostUnknown;
            if (estimate.TargetExactCredits is { } exactCredits)
                changes.Add(new(AssistantChangeKind.Info, $"This part costs exactly about {CreditCostWording.Up(exactCredits)} credits."));
            if (!estimate.Affordable)
            {
                changes.Add(new(AssistantChangeKind.Info, estimate.BalanceCredits is { } balance
                    ? $"You don't have enough credits to start this: it needs up to {CreditCostWording.Up(estimate.RequiredCredits)} available and you have {CreditShortfallMessage.Down(balance)}, "
                        + $"so {Shown(CreditShortfallMessage.MissingCreditsFromCredits(estimate.RequiredCredits, balance))} more are needed."
                    : "You don't have enough credits to start this."));
            }
        }
        else if (estimate is null && _costEstimator is { BillingEnabled: true })
        {
            paid = true;
            unestimated = true; // billing on, no figure: auto mode fails closed
            cost = "Cost unavailable — counted as a paid run";
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, paid ? AssistantActionRisk.Paid : AssistantActionRisk.Normal, info.FallbackTitle,
            Summary: $"Change the step {Quote(Short(step.Label, MaxQuoted))} ({step.StepType}) of a finished run in {Quote(project.Name)}. "
                + "This starts a new run and counts as one of your paid runs this hour.",
            Changes: changes.Count == 0 ? null : changes.Take(MaxChanges).ToList(), ProjectName: project.Name, Cost: cost, ProjectId: project.Id,
            CostCredits: costCredits, CostUnestimated: unestimated));
    }

    /// <summary>
    /// The card of an app-input change (docs/design/app-inputs.md 25): each changed field by its label with the value before
    /// and after (a choice by its label, a switch by its words, a model by its name), what is redone and reused, and the
    /// price, all from the service's own estimate of these exact values.
    /// </summary>
    private async Task<ApprovalVerdict> StepInputsAsync(
        ApprovalToolInfo info, JsonElement? rawInputs, JsonElement? rawBrief, JsonElement? rawChanges, Project project, Guid executionId,
        CancellationToken ct)
    {
        Dictionary<string, JsonElement>? inputs = rawInputs?.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        ReelBolt.Shared.Briefs.RunBrief? brief = null;
        StepChangeTargetRequest[]? targets = null;
        try
        {
            brief = rawBrief?.Deserialize<ReelBolt.Shared.Briefs.RunBrief>(ReelBolt.Shared.Briefs.RunBriefJson.Options);
            if (rawChanges is { } changesArg)
            {
                targets = changesArg.ValueKind == JsonValueKind.Array
                    ? changesArg.Deserialize<StepChangeTargetRequest[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    : null;
                if (targets is not { Length: > 0 } || targets.Any(t => t is null))
                    return ApprovalVerdict.Unreachable("the changes could not be read");
            }
        }
        catch (JsonException)
        {
            return ApprovalVerdict.Unreachable("the changes could not be read");
        }

        StepChangeRequestResult? result = await _stepChanges!.EstimateAsync(
            project, executionId, _user.OrganizationId, new CreateStepChangeRequest(null, null, null, null, null, targets, inputs, brief), ct);
        if (result?.Preview is not { } preview || (preview.InputChanges is not { Count: > 0 } && targets is null))
            return ApprovalVerdict.Unreachable(string.IsNullOrWhiteSpace(result?.Message) ? "this change cannot be made" : result.Message);
        IReadOnlyList<StepChangeInputChangeResponse> lines = preview.InputChanges ?? [];

        var changes = new List<AssistantActionChange>();
        foreach (StepChangeInputChangeResponse line in lines.Take(6))
        {
            changes.Add(new(AssistantChangeKind.Info,
                $"{Quote(Short(line.Label, MaxQuoted))}: {Quote(Short(line.Before, MaxQuoted))} → {Quote(Short(line.After, MaxQuoted))}"));
        }
        if (lines.Count > 6)
            changes.Add(new(AssistantChangeKind.Info, $"…and {lines.Count - 6} more fields."));

        // The step changes of the same request (docs/design/app-inputs.md 29): each step by its label, with its note.
        foreach (StepChangeTargetResponse target in (preview.Targets ?? []).Where(t => t.Kind != "inputs"))
        {
            changes.Add(new(AssistantChangeKind.Info, string.IsNullOrWhiteSpace(target.Instruction)
                ? $"Step {Quote(Short(target.StepLabel, MaxQuoted))}"
                : $"Step {Quote(Short(target.StepLabel, MaxQuoted))}: {Quote(Short(target.Instruction.Trim(), MaxRequestText), MaxRequestText)}"));
        }

        string[] redo = preview.Replay.Steps.Where(s => s.Disposition is "Run" or "Revise").Select(s => s.Label).Distinct().ToArray();
        string[] reuse = preview.Replay.Steps.Where(s => s.Disposition is "Reuse" or "KeepIfCompatible").Select(s => s.Label).Distinct().ToArray();
        changes.Add(new(AssistantChangeKind.Info,
            $"Redone: {(redo.Length > 0 ? string.Join(", ", redo.Select(r => Short(r, MaxQuoted))) : "the steps that use these fields")}. "
            + (reuse.Length > 0 ? $"Reused at no cost: {string.Join(", ", reuse.Select(r => Short(r, MaxQuoted)))}." : "Nothing is reused.")));

        string? cost = null;
        double? costCredits = null;
        bool unestimated = false;
        bool paid = false;
        StepChangeEstimateResponse? estimate = preview.Estimate;
        if (estimate is { BillingEnabled: true })
        {
            paid = true;
            costCredits = estimate.RequiredCredits;
            cost = CreditCostWording.RunNeeds(estimate.LowCredits, estimate.HighCredits, estimate.RequiredCredits, 0, 0) ?? CreditCostWording.RunCostUnknown;
            if (!estimate.Affordable)
            {
                changes.Add(new(AssistantChangeKind.Info, estimate.BalanceCredits is { } balance
                    ? $"You don't have enough credits to start this: it needs up to {CreditCostWording.Up(estimate.RequiredCredits)} available and you have {CreditShortfallMessage.Down(balance)}, "
                        + $"so {Shown(CreditShortfallMessage.MissingCreditsFromCredits(estimate.RequiredCredits, balance))} more are needed."
                    : "You don't have enough credits to start this."));
            }
        }
        else if (estimate is null && _costEstimator is { BillingEnabled: true })
        {
            paid = true;
            unestimated = true;
            cost = "Cost unavailable — counted as a paid run";
        }

        IEnumerable<string> named = lines.Select(l => Quote(Short(l.Label, MaxQuoted)))
            .Concat((preview.Targets ?? []).Where(t => t.Kind != "inputs").Select(t => "the step " + Quote(Short(t.StepLabel, MaxQuoted))));
        string fields = string.Join(", ", named);
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, paid ? AssistantActionRisk.Paid : AssistantActionRisk.Normal, info.FallbackTitle,
            Summary: $"Change {fields} of a finished app run in {Quote(project.Name)} and make it again. "
                + "This starts a new run and counts as one of your paid runs this hour.",
            Changes: changes.Take(MaxChanges).ToList(), ProjectName: project.Name, Cost: cost, ProjectId: project.Id,
            CostCredits: costCredits, CostUnestimated: unestimated));
    }

    private static bool HasSingleStepArgs(AIFunctionArguments args) =>
        (ToolArgs.Get(args, "stepOrder") is { ValueKind: JsonValueKind.Number })
        || ToolArgs.GetGuid(args, "stepResultId") is not null
        || ToolArgs.GetStrings(args, "itemIds") is { Length: > 0 }
        || !string.IsNullOrWhiteSpace(ToolArgs.GetString(args, "instruction"))
        || ToolArgs.Get(args, "overrides") is { ValueKind: JsonValueKind.Object };

    /// <summary>The film-base line of a change-request card, in the revision card's "Starts from version N" wording.</summary>
    internal static string FilmBaseLine(StepChangeFilmBaseResponse filmBase) =>
        filmBase.NewerThanRun && filmBase.RunVersionNumber is { } runNumber
            ? $"The video starts from version {filmBase.VersionNumber}, the latest, not version {runNumber} this run made"
            : $"The video starts from version {filmBase.VersionNumber}, the version this run made";

    private async Task<ApprovalVerdict> StepChangesAsync(
        ApprovalToolInfo info, JsonElement rawChanges, Project project, Guid executionId, CancellationToken ct)
    {
        StepChangeTargetRequest[]? targets;
        try
        {
            targets = rawChanges.ValueKind == JsonValueKind.Array
                ? rawChanges.Deserialize<StepChangeTargetRequest[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;
        }
        catch (JsonException)
        {
            return ApprovalVerdict.Unreachable("the changes could not be read");
        }

        if (targets is not { Length: > 0 } || targets.Any(t => t is null))
            return ApprovalVerdict.Unreachable("the changes could not be read");

        CreateStepChangeRequest body = new(null, null, null, null, null, targets);
        StepChangeCapabilitiesResponse? caps = await _stepChanges!.GetCapabilitiesAsync(project, executionId, ct);
        StepChangeRequestResult? result = await _stepChanges.EstimateAsync(project, executionId, _user.OrganizationId, body, ct);
        if (result?.Preview is not { } preview)
            return ApprovalVerdict.Unreachable(string.IsNullOrWhiteSpace(result?.Message) ? "this change cannot be made" : result.Message);

        var changes = new List<AssistantActionChange>();
        var labels = new List<string>();
        foreach (StepChangeTargetRequest t in targets.OrderBy(t => t.StepOrder ?? int.MaxValue))
        {
            StepChangeStepCapabilityResponse? step = caps?.Steps.FirstOrDefault(s =>
                t.StepResultId is not null ? s.StepResultId == t.StepResultId : s.StepOrder == t.StepOrder);
            if (step is null || !(step.Supported || step.Combinable))
                return ApprovalVerdict.Unreachable("one of these steps cannot be changed");

            labels.Add(Short(step.Label, MaxQuoted));
            // The headline (step and every item in plain words) is never cut; a long note is capped with a visible ellipsis
            // plus a line saying it is shown shortened, so the user still knows the whole note is used.
            string[] ids = [.. (t.ItemIds ?? []).Distinct(StringComparer.Ordinal)];
            string itemWords = ids.Length == 0
                ? string.Empty
                : " - " + string.Join("; ", ids.Select(id =>
                    step.Items.FirstOrDefault(i => i.Id == id)?.Text is { Length: > 0 } tx ? Quote(Short(tx, 40)) : "one of its items"));
            changes.Add(new(AssistantChangeKind.Info, $"Step {Quote(Short(step.Label, MaxQuoted))}{itemWords}"));
            if (!string.IsNullOrWhiteSpace(t.Instruction))
            {
                string note = t.Instruction.Trim();
                string shownNote = Short(note, MaxRequestText);
                changes.Add(new(AssistantChangeKind.Info, $"Change: {Quote(shownNote, MaxRequestText)}"));
                if (shownNote.Length < note.Length)
                    changes.Add(new(AssistantChangeKind.Info, "The note is shown shortened; the whole note is used."));
            }
            if (t.Overrides is { Count: > 0 })
            {
                string[] lines = [.. AssistantChangeRequestTools.DescribeOverrides(
                    t.Overrides, id => step.Items.FirstOrDefault(i => i.Id == id)?.Text is { Length: > 0 } tx ? Quote(Short(tx, MaxQuoted)) : "One item")];
                foreach (string line in lines.Take(2))
                    changes.Add(new(AssistantChangeKind.Info, line));
                if (lines.Length > 2)
                    changes.Add(new(AssistantChangeKind.Info, $"…and changes to {lines.Length - 2} more items of this step."));
            }
        }

        string[] redo = preview.Replay.Steps.Where(s => s.Disposition is "Run" or "Revise").Select(s => s.Label).Distinct().ToArray();
        string[] reuse = preview.Replay.Steps.Where(s => s.Disposition is "Reuse" or "KeepIfCompatible").Select(s => s.Label).Distinct().ToArray();
        string redoneLine = $"Redone: {(redo.Length > 0 ? string.Join(", ", redo.Select(r => Short(r, MaxQuoted))) : "these steps")}. "
            + (reuse.Length > 0 ? $"Reused at no cost: {string.Join(", ", reuse.Select(r => Short(r, MaxQuoted)))}." : "Nothing is reused.");

        string? cost = null;
        double? costCredits = null;
        bool unestimated = false;
        bool paid = false;
        StepChangeEstimateResponse? estimate = preview.Estimate;
        if (estimate is { BillingEnabled: true })
        {
            paid = true;
            costCredits = estimate.RequiredCredits;
            cost = CreditCostWording.RunNeeds(estimate.LowCredits, estimate.HighCredits, estimate.RequiredCredits, 0, 0) ?? CreditCostWording.RunCostUnknown;
            if (estimate.TargetExactCredits is { } exactCredits)
                changes.Add(new(AssistantChangeKind.Info, $"These changes cost exactly about {CreditCostWording.Up(exactCredits)} credits."));
            if (!estimate.Affordable)
            {
                changes.Add(new(AssistantChangeKind.Info, estimate.BalanceCredits is { } balance
                    ? $"You don't have enough credits to start this: it needs up to {CreditCostWording.Up(estimate.RequiredCredits)} available and you have {CreditShortfallMessage.Down(balance)}, "
                        + $"so {Shown(CreditShortfallMessage.MissingCreditsFromCredits(estimate.RequiredCredits, balance))} more are needed."
                    : "You don't have enough credits to start this."));
            }
        }
        else if (estimate is null && _costEstimator is { BillingEnabled: true })
        {
            paid = true;
            unestimated = true;
            cost = "Cost unavailable — counted as a paid run";
        }

        // Which film version the change builds on (the newest, as a revision does), by number, never by id.
        if (preview.FilmBase is { } filmBase)
            changes.Add(new(AssistantChangeKind.Info, FilmBaseLine(filmBase)));

        // Nothing is ever dropped: if the card cannot show every change plus the cost lines, no card is built and the Apply is refused.
        changes.Add(new(AssistantChangeKind.Info, redoneLine));
        if (changes.Count > MaxManyChanges)
            return ApprovalVerdict.Unreachable("these changes are too large to show in one approval");
        List<AssistantActionChange> shown = changes;
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, paid ? AssistantActionRisk.Paid : AssistantActionRisk.Normal, info.FallbackTitle,
            Summary: $"Change several steps of a finished run in {Quote(project.Name)}: {string.Join(", ", labels)}. "
                + "This starts one new run and counts as one of your paid runs this hour.",
            Changes: shown, ProjectName: project.Name, Cost: cost, ProjectId: project.Id,
            CostCredits: costCredits, CostUnestimated: unestimated));
    }

    // ------------------------------------------------------------------ workflows

    // ------------------------------------------------------------------ patches, clones, picture documents, green screen

    private async Task<ApprovalVerdict> PatchWorkflowAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? workflowId = ToolArgs.GetGuid(args, "workflowId") ?? context.WorkflowId;
        if (project is null || workflowId is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        string? name = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == workflowId && w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);
        if (name is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        var changes = new List<AssistantActionChange>();
        if (proposal is not null)
            AddNormalizationLines(changes, proposal.Json);
        // The app page first (docs/design/app-inputs.md 13.3): the sentences the tool built from the stored page and the
        // steps' labels. The step lines below still show every setting the patch writes, the bindings included.
        changes.AddRange(AppChangeLines(proposal));

        if (proposal is not null && Property(proposal.Json, "changedSteps") is { ValueKind: JsonValueKind.Array } steps)
        {
            foreach (JsonElement step in steps.EnumerateArray())
            {
                int order = Property(step, "stepOrder") is { ValueKind: JsonValueKind.Number } o && o.TryGetInt32(out int value) ? value : 0;

                // A removal is already the Remove line from the normalizer's report; an added step gets its own.
                if (Property(step, "removed") is { ValueKind: JsonValueKind.True })
                    continue;
                if (Property(step, "added") is { ValueKind: JsonValueKind.True })
                {
                    changes.Add(new(AssistantChangeKind.Add, StepText(new StepRef(
                        order,
                        Property(step, "label") is { ValueKind: JsonValueKind.String } l ? l.GetString() : null,
                        Property(step, "stepType") is { ValueKind: JsonValueKind.String } t ? t.GetString() : null))));
                    continue;
                }

                if (Property(step, "fields") is not { ValueKind: JsonValueKind.Array } fields)
                    continue;

                // A step above a removed loop keeps its identity but moves up one; the card says so.
                string stepText = Property(step, "was") is { ValueKind: JsonValueKind.Number } w && w.TryGetInt32(out int was)
                    ? $"Step {was} → {order}"
                    : $"Step {order}";
                foreach (JsonElement field in fields.EnumerateArray())
                {
                    string label = Property(field, "field") is { ValueKind: JsonValueKind.String } f ? f.GetString() ?? "" : "";
                    changes.Add(new(AssistantChangeKind.Change,
                        $"{stepText}: {Short(label, 80)}: {ValueText(Property(field, "before"))} → {ValueText(Property(field, "after"))}"));
                }
            }
        }
        else
        {
            changes.AddRange(PatchOperationLines(ToolArgs.Get(args, "operations")));
        }

        changes.AddRange(await AvatarLinesAsync(project.Id,
            [.. AvatarIdsIn(ToolArgs.Get(args, "operations")), .. AvatarIdsIn(proposal?.Json)], ct));

        IReadOnlyList<int>? grantedOrders = AddEgressGrantLines(changes, proposal);

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Change the workflow {Quote(name)}",
            Summary: changes.Count == 0 ? "No settings would change." : null,
            Changes: Cap(changes), ProjectName: project.Name, ProjectId: project.Id, WorkflowId: workflowId,
            AllowsMediaEgress: grantedOrders is not null, GrantedStepOrders: grantedOrders));
    }

    /// <summary>
    /// The review-loop normalizations the proposal applied: a removed loop is a removal the user approves, and a
    /// retarget says where the review now runs. Read from the proposal's <c>normalization</c> array; a proposal
    /// from before that array existed has none and reads exactly as it used to.
    /// </summary>
    private static void AddNormalizationLines(List<AssistantActionChange> changes, JsonElement proposal)
    {
        if (Property(proposal, "normalization") is not { ValueKind: JsonValueKind.Array } entries)
            return;

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string? change = Property(entry, "change") is { ValueKind: JsonValueKind.String } c ? c.GetString() : null;
            int order = Property(entry, "stepOrder") is { ValueKind: JsonValueKind.Number } o && o.TryGetInt32(out int value) ? value : 0;
            string? label = Property(entry, "label") is { ValueKind: JsonValueKind.String } l ? l.GetString() : null;

            if (string.Equals(change, "removed", StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new(AssistantChangeKind.Remove,
                    $"Step {order}: remove {Quote(label ?? "the review")} — a review there would not change anything on a repeat; the steps after it move up one"));
                continue;
            }

            if (!string.Equals(change, "retargeted", StringComparison.OrdinalIgnoreCase))
                continue;

            int? from = IntProperty(entry, "fromStepOrder");
            int? to = IntProperty(entry, "toStepOrder");
            if (to is { } toOrder)
                changes.Add(new(AssistantChangeKind.Change, from is { } fromOrder
                    ? $"Step {order}: the review goes back to step {toOrder} instead of step {fromOrder}"
                    : $"Step {order}: the review goes back to step {toOrder}"));
        }
    }

    /// <summary>An integer property of a JSON object, null when absent or not a number.</summary>
    private static int? IntProperty(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out int value) ? value : null;

    /// <summary>What the patch was asked to do, step by step, when no stored proposal can show the exact old and new values.</summary>
    private static IEnumerable<AssistantActionChange> PatchOperationLines(JsonElement? operations)
    {
        if (operations is not { ValueKind: JsonValueKind.Array } list)
            yield break;

        foreach (JsonElement operation in list.EnumerateArray())
        {
            int order = Property(operation, "stepOrder") is { ValueKind: JsonValueKind.Number } o && o.TryGetInt32(out int value) ? value : 0;
            // Matched case-insensitively, exactly as the tool matches it, so a card never says "change" for an op the tool runs.
            string? op = Property(operation, "op") is { ValueKind: JsonValueKind.String } p ? p.GetString()?.ToLowerInvariant() : null;
            switch (op)
            {
                case "renamestep":
                    string? label = Property(operation, "label") is { ValueKind: JsonValueKind.String } l ? l.GetString() : null;
                    yield return new(AssistantChangeKind.Change, $"Step {order}: rename to {Quote(label ?? "")}");
                    break;
                case "setstepconfig":
                    if (Property(operation, "patch") is { ValueKind: JsonValueKind.Object } patch)
                    {
                        List<JsonProperty> keys = patch.EnumerateObject().ToList();
                        string values = string.Join(", ", keys.Take(6).Select(k => $"{Short(k.Name, 40)} to {ValueText(k.Value)}"));
                        if (keys.Count > 6)
                            values += $" and {keys.Count - 6} more";
                        yield return new(AssistantChangeKind.Change, $"Step {order}: set {values}");
                    }
                    else
                    {
                        yield return new(AssistantChangeKind.Change, $"Step {order}: change settings");
                    }

                    break;
                case "setstepfield":
                    string? field = Property(operation, "field") is { ValueKind: JsonValueKind.String } f ? f.GetString() : null;
                    yield return new(AssistantChangeKind.Change, $"Step {order}: set {Short(field ?? "a setting", 40)} to {ValueText(Property(operation, "value"))}");
                    break;
                // The app-page operations (docs/design/app-inputs.md 13.3), by the field's label where the call gives one.
                case "setappinput":
                    string? inputLabel = Property(operation, "input") is { ValueKind: JsonValueKind.Object } input
                        && Property(input, "label") is { ValueKind: JsonValueKind.String } il ? il.GetString() : null;
                    yield return new(AssistantChangeKind.Add, inputLabel is null
                        ? "Adds or changes a field of the app page"
                        : $"Adds or changes the field {Quote(inputLabel)} of the app page");
                    break;
                case "removeappinput":
                    yield return new(AssistantChangeKind.Remove, "Removes a field from the app page, and every use of it");
                    break;
                case "bindappinput":
                    string? setting = Property(operation, "field") is { ValueKind: JsonValueKind.String } bf ? bf.GetString() : null;
                    yield return new(AssistantChangeKind.Change, $"Step {order}: {Short(setting ?? "a setting", 40)} reads a field of the app page");
                    break;
                case "setapppage":
                    yield return new(AssistantChangeKind.Change, "Changes the app page (its title, field order or the request sent with each run)");
                    break;
                default:
                    yield return new(AssistantChangeKind.Change, $"Step {order}: change");
                    break;
            }
        }
    }

    /// <summary>
    /// The plain sentences a workflow proposal lists about its app page (<c>appChanges</c>, docs/design/app-inputs.md 13.3):
    /// built by the tool from the stored page, the proposed page and the steps' own labels, never from the model's words.
    /// None for a proposal without them (every proposal while app inputs are off).
    /// </summary>
    private static IEnumerable<AssistantActionChange> AppChangeLines(ProposalRecord? proposal)
    {
        if (proposal is null || Property(proposal.Json, "appChanges") is not { ValueKind: JsonValueKind.Array } lines)
            yield break;
        foreach (JsonElement line in lines.EnumerateArray().Take(AssistantAppInputs.MaxLines))
        {
            if (line.ValueKind != JsonValueKind.String || line.GetString() is not { Length: > 0 } text)
                continue;
            string kind = text.StartsWith("Removes", StringComparison.Ordinal) || text.StartsWith("Stops", StringComparison.Ordinal)
                ? AssistantChangeKind.Remove
                : text.StartsWith("Adds", StringComparison.Ordinal) ? AssistantChangeKind.Add : AssistantChangeKind.Change;
            yield return new(kind, Short(text, 300));
        }
    }

    private static string ValueText(JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "none",
        { ValueKind: JsonValueKind.String } s => Short(s.GetString() ?? "", 60),
        { } other => Short(other.GetRawText(), 60)
    };

    private async Task<ApprovalVerdict> CloneWorkflowAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        Project? source = await ResolveProjectAsync(args, context, ct);
        Guid? workflowId = ToolArgs.GetGuid(args, "workflowId") ?? context.WorkflowId;
        if (source is null || workflowId is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        string? workflowName = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == workflowId && w.ProjectId == source.Id && w.Kind == WorkflowDefinitionKind.Standard)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);
        if (workflowName is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        Project target = source;
        if (ToolArgs.GetGuid(args, "targetProjectId") is { } targetId && targetId != Guid.Empty && targetId != source.Id)
        {
            Project? found = (await _projectAccess.GetAsync(targetId, ProjectPermission.View, ct)).Project;
            if (found is null)
                return ApprovalVerdict.Unreachable("project not found");
            target = found;
        }

        string copyName = ToolArgs.GetString(args, "name") is { Length: > 0 } given && !string.IsNullOrWhiteSpace(given)
            ? given
            : proposal is not null && Property(proposal.Json, "name") is { ValueKind: JsonValueKind.String } proposed && proposed.GetString() is { Length: > 0 } fromProposal
                ? fromProposal
                : $"{workflowName} (copy)";
        string where = target.Id == source.Id
            ? $"in {Quote(source.Name)}"
            : $"from {Quote(source.Name)} into {Quote(target.Name)}";

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, info.FallbackTitle,
            Summary: $"Copy {Quote(workflowName)} {where} as {Quote(copyName)}.",
            ProjectName: target.Name, ProjectId: target.Id));
    }

    private async Task<ApprovalVerdict> CreatePictureDocumentAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        string? imageName = proposal is not null && Property(proposal.Json, "imageName") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null;
        string image = string.IsNullOrWhiteSpace(imageName) ? "an image" : Quote(imageName);
        string named = ToolArgs.GetString(args, "name") is { } given && !string.IsNullOrWhiteSpace(given) ? $" named {Quote(given)}" : "";

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, info.FallbackTitle,
            Summary: $"Open {image} in the thumbnail studio as a new picture{named} in {Quote(project.Name)}. It is free.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>
    /// The object-effect card: the object, the effect (and "everything except the object"), the video, the timeline and how many
    /// clips, and the price from the same estimate the proposal read. Built from the database and the exact arguments, by name.
    /// A call that names no timeline, video or clips, or whose estimate cannot be read, gets no card and does not run.
    /// </summary>
    private async Task<ApprovalVerdict> ObjectEffectAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null || _objectAnalysis is null)
            return ApprovalVerdict.Unreachable("clips not found");

        AssistantObjectEffectTools.TargetResult resolved = await AssistantObjectEffectTools.ResolveTargetAsync(
            _db, project.Id,
            ToolArgs.GetString(args, "objectDescription"), ToolArgs.GetString(args, "effect"),
            ToolArgs.GetString(args, "timelineName"), ToolArgs.GetString(args, "fileName"),
            ToolArgs.Get(args, "strength") is { ValueKind: JsonValueKind.Number } s ? s.GetDouble() : null,
            ToolArgs.GetBool(args, "everythingExceptObject"), ct);
        if (resolved.Target is not { } target)
            return ApprovalVerdict.Unreachable("clips not found");

        ObjectAnalysisResult<ObjectAnalysisEstimate> result = await _objectAnalysis.EstimateAsync(
            new ObjectAnalysisCaller(_user.UserId, project.OrganizationId, project, _user.EmailVerified),
            AssistantObjectEffectTools.MediaRef(target), new ObjectAnalysisEstimateRequest(target.Prompt), ct);
        if (result.Value is not { } estimate)
            return ApprovalVerdict.Unreachable("the object search cannot be priced");

        string effectWord = target.Effect.ToLowerInvariant();
        string where = target.Invert
            ? $"Add a {effectWord} to everything except {Quote(target.Prompt)}"
            : $"Add a {effectWord} to {Quote(target.Prompt)}";
        string clips = AssistantObjectEffectTools.Clips(target.ItemIds.Count);
        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Add, $"A {effectWord}{(target.Invert ? " on everything except the object" : " on the object")}: {Quote(target.Prompt)}"),
            new(AssistantChangeKind.Info, $"{clips} of {Quote(target.FileName)} on the timeline {Quote(target.TimelineName)}"),
            new(AssistantChangeKind.Info, "It shows up on the timeline when the search finishes, usually within a few minutes. Nothing is changed if the object is not found."),
        };

        string? cost = null;
        double? credits = null;
        bool paid = false;
        if (estimate.Cached)
        {
            cost = "Free: the same search was already done";
        }
        else if (estimate.BillingEnabled)
        {
            paid = true;
            credits = estimate.Credits;
            cost = estimate.UpperBound ? $"Up to {CreditCostWording.Up(estimate.Credits)} credits" : $"About {CreditCostWording.Up(estimate.Credits)} credits";
            if (!estimate.Affordable)
                changes.Add(new(AssistantChangeKind.Info, "You don't have enough credits to start this."));
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, paid ? AssistantActionRisk.Paid : AssistantActionRisk.Normal, info.FallbackTitle,
            Summary: $"{where} in {Quote(target.FileName)} on the timeline {Quote(target.TimelineName)} ({clips}) in {Quote(project.Name)}. "
                + (cost is null ? "Finding the object is free." : cost + "."),
            Changes: changes, ProjectName: project.Name, Cost: cost, ProjectId: project.Id, CostCredits: credits));
    }

    /// <summary>
    /// The video-enhance card: the operation in plain words, the source by name, its seconds and the output size and rate, and
    /// the price, all from the same <see cref="IVideoEnhanceService"/> estimate the proposal read and from the exact
    /// arguments. A hosted operation with no recorded consent gets no card: the gate answers that the customer must agree in
    /// the editor and the tool never runs.
    /// </summary>
    private async Task<ApprovalVerdict> VideoEnhanceAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        if (_videoEnhance is null)
            return ApprovalVerdict.Unreachable("video enhancement is not available");

        AssistantVideoEnhanceTools.Prepared prepared = await AssistantVideoEnhanceTools.PrepareAsync(
            _db, _projectAccess, _videoEnhance, _user, context,
            ToolArgs.GetString(args, "op"), ToolArgs.GetString(args, "engine"),
            ToolArgs.GetGuid(args, "fileId"), ToolArgs.GetGuid(args, "outputId"),
            IntArg(args, "upscaleFactor"), IntArg(args, "targetHeight"), IntArg(args, "frameMultiplier"),
            ToolArgs.GetString(args, "denoiseStrength"), ToolArgs.GetBool(args, "keepGrain"),
            IntArg(args, "shutterAngle"), ToolArgs.GetGuid(args, "projectId"), ct,
            new AssistantVideoEnhanceTools.ExtendArgs(
                ToolArgs.GetString(args, "targetAspect"), DoubleArg(args, "marginLeft"), DoubleArg(args, "marginRight"),
                DoubleArg(args, "marginTop"), DoubleArg(args, "marginBottom"), ToolArgs.GetString(args, "prompt")));
        if (prepared.Error is not null)
        {
            return string.Equals(CodeOf(prepared.Error), AssistantVideoEnhanceTools.NeedsCustomerOkCode, StringComparison.Ordinal)
                ? ApprovalVerdict.AnswerWith(prepared.Error)
                : ApprovalVerdict.Unreachable(MessageOf(prepared.Error));
        }

        AssistantVideoEnhanceTools.Facts facts = prepared.Facts!;
        VideoEnhanceEstimate estimate = facts.Estimate;
        string what = AssistantVideoEnhanceTools.DescribeOp(
            facts.Op, facts.Body.UpscaleFactor, facts.Body.TargetHeight, facts.Body.FrameMultiplier, facts.Body);
        string source = AssistantVideoEnhanceTools.Quote(facts.SourceName);
        string projectName = facts.Project.Name;

        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Add, $"{char.ToUpperInvariant(what[0])}{what[1..]} of {source}"),
            new(AssistantChangeKind.Info, estimate.Seconds > 0 ? $"{estimate.Seconds} s of video" : "The video's length is not known yet"),
        };
        if (estimate.OutputWidth > 0 && estimate.OutputHeight > 0)
            changes.Add(new(AssistantChangeKind.Info, $"Output: {estimate.OutputWidth}×{estimate.OutputHeight} at {estimate.OutputFps:0.###} fps"));
        // The customer approves what is SENT: the sanitized Extend prompt (never the raw argument), shortened for the card.
        string cardPrompt = AssistantVideoEnhanceTools.CardSafe(facts.Body.Prompt ?? string.Empty);
        if (cardPrompt.Length > 0)
        {
            string shown = cardPrompt.Length > 120 ? cardPrompt[..120].TrimEnd() + "…" : cardPrompt;
            changes.Add(new(AssistantChangeKind.Info, $"What the new edges should show: \u201c{shown}\u201d"));
        }

        string? cost;
        double? credits = null;
        // A hosted operation is Paid-risk whether or not billing is on: the video leaves the platform either way, and the
        // risk badge is the user's cue that this is not a local, free edit.
        bool paid = estimate.Hosted;
        if (estimate.Hosted)
        {
            changes.Add(new(AssistantChangeKind.Info,
                "The video is sent to an outside video service for processing (this project already allows it)."));
            if (estimate.BillingEnabled)
            {
                credits = estimate.Credits;
                cost = $"About {CreditCostWording.Up(estimate.Credits)} credits";
                if (!estimate.Affordable)
                    changes.Add(new(AssistantChangeKind.Info, "You don't have enough credits to start this."));
            }
            else
            {
                cost = "Paid";
            }
        }
        else
        {
            cost = "Free";
        }

        string length = estimate.Seconds > 0 ? $"{estimate.Seconds} s" : "of unknown length";
        string output = estimate.OutputWidth > 0 && estimate.OutputHeight > 0
            ? $" The output is {estimate.OutputWidth}×{estimate.OutputHeight} at {estimate.OutputFps:0.###} fps."
            : string.Empty;
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, paid ? AssistantActionRisk.Paid : AssistantActionRisk.Normal, info.FallbackTitle,
            Summary: $"{char.ToUpperInvariant(what[0])}{what[1..]} of {source} in {AssistantVideoEnhanceTools.Quote(projectName)}: {length}.{output} "
                + (estimate.Hosted ? cost + "." : "Free."),
            Changes: changes, ProjectName: projectName, Cost: cost, ProjectId: facts.Project.Id, CostCredits: credits));
    }

    private static double? DoubleArg(AIFunctionArguments args, string name) =>
        ToolArgs.Get(args, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double value) ? value : null;

    private static int? IntArg(AIFunctionArguments args, string name) =>
        ToolArgs.Get(args, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out int value) ? value : null;

    private static string? CodeOf(string errorJson) => PropertyOf(errorJson, "code");

    private static string MessageOf(string errorJson) => PropertyOf(errorJson, "error") ?? "this video cannot be enhanced";

    private static string? PropertyOf(string json, string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ApprovalVerdict> GreenScreenAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? plateId = ToolArgs.GetGuid(args, "plateFileId");
        if (project is null || plateId is null)
            return ApprovalVerdict.Unreachable("clip not found");

        string? plateName = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.Id == plateId && f.ProjectId == project.Id)
            .Select(f => f.OriginalFileName)
            .FirstOrDefaultAsync(ct);
        if (plateName is null)
            return ApprovalVerdict.Unreachable("clip not found");

        // Built from the database, exactly as the tool decides it (never from the remembered proposal): the requested
        // name, or the first numbered one, and whether that is a new workflow or one this tool made whose steps are replaced.
        _ = proposal;
        string requested = ToolArgs.GetString(args, "name")?.Trim() is { Length: > 0 } given
            ? given
            : WorkflowTemplateCatalog.BuildVersionedWorkflowName(WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.GreenScreenInsertTemplateKey)!);
        if (await AssistantGreenScreenTools.ResolveTargetAsync(_db, project.Id, requested, ct) is not { } target)
            return ApprovalVerdict.Unreachable("no free workflow name");

        const string Tail = "It uses no AI model; only processing time is charged. The clip needs a screen or panel filled with one flat colour, such as green or blue, that stays in view and is mostly uncovered. This does not start a run.";
        if (target.Existing is not null)
        {
            return ApprovalVerdict.Ask(new ApprovalCard(
                info.Kind, AssistantActionRisk.Destructive, $"Replace the steps of the existing workflow {Quote(target.WorkflowName)}",
                Summary: $"Replace the steps of the workflow {Quote(target.WorkflowName)} in {Quote(project.Name)} with the green-screen insert for {Quote(plateName)}; its current steps are removed. " + Tail,
                ProjectName: project.Name, ProjectId: project.Id, WorkflowId: target.Existing.Id));
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, info.FallbackTitle,
            Summary: $"Create the workflow {Quote(target.WorkflowName)} in {Quote(project.Name)} to put your content on the green screen in {Quote(plateName)}. " + Tail,
            ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>
    /// "Clean the audio": the target in plain words, the source's name, its length in minutes and the credits (Free), read
    /// from the database through the same estimate the run uses and from the exact arguments the tool will run with. A
    /// hosted AI target is never run from the assistant (the customer's OK lives in the editor), so the gate answers that in
    /// the tool's place.
    /// </summary>
    private async Task<ApprovalVerdict> CleanAudioAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? fileId = ToolArgs.GetGuid(args, "fileId");
        Guid? outputId = ToolArgs.GetGuid(args, "outputId");
        if (project is null || _audioCleanup is null || (fileId is null) == (outputId is null))
            return ApprovalVerdict.Unreachable("audio not found");

        AudioCleanupRequestBody body = new(ToolArgs.GetString(args, "target"), false, ToolArgs.GetString(args, "outputKind"));
        if (!AudioCleanupService.TryReadBody(body, out ReelBolt.Shared.Workflows.AudioCleanupTarget target, out _, out _))
            return ApprovalVerdict.Unreachable("unknown cleanup");
        if (AudioCleanupService.IsHosted(target))
            return ApprovalVerdict.AnswerWith(JsonSerializer.Serialize(new { error = AssistantAudioCleanupTools.NeedsCustomerOkMessage, code = "NEEDS_CUSTOMER_OK" }));

        string mediaRef = fileId is { } f ? $"file:{f}" : $"step_result:{outputId}";
        AudioCleanupResult<AudioCleanupEstimateResponse> estimate = await _audioCleanup.EstimateAsync(project.Id, mediaRef, body, ct);
        if (estimate.Value is not { } priced)
            return ApprovalVerdict.Unreachable("audio not found");

        string source = fileId is { } id
            ? await _db.ProjectFiles.AsNoTracking().Where(x => x.Id == id && x.ProjectId == project.Id).Select(x => x.OriginalFileName).FirstOrDefaultAsync(ct) ?? "the file"
            : "an earlier render";
        string minutes = priced.Seconds is { } seconds ? $" ({Math.Round(seconds / 60.0, 1).ToString("0.#", CultureInfo.InvariantCulture)} min)" : "";
        string cost = priced.Credits is > 0 ? $"{CreditCostWording.Up(priced.Credits.Value)} credits" : "Free";
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, info.FallbackTitle,
            Summary: $"Clean the audio of {Quote(source)}{minutes} in {Quote(project.Name)}: {AssistantAudioCleanupTools.Describe(target)}. Cost: {cost}. It starts a run; the cleaned file is added to the project's files.",
            ProjectName: project.Name, Cost: cost, ProjectId: project.Id, CostCredits: priced.Credits ?? 0));
    }

    /// <summary>
    /// "Add a library voice": the voice's name as the LIBRARY gives it (looked up again now, never the model's
    /// words), the name it will have in the project, its languages, and the project's name. An id that is not a
    /// public library voice (or a voice cloned on the platform account) gets no card and nothing runs.
    /// </summary>
    private async Task<ApprovalVerdict> AddLibraryVoiceAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        string voiceId = ToolArgs.GetString(args, "vendorVoiceId")?.Trim() ?? string.Empty;
        if (project is null || _voiceLibrary is null || voiceId.Length == 0)
            return ApprovalVerdict.Unreachable("voice not found");

        ReelBolt.Inference.Api.Services.Voices.VoiceLibraryResult<VoiceLibraryItemResponse?> found =
            await _voiceLibrary.FindAsync(project.Id, voiceId, ct);
        if (found.Value is not { } voice)
            return ApprovalVerdict.Unreachable("voice not found in the voice library");

        string? displayName = ToolArgs.GetString(args, "displayName")?.Trim();
        string libraryName = string.IsNullOrWhiteSpace(voice.Name) ? "a library voice" : voice.Name;
        string named = string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, voice.Name, StringComparison.Ordinal)
            ? ""
            : $" It will be called {Quote(displayName)} in the project.";
        string languages = voice.Languages.Count > 0 ? $" Languages: {string.Join(", ", voice.Languages.Take(5))}." : "";
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            $"Add the voice {Quote(libraryName)}",
            Summary: $"Add {Quote(libraryName)} from the voice library to the voices of {Quote(project.Name)}.{named}{languages} Free; no consent is needed for a library voice.",
            ProjectName: project.Name, Cost: "Free", ProjectId: project.Id, CostCredits: 0));
    }

    /// <summary>
    /// "Show this picture on the video": the file's name and kind, the form in plain words, the span, the opacity
    /// when it is below 100 and the sound choice. Built from the database and the exact arguments the tool will
    /// run with, never from ids alone; no run starts.
    /// </summary>
    private async Task<ApprovalVerdict> MediaOverlayAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? mediaId = ToolArgs.GetGuid(args, "mediaFileId");
        Guid? workflowId = ToolArgs.GetGuid(args, "workflowId");
        int stepOrder = ToolArgs.Get(args, "stepOrder") is { ValueKind: JsonValueKind.Number } order && order.TryGetInt32(out int value)
            && value > 0
                ? value
                : 0;
        if (project is null || mediaId is null || workflowId is null || stepOrder == 0)
            return ApprovalVerdict.Unreachable("picture or video not found");

        var media = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.Id == mediaId && f.ProjectId == project.Id &&
                f.CsamStatus != CsamScanStatus.Quarantined && f.ModerationStatus != FileModerationStatus.Quarantined)
            .Select(f => new { f.OriginalFileName, f.MimeType })
            .FirstOrDefaultAsync(ct);
        if (media is null)
            return ApprovalVerdict.Unreachable("picture or video not found");

        var workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == workflowId && w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard)
            .Select(w => new
            {
                w.Name,
                Step = w.Steps.Where(s => s.StepOrder == stepOrder)
                    .Select(s => new { s.Label, s.StepType })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (workflow?.Step is null || workflow.Step.StepType != StepType.VideoCompile)
            return ApprovalVerdict.Unreachable("compile step not found");

        MediaOverlayArg request = new(
            mediaId.Value,
            workflowId.Value,
            stepOrder,
            ToolArgs.GetString(args, "form"),
            ToolArgs.GetString(args, "anchor"),
            ToolArgs.GetString(args, "size"),
            Number(args, "startSec"),
            Number(args, "endSec"),
            ToolArgs.Get(args, "opacity") is { ValueKind: JsonValueKind.Number } opacity && opacity.TryGetInt32(out int percent) ? percent : null,
            ToolArgs.GetString(args, "cardAudio"),
            ToolArgs.GetString(args, "audio"),
            ToolArgs.GetString(args, "audioLevel"));

        if (!AssistantMediaOverlayTools.TryParseChoices(request, out AssistantMediaOverlayTools.Choices choices, out _))
            return ApprovalVerdict.Unreachable("invalid placement");

        // An image has no sound; the summary must not claim the clip's own audio when the applied
        // config will be muted (the compile ignores the inapplicable field).
        if (!media.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            choices = choices with { Audio = MediaOverlayAudio.Muted };

        string kind = media.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "a picture" : "a video";
        string stepLabel = string.IsNullOrWhiteSpace(workflow.Step.Label) ? "the compile step" : Short(workflow.Step.Label!, 80);
        string summary = $"Show {Quote(media.OriginalFileName)} ({kind}) {choices.FormText} in {Quote(workflow.Name)} "
            + $"at step {stepOrder} ({stepLabel}) {choices.SpanText}, {choices.OpacityText}; {choices.SoundText}. "
            + "It saves the change and starts no run.";
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Show {Quote(media.OriginalFileName)} on the video",
            Summary: summary, ProjectName: project.Name, ProjectId: project.Id, WorkflowId: workflowId));
    }

    private static double? Number(AIFunctionArguments args, string name) =>
        ToolArgs.Get(args, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double value)
            ? value
            : null;

    /// <summary>
    /// The create card (docs/design/workflow-apps.md 6.4): the workflow's name and the recipe it comes from, its step
    /// count, the patches applied, the cost class (never vendor prices) and the lint warnings. Built from the catalog and
    /// the arguments the tool will run with; the proposal only adds what the linter found. No run starts.
    /// </summary>
    private async Task<ApprovalVerdict> CreateFromTemplateAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");
        // A hidden avatar key is the recipe that replaced it (docs/design/app-inputs.md 22).
        if (ToolArgs.GetString(args, "templateKey")?.Trim() is not { Length: > 0 } key
            || AssistantRecipeTools.ResolveRecipe(WorkflowTemplateCatalog.GetListed(), key).Template is not { } template)
            return ApprovalVerdict.Unreachable("recipe not found");

        RecipeFitInfo recipe = RecipeFit.Describe(template);
        IReadOnlyList<string> patches = RecipePatches.Normalize(ToolArgs.GetStrings(args, "patches"), out _);
        (RunBrief? brief, string? briefError) = RunBriefValidator.ParseJson(ToolArgs.Get(args, "brief"));
        if (briefError is not null)
            brief = null;

        // The same steps the tool builds (pure: one placeholder id per agent type, mapped back for the cost class).
        Dictionary<AgentType, Guid> ids = Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Guid.NewGuid());
        Dictionary<Guid, AgentType> typeOf = ids.ToDictionary(kv => kv.Value, kv => kv.Key);
        RecipePatchOutcome built = RecipeInstance.Build(template, patches, brief, ids);
        int stepCount = built.Steps?.Count ?? template.Steps.Count;
        IReadOnlyList<AppStepFacts> facts = built.Steps is null
            ? AppStepFacts.FromTemplate(template)
            : [.. built.Steps.Select(s => AppStepFacts.From(
                Enum.TryParse(s.StepType, out StepType stepType) ? stepType : StepType.Agent,
                typeOf.GetValueOrDefault(s.AgentDefinitionId),
                s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson))];

        // The name the workflow WILL get: the requested one (or the recipe title), made unique exactly as the tool does.
        string requested = ToolArgs.GetString(args, "name")?.Trim() is { Length: > 0 } given ? given : recipe.Title;
        string name = await AssistantRecipeTools.ResolveFreeNameAsync(_db, project.Id, requested, ct) ?? requested;

        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Info, $"Recipe: {Quote(recipe.Title)}"),
            new(AssistantChangeKind.Info, $"{stepCount} step{(stepCount == 1 ? "" : "s")}"),
        };
        foreach (string patchId in patches)
            changes.Add(new(AssistantChangeKind.Add, RecipePatches.Catalog.First(p => p.Id == patchId).Summary));
        if (brief is not null)
            changes.AddRange(BriefLines(brief));
        // The app page's fields this recipe asks for (docs/design/app-inputs.md 13.3), by label: the person fills them in each run.
        if (template.AppManifest?.Inputs is { } recipeInputs)
        {
            List<string> fields = [.. recipeInputs
                .Where(i => i.Kind == AppManifestVocabulary.KindInput && !string.IsNullOrWhiteSpace(i.Label))
                .Select(i => Quote(i.Label!))];
            if (fields.Count > 0)
                changes.Add(new(AssistantChangeKind.Info, $"Its app page asks for: {string.Join(", ", fields.Take(8))}{(fields.Count > 8 ? $" and {fields.Count - 8} more" : "")}."));
            if (brief?.Inputs is { Count: > 0 } defaults)
                changes.Add(new(AssistantChangeKind.Info, $"Pre-fills {defaults.Count} of those field{(defaults.Count == 1 ? "" : "s")} with the values you chose."));
        }
        changes.Add(new(AssistantChangeKind.Info, $"Cost when run: {AppCostClass.Derive(facts)}. Creating it costs nothing and starts no run."));
        if (proposal is not null)
        {
            foreach (JsonElement finding in LintMessages(proposal.Json, "warnings").Take(3))
                changes.Add(new(AssistantChangeKind.Info, $"Heads-up: {Short(finding.GetString() ?? string.Empty, 200)}"));
            foreach (JsonElement finding in LintMessages(proposal.Json, "needsBeforeRunning").Take(3))
                changes.Add(new(AssistantChangeKind.Info, $"Needed before running: {Short(finding.GetString() ?? string.Empty, 200)}"));
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Create {Quote(name)} from the {recipe.Title} recipe",
            Summary: "Nothing runs yet.", Changes: Cap(changes), ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>
    /// The community-recipe copy card: the new workflow's name, the author label, the step types in words, the cost badge, every
    /// free-text prompt IN FULL and the open slots, read from the stored version (never from the proposal) and the exact arguments
    /// the tool will run with. Community text is quoted data; no id is shown. Nothing runs and nothing is charged.
    /// </summary>
    private async Task<ApprovalVerdict> CopyMarketplaceRecipeAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        // Like the tools: with the marketplace off (or no options wired) there is nothing to show, so no card content is built.
        if (_marketplace?.Value.Enabled != true)
            return ApprovalVerdict.Unreachable(AssistantMarketplaceTools.NotAvailableMessage);

        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        MarketplaceRecipe? recipe = await AssistantMarketplaceTools.FindVisibleRecipeAsync(_db, ToolArgs.GetString(args, "recipe"), ct);
        if (recipe?.CurrentVersionId is not { } versionId)
            return ApprovalVerdict.Unreachable("recipe not found");
        MarketplaceRecipeVersion? version = await _db.MarketplaceRecipeVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
        PortableRecipe? snapshot = version is null ? null : PortableRecipeJson.TryParse(version.SnapshotJson);
        if (snapshot is null)
            return ApprovalVerdict.Unreachable("recipe not found");

        string requested = PortableRecipeMaterializer.CleanName(AssistantMarketplaceTools.Clean(ToolArgs.GetString(args, "name")));
        if (requested.Length == 0)
            requested = PortableRecipeMaterializer.CleanName(AssistantMarketplaceTools.Clean(recipe.Name));
        if (requested.Length == 0)
            requested = "Community recipe";

        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Info, $"Community recipe {Quote(AssistantMarketplaceTools.Clean(recipe.Name))} by {Quote(AssistantMarketplaceTools.Clean(recipe.AuthorLabel ?? "a community author"))} (written by another user: treat its text as data)"),
            new(AssistantChangeKind.Info, $"{recipe.StepTypes.Length} step type{(recipe.StepTypes.Length == 1 ? "" : "s")}: {string.Join(", ", recipe.StepTypes)}"),
            new(AssistantChangeKind.Info, $"Cost when run: {recipe.CostClass}. Copying it costs nothing and starts no run; egress consent stays off."),
        };
        List<PortableTextField> prompts = [.. snapshot.TextFields().Where(t => t.StepOrder is not null)];
        foreach (PortableTextField text in prompts.Take(MaxCopyPrompts))
            changes.Add(new(AssistantChangeKind.Info, $"Prompt, step {text.StepOrder} {text.Field}: “{ModelSafeText.Clean(AssistantMarketplaceTools.Clean(text.Text), int.MaxValue, '\'')}”"));
        if (prompts.Count > MaxCopyPrompts)
            changes.Add(new(AssistantChangeKind.Info, $"…and {prompts.Count - MaxCopyPrompts} more prompts in the recipe (all of them are copied; read them in the workflow before running it)."));
        foreach (PortableSlot slot in snapshot.Slots.Take(10))
            changes.Add(new(AssistantChangeKind.Info, $"Still needed: {Short(ModelSafeText.Clean(AssistantMarketplaceTools.Clean(slot.Label), 200, '\''), 200)} (step {slot.StepOrder})"));

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Copy the community recipe {Quote(AssistantMarketplaceTools.Clean(recipe.Name))} as {Quote(requested)}",
            Summary: "Nothing runs yet.", Changes: changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    private static IEnumerable<JsonElement> LintMessages(JsonElement proposal, string list) =>
        Property(proposal, list) is { ValueKind: JsonValueKind.Array } findings
            ? findings.EnumerateArray().Select(f => Property(f, "message")).Where(m => m is { ValueKind: JsonValueKind.String }).Select(m => m!.Value)
            : [];

    private async Task<ApprovalVerdict> WorkflowEditAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        Guid? workflowId = ToolArgs.GetGuid(args, "workflowId");
        string? name = ToolArgs.GetString(args, "name");
        var changes = new List<AssistantActionChange>();

        if (workflowId is null)
        {
            int stepCount;
            if (proposal is not null)
            {
                // ApplyWorkflow writes diff.ProposedSteps, so the card describes the remembered proposal's
                // normalized additions (plus an explicit removal/retarget), never the raw steps argument.
                AddNormalizationLines(changes, proposal.Json);
                stepCount = AddEntries(changes, proposal.Json, "additions", AssistantChangeKind.Add);
            }
            else
            {
                foreach (StepRef step in StepsOf(args))
                    changes.Add(new(AssistantChangeKind.Add, StepText(step)));
                stepCount = changes.Count;
            }

            changes.AddRange(await AvatarLinesAsync(project.Id,
                [.. AvatarIdsIn(ToolArgs.Get(args, "steps")), .. AvatarIdsIn(proposal?.Json)], ct));
            changes.AddRange(AppChangeLines(proposal));
            IReadOnlyList<int>? createOrders = AddEgressGrantLines(changes, proposal);

            return ApprovalVerdict.Ask(new ApprovalCard(
                info.Kind, info.Risk,
                string.IsNullOrWhiteSpace(name) ? "Create a new workflow" : $"Create the workflow {Quote(name)}",
                Summary: $"A new workflow with {stepCount} step{(stepCount == 1 ? "" : "s")}.",
                Changes: Cap(changes), ProjectName: project.Name, ProjectId: project.Id,
                AllowsMediaEgress: createOrders is not null, GrantedStepOrders: createOrders));
        }

        string? existingName = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == workflowId && w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);
        if (existingName is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        // A rename rides along with the steps and is not part of the confirmation token, so the card has to
        // say it: otherwise "change the steps" would also quietly rename the workflow.
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, existingName, StringComparison.Ordinal))
            changes.Add(new(AssistantChangeKind.Change, $"Rename the workflow to {Quote(name)}"));

        if (proposal is not null)
        {
            // A normalization removal or retarget is its own explicit change, before the ordinary diff lines.
            AddNormalizationLines(changes, proposal.Json);
            AddEntries(changes, proposal.Json, "additions", AssistantChangeKind.Add);
            AddEntries(changes, proposal.Json, "removals", AssistantChangeKind.Remove);
            AddEntries(changes, proposal.Json, "modifications", AssistantChangeKind.Change);
        }
        else
        {
            changes.Add(new(AssistantChangeKind.Info, "Replaces the workflow's steps with the proposed ones."));
        }

        changes.AddRange(await AvatarLinesAsync(project.Id,
            [.. AvatarIdsIn(ToolArgs.Get(args, "steps")), .. AvatarIdsIn(proposal?.Json)], ct));
        changes.AddRange(AppChangeLines(proposal));
        IReadOnlyList<int>? editOrders = AddEgressGrantLines(changes, proposal);

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Change the workflow {Quote(existingName)}",
            Summary: changes.Count == 0 ? "No steps would change." : null,
            Changes: Cap(changes), ProjectName: project.Name, ProjectId: project.Id, WorkflowId: workflowId,
            AllowsMediaEgress: editOrders is not null, GrantedStepOrders: editOrders));
    }

    /// <summary>
    /// A workflow edit that switches a step's footage-sharing consent on (the proposal's server-built <c>egressGrants</c>,
    /// worked out from the steps' own configs, never from model words): one line per step saying so, first in the list.
    /// The card then asks as a media-sending card, in every mode; allowing it is the permission. True when any step is listed.
    /// </summary>
    private static IReadOnlyList<int>? AddEgressGrantLines(List<AssistantActionChange> changes, ProposalRecord? proposal)
    {
        if (proposal is null || Property(proposal.Json, "egressGrants") is not { ValueKind: JsonValueKind.Array } grants)
            return null;

        List<string> parts = [];
        List<int> orders = [];
        foreach (JsonElement grant in grants.EnumerateArray())
        {
            int order = Property(grant, "stepOrder") is { ValueKind: JsonValueKind.Number } o && o.TryGetInt32(out int value) ? value : 0;
            string label = Property(grant, "label") is { ValueKind: JsonValueKind.String } l && !string.IsNullOrWhiteSpace(l.GetString())
                ? new string([.. l.GetString()!.Where(c => !char.IsControl(c))]) : "step " + order;
            string? reason = Property(grant, "reason") is { ValueKind: JsonValueKind.String } r ? r.GetString() : null;
            orders.Add(order);
            parts.Add(reason is null
                ? $"the permission switch on {Quote(label, 60)}"
                : $"{Quote(label, 60)} {MediaEgressNeed.Sends(reason)}");
        }

        if (parts.Count == 0)
            return null;
        changes.Insert(0, new(AssistantChangeKind.Info, EgressGrantCardLine(parts)));
        return orders;
    }

    /// <summary>The card line of a workflow edit that allows sending media from now on, one part per step.</summary>
    internal static string EgressGrantCardLine(IEnumerable<string> parts) =>
        "Allows sending your media to AI services from now on, in this workflow: " + string.Join("; ", parts) + ".";

    private async Task<ApprovalVerdict> RunAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? workflowId = ToolArgs.GetGuid(args, "workflowId");
        if (project is null || workflowId is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (workflow is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        bool makesClips = workflow.Steps.Any(s => s.StepType == StepType.VideoGenerate);
        bool? regenerate = ToolArgs.GetBool(args, "regenerateVideoClips");

        // A workflow that generates paid clips first asks the user whether to reuse them. Nothing is run and
        // nothing is approved yet, and the gate asks the question ITSELF — it never calls the tool on the
        // claim that "the tool will only ask": the tool re-reads the workflow, and an edit in between could
        // make it run instead.
        if (makesClips && regenerate is null)
        {
            return ApprovalVerdict.AnswerWith(PaidClipsQuestion.Build(
                workflow.Name, project.Name,
                workflow.Steps.Where(s => s.StepType == StepType.VideoGenerate).Select(s => s.VideoGenerateConfigJson)));
        }

        var changes = new List<AssistantActionChange>();
        if (ToolArgs.GetString(args, "userRequest") is { Length: > 0 } request)
            changes.Add(new(AssistantChangeKind.Info, $"Your request: {Quote(Short(request, MaxRequestText), MaxRequestText)}"));

        string? styleName = null;
        if (ToolArgs.GetGuid(args, "styleId") is { } styleId)
            styleName = await _db.EditingStyles.AsNoTracking()
                .Where(s => s.Id == styleId && s.OwnerId == context.UserId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(ct);
        else if (ToolArgs.GetString(args, "stylePresetKey") is { Length: > 0 } preset)
            styleName = Humanize(preset);
        if (styleName is not null)
            changes.Add(new(AssistantChangeKind.Info, $"Editing style: {styleName}"));

        string? outputFormat = ToolArgs.GetString(args, "outputFormat");
        if (outputFormat is { Length: > 0 } format && !format.Equals("Original", StringComparison.OrdinalIgnoreCase))
            changes.Add(new(AssistantChangeKind.Info, $"Output format: {format}"));

        // The run's choices are resolved EXACTLY as the ExecuteWorkflow tool resolves them (same resolver,
        // same arguments), so the card prices the resolution the run will render at, and a style, format or
        // resolution the tool refuses is said here instead of being priced.
        // The typed run brief (docs/run-brief.md) goes through the same parser and splitter as the tool, so a brief's
        // resolution is priced and plan-checked here and every field it sets is listed below.
        (RunBrief? runBrief, string? briefError) = RunBriefValidator.ParseJson(ToolArgs.Get(args, "brief"));
        RunBriefResolution choices = briefError is not null
            ? RunBriefResolution.Fail(briefError)
            : await RunBriefSplitter.ResolveAsync(
                new RunStyleResolver(_db), _db, context.UserId, project.Id,
                new LegacyRunInputs(ToolArgs.GetGuid(args, "styleId"), ToolArgs.GetString(args, "stylePresetKey"), null,
                    outputFormat, ToolArgs.GetString(args, "outputResolution"), ToolArgs.GetString(args, "userRequest")),
                runBrief, ct, new AppInputRunStart(_appInputs, _user.OrganizationId, workflow.Id,
                    // The card is what asks: in chat the person's own click on it gives the permission. Anywhere else nobody can.
                    context.ChatApproved ? MediaEgressPermission.ByAssistantCard : null));
        string? refusal = choices.Error;
        // The permission to send media, said from the steps themselves (never from the model's words): which steps send, and what.
        bool allowsMediaEgress = refusal is null && choices.AppInputs?.MediaEgress is { Present: true };
        if (allowsMediaEgress)
            changes.Add(new(AssistantChangeKind.Info, MediaEgressCardLine(choices.AppInputs!.MediaEgress!)));
        // docs/design/app-inputs.md 13.3: an app run is priced on its steps as they will run (bound with its inputs, the
        // chosen branch only, its chosen agent models), exactly as the tool's preflight prices it; and the card names each
        // value it sets, by the field's label, and each chosen model by name. Without app inputs nothing changes.
        RunStartPricing pricing = refusal is null ? choices.PricingOr(RunStartPricing.SavedWorkflow) : RunStartPricing.SavedWorkflow;
        if (refusal is null && choices.AppInputs is { Applies: true, Inputs: { Count: > 0 } values })
        {
            AppManifest? manifest = AppManifestJson.TryParse(workflow.AppManifestJson);
            IReadOnlyDictionary<Guid, string> modelNames = await AssistantAppInputs.ModelNamesAsync(
                _db, AssistantAppInputs.ChosenModels(manifest, values), ct);
            foreach (string line in AssistantAppInputs.RunLines(manifest, values, modelNames))
                changes.Add(new(AssistantChangeKind.Info, line));
        }
        if (pricing.Steps is { } runSteps && pricing.ChargedStepOrders is { } reached && reached.Count < runSteps.Count)
        {
            int skipped = runSteps.Count - reached.Count;
            changes.Add(new(AssistantChangeKind.Info,
                $"Takes the path these fields choose: {skipped} step{(skipped == 1 ? "" : "s")} of the workflow do{(skipped == 1 ? "es" : "")} not run."));
        }

        // Avatars phase 2: every person this run shows, by the avatar's own display name, and that their media leave.
        changes.AddRange(await AvatarLinesAsync(project.Id,
        [
            .. workflow.Steps.Where(s => s.StepType == StepType.VideoGenerate).SelectMany(s => AvatarIdsInConfig(s.VideoGenerateConfigJson)),
            .. (refusal is null && runBrief?.Avatar?.AvatarId is Guid briefAvatar
                && workflow.Steps.Any(s => s.StepType == StepType.VideoGenerate) ? [briefAvatar] : Array.Empty<Guid>()),
        ], ct));

        // Avatars and objects in planned shots (docs/design/avatar-objects.md): a step whose shot planner may use the
        // project's people and objects says so, naming none (the planner chooses when the run gets there).
        if (await PlannedSubjectsLineAsync(project.Id, workflow.Steps, ct) is { } plannedSubjects)
            changes.Add(new(AssistantChangeKind.Info, plannedSubjects));

        if (refusal is null && runBrief is not null)
        {
            changes.AddRange(BriefLines(runBrief));

            // A field the workflow cannot read (WL011) is said here so the card is honest: the linter's own test, run
            // on the steps with their agent types loaded.
            List<WorkflowStep> readers = [.. workflow.Steps];
            await WorkflowLinterAgents.FillAsync(_db, project.OrganizationId, readers, ct);
            IReadOnlyList<string> unused = WorkflowLinter.UnreadBriefFields(readers, runBrief);
            if (unused.Count > 0)
                changes.Add(new(AssistantChangeKind.Info, $"Not used by this app: {string.Join(", ", unused)}"));
        }

        // A brief file is named from the database (same project only, as the run resolves it), so the user sees which
        // file becomes the run's brief; one the project does not have is said here, as the run would refuse it.
        if (refusal is null && ToolArgs.GetGuid(args, "briefFileId") is { } briefFileId)
        {
            string? briefName = await _db.ProjectFiles.AsNoTracking()
                .Where(f => f.Id == briefFileId && f.ProjectId == project.Id)
                .Select(f => f.OriginalFileName)
                .FirstOrDefaultAsync(ct);
            if (briefName is null)
                refusal = "the brief file is not a file of this project.";
            else
                changes.Add(new(AssistantChangeKind.Info, $"Brief: the file {Quote(Short(briefName, 120))}"));
        }

        // The plan's resolution check, as the tool makes it (same organization, same resolved resolution):
        // a resolution the plan does not render is said here instead of being priced.
        if (refusal is null && _planGate is not null
            && await _planGate.CheckResolutionAsync(project.OrganizationId, choices.Choices?.OutputResolution, ct) is { } notEntitled)
            refusal = notEntitled.Message;

        // The plan's gated steps, as the tool and RunStartGate make the check (after the resolution, before capacity).
        if (refusal is null && _planGate is not null
            && await _planGate.CheckRunStepsAsync(project.OrganizationId, project.Id, workflow.Id, pricing.Steps, pricing.ChargedStepOrders, ct) is { } stepNotEntitled)
            refusal = stepNotEntitled.Message;

        // the profile-build template carries the same cap and accept-the-estimate check as every
        // other route that starts it. Approving this card IS the acceptance, so the card says what is
        // accepted (the range and the cap); a run the cap or an unavailable estimate would refuse is said
        // here instead of being offered. Any other workflow gets no line.
        if (refusal is null && _designBuildGate.IsBuildTemplate(workflow))
        {
            DesignBuildGateResult buildGate = await _designBuildGate.CheckAsync(
                _user.OrganizationId, workflow, DesignBuildEstimateGate.AcceptedByApproval, ct);
            if (buildGate.Refused)
                refusal = buildGate.Message;
            else if (buildGate.High > 0)
                changes.Add(new(AssistantChangeKind.Info,
                    $"AI fallback estimate: {buildGate.Low:0.##} to {buildGate.High:0.##} credits (cap {buildGate.CapCredits:0.##}); approving this run accepts it."));
        }

        if (refusal is not null)
            changes.Add(new(AssistantChangeKind.Info, $"This run will not start: {refusal}"));

        int? resolution = refusal is null ? choices.Choices?.OutputResolution : null;
        if (resolution is { } shortSide)
            changes.Add(new(AssistantChangeKind.Info, $"Resolution: {OutputResolution.DisplayName(shortSide)}"));
        if (regenerate == true)
            changes.Add(new(AssistantChangeKind.Info, "Makes new video clips instead of reusing the earlier ones."));
        else if (makesClips)
            changes.Add(new(AssistantChangeKind.Info, "Reuses earlier video clips; makes new ones (paid) only where none can be reused."));

        // Credits: priced through the very preflight call the tool makes (same steps, same resolution, same
        // organization), and the card shows what that preflight REQUIRES, not only the usual range. The
        // verdict is not used — it is "allowed" for every affordable run — only the estimate it carries.
        // A preflight that could not be evaluated also says "allowed" (the run fails open and is still
        // charged), so the card stays paid: with the estimate's figures when it has them, else saying so.
        string? cost = null;
        double? costCredits = null;
        bool unestimated = false;
        bool paid = regenerate == true || makesClips;
        using IDisposable briefFootage = ReelBolt.Shared.Metering.SourceFootageBrief.Enter(runBrief?.SourceFiles);
        using IDisposable briefAvatarPricing = ReelBolt.Shared.Metering.BriefAvatarPricing.Enter(runBrief?.Avatar?.AvatarId);
        using IDisposable chosenModels = ReelBolt.Shared.Metering.AppAgentModelAmbient.Enter(pricing.AgentModels, pricing.StepModels);
        if (refusal is null
            && await (pricing.Steps is { } boundSteps
                ? _preflight.PriceStepsAsync(_user.OrganizationId, boundSteps, pricing.ChargedStepOrders, resolution, ct)
                : _preflight.PriceAsync(_user.OrganizationId, project.Id, workflow.Id, resolution, ct)) is { } priced)
        {
            WorkflowCostEstimate estimate = priced.Estimate;
            cost = CreditCostWording.RunNeeds(
                WorkflowsController.ToCredits(estimate.LowMillis),
                WorkflowsController.ToCredits(estimate.HighMillis),
                WorkflowsController.ToCredits(estimate.RequiredMillis),
                WorkflowsController.ToCredits(estimate.FixedMaxMillis),
                estimate.HasHistory ? WorkflowsController.ToCredits(estimate.TypicalMillis) : 0,
                priced.Refused ? WorkflowsController.ToCredits(priced.BalanceMillis) : null);
            // The p90 "required" figure (what the run holds), the same one the card's text leads with.
            costCredits = WorkflowsController.ToCredits(estimate.RequiredMillis);
            if (!priced.Evaluated)
            {
                cost ??= CreditCostWording.RunCostUnknown;
                unestimated = _costEstimator is not { BillingEnabled: false };
            }
            paid |= cost is not null;
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, paid ? AssistantActionRisk.Paid : AssistantActionRisk.Normal,
            $"Run the workflow {Quote(workflow.Name)}",
            Changes: changes.Count == 0 ? null : changes, ProjectName: project.Name, Cost: cost,
            ProjectId: project.Id, WorkflowId: workflowId,
            CostCredits: costCredits, CostUnestimated: unestimated, AllowsMediaEgress: allowsMediaEgress));
    }

    /// <summary>The card line of a run that allows sending media: each step that sends and what it sends.</summary>
    internal static string MediaEgressCardLine(MediaEgressSummary sent) =>
        "Allows sending your media to AI services for this run: "
        + string.Join("; ", MediaEgressPermission.Sent(sent).Select(s => $"{Short(s.StepName, 60)} {s.Sends}")) + ".";

    /// <summary>
    /// The avatars a run or a workflow change shows (docs/design/avatars.md "Assistant and MCP";
    /// docs/design/avatar-objects.md): each by its own display name (never the consent subject's name, never an id) and
    /// its kind, a person as "(avatar)" and an object as "(object)", and that their media go to the video provider. One
    /// line for everything shown; an id that is not an avatar of the project gets its own line. The permission to send
    /// the media stays the customer's, in the app.
    /// </summary>
    private async Task<IReadOnlyList<AssistantActionChange>> AvatarLinesAsync(Guid projectId, IReadOnlyCollection<Guid> avatarIds, CancellationToken ct)
    {
        Guid[] ids = [.. avatarIds.Where(i => i != Guid.Empty).Distinct()];
        if (ids.Length == 0)
            return [];

        var rows = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.SubjectKind })
            .ToDictionaryAsync(a => a.Id, ct);
        List<(string Name, AvatarSubjectKind Kind)> shown =
            [.. ids.Where(rows.ContainsKey).Select(id => (rows[id].Name, rows[id].SubjectKind))];

        List<AssistantActionChange> lines = [];
        if (SubjectsLine(shown) is { } line)
            lines.Add(new AssistantActionChange(AssistantChangeKind.Info, line));
        lines.AddRange(ids.Where(id => !rows.ContainsKey(id)).Select(_ =>
            new AssistantActionChange(AssistantChangeKind.Info, "Shows an avatar that is not in this project; that step would be refused.")));
        return lines;
    }

    /// <summary>
    /// The one sentence that names what is shown and says what leaves: <c>Shows: "Anna" (avatar), "Espresso machine"
    /// (object). Sends their pictures and videos to the video provider.</c> A person's voice samples are named whenever
    /// a person is shown. Null when nothing is shown.
    /// </summary>
    internal static string? SubjectsLine(IReadOnlyList<(string Name, AvatarSubjectKind Kind)> shown)
    {
        if (shown.Count == 0)
            return null;

        string names = string.Join(", ", shown.Select(s =>
            $"{Quote(Short(s.Name, 80))} ({(s.Kind == AvatarSubjectKind.Object ? "object" : "avatar")})"));
        bool anyPerson = shown.Any(s => s.Kind != AvatarSubjectKind.Object);
        string sends = shown.Count == 1
            ? anyPerson
                ? "Sends that person's pictures, videos and voice samples to the video provider."
                : "Sends that object's pictures and videos to the video provider."
            : anyPerson
                ? "Sends their pictures, videos and voice samples to the video provider."
                : "Sends their pictures and videos to the video provider.";
        return $"Shows: {names}. {sends}";
    }

    /// <summary>The card line of a run whose shot planner may show the project's people and objects.</summary>
    internal const string PlannedSubjectsLine =
        "May show the project's people and objects: the shot planner chooses them when it runs, and their pictures and " +
        "videos then go to the video provider.";

    /// <summary>
    /// <see cref="PlannedSubjectsLine"/> when a VideoGenerate step of the run reads a plan and lets its planner use
    /// subjects (<c>PlannedSubjects = Auto</c>, its own permission to send media on) and the project holds an avatar
    /// (the same fact the run's hold reads, <see cref="ReelBolt.Shared.Metering.ProjectSubjectsPricing"/>); null otherwise. No avatar is named:
    /// which ones a shot shows is decided at run time.
    /// </summary>
    private async Task<string?> PlannedSubjectsLineAsync(Guid projectId, IEnumerable<WorkflowStep> steps, CancellationToken ct)
    {
        if (PlannedSubjectsCardLine(steps, projectHasAvatars: true) is null)
            return null;

        return PlannedSubjectsCardLine(steps, await _db.ProjectAvatars.AsNoTracking().AnyAsync(a => a.ProjectId == projectId, ct));
    }

    /// <summary>The rule of <see cref="PlannedSubjectsLineAsync"/> once the project's avatars are known.</summary>
    internal static string? PlannedSubjectsCardLine(IEnumerable<WorkflowStep> steps, bool projectHasAvatars) =>
        projectHasAvatars
        && steps.Any(s => s.StepType == StepType.VideoGenerate && ReelBolt.Shared.Metering.ProjectSubjectsPricing.Matters(s.VideoGenerateConfigJson))
            ? PlannedSubjectsLine
            : null;

    /// <summary>Every <c>avatarId</c> in a tool argument or proposal, including inside step configs carried as JSON strings.</summary>
    internal static IEnumerable<Guid> AvatarIdsIn(JsonElement? element)
    {
        if (element is not { } root)
            yield break;

        var pending = new Stack<JsonElement>();
        pending.Push(root);
        int budget = 10_000;
        while (pending.Count > 0 && budget-- > 0)
        {
            JsonElement e = pending.Pop();
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty p in e.EnumerateObject())
                    {
                        if (string.Equals(p.Name, "avatarId", StringComparison.OrdinalIgnoreCase)
                            && p.Value.ValueKind == JsonValueKind.String && Guid.TryParse(p.Value.GetString(), out Guid id))
                            yield return id;
                        else
                            pending.Push(p.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in e.EnumerateArray())
                        pending.Push(item);
                    break;
                case JsonValueKind.String when e.GetString() is { Length: > 1 } text && text.TrimStart().StartsWith('{'):
                    foreach (Guid nested in AvatarIdsInConfig(text))
                        yield return nested;
                    break;
            }
        }
    }

    /// <summary>The avatar ids in one config JSON string (a step's stored config); none for unreadable JSON.</summary>
    internal static IEnumerable<Guid> AvatarIdsInConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.Contains("avatarId", StringComparison.OrdinalIgnoreCase))
            return [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return [.. AvatarIdsIn(doc.RootElement.Clone())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>One plain line per brief field that is set. Names, never ids.</summary>
    private static IEnumerable<AssistantActionChange> BriefLines(RunBrief brief)
    {
        static AssistantActionChange Info(string text) => new(AssistantChangeKind.Info, text);

        if (brief.Language is { Length: > 0 } language)
            yield return Info($"Language: {Short(language, 40)}");
        if (brief.SourceLanguage is { Length: > 0 } source)
            yield return Info($"Footage language: {Short(source, 40)}");
        if (brief.LengthSec is { } length)
            yield return Info($"Target length: {length} seconds");
        if (brief.Format is { Length: > 0 } format && !format.Equals("Original", StringComparison.OrdinalIgnoreCase))
            yield return Info($"Output format: {Short(format, 40)}");
        if (brief.StylePreset is { Length: > 0 } preset)
            yield return Info($"Editing style: {Humanize(preset)}");
        else if (brief.StyleId is not null)
            yield return Info("Editing style: a saved style");
        else if (brief.Style is not null)
            yield return Info("Editing style: a one-off style");
        if (brief.Tone is { } tone)
            yield return Info($"Tone: {tone}");
        if (RunBriefValidator.NormalizeDesignSystem(brief.DesignSystem) == RunBrief.DesignSystemNone)
            yield return Info("Brand: runs without the project's brand design.");
        if (brief.Voice is { } voice)
        {
            yield return Info(voice.Mode == RunBriefVoiceMode.Narrated
                ? (voice.ProjectVoiceId is not null || voice.LibraryVoiceId is not null ? "Voice: narrated with the chosen voice." : "Voice: narrated.")
                : "Voice: none (silent).");
        }
        if (brief.SourceFiles is { Count: > 0 } groups)
            yield return Info($"Source files: {groups.Sum(g => g.FileIds?.Count ?? 0)} chosen from this project");
        if (brief.Notes is { Length: > 0 } notes)
            yield return Info($"Your request: {Quote(Short(notes, MaxRequestText), MaxRequestText)}");
        // The avatar itself is named on its own line (AvatarLinesAsync), from the database.
    }

    private async Task<ApprovalVerdict> DeleteWorkflowAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? workflowId = ToolArgs.GetGuid(args, "workflowId");
        if (project is null || workflowId is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        var workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == workflowId && w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard)
            .Select(w => new { w.Name, Steps = w.Steps.Count })
            .FirstOrDefaultAsync(ct);
        if (workflow is null)
            return ApprovalVerdict.Unreachable("workflow not found");

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Delete the workflow {Quote(workflow.Name)}",
            Summary: $"This permanently deletes the workflow and its {workflow.Steps} step{(workflow.Steps == 1 ? "" : "s")}. Past runs stay in the project's history.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    private async Task<ApprovalVerdict> StopExecutionAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? executionId = ToolArgs.GetGuid(args, "executionId");
        if (project is null || executionId is null)
            return ApprovalVerdict.Unreachable("execution not found");

        var run = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.ProjectId == project.Id)
            .Select(e => new { e.Status, WorkflowName = e.WorkflowDefinition.Name })
            .FirstOrDefaultAsync(ct);
        if (run is null)
            return ApprovalVerdict.Unreachable("execution not found");

        // Credits used so far: what this run's steps have settled, net of refunds (the ledger, not an estimate).
        long usedMillis = await _db.CreditLedgerEntries.AsNoTracking()
            .Where(l => l.ExecutionId == executionId && (l.Kind == ReelBolt.Shared.Data.Models.Billing.CreditLedgerEntryKind.Settle || l.Kind == ReelBolt.Shared.Data.Models.Billing.CreditLedgerEntryKind.Refund))
            .SumAsync(l => -l.BalanceDeltaMillis, ct);
        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Info, "The run stops and the work in progress is lost."),
            new(AssistantChangeKind.Info, "Credits already used are not refunded; steps that have not started are not charged."),
        };
        if (usedMillis > 0)
            changes.Add(new(AssistantChangeKind.Info, $"Credits used so far: {Shown(usedMillis / 1000.0)}."));

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Stop the run of {Quote(run.WorkflowName)}",
            Summary: run.Status is ExecutionStatus.Queued or ExecutionStatus.Running ? null : "This run has already finished, so there is nothing to stop.",
            Changes: changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    // ------------------------------------------------------------------ projects

    private ApprovalVerdict CreateProject(ApprovalToolInfo info, AIFunctionArguments args)
    {
        string? name = ToolArgs.GetString(args, "name");
        string? description = ToolArgs.GetString(args, "description");
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            string.IsNullOrWhiteSpace(name) ? "Create a new project" : $"Create the project {Quote(name)}",
            Summary: string.IsNullOrWhiteSpace(description) ? null : Short(description, 200),
            Changes: OrganizationChanges(args, update: false)));
    }

    /// <summary>Plain lines for the tags and folder a project create/update will set (empty when it sets neither).</summary>
    private IReadOnlyList<AssistantActionChange>? OrganizationChanges(AIFunctionArguments args, bool update)
    {
        string[]? tags = ToolArgs.GetStrings(args, "tags");
        string? folder = ToolArgs.GetString(args, "folder");
        if ((tags is null && folder is null) || _user.OrganizationId is not { } orgId)
            return null;

        // An invalid request never reaches Apply (the tool refuses it before verifying the token), so no line is shown for it.
        if (ProjectOrganizationPlan.Build(_db, orgId, tags, folder, update, out ProjectOrganizationPlan plan) is not null || plan.IsEmpty)
            return null;
        // One line per tag and one for the folder, each name quoted, so a name can never read as another change.
        List<AssistantActionChange> lines = [];
        if (plan.Tags is not null)
        {
            if (plan.Tags.Count == 0)
                lines.Add(new(AssistantChangeKind.Info, "All tags will be removed"));
            foreach (string tag in plan.Tags)
                lines.Add(new(AssistantChangeKind.Info, plan.NewTags.Contains(tag, StringComparer.OrdinalIgnoreCase)
                    ? $"Tag {Quote(tag)} (new, will be created)"
                    : $"Tag {Quote(tag)}"));
        }
        if (plan.Folder is not null)
            lines.Add(new(AssistantChangeKind.Info, plan.Folder.Length == 0
                ? "The project will be taken out of its folder"
                : plan.NewFolder ? $"Folder {Quote(plan.Folder)} (new, will be created)" : $"Folder {Quote(plan.Folder)}"));
        return lines;
    }

    private async Task<ApprovalVerdict> UpdateProjectAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        string? name = ToolArgs.GetString(args, "name");
        string? description = ToolArgs.GetString(args, "description");
        bool renamed = !string.IsNullOrWhiteSpace(name) && !string.Equals(name, project.Name, StringComparison.Ordinal);

        // The tool stores the description it is given, so an empty one erases the current text. Say so.
        string? summary = !string.IsNullOrWhiteSpace(description)
            ? $"New description: {Quote(Short(description, 200), 200)}"
            : !string.IsNullOrWhiteSpace(project.Description) ? "The project's description will be removed." : null;

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            renamed ? $"Rename the project {Quote(project.Name)} to {Quote(name!)}" : $"Update the project {Quote(project.Name)}",
            Summary: summary, Changes: OrganizationChanges(args, update: true), ProjectName: project.Name, ProjectId: project.Id));
    }

    private async Task<ApprovalVerdict> SavePronunciationAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        string term = PronunciationGlossaryValidator.Normalize(ToolArgs.GetString(args, "term"));
        string spelling = PronunciationGlossaryValidator.Normalize(ToolArgs.GetString(args, "spelling"));
        if (term.Length == 0 || spelling.Length == 0)
            return ApprovalVerdict.Unreachable("word or spelling missing");

        string? current = (await _db.ProjectPronunciationEntries.AsNoTracking()
            .Where(e => e.ProjectId == project.Id).ToListAsync(ct))
            .FirstOrDefault(e => string.Equals(e.Term, term, StringComparison.OrdinalIgnoreCase))?.Spelling;

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            $"Say {Quote(term)} as {Quote(spelling)} in narration",
            Summary: (current is null ? "" : $"It is currently said as {Quote(current)}. ")
                + "Subtitles and on-screen text are unchanged.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    private async Task<ApprovalVerdict> AddSoundEffectAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        if (ToolArgs.GetBool(args, "starterSet") == true)
            return ApprovalVerdict.Ask(new ApprovalCard(
                info.Kind, info.Risk,
                $"Add a starter set of sound effects to {Quote(project.Name)}",
                Summary: "About 24 varied sounds are copied into the project's media. It is free and does not use your storage.",
                ProjectName: project.Name, ProjectId: project.Id));

        string wanted = ToolArgs.GetString(args, "name").Trim();
        List<SfxLibraryItem> enabled = await _db.SfxLibraryItems.AsNoTracking().Where(i => i.IsEnabled).ToListAsync(ct);
        SfxLibraryItem? item = enabled.FirstOrDefault(i => string.Equals(i.Slug, wanted, StringComparison.OrdinalIgnoreCase))
            ?? enabled.FirstOrDefault(i => string.Equals(i.Title, wanted, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return ApprovalVerdict.Unreachable("sound not found");

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            $"Add the sound {Quote(item.Title)} to {Quote(project.Name)}",
            Summary: $"A copy of this {item.Category} sound ({item.DurationMs / 1000.0:0.##} s) is added to the project's media. It is free and does not use your storage.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    private async Task<ApprovalVerdict> DeleteProjectAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        int files = await _db.ProjectFiles.AsNoTracking().CountAsync(f => f.ProjectId == project.Id, ct);
        int workflows = await _db.WorkflowDefinitions.AsNoTracking()
            .CountAsync(w => w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard, ct);

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Delete the project {Quote(project.Name)}",
            Summary: $"This permanently deletes the project with its {files} file{(files == 1 ? "" : "s")} and {workflows} workflow{(workflows == 1 ? "" : "s")}.",
            ProjectId: project.Id));
    }

    // ------------------------------------------------------------------ files

    private async Task<ApprovalVerdict> WriteFileAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        // The path the tool will really write — normalised by the tool's own rules — so the card can never call an
        // overwrite a "create". Whether the file exists is read NOW: the proposal may be minutes old, and the
        // token covers the content, not whether something was added at that path since.
        string? path = AssistantFileTools.WrittenPath(ToolArgs.GetString(args, "directoryPath"), ToolArgs.GetString(args, "fileName"));
        if (path is null)
            return ApprovalVerdict.Unreachable("invalid file path");

        bool replaces = await _db.ProjectFiles.AsNoTracking().AnyAsync(f => f.ProjectId == project.Id && f.OriginalPath == path, ct)
            || (proposal is not null && Property(proposal.Json, "replacesExistingFile") is { ValueKind: JsonValueKind.True });

        var changes = new List<AssistantActionChange>();
        if (proposal is not null && Property(proposal.Json, "sizeBytes") is { ValueKind: JsonValueKind.Number } size && size.TryGetInt64(out long bytes))
            changes.Add(new(AssistantChangeKind.Info, $"Size: {FormatBytes(bytes)}"));

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind,
            replaces ? AssistantActionRisk.Destructive : info.Risk,
            replaces ? $"Replace the contents of the file {Quote(path)}" : $"Create the file {Quote(path)}",
            Summary: replaces ? "The file's current contents are overwritten. This can't be undone." : null,
            Changes: changes.Count == 0 ? null : changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>Most removed + added lines the targeted-edit card spells out; the rest is counted, never dropped silently.</summary>
    private const int MaxDiffLines = 60;
    private const int MaxDiffLineChars = 200;
    private const int MinHunkLines = 4;
    private const int MaxCardRows = 100;
    private const int MaxDiffSpanChars = 400;

    /// <summary>
    /// The card for <c>ApplyEditProjectFile</c> / <c>ApplyInsertProjectFileLines</c>: the file's path and project, and
    /// per changed region the line number with the lines removed and added (and a little unchanged context). It is built
    /// from the file as stored NOW and the exact arguments the tool will receive, by dry-running the very same edit; a
    /// change that no longer applies, or a file that is not there, is unreachable and the call is refused.
    /// </summary>
    private async Task<ApprovalVerdict> TargetedFileChangeAsync(
        string tool, ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Guid? requested = ToolArgs.GetGuid(args, "projectId");
        Guid projectId = requested is { } r && r != Guid.Empty ? r : context.ProjectId ?? Guid.Empty;
        Project? project = projectId == Guid.Empty ? null : (await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct)).Project;
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        ProjectFile? file = AssistantFileTools.FindTarget(_db, project.Id, ToolArgs.GetGuid(args, "fileId"), ToolArgs.GetString(args, "path"), out _);
        if (file is null || _fileStorage is null
            || !ProjectFileTextPolicy.IsLikelyText(file.MimeType, file.OriginalFileName) || !ProjectFileTextPolicy.IsWritableText(file.OriginalFileName))
        {
            return ApprovalVerdict.Unreachable("file not found");
        }

        // A file that is not served is not read for a preview either: the answer is the same as for a missing file.
        if (await OutputGate.IsFileHeldAsync(_db, file, ct))
            return ApprovalVerdict.Unreachable("file not found");

        (byte[] bytes, bool tooLarge) = await AssistantFileTools.ReadBoundedAsync(
            _fileStorage, project.Id, file.StorageKey, AssistantFileTools.MaxContentBytes, ct);
        if (tooLarge)
            return ApprovalVerdict.Unreachable("file too large to edit");

        string before;
        try
        {
            before = ProjectFileTargetedEdits.Utf8Decode(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ApprovalVerdict.Unreachable("file is not text");
        }

        string after;
        string error;
        bool applied;
        if (tool == "ApplyEditProjectFile")
        {
            List<ProjectFileEdit>? edits = null;
            try
            {
                edits = ToolArgs.Get(args, "edits") is { ValueKind: JsonValueKind.Array } array
                    ? array.Deserialize<List<ProjectFileEdit>>(Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)
                    : null;
            }
            catch (JsonException)
            {
                // fall through: no usable edits is an edit that cannot apply
            }

            applied = ProjectFileTargetedEdits.TryEdit(before, edits, out after, out error);
        }
        else
        {
            int afterLine = ProjectFileTargetedEdits.TryReadInt(ToolArgs.Get(args, "afterLine"), out int parsed) ? parsed : int.MinValue;
            applied = ProjectFileTargetedEdits.TryInsert(before, afterLine, ToolArgs.GetString(args, "text"), out after, out error);
        }

        if (!applied)
            return ApprovalVerdict.Unreachable("the change no longer applies: " + Short(error, 200));
        if (System.Text.Encoding.UTF8.GetByteCount(after) > AssistantFileTools.MaxContentBytes)
            return ApprovalVerdict.Unreachable("the result would be too large");

        string path = file.OriginalPath ?? file.OriginalFileName;
        IReadOnlyList<DiffHunk> hunks = ProjectFileTargetedEdits.Diff(before, after);
        int removed = hunks.Sum(h => h.Removed.Count);
        int added = hunks.Sum(h => h.Added.Count);

        // Past the diff bound the change is one undivided hunk too large to review; refused, never shown cut.
        if (removed + added > ProjectFileTargetedEdits.MaxDiffedLines)
            return ApprovalVerdict.Unreachable("the edit is too large to review in one step; split it into smaller edits (separate Propose and Apply calls)");

        // The line diff treats CRLF and LF alike, so a change of line endings would not show as a changed line. It is
        // reported on its own row; a change the card cannot show at all is refused, never shown as an empty card.
        string? lineEndingNote = LineEndingNote(before, after);
        if (hunks.Count == 0 && lineEndingNote is null && !string.Equals(before, after, StringComparison.Ordinal))
            return ApprovalVerdict.Unreachable("the edit changes only invisible characters, which the card cannot show; make the visible change explicit");

        // Every hunk keeps its header and a minimum of changed lines; an edit with too many hunks to give each its
        // minimum inside the card budget is refused rather than shown partially.
        var rendered = new List<(string[] Removed, string[] Added, bool Chunked)>();
        foreach (DiffHunk h in hunks)
        {
            if (RenderHunkLines(h) is not { } lines)
                return ApprovalVerdict.Unreachable("a changed line is too long to review on the card; split it into smaller edits (separate Propose and Apply calls)");
            rendered.Add(lines);
        }

        // A hunk with a long line shown in chunks must be shown whole: it is refused, never cut.
        int[] allowance = rendered.Select(x => x.Chunked ? x.Removed.Length + x.Added.Length : Math.Min(x.Removed.Length + x.Added.Length, MinHunkLines)).ToArray();
        // Every row counts against MaxCardRows: a header, context, the changed lines and a "more" marker per change.
        // Context is dropped when it would not fit; if even the bare minimum does not fit the edit is refused.
        int OverheadRows(bool ctx) => (lineEndingNote is null ? 0 : 1) + hunks.Sum(h => 2 + (ctx ? h.ContextBefore.Count + h.ContextAfter.Count : 0));
        bool withContext = OverheadRows(true) + allowance.Sum() <= MaxCardRows;
        int lineBudget = Math.Min(MaxDiffLines, MaxCardRows - OverheadRows(withContext));
        if (allowance.Sum() > lineBudget)
            return ApprovalVerdict.Unreachable("the edit is too large to review in one step; split it into smaller edits (separate Propose and Apply calls)");

        int spare = lineBudget - allowance.Sum();
        for (int i = 0; i < hunks.Count && spare > 0; i++)
        {
            int extra = Math.Min(spare, rendered[i].Removed.Length + rendered[i].Added.Length - allowance[i]);
            allowance[i] += extra;
            spare -= extra;
        }

        var changes = new List<AssistantActionChange>();
        if (lineEndingNote is not null)
            changes.Add(new(AssistantChangeKind.Info, lineEndingNote));
        for (int i = 0; i < hunks.Count; i++)
        {
            DiffHunk hunk = hunks[i];
            changes.Add(new(AssistantChangeKind.Info, hunk.Removed.Count == 0
                ? $"At line {hunk.Line}"
                : $"Line {hunk.Line}" + (hunk.Removed.Count > 1 ? $"–{hunk.Line + hunk.Removed.Count - 1}" : string.Empty)));
            foreach (string line in withContext ? hunk.ContextBefore : [])
                changes.Add(new(AssistantChangeKind.Info, "  " + DiffLine(line)));

            (string[] removedText, string[] addedText, _) = rendered[i];
            int showAdded = Math.Min(addedText.Length, allowance[i] / 2);
            int showRemoved = Math.Min(removedText.Length, allowance[i] - showAdded);
            showAdded = Math.Min(addedText.Length, allowance[i] - showRemoved);
            foreach (string line in removedText.Take(showRemoved))
                changes.Add(new(AssistantChangeKind.Remove, line));
            foreach (string line in addedText.Take(showAdded))
                changes.Add(new(AssistantChangeKind.Add, line));

            int hidden = removedText.Length + addedText.Length - showRemoved - showAdded;
            if (hidden > 0)
                changes.Add(new(AssistantChangeKind.Info, $"…and {hidden} more changed line{(hidden == 1 ? "" : "s")} in this change."));
            foreach (string line in withContext ? hunk.ContextAfter : [])
                changes.Add(new(AssistantChangeKind.Info, "  " + DiffLine(line)));
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, AssistantActionRisk.Destructive,
            tool == "ApplyEditProjectFile" ? $"Change the file {Quote(path)}" : $"Add lines to the file {Quote(path)}",
            Summary: $"{removed} line{(removed == 1 ? "" : "s")} removed, {added} added, in {hunks.Count} place{(hunks.Count == 1 ? "" : "s")}. The file is edited in place: its previous contents are replaced and can't be undone.",
            Changes: changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>"Line endings: CRLF → mixed" when the edit switches the file's line-ending style, else null.</summary>
    private static string? LineEndingNote(string before, string after)
    {
        static string Style(string text)
        {
            int crlf = text.Split("\r\n").Length - 1;
            int lf = text.Count(ch => ch == '\n') - crlf;
            return crlf > 0 && lf > 0 ? "mixed" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : "none";
        }

        string b = Style(before), a = Style(after);
        return b == a || b == "none" || a == "none" ? null : $"Line endings change: {b} → {a}.";
    }

    /// <summary>
    /// A line for the card, cut to <see cref="MaxDiffLineChars"/>. A cut line always says how much is hidden, so a
    /// truncated line can never pass for the whole line.
    /// </summary>
    private static string DiffLine(string line)
    {
        string text = line.TrimEnd('\r');
        return text.Length <= MaxDiffLineChars
            ? Escape(text)
            : Escape(text[..SafeCut(text, MaxDiffLineChars)]) + $"⟦+{text.Length - SafeCut(text, MaxDiffLineChars)} chars⟧";
    }

    /// <summary>Most combining marks shown on one base character; a longer stack is escaped so it cannot paint over other rows.</summary>
    private const int MaxMarksPerBase = 4;

    private const string WrapMarker = "⟦line continues⟧";
    private const string ContinuationMarker = "⟦continued⟧";

    /// <summary>
    /// Makes every character a reader cannot see, or could mistake for something else, visible: control, format
    /// (bidirectional marks, zero-width characters), line and paragraph separator, private-use, non-ASCII space and
    /// unpaired surrogate characters, U+061C and the card's own marker brackets become <c>\uXXXX</c> (<c>\u{XXXXX}</c> above
    /// U+FFFF), a tab becomes <c>\t</c> and a backslash becomes <c>\\</c>, so an escape token on the card can never be faked
    /// by plain text. It walks code points, so a surrogate pair is never split.
    /// </summary>
    internal static string Escape(string text)
    {
        StringBuilder? builder = null;
        int markRun = 0;       // combining marks already shown on the current base (more than MaxMarksPerBase are escaped)
        bool afterVisible = false; // the previous code point is a shown, non-space character a combining mark can sit on
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            int length = 1;
            int codePoint = c;
            UnicodeCategory category;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var rune = new Rune(c, text[i + 1]);
                length = 2;
                codePoint = rune.Value;
                category = Rune.GetUnicodeCategory(rune);
            }
            else
            {
                category = char.GetUnicodeCategory(c);
            }

            string? token = null;
            if (category == UnicodeCategory.Surrogate && length == 1)
                token = "\\u" + codePoint.ToString("X4", CultureInfo.InvariantCulture);
            else if (codePoint == '\\')
                token = "\\\\";
            else if (codePoint == '\t')
                token = "\\t";
            else if (codePoint is 0x061C or 0x27E6 or 0x27E7
                     || IsDefaultIgnorable(codePoint)
                     || (codePoint != ' ' && IsHiddenCategory(category))
                     || (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark && (!afterVisible || markRun >= MaxMarksPerBase)))
                token = codePoint > 0xFFFF
                    ? "\\u{" + codePoint.ToString("X", CultureInfo.InvariantCulture) + "}"
                    : "\\u" + codePoint.ToString("X4", CultureInfo.InvariantCulture);

            if (token is not null)
            {
                builder ??= new StringBuilder(text, 0, i, text.Length + 16);
                builder.Append(token);
            }
            else
            {
                builder?.Append(text, i, length);
            }

            afterVisible = token is null && codePoint != ' ';
            markRun = token is null && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark ? markRun + 1 : 0;
            i += length;
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>Code points that draw nothing (Unicode Default_Ignorable_Code_Point, plus the blank braille cell), shown escaped.</summary>
    private static bool IsDefaultIgnorable(int codePoint) => codePoint is 0x034F or 0x2800 or 0x3164 or 0xFFA0
        or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x115F and <= 0x1160) or (>= 0x17B4 and <= 0x17B5) or (>= 0x180B and <= 0x180F)
        or (>= 0x2060 and <= 0x206F) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0000 and <= 0xE0FFF);

    private static bool IsHiddenCategory(UnicodeCategory category) => category is UnicodeCategory.Control
        or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.SpaceSeparator;

    /// <summary>The length of the first <paramref name="count"/> characters of <paramref name="text"/> (from its start), backing off one so a surrogate pair is never split.</summary>
    private static int SafeCut(string text, int count)
    {
        if (count > 0 && count < text.Length && char.IsHighSurrogate(text[count - 1]))
            return count - 1;
        return count;
    }

    /// <summary>Trailing spaces of a changed line would be invisible (a whitespace-only change would look like no change); each shows as <c>\s</c>.</summary>
    private static string ShowTrailingSpaces(string escaped)
    {
        int trail = 0;
        while (trail < escaped.Length && escaped[escaped.Length - 1 - trail] == ' ')
            trail++;
        return trail == 0 ? escaped : escaped[..^trail] + string.Concat(Enumerable.Repeat("\\s", trail));
    }

    /// <summary>Spaces at the edges of a chunk would be invisible on the card; each shows as <c>\s</c> (an escaped text never holds a bare backslash).</summary>
    private static string ShowEdgeSpaces(string escaped)
    {
        int lead = 0;
        while (lead < escaped.Length && escaped[lead] == ' ')
            lead++;
        if (lead == escaped.Length)
            return string.Concat(Enumerable.Repeat("\\s", lead));

        int trail = 0;
        while (escaped[escaped.Length - 1 - trail] == ' ')
            trail++;
        return string.Concat(Enumerable.Repeat("\\s", lead)) + escaped[lead..(escaped.Length - trail)] + string.Concat(Enumerable.Repeat("\\s", trail));
    }

    /// <summary>
    /// The removed and added lines of a hunk as the card shows them. A line over <see cref="MaxDiffLineChars"/> is shown
    /// only when its WHOLE differing span is visible: removed and added lines at the same position are compared for their
    /// common prefix and common suffix, and the span between them is shown (with a little context and elision markers for
    /// the unchanged ends). A span wider than <see cref="MaxDiffSpanChars"/> cannot be shown honestly, so the card is refused (<c>null</c>)
    /// rather than truncated; a long line with no line to pair it with is shown whole in consecutive rows (<see cref="AddWhole"/>).
    /// </summary>
    private static (string[] Removed, string[] Added, bool Chunked)? RenderHunkLines(DiffHunk hunk)
    {
        // A carriage return is hidden only when every changed line of the hunk ends in one (a CRLF file); a hunk that
        // adds or removes one shows it as \u000D, so a line-ending change is never invisible.
        bool uniformCr = hunk.Removed.Concat(hunk.Added).All(l => l.EndsWith('\r'));
        string TrimUniformCr(string l) => uniformCr ? l.TrimEnd('\r') : l;
        bool paired = hunk.Removed.Count == hunk.Added.Count;
        bool chunked = false;
        var removed = new List<string>();
        var added = new List<string>();

        for (int i = 0; i < Math.Max(hunk.Removed.Count, hunk.Added.Count); i++)
        {
            string? r = i < hunk.Removed.Count ? TrimUniformCr(hunk.Removed[i]) : null;
            string? a = i < hunk.Added.Count ? TrimUniformCr(hunk.Added[i]) : null;
            bool longPair = paired && ((r?.Length ?? 0) > MaxDiffLineChars || (a?.Length ?? 0) > MaxDiffLineChars);
            if (longPair)
            {
                int d = 0;
                while (d < r!.Length && d < a!.Length && r[d] == a[d])
                    d++;
                int suffix = 0;
                while (suffix < r.Length - d && suffix < a.Length - d && r[r.Length - 1 - suffix] == a[a.Length - 1 - suffix])
                    suffix++;
                if (Math.Max(r.Length, a.Length) - d - suffix > MaxDiffSpanChars)
                    return null;

                removed.Add(Window(r, d, suffix));
                added.Add(Window(a, d, suffix));
                continue;
            }

            if (r is not null)
                chunked |= AddWhole(removed, r);
            if (a is not null)
                chunked |= AddWhole(added, a);
        }

        return (removed.ToArray(), added.ToArray(), chunked);
    }

    /// <summary>
    /// A whole line: one row, or consecutive rows of at most <see cref="MaxDiffLineChars"/> characters when it is longer.
    /// Every row of a split line says so (a wrap marker at the end of all but the last, a continuation marker at the start
    /// of all but the first) and shows edge spaces as <c>\s</c>, so no row can pass for a line of its own.
    /// </summary>
    private static bool AddWhole(List<string> rows, string line)
    {
        if (line.Length <= MaxDiffLineChars)
        {
            rows.Add(ShowTrailingSpaces(Escape(line)));
            return false;
        }

        for (int pos = 0; pos < line.Length;)
        {
            // The cut is judged at the END OF THIS ROW (pos + take), never at the start of the line, and take never drops below 1.
            int remaining = line.Length - pos;
            int take = Math.Min(MaxDiffLineChars, remaining);
            if (take < remaining && take > 1 && char.IsHighSurrogate(line[pos + take - 1]))
                take--;
            string piece = ShowEdgeSpaces(Escape(line.Substring(pos, take)));
            bool first = pos == 0;
            pos += take;
            rows.Add((first ? string.Empty : ContinuationMarker) + piece + (pos < line.Length ? WrapMarker : string.Empty));
        }

        return true;
    }

    /// <summary>The differing span of a line plus 40 characters either side; the unchanged ends are elided with markers.</summary>
    private static string Window(string text, int firstDifference, int commonSuffix)
    {
        const int margin = 40;
        int start = Math.Max(0, firstDifference - margin);
        if (start > 0 && char.IsLowSurrogate(text[start]))
            start--;
        int end = Math.Min(text.Length, text.Length - commonSuffix + margin);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
            end++;
        string head = start > 0 ? $"⟦+{start} chars; change at char {firstDifference + 1}⟧" : string.Empty;
        string tail = end < text.Length ? $"⟦+{text.Length - end} chars⟧" : string.Empty;
        string shown = Escape(text[start..end]);
        return head + (tail.Length == 0 ? ShowTrailingSpaces(shown) : shown) + tail;
    }

    private async Task<ApprovalVerdict> DeleteFileAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        Guid? fileId = ToolArgs.GetGuid(args, "fileId");
        if (project is null || fileId is null)
            return ApprovalVerdict.Unreachable("file not found");

        string? path = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.ProjectId == project.Id)
            .Select(f => f.OriginalPath ?? f.OriginalFileName)
            .FirstOrDefaultAsync(ct);
        if (path is null)
            return ApprovalVerdict.Unreachable("file not found");

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Delete the file {Quote(path)}",
            Summary: "The file is permanently removed from the project.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    // ------------------------------------------------------------------ styles

    private async Task<ApprovalVerdict> SaveStyleAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        string? name = ToolArgs.GetString(args, "name");
        string label = string.IsNullOrWhiteSpace(name) ? "an editing style" : $"the editing style {Quote(name)}";

        if (ToolArgs.GetGuid(args, "styleId") is not { } styleId)
            return ApprovalVerdict.Ask(new ApprovalCard(info.Kind, info.Risk, $"Save {label}"));

        string? existing = await _db.EditingStyles.AsNoTracking()
            .Where(s => s.Id == styleId && s.OwnerId == context.UserId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);
        if (existing is null)
            return ApprovalVerdict.Unreachable("style not found");

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            string.Equals(existing, name, StringComparison.Ordinal)
                ? $"Update the editing style {Quote(existing)}"
                : $"Replace the editing style {Quote(existing)}" + (string.IsNullOrWhiteSpace(name) ? "" : $" with {Quote(name)}"),
            Summary: "Its settings are replaced. Runs that already used it keep the settings they ran with."));
    }

    // ------------------------------------------------------------------ design system

    /// <summary>The brand values the model is about to save, exactly as the tool will receive them.</summary>
    private async Task<ApprovalVerdict> SaveDesignSystemAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        var changes = new List<AssistantActionChange>();
        AddText(changes, AssistantChangeKind.Change, "Primary colour", ToolArgs.GetString(args, "primaryColor"));
        AddText(changes, AssistantChangeKind.Change, "Secondary colour", ToolArgs.GetString(args, "secondaryColor"));
        AddText(changes, AssistantChangeKind.Change, "Background colour", ToolArgs.GetString(args, "backgroundColor"));
        AddText(changes, AssistantChangeKind.Change, "Text colour", ToolArgs.GetString(args, "textColor"));
        AddText(changes, AssistantChangeKind.Change, "Heading font", ToolArgs.GetString(args, "headingFont"));
        AddText(changes, AssistantChangeKind.Change, "Body font", ToolArgs.GetString(args, "bodyFont"));
        AddText(changes, AssistantChangeKind.Change, "Tagline", ToolArgs.GetString(args, "tagline"));
        await AddAssetChangesAsync(changes, project.Id, "Logo", "logo", ToolArgs.GetStrings(args, "logoProjectFileIds"), ct);
        await AddAssetChangesAsync(changes, project.Id, "Icon", "icon", ToolArgs.GetStrings(args, "iconProjectFileIds"), ct);
        await AddAssetChangesAsync(changes, project.Id, "Preview", "preview", ToolArgs.GetStrings(args, "previewProjectFileIds"), ct);

        // The written guidance, described from the exact arguments (and the current counts, by name not id).
        bool brandValuesChange = changes.Count > 0;
        JsonObject? currentVoice = await CurrentVoiceAsync(project.Id, ct);

        string? summaryRaw = ToolArgs.GetString(args, "guidanceSummary");
        if (DesignText.Sanitize(summaryRaw, AssistantDesignSystemTools.GuidanceSummaryMax) is { } summaryClean)
        {
            const int shownChars = 200;
            changes.Add(new(AssistantChangeKind.Change, "Guidance summary: " + Short(summaryClean, shownChars)
                + (summaryClean.Length > shownChars ? " (shortened here, the full text is saved)" : string.Empty)));
        }

        // Same cleaning as the tool: sanitize at the validator cap, drop what is empty afterwards.
        var lists = new List<(string Label, string[] Items)>();
        foreach ((string arg, string label, string key, int cap) in new[]
                 {
                     ("dos", "Do list", "do", AssistantDesignSystemTools.RuleMaxLength),
                     ("donts", "Don't list", "dont", AssistantDesignSystemTools.RuleMaxLength),
                     ("tone", "Tone list", "tone", AssistantDesignSystemTools.ToneMaxLength),
                     ("vocabulary", "Vocabulary", "vocabulary", AssistantDesignSystemTools.VocabularyMaxLength)
                 })
        {
            string[]? items = ToolArgs.GetStrings(args, arg);
            if (items is null)
                continue;

            string[] kept = items.Select(i => DesignText.Sanitize(i, cap)).OfType<string>().ToArray();
            int before = (currentVoice?[key] as JsonArray)?.Count ?? 0;
            changes.Add(new(AssistantChangeKind.Change, kept.Length == 0
                ? $"Clear the {label} ({before} -> 0 items)"
                : $"Rewrite the {label} ({before} -> {kept.Length} items)"));
            lists.Add((label, kept));
        }

        // Every headline first, then as many item lines as fit; whatever is cut is named.
        int room = Math.Max(0, MaxChanges - changes.Count - 1);
        var cut = new List<string>();
        foreach ((string label, string[] kept) in lists)
        {
            int shown = Math.Min(kept.Length, room);
            for (int i = 0; i < shown; i++)
                changes.Add(new(AssistantChangeKind.Info, $"{label}: " + Short(kept[i], 140)));
            room -= shown;
            if (shown < kept.Length)
                cut.Add($"{label} ({kept.Length - shown} more)");
        }

        if (cut.Count > 0)
            changes.Add(new(AssistantChangeKind.Info, "Not listed here, but saved: " + string.Join(", ", cut) + "."));

        string title = changes.Count == 0
            ? $"Create a design system draft for the project {Quote(project.Name)}"
            : $"Save a new design system draft for the project {Quote(project.Name)}";
        if (!brandValuesChange && changes.Count > 0 && lists.Count == 1 && summaryRaw is null)
        {
            string headline = changes[0].Text;
            title = $"{headline} in the project {Quote(project.Name)}";
        }
        else if (!brandValuesChange && (lists.Count > 0 || summaryRaw is not null))
        {
            var names = lists.Select(l => l.Label).ToList();
            if (summaryRaw is not null)
                names.Insert(0, "summary");
            title = $"Rewrite the brand guidance ({string.Join(", ", names)}) in the project {Quote(project.Name)}";
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, title,
            Summary: "A draft is added for review. It is not live until it is approved on the Design system page.",
            Changes: changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>
    /// One line per supplied asset role, naming the primary file and counting the variants. The ids are
    /// looked up on THIS project so a foreign id is described without ever printing the GUID.
    /// </summary>
    private async Task AddAssetChangesAsync(
        List<AssistantActionChange> changes, Guid projectId, string label, string role, string[]? ids,
        CancellationToken ct)
    {
        if (ids is null)
            return;

        if (ids.Length == 0)
        {
            changes.Add(new(AssistantChangeKind.Change, $"Clear the {role}"));
            return;
        }

        var parsed = new List<Guid>(ids.Length);
        foreach (string? raw in ids)
        {
            if (Guid.TryParse(raw, out Guid id) && id != Guid.Empty)
                parsed.Add(id);
        }

        List<ProjectFile> files = parsed.Count == 0
            ? []
            : await _db.ProjectFiles.AsNoTracking()
                .Where(file => file.ProjectId == projectId && parsed.Contains(file.Id)
                    && file.CsamStatus != CsamScanStatus.Quarantined)
                .ToListAsync(ct);

        ProjectFile? primary = parsed.Count == 0 ? null : files.FirstOrDefault(file => file.Id == parsed[0]);
        string? name = primary is null
            ? null
            : DesignText.Sanitize(primary.OriginalFileName, DesignDocumentValidator.MaxAssetNameLength);
        changes.Add(new(AssistantChangeKind.Change, $"{label}: " + (name ?? "a file that is not in this project")));

        int variants = ids.Length - 1;
        if (variants > 0)
            changes.Add(new(AssistantChangeKind.Info, $"{variants} more {role} variant{(variants == 1 ? string.Empty : "s")}"));
    }

    /// <summary>The <c>voice</c> object of the project's current approved brand document, or null.</summary>
    private async Task<JsonObject?> CurrentVoiceAsync(Guid projectId, CancellationToken ct)
    {
        string? json = await (
                from system in _db.DesignSystems.AsNoTracking()
                join version in _db.DesignSystemVersions.AsNoTracking() on system.CurrentVersionId equals version.Id
                where system.ProjectId == projectId && system.Kind == DesignSystemKinds.Design
                select version.DocumentJson)
            .FirstOrDefaultAsync(ct);
        return ParseDiff(json)?["voice"] as JsonObject;
    }

    /// <summary>
    /// Approve or reject one pending design change. The change is found by the SAME rule the tool uses
    /// (<see cref="AssistantDesignSystemTools.MatchChange"/>), over the project's own pending proposals, so the
    /// card describes the change the tool will decide, or the call is refused here and the tool never runs.
    /// </summary>
    private async Task<ApprovalVerdict> DecideDesignChangeAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        string decision = (ToolArgs.GetString(args, "decision") ?? string.Empty).Trim().ToLowerInvariant();
        if (decision is not ("approve" or "reject"))
            return ApprovalVerdict.Unreachable("decision must be approve or reject");

        var pending = await (
                from proposal in _db.DesignSystemChangeProposals.AsNoTracking()
                join system in _db.DesignSystems.AsNoTracking() on proposal.DesignSystemId equals system.Id
                where system.ProjectId == project.Id && proposal.Status == DesignProposalStatus.Pending
                select new { system.Kind, proposal.Id, proposal.CreatedAt, proposal.DiffJson })
            .ToListAsync(ct);

        var diffs = pending.ToDictionary(p => p.Id, p => ParseDiff(p.DiffJson));
        AssistantDesignSystemTools.ChangeMatchResult match = AssistantDesignSystemTools.MatchChange(
            ToolArgs.GetString(args, "changeName"),
            pending.GroupBy(p => p.Kind).Select(g => (g.Key, (IReadOnlyList<AssistantDesignSystemTools.ChangeKey>)g
                .Select(p => new AssistantDesignSystemTools.ChangeKey(p.Id, p.CreatedAt, diffs[p.Id])).ToList())));
        if (match.Ambiguous)
            return ApprovalVerdict.Unreachable("more than one pending design change has that name");
        if (match.Id is not { } changeId || match.Name is null)
            return ApprovalVerdict.Unreachable("design change not found");

        JsonObject? diff = diffs[changeId];
        List<string> paths = AssistantDesignSystemTools.ChangedPaths(diff, int.MaxValue);
        var changes = paths.Take(MaxChanges - 1)
            .Select(path => new AssistantActionChange(AssistantChangeKind.Change, path))
            .ToList();
        if (paths.Count > changes.Count)
            changes.Add(new(AssistantChangeKind.Info, $"…and {paths.Count - changes.Count} more."));

        string tokens = paths.Count == 1 ? "1 token" : $"{paths.Count} tokens";
        bool approve = decision == "approve";
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            $"{(approve ? "Approve" : "Reject")} the design change {Quote(match.Name)}",
            Summary: approve
                ? $"Makes the proposed version the live design for {Quote(project.Name)}. {tokens} affected."
                : $"The proposed version does not become the live design for {Quote(project.Name)}. {tokens} affected.",
            Changes: changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    // ------------------------------------------------------------------ project history

    private async Task<ApprovalVerdict> SaveVersionAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null)
            return ApprovalVerdict.Unreachable("project not found");

        string name = (ToolArgs.GetString(args, "name") ?? string.Empty).Trim();
        if (!ProjectVersionName.IsValid(name))
            return ApprovalVerdict.Unreachable("invalid version name");

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Save the version {Quote(name)}",
            Summary: $"Saves the current state of {Quote(project.Name)} as a named restore point.",
            ProjectName: project.Name, ProjectId: project.Id));
    }

    // ------------------------------------------------------------------ social publishing

    /// <summary>
    /// The card of a scheduled post: the account, platform, project, video, time and the ENTIRE text, all read from the database
    /// and the call's own arguments by the shared <see cref="AssistantSocialDrafts"/> resolver the tool also uses. Nothing is
    /// shortened: the person must see exactly what will be posted in their name. Names, never ids.
    /// </summary>
    private async Task<ApprovalVerdict> SchedulePostAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null || _socialOutputs is null || _user.OrganizationId is not { } organizationId)
            return ApprovalVerdict.Unreachable("project not found");

        (SocialDraftView? view, string? failure) = await AssistantSocialDrafts.ResolveAsync(
            _socialOutputs, _db, organizationId, project, SocialDraft.FromArgs(args, project.Id), DateTime.UtcNow, ct);
        if (view is null)
            return ApprovalVerdict.Unreachable(failure ?? "post not found");
        // The text on this card must be the text the proposal's token was made for; if the saved post text changed in between,
        // no card is shown (the person is never asked to approve text the post will not carry).
        if (_confirmationTokens is not null && ToolArgs.GetString(args, "confirmationToken") is { Length: > 0 } presented
            && !_confirmationTokens.Verify(AssistantSocialTools.OpSchedule, AssistantSocialDrafts.TokenPayload(SocialDraft.FromArgs(args, project.Id), view), _user.UserId, organizationId, presented))
            return ApprovalVerdict.Unreachable("the post text changed after it was proposed; propose it again");

        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Info, $"Account: {Quote(view.AccountName)} on {view.PlatformName}"),
            new(AssistantChangeKind.Info, $"Video: {Quote(view.OutputName)} in project {Quote(project.Name)}"),
            new(AssistantChangeKind.Info, $"When: {view.WhenText}"),
        };
        if (view.Title is not null)
            changes.Add(new(AssistantChangeKind.Add, $"Title: {PostedText(view.Title)}"));
        changes.Add(new(AssistantChangeKind.Add, view.Body is null ? "Text: (none)" : $"Text: {PostedText(view.Body)}"));
        if (view.Hashtags.Count > 0)
            changes.Add(new(AssistantChangeKind.Add, $"Hashtags: {string.Join(' ', view.Hashtags)}"));
        if (view.Privacy is not null)
            changes.Add(new(AssistantChangeKind.Info, $"Visibility: {view.Privacy}"));
        if (view.AiGenerated)
            changes.Add(new(AssistantChangeKind.Info, "Labelled as made with AI"));

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Schedule a post on {view.PlatformName} for {view.WhenText}",
            Summary: $"This will be posted publicly in your name to {Quote(view.AccountName)} ({view.PlatformName}) at the time shown, "
                + "after a last check. You can cancel it until it starts sending.",
            Changes: changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    private async Task<ApprovalVerdict> CancelScheduledPostAsync(ApprovalToolInfo info, AIFunctionArguments args, CancellationToken ct)
    {
        if (ToolArgs.GetGuid(args, "postId") is not { } postId || _socialOutputs is null || _user.OrganizationId is not { } organizationId)
            return ApprovalVerdict.Unreachable("post not found");

        SocialPost? post = await _db.SocialPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId && p.OrganizationId == organizationId, ct);
        Project? project = post is null ? null : (await _projectAccess.GetAsync(post.ProjectId, ProjectPermission.View, ct)).Project;
        if (post is null || project is null || post.Status is not (SocialPostStatus.Scheduled or SocialPostStatus.Draft))
            return ApprovalVerdict.Unreachable("post not found");

        SocialAccount? account = await _db.SocialAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == post.SocialAccountId && a.OrganizationId == organizationId, ct);
        (_, string? video) = await AssistantSocialDrafts.FindOutputAsync(_socialOutputs, _db, organizationId, post.ProjectId, post.OutputId, post.OutputKind, ct);
        string platform = SocialPlatforms.DisplayName(post.Platform);
        string when = AssistantSocialTools.WhenText(post);

        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Info, $"Account: {Quote(account is null ? platform : AssistantSocialDrafts.AccountLabel(account))} on {platform}"),
            new(AssistantChangeKind.Info, $"Video: {Quote(video ?? "(video removed)")} in project {Quote(project.Name)}"),
            new(AssistantChangeKind.Info, $"Was due: {when}"),
        };
        if (!string.IsNullOrWhiteSpace(post.Title))
            changes.Add(new(AssistantChangeKind.Remove, $"Title: {PostedText(post.Title)}"));
        if (!string.IsNullOrWhiteSpace(post.Body))
            changes.Add(new(AssistantChangeKind.Remove, $"Text: {PostedText(post.Body)}"));

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Cancel the scheduled post on {platform} for {when}",
            Summary: "This post will not be sent. Nothing is deleted from the channel because nothing was posted yet.",
            Changes: changes, ProjectName: project.Name, ProjectId: project.Id));
    }

    /// <summary>Post text for a card: whole, never shortened (line breaks normalised); the card shows exactly what will be posted.</summary>
    private static string PostedText(string value) => Regex.Replace(value.Trim(), @"\r\n?", "\n");

    private async Task<ApprovalVerdict> RestoreVersionAsync(
        ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Project? project = await ResolveProjectAsync(args, context, ct);
        if (project is null || _history is null || _historyRestore is null)
            return ApprovalVerdict.Unreachable("project not found");

        string scopeText = (ToolArgs.GetString(args, "scope") ?? string.Empty).Trim().ToLowerInvariant();
        if (scopeText is not ("item" or "project"))
            return ApprovalVerdict.Unreachable("scope must be item or project");
        var scope = scopeText == "project"
            ? ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreScope.Project
            : ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreScope.Item;

        if (ToolArgs.Get(args, "version") is not { ValueKind: JsonValueKind.Number } numberElement
            || !numberElement.TryGetInt32(out int number) || number < 1 || number == int.MaxValue)
            return ApprovalVerdict.Unreachable("version not found");

        var page = await _history.ListVersionsAsync(project.Id, 1, number + 1, ct);
        var version = page?.Versions.FirstOrDefault();
        if (version is null || version.Number != number)
            return ApprovalVerdict.Unreachable("version not found");

        var detail = await _history.GetVersionAsync(project.Id, version.Id, ct);
        if (detail is null)
            return ApprovalVerdict.Unreachable("version not found");

        IReadOnlyList<ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreItemRef> refs = [];
        if (scope == ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreScope.Item)
        {
            // The ONE resolver the tool uses, so the card never describes different items than the tool restores.
            var resolution = AssistantHistoryItems.Resolve(detail.Items, ToolArgs.GetStrings(args, "items"));
            if (resolution.Error is not null)
                return ApprovalVerdict.Unreachable("an item name is missing or ambiguous");
            refs = resolution.Refs;
        }

        var preview = await _historyRestore.PreviewAsync(project.Id, version.Id, scope, refs, ct);
        if (preview is null)
            return ApprovalVerdict.Unreachable("version not found");

        // The card must never hide what a restore does (contract s10): the safety-version line and
        // the totals come first and are always shown, then every changed/removed item one per line
        // up to 100, then ONE line accounting for the rest — so a long restore is never a silent cut.
        int restoredCount = preview.Changes.Count(c => c.Change != "added");
        int removedCount = preview.Changes.Count(c => c.Change == "added");
        int blockedCount = preview.Blocked.Count;
        var changes = new List<AssistantActionChange>
        {
            new(AssistantChangeKind.Info,
                "A safety version of the current state is saved first, so this can be undone."),
            new(AssistantChangeKind.Info,
                $"{restoredCount} restored/changed, {removedCount} removed, {blockedCount} blocked.")
        };

        const int MaxListedItems = 100;
        foreach (var change in preview.Changes.Take(MaxListedItems))
        {
            string label = AssistantHistoryItems.Label(change.Kind, change.Name);
            string kind = AssistantHistoryItems.KindLabel(change.Kind);
            changes.Add(change.Change switch
            {
                "added" => new(AssistantChangeKind.Remove, $"Removed from the project: {kind} {Quote(label)}"),
                "removed" => new(AssistantChangeKind.Add, $"Brought back: {kind} {Quote(label)}"),
                "moved" => new(AssistantChangeKind.Change, $"Moved back: {kind} {Quote(label)}"),
                _ => new(AssistantChangeKind.Change, $"Goes back to this version: {kind} {Quote(label)}")
            });
        }

        if (preview.Changes.Count > MaxListedItems)
        {
            int hidden = preview.Changes.Count - MaxListedItems;
            int hiddenRemoved = preview.Changes.Skip(MaxListedItems).Count(c => c.Change == "added");
            changes.Add(new(AssistantChangeKind.Info, $"…and {hidden} more ({hiddenRemoved} removed)."));
        }

        foreach (var blocked in preview.Blocked)
        {
            changes.Add(new(AssistantChangeKind.Info,
                $"Skipped ({blocked.Reason}): {AssistantHistoryItems.KindLabel(blocked.Kind)} {Quote(AssistantHistoryItems.Label(blocked.Kind, blocked.Name))}"));
        }

        string versionLabel = AssistantHistoryItems.VersionLabel(version);

        bool whole = scope == ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreScope.Project;
        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            whole ? $"Restore the whole project to {versionLabel}" : $"Restore {refs.Count} item(s) from {versionLabel}",
            Summary: whole
                ? $"Puts {Quote(project.Name)} back to {versionLabel}. Everything listed below changes; anything the version does not have is removed."
                : $"Puts the chosen items of {Quote(project.Name)} back to {versionLabel}. Everything listed below changes.",
            Changes: changes,
            ProjectName: project.Name, ProjectId: project.Id));
    }

    private static JsonObject? ParseDiff(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ pictures

    private async Task<ApprovalVerdict> PictureAsync(
        string tool, ApprovalToolInfo info, AIFunctionArguments args, AssistantToolContext context, ProposalRecord? proposal, CancellationToken ct)
    {
        // No picture to act on: there is nothing to approve, and the tool must not run on a guess.
        Guid? documentId = ToolArgs.GetGuid(args, "documentId") ?? context.ImageDocumentId;
        if (documentId is null)
            return ApprovalVerdict.Unreachable("no picture open");

        Project? project = await ResolveProjectAsync(args, context, ct);
        string? summary = proposal is not null && Property(proposal.Json, "summary") is { ValueKind: JsonValueKind.String } s
            ? Scrub(s.GetString())
            : null;

        string title = tool switch
        {
            "ApplyAddTextLayer" => ToolArgs.GetString(args, "text") is { Length: > 0 } text
                ? $"Add the text {Quote(Short(text, 60))} to the picture"
                : info.FallbackTitle,
            "ApplyDeletePictureLayers" => "Delete layers from the picture",
            "ApplyRunPictureTool" => ToolArgs.GetString(args, "tool") is { Length: > 0 } picTool
                ? $"Run the AI picture tool {Quote(Humanize(picTool))}"
                : info.FallbackTitle,
            "ApplyDesignGraphic" => "Design a graphic for the picture",
            _ => info.FallbackTitle
        };

        // A paid tool stays paid unless nothing is explicitly charged: billing is off on this installation,
        // or the quote says the workspace's own AI service bills it (credits 0). An unknown price is not
        // "free": the card stays paid and says the cost could not be worked out.
        string risk = info.Risk;
        string? cost = null;
        double? costCredits = null;
        bool unestimated = false;
        if (info.Risk == AssistantActionRisk.Paid)
        {
            if (_costEstimator is { BillingEnabled: false })
            {
                risk = AssistantActionRisk.Normal;
            }
            else
            {
                JsonElement? estimate = proposal is not null && Property(proposal.Json, "estimatedCost") is { ValueKind: JsonValueKind.Object } est
                    ? est
                    : null;
                // The graphic is priced only for the access-checked project, never the page's raw id.
                if (tool == "ApplyDesignGraphic")
                    (cost, costCredits) = await GraphicCostAsync(project?.Id, ct);
                else
                    (cost, costCredits) = PictureToolCost(estimate);
                if (cost is null)
                    risk = AssistantActionRisk.Normal;
                else if (costCredits is null)
                    unestimated = true; // billing on, paid by nature, no numeric figure: auto mode fails closed
            }
        }

        // Avatars and objects a picture tool shows (docs/design/avatar-objects.md): one line each, with the avatar's
        // own name and kind read from the database. A name the builder cannot place is refused, never run.
        IReadOnlyList<AssistantActionChange>? subjectLines = null;
        bool picturesForThisCall = false;
        if (tool == "ApplyRunPictureTool" && ToolArgs.Get(args, "subjects") is { } named)
        {
            (subjectLines, string? unplaced) = await PictureSubjectLinesAsync(project, named, ct);
            if (unplaced is not null)
                return ApprovalVerdict.Unreachable(unplaced);

            // A call that allows sending the pictures for itself, while the project has not recorded its agreement, says so
            // on its card; such a card always asks (auto mode too), and allowing it is the consent for this one call.
            if (subjectLines is { Count: > 0 } && project is not null
                && AssistantAvatarTools.AsksForMedia(project, ToolArgs.GetBool(args, "allowMediaEgress")))
            {
                picturesForThisCall = true;
                subjectLines = [.. subjectLines, new AssistantActionChange(AssistantChangeKind.Info, MediaForThisCallLine)];
            }
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, risk, title, Summary: summary, Changes: subjectLines is { Count: > 0 } ? subjectLines : null,
            ProjectName: project?.Name, Cost: cost,
            ProjectId: project?.Id ?? context.ProjectId, DocumentId: documentId,
            CostCredits: costCredits, CostUnestimated: unestimated, AllowsMediaEgress: picturesForThisCall));
    }

    /// <summary>
    /// One line per avatar or object an AI picture tool shows: <c>Shows: "Anna" (avatar). Sends that person's pictures
    /// to the picture service.</c> The names are the avatars' own, resolved in the access-checked project exactly as
    /// the tool resolves them (<see cref="ReelBolt.Inference.Api.Services.Avatars.ProjectSubjectNames"/>), never the
    /// model's words. A name that names no avatar, or more than one, has no card: the call is refused.
    /// </summary>
    private async Task<(IReadOnlyList<AssistantActionChange>? Lines, string? Unplaced)> PictureSubjectLinesAsync(
        Project? project, JsonElement named, CancellationToken ct)
    {
        if (named.ValueKind != JsonValueKind.Array || named.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            return (null, "the avatars to show could not be read");

        string[] names = [.. named.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
        if (names.Length == 0)
            return (null, null);
        if (project is null)
            return (null, "no picture open");

        List<ProjectAvatar> avatars = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == project.Id)
            .ToListAsync(ct);
        (IReadOnlyList<ProjectAvatar>? subjects, string? problem) =
            ReelBolt.Inference.Api.Services.Avatars.ProjectSubjectNames.Resolve(avatars, names);
        if (subjects is null)
            return (null, problem);

        return ([.. subjects.Select(a => new AssistantActionChange(AssistantChangeKind.Info,
            a.SubjectKind == AvatarSubjectKind.Object
                ? $"Shows: {Quote(Short(a.Name, 80))} (object). Sends its pictures to the picture service."
                : $"Shows: {Quote(Short(a.Name, 80))} (avatar). Sends that person's pictures to the picture service."))], null);
    }

    /// <summary>
    /// A designed graphic's cost line (billing on), priced FRESH through the graphics service (the same
    /// hidden step its request preflights). An earlier proposal's figures are never used: history may have
    /// moved since, and a stale "needs" figure could be below what the request now requires. With no fresh
    /// estimate the line says the cost could not be worked out. Never null: a graphic is always charged.
    /// </summary>
    private async Task<(string Cost, double? Credits)> GraphicCostAsync(Guid? projectId, CancellationToken ct)
    {
        if (_graphics is not null
            && await _graphics.EstimateAsync(_user.OrganizationId, projectId, ct) is { } fresh
            && CreditCostWording.RunCost(fresh.LowCredits, fresh.HighCredits, fresh.RequiredCredits, 0) is { } line)
        {
            return (line, fresh.RequiredCredits);
        }

        return (CreditCostWording.CostUnknown, null);
    }

    /// <summary>
    /// An AI picture tool's cost line (billing on), from the proposal's one-picture quote: rounded UP, and
    /// said to be a starting price (the tool has no hold; it is metered after the fact). Null only for an
    /// explicit zero quote (the workspace's own AI service bills it); an unknown quote says so.
    /// </summary>
    private static (string? Cost, double? Credits) PictureToolCost(JsonElement? proposalEstimate)
    {
        double? credits = proposalEstimate is { } est ? Number(est, "credits") : null;
        return credits switch
        {
            > 0 => ($"about {CreditCostWording.Up(credits.Value)} credits per picture; larger pictures or several results can cost more", credits),
            0 => (null, null),
            _ => (CreditCostWording.CostUnknown, null)
        };
    }

    private static double? Number(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;

    // ------------------------------------------------------------------ platform and workspace administration

    /// <summary>
    /// The card for a new AI service connection: built from the arguments the tool will receive, so the
    /// endpoint and the default flag an admin approves are the ones that get stored. With <c>providerId</c> the model
    /// joins an existing account, whose name the card shows instead of an endpoint.
    /// </summary>
    private async Task<ApprovalVerdict> ProviderCreateAsync(ApprovalToolInfo info, AIFunctionArguments args, CancellationToken ct)
    {
        if (!_user.IsPlatformAdmin)
            return ApprovalVerdict.Unreachable("admin rights required");

        string? accountName = null;
        if (ToolArgs.GetGuid(args, "providerId") is { } accountId)
        {
            accountName = await _db.InferenceProviders.AsNoTracking()
                .Where(p => p.Id == accountId && p.OrganizationId == null && !p.IsSystem)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct);
            if (accountName is null)
                return ApprovalVerdict.Unreachable("connection not found");
        }

        string? name = ToolArgs.GetString(args, "name");
        var changes = new List<AssistantActionChange>();
        if (accountName is not null)
            changes.Add(new(AssistantChangeKind.Info, $"Added to the existing connection {Quote(accountName)}, using its endpoint and key"));
        AddText(changes, AssistantChangeKind.Info, "Kind", ToolArgs.GetString(args, "kind"));
        AddText(changes, AssistantChangeKind.Info, "Used for", ToolArgs.GetString(args, "capability"));
        AddText(changes, AssistantChangeKind.Info, "Model", ToolArgs.GetString(args, "modelName"));
        if (accountName is null)
            AddText(changes, AssistantChangeKind.Change, "Endpoint", Authority(ToolArgs.GetString(args, "endpoint")));
        if (ToolArgs.GetBool(args, "isDefault") == true)
            changes.Add(new(AssistantChangeKind.Change, "Becomes the default connection for that purpose"));
        if (ToolArgs.GetBool(args, "isEnabled") == false)
            changes.Add(new(AssistantChangeKind.Info, "Starts switched off"));
        if (accountName is null)
        {
            changes.Add(new(
                AssistantChangeKind.Info,
                string.IsNullOrWhiteSpace(ToolArgs.GetString(args, "apiKey")) ? "No API key" : "An API key is stored with it"));
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk,
            string.IsNullOrWhiteSpace(name) ? "Add an AI service connection" : $"Add the AI service connection {Quote(name)}",
            Changes: changes));
    }

    private async Task<ApprovalVerdict> ProviderUpdateAsync(ApprovalToolInfo info, AIFunctionArguments args, CancellationToken ct)
    {
        if (!_user.IsPlatformAdmin)
            return ApprovalVerdict.Unreachable("admin rights required");
        if (await PlatformCapabilityAsync(args, ct) is not { } target)
            return ApprovalVerdict.Unreachable("connection not found");

        string current = target.DisplayName;
        string? endpoint = ToolArgs.GetString(args, "endpoint");
        string? apiKey = ToolArgs.GetString(args, "apiKey");
        string? modelName = ToolArgs.GetString(args, "modelName");
        bool? isEnabled = ToolArgs.GetBool(args, "isEnabled");
        bool? personalOAuthMode = ToolArgs.GetBool(args, "personalOAuthMode");
        JsonElement? timeoutArg = ToolArgs.Get(args, "timeoutSeconds");
        int? timeout = timeoutArg is { ValueKind: JsonValueKind.Number } t && t.TryGetInt32(out int seconds) ? seconds : null;

        var changes = new List<AssistantActionChange>();
        string? newName = ToolArgs.GetString(args, "name");
        bool renames = !string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, current, StringComparison.Ordinal);
        if (renames)
            changes.Add(new(AssistantChangeKind.Change, $"Rename it to {Quote(newName!)}"));
        AddText(changes, AssistantChangeKind.Change, "Endpoint", Authority(endpoint));
        AddText(changes, AssistantChangeKind.Change, "Model", modelName);
        AddText(changes, AssistantChangeKind.Change, "Kind", ToolArgs.GetString(args, "kind"));
        AddText(changes, AssistantChangeKind.Change, "Used for", ToolArgs.GetString(args, "capability"));
        if (timeoutArg is { } rawTimeout)
            changes.Add(new(AssistantChangeKind.Change, timeout is { } s ? $"Request timeout: {s} seconds" : $"Request timeout: {Short(rawTimeout.GetRawText(), 40)}"));
        if (personalOAuthMode is { } oauth)
        {
            changes.Add(new(AssistantChangeKind.Change, oauth
                ? "Calls use each person's own sign-in with the provider, not the stored key"
                : "Calls use the stored key, no longer each person's own sign-in"));
        }
        if (ToolArgs.GetBool(args, "isDefault") is { } isDefault)
            changes.Add(new(AssistantChangeKind.Change, isDefault ? "Becomes the default connection for its purpose" : "Stops being the default connection"));
        if (isEnabled is { } enabled)
            changes.Add(new(AssistantChangeKind.Change, enabled ? "Switched on" : "Switched off"));
        if (apiKey is { } key)
            changes.Add(new(AssistantChangeKind.Change, key.Trim().Length == 0 ? "The stored API key is removed" : "The stored API key is replaced"));

        // The name and the model code are the MODEL's: its other capabilities change with them.
        if (renames || modelName is not null)
        {
            List<InferenceProviderCapability> otherUses = await _db.ModelCapabilities.AsNoTracking()
                .Where(c => c.ModelId == target.ModelId && c.Id != target.Id)
                .OrderBy(c => c.Capability)
                .Select(c => c.Capability)
                .ToListAsync(ct);
            if (otherUses.Count > 0)
            {
                changes.Add(new(AssistantChangeKind.Change,
                    $"Also applies to the model's other uses: {string.Join(", ", otherUses)}"));
            }
        }

        // The endpoint, key, timeout, OAuth mode and kind belong to the provider account, and switching on switches the
        // account on: every other model under it changes with them (the tool decides with the same predicate).
        int? timeoutForPredicate = timeoutArg is null ? null : timeout ?? 0;
        string? kindArg = ToolArgs.GetString(args, "kind");
        if (AssistantAdminTools.AccountChanges(endpoint, apiKey, timeoutForPredicate, personalOAuthMode, isEnabled, kindArg))
        {
            IReadOnlyList<string> siblings = await SiblingModelNamesAsync(target, ct);
            if (siblings.Count > 0)
            {
                string names = string.Join(", ", siblings.Take(10).Select(n => Quote(n)))
                               + (siblings.Count > 10 ? $" and {siblings.Count - 10} more" : string.Empty);
                bool onlySwitchedOn = endpoint is null && apiKey is null && timeoutArg is null && personalOAuthMode is null && kindArg is null;
                changes.Add(new(AssistantChangeKind.Change, onlySwitchedOn
                    ? $"Switching it on also switches its account on, which brings back the connections sharing it: {names}"
                    : $"Also changes the connections sharing its account: {names}"));
            }
        }

        if (changes.Count == 0)
            changes.Add(new(AssistantChangeKind.Info, "No visible setting changes."));

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Change the AI service connection {Quote(current)}", Changes: Cap(changes)));
    }

    /// <summary>The display names of the other platform models under the same provider account as <paramref name="target"/>.</summary>
    private async Task<IReadOnlyList<string>> SiblingModelNamesAsync(PlatformCapability target, CancellationToken ct) =>
        await _db.InferenceModels.AsNoTracking()
            .Where(m => m.OrganizationId == null && m.ProviderId == target.ProviderId && m.Id != target.ModelId)
            .OrderBy(m => m.DisplayName)
            .Select(m => m.DisplayName)
            .ToListAsync(ct);

    private async Task<ApprovalVerdict> ProviderDeleteAsync(ApprovalToolInfo info, AIFunctionArguments args, CancellationToken ct)
    {
        if (!_user.IsPlatformAdmin)
            return ApprovalVerdict.Unreachable("admin rights required");
        if (await PlatformCapabilityAsync(args, ct) is not { } target)
            return ApprovalVerdict.Unreachable("connection not found");

        // The tool removes the model once it serves nothing else, then the account once it has no model left.
        bool modelEmpties = !await _db.ModelCapabilities.AnyAsync(c => c.ModelId == target.ModelId && c.Id != target.Id, ct);
        bool accountEmpties = modelEmpties
            && !await _db.InferenceModels.AnyAsync(m => m.ProviderId == target.ProviderId && m.Id != target.ModelId, ct);
        string summary = "Anything that used it falls back to the default connection.";
        if (accountEmpties)
        {
            summary += target.HasApiKey
                ? $" It is the last model of its account {Quote(target.AccountName)}, so the account and its stored key are removed too."
                : $" It is the last model of its account {Quote(target.AccountName)}, so the account is removed too.";
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Remove the AI service connection {Quote(target.DisplayName)}", Summary: summary));
    }

    private async Task<ApprovalVerdict> AgentSkillsAsync(ApprovalToolInfo info, AIFunctionArguments args, CancellationToken ct)
    {
        if (!OrgAuthority.IsOrganizationAdmin(_user.OrgRole))
            return ApprovalVerdict.Unreachable("admin rights required");
        if (ToolArgs.GetGuid(args, "agentId") is not { } agentId)
            return ApprovalVerdict.Unreachable("agent not found");

        string? name = await _db.AgentDefinitions.AsNoTracking()
            .Where(a => a.Id == agentId && !a.IsBuiltIn && a.OrganizationId == _user.OrganizationId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(ct);
        if (name is null)
            return ApprovalVerdict.Unreachable("agent not found");

        var changes = new List<AssistantActionChange>();
        if (ToolArgs.Get(args, "skills") is { ValueKind: JsonValueKind.Array } skills)
        {
            string[] names = skills.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => Humanize(e.GetString() ?? string.Empty))
                .Where(n => n.Length > 0)
                .ToArray();
            changes.Add(new(AssistantChangeKind.Change, names.Length == 0 ? "Skills: none" : $"Skills: {string.Join(", ", names.Take(10))}{(names.Length > 10 ? $" and {names.Length - 10} more" : "")}"));
        }

        return ApprovalVerdict.Ask(new ApprovalCard(
            info.Kind, info.Risk, $"Change the skills of the agent {Quote(name)}",
            Summary: "Its current skills are replaced by the new list.",
            Changes: changes.Count == 0 ? null : changes));
    }

    /// <summary>The platform capability a connection tool targets, with its model and account.</summary>
    private sealed record PlatformCapability(
        Guid Id, Guid ModelId, Guid ProviderId, string DisplayName, string AccountName, bool HasApiKey);

    /// <summary>
    /// The PLATFORM capability in <c>providerId</c>, or null — as the tool sees it: the system compute provider is
    /// never offered, so a card for it would describe an action the tool refuses.
    /// </summary>
    private Task<PlatformCapability?> PlatformCapabilityAsync(AIFunctionArguments args, CancellationToken ct) =>
        ToolArgs.GetGuid(args, "providerId") is { } id
            ? _db.ModelCapabilities.AsNoTracking()
                .Where(c => c.Id == id && c.OrganizationId == null && !c.Model.Provider.IsSystem)
                .Select(c => new PlatformCapability(c.Id, c.ModelId, c.Model.ProviderId, c.Model.DisplayName,
                    c.Model.Provider.Name, c.Model.Provider.ApiKeyEncrypted != null && c.Model.Provider.ApiKeyEncrypted != ""))
                .FirstOrDefaultAsync(ct)
            : Task.FromResult<PlatformCapability?>(null);

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The project the call targets — the explicit <c>projectId</c> argument, else the page's — resolved
    /// through the same access service the tool uses, and always at the WEAKEST permission (<c>View</c>).
    /// </summary>
    /// <remarks>
    /// A <c>null</c> here is a refusal (<see cref="ApprovalVerdict.Unreachable"/>): the gate does not run the
    /// tool. Resolving at <c>View</c> keeps that from refusing anything the tool would have done — whoever
    /// holds the tool's stricter permission (<c>Edit</c>, <c>Delete</c>) holds this one — and the price is a
    /// card for a caller whose role the tool will then refuse, reported on the card as it happens.
    /// </remarks>
    private async Task<Project?> ResolveProjectAsync(AIFunctionArguments args, AssistantToolContext context, CancellationToken ct)
    {
        Guid? id = ToolArgs.GetGuid(args, "projectId");
        if (id is null || id == Guid.Empty)
            id = context.ProjectId;
        if (id is not { } projectId || projectId == Guid.Empty)
            return null;

        return (await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct)).Project;
    }

    private static IReadOnlyList<AssistantActionChange> Cap(List<AssistantActionChange> changes)
    {
        if (changes.Count <= MaxChanges)
            return changes;

        var capped = changes.Take(MaxChanges).ToList();
        capped.Add(new(AssistantChangeKind.Info, $"…and {changes.Count - MaxChanges} more."));
        return capped;
    }

    private static void AddText(List<AssistantActionChange> changes, string kind, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            changes.Add(new(kind, $"{label}: {Short(value, 120)}"));
    }

    /// <summary>
    /// <c>https://host:port</c> of an endpoint — where requests (and the stored key) will be sent — without a
    /// path, query or credentials. A value that is not an absolute URL is shown as written, shortened.
    /// </summary>
    private static string? Authority(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.GetLeftPart(UriPartial.Authority)
            : Short(endpoint, 120);
    }

    /// <summary>The step lines of one list (<c>additions</c>, <c>removals</c> or <c>modifications</c>) of a workflow proposal; returns how many steps it listed.</summary>
    private static int AddEntries(List<AssistantActionChange> changes, JsonElement proposal, string list, string kind)
    {
        if (Property(proposal, list) is not { ValueKind: JsonValueKind.Array } entries)
            return 0;

        int count = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            int order = Property(entry, "stepOrder") is { ValueKind: JsonValueKind.Number } o && o.TryGetInt32(out int value) ? value : 0;
            string? label = Property(entry, "summary") is { ValueKind: JsonValueKind.String } l ? l.GetString() : null;
            string? type = Property(entry, "stepType") is { ValueKind: JsonValueKind.String } t ? t.GetString() : null;
            changes.Add(new(kind, StepText(new StepRef(order, label, type))));
            count++;
        }

        return count;
    }

    /// <summary>
    /// "Step 3: Review (Video generate)" — the label the model wrote AND what kind of step it really is, so a
    /// step that can spend credits is never hidden behind an innocent name.
    /// </summary>
    private static string StepText(StepRef step)
    {
        string kindWords = Humanize(step.StepType ?? "Agent");
        return string.IsNullOrWhiteSpace(step.Label)
            ? $"Step {step.Order}: {kindWords}"
            : $"Step {step.Order}: {Short(step.Label, 80)} ({kindWords.ToLowerInvariant()})";
    }

    private readonly record struct StepRef(int Order, string? Label, string? StepType);

    /// <summary>The steps of an apply call's <c>steps</c> argument, however the invoker handed them over.</summary>
    private static IEnumerable<StepRef> StepsOf(AIFunctionArguments args)
    {
        if (ToolArgs.Get(args, "steps") is not { ValueKind: JsonValueKind.Array } steps)
            yield break;

        foreach (JsonElement step in steps.EnumerateArray())
        {
            int order = Property(step, "stepOrder") is { ValueKind: JsonValueKind.Number } o && o.TryGetInt32(out int value) ? value : 0;
            yield return new StepRef(
                order,
                Property(step, "label") is { ValueKind: JsonValueKind.String } l ? l.GetString() : null,
                Property(step, "stepType") is { ValueKind: JsonValueKind.String } t ? t.GetString() : null);
        }
    }

    /// <summary>A property of a JSON object by name, ignoring case; null when absent or not an object. For results our own code wrote.</summary>
    internal static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (JsonProperty property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;

        return null;
    }

    /// <summary>
    /// Wraps a name in curly quotes, bounded; the title reads as a sentence the user can say yes to. Quote
    /// characters INSIDE the name are turned into apostrophes so a name written by someone else can never
    /// close the quote early and read as part of the sentence around it.
    /// </summary>
    internal static string Quote(string value, int max = MaxQuoted) =>
        $"“{Short(ModelSafeText.Clean(value, int.MaxValue, '\''), max)}”";

    internal static string Short(string value, int max)
    {
        string single = Regex.Replace(value.Trim(), @"\s+", " ");
        return single.Length <= max ? single : single[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Removes any GUID (and its brackets) from a model- or tool-written sentence.</summary>
    internal static string? Scrub(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Short(GuidPattern.Replace(text, string.Empty), 400);

    /// <summary>"VideoAnalyze" or "video-analyze" → "Video analyze": a label a person can read.</summary>
    internal static string Humanize(string identifier)
    {
        var sb = new StringBuilder();
        foreach (char c in identifier.Trim())
        {
            if (c is '_' or '-' or '.')
                sb.Append(' ');
            else if (char.IsUpper(c) && sb.Length > 0 && sb[^1] != ' ' && !char.IsUpper(sb[^1]))
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            else
                sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : c);
        }

        return sb.ToString();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };
}

/// <summary>
/// Reads a tool call's arguments the way the function binder does: by EXACT name. The binder is ordinal
/// (<c>{"Name": …}</c> does not satisfy a parameter called <c>name</c>), so a case-insensitive read here would
/// let the card and the tool see two different values for one call. Values come as model-written JSON
/// (<see cref="JsonElement"/>) or as already-typed objects (a test, an in-process caller).
/// </summary>
internal static class ToolArgs
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static JsonElement? Get(AIFunctionArguments args, string name)
    {
        if (!args.TryGetValue(name, out object? value))
            return null;

        return value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(value, Camel)
        };
    }

    public static string? GetString(AIFunctionArguments args, string name) =>
        Get(args, name) is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

    public static string[]? GetStrings(AIFunctionArguments args, string name) =>
        Get(args, name) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : null;

    public static Guid? GetGuid(AIFunctionArguments args, string name) =>
        GetString(args, name) is { } text && Guid.TryParse(text, out Guid id) ? id : null;

    public static bool? GetBool(AIFunctionArguments args, string name) => Get(args, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null
    };
}
