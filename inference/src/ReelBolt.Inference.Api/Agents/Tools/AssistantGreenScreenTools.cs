using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
/// The content to show on the green plate, as the assistant names it: <c>from</c> is <c>ProjectFile</c>,
/// <c>Step</c> or <c>StepResult</c>, with exactly that kind's id (the shape of <see cref="InsertContentRef"/>).
/// </summary>
public sealed record GreenScreenContentArg(string? From, Guid? ProjectFileId = null, int? StepOrder = null, Guid? StepResultId = null);

/// <summary>
/// The green-screen insert as assistant and MCP tools: <c>ProposeGreenScreenInsert</c> checks the plate video and the
/// content and prices nothing (there is nothing to price), <c>ApplyGreenScreenInsert</c> creates the workflow from the
/// <c>green-screen-insert</c> template with the ids filled in, or replaces the steps of a same-named workflow THIS TOOL
/// made (its steps still have the template's shape: same step types and labels, in order). A same-named workflow of any
/// other shape is never touched: a new workflow with a numbered name ("… (2)") is made instead. The confirmation token
/// binds the action, the existing workflow's id and a hash of its stored steps, so an edit after the proposal, or a
/// workflow that appeared after a "create" proposal, makes Apply refuse as stale. Every save goes through the normal
/// project-scoped step validation. Neither tool starts a run: <c>ExecuteWorkflow</c> stays the run gate.
/// </summary>
/// <remarks>Not registered in <c>AssistantToolProvider</c> here; the integration ticket composes it.</remarks>
public sealed class AssistantGreenScreenTools
{
    internal const string OpGreenScreenInsert = "GreenScreenInsert";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";
    internal const int MaxNameChars = 200;

    /// <summary>Most numbered names ("… (2)" .. "… (50)") tried before the tool gives up.</summary>
    internal const int MaxNameSuffix = 50;

    /// <summary>Progress captions, in <c>AssistantToolProgress</c>'s voice, for the integration ticket to merge.</summary>
    public static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProposeGreenScreenInsert"] = "Checking the green-screen clip and content",
        ["ApplyGreenScreenInsert"] = "Setting up the green-screen workflow",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IWorkflowEditDiffService _diffService;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly ICurrentUser _currentUser;
    private readonly IPlanGateService? _planGate;
    private readonly StepProviderOwnershipValidator _providerOwnership;

    public AssistantGreenScreenTools(
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
            " plateFileId is the project file id of the clip with the green screen (a video; find it with ListProjectFiles). content says what to show on the screen: {\"from\":\"ProjectFile\",\"projectFileId\":\"<id>\"} for an uploaded screen recording, video or picture, or {\"from\":\"StepResult\",\"stepResultId\":\"<outputId>\"} for a render of an earlier run of this project (the outputId from GetWorkflowExecution); give exactly the one id of its kind. name is optional (default \"Green-screen insert v1\"); a workflow with that name that this tool made has its steps replaced, any other workflow with that name is left alone and a new one with a numbered name is created.";
        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (Guid plateFileId, GreenScreenContentArg content, string? name = null, Guid? projectId = null)
                    => ProposeAsync(context, plateFileId, content, name, projectId),
                name: "ProposeGreenScreenInsert",
                description: "Proposes a workflow that puts a screen recording, video or picture onto a tracked green screen in a clip (a phone, a monitor; it follows the screen as it moves and stays behind anything passing in front). It uses no AI model; only processing time is charged. Checks the clip and the content and returns a confirmationToken, the workflow name, whether it would be created or updated, and a plain-language summary, WITHOUT writing anything or running anything." + shape + " " + AssistantProjectScope.ProjectIdNote + " The proposal's confirmationToken is accepted by ApplyGreenScreenInsert."),
            AIFunctionFactory.Create(
                (Guid plateFileId, GreenScreenContentArg content, string confirmationToken, string? name = null, Guid? projectId = null)
                    => ApplyAsync(context, plateFileId, content, confirmationToken, name, projectId),
                name: "ApplyGreenScreenInsert",
                description: "Creates (or replaces the steps of a same-named one this tool made) the green-screen workflow a prior ProposeGreenScreenInsert call proposed. confirmationToken is verified against the proposal's plateFileId, content and name; a fabricated or stale token, or a changed argument, is refused and nothing is written. It does NOT run the workflow; ExecuteWorkflow runs it (workflowId from the result)." + shape + " " + AssistantProjectScope.ProjectIdNote + " Returns {workflowId, workflowName, projectName, action, steps, hint}."),
        };
    }

    private async Task<string> ProposeAsync(AssistantToolContext context, Guid plateFileId, GreenScreenContentArg? content, string? name, Guid? projectId)
    {
        Prepared prepared = await PrepareAsync(context, ProjectPermission.Edit, plateFileId, content, name, projectId);
        if (prepared.Error is not null)
            return prepared.Error;

        string token = _confirmationTokens.Compute(OpGreenScreenInsert, prepared.Payload!, context.UserId, _currentUser.OrganizationId);
        return JsonSerializer.Serialize(new
        {
            operation = OpGreenScreenInsert,
            confirmationToken = token,
            summary = prepared.Summary,
            projectName = prepared.Project!.Name,
            workflowName = prepared.WorkflowName,
            action = prepared.Existing is null ? "create" : "update",
            steps = StepList(prepared.Steps!),
        }, Json);
    }

    private async Task<string> ApplyAsync(AssistantToolContext context, Guid plateFileId, GreenScreenContentArg? content, string? confirmationToken, string? name, Guid? projectId)
    {
        if (!_currentUser.EmailVerified)
            return Error(EmailVerificationGate.Message, EmailVerificationGate.ErrorCode);

        Prepared prepared = await PrepareAsync(context, ProjectPermission.Edit, plateFileId, content, name, projectId);
        if (prepared.Error is not null)
            return prepared.Error;

        if (!_confirmationTokens.Verify(OpGreenScreenInsert, prepared.Payload!, context.UserId, _currentUser.OrganizationId, confirmationToken ?? string.Empty))
            return Error(StaleTokenMessage);

        Project project = prepared.Project!;
        WorkflowDefinition workflow;
        if (prepared.Existing is { } existing)
        {
            // The one owner of step replacement (P2-N1): it takes the FOR UPDATE lock and archives the steps a
            // past run's results still reference, so the workflow_step_results -> workflow_steps cascade cannot
            // destroy them. It saves the pending rename/UpdatedAt itself; the caller must not add the steps.
            workflow = existing;
            workflow.UpdatedAt = DateTime.UtcNow;
            await new WorkflowStepReplacer(_db).ReplaceStepsAsync(
                workflow, prepared.Steps!.Select(step => _diffService.BuildStep(workflow.Id, step)), CancellationToken.None);
        }
        else
        {
            workflow = new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = prepared.WorkflowName!,
                ProjectId = project.Id,
                CreatedVia = WorkflowCreatedVia.Assistant,
                CreatedInThreadId = context.ThreadId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.WorkflowDefinitions.Add(workflow);
            foreach (CreateWorkflowStepRequest step in prepared.Steps!)
                _db.WorkflowSteps.Add(_diffService.BuildStep(workflow.Id, step));
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        return JsonSerializer.Serialize(new
        {
            workflowId = workflow.Id,
            workflowName = workflow.Name,
            projectId = project.Id,
            projectName = project.Name,
            action = prepared.Existing is null ? "create" : "update",
            steps = StepList(prepared.Steps!),
            hint = "Nothing has run yet. Run it with ExecuteWorkflow using this workflowId; it uses no AI model; only processing time is charged.",
        }, Json);
    }

    /// <summary>Everything both tools decide the same way, so a token can only be minted for a workflow Apply would save.</summary>
    private async Task<Prepared> PrepareAsync(
        AssistantToolContext context, ProjectPermission permission, Guid plateFileId, GreenScreenContentArg? content, string? name, Guid? projectId)
    {
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, permission, projectId, out string scopeError);
        if (project is null)
            return Prepared.Fail(scopeError);

        if (content is null)
            return Prepared.Fail(Error("content is required: say what to show on the green screen.", "INVALID_REQUEST"));

        InsertContentRef reference = new(content.From ?? string.Empty, content.ProjectFileId, content.StepOrder, content.StepResultId);
        if (reference.Validate() is { } malformed)
            return Prepared.Fail(Error("content is not valid: " + malformed.Replace("insertContentRef from", "content from", StringComparison.Ordinal), "INVALID_REQUEST"));
        reference = reference with { From = reference.Kind! };

        string? workflowName = ResolveName(name, out string? nameError);
        if (nameError is not null)
            return Prepared.Fail(Error(nameError, "INVALID_REQUEST"));

        ProjectFile? plate = await _db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(
            f => f.Id == plateFileId && f.ProjectId == project.Id && f.CsamStatus != CsamScanStatus.Quarantined);
        if (plate is null)
            return Prepared.Fail(Error("plate file not found in this project: pass the project file id of the clip with the green screen (ListProjectFiles).", "NOT_FOUND"));
        if (!plate.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return Prepared.Fail(Error("the plate must be a video file: the clip with the green screen.", "INVALID_REQUEST"));

        string contentLabel = "an earlier render";
        if (reference.ProjectFileId is { } contentFileId)
        {
            ProjectFile? contentFile = await _db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(
                f => f.Id == contentFileId && f.ProjectId == project.Id && f.CsamStatus != CsamScanStatus.Quarantined);
            if (contentFile is null)
                return Prepared.Fail(Error("content file not found in this project.", "NOT_FOUND"));
            if (!contentFile.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                && !contentFile.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return Prepared.Fail(Error("the content must be a video or a picture.", "INVALID_REQUEST"));
            }

            contentLabel = contentFile.OriginalFileName;
        }

        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.GreenScreenInsertTemplateKey)!;
        workflowName ??= WorkflowTemplateCatalog.BuildVersionedWorkflowName(template);

        Guid? agentId = await _db.AgentDefinitions.AsNoTracking()
            .Where(a => a.IsBuiltIn && a.AgentType == AgentType.VideoTransform)
            .OrderBy(a => a.CreatedAt)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
        if (agentId is null)
            return Prepared.Fail(Error("the built-in video agent is missing.", "UNAVAILABLE"));

        IReadOnlyDictionary<AgentType, Guid> ids = new Dictionary<AgentType, Guid> { [AgentType.VideoTransform] = agentId.Value };
        List<CreateWorkflowStepRequest> steps = [];
        for (int i = 0; i < template.Steps.Count; i++)
            steps.Add(AssistantStepShapes.FromTemplateStep(template.Steps[i], i + 1, ids));
        steps[0] = steps[0] with { VideoAnalyzeConfigJson = WithPlate(steps[0].VideoAnalyzeConfigJson!, plate.Id) };
        steps[1] = steps[1] with { VideoCompileConfigJson = WithContent(steps[1].VideoCompileConfigJson!, reference) };

        // The normal save path: the shape and order rules, the project-scoped reference lookups (a step result of
        // another project never passes) and the plan gate, exactly as ApplyWorkflow runs them.
        List<string> errors = [.. StepConfigSaveValidator.Validate(steps)];
        errors.AddRange(await _providerOwnership.ValidateAsync(project.OrganizationId, project.Id, steps, CancellationToken.None));
        if (errors.Count > 0)
            return Prepared.Fail(JsonSerializer.Serialize(new { error = StepConfigSaveValidator.FormatMessage(errors), errors }));
        if (_planGate is not null && await _planGate.CheckStepsAsync(project.OrganizationId, steps, CancellationToken.None) is { } refused)
            return Prepared.Fail(JsonSerializer.Serialize(new { error = refused.Message, planGate = refused.ToBody() }));

        GreenScreenTarget? target = await ResolveTargetAsync(_db, project.Id, workflowName, CancellationToken.None);
        if (target is null)
            return Prepared.Fail(Error($"every name from \"{workflowName}\" to \"{workflowName} ({MaxNameSuffix})\" is taken by another workflow: pass a different name.", "INVALID_REQUEST"));
        WorkflowDefinition? existing = target.Existing;
        workflowName = target.WorkflowName;

        // WL018 at propose and at apply (both go through here): an edit that would orphan an exposed app input is refused.
        if (existing is not null)
        {
            IReadOnlyList<string> orphans = WorkflowAppSupport.OrphanErrors(_db, existing.Id, steps);
            if (orphans.Count > 0)
                return Prepared.Fail(Error(string.Join(" ", orphans)));
        }

        // The token binds what Apply will DO to the stored state it was proposed against: the action, the
        // workflow it would replace and a hash of that workflow's stored steps.
        string payload = JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            plateFileId = plate.Id,
            content = new { from = reference.From, projectFileId = reference.ProjectFileId, stepOrder = reference.StepOrder, stepResultId = reference.StepResultId },
            workflowName,
            action = existing is null ? "create" : "update",
            existingWorkflowId = existing?.Id,
            existingStepsHash = target.StepsHash,
        });
        string summary = $"{(existing is null ? "Create the workflow" : "Replace the steps of the existing workflow")} \"{workflowName}\" in project \"{project.Name}\": it puts {contentLabel} "
            + $"onto the green screen in \"{plate.OriginalFileName}\", following the screen as it moves. It uses no AI model; only processing time is charged. "
            + "The clip needs a screen or panel filled with one flat colour, such as green or blue, that stays in view and is mostly uncovered. "
            + "It does not run yet: run it with ExecuteWorkflow once it is saved.";
        return new Prepared(null, project, workflowName, steps, existing, payload, summary);
    }

    /// <summary>
    /// The workflow the green-screen apply writes, decided from the database (the tool and its approval card both
    /// call this): the first of <paramref name="requestedName"/>, "… (2)", "… (3)" … that is either free (create) or a
    /// Standard workflow this tool made (replace its steps). <c>null</c> when every candidate is taken by another workflow.
    /// </summary>
    internal static async Task<GreenScreenTarget?> ResolveTargetAsync(InferenceApiDbContext db, Guid projectId, string requestedName, CancellationToken ct)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.GreenScreenInsertTemplateKey)!;
        for (int n = 1; n <= MaxNameSuffix; n++)
        {
            string candidate = n == 1 ? requestedName : $"{requestedName} ({n})";
            WorkflowDefinition? found = await db.WorkflowDefinitions
                .FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Name == candidate, ct);
            if (found is null)
                return new GreenScreenTarget(candidate, null, string.Empty);

            List<WorkflowStep> steps = await db.WorkflowSteps.AsNoTracking()
                .Where(s => s.WorkflowDefinitionId == found.Id)
                .OrderBy(s => s.StepOrder)
                .ToListAsync(ct);
            if (found.Kind == WorkflowDefinitionKind.Standard && HasTemplateShape(steps, template))
                return new GreenScreenTarget(candidate, found, HashSteps(steps));
        }

        return null;
    }

    /// <summary>The marker of a workflow this tool (or the template picker) made: the template's steps, in order, by type and label.</summary>
    private static bool HasTemplateShape(IReadOnlyList<WorkflowStep> steps, WorkflowTemplateDefinition template)
    {
        if (steps.Count != template.Steps.Count)
            return false;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].StepOrder != i + 1
                || steps[i].StepType != template.Steps[i].StepType
                || !string.Equals(steps[i].Label, template.Steps[i].Label, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly PropertyInfo[] HashedStepProperties = typeof(WorkflowStep)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && IsScalar(p.PropertyType))
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    private static bool IsScalar(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(decimal) || t == typeof(DateTime);
    }

    /// <summary>SHA-256 over every scalar column of every stored step (ids, types, labels and every config), so any edit changes it.</summary>
    internal static string HashSteps(IReadOnlyList<WorkflowStep> steps)
    {
        List<Dictionary<string, object?>> rows = steps
            .Select(step => HashedStepProperties.ToDictionary(p => p.Name, p => p.GetValue(step), StringComparer.Ordinal))
            .ToList();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows)))).ToLowerInvariant();
    }

    private static string? ResolveName(string? name, out string? error)
    {
        error = null;
        string? trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length > MaxNameChars)
            error = $"name may be at most {MaxNameChars} characters.";
        return trimmed;
    }

    private static string WithPlate(string analyzeJson, Guid plateFileId)
    {
        JsonObject root = JsonNode.Parse(analyzeJson)!.AsObject();
        root["source"] = new JsonObject { ["kind"] = nameof(VideoSourceKind.ProjectFile), ["projectFileId"] = plateFileId };
        // The template's "placeholders" names the field this call just filled in; leaving it in the stored
        // step would tell the user (and the assistant reading the step) to fill in what is already set.
        root.Remove("placeholders");
        return root.ToJsonString();
    }

    private static string WithContent(string compileJson, InsertContentRef reference)
    {
        JsonObject root = JsonNode.Parse(compileJson)!.AsObject();
        JsonObject node = new() { ["from"] = reference.From };
        if (reference.ProjectFileId is { } file) node["projectFileId"] = file;
        if (reference.StepOrder is { } order) node["stepOrder"] = order;
        if (reference.StepResultId is { } result) node["stepResultId"] = result;
        root["insertContentRef"] = node;
        root.Remove("placeholders");
        return root.ToJsonString();
    }

    private static IEnumerable<object> StepList(IReadOnlyList<CreateWorkflowStepRequest> steps) =>
        steps.Select(s => (object)new { stepOrder = s.StepOrder, label = s.Label, stepType = s.StepType });

    private static string Error(string message, string? code = null) =>
        code is null
            ? JsonSerializer.Serialize(new { error = message })
            : JsonSerializer.Serialize(new { error = message, code });

    internal sealed record GreenScreenTarget(string WorkflowName, WorkflowDefinition? Existing, string StepsHash);

    private sealed record Prepared(
        string? Error,
        Project? Project,
        string? WorkflowName,
        List<CreateWorkflowStepRequest>? Steps,
        WorkflowDefinition? Existing,
        string? Payload,
        string? Summary)
    {
        public static Prepared Fail(string error) => new(error, null, null, null, null, null, null);
    }
}
