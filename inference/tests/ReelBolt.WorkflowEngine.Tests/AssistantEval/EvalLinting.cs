using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>
/// The linter's view of the goal's stub project: providers by capability, plan entitlements, files by role, design
/// status and the one consented voice, all read from the fixture's <c>project</c> block.
/// </summary>
internal sealed class EvalLintFacts(EvalProject project, Guid? voiceId) : IWorkflowLintFacts
{
    private bool Has(string capability) => project.Providers.Contains(capability, StringComparer.OrdinalIgnoreCase);

    public Task<bool?> ProviderAvailableAsync(InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct) =>
        Task.FromResult<bool?>(capability switch
        {
            InferenceProviderCapability.Chat or InferenceProviderCapability.Decision => Has("chat"),
            InferenceProviderCapability.Transcription => Has("transcription"),
            InferenceProviderCapability.Vision => Has("vision"),
            InferenceProviderCapability.VideoGeneration => Has("videoGeneration"),
            InferenceProviderCapability.SpeechSynthesis => Has("voice"),
            _ => null,
        });

    public Task<PlanGateViolation?> StepEntitlementAsync(StepType stepType, CancellationToken ct)
    {
        // plans.catalog.json: Trial has neither video nor audio generation; Creator has audio only; Pro and up have both.
        bool trial = project.Plan.Equals("trial", StringComparison.OrdinalIgnoreCase);
        bool creator = project.Plan.Equals("creator", StringComparison.OrdinalIgnoreCase);
        bool blocked = stepType switch
        {
            StepType.VideoGenerate => trial || creator,
            StepType.AudioGenerate => trial,
            _ => false,
        };
        return Task.FromResult<PlanGateViolation?>(blocked
            ? new PlanGateViolation(PlanGate.NotEntitledCode, stepType.ToString(), "not on this plan", 402)
            : null);
    }

    public Task<PlanGateViolation?> ResolutionEntitlementAsync(int shortSide, CancellationToken ct) => Task.FromResult<PlanGateViolation?>(null);

    public Task<IReadOnlyDictionary<RunBriefFileRole, int>?> FileCountsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<RunBriefFileRole, int>?>(new Dictionary<RunBriefFileRole, int>
        {
            [RunBriefFileRole.Video] = project.VideoClips,
            [RunBriefFileRole.Image] = project.Images,
            [RunBriefFileRole.Music] = project.MusicTrack ? 1 : 0,
            [RunBriefFileRole.Sfx] = project.SfxFiles ? 1 : 0,
            [RunBriefFileRole.Code] = project.Code == "none" ? 0 : 2,
            [RunBriefFileRole.Document] = 0,
        });

    public Task<bool?> ProjectFileExistsAsync(Guid fileId, CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task<bool?> HasApprovedDesignSystemAsync(CancellationToken ct) =>
        Task.FromResult<bool?>(project.DesignSystem.Equals("approved", StringComparison.OrdinalIgnoreCase));

    public Task<LintVoiceFact?> ProjectVoiceAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<LintVoiceFact?>(voiceId == id
            ? new LintVoiceFact(true, true, null, "Presenter voice", null)
            : new LintVoiceFact(false, false, null, null, null));

    public Task<double?> SourceVideoSecondsAsync(CancellationToken ct) => Task.FromResult<double?>(null);
}

/// <summary>Runs the real <see cref="WorkflowLinter"/> over the first accepted proposal, as the product would before a run.</summary>
internal static class EvalLinting
{
    private sealed class FixedSource(IWorkflowLintFacts facts) : IWorkflowLintFactsSource
    {
        public IWorkflowLintFacts Create(Guid? organizationId, Guid projectId) => facts;
    }

    public static async Task<IReadOnlySet<string>> LintAsync(
        EvalProject project, Guid? voiceId, string stepsJson, IReadOnlyDictionary<Guid, AgentDefinition> agents,
        IReadOnlyDictionary<string, string> brief, CancellationToken ct = default)
    {
        List<CreateWorkflowStepRequest>? requests;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stepsJson);
            requests = doc.RootElement.TryGetProperty("steps", out JsonElement s)
                ? s.Deserialize<List<CreateWorkflowStepRequest>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;
        }
        catch (JsonException)
        {
            return new HashSet<string>();
        }
        if (requests is null or { Count: 0 }) return new HashSet<string>();

        var facts = new EvalLintFacts(project, voiceId);
        var linter = new WorkflowLinter(new FixedSource(facts));
        WorkflowLintReport report = await linter.LintAsync(
            new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), WorkflowLinter.FromRequests(requests, agents), ToBrief(brief), null, facts), ct);
        return report.Findings.Select(f => f.RuleId).ToHashSet(StringComparer.Ordinal);
    }

    public static RunBrief? ToBrief(IReadOnlyDictionary<string, string> b)
    {
        if (b.Count == 0) return null;
        b.TryGetValue("language", out string? language);
        b.TryGetValue("format", out string? format);
        b.TryGetValue("resolution", out string? resolution);
        b.TryGetValue("designSystem", out string? design);
        int? length = b.TryGetValue("lengthSec", out string? l) && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
        RunBriefVoice? voice = b.TryGetValue("voice", out string? v)
            ? new RunBriefVoice(v.Equals("narrated", StringComparison.OrdinalIgnoreCase) ? RunBriefVoiceMode.Narrated : RunBriefVoiceMode.None,
                b.TryGetValue("voiceId", out string? id) && Guid.TryParse(id, out Guid g) ? g : null)
            : null;
        return new RunBrief(Language: language, LengthSec: length, Format: format, Resolution: resolution, DesignSystem: design, Voice: voice);
    }
}
