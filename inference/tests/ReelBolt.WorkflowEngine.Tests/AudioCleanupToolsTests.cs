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

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>ProposeCleanAudio / ApplyCleanAudio: token binding, consent refusal for hosted targets, authority, the card and the MCP projection.</summary>
public class AudioCleanupToolsTests
{
    private sealed class Caller(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
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
        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public ProjectFile File { get; }
        public Executions Exec { get; }
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();

        public Rig()
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"cleanup-tools-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            File = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = "interview.mp4",
                MimeType = "video/mp4",
                StorageKey = $"projects/{Project.Id}/userFiles/interview.mp4",
                StorageBucket = "b",
                SizeBytes = 10,
                CsamStatus = CsamScanStatus.Clean
            };
            Db.Projects.Add(Project);
            Db.ProjectFiles.Add(File);
            Db.MediaDerivatives.Add(new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                SourceProjectFileId = File.Id,
                SourceKind = MediaSourceKind.ProjectFile,
                SourceStorageKey = File.StorageKey,
                CacheKey = Guid.NewGuid().ToString("N"),
                Kind = MediaDerivativeKind.Thumbnail,
                StorageKey = "x",
                StorageBucket = "b",
                SourceProbeJson = new MediaSourceProbe(150, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
            });
            Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "placeholder", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            Db.SaveChanges();
            Exec = new Executions(Db);
        }

        public AudioCleanupService Service(Guid? user = null)
        {
            Caller caller = new(user ?? User);
            return new AudioCleanupService(Db, TestTenancy.Access(Db, caller), caller, Exec, new Guard(), new Mock<IInferenceProviderResolver>().Object);
        }

        public AssistantAudioCleanupTools Tools(Guid? user = null)
        {
            Caller caller = new(user ?? User);
            return new AssistantAudioCleanupTools(Db, TestTenancy.Access(Db, caller), Service(user), Tokens, caller);
        }

        public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, Guid? user = null)
        {
            Guid who = user ?? User;
            AIFunction fn = Tools(who).GetTools(new AssistantToolContext(who, Project.Id)).Single(t => t.Name == tool);
            object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public Dictionary<string, object?> Args(string target = "Hum", string? token = null, Guid? file = null) =>
            new(token is null ? [new("target", target), new("fileId", file ?? File.Id)] : [new("target", target), new("fileId", file ?? File.Id), new("confirmationToken", token)]);

        public AssistantApprovalCardBuilder Cards()
        {
            Caller caller = new(User);
            return new AssistantApprovalCardBuilder(Db, TestTenancy.Access(Db, caller), caller, audioCleanup: Service());
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Propose_returns_a_token_a_free_price_and_writes_nothing()
    {
        using Rig rig = new();
        JsonElement result = await rig.CallAsync("ProposeCleanAudio", rig.Args("Hum"));

        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("credits").GetString().Should().Be("Free");
        result.GetProperty("minutes").GetDouble().Should().Be(2.5);
        result.GetProperty("summary").GetString().Should().Contain("interview.mp4").And.Contain("Launch Teaser");
        rig.Db.WorkflowDefinitions.Should().BeEmpty();
        rig.Exec.Queued.Should().Be(0);
    }

    [Fact]
    public async Task Apply_with_the_proposals_token_starts_the_run_and_returns_the_execution_id_without_waiting()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeCleanAudio", rig.Args("Wind"))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await rig.CallAsync("ApplyCleanAudio", rig.Args("Wind", token));

        result.GetProperty("executionId").GetGuid().Should().Be(rig.Db.WorkflowExecutions.Single().Id);
        result.GetProperty("state").GetString().Should().Be("queued");
        rig.Exec.Queued.Should().Be(1);
        WorkflowStep step = rig.Db.WorkflowSteps.Single();
        step.AudioCleanupConfigJson.Should().Contain("Wind");
        AudioCleanupService.TryParseConfig(step.AudioCleanupConfigJson)!.AllowSourceMediaEgress.Should().BeFalse("the tool never turns egress on");
    }

    [Fact]
    public async Task A_tampered_argument_or_a_fabricated_token_is_refused_and_nothing_runs()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeCleanAudio", rig.Args("Hum"))).GetProperty("confirmationToken").GetString()!;

        JsonElement changed = await rig.CallAsync("ApplyCleanAudio", rig.Args("Hiss", token));
        JsonElement fabricated = await rig.CallAsync("ApplyCleanAudio", rig.Args("Hum", "nope"));

        changed.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        fabricated.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        rig.Exec.Queued.Should().Be(0);
        rig.Db.WorkflowDefinitions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("IsolateVoice")]
    [InlineData("RemoveMusic")]
    public async Task A_hosted_target_answers_needs_the_customers_ok_in_the_editor_and_runs_nothing(string target)
    {
        using Rig rig = new();

        JsonElement propose = await rig.CallAsync("ProposeCleanAudio", rig.Args(target));
        JsonElement apply = await rig.CallAsync("ApplyCleanAudio", rig.Args(target, "any"));

        foreach (JsonElement answer in new[] { propose, apply })
        {
            answer.GetProperty("code").GetString().Should().Be("NEEDS_CUSTOMER_OK");
            answer.GetProperty("error").GetString().Should().Contain("customer's OK in the editor");
            answer.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        }

        rig.Exec.Queued.Should().Be(0);
        rig.Db.WorkflowDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_the_caller_cannot_edit_and_media_that_is_not_the_projects_are_absent()
    {
        using Rig rig = new();
        Guid stranger = Guid.NewGuid();

        JsonElement foreign = await rig.CallAsync("ProposeCleanAudio", rig.Args("Hum"), stranger);
        JsonElement missing = await rig.CallAsync("ProposeCleanAudio", rig.Args("Hum", file: Guid.NewGuid()));

        foreign.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        foreign.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        missing.GetProperty("code").GetString().Should().Be("NOT_FOUND");
        rig.Exec.Queued.Should().Be(0);
    }

    [Fact]
    public async Task The_approval_card_names_the_target_the_source_the_minutes_and_says_free_and_a_hosted_target_gets_no_card()
    {
        using Rig rig = new();
        AIFunctionArguments args = new()
        {
            ["target"] = "Echo",
            ["fileId"] = rig.File.Id.ToString(),
            ["confirmationToken"] = "t"
        };

        ApprovalVerdict verdict = await rig.Cards().BuildAsync("ApplyCleanAudio", args, new AssistantToolContext(rig.User, rig.Project.Id), null, CancellationToken.None);

        verdict.Card.Should().NotBeNull();
        verdict.Card!.Summary.Should().Contain("remove echo").And.Contain("interview.mp4").And.Contain("2.5 min").And.Contain("Free").And.Contain("Launch Teaser");
        verdict.Card.Cost.Should().Be("Free");
        verdict.Card.CostCredits.Should().Be(0);

        AIFunctionArguments hosted = new() { ["target"] = "IsolateVoice", ["fileId"] = rig.File.Id.ToString() };
        ApprovalVerdict refused = await rig.Cards().BuildAsync("ApplyCleanAudio", hosted, new AssistantToolContext(rig.User, rig.Project.Id), null, CancellationToken.None);
        refused.Card.Should().BeNull();
        refused.Answer.Should().Contain("NEEDS_CUSTOMER_OK").And.Contain("editor");
    }

    [Fact]
    public void The_gate_asks_for_ApplyCleanAudio_and_the_mcp_surface_collapses_the_pair_to_one_destructive_tool_with_a_title()
    {
        AssistantApprovalCatalog.IsGated("ApplyCleanAudio").Should().BeTrue();
        AssistantApprovalCatalog.KnownTools.Should().Contain("ApplyCleanAudio");

        using Rig rig = new();
        Caller caller = new(rig.User);
        IReadOnlyList<AIFunction> tools = rig.Tools().GetTools(new AssistantToolContext(rig.User, rig.Project.Id));
        McpToolDescriptor descriptor = AssistantMcpToolSurface
            .Project(tools, new HashSet<string>(StringComparer.Ordinal)).Single();

        descriptor.Name.Should().Be("CleanAudio");
        descriptor.Annotation.Title.Should().Be("Clean the audio");
        descriptor.Annotation.DestructiveHint.Should().BeTrue();
        descriptor.Annotation.ReadOnlyHint.Should().BeFalse();
        _ = caller;
    }
}
