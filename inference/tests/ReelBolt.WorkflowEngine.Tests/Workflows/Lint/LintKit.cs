using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>Builders for the lint tests: steps by type with their config, and configurable facts.</summary>
internal static class LintKit
{
    public static WorkflowStep Agent(int order, AgentType type, string? label = null) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.Agent,
        Label = label,
        AgentDefinitionId = Guid.NewGuid(),
        AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = type, Name = type.ToString() }
    };

    public static WorkflowStep Of(int order, StepType type, string? configJson = null, int? loopTarget = null, AgentType agent = AgentType.VideoTransform)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            LoopTargetStepOrder = loopTarget,
            AgentDefinitionId = Guid.NewGuid(),
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = agent, Name = agent.ToString() }
        };
        switch (type)
        {
            case StepType.Voiceover: step.VoiceoverConfigJson = configJson; break;
            case StepType.VideoCompile: step.VideoCompileConfigJson = configJson; break;
            case StepType.VideoAnalyze: step.VideoAnalyzeConfigJson = configJson; break;
            case StepType.VideoGenerate: step.VideoGenerateConfigJson = configJson; break;
            case StepType.AudioGenerate: step.AudioGenerateConfigJson = configJson; break;
            case StepType.ImageGenerate: step.ImageGenerateConfigJson = configJson; break;
            case StepType.DesignSystem: step.DesignSystemConfigJson = configJson; break;
            case StepType.ThumbnailGenerate: step.ThumbnailGenerateConfigJson = configJson; break;
            case StepType.ReviewLoop: step.ReviewLoopConfigJson = configJson; break;
        }

        return step;
    }

    public static WorkflowStep Voiceover(int order, string configJson) => Of(order, StepType.Voiceover, configJson);

    public static WorkflowStep Compile(int order, string configJson) => Of(order, StepType.VideoCompile, configJson);

    public static WorkflowStep ReviewLoopAt(int order, int? target) => Of(order, StepType.ReviewLoop, null, target, AgentType.ReviewAgent);

    public static WorkflowLinter Linter(IWorkflowLintFacts? facts = null, IAppManifestOrphanSource? orphans = null) =>
        new(new FixedFactsSource(facts ?? new FakeFacts()), orphans);

    public static async Task<IReadOnlyList<LintFinding>> LintAsync(
        IEnumerable<WorkflowStep> steps, RunBrief? brief = null, FakeFacts? facts = null, IAppManifestOrphanSource? orphans = null)
    {
        facts ??= new FakeFacts();
        WorkflowLintReport report = await Linter(facts, orphans).LintAsync(
            new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), [.. steps], brief, null, facts), CancellationToken.None);
        return report.Findings;
    }

    public static IEnumerable<LintFinding> Rule(this IReadOnlyList<LintFinding> findings, string ruleId) =>
        findings.Where(f => f.RuleId == ruleId);

    public sealed class FixedFactsSource(IWorkflowLintFacts facts) : IWorkflowLintFactsSource
    {
        public IWorkflowLintFacts Create(Guid? organizationId, Guid projectId) => facts;
    }

    /// <summary>Facts that say "everything is fine" until a test breaks one.</summary>
    public sealed class FakeFacts : IWorkflowLintFacts
    {
        public HashSet<InferenceProviderCapability> MissingProviders { get; } = [];

        public HashSet<StepType> UnentitledSteps { get; } = [];

        public int? UnentitledAboveShortSide { get; set; }

        public Dictionary<RunBriefFileRole, int>? FileCounts { get; set; } =
            Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 3);

        public int ArchiveFiles { get; set; }

        public Task<int> ArchiveFilesAsync(CancellationToken ct) => Task.FromResult(ArchiveFiles);

        public int UnpackingArchives { get; set; }

        public Task<int> UnpackingArchivesAsync(CancellationToken ct) => Task.FromResult(UnpackingArchives);

        public bool? DesignApproved { get; set; } = true;

        public Dictionary<Guid, LintVoiceFact> Voices { get; } = [];

        /// <summary>Avatar facts by id; an avatar absent from this map is unknown (no finding).</summary>
        public Dictionary<Guid, LintAvatarFact> Avatars { get; } = [];

        public Task<LintAvatarFact?> ProjectAvatarAsync(Guid avatarId, CancellationToken ct) =>
            Task.FromResult(Avatars.GetValueOrDefault(avatarId));

        /// <summary>The kind of the video provider a step resolves to; null (the default) is unknown, so WL034 never fires.</summary>
        public InferenceProviderKind? VideoProviderKind { get; set; }

        public Task<InferenceProviderKind?> VideoGenerationProviderKindAsync(Guid? explicitProviderId, CancellationToken ct) =>
            Task.FromResult(VideoProviderKind);

        public HashSet<Guid> MissingFiles { get; } = [];

        /// <summary>Media verdicts by file id; a file absent from this map is usable.</summary>
        public Dictionary<Guid, LintMediaVerdict> MediaVerdicts { get; } = [];

        public double? SourceSeconds { get; set; }

        public Task<bool?> ProviderAvailableAsync(InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct) =>
            Task.FromResult<bool?>(!MissingProviders.Contains(capability));

        /// <summary>When set, plan answers come from the real plan rules over this entitlement set.</summary>
        public OrganizationEntitlementSet? Plan { get; set; }

        public Task<PlanGateViolation?> StepEntitlementAsync(StepType stepType, CancellationToken ct) =>
            Task.FromResult(Plan is { } plan
                ? PlanGate.CheckStep(stepType, plan)
                : UnentitledSteps.Contains(stepType)
                    ? new PlanGateViolation(PlanGate.NotEntitledCode, "x", "not on plan", 402)
                    : null);

        public Task<PlanGateViolation?> ResolutionEntitlementAsync(int shortSide, CancellationToken ct) =>
            Task.FromResult(Plan is { } set
                ? PlanGate.CheckResolution(shortSide, set)
                : UnentitledAboveShortSide is { } cap && shortSide > cap
                ? new PlanGateViolation(PlanGate.NotEntitledCode, "resolution", "too high", 402)
                : null);

        public Task<IReadOnlyDictionary<RunBriefFileRole, int>?> FileCountsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<RunBriefFileRole, int>?>(FileCounts);

        public Task<bool?> ProjectFileExistsAsync(Guid fileId, CancellationToken ct) =>
            Task.FromResult<bool?>(!MissingFiles.Contains(fileId));

        public Task<LintMediaVerdict?> ProjectMediaVerdictAsync(Guid fileId, CancellationToken ct) =>
            Task.FromResult<LintMediaVerdict?>(MediaVerdicts.GetValueOrDefault(fileId, LintMediaVerdict.Usable));

        public Task<bool?> HasApprovedDesignSystemAsync(CancellationToken ct) => Task.FromResult(DesignApproved);

        public Task<LintVoiceFact?> ProjectVoiceAsync(Guid voiceId, CancellationToken ct) =>
            Task.FromResult(Voices.GetValueOrDefault(voiceId));

        public Task<double?> SourceVideoSecondsAsync(CancellationToken ct) => Task.FromResult(SourceSeconds);
    }

    /// <summary>A template step as the stored step the provisioning service would create (agent resolved by type).</summary>
    public static List<WorkflowStep> FromTemplate(WorkflowTemplateDefinition template)
    {
        Dictionary<AgentType, AgentDefinition> agents = [];
        List<WorkflowStep> steps = [];
        for (int i = 0; i < template.Steps.Count; i++)
        {
            WorkflowTemplateStepDefinition s = template.Steps[i];
            if (!agents.TryGetValue(s.AgentType, out AgentDefinition? agent))
                agents[s.AgentType] = agent = new AgentDefinition { Id = Guid.NewGuid(), AgentType = s.AgentType, Name = s.AgentType.ToString(), IsBuiltIn = true };
            steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent,
                StepOrder = i + 1,
                Label = s.Label,
                StepType = s.StepType,
                ConditionExpression = s.ConditionExpression,
                LoopSourceExpression = s.LoopSourceExpression,
                LoopTargetStepOrder = s.LoopTargetStepOrder,
                MaxIterations = s.MaxIterations,
                MinScore = s.MinScore,
                InputMappingJson = s.InputMappingJson,
                AgentInputContextMode = s.AgentInputContextMode,
                SelectedPriorStepOrdersJson = s.SelectedPriorStepOrders is { Count: > 0 }
                    ? System.Text.Json.JsonSerializer.Serialize(s.SelectedPriorStepOrders)
                    : null,
                TrueBranchStepOrder = s.TrueBranchStepOrder,
                FalseBranchStepOrder = s.FalseBranchStepOrder,
                ExtractConfigJson = s.ExtractConfigJson,
                VideoAnalyzeConfigJson = s.VideoAnalyzeConfigJson,
                VideoCompileConfigJson = s.VideoCompileConfigJson,
                EditRoomConfigJson = s.EditRoomConfigJson,
                GraphicsRoomConfigJson = s.GraphicsRoomConfigJson,
                ColorGradeRoomConfigJson = s.ColorGradeRoomConfigJson,
                ConditionalConfigJson = s.ConditionalConfigJson,
                ReviewLoopConfigJson = s.ReviewLoopConfigJson,
                VideoGenerateConfigJson = s.VideoGenerateConfigJson,
                VoiceoverConfigJson = s.VoiceoverConfigJson,
                DailiesRoomConfigJson = s.DailiesRoomConfigJson,
                TimelineRenderConfigJson = s.TimelineRenderConfigJson,
                DesignSystemConfigJson = s.DesignSystemConfigJson,
                ExtractFrameConfigJson = s.ExtractFrameConfigJson,
                AudioGenerateConfigJson = s.AudioGenerateConfigJson,
                PinInsertConfigJson = s.PinInsertConfigJson,
                ThumbnailGenerateConfigJson = s.ThumbnailGenerateConfigJson,
                CacheMode = s.CacheMode
            });
        }

        return steps;
    }
}
