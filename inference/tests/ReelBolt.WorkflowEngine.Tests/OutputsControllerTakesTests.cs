#nullable enable
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Takes: every render of one workflow step is a take of that step's video; the list says which is
/// the latest, which are intermediate, and the owner can pin, delete one earlier take, or delete
/// every earlier take — never the latest one and never a pinned one.
/// </summary>
public class OutputsControllerTakesTests
{
    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public System.Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private sealed class Scenario
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Guid OwnerId { get; init; }
        public required Project Project { get; init; }
        public required WorkflowStepResult RunOne { get; init; }
        public required WorkflowStepResult RunTwoPass1 { get; init; }
        public required WorkflowStepResult RunTwoPass2 { get; init; }
        public required WorkflowStepResult OtherStep { get; init; }
        public Mock<IAmazonS3> S3 { get; } = new();
        public List<string> DeletedKeys { get; } = [];
        public Mock<ReelBolt.Inference.Api.Services.Storage.IFileStorageService> Storage { get; } = new();
        public List<(string From, string To)> Copies { get; } = [];

        public OutputsController Controller(Guid? user = null)
        {
            S3.Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, key, _) => DeletedKeys.Add(key))
                .ReturnsAsync(new DeleteObjectResponse());
            Storage.SetupGet(s => s.BucketName).Returns("reelforge");
            Storage.Setup(s => s.CopyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, string, CancellationToken>((_, from, to, _) => Copies.Add((from, to)))
                .Returns(Task.CompletedTask);
            return new OutputsController(Db, new FakeCurrentUser(user ?? OwnerId), TestTenancy.Access(Db, new FakeCurrentUser(user ?? OwnerId)), S3.Object,
                Storage.Object,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build());
        }
    }

    private static readonly DateTime FarMarker = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Deleting_a_held_take_leaves_its_key_and_its_file_row()
    {
        Scenario s = await BuildAsync();
        string key = s.RunOne.OutputStorageKey!;
        List<OutputVideoResponse> before = Outputs(await s.Controller().ListOutputs(s.Project.Id, CancellationToken.None));
        s.Project.CsamOutputHoldAt = FarMarker;
        await s.Db.SaveChangesAsync();

        await s.Controller().DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None);

        s.DeletedKeys.Should().BeEmpty();
        s.Db.ProjectFiles.Any(f => f.StorageKey == key).Should().BeTrue();
        s.Db.WorkflowStepResults.Single(r => r.Id == s.RunOne.Id).OutputStorageKey.Should().Be(key);
        before.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Pruning_held_takes_leaves_every_key_and_file_row()
    {
        Scenario s = await BuildAsync();
        string key = s.RunOne.OutputStorageKey!;
        string group = Outputs(await s.Controller().ListOutputs(s.Project.Id, CancellationToken.None))
            .Single(o => o.StepResultId == s.RunOne.Id).TakeGroupKey!;
        s.Project.CsamOutputHoldAt = FarMarker;
        await s.Db.SaveChangesAsync();

        await s.Controller().PruneTakes(s.Project.Id, new PruneTakesRequest(group, 1), CancellationToken.None);

        s.DeletedKeys.Should().BeEmpty();
        s.Db.ProjectFiles.Any(f => f.StorageKey == key).Should().BeTrue();
        s.Db.WorkflowStepResults.Single(r => r.Id == s.RunOne.Id).OutputStorageKey.Should().Be(key);
    }

    private static async Task<Scenario> BuildAsync()
    {
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid ownerId = Guid.NewGuid();
        DateTime t0 = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
        var project = new Project { Id = Guid.NewGuid(), Name = "Reel", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = t0, UpdatedAt = t0 };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "15s reel", CreatedAt = t0, UpdatedAt = t0 };
        var compile = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, Label = "Compile" };
        var author = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Agent, Label = "Author" };
        var run1 = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, Status = ExecutionStatus.Passed, StartedAt = t0 };
        var run2 = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, Status = ExecutionStatus.Passed, StartedAt = t0.AddHours(1) };
        string Key(string name) => $"projects/{project.Id}/outputFiles/{name}";
        WorkflowStepResult Render(WorkflowExecution run, WorkflowStep step, string name, double hours) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = Key(name),
            ExecutedAt = t0.AddHours(hours),
            CompletedAt = t0.AddHours(hours)
        };

        WorkflowStepResult runOne = Render(run1, compile, "a.mp4", 0.5);
        WorkflowStepResult pass1 = Render(run2, compile, "b1.mp4", 1.2);
        WorkflowStepResult pass2 = Render(run2, compile, "b2.mp4", 1.4);
        WorkflowStepResult other = Render(run2, author, "promo.mp4", 1.1);

        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.AddRange(compile, author);
        db.WorkflowExecutions.AddRange(run1, run2);
        db.WorkflowStepResults.AddRange(runOne, pass1, pass2, other);
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "15s reel - run 1.mp4",
            Category = "outputFiles",
            StorageKey = Key("a.mp4"),
            StorageBucket = "reelforge",
            MimeType = "video/mp4"
        });
        await db.SaveChangesAsync();
        return new Scenario { Db = db, OwnerId = ownerId, Project = project, RunOne = runOne, RunTwoPass1 = pass1, RunTwoPass2 = pass2, OtherStep = other };
    }

    private static List<OutputVideoResponse> Outputs(ActionResult<List<OutputVideoResponse>> result) =>
        (List<OutputVideoResponse>)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task Renders_of_one_step_are_takes_newest_first_with_intermediate_passes_marked()
    {
        Scenario s = await BuildAsync();

        List<OutputVideoResponse> outputs = Outputs(await s.Controller().ListOutputs(s.Project.Id, CancellationToken.None));

        OutputVideoResponse latest = outputs.Single(o => o.StepResultId == s.RunTwoPass2.Id);
        OutputVideoResponse pass1 = outputs.Single(o => o.StepResultId == s.RunTwoPass1.Id);
        OutputVideoResponse runOne = outputs.Single(o => o.StepResultId == s.RunOne.Id);
        OutputVideoResponse promo = outputs.Single(o => o.StepResultId == s.OtherStep.Id);

        latest.TakeGroupKey.Should().Be(pass1.TakeGroupKey).And.Be(runOne.TakeGroupKey);
        promo.TakeGroupKey.Should().NotBe(latest.TakeGroupKey, "another step's video is its own group");
        (latest.TakeIndex, latest.TakeCount, latest.IsLatestTake, latest.IsIntermediate).Should().Be((1, 3, true, false));
        (pass1.TakeIndex, pass1.IsLatestTake, pass1.IsIntermediate).Should().Be((2, false, true));
        (runOne.TakeIndex, runOne.IsLatestTake, runOne.IsIntermediate).Should().Be((3, false, false));
        promo.IsLatestTake.Should().BeTrue();
    }

    [Fact]
    public async Task A_revision_replays_final_cut_is_the_newest_take_of_the_original_compile_and_copied_rows_are_not_takes()
    {
        Scenario s = await BuildAsync();
        WorkflowStep originalCompile = s.Db.WorkflowSteps.Single(st => st.Id == s.RunOne.WorkflowStepId);
        Guid originalWorkflow = originalCompile.WorkflowDefinitionId;

        // The revision's snapshot of the workflow: the author (2) became a Revise step, the compile (3) re-ran.
        var snapshot = new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = s.Project.Id, Name = "Changes to 15s reel", Kind = WorkflowDefinitionKind.CompositionReplay };
        var revise = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Revise };
        var compile = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile };
        var replay = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, ProjectId = s.Project.Id, Status = ExecutionStatus.Passed, StartedAt = DateTime.UtcNow };
        ReelBolt.Shared.Workflows.Replay.CompositionReplayPlan plan = new(1, s.RunTwoPass2.WorkflowExecutionId, originalWorkflow, 2,
            ReelBolt.Shared.Workflows.Replay.CompositionCutPolicy.KeepCut, [], [3]);
        string Key(string name) => $"projects/{s.Project.Id}/outputFiles/{name}";
        WorkflowStepResult film = new() { Id = Guid.NewGuid(), WorkflowExecutionId = replay.Id, WorkflowStepId = revise.Id, Status = StepStatus.Completed, OutputStorageKey = Key("revision.mp4"), ExecutedAt = DateTime.UtcNow.AddMinutes(1) };
        WorkflowStepResult cut = new() { Id = Guid.NewGuid(), WorkflowExecutionId = replay.Id, WorkflowStepId = compile.Id, Status = StepStatus.Completed, OutputStorageKey = Key("cut-v2.mp4"), ExecutedAt = DateTime.UtcNow.AddMinutes(2) };
        WorkflowStepResult copy = new() { Id = Guid.NewGuid(), WorkflowExecutionId = replay.Id, WorkflowStepId = compile.Id, Status = StepStatus.Completed, OutputStorageKey = s.RunOne.OutputStorageKey, ReusedFromStepResultId = s.RunOne.Id, ExecutedAt = DateTime.UtcNow };
        s.Db.WorkflowDefinitions.Add(snapshot);
        s.Db.WorkflowSteps.AddRange(revise, compile);
        s.Db.WorkflowExecutions.Add(replay);
        s.Db.WorkflowStepResults.AddRange(film, cut, copy);
        s.Db.CompositionRevisions.Add(new CompositionRevision
        {
            Id = Guid.NewGuid(),
            ProjectId = s.Project.Id,
            WorkspaceId = Guid.NewGuid(),
            SourceExecutionId = s.RunTwoPass2.WorkflowExecutionId,
            ExecutionId = replay.Id,
            WorkflowDefinitionId = snapshot.Id,
            Instruction = "x",
            ReplayPlanJson = plan.Serialize()
        });
        await s.Db.SaveChangesAsync();

        List<OutputVideoResponse> outputs = Outputs(await s.Controller().ListOutputs(s.Project.Id, CancellationToken.None));

        outputs.Should().NotContain(o => o.StepResultId == copy.Id, "a copied row is not a render of its own");
        OutputVideoResponse newCut = outputs.Single(o => o.StepResultId == cut.Id);
        newCut.TakeGroupKey.Should().Be(outputs.Single(o => o.StepResultId == s.RunTwoPass2.Id).TakeGroupKey);
        (newCut.TakeIndex, newCut.IsLatestTake, newCut.IsFinal).Should().Be((1, true, true));
        outputs.Single(o => o.StepResultId == film.Id).TakeGroupKey
            .Should().Be(outputs.Single(o => o.StepResultId == s.OtherStep.Id).TakeGroupKey, "the re-rendered film is a take of the film step it replaced");

        // Deleting the original take its copy points at unlinks the copy too.
        IActionResult deleted = await s.Controller().DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None);
        deleted.Should().BeOfType<NoContentResult>();
        (await s.Db.WorkflowStepResults.SingleAsync(r => r.Id == copy.Id)).OutputStorageKey.Should().BeNull();
    }

    [Fact]
    public async Task An_editor_render_is_deleted_even_though_it_is_the_latest_take_of_its_own_workflow()
    {
        Scenario s = await BuildAsync();
        WorkflowDefinition wf = await s.Db.WorkflowDefinitions.SingleAsync();
        wf.Kind = WorkflowDefinitionKind.TimelineRender;
        await s.Db.SaveChangesAsync();
        string key = s.RunOne.OutputStorageKey!;
        // RunTwoPass2 is the latest take of the group: a normal workflow refuses it, a timeline render does not.
        IActionResult latest = await s.Controller().DeleteTake(s.Project.Id, s.RunTwoPass2.Id, CancellationToken.None);
        latest.Should().BeOfType<NoContentResult>();
        s.Db.WorkflowStepResults.Single(r => r.Id == s.RunTwoPass2.Id).OutputStorageKey.Should().BeNull();
        key.Should().NotBeNull();
    }
    [Fact]
    public async Task Deleting_an_earlier_take_removes_its_video_and_its_file_row_but_keeps_the_run_history()
    {
        Scenario s = await BuildAsync();
        string key = s.RunOne.OutputStorageKey!;

        IActionResult result = await s.Controller().DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        s.DeletedKeys.Should().Equal(key);
        s.Db.ProjectFiles.Any(f => f.StorageKey == key).Should().BeFalse();
        WorkflowStepResult kept = s.Db.WorkflowStepResults.Single(r => r.Id == s.RunOne.Id);
        kept.OutputStorageKey.Should().BeNull("the take is gone");
        Outputs(await s.Controller().ListOutputs(s.Project.Id, CancellationToken.None))
            .Should().NotContain(o => o.StepResultId == s.RunOne.Id);
    }

    [Fact]
    public async Task Deleting_a_take_a_picture_uses_gives_the_picture_its_own_copy_first()
    {
        // D10: a picture made from an output image keeps working when that take is deleted.
        Scenario s = await BuildAsync();
        string key = s.RunOne.OutputStorageKey!;
        ProjectFile row = s.Db.ProjectFiles.Single(f => f.StorageKey == key);
        row.SizeBytes = 4321;
        Guid documentId = Guid.NewGuid();
        s.Db.EditImageDocuments.Add(new EditImageDocument
        {
            Id = documentId,
            ProjectId = s.Project.Id,
            SourceProjectFileId = row.Id,
            Revision = 1,
            Layers =
            {
                new EditImageLayer
                {
                    Id = Guid.NewGuid(), DocumentId = documentId, ZOrder = 1, Role = EditImageLayerRoles.Background,
                    Kind = EditImageLayerKind.Source, SourceProjectFileId = row.Id, StorageKey = key,
                    StorageBucket = "reelforge", MimeType = "image/png", TransformJson = "{}"
                }
            }
        });
        await s.Db.SaveChangesAsync();

        IActionResult result = await s.Controller().DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (string from, string to) = s.Copies.Should().ContainSingle().Subject;
        from.Should().Be(key);
        to.Should().StartWith($"projects/{s.Project.Id}/agentFiles/image-editor/{documentId}/source-");
        EditImageLayer layer = s.Db.EditImageLayers.Single(l => l.DocumentId == documentId);
        layer.StorageKey.Should().Be(to);
        layer.CopySizeBytes.Should().Be(4321);
        s.Db.EditImageDocuments.Single(d => d.Id == documentId).SourceProjectFileId.Should().BeNull();
        s.DeletedKeys.Should().Equal(key);
    }
    [Fact]
    public async Task The_latest_take_and_a_pinned_take_are_never_deleted()
    {
        Scenario s = await BuildAsync();
        OutputsController controller = s.Controller();

        (await controller.DeleteTake(s.Project.Id, s.RunTwoPass2.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        (await controller.PinTake(s.Project.Id, s.RunOne.Id, new PinTakeRequest(true), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await controller.DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        s.DeletedKeys.Should().BeEmpty();
        Outputs(await controller.ListOutputs(s.Project.Id, CancellationToken.None))
            .Single(o => o.StepResultId == s.RunOne.Id).IsPinned.Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_earlier_takes_keeps_the_latest_and_the_pinned_ones()
    {
        Scenario s = await BuildAsync();
        OutputsController controller = s.Controller();
        string group = Outputs(await controller.ListOutputs(s.Project.Id, CancellationToken.None))
            .Single(o => o.StepResultId == s.RunTwoPass2.Id).TakeGroupKey!;
        await controller.PinTake(s.Project.Id, s.RunOne.Id, new PinTakeRequest(true), CancellationToken.None);

        ActionResult<PruneTakesResponse> result = await controller.PruneTakes(s.Project.Id, new PruneTakesRequest(group), CancellationToken.None);

        ((OkObjectResult)result.Result!).Value.Should().Be(new PruneTakesResponse(1, 2));
        s.DeletedKeys.Should().Equal($"projects/{s.Project.Id}/outputFiles/b1.mp4");
        List<OutputVideoResponse> after = Outputs(await controller.ListOutputs(s.Project.Id, CancellationToken.None));
        after.Select(o => o.StepResultId).Should().BeEquivalentTo([s.RunTwoPass2.Id, s.RunOne.Id, s.OtherStep.Id]);
    }

    [Fact]
    public async Task A_video_another_take_still_uses_is_not_deleted_from_storage()
    {
        Scenario s = await BuildAsync();
        // A step-cache hit reuses the earlier run's object under the same key.
        s.RunTwoPass1.OutputStorageKey = s.RunOne.OutputStorageKey;
        await s.Db.SaveChangesAsync();

        (await s.Controller().DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        s.DeletedKeys.Should().BeEmpty("the surviving take still plays from it");
        s.Db.ProjectFiles.Count().Should().Be(1, "the file row still points at a video that exists");
    }

    [Fact]
    public async Task Pruning_a_take_whose_video_a_deduplicated_composition_version_still_names_keeps_the_object()
    {
        Scenario s = await BuildAsync();
        // An unchanged re-render deduped onto the parent version, which keeps iteration 1's key.
        string key = s.RunOne.OutputStorageKey!;
        Guid versionId = Guid.NewGuid();
        s.Db.CompositionWorkspaceVersions.Add(new CompositionWorkspaceVersion
        {
            Id = versionId,
            WorkspaceId = Guid.NewGuid(),
            ProjectId = s.Project.Id,
            VersionNumber = 1,
            StorageKey = $"projects/{s.Project.Id}/agentFiles/ws.tar",
            Sha256 = "abc",
            RenderOutputStorageKey = key
        });
        await s.Db.SaveChangesAsync();
        OutputsController controller = s.Controller();

        (await controller.DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        s.DeletedKeys.Should().BeEmpty("the version still plays from this object");
        s.Db.ProjectFiles.Any(f => f.StorageKey == key).Should().BeTrue("the file row still points at an object that exists");
        s.Db.WorkflowStepResults.Single(r => r.Id == s.RunOne.Id).OutputStorageKey.Should().BeNull("the take itself is gone");
        s.Db.CompositionWorkspaceVersions.Single(v => v.Id == versionId).RenderOutputStorageKey.Should().Be(key);
    }

    [Fact]
    public async Task A_render_of_a_run_still_going_is_never_the_latest_take_and_never_deleted()
    {
        Scenario s = await BuildAsync();
        var running = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = s.Db.WorkflowExecutions.First().WorkflowDefinitionId,
            ProjectId = s.Project.Id,
            Status = ExecutionStatus.Running,
            StartedAt = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc)
        };
        var draft = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = running.Id,
            WorkflowStepId = s.RunTwoPass2.WorkflowStepId,
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{s.Project.Id}/outputFiles/draft.mp4",
            ExecutedAt = running.StartedAt.Value,
            CompletedAt = running.StartedAt.Value
        };
        s.Db.WorkflowExecutions.Add(running);
        s.Db.WorkflowStepResults.Add(draft);
        await s.Db.SaveChangesAsync();
        OutputsController controller = s.Controller();

        List<OutputVideoResponse> outputs = Outputs(await controller.ListOutputs(s.Project.Id, CancellationToken.None));
        outputs.Single(o => o.StepResultId == s.RunTwoPass2.Id).IsLatestTake.Should().BeTrue("the newest render of a run that ended");
        outputs.Single(o => o.StepResultId == draft.Id).IsLatestTake.Should().BeFalse();

        (await controller.DeleteTake(s.Project.Id, draft.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        await controller.PruneTakes(s.Project.Id, new PruneTakesRequest(outputs[0].TakeGroupKey!), CancellationToken.None);
        s.DeletedKeys.Should().NotContain(draft.OutputStorageKey!);
        s.DeletedKeys.Should().NotContain($"projects/{s.Project.Id}/outputFiles/b2.mp4");
    }

    [Fact]
    public async Task A_project_in_another_organization_is_not_found()
    {
        Scenario s = await BuildAsync();
        OutputsController stranger = s.Controller(Guid.NewGuid());

        (await stranger.DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await stranger.PinTake(s.Project.Id, s.RunOne.Id, new PinTakeRequest(true), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await stranger.PruneTakes(s.Project.Id, new PruneTakesRequest("x"), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        s.DeletedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_take_removes_the_stored_moderation_verdict_of_its_object_only()
    {
        Scenario s = await BuildAsync();
        string key = s.RunOne.OutputStorageKey!;
        string other = s.RunTwoPass2.OutputStorageKey!;
        foreach (string k in new[] { key, other })
        {
            s.Db.OutputModerationVerdicts.Add(new ReelBolt.Inference.Api.Services.Moderation.OutputModerationVerdict
            {
                Id = Guid.NewGuid(),
                OrganizationId = s.Project.OrganizationId,
                ProjectId = s.Project.Id,
                StorageKey = k,
                SourceVersion = "e1:1",
                State = "Allow",
                ModelSnapshot = "omni-moderation-2024-09-26"
            });
        }
        await s.Db.SaveChangesAsync();
        IActionResult result = await s.Controller().DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
        s.Db.OutputModerationVerdicts.Select(v => v.StorageKey).ToList().Should().Equal(other);
    }

    [Fact]
    public async Task Deleting_a_take_whose_saved_file_an_avatar_lists_keeps_that_file_and_its_video()
    {
        Scenario s = await BuildAsync();
        string key = s.RunOne.OutputStorageKey!;
        ProjectFile saved = s.Db.ProjectFiles.Single(f => f.StorageKey == key);
        Avatars.AvatarSeed.Attach(s.Db, s.Project.Id, "Anna", saved);

        IActionResult result = await s.Controller().DeleteTake(s.Project.Id, s.RunOne.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        s.DeletedKeys.Should().BeEmpty("the avatar's consent covers those bytes");
        s.Db.ProjectFiles.Any(f => f.Id == saved.Id).Should().BeTrue();
        s.Db.WorkflowStepResults.Single(r => r.Id == s.RunOne.Id).OutputStorageKey.Should().BeNull("the take itself is gone");
    }
}
