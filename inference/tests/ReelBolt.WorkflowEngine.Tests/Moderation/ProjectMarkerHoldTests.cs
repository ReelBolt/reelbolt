using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>The project's output marker (Project.CsamOutputHoldAt) holds moderation work, additive to the file predicate.</summary>
public sealed class ProjectMarkerHoldTests
{
    private static (PublishModeration Service, InferenceApiDbContext Db, Project Project, string Key, Mock<IFileStorageService> Storage, Mock<IContentModerationGate> Gate)
        Build(bool marker)
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid owner = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = owner,
            OrganizationId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CsamOutputHoldAt = marker ? DateTime.UtcNow : null
        };
        db.Projects.Add(project);
        string key = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(project.Id, "outputFiles", null, "a.md");
        db.OutputModerationVerdicts.Add(new OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = key,
            SourceVersion = "e1:1000",
            State = nameof(ModerationAction.Allow),
            ModelSnapshot = "m"
        });
        db.SaveChanges();
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1000, "text/plain", "\"e1\""));
        Mock<IContentModerationGate> gate = new();
        PublishModeration service = new(
            gate.Object, storage.Object, new Mock<IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, db,
            Options.Create(new ContentModerationOptions { Enabled = true }), NullLogger<PublishModeration>.Instance);
        return (service, db, project, key, storage, gate);
    }

    [Fact]
    public async Task A_marked_project_is_held_and_an_unmarked_one_is_not()
    {
        var held = Build(marker: true);
        (await held.Service.IsProjectHeldAsync(held.Project.Id, CancellationToken.None)).Should().BeTrue();
        var free = Build(marker: false);
        (await free.Service.IsProjectHeldAsync(free.Project.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_previously_allowed_output_of_a_marked_project_is_refused_neutrally_with_no_read_and_no_row()
    {
        var w = Build(marker: true);
        int rows = await w.Db.OutputModerationVerdicts.CountAsync();

        PublishModerationOutcome? file = await w.Service.GateStoredFileAsync(w.Project.Id, w.Key, CancellationToken.None);
        PublishModerationOutcome? copy = await w.Service.GateStoredCopyAsync(w.Project.Id, w.Key, CancellationToken.None);
        PublishModerationOutcome link = await w.Service.EvaluateObjectAsync(
            w.Project.Id, w.Key, "text/plain",
            new ModerationAttribution(Guid.Empty, w.Project.Id, null, null, null, ModerationSurface.Publish, ModerationContentKind.Text),
            CancellationToken.None);

        file.Should().NotBeNull();
        copy.Should().NotBeNull();
        link.Allowed.Should().BeFalse();
        foreach (PublishModerationOutcome o in new[] { file!, copy!, link })
        {
            o.Allowed.Should().BeFalse();
            o.Message.Should().NotContain("CSAM").And.NotContain("held");
        }
        w.Storage.Verify(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await w.Db.OutputModerationVerdicts.CountAsync()).Should().Be(rows);
    }

    [Fact]
    public async Task Text_of_a_marked_project_never_reaches_the_vendor()
    {
        var w = Build(marker: true);
        PublishModerationOutcome outcome = await w.Service.EvaluateTextAsync(
            "hello", new ModerationAttribution(Guid.Empty, w.Project.Id, null, null, null, ModerationSurface.Publish, ModerationContentKind.Text),
            CancellationToken.None);
        outcome.Allowed.Should().BeFalse();
        w.Gate.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unmarked_project_still_serves_its_stored_allow()
    {
        var w = Build(marker: false);
        (await w.Service.GateStoredFileAsync(w.Project.Id, w.Key, CancellationToken.None)).Should().BeNull();
    }
}
