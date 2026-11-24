using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The platform (non-admin) tool surface added by T-005: projects, files and workflow deletion.
///
/// <para>What is actually being defended here is not "the tool returned a string" but the two
/// properties the product decision pins: <b>a mutation only happens against a token the server itself
/// recomputed from the object as it stands right now</b>, and <b>a caller who does not own the
/// object can never tell it apart from one that does not exist</b>. Every refusal test therefore
/// asserts on the database's write counter and the change tracker as well as on the response, so a
/// "refusal" that quietly wrote something still fails.</para>
/// </summary>
public class AssistantPlatformToolTests
{
    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private sealed class WriteCountingDbContext(DbContextOptions<InferenceApiDbContext> options)
        : InferenceApiDbContext(options)
    {
        public int SaveCount { get; private set; }

        public void ResetSaveCount() => SaveCount = 0;

        public override int SaveChanges()
        {
            SaveCount++;
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static WriteCountingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WriteCountingDbContext(options);
    }

    private static readonly Guid StrangeProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId, string name = "Exact Name")
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "seeded",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static ProjectFile SeedFile(
        InferenceApiDbContext db,
        Guid projectId,
        string fileName = "notes.md",
        string storageKey = "projects/p/userFiles/notes.md",
        string mimeType = "text/markdown")
    {
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = fileName,
            OriginalPath = fileName,
            StorageKey = storageKey,
            StorageBucket = "bucket",
            MimeType = mimeType,
            SizeBytes = 5,
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    private static WorkflowDefinition SeedWorkflow(InferenceApiDbContext db, Guid projectId, string name = "My Workflow")
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkflowDefinitions.Add(workflow);
        db.SaveChanges();
        return workflow;
    }

    private static string AsText(object? raw) => raw switch
    {
        null => string.Empty,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => raw.ToString()!
    };

    private static string ErrorOf(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    /// <summary>Invokes one tool by name and returns the raw text it produced.</summary>
    private static async Task<string> InvokeAsync(
        AssistantToolProvider provider,
        AssistantToolContext context,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);

        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in arguments)
            args[key] = value;

        return AsText(await tool.InvokeAsync(args));
    }

    private static void AssertNothingWasWritten(WriteCountingDbContext db)
    {
        db.SaveCount.Should().Be(0, "a refused tool must not persist anything");
        db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty("a refused tool must leave the change tracker clean");
    }

    // ---------------------------------------------------------------------
    // Acceptance: the surface is registered
    // ---------------------------------------------------------------------

    [Fact]
    public void Provider_registers_the_read_tools_and_every_propose_apply_pair()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));

        HashSet<string> names = provider.GetTools(new AssistantToolContext(ownerId)).Select(t => t.Name).ToHashSet();

        names.Should().Contain(new[] { "ListProjects", "GetProject", "ListWorkflows", "ListProjectFiles", "ReadProjectFile" });

        foreach (string operation in new[]
                 { "CreateProject", "UpdateProject", "WriteProjectFile", "DeleteProjectFile", "DeleteWorkflow", "DeleteProject" })
        {
            names.Should().Contain($"Propose{operation}", "every mutation is proposed first");
            names.Should().Contain($"Apply{operation}", "every proposal has an apply that verifies it");
        }
    }

    // ---------------------------------------------------------------------
    // Acceptance: every Apply* recomputes the token and writes nothing when it fails
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("ApplyCreateProject")]
    [InlineData("ApplyUpdateProject")]
    [InlineData("ApplyWriteProjectFile")]
    [InlineData("ApplyDeleteProjectFile")]
    [InlineData("ApplyDeleteWorkflow")]
    [InlineData("ApplyDeleteProject")]
    public async Task EveryApplyTool_refuses_a_fabricated_token_and_writes_nothing(string toolName)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile file = SeedFile(db, project.Id);
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id);
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);

        int projectsBefore = db.Projects.Count();
        int filesBefore = db.ProjectFiles.Count();
        int workflowsBefore = db.WorkflowDefinitions.Count();

        db.ResetSaveCount();
        string text = await InvokeAsync(
            provider,
            new AssistantToolContext(ownerId, project.Id),
            toolName,
            ApplyArguments(toolName, file, workflow, confirmationToken: "not-a-token-this-server-ever-minted"));

        ErrorOf(text).Should().Be("stale confirmationToken, re-propose and try again");
        AssertNothingWasWritten(db);
        db.Projects.Count().Should().Be(projectsBefore);
        db.ProjectFiles.Count().Should().Be(filesBefore);
        db.WorkflowDefinitions.Count().Should().Be(workflowsBefore);
        storage.Uploads.Should().BeEmpty("a refused write must not reach object storage");
        storage.DeletedKeys.Should().BeEmpty("a refused delete must not reach object storage");
    }

    [Fact]
    public async Task WriteProjectFile_token_is_bound_to_the_exact_bytes_that_were_proposed()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);

        string proposed = Convert.ToBase64String(Encoding.UTF8.GetBytes("the body the user saw"));
        string substituted = Convert.ToBase64String(Encoding.UTF8.GetBytes("a DIFFERENT body"));

        string proposal = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"), ("contentBase64", proposed));
        string token = TokenOf(proposal);

        db.ResetSaveCount();

        // Same destination, same mime type, same token — only the BODY differs. Without a content
        // hash in the canonical payload this would write happily, which is precisely the hole the
        // payload's contentHash exists to close.
        string applied = await InvokeAsync(provider, context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", substituted), ("confirmationToken", token));

        ErrorOf(applied).Should().Be("stale confirmationToken, re-propose and try again");
        AssertNothingWasWritten(db);
        storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteProject_rejects_the_keyless_workflow_digest_scheme()
    {
        // ApplyWorkflow's token is a plain, unkeyed SHA-256 for backward compatibility. That
        // carve-out is a compatibility obligation on ONE operation name and must not be a template:
        // a digest fabricated under that scheme must not authorise a new destructive tool.
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId, "Exact Name");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));

        string payload = AssistantProjectTools.BuildDeleteProjectPayload(project.Id, "Exact Name");
        string forged = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        db.ResetSaveCount();
        string text = await InvokeAsync(provider, new AssistantToolContext(ownerId, project.Id), "ApplyDeleteProject",
            ("projectId", null), ("confirmationToken", forged));

        ErrorOf(text).Should().Be("stale confirmationToken, re-propose and try again");
        AssertNothingWasWritten(db);
        db.Projects.Should().ContainSingle("the project must survive a forged confirmation");
    }

    // ---------------------------------------------------------------------
    // Acceptance: DeleteProject's payload is exactly {op, id, name}
    // ---------------------------------------------------------------------

    [Fact]
    public void DeleteProject_canonical_payload_is_exactly_the_operation_the_id_and_the_name()
    {
        AssistantProjectTools.BuildDeleteProjectPayload(StrangeProjectId, "Exact Name")
            .Should().Be(
                "{\"op\":\"DeleteProject\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"name\":\"Exact Name\"}");
    }

    [Fact]
    public async Task DeleteProject_token_stops_verifying_when_the_project_is_renamed_between_propose_and_apply()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId, "Archive 2024");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));
        var context = new AssistantToolContext(ownerId, project.Id);

        string proposal = await InvokeAsync(provider, context, "ProposeDeleteProject", ("projectId", null));
        string token = TokenOf(proposal);

        // The user was shown "delete Archive 2024" and confirmed THAT. The project is then renamed —
        // by the same user, in another tab — so the confirmation no longer describes what would be
        // destroyed. The name is inside the payload precisely so this is refused.
        project.Name = "Archive 2025 — DO NOT DELETE";
        db.SaveChanges();

        db.ResetSaveCount();
        string applied = await InvokeAsync(provider, context, "ApplyDeleteProject",
            ("projectId", null), ("confirmationToken", token));

        ErrorOf(applied).Should().Be("stale confirmationToken, re-propose and try again");
        AssertNothingWasWritten(db);
        db.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteProject_erases_the_history_objects_before_removing_the_project()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId, "Archive");
        var eraser = new Mock<ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser>();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), historyEraser: eraser.Object);
        var context = new AssistantToolContext(ownerId, project.Id);
        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProject", ("projectId", null)));

        await InvokeAsync(provider, context, "ApplyDeleteProject", ("projectId", null), ("confirmationToken", token));

        eraser.Verify(e => e.DeleteProjectObjectsAsync(project.Id, It.IsAny<CancellationToken>()), Times.Once);
        db.Projects.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteProject_keeps_the_project_when_the_history_eraser_fails()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId, "Archive");
        var eraser = new Mock<ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser>();
        eraser.Setup(e => e.DeleteProjectObjectsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), historyEraser: eraser.Object);
        var context = new AssistantToolContext(ownerId, project.Id);
        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProject", ("projectId", null)));

        string text = await InvokeAsync(provider, context, "ApplyDeleteProject", ("projectId", null), ("confirmationToken", token));

        ErrorOf(text).Should().Contain("could not be deleted");
        db.Projects.Should().ContainSingle("the project stays so a retry can still find the history objects");
    }

    private static (Project Project, ProjectFile File, AssistantToolProvider Provider, AssistantToolContext Context) HeldWorld(WriteCountingDbContext db)
    {
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile file = SeedFile(db, project.Id, "made.md", $"projects/{project.Id}/userFiles/made.md");
        file.OutputProducedAt = DateTime.UtcNow.AddDays(-3);
        db.SaveChanges();
        var storage = new FakeFileStorage();
        return (project, file, AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage), new AssistantToolContext(ownerId, project.Id));
    }

    private static void Hold(WriteCountingDbContext db, Project project)
    {
        project.CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
        db.SaveChanges();
    }

    [Fact]
    public async Task ListProjectFiles_leaves_out_a_moved_held_file()
    {
        using WriteCountingDbContext db = CreateContext();
        var w = HeldWorld(db);
        (await InvokeAsync(w.Provider, w.Context, "ListProjectFiles")).Should().Contain(w.File.Id.ToString());
        Hold(db, w.Project);

        (await InvokeAsync(w.Provider, w.Context, "ListProjectFiles")).Should().NotContain(w.File.Id.ToString());
    }

    [Fact]
    public async Task ApplyDeleteProjectFile_refuses_a_file_held_after_the_proposal()
    {
        using WriteCountingDbContext db = CreateContext();
        var w = HeldWorld(db);
        string token = TokenOf(await InvokeAsync(w.Provider, w.Context, "ProposeDeleteProjectFile", ("fileId", w.File.Id)));
        Hold(db, w.Project);

        string applied = await InvokeAsync(w.Provider, w.Context, "ApplyDeleteProjectFile", ("fileId", w.File.Id), ("confirmationToken", token));

        applied.Should().Be(ReelBolt.Inference.Api.Services.Outputs.OutputGate.ToolJson());
        db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyDeleteProjectFile_refuses_an_avatar_reference_naming_the_avatar()
    {
        using WriteCountingDbContext db = CreateContext();
        var w = HeldWorld(db);
        string token = TokenOf(await InvokeAsync(w.Provider, w.Context, "ProposeDeleteProjectFile", ("fileId", w.File.Id)));
        ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(db, w.Project.Id, "Anna", w.File);

        string applied = await InvokeAsync(w.Provider, w.Context, "ApplyDeleteProjectFile", ("fileId", w.File.Id), ("confirmationToken", token));

        applied.Should().Contain(ReelBolt.Shared.Avatars.AvatarFileUse.InUseCode).And.Contain("Anna");
        db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task Writing_over_an_avatar_reference_is_refused()
    {
        using WriteCountingDbContext db = CreateContext();
        var w = HeldWorld(db);
        string content = Convert.ToBase64String(Encoding.UTF8.GetBytes("new"));
        string token = TokenOf(await InvokeAsync(w.Provider, w.Context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", w.File.OriginalFileName), ("mimeType", "text/markdown"), ("contentBase64", content)));
        ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(db, w.Project.Id, "Anna", w.File);

        string applied = await InvokeAsync(w.Provider, w.Context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", w.File.OriginalFileName), ("mimeType", "text/markdown"), ("contentBase64", content), ("confirmationToken", token));

        applied.Should().Contain(ReelBolt.Shared.Avatars.AvatarFileUse.InUseCode);
        db.ProjectFiles.Single().StorageKey.Should().Be(w.File.StorageKey);
    }

    [Fact]
    public async Task Writing_over_a_held_file_is_refused()
    {
        using WriteCountingDbContext db = CreateContext();
        var w = HeldWorld(db);
        string content = Convert.ToBase64String(Encoding.UTF8.GetBytes("new"));
        string token = TokenOf(await InvokeAsync(w.Provider, w.Context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", w.File.OriginalFileName), ("mimeType", "text/markdown"), ("contentBase64", content)));
        Hold(db, w.Project);

        string applied = await InvokeAsync(w.Provider, w.Context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", w.File.OriginalFileName), ("mimeType", "text/markdown"), ("contentBase64", content), ("confirmationToken", token));

        applied.Should().Be(ReelBolt.Inference.Api.Services.Outputs.OutputGate.ToolJson());
        db.ProjectFiles.Single().StorageKey.Should().Be(w.File.StorageKey);
    }

    [Theory]
    [InlineData("ProposeEditProjectFile")]
    [InlineData("ApplyEditProjectFile")]
    [InlineData("ProposeInsertProjectFileLines")]
    [InlineData("ApplyInsertProjectFileLines")]
    public async Task Targeted_edits_of_a_held_file_are_refused(string tool)
    {
        using WriteCountingDbContext db = CreateContext();
        var w = HeldWorld(db);
        Hold(db, w.Project);
        List<(string Key, object? Value)> args = [("fileId", w.File.Id)];
        if (tool.Contains("Edit")) args.Add(("edits", new List<ProjectFileEdit>()));
        else { args.Add(("afterLine", 0)); args.Add(("text", "x")); }
        if (tool.StartsWith("Apply")) args.Add(("confirmationToken", "t"));

        string answer = await InvokeAsync(w.Provider, w.Context, tool, args.ToArray());

        answer.Should().Be(ReelBolt.Inference.Api.Services.Outputs.OutputGate.ToolJson());
    }

    [Fact]
    public async Task DeleteProjectFile_leaves_a_picture_that_used_the_photo_its_own_copy()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile file = SeedFile(db, project.Id, "photo.png", $"projects/{project.Id}/userFiles/photo.png", "image/png");
        Guid pictureId = await PictureCopySeed.PictureOverAsync(db, file);
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);

        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProjectFile", ("fileId", file.Id)));
        await InvokeAsync(provider, context, "ApplyDeleteProjectFile", ("fileId", file.Id), ("confirmationToken", token));

        db.ProjectFiles.Should().BeEmpty();
        storage.DeletedKeys.Should().Contain(file.StorageKey);
        string copyKey = await PictureCopySeed.AssertKeptOwnCopyAsync(db, project.Id, pictureId, file.StorageKey);
        storage.Copies.Should().ContainSingle().Which.Should().Be((file.StorageKey, copyKey));
    }

    [Fact]
    public async Task DeleteProjectFile_is_refused_before_copying_when_the_pictures_copies_do_not_fit_the_quota()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile file = SeedFile(db, project.Id, "photo.png", $"projects/{project.Id}/userFiles/photo.png", "image/png");
        await PictureCopySeed.PictureOverAsync(db, file);
        await PictureCopySeed.PictureOverAsync(db, file);
        var storage = new FakeFileStorage();
        var gate = new ReelBolt.WorkflowEngine.Tests.Uploads.UploadsPlanGate();
        gate.Violations.Enqueue(new ReelBolt.Shared.Billing.PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage, planGate: gate);
        var context = new AssistantToolContext(ownerId, project.Id);

        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProjectFile", ("fileId", file.Id)));
        string applied = await InvokeAsync(provider, context, "ApplyDeleteProjectFile", ("fileId", file.Id), ("confirmationToken", token));

        using JsonDocument doc = JsonDocument.Parse(applied);
        doc.RootElement.GetProperty("code").GetString().Should().Be("PICTURE_COPIES_OVER_QUOTA");
        ErrorOf(applied).Should().StartWith("This photo is used by 2 pictures.");
        gate.UploadChecks.Should().Equal(file.SizeBytes);
        db.ProjectFiles.Should().ContainSingle();
        storage.Copies.Should().BeEmpty();
        storage.DeletedKeys.Should().BeEmpty();
    }

    /// <summary>
    /// T-114 push-review finding 1: a transient object-store failure may still answer success, because the
    /// deletion mark was written in the transaction that removed the row, so the retention sweep is
    /// guaranteed to finish the job. Without that mark this answer would hide surviving bytes.
    /// </summary>
    [Fact]
    public async Task DeleteProjectFile_leaves_a_durable_mark_when_the_object_removal_is_transient()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile file = SeedFile(db, project.Id, "photo.png", $"projects/{project.Id}/userFiles/photo.png", "image/png");
        var storage = new FakeFileStorage
        {
            DeleteFailure = new Amazon.S3.AmazonS3Exception("throttled") { StatusCode = System.Net.HttpStatusCode.ServiceUnavailable }
        };
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);
        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProjectFile", ("fileId", file.Id)));

        string applied = await InvokeAsync(provider, context, "ApplyDeleteProjectFile",
            ("fileId", file.Id), ("confirmationToken", token));

        using JsonDocument doc = JsonDocument.Parse(applied);
        doc.RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue("a durable mark retries the object removal");
        db.ProjectFiles.Should().BeEmpty();
        StorageRetentionMark mark = db.StorageRetentionMarks.Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(file.StorageKey);
        mark.PurgedAt.Should().BeNull("the sweep retries an unpurged mark");
        storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(file.StorageKey);
    }

    /// <summary>
    /// T-114 push-review finding 1: a scope error is not a transient provider failure. Swallowing it would
    /// report a delete whose bytes survive with no durable record, so it must surface instead of a success.
    /// </summary>
    [Fact]
    public async Task DeleteProjectFile_does_not_answer_success_when_the_object_removal_is_a_scope_error()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile file = SeedFile(db, project.Id, "photo.png", $"projects/{project.Id}/userFiles/photo.png", "image/png");
        var storage = new FakeFileStorage { DeleteFailure = new UnauthorizedAccessException("outside the project's scope") };
        var publish = new Mock<IPublishEndpoint>();
        publish.Setup(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage, publish.Object);
        var context = new AssistantToolContext(ownerId, project.Id);
        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProjectFile", ("fileId", file.Id)));

        Func<Task> act = () => InvokeAsync(provider, context, "ApplyDeleteProjectFile",
            ("fileId", file.Id), ("confirmationToken", token));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        db.StorageRetentionMarks.Should().ContainSingle("the committed mark is what makes the retry possible");
        storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(file.StorageKey);
        publish.Verify(p => p.Publish(
            It.Is<ProjectFileIndexingRequested>(e => e.Operation == "Delete" && e.FileId == file.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteProject_happy_path_removes_only_the_confirmed_project()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project mine = SeedProject(db, ownerId, "Mine");
        Project other = SeedProject(db, ownerId, "Other");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));
        var context = new AssistantToolContext(ownerId, mine.Id);

        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProject", ("projectId", null)));
        string applied = await InvokeAsync(provider, context, "ApplyDeleteProject",
            ("projectId", null), ("confirmationToken", token));

        using JsonDocument doc = JsonDocument.Parse(applied);
        doc.RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue();
        db.Projects.Select(p => p.Id).Should().BeEquivalentTo(new[] { other.Id });
    }

    // ---------------------------------------------------------------------
    // Acceptance: ownership is re-checked per call, and a non-owner is indistinguishable
    //             from a missing object
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetProject_reports_a_stranger_project_exactly_as_a_missing_one()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Project theirs = SeedProject(db, otherUserId, "Theirs");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(callerId));

        string stranger = await InvokeAsync(provider, new AssistantToolContext(callerId), "GetProject", ("projectId", theirs.Id));
        string missing = await InvokeAsync(provider, new AssistantToolContext(callerId), "GetProject", ("projectId", Guid.NewGuid()));

        ErrorOf(stranger).Should().Be("project not found");
        stranger.Should().Be(missing, "the two cases must be byte-identical, or a guessed id is confirmed");
    }

    [Fact]
    public async Task Context_scoped_tools_report_a_stranger_project_exactly_as_no_project()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Project theirs = SeedProject(db, otherUserId, "Theirs");
        SeedFile(db, theirs.Id, "secret.md");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(callerId));

        var pointingAtTheirs = new AssistantToolContext(callerId, theirs.Id);
        var noProject = new AssistantToolContext(callerId);

        foreach (string toolName in new[] { "ListWorkflows", "ListProjectFiles" })
        {
            string withStranger = await InvokeAsync(provider, pointingAtTheirs, toolName);
            string withNothing = await InvokeAsync(provider, noProject, toolName);

            ErrorOf(withStranger).Should().Be("no project in context");
            withStranger.Should().Be(withNothing);
            withStranger.Should().NotContain("secret.md");
        }
    }

    [Fact]
    public async Task DeleteProjectFile_refuses_a_stranger_and_writes_nothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Project theirs = SeedProject(db, otherUserId, "Theirs");
        ProjectFile theirFile = SeedFile(db, theirs.Id, "secret.md");
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(callerId), storage);

        // Both a direct file id and a context pointing at somebody else's project must dead-end.
        db.ResetSaveCount();
        string viaContext = await InvokeAsync(provider, new AssistantToolContext(callerId, theirs.Id),
            "ProposeDeleteProjectFile", ("fileId", theirFile.Id));
        string viaNoProject = await InvokeAsync(provider, new AssistantToolContext(callerId),
            "ProposeDeleteProjectFile", ("fileId", theirFile.Id));

        ErrorOf(viaContext).Should().Be("no project in context");
        viaContext.Should().Be(viaNoProject);
        AssertNothingWasWritten(db);
        storage.DeletedKeys.Should().BeEmpty();
        db.ProjectFiles.Should().ContainSingle();
    }

    // ---------------------------------------------------------------------
    // Acceptance: WriteProjectFile — traversal refused as JSON, write goes through IFileStorageService
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("../evil", "notes.md")]
    [InlineData("a/../../b", "notes.md")]
    [InlineData("..", "notes.md")]
    [InlineData(null, "../../evil.md")]
    [InlineData(null, "..")]
    [InlineData(null, "nested/notes.md")]
    public async Task WriteProjectFile_refuses_traversal_with_a_json_error_never_an_exception(
        string? directoryPath,
        string fileName)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);

        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes("x"));

        db.ResetSaveCount();
        string proposed = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", directoryPath), ("fileName", fileName), ("mimeType", "text/markdown"), ("contentBase64", body));

        ErrorOf(proposed).Should().Be("invalid path: fileName and directoryPath must be relative and must not contain '..'");
        AssertNothingWasWritten(db);
        storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteProjectFile_proposes_then_applies_through_IFileStorageService()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);
        byte[] body = Encoding.UTF8.GetBytes("# Notes\n");
        string base64 = Convert.ToBase64String(body);

        string proposal = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", "docs"), ("fileName", "notes.md"), ("mimeType", "text/markdown"), ("contentBase64", base64));
        using (JsonDocument doc = JsonDocument.Parse(proposal))
        {
            doc.RootElement.GetProperty("replacesExistingFile").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("contentHash").GetString().Should().Be(AssistantFileTools.HashContent(body));
        }

        string applied = await InvokeAsync(provider, context, "ApplyWriteProjectFile",
            ("directoryPath", "docs"), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", base64), ("confirmationToken", TokenOf(proposal)));

        using JsonDocument appliedDoc = JsonDocument.Parse(applied);
        appliedDoc.RootElement.GetProperty("overwritten").GetBoolean().Should().BeFalse();
        appliedDoc.RootElement.GetProperty("originalPath").GetString().Should().Be("docs/notes.md");

        // The write reached object storage and nowhere else: the storage service is the seam the
        // controller uses, and nothing in this path touches the filesystem.
        storage.Uploads.Should().ContainSingle();
        storage.Uploads[0].FileName.Should().Be("notes.md");
        storage.Uploads[0].ContentType.Should().Be("text/markdown");
        storage.Uploads[0].Category.Should().Be("userFiles");
        storage.Uploads[0].Content.Should().Equal(body);

        ProjectFile stored = db.ProjectFiles.Should().ContainSingle().Subject;
        stored.OriginalPath.Should().Be("docs/notes.md");
        stored.DirectoryPath.Should().Be("docs");
        stored.MimeType.Should().Be("text/markdown");
        stored.SizeBytes.Should().Be(body.Length);
        stored.StorageKey.Should().Be(storage.Uploads[0].StorageKey);
        stored.Id.Should().Be(storage.Uploads[0].FileId);
    }

    [Fact]
    public async Task WriteProjectFile_overwrites_in_place_when_the_path_already_exists()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile existing = SeedFile(db, project.Id, "notes.md");
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("replaced"));

        string proposal = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"), ("contentBase64", base64));
        using (JsonDocument doc = JsonDocument.Parse(proposal))
        {
            doc.RootElement.GetProperty("replacesExistingFile").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("existingFileId").GetGuid().Should().Be(existing.Id);
        }

        await InvokeAsync(provider, context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", base64), ("confirmationToken", TokenOf(proposal)));

        db.ProjectFiles.Should().ContainSingle("an overwrite replaces the row, it does not duplicate it");
        db.ProjectFiles.Single().Id.Should().Be(existing.Id);
        db.ProjectFiles.Single().SizeBytes.Should().Be(8);
    }

    [Fact]
    public async Task WriteProjectFile_overwrite_of_a_library_sound_row_clears_its_quota_exemption()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile existing = SeedFile(db, project.Id, "notes.md");
        existing.SourceKind = "sfx_library";
        db.SaveChanges();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), new FakeFileStorage());
        var context = new AssistantToolContext(ownerId, project.Id);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("replaced"));

        string proposal = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"), ("contentBase64", base64));
        await InvokeAsync(provider, context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", base64), ("confirmationToken", TokenOf(proposal)));

        db.ProjectFiles.Single().SourceKind.Should().BeNull();
        long used = await new ReelBolt.Inference.Api.Services.Billing.StorageUsageService(db, Moq.Mock.Of<ReelBolt.Shared.Tenancy.IOrganizationEntitlements>())
            .GetUsedBytesAsync(project.OrganizationId, default);
        used.Should().Be(8);
    }

    [Fact]
    public async Task WriteProjectFile_overwrite_keeps_a_non_library_source_kind()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile existing = SeedFile(db, project.Id, "notes.md");
        existing.SourceKind = "github";
        db.SaveChanges();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), new FakeFileStorage());
        var context = new AssistantToolContext(ownerId, project.Id);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("replaced"));

        string proposal = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"), ("contentBase64", base64));
        await InvokeAsync(provider, context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", base64), ("confirmationToken", TokenOf(proposal)));

        db.ProjectFiles.Single().SourceKind.Should().Be("github");
    }

    [Fact]
    public async Task WriteProjectFile_overwrite_of_a_library_sound_is_charged_its_full_new_size()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile existing = SeedFile(db, project.Id, "notes.md");
        existing.SourceKind = "sfx_library";
        db.SaveChanges();
        var gate = new ReelBolt.WorkflowEngine.Tests.Uploads.UploadsPlanGate();
        gate.Violations.Enqueue(new ReelBolt.Shared.Billing.PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), new FakeFileStorage(), planGate: gate);
        var context = new AssistantToolContext(ownerId, project.Id);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789"));

        string proposal = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"), ("contentBase64", base64));
        string applied = await InvokeAsync(provider, context, "ApplyWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", base64), ("confirmationToken", TokenOf(proposal)));

        gate.UploadChecks.Should().Contain(10L, "the exempt row's 5 bytes were never counted, so none are netted off");
        gate.UploadChecks.Should().NotContain(5L);
        db.ProjectFiles.Single().SizeBytes.Should().Be(5, "the refused overwrite changed nothing");
        applied.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DeleteProjectFile_of_a_library_sound_picture_source_frees_no_quota_bytes()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile file = SeedFile(db, project.Id, "photo.png", $"projects/{project.Id}/userFiles/photo.png", "image/png");
        file.SourceKind = "sfx_library";
        db.SaveChanges();
        await PictureCopySeed.PictureOverAsync(db, file);
        await PictureCopySeed.PictureOverAsync(db, file);
        var gate = new ReelBolt.WorkflowEngine.Tests.Uploads.UploadsPlanGate();
        gate.Violations.Enqueue(new ReelBolt.Shared.Billing.PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), new FakeFileStorage(), planGate: gate);
        var context = new AssistantToolContext(ownerId, project.Id);

        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteProjectFile", ("fileId", file.Id)));
        await InvokeAsync(provider, context, "ApplyDeleteProjectFile", ("fileId", file.Id), ("confirmationToken", token));

        gate.UploadChecks.Should().Equal([file.SizeBytes * 2]);
    }

    [Fact]
    public async Task WriteProjectFile_refuses_content_over_the_cap_and_a_non_text_mime_type()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        var storage = new FakeFileStorage();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);

        byte[] tooBig = new byte[AssistantFileTools.MaxContentBytes + 1];
        db.ResetSaveCount();
        string oversized = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "big.md"), ("mimeType", "text/markdown"),
            ("contentBase64", Convert.ToBase64String(tooBig)));

        ErrorOf(oversized).Should().Contain(AssistantFileTools.MaxContentBytes.ToString());

        string binary = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "clip.mp4"), ("mimeType", "video/mp4"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("x"))));

        ErrorOf(binary).Should().Contain("not a text-editable asset");

        string badBase64 = await InvokeAsync(provider, context, "ProposeWriteProjectFile",
            ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", "!!! not base64 !!!"));

        ErrorOf(badBase64).Should().Be("contentBase64 is not valid base64");

        AssertNothingWasWritten(db);
        storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadProjectFile_returns_text_for_an_owned_file_and_refuses_a_binary_one()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        ProjectFile text = SeedFile(db, project.Id, "notes.md", "key-text", "text/markdown");
        SeedFile(db, project.Id, "clip.mp4", "key-video", "video/mp4");
        var storage = new FakeFileStorage();
        storage.Objects["key-text"] = Encoding.UTF8.GetBytes("hello assistant");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), storage);
        var context = new AssistantToolContext(ownerId, project.Id);

        string read = await InvokeAsync(provider, context, "ReadProjectFile", ("fileId", text.Id));
        using (JsonDocument doc = JsonDocument.Parse(read))
        {
            doc.RootElement.GetProperty("content").GetString().Should().Be("hello assistant");
            doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        }

        ProjectFile binary = db.ProjectFiles.Single(f => f.OriginalFileName == "clip.mp4");
        string refused = await InvokeAsync(provider, context, "ReadProjectFile", ("fileId", binary.Id));
        ErrorOf(refused).Should().Contain("not a text-editable asset");
    }

    // ---------------------------------------------------------------------
    // Acceptance: CreateProject works with no project in context; ExecuteWorkflow still refuses
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CreateProject_works_with_no_project_in_context()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));

        // Review Focus #5: a page with no project is a normal state, and creating one is exactly what
        // an assistant must be able to do from it.
        var context = new AssistantToolContext(ownerId);
        string proposal = await InvokeAsync(provider, context, "ProposeCreateProject",
            ("name", "Launch video"), ("description", "made from a page with no project"));
        TokenOf(proposal).Should().NotBeNullOrWhiteSpace();

        string applied = await InvokeAsync(provider, context, "ApplyCreateProject",
            ("name", "Launch video"), ("description", "made from a page with no project"),
            ("confirmationToken", TokenOf(proposal)));

        using JsonDocument doc = JsonDocument.Parse(applied);
        doc.RootElement.GetProperty("name").GetString().Should().Be("Launch video");

        Project created = db.Projects.Should().ContainSingle().Subject;
        created.OwnerId.Should().Be(ownerId);
        created.Name.Should().Be("Launch video");
        created.Description.Should().Be("made from a page with no project");
    }

    [Fact]
    public async Task CreateProject_records_project_created_for_the_caller()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        var log = new ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventLog();
        var emitter = new ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter(
            log, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter>.Instance);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId), productEvents: emitter);
        var context = new AssistantToolContext(ownerId);

        string proposal = await InvokeAsync(provider, context, "ProposeCreateProject", ("name", "Launch video"), ("description", null));
        await InvokeAsync(provider, context, "ApplyCreateProject",
            ("name", "Launch video"), ("description", null), ("confirmationToken", TokenOf(proposal)));

        log.Rows.Should().ContainSingle().Which.Event.Should().Be("activation.project_created");
        log.Rows[0].UserId.Should().Be(ownerId);
    }

    [Fact]
    public async Task CreateProject_refuses_a_token_minted_for_a_different_name()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));
        var context = new AssistantToolContext(ownerId);

        string token = TokenOf(await InvokeAsync(provider, context, "ProposeCreateProject",
            ("name", "Proposed name"), ("description", "d")));

        db.ResetSaveCount();
        string applied = await InvokeAsync(provider, context, "ApplyCreateProject",
            ("name", "A name the user never saw"), ("description", "d"), ("confirmationToken", token));

        ErrorOf(applied).Should().Be("stale confirmationToken, re-propose and try again");
        AssertNothingWasWritten(db);
        db.Projects.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteWorkflow_still_refuses_without_a_project_and_never_acquires_a_slot()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        var guard = new Mock<IAssistantExecutionGuard>();
        guard.Setup(g => g.TryAcquireAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunRateDecision(true, RunRateBucket.Paid, 1, 10, 3600, 0));

        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(
            db, new FakeCurrentUser(ownerId), executionGuard: guard.Object);

        string text = await InvokeAsync(provider, new AssistantToolContext(ownerId), "ExecuteWorkflow",
            ("workflowId", Guid.NewGuid()), ("userRequest", null), ("regenerateVideoClips", null));

        ErrorOf(text).Should().Be("no project in context");

        // The guard (its classify-under-lock overload, T-012) is not consulted at all on the refusal path —
        // a rate-limit slot must not be burned by a call that never reached an execution.
        guard.Verify(g => g.TryAcquireAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteWorkflow_with_video_step_asks_for_a_decision_and_never_executes()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id);
        db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = "{\"maxSpendCredits\": 2.5}"
        });
        db.SaveChanges();
        var guard = new Mock<IAssistantExecutionGuard>();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(
            db, new FakeCurrentUser(ownerId), executionGuard: guard.Object);

        string text = await InvokeAsync(provider, new AssistantToolContext(ownerId, project.Id), "ExecuteWorkflow",
            ("workflowId", workflow.Id), ("userRequest", null), ("regenerateVideoClips", null));

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("needsDecision").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("estimatedMaxCostUsd").GetDecimal().Should().Be(2.5m);
        guard.Verify(g => g.TryAcquireAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // Workflow deletion
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DeleteWorkflow_token_stops_verifying_when_the_workflow_is_renamed()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        WorkflowDefinition workflow = SeedWorkflow(db, project.Id, "Draft pipeline");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));
        var context = new AssistantToolContext(ownerId, project.Id);

        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteWorkflow", ("workflowId", workflow.Id)));
        workflow.Name = "PROMOTED pipeline";
        db.SaveChanges();

        db.ResetSaveCount();
        string applied = await InvokeAsync(provider, context, "ApplyDeleteWorkflow",
            ("workflowId", workflow.Id), ("confirmationToken", token));

        ErrorOf(applied).Should().Be("stale confirmationToken, re-propose and try again");
        AssertNothingWasWritten(db);
        db.WorkflowDefinitions.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteWorkflow_refuses_a_workflow_of_another_project_and_writes_nothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project mine = SeedProject(db, ownerId, "Mine");
        Project other = SeedProject(db, ownerId, "Other");
        WorkflowDefinition theirs = SeedWorkflow(db, other.Id, "Elsewhere");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));
        var context = new AssistantToolContext(ownerId, mine.Id);

        db.ResetSaveCount();
        string applied = await InvokeAsync(provider, context, "ApplyDeleteWorkflow",
            ("workflowId", theirs.Id), ("confirmationToken", "anything"));

        ErrorOf(applied).Should().Be("workflow not found");
        AssertNothingWasWritten(db);
        db.WorkflowDefinitions.Should().ContainSingle();
    }

    [Fact]
    public void DeleteWorkflow_canonical_payload_binds_the_project_the_id_and_the_name()
    {
        Guid projectId = StrangeProjectId;
        Guid workflowId = Guid.Parse("22222222-3333-4444-5555-666666666666");

        AssistantWorkflowTools.BuildDeleteWorkflowPayload(projectId, workflowId, "Exact Name")
            .Should().Be(
                "{\"op\":\"DeleteWorkflow\",\"projectId\":\"11111111-2222-3333-4444-555555555555\","
                + "\"workflowId\":\"22222222-3333-4444-5555-666666666666\",\"name\":\"Exact Name\"}");
    }

    [Fact]
    public async Task DeleteWorkflow_happy_path_deletes_only_the_confirmed_workflow()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Project project = SeedProject(db, ownerId);
        WorkflowDefinition doomed = SeedWorkflow(db, project.Id, "Doomed");
        WorkflowDefinition kept = SeedWorkflow(db, project.Id, "Kept");
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(ownerId));
        var context = new AssistantToolContext(ownerId, project.Id);

        string token = TokenOf(await InvokeAsync(provider, context, "ProposeDeleteWorkflow", ("workflowId", doomed.Id)));
        await InvokeAsync(provider, context, "ApplyDeleteWorkflow",
            ("workflowId", doomed.Id), ("confirmationToken", token));

        db.WorkflowDefinitions.Select(w => w.Id).Should().BeEquivalentTo(new[] { kept.Id });
    }

    private static string TokenOf(string proposal)
    {
        using JsonDocument doc = JsonDocument.Parse(proposal);
        return doc.RootElement.GetProperty("confirmationToken").GetString()!;
    }

    private static (string Key, object? Value)[] ApplyArguments(
        string toolName,
        ProjectFile file,
        WorkflowDefinition workflow,
        string confirmationToken) => toolName switch
        {
            "ApplyCreateProject" => [("name", "Created by the assistant"), ("description", (object?)null), ("confirmationToken", confirmationToken)],
            "ApplyUpdateProject" => [("projectId", (object?)null), ("name", "Renamed"), ("description", (object?)null), ("confirmationToken", confirmationToken)],
            "ApplyWriteProjectFile" =>
            [
                ("directoryPath", null), ("fileName", "notes.md"), ("mimeType", "text/markdown"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))),
            ("confirmationToken", confirmationToken)
            ],
            "ApplyDeleteProjectFile" => [("fileId", file.Id), ("confirmationToken", confirmationToken)],
            "ApplyDeleteWorkflow" => [("workflowId", workflow.Id), ("confirmationToken", confirmationToken)],
            "ApplyDeleteProject" => [("projectId", (object?)null), ("confirmationToken", confirmationToken)],
            _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "no arguments defined for this tool")
        };
}

/// <summary>
/// Builds an <see cref="AssistantToolProvider"/> over the four platform surfaces for the tests that
/// do not care about their wiring. Propose and apply MUST share one
/// <see cref="IConfirmationTokenService"/>, so it is created once here and handed to all of them.
/// </summary>
internal static class AssistantToolTestFactory
{
    /// <summary>The default guard: refuses every run, as an unconfigured guard mock did when it answered <c>false</c>.</summary>
    private static IAssistantExecutionGuard DenyingGuard()
    {
        var guard = new Mock<IAssistantExecutionGuard>();
        guard.Setup(g => g.TryAcquireAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunRateDecision(false, RunRateBucket.Paid, 10, 10, 3600, 720));
        return guard.Object;
    }

    internal static AssistantToolProvider CreateProvider(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IFileStorageService? fileStorage = null,
        IPublishEndpoint? publishEndpoint = null,
        IAssistantExecutionGuard? executionGuard = null,
        IConfirmationTokenService? confirmationTokens = null,
        ISecretProtector? secretProtector = null,
        IPlatformDocsSearch? docsSearch = null,
        ReelBolt.Inference.Api.Services.Inference.InferenceProviderTester? providerTester = null,
        ReelBolt.Inference.Api.Services.Billing.IWorkflowCostEstimator? costEstimator = null,
        IWorkflowExecutionService? executionService = null,
        Func<IConfirmationTokenService, AssistantAdminTools>? adminTools = null,
        ReelBolt.Shared.Inference.IInferenceProviderResolver? providerResolver = null,
        ReelBolt.Inference.Api.Services.Workflows.IBriefFileResolver? briefFiles = null,
        ReelBolt.Inference.Api.Services.Billing.IPlanGateService? planGate = null,
        ReelBolt.Inference.Api.Services.Workflows.IWorkflowLinter? linter = null,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null,
        ReelBolt.Inference.Api.Services.Workflows.AppInputValueValidator? appInputs = null,
        ReelBolt.Inference.Api.Services.Billing.IWorkflowExecutionPreflight? executionPreflight = null,
        bool avatarsEnabled = false,
        ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? historyEraser = null)
    {
        IConfirmationTokenService tokens = confirmationTokens ?? ConfirmationTokenService.CreateEphemeral();
        var readiness = new ReelBolt.Inference.Api.Services.Workflows.ProjectReadinessFacts(db);

        return new AssistantToolProvider(
            db,
            new WorkflowEditDiffService(db),
            executionService ?? Mock.Of<IWorkflowExecutionService>(),
            executionGuard ?? DenyingGuard(),
            currentUser,
            new AssistantProjectTools(db, tokens, TemplateProvisioning(db, avatarsEnabled), currentUser, TestTenancy.Access(db, currentUser), productEvents: productEvents, historyEraser: historyEraser),
            new AssistantFileTools(db, tokens, fileStorage ?? new FakeFileStorage(), publishEndpoint ?? Mock.Of<IPublishEndpoint>(), new RecordingSummarizationQueue(), TestTenancy.Access(db, currentUser), planGate),
            new AssistantWorkflowTools(db, tokens, TestTenancy.Access(db, currentUser), currentUser, publishEndpoint: publishEndpoint),
            adminTools?.Invoke(tokens)
                ?? new AssistantAdminTools(db, tokens, currentUser, secretProtector ?? new FakeSecretProtector(), tester: providerTester),
            new AssistantKnowledgeTools(db, docsSearch ?? new FakePlatformDocsSearch(), TemplateProvisioning(db, avatarsEnabled), null, TestTenancy.Access(db, currentUser), readiness),
            new AssistantStyleTools(db, tokens, TestTenancy.Access(db, currentUser)),
            DesignSystemTools(db, currentUser, tokens, fileStorage),
            PictureTools(db, currentUser, tokens, fileStorage),
            P0Tools(db, currentUser, tokens).Outputs,
            P0Tools(db, currentUser, tokens).Uploads,
            P0Tools(db, currentUser, tokens).Runs,
            P0Tools(db, currentUser, tokens).Revisions,
            P0Tools(db, currentUser, tokens).ChangeRequests,
            P2Tools(db, currentUser, tokens, fileStorage).Patch,
            P2Tools(db, currentUser, tokens, fileStorage).PictureDocuments,
            P2Tools(db, currentUser, tokens, fileStorage).GreenScreen,
            TestTenancy.Access(db, currentUser),
            null,
            null,
            // B7: the execute preflight the ExecuteWorkflow tool runs. A null estimator here is the
            // same "nothing wired" state a host without billing has, so the preflight refuses nothing.
            executionPreflight ?? new ReelBolt.Inference.Api.Services.Billing.WorkflowExecutionPreflight(db, costEstimator),
            designBuildGate: null,
            providerResolver: providerResolver,
            briefFiles: briefFiles,
            recipeTools: RecipeTools(db, currentUser, tokens, costEstimator, linter, planGate),
            linter: linter,
            mediaOverlayTools: new AssistantMediaOverlayTools(
                db, TestTenancy.Access(db, currentUser), new WorkflowEditDiffService(db, tokens), tokens, currentUser),
            objectEffectTools: ObjectEffectTools(db, currentUser, tokens),
            audioCleanupTools: new AssistantAudioCleanupTools(
                db, TestTenancy.Access(db, currentUser),
                new ReelBolt.Inference.Api.Services.Media.AudioCleanupService(
                    db, TestTenancy.Access(db, currentUser), currentUser, Mock.Of<IWorkflowExecutionService>(),
                    Mock.Of<ReelBolt.Inference.Api.Services.Workflows.IAssistantExecutionGuard>(), Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>()),
                tokens, currentUser),
            voiceTools: VoiceTools(db, currentUser, tokens),
            avatarTools: AvatarTools(db, currentUser, tokens: tokens),
            appInputs: appInputs,
            videoEnhanceTools: VideoEnhanceTools(db, currentUser, tokens),
            marketplaceTools: MarketplaceTools(db, currentUser, tokens),
            historyTools: new AssistantHistoryTools(
                tokens, TestTenancy.Access(db, currentUser),
                Mock.Of<ReelBolt.Inference.Api.Services.History.IProjectHistoryService>(),
                Mock.Of<ReelBolt.Inference.Api.Services.History.IProjectHistoryRestoreService>()),
            postTextTools: new ReelBolt.Inference.Api.Agents.Tools.AssistantPostTextTools(TestTenancy.Access(db, currentUser), Mock.Of<IServiceProvider>()),
            videoEventsTools: VideoEventsTools(db, currentUser),
            socialTools: SocialTools(db, currentUser, tokens));
    }

    /// <summary>The social publishing tools over stand-in services (the surface tests only count, classify and describe them).</summary>
    internal static ReelBolt.Inference.Api.Agents.Tools.AssistantSocialTools SocialTools(InferenceApiDbContext db, ICurrentUser currentUser, IConfirmationTokenService tokens) =>
        new(tokens, TestTenancy.Access(db, currentUser), db, currentUser,
            new ReelBolt.WorkflowEngine.Tests.SocialPublishing.FakeEntitlements(),
            Mock.Of<ReelBolt.Inference.Api.Services.SocialPublishing.ISocialOutputResolver>(), Mock.Of<IServiceProvider>());

    /// <summary>The two read-only video event tools over a stand-in store (the surface tests never read an analysis).</summary>
    internal static AssistantVideoEventsTools VideoEventsTools(InferenceApiDbContext db, ICurrentUser currentUser) =>
        new(db, TestTenancy.Access(db, currentUser), Mock.Of<ReelBolt.Inference.Api.Services.Storage.IFileStorageService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantVideoEventsTools>.Instance);

    /// <summary>The community-recipe tools over this context (flag off: the surface tests only count and describe them).</summary>
    internal static AssistantMarketplaceTools MarketplaceTools(InferenceApiDbContext db, ICurrentUser currentUser, IConfirmationTokenService tokens)
    {
        var access = TestTenancy.Access(db, currentUser);
        var options = new ReelBolt.Inference.Api.Services.Marketplace.MarketplaceOptions();
        var snapshot = Mock.Of<Microsoft.Extensions.Options.IOptionsSnapshot<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceOptions>>(o => o.Value == options);
        var query = new ReelBolt.Inference.Api.Services.Marketplace.MarketplacePublicQuery(
            db, new ReelBolt.Inference.Api.Services.Marketplace.InMemoryRecipeSearch(), snapshot);
        var copy = new ReelBolt.Inference.Api.Services.Marketplace.MarketplaceCopyService(
            db, currentUser, access, Mock.Of<IProjectReadinessFacts>(), new StepProviderOwnershipValidator(db),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Marketplace.MarketplaceCopyService>.Instance);
        return new AssistantMarketplaceTools(db, access, tokens, currentUser, query, copy, snapshot);
    }

    /// <summary>The video-enhance pair over the real service and a stand-in execution service (the surface tests never start a run).</summary>
    internal static AssistantVideoEnhanceTools VideoEnhanceTools(InferenceApiDbContext db, ICurrentUser currentUser, IConfirmationTokenService tokens) =>
        new(db, TestTenancy.Access(db, currentUser),
            new ReelBolt.Inference.Api.Services.Media.VideoEnhanceService(
                db, Mock.Of<IWorkflowExecutionService>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Media.VideoEnhanceService>.Instance),
            tokens, currentUser);

    /// <summary>The avatar tools, people switched on, over no shared services (their own tests cover the services, the switches and the rule).</summary>
    internal static AssistantAvatarTools AvatarTools(
        InferenceApiDbContext db, ICurrentUser currentUser, bool enabled = true, IConfirmationTokenService? tokens = null) =>
        new(db, TestTenancy.Access(db, currentUser), Mock.Of<ReelBolt.Inference.Api.Services.Storage.IFileStorageService>(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Avatars:Enabled"] = enabled ? "true" : "false" })
                .Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantAvatarTools>.Instance,
            confirmationTokens: tokens,
            currentUser: currentUser);

    /// <summary>The voice tools over a stand-in speech provider with no library (the surface tests never browse).</summary>
    internal static AssistantVoiceTools VoiceTools(InferenceApiDbContext db, ICurrentUser currentUser, IConfirmationTokenService tokens) =>
        new(db, TestTenancy.Access(db, currentUser),
            new ReelBolt.Inference.Api.Services.Voices.ProjectVoiceLibraryService(
                db, Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>(), Mock.Of<ReelBolt.Shared.Inference.ISpeechSynthesisClientFactory>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Voices.ProjectVoiceLibraryService>.Instance),
            tokens, currentUser);

    /// <summary>The object-effect pair over a stand-in analysis service (the surface tests never call it).</summary>
    internal static AssistantObjectEffectTools ObjectEffectTools(InferenceApiDbContext db, ICurrentUser currentUser, IConfirmationTokenService tokens)
    {
        var analysis = Mock.Of<ReelBolt.Inference.Api.Services.ObjectAnalysis.IObjectAnalysisService>();
        var applier = new ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectEffectApplier(
            db, analysis, Mock.Of<ReelBolt.Inference.Api.Services.EditTimelines.IEditTimelineWriter>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectEffectApplier>.Instance);
        return new AssistantObjectEffectTools(db, TestTenancy.Access(db, currentUser), analysis, applier, tokens, currentUser);
    }

    /// <summary>The recipe-first surface (readiness, estimate, create-from-recipe) over this context.</summary>
    internal static AssistantRecipeTools RecipeTools(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IConfirmationTokenService tokens,
        ReelBolt.Inference.Api.Services.Billing.IWorkflowCostEstimator? costEstimator = null,
        ReelBolt.Inference.Api.Services.Workflows.IWorkflowLinter? linter = null,
        ReelBolt.Inference.Api.Services.Billing.IPlanGateService? planGate = null) =>
        new(db, TestTenancy.Access(db, currentUser), new ReelBolt.Inference.Api.Services.Workflows.ProjectReadinessFacts(db),
            TemplateProvisioning(db), new WorkflowEditDiffService(db, tokens), tokens, currentUser,
            costEstimator: costEstimator, linter: linter, planGate: planGate);

    /// <summary>
    /// The design-system surface (SPEC 15) over this context, with the real
    /// <see cref="ReelBolt.Inference.Api.Services.DesignSystems.DesignSystemService"/> so a test can
    /// create and decide real Drafts and proposals in the in-memory database.
    /// </summary>
    internal static AssistantDesignSystemTools DesignSystemTools(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IConfirmationTokenService tokens,
        IFileStorageService? fileStorage = null)
    {
        var service = new ReelBolt.Inference.Api.Services.DesignSystems.DesignSystemService(
            db,
            fileStorage ?? new FakeFileStorage(),
            Mock.Of<ReelBolt.Inference.Api.Services.Billing.IPlanGateService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.DesignSystems.DesignSystemService>.Instance);
        return new AssistantDesignSystemTools(db, tokens, TestTenancy.Access(db, currentUser), service);
    }

    /// <summary>
    /// The picture-editor surface over the given context. Its writes go through
    /// <paramref name="commands"/> (default: the REAL controller-backed adapter over this context, so a
    /// test exercises the editor's own routes); the graphics service is the real one over this context.
    /// </summary>
    internal static AssistantPictureTools PictureTools(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IConfirmationTokenService tokens,
        IFileStorageService? fileStorage = null,
        ReelBolt.Inference.Api.Services.Assistant.IImageDocumentCommands? commands = null,
        ReelBolt.Shared.Inference.IInferenceProviderResolver? resolver = null,
        ReelBolt.Inference.Api.Services.Billing.IWorkflowCostEstimator? costEstimator = null,
        ReelBolt.Shared.Metering.IModelPriceService? prices = null,
        ReelBolt.Shared.Inference.IChatClientFactory? chatClients = null,
        ReelBolt.Inference.Api.Services.Avatars.IProjectSubjectReader? subjectReader = null,
        ReelBolt.Inference.Api.Services.Avatars.AvatarOptions? avatarOptions = null)
    {
        IFileStorageService storage = fileStorage ?? new FakeFileStorage();
        ReelBolt.Inference.Api.Services.Auth.IProjectAccessService access = TestTenancy.Access(db, currentUser);
        ReelBolt.Shared.Inference.IInferenceProviderResolver providers =
            resolver ?? Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>();
        var graphics = new ReelBolt.Inference.Api.Services.Graphics.GraphicDesignService(
            db,
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionService>.Instance),
            storage,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Graphics.GraphicDesignService>.Instance,
            costEstimator);
        return new AssistantPictureTools(
            db, tokens, access, currentUser,
            commands ?? PictureCommands(db, currentUser, storage, providers),
            graphics, providers, storage, prices, costEstimator, chatClients,
            subjectReader: subjectReader,
            avatarOptions: avatarOptions is null ? null : Microsoft.Extensions.Options.Options.Create(avatarOptions));
    }

    /// <summary>
    /// The four P0 surfaces (outputs, uploads, runs, revisions) over the given context, every collaborator a
    /// do-nothing mock: they exist so a provider built here registers the full tool set.
    /// </summary>
    internal static (AssistantOutputTools Outputs, AssistantUploadTools Uploads, AssistantRunTools Runs, AssistantRevisionTools Revisions, AssistantChangeRequestTools ChangeRequests) P0Tools(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IConfirmationTokenService tokens,
        IAssistantExecutionGuard? guard = null)
    {
        ReelBolt.Inference.Api.Services.Auth.IProjectAccessService access = TestTenancy.Access(db, currentUser);
        return (
            new AssistantOutputTools(
                db, tokens, access,
                Mock.Of<ReelBolt.Inference.Api.Services.Outputs.IOutputDescriber>(),
                new FakeFileStorage(),
                Mock.Of<ReelBolt.Inference.Api.Services.Outputs.IStorageObjectCopier>(),
                Mock.Of<IPublishEndpoint>(),
                new RecordingSummarizationQueue()),
            new AssistantUploadTools(
                access, currentUser, tokens, null,
                Mock.Of<ReelBolt.Inference.Api.Services.Uploads.IUrlImportService>(),
                Mock.Of<ReelBolt.Inference.Api.Services.Uploads.IProjectFileAddedEffects>(),
                db),
            new AssistantRunTools(db, access, currentUser, Mock.Of<ReelBolt.Inference.Api.Services.Billing.ICreditBalanceReader>()),
            new AssistantRevisionTools(
                db, access,
                Mock.Of<ReelBolt.Inference.Api.Services.Compositions.ICompositionRevisionService>(),
                guard ?? Mock.Of<IAssistantExecutionGuard>(), tokens, currentUser),
            new AssistantChangeRequestTools(
                access,
                Mock.Of<ReelBolt.Inference.Api.Services.ChangeRequests.IStepChangeRequestService>(),
                guard ?? Mock.Of<IAssistantExecutionGuard>(), tokens, currentUser));
    }

    /// <summary>
    /// The three P2 surfaces (workflow patches and clones, picture documents, the green-screen insert) over the given
    /// context, so a provider built here registers the full tool set.
    /// </summary>
    internal static (AssistantWorkflowPatchTools Patch, AssistantPictureDocumentTools PictureDocuments, AssistantGreenScreenTools GreenScreen) P2Tools(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IConfirmationTokenService tokens,
        IFileStorageService? fileStorage = null)
    {
        IFileStorageService storage = fileStorage ?? new FakeFileStorage();
        ReelBolt.Inference.Api.Services.Auth.IProjectAccessService access = TestTenancy.Access(db, currentUser);
        var diff = new WorkflowEditDiffService(db, tokens);
        return (
            new AssistantWorkflowPatchTools(db, tokens, access, diff, new StepProviderOwnershipValidator(db)),
            new AssistantPictureDocumentTools(db, tokens, access, currentUser, PictureCommands(db, currentUser, storage, Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>()), storage),
            new AssistantGreenScreenTools(db, access, diff, tokens, currentUser));
    }

    /// <summary>The real controller-backed command adapter, over a service provider holding exactly what the image controllers need.</summary>
    internal static ReelBolt.Inference.Api.Services.Assistant.ControllerImageDocumentCommands PictureCommands(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IFileStorageService storage,
        ReelBolt.Shared.Inference.IInferenceProviderResolver providers,
        ReelBolt.Shared.Inference.IImageClientFactory? imageClients = null,
        ReelBolt.Shared.Inference.IImageAnalysisClientFactory? analysisClients = null)
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, db);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, currentUser);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, TestTenancy.Access(db, currentUser));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, storage);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, providers);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, imageClients ?? Mock.Of<ReelBolt.Shared.Inference.IImageClientFactory>());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, analysisClients ?? Mock.Of<ReelBolt.Shared.Inference.IImageAnalysisClientFactory>());
        return new ReelBolt.Inference.Api.Services.Assistant.ControllerImageDocumentCommands(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
    }

    /// <summary>
    /// Auto-template provisioning with the starter disabled, so a test project is created with
    /// exactly the rows the test seeded and nothing else.
    /// </summary>
    internal static WorkflowTemplateProvisioningService TemplateProvisioning(InferenceApiDbContext db, bool avatarsEnabled = false)
    {
        // A real (in-memory) configuration rather than a mock: the knowledge tools read
        // WorkflowTemplates:EnableAdvancedTemplates through GetValue, which a bare mock cannot serve.
        Microsoft.Extensions.Configuration.IConfiguration configuration =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowTemplates:EnableAutoStarter"] = "false", ["Avatars:Enabled"] = avatarsEnabled ? "true" : "false" })
                .Build();
        return new WorkflowTemplateProvisioningService(
            db, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowTemplateProvisioningService>.Instance, configuration);
    }
}

internal sealed class FakeCurrentUser(Guid userId) : ICurrentUser
{
    public Guid UserId { get; } = userId;
    public string Email { get; } = "test@example.com";
    public bool IsAuthenticated { get; } = true;
    public bool IsPlatformAdmin { get; } = false;
    public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
    public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
}

/// <summary>
/// An in-memory stand-in for object storage that records exactly what was uploaded and deleted, so a
/// test can prove a refused tool never reached storage and an accepted one went through
/// <see cref="IFileStorageService"/> rather than the filesystem.
/// </summary>
internal sealed class FakeFileStorage : IFileStorageService, IObjectListing
{
    internal sealed record Upload(Guid ProjectId, Guid FileId, string FileName, string ContentType, string Category, string? OriginalPath, byte[] Content, string StorageKey);

    public string BucketName => "fake-bucket";

    /// <summary>Last-modified time per object for the orphan pass; an object with no entry is "just written".</summary>
    public Dictionary<string, DateTime> ObjectLastModified { get; } = new(StringComparer.Ordinal);

    /// <summary>The orphan pass sees exactly what is in <see cref="Objects"/>, at its recorded age.</summary>
    public Task<IReadOnlyList<StoredObjectEntry>> ListAsync(Guid projectId, string category, CancellationToken ct)
    {
        string prefix = $"projects/{projectId}/{category}/";
        IReadOnlyList<StoredObjectEntry> entries = [.. Objects.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => new StoredObjectEntry(k, ObjectLastModified.TryGetValue(k, out DateTime modified) ? modified : DateTime.UtcNow))];
        return Task.FromResult(entries);
    }

    public List<Upload> Uploads { get; } = new();
    public List<string> DeletedKeys { get; } = new();

    /// <summary>Keys per <see cref="DeleteManyAsync"/> call, so a test can prove the 1,000-key batch bound.</summary>
    public List<int> DeleteBatchSizes { get; } = new();
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public Task<StoredFileObject> UploadAsync(
        Guid projectId,
        Guid fileId,
        Stream content,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        string category = "userFiles",
        string? originalPath = null)
    {
        using MemoryStream buffer = new();
        content.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        string normalizedOriginalPath = string.IsNullOrWhiteSpace(originalPath) ? fileName : originalPath;
        string extension = Path.GetExtension(normalizedOriginalPath);
        string storageFileName = extension is "" or "." ? fileId.ToString() : $"{fileId}{extension.ToLowerInvariant()}";
        int separator = normalizedOriginalPath.LastIndexOf('/');
        string? directoryPath = separator > 0 ? normalizedOriginalPath[..separator] : null;
        string storagePrefix = $"projects/{projectId}/{category}/";
        string storageKey = directoryPath is null
            ? $"{storagePrefix}{storageFileName}"
            : $"{storagePrefix}{directoryPath}/{storageFileName}";

        Uploads.Add(new Upload(projectId, fileId, fileName, contentType, category, originalPath, bytes, storageKey));
        Objects[storageKey] = bytes;

        return Task.FromResult(new StoredFileObject(
            storageKey,
            "fake-bucket",
            storagePrefix,
            "{}",
            category,
            Path.GetFileName(normalizedOriginalPath),
            normalizedOriginalPath,
            directoryPath,
            storageFileName));
    }

    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? bytes)
            ? new StoredObjectMetadata(bytes.Length, "application/octet-stream", ETagOf(bytes))
            : null);

    // An object's ETag changes whenever a new byte array is stored at the key (a re-PUT).
    private static string ETagOf(byte[] bytes) => $"etag-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bytes)}";

    public Task<bool> MoveIfMatchAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, string expectedETag, CancellationToken ct)
    {
        if (!Objects.TryGetValue(sourceStorageKey, out byte[]? bytes) || !string.Equals(ETagOf(bytes), expectedETag, StringComparison.Ordinal))
            return Task.FromResult(false);
        Objects[destinationStorageKey] = bytes;
        Objects.Remove(sourceStorageKey);
        return Task.FromResult(true);
    }

    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Objects.TryGetValue(storageKey, out byte[]? bytes) ? bytes : []));

    /// <summary>When set, every delete throws it after recording the attempt (a storage provider failure).</summary>
    public Exception? DeleteFailure { get; set; }

    public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        DeletedKeys.Add(storageKey);
        if (DeleteFailure is not null) throw DeleteFailure;
        Objects.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
    {
        DeleteBatchSizes.Add(storageKeys.Count);
        int count = 0;
        foreach (string storageKey in storageKeys)
        {
            if (!Objects.Remove(storageKey)) continue;
            DeletedKeys.Add(storageKey);
            count++;
        }
        return Task.FromResult(count);
    }

    public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Every server-side copy as (source, destination); the bytes follow when the source is held.</summary>
    public List<(string From, string To)> Copies { get; } = new();

    public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        Copies.Add((sourceStorageKey, destinationStorageKey));
        if (Objects.TryGetValue(sourceStorageKey, out byte[]? bytes)) Objects[destinationStorageKey] = bytes;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The ETag the media-derivatives publisher reads best-effort before publishing; a fake store
    /// has no object versions, so <c>null</c> ("could not be read") is the honest answer and
    /// exercises the publisher's tolerated-absence path.
    /// </summary>
    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

internal sealed class RecordingSummarizationQueue : IBackgroundTaskQueue<FileSummarizationTask>
{
    public List<FileSummarizationTask> Queued { get; } = new();

    public bool TryQueue(FileSummarizationTask task)
    {
        Queued.Add(task);
        return true;
    }

    public ValueTask<FileSummarizationTask> DequeueAsync(CancellationToken ct) =>
        throw new NotSupportedException("not used in tests");

    public void Complete(FileSummarizationTask task) { }

    public bool IsTracked(FileSummarizationTask task) => Queued.Contains(task);

    public int FreeCapacity => int.MaxValue;

    public int TrackedCount => Queued.Count;
}

/// <summary>
/// A documentation search that returns a fixed result and records every query, so a test can prove
/// what the tool asked for without Qdrant or an embedding provider.
/// </summary>
internal sealed class FakePlatformDocsSearch(PlatformDocsSearchResult? result = null) : IPlatformDocsSearch
{
    public List<(string Query, int Limit)> Queries { get; } = new();

    public Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct)
    {
        Queries.Add((query, limit));
        return Task.FromResult(result ?? new PlatformDocsSearchResult(PlatformDocsSearchStatus.Ok, []));
    }
}
