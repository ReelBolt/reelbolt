#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Archives;

public class ArchiveUnpackRoutesAndHookTests
{
    private sealed class FakeUser(Guid org, OrganizationRole role, bool verified = true) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => role;
        public bool EmailVerified => verified;
    }

    private static (UnpackHarness H, ProjectArchiveUnpacksController C, ArchiveUnpackSignal Signal) Route(
        OrganizationRole role = OrganizationRole.Member, bool verified = true, Guid? foreignOrg = null)
    {
        UnpackHarness h = new();
        FakeUser user = new(foreignOrg ?? h.Project.OrganizationId, role, verified);
        ArchiveUnpackSignal signal = new();
        ArchiveUnpackScheduler scheduler = new(h.Db, signal);
        ProjectArchiveUnpacksController controller = new(h.Db, TestTenancy.Access(h.Db, user), user, scheduler);
        return (h, controller, signal);
    }

    // ------------------------------------------------------------------ the upload hook

    private static (ProjectFileAddedEffects Effects, UnpackHarness H) Hook()
    {
        UnpackHarness h = new();
        ProjectFileAddedEffects effects = new(
            Mock.Of<IPublishEndpoint>(), Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(), Mock.Of<IFileStorageService>(),
            unpacks: new ArchiveUnpackScheduler(h.Db, new ArchiveUnpackSignal()));
        return (effects, h);
    }

    [Fact]
    public async Task A_zip_upload_gets_a_pending_unpack_row()
    {
        (ProjectFileAddedEffects effects, UnpackHarness h) = Hook();
        ProjectFile zip = h.AddZip("code.zip");

        await effects.RunAsync(zip, h.UserId, CancellationToken.None);

        ProjectArchiveUnpack row = await h.Db.ProjectArchiveUnpacks.SingleAsync();
        row.Status.Should().Be(ArchiveUnpackRunStatus.Pending);
        row.SourceFileId.Should().Be(zip.Id);
        row.ProjectId.Should().Be(h.Project.Id);
        row.OrganizationId.Should().Be(h.Project.OrganizationId);
        row.RequestedByUserId.Should().Be(h.UserId);
        row.ArchiveName.Should().Be("code.zip");
    }

    [Fact]
    public async Task A_zip_mime_without_the_extension_also_unpacks_and_running_twice_queues_once()
    {
        (ProjectFileAddedEffects effects, UnpackHarness h) = Hook();
        ProjectFile zip = h.AddZip("bundle.dat");

        await effects.RunAsync(zip, h.UserId, CancellationToken.None);
        await effects.RunAsync(zip, h.UserId, CancellationToken.None);

        (await h.Db.ProjectArchiveUnpacks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Non_zip_synced_and_quarantined_rows_queue_nothing()
    {
        (ProjectFileAddedEffects effects, UnpackHarness h) = Hook();
        ProjectFile text = h.AddZip("notes.txt");
        text.MimeType = "text/plain";
        ProjectFile synced = h.AddZip("repo.zip");
        synced.SourceKind = "github";
        ProjectFile cloud = h.AddZip("drive.zip");
        cloud.SourceKind = "gdrive";
        ProjectFile quarantined = h.AddZip("bad.zip");
        quarantined.CsamStatus = CsamScanStatus.Quarantined;
        ProjectFile output = h.AddZip("render.zip");
        output.Category = "outputFiles";

        foreach (ProjectFile f in new[] { text, synced, cloud, quarantined, output })
            await effects.RunAsync(f, h.UserId, CancellationToken.None);

        (await h.Db.ProjectArchiveUnpacks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_scheduling_failure_never_fails_the_upload()
    {
        UnpackHarness h = new();
        Mock<IArchiveUnpackScheduler> broken = new();
        broken.Setup(s => s.ScheduleForUploadAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // The real scheduler swallows its own failures: a disposed context makes the insert throw.
        ProjectFile zip = h.AddZip();
        ArchiveUnpackScheduler scheduler = new(h.Db, new ArchiveUnpackSignal());
        h.Db.Dispose();

        Func<Task> act = () => scheduler.ScheduleForUploadAsync(zip, h.UserId, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ------------------------------------------------------------------ routes

    [Fact]
    public async Task Unpack_queues_a_zip_and_returns_the_summary()
    {
        (UnpackHarness h, ProjectArchiveUnpacksController c, _) = Route();
        ProjectFile zip = h.AddZip();

        ActionResult<ArchiveUnpackResponse> result = await c.Unpack(h.Project.Id, zip.Id, CancellationToken.None);

        AcceptedResult accepted = result.Result.Should().BeOfType<AcceptedResult>().Subject;
        ArchiveUnpackResponse body = accepted.Value.Should().BeOfType<ArchiveUnpackResponse>().Subject;
        body.Status.Should().Be("Pending");
        body.ArchiveName.Should().Be("app.zip");
        (await h.Db.ProjectArchiveUnpacks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Unpack_while_one_is_active_is_409_and_after_a_failure_it_is_a_retry()
    {
        (UnpackHarness h, ProjectArchiveUnpacksController c, _) = Route();
        ProjectFile zip = h.AddZip();
        ProjectArchiveUnpack first = h.AddUnpack(zip);

        ActionResult<ArchiveUnpackResponse> busy = await c.Unpack(h.Project.Id, zip.Id, CancellationToken.None);
        CodeOf(busy.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value).Should().Be("UNPACK_IN_PROGRESS");

        first.Status = ArchiveUnpackRunStatus.Failed;
        h.Db.SaveChanges();
        ActionResult<ArchiveUnpackResponse> retry = await c.Unpack(h.Project.Id, zip.Id, CancellationToken.None);
        retry.Result.Should().BeOfType<AcceptedResult>();
        (await h.Db.ProjectArchiveUnpacks.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Unpack_of_a_non_archive_is_400_and_of_an_unknown_file_404()
    {
        (UnpackHarness h, ProjectArchiveUnpacksController c, _) = Route();
        ProjectFile text = h.AddZip("notes.txt");
        text.MimeType = "text/plain";
        h.Db.SaveChanges();

        CodeOf((await c.Unpack(h.Project.Id, text.Id, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Be("NOT_AN_ARCHIVE");
        (await c.Unpack(h.Project.Id, Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_foreign_project_is_404_and_a_billing_role_is_403_on_both_routes()
    {
        (UnpackHarness h, ProjectArchiveUnpacksController foreign, _) = Route(foreignOrg: Guid.NewGuid());
        ProjectFile zip = h.AddZip();
        (await foreign.Unpack(h.Project.Id, zip.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await foreign.List(h.Project.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();

        (UnpackHarness h2, ProjectArchiveUnpacksController billing, _) = Route(OrganizationRole.Billing);
        ProjectFile zip2 = h2.AddZip();
        (await billing.Unpack(h2.Project.Id, zip2.Id, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await billing.List(h2.Project.Id, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await h.Db.ProjectArchiveUnpacks.CountAsync()).Should().Be(0);
        (await h2.Db.ProjectArchiveUnpacks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unverified_email_cannot_queue_an_unpack()
    {
        (UnpackHarness h, ProjectArchiveUnpacksController c, _) = Route(verified: false);
        ProjectFile zip = h.AddZip();

        ObjectResult denied = (await c.Unpack(h.Project.Id, zip.Id, CancellationToken.None)).Result.Should().BeAssignableTo<ObjectResult>().Subject;

        denied.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task The_list_shows_active_first_then_newest_at_most_twenty_with_no_storage_key()
    {
        (UnpackHarness h, ProjectArchiveUnpacksController c, _) = Route();
        for (int i = 0; i < 24; i++)
        {
            ProjectArchiveUnpack done = h.AddUnpack(h.AddZip($"z{i}.zip"), ArchiveUnpackRunStatus.Done);
            done.CreatedAt = h.Now.AddMinutes(i);
            done.FilesCreated = i;
        }

        ProjectArchiveUnpack active = h.AddUnpack(h.AddZip("active.zip"), ArchiveUnpackRunStatus.Running);
        active.CreatedAt = h.Now.AddDays(-5);
        active.SkippedJson = """{"counts":{"EXCLUDED_VCS":2},"samples":[{"path":".git/config","reason":"EXCLUDED_VCS"}]}""";
        h.Db.SaveChanges();

        ActionResult<IReadOnlyList<ArchiveUnpackResponse>> result = await c.List(h.Project.Id, CancellationToken.None);

        IReadOnlyList<ArchiveUnpackResponse> rows = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IReadOnlyList<ArchiveUnpackResponse>>().Subject;
        rows.Should().HaveCount(20);
        rows[0].ArchiveName.Should().Be("active.zip");
        rows[0].Status.Should().Be("Running");
        rows[0].Skipped.Counts["EXCLUDED_VCS"].Should().Be(2);
        rows[0].Skipped.Samples.Single().Path.Should().Be(".git/config");
        rows[1].ArchiveName.Should().Be("z23.zip");

        string json = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.ToLowerInvariant().Should().NotContain("storage").And.NotContain("bucket").And.NotContain("key");
        JsonDocument.Parse(json).RootElement[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "archiveName", "status", "failureCode", "filesCreated", "bytesCreated", "targetDirectory", "skipped", "createdAt", "finishedAt");
    }

    [Fact]
    public async Task The_list_never_shows_another_projects_unpacks()
    {
        (UnpackHarness h, ProjectArchiveUnpacksController c, _) = Route();
        h.AddUnpack(h.AddZip());
        h.Db.ProjectArchiveUnpacks.Add(new ProjectArchiveUnpack
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            OrganizationId = h.Project.OrganizationId,
            ArchiveName = "other.zip",
            RequestedByUserId = h.UserId
        });
        h.Db.SaveChanges();

        IReadOnlyList<ArchiveUnpackResponse> rows = (IReadOnlyList<ArchiveUnpackResponse>)((OkObjectResult)(await c.List(h.Project.Id, CancellationToken.None)).Result!).Value!;

        rows.Select(r => r.ArchiveName).Should().Equal("app.zip");
    }

    private static string? CodeOf(object? body) => body?.GetType().GetProperty("code")?.GetValue(body) as string;

    // ------------------------------------------------------------------ assistant note

    [Fact]
    public void The_assistant_answer_for_a_zip_says_it_is_unpacking_and_for_other_files_is_unchanged()
    {
        MethodInfo answer = typeof(AssistantUploadTools).GetMethod("FileAnswer", BindingFlags.NonPublic | BindingFlags.Static)!;
        Project project = new() { Id = Guid.NewGuid(), Name = "Demo" };
        ProjectFile zip = new() { OriginalFileName = "my app.zip", DirectoryPath = "uploads", MimeType = "application/zip", SizeBytes = 5 };
        ProjectFile clip = new() { OriginalFileName = "clip.mp4", MimeType = "video/mp4", SizeBytes = 7 };

        using JsonDocument zipJson = JsonDocument.Parse((string)answer.Invoke(null, [project, zip])!);
        zipJson.RootElement.GetProperty("unpacking").GetBoolean().Should().BeTrue();
        zipJson.RootElement.GetProperty("note").GetString().Should().Contain("being unpacked into the folder \"uploads/my app\"");

        using JsonDocument clipJson = JsonDocument.Parse((string)answer.Invoke(null, [project, clip])!);
        clipJson.RootElement.TryGetProperty("unpacking", out _).Should().BeFalse();
        clipJson.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "fileId", "fileName", "directoryPath", "sizeBytes", "mimeType", "projectName");
    }

    // ------------------------------------------------------------------ readiness

    [Fact]
    public void Readiness_says_unpacking_while_a_zip_is_still_being_unpacked()
    {
        ProjectReadinessSnapshot snapshot = new(0, 0, 0, 0, new HashSet<InferenceProviderCapability>(), false, false, ArchiveFiles: 1, UnpackingArchives: 1);
        IReadOnlyList<ReadinessMissing> missing = ProjectReadinessFacts.Evaluate(
            [ReelBolt.Shared.Workflows.Apps.AppStepFacts.From(StepType.Agent, AgentType.CodeStructureAnalyzer, null, null)], snapshot);

        missing.Should().ContainSingle(m => m.Need == "code").Which.Message.Should().Be(ProjectReadinessFacts.UnpackingCodeMessage);
        missing.Single().Message.Should().NotBe(ProjectReadinessFacts.ArchiveOnlyCodeMessage);
    }

    [Fact]
    public async Task The_snapshot_counts_only_active_unpacks()
    {
        UnpackHarness h = new();
        h.AddUnpack(h.AddZip("a.zip"), ArchiveUnpackRunStatus.Pending);
        h.AddUnpack(h.AddZip("b.zip"), ArchiveUnpackRunStatus.Failed);
        ProjectReadinessFacts facts = new(h.Db);

        ProjectReadinessSnapshot snapshot = await facts.LoadAsync(h.Project, CancellationToken.None);

        snapshot.UnpackingArchives.Should().Be(1);
        snapshot.ArchiveFiles.Should().Be(2);
    }
}
