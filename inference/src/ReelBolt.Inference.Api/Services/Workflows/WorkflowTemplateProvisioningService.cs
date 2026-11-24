using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

public sealed class WorkflowTemplateProvisioningService
{
    private readonly InferenceApiDbContext _db;
    private readonly ILogger<WorkflowTemplateProvisioningService> _logger;
    private readonly IConfiguration _configuration;

    public WorkflowTemplateProvisioningService(
        InferenceApiDbContext db,
        ILogger<WorkflowTemplateProvisioningService> logger,
        IConfiguration configuration)
    {
        _db = db;
        _logger = logger;
        _configuration = configuration;
    }

    public bool IsAutoStarterEnabled() =>
        _configuration.GetValue("WorkflowTemplates:EnableAutoStarter", true);

    /// <summary>
    /// The people switch <c>Avatars:Enabled</c> (default false): off, the avatar recipes are neither listed nor created.
    /// Every avatar recipe is written for a person, so the objects switch (<c>Avatars:Objects:Enabled</c>) alone does not
    /// open them; the catalog lists them by the same switch (<c>AppStepFacts.PersonAvatarsEnabled</c>).
    /// </summary>
    public bool AreAvatarsEnabled() => ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(_configuration, "Avatars:Enabled");

    private IReadOnlyList<WorkflowTemplateDefinition> WithoutAvatarRecipesWhenOff(IReadOnlyList<WorkflowTemplateDefinition> templates) =>
        AreAvatarsEnabled() ? templates : templates.Where(t => !WorkflowTemplateCatalog.IsAvatarTemplate(t.Key)).ToList();

    public bool IsAdvancedTemplatesEnabled() =>
        _configuration.GetValue("WorkflowTemplates:EnableAdvancedTemplates", true);

    public async Task<IReadOnlyList<WorkflowDefinition>> EnsureAutoTemplatesAsync(Guid projectId, CancellationToken ct)
    {
        if (!IsAutoStarterEnabled())
            return [];

        List<WorkflowDefinition> created = [];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAutoCreateTemplates())
        {
            WorkflowDefinition? workflow = await CreateFromTemplateAsync(projectId, template.Key, skipIfExists: true, ct);
            if (workflow != null)
                created.Add(workflow);
        }

        return created;
    }

    public Task<WorkflowDefinition?> CreateFromTemplateAsync(Guid projectId, string templateKey, bool skipIfExists, CancellationToken ct)
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.GetByKey(templateKey);
        if (template is null || (!AreAvatarsEnabled() && WorkflowTemplateCatalog.IsAvatarTemplate(template.Key)))
            throw new InvalidOperationException($"Unknown workflow template '{templateKey}'.");
        string requestedKey = template.Key;
        // A hidden template's key creates the template it was merged into (9.5), with that key's defaults.
        if (template.SupersededBy is { } replacement && WorkflowTemplateCatalog.GetByKey(replacement) is { } merged)
            template = merged;

        // The profile build route (SPEC 9.4 `POST /builds`) must be able to provision
        // `product-profile-from-code` regardless of the advanced-templates switch: the route is the
        // only way to build the profile the advanced `-ds` templates require, so gating it would make
        // those templates unreachable when the switch is off. Every other key keeps the gate.
        bool alwaysAvailable = string.Equals(
            template.Key, WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey, StringComparison.Ordinal);

        if (!template.AutoCreateOnProject && !alwaysAvailable && !IsAdvancedTemplatesEnabled())
            throw new InvalidOperationException("Advanced workflow templates are disabled by configuration.");

        return CreateFromTemplateAsync(projectId, template, requestedKey, skipIfExists, ct);
    }

    /// <summary>
    /// Every template, hidden and superseded ones included. The gallery, the template list and the assistant's recipe
    /// tools (lane L8) use <see cref="ListRecipesAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowTemplateDefinition>> ListAvailableTemplatesAsync(CancellationToken ct)
    {
        _ = ct;
        IReadOnlyList<WorkflowTemplateDefinition> all = WithoutAvatarRecipesWhenOff(WorkflowTemplateCatalog.GetAll());
        if (IsAdvancedTemplatesEnabled())
            return all;

        return all.Where(t => t.AutoCreateOnProject).ToList();
    }

    /// <summary>
    /// The templates a person picks from in the gallery and the template list: hidden templates left out.
    /// </summary>
    public Task<IReadOnlyList<WorkflowTemplateDefinition>> ListRecipesAsync(CancellationToken ct)
    {
        IReadOnlyList<WorkflowTemplateDefinition> listed = WithoutAvatarRecipesWhenOff(WorkflowTemplateCatalog.GetListed());
        return Task.FromResult<IReadOnlyList<WorkflowTemplateDefinition>>(
            IsAdvancedTemplatesEnabled() ? listed : listed.Where(t => t.AutoCreateOnProject).ToList());
    }

    /// <summary>
    /// The recipe a new project may start with (<c>POST /api/v1/projects</c> <c>starterTemplateKey</c>): one
    /// of <see cref="ListRecipesAsync"/> by its exact key, so it is listed (never a hidden or superseded
    /// key), obeys the avatar switch, and with the advanced-templates switch off only the automatic starter
    /// qualifies. Null for anything else. Whatever this returns, <see cref="CreateFromTemplateAsync(Guid, string, bool, CancellationToken)"/>
    /// accepts.
    /// </summary>
    public async Task<WorkflowTemplateDefinition?> FindStarterRecipeAsync(string? templateKey, CancellationToken ct)
    {
        if (templateKey is null) return null;
        IReadOnlyList<WorkflowTemplateDefinition> recipes = await ListRecipesAsync(ct);
        return recipes.FirstOrDefault(t => string.Equals(t.Key, templateKey, StringComparison.Ordinal));
    }

    private async Task<WorkflowDefinition?> CreateFromTemplateAsync(
        Guid projectId, WorkflowTemplateDefinition template, string requestedKey, bool skipIfExists, CancellationToken ct)
    {
        string workflowName = WorkflowTemplateCatalog.BuildVersionedWorkflowName(template);

        WorkflowDefinition? existing = await _db.WorkflowDefinitions
            .Include(workflow => workflow.Steps)
            .FirstOrDefaultAsync(workflow => workflow.ProjectId == projectId && workflow.Name == workflowName
                && workflow.Kind == WorkflowDefinitionKind.Standard, ct);

        if (existing is not null)
        {
            if (skipIfExists)
                return existing;

            throw new InvalidOperationException($"Workflow template '{template.Key}' has already been applied to this project.");
        }

        IReadOnlyDictionary<AgentType, AgentDefinition> agentsByType = await ResolveBuiltInAgentsByTypeAsync(template, ct);

        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = workflowName,
            RequiresUserInput = template.RequiresUserInput,
            // The curated app manifest is copied as-is; a template without one gets a derived manifest at read time. Its
            // agent-model inputs get the built-in agent they pick for, and a merged template's key its own defaults.
            AppManifestJson = WorkflowTemplateAppInputs.ManifestForCreate(
                    template, requestedKey, type => agentsByType.TryGetValue(type, out AgentDefinition? agent) ? agent.Id : null) is { } manifest
                ? AppManifestJson.Serialize(manifest)
                : null,
            CreatedVia = WorkflowCreatedVia.Template,
            TemplateKey = template.Key,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        foreach (WorkflowStep step in BuildSteps(template, workflow.Id, agentsByType))
            workflow.Steps.Add(step);

        _db.WorkflowDefinitions.Add(workflow);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Applied workflow template {TemplateKey} to project {ProjectId} as workflow {WorkflowId}",
            template.Key,
            projectId,
            workflow.Id);

        return workflow;
    }

    /// <summary>The template's steps as unsaved rows of <paramref name="workflowId"/>, with the built-in agent ids filled in.</summary>
    private static List<WorkflowStep> BuildSteps(
        WorkflowTemplateDefinition template,
        Guid workflowId,
        IReadOnlyDictionary<AgentType, AgentDefinition> agentsByType)
    {
        List<WorkflowStep> steps = [];
        for (int i = 0; i < template.Steps.Count; i++)
        {
            WorkflowTemplateStepDefinition stepDefinition = template.Steps[i];
            AgentDefinition primaryAgent = agentsByType[stepDefinition.AgentType];

            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = primaryAgent.Id,
                StepOrder = i + 1,
                Label = stepDefinition.Label,
                StepType = stepDefinition.StepType,
                ConditionExpression = stepDefinition.ConditionExpression,
                LoopSourceExpression = stepDefinition.LoopSourceExpression,
                LoopTargetStepOrder = stepDefinition.LoopTargetStepOrder,
                MaxIterations = stepDefinition.MaxIterations,
                MinScore = stepDefinition.MinScore,
                InputMappingJson = stepDefinition.InputMappingJson,
                AgentInputContextMode = stepDefinition.AgentInputContextMode,
                SelectedPriorStepOrdersJson = stepDefinition.SelectedPriorStepOrders is { Count: > 0 }
                    ? System.Text.Json.JsonSerializer.Serialize(stepDefinition.SelectedPriorStepOrders)
                    : null,
                TrueBranchStepOrder = stepDefinition.TrueBranchStepOrder,
                FalseBranchStepOrder = stepDefinition.FalseBranchStepOrder,
                ParallelAgentIdsJson = BuildParallelAgentIdsJson(stepDefinition, agentsByType),
                ExtractConfigJson = stepDefinition.ExtractConfigJson,
                VideoAnalyzeConfigJson = stepDefinition.VideoAnalyzeConfigJson,
                VideoCompileConfigJson = stepDefinition.VideoCompileConfigJson,
                EditRoomConfigJson = stepDefinition.EditRoomConfigJson,
                GraphicsRoomConfigJson = stepDefinition.GraphicsRoomConfigJson,
                ColorGradeRoomConfigJson = stepDefinition.ColorGradeRoomConfigJson,
                ConditionalConfigJson = stepDefinition.ConditionalConfigJson,
                ReviewLoopConfigJson = stepDefinition.ReviewLoopConfigJson,
                VideoGenerateConfigJson = stepDefinition.VideoGenerateConfigJson,
                VoiceoverConfigJson = stepDefinition.VoiceoverConfigJson,
                DailiesRoomConfigJson = stepDefinition.DailiesRoomConfigJson,
                TimelineRenderConfigJson = stepDefinition.TimelineRenderConfigJson,
                DesignSystemConfigJson = stepDefinition.DesignSystemConfigJson,
                ExtractFrameConfigJson = stepDefinition.ExtractFrameConfigJson,
                AudioGenerateConfigJson = stepDefinition.AudioGenerateConfigJson,
                PinInsertConfigJson = stepDefinition.PinInsertConfigJson,
                AudioCleanupConfigJson = stepDefinition.AudioCleanupConfigJson,
                VideoEnhanceConfigJson = stepDefinition.VideoEnhanceConfigJson,
                ImageGenerateConfigJson = stepDefinition.ImageGenerateConfigJson,
                CacheMode = stepDefinition.CacheMode
            };

            steps.Add(step);
        }

        return steps;
    }

    /// <summary>
    /// A dry instance of a template: its steps exactly as provisioning would create them, built in memory and never
    /// saved or attached, so a price can be estimated for a recipe the project does not have yet. Null for an
    /// unknown key. Reads only the platform's built-in agent rows.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowStep>?> BuildDryStepsAsync(string templateKey, CancellationToken ct)
    {
        // A recipe with app inputs is priced from the steps it would be created with.
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.GetByKey(templateKey);
        if (template is null || (!AreAvatarsEnabled() && WorkflowTemplateCatalog.IsAvatarTemplate(template.Key)))
            return null;
        IReadOnlyDictionary<AgentType, AgentDefinition> agentsByType = await ResolveBuiltInAgentsByTypeAsync(template, ct);
        return BuildSteps(template, Guid.NewGuid(), agentsByType);
    }

    /// <summary>
    /// A dry app of a recipe (docs/design/app-inputs.md 28): the page it would be created with (its agent-model fields naming the
    /// built-in agents they pick for) and its dry steps, never saved. Null for an unknown key.
    /// </summary>
    public async Task<(AppManifest Manifest, IReadOnlyList<WorkflowStep> Steps)?> BuildDryAppAsync(string templateKey, CancellationToken ct)
    {
        IReadOnlyList<WorkflowTemplateDefinition> recipes = await ListRecipesAsync(ct);
        WorkflowTemplateDefinition? template = recipes.FirstOrDefault(t => string.Equals(t.Key, templateKey, StringComparison.Ordinal))
            ?? WorkflowTemplateCatalog.GetByKey(templateKey);
        if (template is null || (!AreAvatarsEnabled() && WorkflowTemplateCatalog.IsAvatarTemplate(template.Key)))
            return null;
        IReadOnlyDictionary<AgentType, AgentDefinition> agentsByType = await ResolveBuiltInAgentsByTypeAsync(template, ct);
        AppManifest? manifest = WorkflowTemplateAppInputs.ManifestForCreate(
            template, templateKey, type => agentsByType.TryGetValue(type, out AgentDefinition? agent) ? agent.Id : null);
        return manifest is null ? null : (manifest, BuildSteps(template, Guid.NewGuid(), agentsByType));
    }

    private async Task<IReadOnlyDictionary<AgentType, AgentDefinition>> ResolveBuiltInAgentsByTypeAsync(
        WorkflowTemplateDefinition template,
        CancellationToken ct)
    {
        HashSet<AgentType> requiredAgentTypes = template.Steps
            .Select(step => step.AgentType)
            .Concat(template.Steps
                .Where(step => step.ParallelAgentTypes is { Count: > 0 })
                .SelectMany(step => step.ParallelAgentTypes!))
            .Concat(template.AppInputAgents?.Values ?? [])
            .ToHashSet();

        List<AgentDefinition> builtIns = await _db.AgentDefinitions
            .Where(agent => agent.IsBuiltIn && requiredAgentTypes.Contains(agent.AgentType))
            .ToListAsync(ct);

        Dictionary<AgentType, AgentDefinition> result = builtIns
            .GroupBy(agent => agent.AgentType)
            .ToDictionary(group => group.Key, group => group.OrderBy(agent => agent.CreatedAt).First());

        AgentType[] missing = requiredAgentTypes
            .Where(type => !result.ContainsKey(type))
            .ToArray();

        if (missing.Length > 0)
            throw new InvalidOperationException($"Missing built-in agent definitions: {string.Join(", ", missing)}");

        return result;
    }

    private static string? BuildParallelAgentIdsJson(
        WorkflowTemplateStepDefinition stepDefinition,
        IReadOnlyDictionary<AgentType, AgentDefinition> agentsByType)
    {
        if (stepDefinition.ParallelAgentTypes is not { Count: > 0 })
            return null;

        List<Guid> ids = stepDefinition.ParallelAgentTypes
            .Distinct()
            .Where(type => agentsByType.ContainsKey(type))
            .Select(type => agentsByType[type].Id)
            .ToList();

        return ids.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(ids);
    }
}
