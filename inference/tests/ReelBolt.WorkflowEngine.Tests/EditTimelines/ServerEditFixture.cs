#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Tests.Controllers;

namespace ReelBolt.WorkflowEngine.Tests.EditTimelines;

/// <summary>A SQLite project with one timeline of media clips, a fake artifact store and helpers to add a finished analysis run.</summary>
internal sealed class ServerEditFixture : IDisposable
{
    public SqliteConnection Connection { get; private set; } = null!;
    public InferenceApiDbContext Db { get; private set; } = null!;
    public Guid ProjectId { get; } = Guid.NewGuid();
    public Guid OwnerId { get; } = Guid.NewGuid();
    public Guid FileId { get; } = Guid.NewGuid();
    public Guid OtherFileId { get; } = Guid.NewGuid();
    public Guid TimelineId { get; } = Guid.NewGuid();
    public Guid OtherProjectId { get; } = Guid.NewGuid();
    public List<Guid> ClipIds { get; } = [];
    public Mock<IFileStorageService> Storage { get; } = new();

    /// <summary>The artifact the fake store serves for any key.</summary>
    public string Artifact { get; set; } = "";

    public const string ArtifactKeyTail = "agentFiles/video-analysis/r/a.json";

    public static async Task<ServerEditFixture> CreateAsync(int clips = 3, long clipFrames = 30, int startRevision = 1)
    {
        ServerEditFixture f = new();
        (f.Connection, f.Db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        Guid org = TestTenancy.OrgOf(f.OwnerId);
        DateTime now = DateTime.UtcNow;
        f.Db.Projects.Add(new Project { Id = f.ProjectId, Name = "P", OwnerId = f.OwnerId, OrganizationId = org, CreatedAt = now, UpdatedAt = now, ObjectEffectsConsentAt = now });
        f.Db.Projects.Add(new Project { Id = f.OtherProjectId, Name = "Q", OwnerId = f.OwnerId, OrganizationId = org, CreatedAt = now, UpdatedAt = now });
        f.Db.ProjectFiles.Add(new ProjectFile { Id = f.FileId, ProjectId = f.ProjectId, StorageKey = $"projects/{f.ProjectId}/userFiles/a.mp4", OriginalFileName = "a.mp4", MimeType = "video/mp4" });
        f.Db.ProjectFiles.Add(new ProjectFile { Id = f.OtherFileId, ProjectId = f.ProjectId, StorageKey = $"projects/{f.ProjectId}/userFiles/b.mp4", OriginalFileName = "b.mp4", MimeType = "video/mp4" });

        List<Item> items = [];
        long at = 0;
        for (int i = 0; i < clips; i++)
        {
            Guid id = Guid.NewGuid();
            f.ClipIds.Add(id);
            items.Add(Clip(id, $"Clip {i + 1}", at, at + clipFrames, f.FileId));
            at += clipFrames;
        }

        f.Db.EditTimelines.Add(new EditTimelineEntity
        {
            Id = f.TimelineId,
            ProjectId = f.ProjectId,
            Name = "Cut",
            Revision = startRevision,
            DocumentJson = JsonSerializer.Serialize(f.Document(items), EditTimelineJson.Options),
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddHours(-1)
        });
        f.Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
        await f.Db.SaveChangesAsync();

        f.Artifact = f.ArtifactJson("g1");
        f.Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult((Stream)new MemoryStream(Encoding.UTF8.GetBytes(f.Artifact))));
        return f;
    }

    public static MediaClip Clip(Guid id, string name, long recordIn, long recordOut, Guid fileId, IReadOnlyList<RegionClipEffect>? effects = null) =>
        new(id, name, recordIn, recordOut, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: fileId), RegionEffects: effects);

    public EditTimeline Document(IReadOnlyList<Item> items) => new(
        id: TimelineId,
        projectId: ProjectId,
        name: "Cut",
        settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
        tracks: [new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items)],
        markers: []);

    /// <summary>The artifact of one source (this fixture's file) with the named tracks, each long enough to cover 100 seconds.</summary>
    public string ArtifactJson(params string[] trackIds) =>
        $$"""{"sources":[{"sourceIndex":0,"storageKey":"x","projectFileId":"{{FileId}}"}],"segmentationTracks":[{{string.Join(",", trackIds.Select(t =>
            "{\"id\":\"" + t + "\",\"prompt\":\"car\",\"sourceIndex\":0,\"startSec\":0,\"endSec\":100,\"fps\":30,\"frameCount\":3000,\"facts\":{\"emptyFrames\":0}}"))}}]}""";

    public InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseSqlite(Connection)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
        .Options);

    public EditTimelineWriter Writer(InferenceApiDbContext? db = null) =>
        new(db ?? Db, Storage.Object, NullLogger<EditTimelineWriter>.Instance);

    public async Task<EditTimeline> ReadTimelineAsync()
    {
        string json = await Db.EditTimelines.AsNoTracking().Where(t => t.Id == TimelineId).Select(t => t.DocumentJson).SingleAsync();
        return JsonSerializer.Deserialize<EditTimeline>(json, EditTimelineJson.Options)!;
    }

    public IEnumerable<MediaClip> Clips(EditTimeline doc) => doc.Tracks.SelectMany(t => t.Items).OfType<MediaClip>();

    public async Task<int> RevisionAsync() =>
        await Db.EditTimelines.AsNoTracking().Where(t => t.Id == TimelineId).Select(t => t.Revision).SingleAsync();

    /// <summary>Adds the hidden run for <paramref name="row"/>: a finished execution with a VideoAnalyze result pointing at the artifact.</summary>
    public async Task<Guid> AddRunAsync(Guid executionId, ExecutionStatus status, StepStatus step = StepStatus.Completed, string? failureCode = null)
    {
        Guid definition = Guid.NewGuid(), stepId = Guid.NewGuid(), result = Guid.NewGuid();
        Db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = definition,
            ProjectId = ProjectId,
            Name = "w",
            Kind = WorkflowDefinitionKind.ObjectAnalysis,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definition,
            ProjectId = ProjectId,
            OrganizationId = TestTenancy.OrgOf(OwnerId),
            Status = status
        });
        Db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = definition, StepOrder = 1, StepType = StepType.VideoAnalyze });
        Db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = result,
            WorkflowExecutionId = executionId,
            WorkflowStepId = stepId,
            Status = step,
            FailureCode = failureCode,
            CompletedAt = DateTime.UtcNow,
            ArtifactStorageKey = $"projects/{ProjectId}/{ArtifactKeyTail}"
        });
        await Db.SaveChangesAsync();
        return result;
    }

    public void Dispose()
    {
        Db.Dispose();
        Connection.Dispose();
    }
}
