using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-023 (QA-P0-3, QA-P0-7): the shared StepResult source resolver, against the REAL key shapes — renders under
/// <c>outputFiles/</c>, VideoGenerate takes at <c>projects/{p}/agentFiles/generated/{exec}/step-{n}-v{k}.mp4</c>.
/// Foreign/missing/pruned answer one SOURCE_NOT_FOUND; a same-project result that cannot be used answers
/// SOURCE_NOT_PLAYABLE with the reason.
/// </summary>
public sealed class StepResultSourceResolverTests : IDisposable
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly WorkflowEngineDbContext _db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_completed_VideoGenerate_take_with_the_real_generated_key_resolves()
    {
        string key = $"projects/{_projectId}/agentFiles/generated/{Guid.NewGuid():D}/step-3-v1.mp4";
        Guid id = Seed(_projectId, key);

        StepResultSourceResolution r = await ResolveAsync(id, allowImages: false);

        r.StorageKey.Should().Be(key);
        r.Code.Should().BeNull();
    }

    [Fact]
    public async Task An_image_output_resolves_only_where_the_consumer_allows_images()
    {
        Guid id = Seed(_projectId, $"projects/{_projectId}/outputFiles/{Guid.NewGuid():D}/still.png");

        (await ResolveAsync(id, allowImages: true)).StorageKey.Should().EndWith("still.png");
        StepResultSourceResolution compile = await ResolveAsync(id, allowImages: false);
        compile.StorageKey.Should().BeNull();
        compile.Code.Should().Be(StepResultSourceResolver.NotPlayableCode);
        compile.Message.Should().Be(StepResultSourceResolver.NotVideoMessage);
    }

    [Fact]
    public async Task A_generated_key_of_another_project_is_SOURCE_NOT_FOUND_even_on_a_row_of_this_project()
    {
        Guid id = Seed(_projectId, $"projects/{Guid.NewGuid()}/agentFiles/generated/{Guid.NewGuid():D}/step-1-v1.mp4");

        StepResultSourceResolution r = await ResolveAsync(id, allowImages: true);

        r.Code.Should().Be(StepResultSourceResolver.NotFoundCode);
        r.Message.Should().Be(StepResultSourceResolver.NotFoundMessage);
    }

    [Fact]
    public async Task A_failed_row_of_another_project_is_SOURCE_NOT_FOUND_not_a_reason_that_leaks_its_status()
    {
        Guid id = Seed(Guid.NewGuid(), $"projects/{_projectId}/outputFiles/x/film.mp4", StepStatus.Failed);

        StepResultSourceResolution r = await ResolveAsync(id, allowImages: true);

        r.Code.Should().Be(StepResultSourceResolver.NotFoundCode);
        r.Message.Should().Be(StepResultSourceResolver.NotFoundMessage);
    }

    [Fact]
    public void CodeFor_maps_every_resolver_message_to_its_code_and_nothing_else()
    {
        StepResultSourceResolver.CodeFor(StepResultSourceResolver.NotFoundMessage).Should().Be("SOURCE_NOT_FOUND");
        StepResultSourceResolver.CodeFor(StepResultSourceResolver.FailedMessage).Should().Be("SOURCE_NOT_PLAYABLE");
        StepResultSourceResolver.CodeFor(StepResultSourceResolver.UnfinishedMessage).Should().Be("SOURCE_NOT_PLAYABLE");
        StepResultSourceResolver.CodeFor(StepResultSourceResolver.NotVideoMessage).Should().Be("SOURCE_NOT_PLAYABLE");
        StepResultSourceResolver.CodeFor(StepResultSourceResolver.NotMediaMessage).Should().Be("SOURCE_NOT_PLAYABLE");
        StepResultSourceResolver.CodeFor("Step 2 did not produce a video/media OutputStorageKey.").Should().BeNull();
    }

    private Task<StepResultSourceResolution> ResolveAsync(Guid stepResultId, bool allowImages) =>
        StepResultSourceResolver.ResolveAsync(_db, _projectId,
            new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: stepResultId), allowImages, CancellationToken.None);

    private Guid Seed(Guid projectId, string? key, StepStatus status = StepStatus.Completed)
    {
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ProjectId = projectId };
        WorkflowStepResult row = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowExecution = run,
            OutputStorageKey = key,
            Status = status
        };
        _db.WorkflowExecutions.Add(run);
        _db.WorkflowStepResults.Add(row);
        _db.SaveChanges();
        return row.Id;
    }
}
