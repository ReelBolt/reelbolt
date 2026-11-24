#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.GitHub;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>A clock that stands still unless a test moves it.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class OutputsUser(Guid userId, Guid? organizationId, OrganizationRole? role = OrganizationRole.Member) : ICurrentUser
{
    public Guid UserId { get; } = userId;
    public string Email => "owner@example.com";
    public bool IsAuthenticated => true;
    public bool IsPlatformAdmin => false;
    public Guid? OrganizationId { get; } = organizationId;
    public OrganizationRole? OrgRole { get; } = role;
}

/// <summary>
/// One organization with one member and one project, holding a render (step result under
/// <c>outputFiles/</c>) and an uploaded file, over an in-memory object store that also answers the S3
/// calls the ranged streamer makes — so every byte a test sees came from the same place.
/// </summary>
internal sealed class OutputsWorld : IDisposable
{
    public const string Bucket = "reelbolt-canary-bucket";
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    public InferenceApiDbContext Db { get; }
    public InMemoryFileStorage Storage { get; } = new();
    public Mock<IAmazonS3> S3 { get; } = new();
    public FixedClock Clock { get; } = new(Now);
    public Guid UserId { get; } = Guid.NewGuid();
    public Guid OrgId { get; } = Guid.NewGuid();
    public Project Project { get; }
    public WorkflowExecution Execution { get; }
    public WorkflowStep Step { get; }
    public WorkflowStepResult Render { get; }
    public ProjectFile File { get; }
    public string RenderKey { get; }

    /// <summary>
    /// S-LFR-01: an armed interceptor makes the next <c>SaveChanges</c> throw, which is how a save failure
    /// is modelled without a real database. Armed by a copier once its rival row is committed, so the tool's
    /// own save — not the rival's — is the one that fails.
    /// </summary>
    internal sealed class FailNextSaveInterceptor : SaveChangesInterceptor
    {
        private bool _armed;
        private int _remaining;

        /// <summary>Saves to let through after being armed before one fails (0 = the next one, the default).</summary>
        public int SkipSaves { get; set; }

        public bool Armed
        {
            get => _armed;
            set
            {
                _armed = value;
                if (value) _remaining = SkipSaves;
            }
        }

        private void MaybeFail()
        {
            if (!_armed)
                return;
            if (_remaining > 0)
            {
                _remaining--;
                return;
            }

            Armed = false;
            throw new DbUpdateException("simulated save failure");
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            MaybeFail();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaybeFail();
            return ValueTask.FromResult(result);
        }
    }

    public OutputsWorld(SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString());
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        Db = new InferenceApiDbContext(options.Options);

        Db.ApplicationUsers.Add(new ApplicationUser { Id = UserId, Email = "owner@example.com", DisplayName = "Owner" });
        Db.Organizations.Add(new Organization { Id = OrgId, Name = "Studio", Slug = $"studio-{OrgId:N}", Kind = OrganizationKind.Team });
        Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = OrgId, UserId = UserId, Role = OrganizationRole.Member });
        Project = new Project { Id = Guid.NewGuid(), Name = "Launch Promo", OwnerId = UserId, OrganizationId = OrgId };
        Db.Projects.Add(Project);

        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = Project.Id, Name = "Film A" };
        Step = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, Label = "Compile" };
        Execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = Project.Id, Status = ExecutionStatus.Passed, StartedAt = Now.UtcDateTime };
        RenderKey = $"projects/{Project.Id}/outputFiles/{Execution.Id}/final-cut.mp4";
        Render = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = Execution.Id,
            WorkflowStepId = Step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = RenderKey,
            OutputJson = "{\"outputDurationSec\":61.2,\"outputWidth\":1920,\"outputHeight\":1080}",
            CompletedAt = Now.UtcDateTime
        };
        Db.WorkflowDefinitions.Add(workflow);
        Db.WorkflowSteps.Add(Step);
        Db.WorkflowExecutions.Add(Execution);
        Db.WorkflowStepResults.Add(Render);
        Storage.Objects[RenderKey] = Bytes(1000);

        string fileKey = $"projects/{Project.Id}/userFiles/{Guid.NewGuid()}.mp3";
        File = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = Project.Id,
            OriginalFileName = "soundtrack.mp3",
            OriginalPath = "soundtrack.mp3",
            Category = "userFiles",
            StorageKey = fileKey,
            StorageBucket = Bucket,
            MimeType = "audio/mpeg",
            SizeBytes = 400
        };
        Db.ProjectFiles.Add(File);
        Storage.Objects[fileKey] = Bytes(400);
        Db.SaveChanges();

        S3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, key, _) => Storage.Objects.TryGetValue(key, out byte[]? b)
                ? Task.FromResult(new GetObjectMetadataResponse { ContentLength = b.Length })
                : throw Missing());
        S3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetObjectRequest, CancellationToken>((req, _) =>
            {
                if (!Storage.Objects.TryGetValue(req.Key, out byte[]? b)) throw Missing();
                byte[] slice = req.ByteRange is { } r ? b[(int)r.Start..((int)r.End + 1)] : b;
                return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(slice), HttpStatusCode = HttpStatusCode.OK });
            });
    }

    public static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    private static AmazonS3Exception Missing() => new("missing") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };

    /// <summary>A second organization with its own project and render, for cross-tenant probes.</summary>
    public (Project Project, WorkflowStepResult Render) AddForeignProject()
    {
        Guid strangerOrg = Guid.NewGuid();
        var project = new Project { Id = Guid.NewGuid(), Name = "Someone else's", OwnerId = Guid.NewGuid(), OrganizationId = strangerOrg };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Theirs" };
        var step = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile };
        var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, Status = ExecutionStatus.Passed };
        string key = $"projects/{project.Id}/outputFiles/{exec.Id}/theirs.mp4";
        var render = new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, WorkflowStepId = step.Id, Status = StepStatus.Completed, OutputStorageKey = key };
        Db.Projects.Add(project);
        Db.WorkflowDefinitions.Add(workflow);
        Db.WorkflowSteps.Add(step);
        Db.WorkflowExecutions.Add(exec);
        Db.WorkflowStepResults.Add(render);
        Db.SaveChanges();
        Storage.Objects[key] = Bytes(50);
        return (project, render);
    }

    /// <summary>A thumbnail row and a two-sheet filmstrip density (with its sidecar) for the render.</summary>
    public (MediaDerivative Thumbnail, MediaDerivative Filmstrip) AddRenderDerivatives()
    {
        string dir = $"projects/{Project.Id}/agentFiles/media-derivatives/abc";
        var thumb = new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = Project.Id,
            SourceKind = MediaSourceKind.StepResult,
            SourceStepResultId = Render.Id,
            SourceStorageKey = RenderKey,
            Kind = MediaDerivativeKind.Thumbnail,
            StorageKey = $"{dir}/thumb.jpg",
            StorageBucket = Bucket,
            MimeType = "image/jpeg",
            Width = 320,
            Height = 180,
            CreatedAt = Now.UtcDateTime
        };
        var strip = new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = Project.Id,
            SourceKind = MediaSourceKind.StepResult,
            SourceStepResultId = Render.Id,
            SourceStorageKey = RenderKey,
            Kind = MediaDerivativeKind.Filmstrip,
            StorageKey = $"{dir}/sheet-0.jpg",
            StorageBucket = Bucket,
            IndexStorageKey = $"{dir}/index.json",
            MimeType = "image/jpeg",
            DurationSec = 61.2,
            CreatedAt = Now.UtcDateTime
        };
        Db.MediaDerivatives.AddRange(thumb, strip);
        Db.SaveChanges();
        Storage.Objects[thumb.StorageKey] = Bytes(30);
        Storage.Objects[strip.StorageKey] = Bytes(40);
        Storage.Objects[$"{dir}/sheet-1.jpg"] = Bytes(41);
        Storage.Objects[strip.IndexStorageKey!] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            densitySec = 2.0,
            thumbWidth = 160,
            thumbHeight = 90,
            cols = 5,
            rows = 4,
            sheetCount = 2,
            sheets = new object[]
            {
                new { storageLeaf = "sheet-0.jpg", firstThumbIndex = 0, thumbCount = 20, firstTimeSec = 0.0 },
                new { storageLeaf = "sheet-1.jpg", firstThumbIndex = 20, thumbCount = 11, firstTimeSec = 40.0 }
            }
        }));
        return (thumb, strip);
    }

    public void Dispose() => Db.Dispose();
}
