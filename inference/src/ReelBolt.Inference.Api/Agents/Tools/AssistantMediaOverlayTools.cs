using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// One picture or video over the film, as the assistant names it: a project file and the moment, form,
/// anchor, size, opacity and sound it is shown with.
/// </summary>
public sealed record MediaOverlayArg(
    Guid MediaFileId,
    Guid WorkflowId,
    int StepOrder,
    string? Form = null,
    string? Anchor = null,
    string? Size = null,
    double? StartSec = null,
    double? EndSec = null,
    int? Opacity = null,
    string? CardAudio = null,
    string? Audio = null,
    string? AudioLevel = null);

/// <summary>
/// "Show this picture on the video" as assistant and MCP tools: <c>ProposeSetMediaOverlay</c> checks the
/// project Edit authority, the media file, the target workflow's VideoCompile step and every placement
/// choice, and prices nothing (a compile is already priced). <c>ApplySetMediaOverlay</c> verifies the
/// confirmation token against the step as it is stored now and appends the overlay to the step's
/// <c>mediaOverlays</c> through <see cref="StepConfigSaveValidator"/> and
/// <see cref="WorkflowStepReplacer"/>, the same path every other workflow save takes. Neither tool starts
/// a run: <c>ExecuteWorkflow</c> stays the run gate, and nothing here can set
/// <c>allowSourceMediaEgress</c>.
/// </summary>
/// <remarks>Registered by <c>AssistantToolProvider</c> and its factory.</remarks>
public sealed class AssistantMediaOverlayTools
{
    internal const string OpSetMediaOverlay = "SetMediaOverlay";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";

    /// <summary>Progress captions merged into <c>AssistantToolProgress</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProposeSetMediaOverlay"] = "Checking the picture and the video",
        ["ApplySetMediaOverlay"] = "Putting the picture on the video",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IWorkflowEditDiffService _diffService;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly ICurrentUser _currentUser;
    private readonly IPlanGateService? _planGate;
    private readonly StepProviderOwnershipValidator _providerOwnership;

    public AssistantMediaOverlayTools(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IWorkflowEditDiffService diffService,
        IConfirmationTokenService confirmationTokens,
        ICurrentUser currentUser,
        IPlanGateService? planGate = null,
        StepProviderOwnershipValidator? providerOwnership = null)
    {
        _db = db;
        _projectAccess = projectAccess;
        _diffService = diffService;
        _confirmationTokens = confirmationTokens;
        _currentUser = currentUser;
        _planGate = planGate;
        _providerOwnership = providerOwnership ?? new StepProviderOwnershipValidator(db);
    }

    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string shape =
            " mediaFileId is the project file id of the picture or video (image/* or video/*; find it with ListProjectFiles). workflowId and stepOrder name the VideoCompile step of a workflow in the same project (GetWorkflow shows both). form is \"FullScreen\" (a card filling the frame) or \"Inset\" (a picture-in-picture box, the default); anchor is one of TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom or BottomRight (default BottomRight); size is Small, Medium or Large (default Medium); startSec and endSec are the moment on the finished film in seconds (both omitted = the whole film); opacity is 0 to 100 (default 100); cardAudio is Keep (the film's sound continues, the default) or Mute (silence under a card); audio is Muted (the default) or Mixed (a video's own sound); audioLevel is Quiet, Balanced (the default) or Prominent. The overlay is appended to the step's mediaOverlays; the compile must render (Mode=Reencode).";
        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (Guid mediaFileId, Guid workflowId, int stepOrder, string? form = null, string? anchor = null, string? size = null,
                    double? startSec = null, double? endSec = null, int? opacity = null, string? cardAudio = null,
                    string? audio = null, string? audioLevel = null, Guid? projectId = null)
                    => ProposeAsync(context, new MediaOverlayArg(
                        mediaFileId, workflowId, stepOrder, form, anchor, size, startSec, endSec, opacity, cardAudio, audio, audioLevel), projectId),
                name: "ProposeSetMediaOverlay",
                description: "Proposes showing a project picture or video over the film at a chosen moment, as a full-screen card or a corner inset, at a chosen size and opacity and with a chosen sound. It checks the project Edit authority, the media file (an image or video of this project, not quarantined), the target workflow's VideoCompile step and every choice, prices nothing (a run is charged by the workflow credit gate) and returns a confirmationToken and a plain-language summary, WITHOUT writing anything or running anything." + shape + " " + AssistantProjectScope.ProjectIdNote + " A proposal's confirmationToken is accepted by ApplySetMediaOverlay."),
            AIFunctionFactory.Create(
                (Guid mediaFileId, Guid workflowId, int stepOrder, string confirmationToken, string? form = null, string? anchor = null, string? size = null,
                    double? startSec = null, double? endSec = null, int? opacity = null, string? cardAudio = null,
                    string? audio = null, string? audioLevel = null, Guid? projectId = null)
                    => ApplyAsync(context, new MediaOverlayArg(
                        mediaFileId, workflowId, stepOrder, form, anchor, size, startSec, endSec, opacity, cardAudio, audio, audioLevel), confirmationToken, projectId),
                name: "ApplySetMediaOverlay",
                description: "Appends the picture or video a prior ProposeSetMediaOverlay call proposed to the target VideoCompile step's mediaOverlays. confirmationToken is verified against the overlay and against the step as it is stored now; a fabricated or stale token, a changed argument, or a step edited after the proposal is refused and nothing is written. The save goes through the normal step validation. It does NOT run the workflow; ExecuteWorkflow runs it." + shape + " " + AssistantProjectScope.ProjectIdNote + " Returns {workflowId, workflowName, stepOrder, stepLabel, projectName, mediaFileName, hint}."),
        };
    }

    private async Task<string> ProposeAsync(AssistantToolContext context, MediaOverlayArg request, Guid? projectId)
    {
        Prepared prepared = await PrepareAsync(context, ProjectPermission.Edit, request, projectId, tracked: false);
        if (prepared.Error is not null)
            return prepared.Error;

        string token = _confirmationTokens.Compute(OpSetMediaOverlay, prepared.Payload!, context.UserId, _currentUser.OrganizationId);
        return JsonSerializer.Serialize(new
        {
            operation = OpSetMediaOverlay,
            confirmationToken = token,
            summary = prepared.Summary,
            projectId = prepared.Project!.Id,
            projectName = prepared.Project.Name,
            workflowId = prepared.Workflow!.Id,
            workflowName = prepared.Workflow.Name,
            stepOrder = prepared.Target!.StepOrder,
            stepLabel = prepared.Target.Label,
            mediaFileName = prepared.Media!.OriginalFileName,
            form = prepared.Overlay.Form.ToString(),
        }, Json);
    }

    private async Task<string> ApplyAsync(AssistantToolContext context, MediaOverlayArg request, string? confirmationToken, Guid? projectId)
    {
        if (!_currentUser.EmailVerified)
            return Error(EmailVerificationGate.Message, EmailVerificationGate.ErrorCode);

        Prepared prepared = await PrepareAsync(context, ProjectPermission.Edit, request, projectId, tracked: true);
        if (prepared.Error is not null)
            return prepared.Error;

        if (!_confirmationTokens.Verify(OpSetMediaOverlay, prepared.Payload!, context.UserId, _currentUser.OrganizationId, confirmationToken ?? string.Empty))
            return Error(StaleTokenMessage);

        Project project = prepared.Project!;
        WorkflowDefinition workflow = prepared.Workflow!;
        workflow.UpdatedAt = DateTime.UtcNow;
        await new WorkflowStepReplacer(_db).ReplaceStepsAsync(
            workflow, prepared.Steps!.Select(step => _diffService.BuildStep(workflow.Id, step)), CancellationToken.None);

        return JsonSerializer.Serialize(new
        {
            workflowId = workflow.Id,
            workflowName = workflow.Name,
            projectId = project.Id,
            projectName = project.Name,
            stepOrder = prepared.Target!.StepOrder,
            stepLabel = prepared.Target.Label,
            mediaFileName = prepared.Media!.OriginalFileName,
            form = prepared.Overlay.Form.ToString(),
            hint = "Nothing has run yet. Run it with ExecuteWorkflow using this workflowId; the compile is charged by the workflow credit gate.",
        }, Json);
    }

    /// <summary>Everything both tools decide the same way, so a token can only be minted for a save Apply would make.</summary>
    private async Task<Prepared> PrepareAsync(
        AssistantToolContext context, ProjectPermission permission, MediaOverlayArg request, Guid? projectId, bool tracked)
    {
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, permission, projectId, out string scopeError);
        if (project is null)
            return Prepared.Fail(scopeError);

        if (request.MediaFileId == Guid.Empty || request.WorkflowId == Guid.Empty || request.StepOrder < 1)
            return Prepared.Fail(Error("mediaFileId, workflowId and a stepOrder of at least 1 are required.", "INVALID_REQUEST"));

        ProjectFile? media = await _db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(
            f => f.Id == request.MediaFileId && f.ProjectId == project.Id &&
                f.CsamStatus != CsamScanStatus.Quarantined && f.ModerationStatus != FileModerationStatus.Quarantined);
        if (media is null)
            return Prepared.Fail(Error("media file not found in this project: pass the project file id of a picture or video (ListProjectFiles).", "NOT_FOUND"));
        if (!media.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !media.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return Prepared.Fail(Error("the media must be a picture or a video.", "INVALID_REQUEST"));
        }

        IQueryable<WorkflowDefinition> query = _db.WorkflowDefinitions.Include(w => w.Steps);
        if (!tracked)
            query = query.AsNoTracking();
        WorkflowDefinition? workflow = await query.FirstOrDefaultAsync(
            w => w.Id == request.WorkflowId && w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard);
        if (workflow is null)
            return Prepared.Fail(Error("workflow not found in this project (ListWorkflows).", "NOT_FOUND"));

        List<WorkflowStep> stored = [.. workflow.Steps.OrderBy(s => s.StepOrder)];
        if (stored.Select(s => s.StepOrder).Distinct().Count() != stored.Count)
            return Prepared.Fail(Error("this workflow has two steps with the same stepOrder, so the compile step cannot be addressed; fix it with ProposeWorkflow.", "INVALID_REQUEST"));

        WorkflowStep? target = stored.FirstOrDefault(s => s.StepOrder == request.StepOrder);
        if (target is null)
            return Prepared.Fail(Error($"no step at stepOrder {request.StepOrder} in this workflow (GetWorkflow shows the steps).", "NOT_FOUND"));
        if (target.StepType != StepType.VideoCompile)
            return Prepared.Fail(Error($"step {request.StepOrder} is not a VideoCompile step: a picture or video is placed by the step that renders the film.", "INVALID_REQUEST"));
        if (string.IsNullOrWhiteSpace(target.VideoCompileConfigJson))
            return Prepared.Fail(Error($"step {request.StepOrder} has no compile settings yet; set them before adding a picture.", "INVALID_REQUEST"));

        if (!TryParseChoices(request, out Choices choices, out string? choiceError))
            return Prepared.Fail(Error(choiceError!, "INVALID_REQUEST"));

        // Audio applies to a video only; an image asking for Mixed is rendered muted, exactly as the
        // compile ignores the inapplicable field.
        if (!media.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            choices = choices with { Audio = MediaOverlayAudio.Muted };

        JsonObject config;
        try
        {
            config = JsonNode.Parse(target.VideoCompileConfigJson)!.AsObject();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException)
        {
            return Prepared.Fail(Error($"step {request.StepOrder} has invalid compile settings; fix them first.", "INVALID_REQUEST"));
        }

        JsonArray overlays = config["mediaOverlays"] is JsonArray existing
            ? (JsonArray)existing.DeepClone()
            : new JsonArray();
        overlays.Add(choices.ToJson(media.Id));
        config["mediaOverlays"] = overlays;

        List<CreateWorkflowStepRequest> steps = [.. stored.Select(AssistantStepShapes.FromStep)];
        int index = steps.FindIndex(s => s.StepOrder == target.StepOrder);
        steps[index] = steps[index] with { VideoCompileConfigJson = config.ToJsonString() };

        List<string> errors = [.. StepConfigSaveValidator.Validate(steps)];
        errors.AddRange(await _providerOwnership.ValidateAsync(project.OrganizationId, project.Id, steps, CancellationToken.None));
        if (errors.Count > 0)
            return Prepared.Fail(JsonSerializer.Serialize(new { error = StepConfigSaveValidator.FormatMessage(errors), errors }));
        if (_planGate is not null && await _planGate.CheckStepsAsync(project.OrganizationId, steps, CancellationToken.None) is { } refused)
            return Prepared.Fail(JsonSerializer.Serialize(new { error = refused.Message, planGate = refused.ToBody() }));

        // The token binds the exact overlay AND the stored step it is appended to, recomputed from the live rows
        // on apply: an edit to the compile step after the proposal makes the token stale.
        string payload = JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            workflowId = workflow.Id,
            stepOrder = target.StepOrder,
            existingStepHash = AssistantGreenScreenTools.HashSteps([target]),
            overlay = new
            {
                mediaFileId = media.Id,
                form = choices.Form.ToString(),
                anchor = choices.Anchor.ToString(),
                size = choices.Size.ToString(),
                startSec = choices.StartSec,
                endSec = choices.EndSec,
                opacityPercent = choices.Opacity,
                cardAudio = choices.CardAudio.ToString(),
                audio = choices.Audio.ToString(),
                volume = choices.Volume.ToString(),
            },
        }, Json);

        string kind = media.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "a picture" : "a video";
        string summary = $"Show \"{media.OriginalFileName}\" ({kind}) {choices.FormText}, in \"{workflow.Name}\" at step {target.StepOrder} ({target.Label}) {choices.SpanText}"
            + $", {choices.OpacityText}; {choices.SoundText}. The save starts no run and uses no AI model.";
        return new Prepared(null, project, workflow, target, media, steps, choices, payload, summary);
    }

    internal static bool TryParseChoices(MediaOverlayArg request, out Choices choices, out string? error)
    {
        choices = default!;
        error = null;

        bool fullScreen = string.Equals(request.Form?.Trim(), "FullScreen", StringComparison.OrdinalIgnoreCase);
        bool inset = request.Form is null
            || string.Equals(request.Form.Trim(), "Inset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Form.Trim(), nameof(MediaOverlayForm.PictureInPicture), StringComparison.OrdinalIgnoreCase);
        if (!fullScreen && !inset)
        {
            error = "form must be FullScreen or Inset.";
            return false;
        }

        if (!TryEnum(request.Anchor, MediaOverlayAnchor.BottomRight, "anchor", out MediaOverlayAnchor anchor, out error)
            || !TryEnum(request.Size, MediaOverlaySize.Medium, "size", out MediaOverlaySize size, out error)
            || !TryEnum(request.CardAudio, MediaOverlayCardAudio.Keep, "cardAudio", out MediaOverlayCardAudio cardAudio, out error)
            || !TryEnum(request.Audio, MediaOverlayAudio.Muted, "audio", out MediaOverlayAudio audio, out error)
            || !TryEnum(request.AudioLevel, MediaOverlayVolume.Balanced, "audioLevel", out MediaOverlayVolume volume, out error))
        {
            return false;
        }

        int opacity = request.Opacity ?? 100;
        if (opacity is < 0 or > 100)
        {
            error = "opacity must be between 0 and 100.";
            return false;
        }

        if (!ValidMoment(request.StartSec) || !ValidMoment(request.EndSec))
        {
            error = $"startSec and endSec must be between 0 and {VideoSourceRef.MaxWindowSec} seconds.";
            return false;
        }

        if (request.StartSec is { } start && request.EndSec is { } end && end <= start)
        {
            error = "endSec must be after startSec.";
            return false;
        }

        choices = new Choices(
            fullScreen ? MediaOverlayForm.Card : MediaOverlayForm.PictureInPicture,
            anchor, size, request.StartSec, request.EndSec, opacity, cardAudio, audio, volume);
        return true;

        static bool ValidMoment(double? moment) =>
            moment is not { } value || (double.IsFinite(value) && value >= 0 && value <= VideoSourceRef.MaxWindowSec);
    }

    private static bool TryEnum<T>(string? raw, T fallback, string name, out T value, out string? error)
        where T : struct, Enum
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }

        string trimmed = raw.Trim();
        if (Enum.GetNames<T>().FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            value = Enum.Parse<T>(match);
            return true;
        }

        value = fallback;
        error = $"{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.";
        return false;
    }

    private static string Error(string message, string? code = null) =>
        code is null
            ? JsonSerializer.Serialize(new { error = message })
            : JsonSerializer.Serialize(new { error = message, code });

    internal sealed record Choices(
        MediaOverlayForm Form,
        MediaOverlayAnchor Anchor,
        MediaOverlaySize Size,
        double? StartSec,
        double? EndSec,
        int Opacity,
        MediaOverlayCardAudio CardAudio,
        MediaOverlayAudio Audio,
        MediaOverlayVolume Volume)
    {
        public string FormText => Form == MediaOverlayForm.Card ? "full screen" : $"in a corner ({AnchorWords})";

        public string AnchorWords => Anchor switch
        {
            MediaOverlayAnchor.TopLeft => "top left",
            MediaOverlayAnchor.Top => "top",
            MediaOverlayAnchor.TopRight => "top right",
            MediaOverlayAnchor.Left => "left",
            MediaOverlayAnchor.Center => "centre",
            MediaOverlayAnchor.Right => "right",
            MediaOverlayAnchor.BottomLeft => "bottom left",
            MediaOverlayAnchor.Bottom => "bottom",
            _ => "bottom right",
        };

        public string SpanText => (StartSec, EndSec) switch
        {
            (null, null) => "for the whole film",
            ({ } start, null) => $"from {Time(start)} to the end",
            (null, { } end) => $"from the start to {Time(end)}",
            ({ } start, { } end) => $"from {Time(start)} to {Time(end)}",
        };

        public string OpacityText => Opacity >= 100 ? "at full opacity" : $"at {Opacity}% opacity";

        public string SoundText
        {
            get
            {
                string card = CardAudio == MediaOverlayCardAudio.Mute ? "mutes the film" : "keeps the film's sound";
                return Audio == MediaOverlayAudio.Mixed ? $"{card}, with the clip's own sound at {Volume}" : card;
            }
        }

        public JsonObject ToJson(Guid mediaFileId)
        {
            JsonObject content = new() { ["from"] = InsertContentRef.ProjectFileKind, ["projectFileId"] = mediaFileId };
            JsonObject overlay = new()
            {
                ["content"] = content,
                ["form"] = Form.ToString(),
                ["anchor"] = Anchor.ToString(),
                ["size"] = Size.ToString(),
                ["opacityPercent"] = Opacity,
                ["audio"] = Audio.ToString(),
                ["volume"] = Volume.ToString(),
                ["cardAudio"] = CardAudio.ToString(),
            };
            if (StartSec is { } start) overlay["startSec"] = start;
            if (EndSec is { } end) overlay["endSec"] = end;
            return overlay;
        }

        private static string Time(double seconds)
        {
            int total = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
            return $"{total / 60}:{total % 60:00}";
        }
    }

    private sealed record Prepared(
        string? Error,
        Project? Project,
        WorkflowDefinition? Workflow,
        WorkflowStep? Target,
        ProjectFile? Media,
        List<CreateWorkflowStepRequest>? Steps,
        Choices? Overlay,
        string? Payload,
        string? Summary)
    {
        public static Prepared Fail(string error) => new(error, null, null, null, null, null, null, null, null);
    }
}
