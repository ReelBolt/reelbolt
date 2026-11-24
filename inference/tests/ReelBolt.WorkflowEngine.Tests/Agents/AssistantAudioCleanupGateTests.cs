#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// QA (audio-cleanup T-013): the ProposeCleanAudio / ApplyCleanAudio paths the builder's own tests leave open:
/// a token replayed by another member, authority re-read on Apply, media of another project of the same caller,
/// every local target, a model-supplied egress flag, the card with billing on, and the gate in ask and auto mode.
/// Fakes only: no vendor, no LLM.
/// </summary>
public class AssistantAudioCleanupGateTests
{
    private sealed class Caller(Guid userId, Guid organizationId, OrganizationRole role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "qa@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
        public bool EmailVerified => true;
    }

    private sealed class Guard : IAssistantExecutionGuard
    {
        public async Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    private sealed class Executions(InferenceApiDbContext db) : IWorkflowExecutionService
    {
        public int Queued { get; private set; }

        public Task<WorkflowExecutionResponse> ExecuteAsync(
            Guid projectId, Guid workflowId, Guid initiatedByUserId, string? userRequest, ExecutionInitiator initiator,
            CancellationToken ct, bool regenerateVideoClips = false, RunChoices? runChoices = null) => throw new NotSupportedException();

        public async Task<WorkflowExecutionResponse> QueueAsync(
            Guid projectId, Guid workflowId, Guid initiatedByUserId, string? userRequest, ExecutionInitiator initiator,
            CancellationToken ct, bool regenerateVideoClips, RunChoices? runChoices, WorkflowDefinitionKind kind, Guid? executionId,
            Func<CancellationToken, Task>? beforePublish = null)
        {
            Queued++;
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                ProjectId = projectId,
                InitiatedByUserId = initiatedByUserId,
                Status = ExecutionStatus.Queued,
                StartedAt = DateTime.UtcNow,
                CorrelationId = "c"
            };
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync(ct);
            if (beforePublish is not null) await beforePublish(ct);
            return new WorkflowExecutionResponse(
                execution.Id, workflowId, "Queued", execution.StartedAt, null, 0, null, "c", null,
                new List<StepResultResponse>(), new List<ReviewScoreResponse>());
        }
    }

    private sealed class Rig : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Org => TestTenancy.OrgOf(Owner);
        public Project Project { get; }
        public ProjectFile File { get; }
        public Project OtherProject { get; }
        public ProjectFile OtherFile { get; }
        public Executions Exec { get; }
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();

        public Rig(bool auto = false)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"qa-cleanup-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            Db.ApplicationUsers.Add(new ApplicationUser { Id = Owner, Email = "qa@example.com", DisplayName = "QA", AssistantAutoApprove = auto });
            Project = AddProject("Launch Teaser");
            OtherProject = AddProject("Other Cut");
            File = AddFile(Project, "interview.mp4");
            OtherFile = AddFile(OtherProject, "secret-call.mp4");
            Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "placeholder", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            Db.SaveChanges();
            Exec = new Executions(Db);
        }

        private Project AddProject(string name)
        {
            Project p = new() { Id = Guid.NewGuid(), Name = name, OwnerId = Owner, OrganizationId = Org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(p);
            return p;
        }

        private ProjectFile AddFile(Project project, string name)
        {
            ProjectFile f = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = name,
                MimeType = "video/mp4",
                StorageKey = $"projects/{project.Id}/userFiles/{name}",
                StorageBucket = "b",
                SizeBytes = 10,
                CsamStatus = CsamScanStatus.Clean
            };
            Db.ProjectFiles.Add(f);
            Db.MediaDerivatives.Add(new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SourceProjectFileId = f.Id,
                SourceKind = MediaSourceKind.ProjectFile,
                SourceStorageKey = f.StorageKey,
                CacheKey = Guid.NewGuid().ToString("N"),
                Kind = MediaDerivativeKind.Thumbnail,
                StorageKey = "x",
                StorageBucket = "b",
                SourceProbeJson = new MediaSourceProbe(150, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
            });
            return f;
        }

        public Caller As(Guid? user = null, OrganizationRole role = OrganizationRole.Owner) => new(user ?? Owner, Org, role);

        public AudioCleanupService Service(Caller caller, bool billingOn = false)
        {
            IWorkflowCostEstimator? estimator = null;
            INonWorkflowChargeService? charges = null;
            if (billingOn)
            {
                Mock<IWorkflowCostEstimator> on = new();
                on.SetupGet(e => e.BillingEnabled).Returns(true);
                estimator = on.Object;
                charges = new Mock<INonWorkflowChargeService>().Object;
            }

            return new AudioCleanupService(Db, TestTenancy.Access(Db, caller), caller, Exec, new Guard(), new Mock<IInferenceProviderResolver>().Object,
                charges: charges, costEstimator: estimator);
        }

        public IReadOnlyList<AIFunction> Tools(Caller caller) =>
            new AssistantAudioCleanupTools(Db, TestTenancy.Access(Db, caller), Service(caller), Tokens, caller)
                .GetTools(new AssistantToolContext(caller.UserId, Project.Id));

        public async Task<JsonElement> CallAsync(string tool, AIFunctionArguments args, Caller? caller = null)
        {
            object? raw = await Tools(caller ?? As()).Single(t => t.Name == tool).InvokeAsync(args);
            return Parse(raw);
        }

        public AssistantApprovalCardBuilder Cards(bool billingOn = false)
        {
            Caller caller = As();
            return new AssistantApprovalCardBuilder(Db, TestTenancy.Access(Db, caller), caller, audioCleanup: Service(caller, billingOn));
        }

        public void Dispose() => Db.Dispose();
    }

    private static JsonElement Parse(object? raw)
    {
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static AIFunctionArguments Args(string target, Guid? fileId, string? token = null, Guid? outputId = null)
    {
        AIFunctionArguments args = new() { ["target"] = target };
        if (fileId is { } f) args["fileId"] = f;
        if (outputId is { } o) args["outputId"] = o;
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    private static void NothingRan(Rig rig)
    {
        rig.Exec.Queued.Should().Be(0);
        rig.Db.WorkflowDefinitions.Should().BeEmpty();
        rig.Db.WorkflowExecutions.Should().BeEmpty();
    }

    // ---- token binding -------------------------------------------------------------------------

    [Fact]
    public async Task A_token_minted_for_one_member_is_refused_when_another_member_of_the_same_workspace_replays_it()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeCleanAudio", Args("Hum", rig.File.Id))).GetProperty("confirmationToken").GetString()!;
        Caller colleague = rig.As(Guid.NewGuid(), OrganizationRole.Member);

        JsonElement replay = await rig.CallAsync("ApplyCleanAudio", Args("Hum", rig.File.Id, token), colleague);

        replay.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        NothingRan(rig);
    }

    [Fact]
    public async Task A_token_for_one_file_does_not_start_a_cleanup_of_another_file_or_output_kind()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeCleanAudio", Args("Hum", rig.File.Id))).GetProperty("confirmationToken").GetString()!;
        AIFunctionArguments otherKind = Args("Hum", rig.File.Id, token);
        otherKind["outputKind"] = "Audio";

        JsonElement kind = await rig.CallAsync("ApplyCleanAudio", otherKind);

        kind.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        NothingRan(rig);
    }

    // ---- authority ------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_re_reads_edit_permission_so_a_role_lost_after_the_proposal_runs_nothing()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeCleanAudio", Args("Wind", rig.File.Id))).GetProperty("confirmationToken").GetString()!;

        // Same user, same workspace, now a Billing role: no project permission at all.
        JsonElement apply = await rig.CallAsync("ApplyCleanAudio", Args("Wind", rig.File.Id, token), rig.As(role: OrganizationRole.Billing));

        apply.TryGetProperty("error", out JsonElement error).Should().BeTrue();
        error.GetString().Should().NotBeNullOrWhiteSpace();
        apply.TryGetProperty("executionId", out _).Should().BeFalse();
        NothingRan(rig);
    }

    [Fact]
    public async Task A_caller_without_edit_permission_gets_no_token()
    {
        using Rig rig = new();
        JsonElement propose = await rig.CallAsync("ProposeCleanAudio", Args("Hum", rig.File.Id), rig.As(Guid.NewGuid(), OrganizationRole.Billing));

        propose.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        propose.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        NothingRan(rig);
    }

    [Fact]
    public async Task A_file_from_another_project_of_the_same_caller_is_absent_and_its_name_never_leaks()
    {
        using Rig rig = new();
        JsonElement propose = await rig.CallAsync("ProposeCleanAudio", Args("Hum", rig.OtherFile.Id));
        JsonElement apply = await rig.CallAsync("ApplyCleanAudio", Args("Hum", rig.OtherFile.Id, "forged"));
        JsonElement output = await rig.CallAsync("ProposeCleanAudio", Args("Hum", null, outputId: Guid.NewGuid()));

        propose.GetProperty("code").GetString().Should().Be("NOT_FOUND");
        apply.GetProperty("code").GetString().Should().Be("NOT_FOUND");
        output.GetProperty("code").GetString().Should().Be("NOT_FOUND");
        propose.GetRawText().Should().NotContain("secret-call");
        NothingRan(rig);

        ApprovalVerdict card = await rig.Cards().BuildAsync("ApplyCleanAudio", Args("Hum", rig.OtherFile.Id, "t"), new AssistantToolContext(rig.Owner, rig.Project.Id), null, CancellationToken.None);
        card.Card.Should().BeNull("a file outside the project gets no card");
        card.Refusal.Should().NotBeNull();
    }

    [Fact]
    public async Task Both_fileId_and_outputId_or_neither_is_refused()
    {
        using Rig rig = new();
        JsonElement both = await rig.CallAsync("ProposeCleanAudio", Args("Hum", rig.File.Id, outputId: Guid.NewGuid()));
        JsonElement neither = await rig.CallAsync("ProposeCleanAudio", Args("Hum", null));

        both.GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
        neither.GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
        NothingRan(rig);
    }

    // ---- consent --------------------------------------------------------------------------------

    [Theory]
    [InlineData("VoiceBasic")]
    [InlineData("Hum")]
    [InlineData("Wind")]
    [InlineData("Hiss")]
    [InlineData("Echo")]
    public async Task Every_local_target_runs_without_consent_and_never_with_egress(string target)
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeCleanAudio", Args(target, rig.File.Id))).GetProperty("confirmationToken").GetString()!;

        JsonElement apply = await rig.CallAsync("ApplyCleanAudio", Args(target, rig.File.Id, token));

        apply.GetProperty("executionId").GetGuid().Should().NotBeEmpty();
        apply.GetProperty("credits").GetString().Should().Be("Free");
        rig.Exec.Queued.Should().Be(1);
        AudioCleanupService.TryParseConfig(rig.Db.WorkflowSteps.Single().AudioCleanupConfigJson)!.AllowSourceMediaEgress.Should().BeFalse();
    }

    [Theory]
    [InlineData("IsolateVoice")]
    [InlineData("RemoveMusic")]
    public async Task A_model_supplied_egress_flag_is_not_a_parameter_and_cannot_turn_a_hosted_target_on(string target)
    {
        using Rig rig = new();
        foreach (AIFunction fn in rig.Tools(rig.As()))
            fn.JsonSchema.GetRawText().Should().NotContainEquivalentOf("egress", "{0} must not accept an egress flag from the model", fn.Name);

        AIFunctionArguments args = Args(target, rig.File.Id, "any");
        args["allowSourceMediaEgress"] = true;
        JsonElement apply = await rig.CallAsync("ApplyCleanAudio", args);
        AIFunctionArguments proposeArgs = Args(target, rig.File.Id);
        proposeArgs["allowSourceMediaEgress"] = true;
        JsonElement propose = await rig.CallAsync("ProposeCleanAudio", proposeArgs);

        apply.GetProperty("code").GetString().Should().Be("NEEDS_CUSTOMER_OK");
        propose.GetProperty("code").GetString().Should().Be("NEEDS_CUSTOMER_OK");
        NothingRan(rig);
    }

    [Fact]
    public void A_workflow_proposal_cannot_switch_on_egress_for_an_AudioCleanup_step()
    {
        Guid agent = Guid.NewGuid();
        CreateWorkflowStepRequest Step(bool consent) => new(
            agent, 1, null, "Isolate the voice", StepType: "AudioCleanup",
            AudioCleanupConfigJson: $"{{\"target\":\"IsolateVoice\",\"allowSourceMediaEgress\":{(consent ? "true" : "false")}}}");

        AssistantEgressConsentGuard.Evaluate([Step(true)], baseline: null).Refusal.Should().NotBeNull("no baseline consent: a flip to on is refused");
        AssistantEgressConsentGuard.Evaluate([Step(true)], [Step(false)]).Refusal.Should().NotBeNull("off to on is refused");
        AssistantEgressConsentGuard.Evaluate([Step(false)], baseline: null).Refusal.Should().BeNull();
    }

    // ---- approval card --------------------------------------------------------------------------

    [Fact]
    public async Task With_billing_on_a_local_cleanup_card_still_says_free_and_carries_no_id()
    {
        using Rig rig = new();
        ApprovalVerdict verdict = await rig.Cards(billingOn: true).BuildAsync(
            "ApplyCleanAudio", Args("Hiss", rig.File.Id, "t"), new AssistantToolContext(rig.Owner, rig.Project.Id), null, CancellationToken.None);

        verdict.Card.Should().NotBeNull();
        verdict.Card!.Cost.Should().Be("Free");
        verdict.Card.CostCredits.Should().Be(0);
        verdict.Card.Summary.Should().Contain("remove hiss").And.Contain("interview.mp4").And.Contain("2.5 min").And.Contain("Launch Teaser");
        verdict.Card.Summary.Should().NotContain(rig.File.Id.ToString()).And.NotContain(rig.Project.Id.ToString());
    }

    // ---- the gate in front of the tool ----------------------------------------------------------

    private static AssistantApprovalGate Gate(Rig rig, List<AssistantActionFrame> frames, AssistantApprovalBroker broker, string? costLimit) =>
        new(
            new AssistantToolContext(rig.Owner, rig.Project.Id), rig.Org, rig.Db, broker, rig.Cards(billingOn: true),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AssistantApprovalGate.TimeoutKey] = "30",
                [AssistantApprovalGate.AutoApproveCostCardKey] = costLimit
            }).Build(),
            emit: (frame, _) =>
            {
                lock (frames) frames.Add(frame);
                return Task.CompletedTask;
            });

    [Fact]
    public async Task Ask_mode_holds_ApplyCleanAudio_behind_a_card_and_a_no_runs_nothing()
    {
        using Rig rig = new(auto: false);
        List<AssistantActionFrame> frames = new();
        AssistantApprovalBroker broker = new();
        AssistantApprovalGate gate = Gate(rig, frames, broker, costLimit: null);
        string token = (await rig.CallAsync("ProposeCleanAudio", Args("Echo", rig.File.Id))).GetProperty("confirmationToken").GetString()!;
        AIFunctionArguments args = Args("Echo", rig.File.Id, token);
        AIFunction apply = rig.Tools(rig.As()).Single(t => t.Name == "ApplyCleanAudio");

        Task<object?> pending = gate.RunAsync("ApplyCleanAudio", args, () => apply.InvokeAsync(args), CancellationToken.None);
        AssistantActionFrame card = await WaitForAsync(frames, AssistantActionState.Pending);

        rig.Exec.Queued.Should().Be(0, "nothing runs while the card waits");
        card.Mode.Should().Be(AssistantActionMode.Ask);
        broker.Answer(card.Id, rig.Owner, rig.Org, allow: false, alwaysAllow: false);
        await pending;
        NothingRan(rig);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.01")]
    public async Task Auto_mode_runs_a_free_cleanup_without_a_card_because_it_never_crosses_the_cost_limit(string? limit)
    {
        using Rig rig = new(auto: true);
        List<AssistantActionFrame> frames = new();
        AssistantApprovalBroker broker = new();
        AssistantApprovalGate gate = Gate(rig, frames, broker, limit);
        string token = (await rig.CallAsync("ProposeCleanAudio", Args("Hum", rig.File.Id))).GetProperty("confirmationToken").GetString()!;
        gate.NoteProposal("ProposeCleanAudio", JsonSerializer.Serialize(new { confirmationToken = token, credits = "Free" }));
        AIFunctionArguments args = Args("Hum", rig.File.Id, token);
        AIFunction apply = rig.Tools(rig.As()).Single(t => t.Name == "ApplyCleanAudio");

        JsonElement result = Parse(await gate.RunAsync("ApplyCleanAudio", args, () => apply.InvokeAsync(args), CancellationToken.None));

        result.GetProperty("executionId").GetGuid().Should().NotBeEmpty();
        rig.Exec.Queued.Should().Be(1);
        broker.OpenCount.Should().Be(0);
        frames.Should().OnlyContain(f => f.Mode == AssistantActionMode.Auto && f.AutoNote == null);
    }

    [Fact]
    public async Task Auto_mode_answers_a_hosted_target_in_the_tools_place_and_never_runs_it()
    {
        using Rig rig = new(auto: true);
        List<AssistantActionFrame> frames = new();
        AssistantApprovalBroker broker = new();
        AssistantApprovalGate gate = Gate(rig, frames, broker, costLimit: null);
        int ran = 0;

        JsonElement answer = Parse(await gate.RunAsync(
            "ApplyCleanAudio", Args("IsolateVoice", rig.File.Id, "t"),
            () => { ran++; return ValueTask.FromResult<object?>("{\"ok\":true}"); }, CancellationToken.None));

        ran.Should().Be(0);
        answer.GetProperty("code").GetString().Should().Be("NEEDS_CUSTOMER_OK");
        NothingRan(rig);
    }

    private static async Task<AssistantActionFrame> WaitForAsync(List<AssistantActionFrame> frames, string state)
    {
        for (int i = 0; i < 500; i++)
        {
            lock (frames)
            {
                AssistantActionFrame? found = frames.FirstOrDefault(f => f.State == state);
                if (found is not null) return found;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No '{state}' frame arrived.");
    }
}
