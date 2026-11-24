#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// The three states a project's earlier outputs can be in, and the rows to try them on. Every surface test seeds one
/// project with an earlier output, a later output and an upload, then turns the project into each state.
/// </summary>
public enum SurfaceState
{
    /// <summary>Marker set: outputs made before it are not served, later ones and uploads are.</summary>
    Held,

    /// <summary>No marker: everything is served.</summary>
    Clear,

    /// <summary>A file carries the moderation flag but the project has no marker: outputs are served.</summary>
    ModerationOnly,
}

public sealed class SurfaceUser(Guid userId) : ICurrentUser
{
    public Guid UserId { get; } = userId;
    public string Email { get; } = "owner@example.com";
    public bool IsAuthenticated => true;
    public bool IsPlatformAdmin => false;
    public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
    public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
}

/// <summary>A context whose project read fails, as a failed marker read does; every other read works.</summary>
public sealed class ProjectReadFailsContext(DbContextOptions<InferenceApiDbContext> options, Type? failing = null) : InferenceApiDbContext(options)
{
    private readonly Type _failing = failing ?? typeof(Project);

    public override DbSet<TEntity> Set<TEntity>() =>
        typeof(TEntity) == _failing ? throw new InvalidOperationException("read failed") : base.Set<TEntity>();
}

/// <summary>A context whose read of the output marker fails and nothing else does.</summary>
public sealed class MarkerReadFailsContext(DbContextOptions<InferenceApiDbContext> options) : InferenceApiDbContext(options)
{
    public override DbSet<TEntity> Set<TEntity>()
    {
        if (typeof(TEntity) == typeof(Project) && new System.Diagnostics.StackTrace().GetFrames().Any(f =>
                f.GetMethod()?.DeclaringType is { } t && (t == typeof(ReelBolt.Shared.Storage.ProjectOutputHold) || t.DeclaringType == typeof(ReelBolt.Shared.Storage.ProjectOutputHold))))
            throw new InvalidOperationException("read failed");
        return base.Set<TEntity>();
    }
}

/// <summary>One project with an earlier output, a later output and an upload, in the state a test asks for.</summary>
public sealed class SurfaceWorld
{
    public static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public DbContextOptions<InferenceApiDbContext> Options { get; }
    public InferenceApiDbContext Db { get; }
    public Guid UserId { get; } = Guid.NewGuid();
    public SurfaceUser User { get; }
    public Project Project { get; }
    public WorkflowDefinition Workflow { get; }
    public AgentDefinition Agent { get; }
    public WorkflowStep Step { get; }
    public WorkflowExecution EarlierRun { get; }
    public WorkflowExecution LaterRun { get; }
    public WorkflowStepResult EarlierResult { get; }
    public WorkflowStepResult LaterResult { get; }
    public string EarlierKey { get; }
    public ProjectFile EarlierFile { get; }
    public ProjectFile LaterFile { get; }
    public ProjectFile Upload { get; }

    public SurfaceWorld()
    {
        Options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options;
        Db = new InferenceApiDbContext(Options);
        User = new SurfaceUser(UserId);
        Project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Reel",
            OwnerId = UserId,
            OrganizationId = TestTenancy.OrgOf(UserId),
            CreatedAt = Marker.AddDays(-30),
            UpdatedAt = Marker.AddDays(-30),
        };
        Workflow = new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = Project.Id, Name = "Reel flow", CreatedAt = Marker.AddDays(-30), UpdatedAt = Marker.AddDays(-30) };
        Agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "Author", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = Marker.AddDays(-30) };
        Step = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow.Id, AgentDefinitionId = Agent.Id, StepOrder = 1, StepType = StepType.VideoCompile, Label = "Compile" };
        EarlierRun = Run(Marker.AddHours(-3));
        LaterRun = Run(Marker.AddDays(2));
        EarlierResult = Result(EarlierRun, "earlier.mp4", Marker.AddHours(-2));
        LaterResult = Result(LaterRun, "later.mp4", Marker.AddDays(2).AddMinutes(10));
        EarlierKey = EarlierResult.OutputStorageKey!;
        EarlierFile = File("outputFiles", "earlier.mp4", Marker.AddHours(-2), produced: Marker.AddHours(-2));
        LaterFile = File("outputFiles", "later.mp4", Marker.AddDays(2).AddMinutes(10), produced: Marker.AddDays(2).AddMinutes(10));
        Upload = File("userFiles", "mine.mp4", Marker.AddHours(-5), produced: null);
        Db.Projects.Add(Project);
        Db.WorkflowDefinitions.Add(Workflow);
        Db.AgentDefinitions.Add(Agent);
        Db.WorkflowSteps.Add(Step);
        Db.WorkflowExecutions.AddRange(EarlierRun, LaterRun);
        Db.WorkflowStepResults.AddRange(EarlierResult, LaterResult);
        Db.ProjectFiles.AddRange(EarlierFile, LaterFile, Upload);
        Db.SaveChanges();
    }

    private WorkflowExecution Run(DateTime started) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Workflow.Id,
        ProjectId = Project.Id,
        Status = ExecutionStatus.Passed,
        StartedAt = started,
        FirstStartedAt = started,
        ResultJson = "{\"done\":true}",
        CompletedAt = started.AddMinutes(20),
    };

    private WorkflowStepResult Result(WorkflowExecution run, string name, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowExecutionId = run.Id,
        WorkflowStepId = Step.Id,
        Status = StepStatus.Completed,
        OutputStorageKey = $"projects/{Project.Id}/outputFiles/{name}",
        Output = "result text",
        OutputJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            a = 1,
            timelineStorageKey = $"projects/{Project.Id}/agentFiles/video-analysis/{run.Id:D}/step-1-timeline.json",
            subtitleStorageKeys = new
            {
                srt = $"projects/{Project.Id}/agentFiles/video-analysis/{run.Id:D}/step-1-subtitles.srt",
                vtt = $"projects/{Project.Id}/agentFiles/video-analysis/{run.Id:D}/step-1-subtitles.vtt",
            },
        }),
        ArtifactStorageKey = $"projects/{Project.Id}/agentFiles/video-analysis/{run.Id:D}/step-1.json",
        TimelineStorageKey = $"projects/{Project.Id}/agentFiles/video-analysis/{run.Id:D}/step-1-timeline.json",
        ExecutedAt = at,
        CompletedAt = at,
    };

    private ProjectFile File(string category, string name, DateTime uploadedAt, DateTime? produced) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Project.Id,
        OriginalFileName = name,
        Category = category,
        StorageKey = $"projects/{Project.Id}/{category}/{name}",
        StorageBucket = "reelforge",
        MimeType = "video/mp4",
        SizeBytes = 8,
        UploadedAt = uploadedAt,
        OutputProducedAt = produced,
        CsamStatus = CsamScanStatus.Clean,
    };

    /// <summary>Puts the project in <paramref name="state"/>. Call before building the thing under test.</summary>
    public SurfaceWorld In(SurfaceState state)
    {
        switch (state)
        {
            case SurfaceState.Held:
                Project.CsamOutputHoldAt = Marker;
                break;
            case SurfaceState.ModerationOnly:
                Db.ProjectFiles.Add(new ProjectFile
                {
                    Id = Guid.NewGuid(),
                    ProjectId = Project.Id,
                    OriginalFileName = "flagged.mp4",
                    Category = "userFiles",
                    StorageKey = $"projects/{Project.Id}/userFiles/flagged.mp4",
                    StorageBucket = "reelforge",
                    MimeType = "video/mp4",
                    SizeBytes = 8,
                    UploadedAt = Marker.AddDays(-6),
                    ModerationStatus = FileModerationStatus.Quarantined,
                });
                break;
        }
        Db.SaveChanges();
        return this;
    }

    public ProjectAccessResult Found => new(ProjectAccessOutcome.Found, Project);

    public IProjectAccessService Access()
    {
        Mock<IProjectAccessService> access = new();
        access.Setup(a => a.GetAsync(Project.Id, It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>())).ReturnsAsync(Found);
        return access.Object;
    }

    /// <summary>A second context on the same rows whose marker read fails.</summary>
    public ProjectReadFailsContext FailingDb() => new(Options);

    /// <summary>A second context on the same rows where only the marker read fails.</summary>
    public MarkerReadFailsContext MarkerFailingDb() => new(Options);

    public static IEnumerable<object[]> ServedStates() => [[SurfaceState.Clear], [SurfaceState.ModerationOnly]];
}
