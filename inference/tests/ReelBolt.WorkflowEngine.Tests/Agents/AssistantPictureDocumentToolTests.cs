using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// ProposeCreatePictureDocument / ApplyCreatePictureDocument: a render or project file becomes a picture
/// document in the thumbnail studio, through the editor's own create action. A video is refused with a pointer
/// to ExtractFrame; another project's file is absent; a proposal writes nothing; the token binds the arguments.
/// </summary>
public class AssistantPictureDocumentToolTests
{
    private sealed class Caller(Guid id, OrganizationRole role = OrganizationRole.Member) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole { get; } = role;
        public bool EmailVerified => true;
    }

    /// <summary>Counts how many times the tool consulted the project access service.</summary>
    private sealed class CountingAccess(IProjectAccessService inner) : IProjectAccessService
    {
        public int Calls { get; private set; }
        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct) { Calls++; return inner.GetAsync(projectId, permission, ct); }
        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) { Calls++; return inner.Get(projectId, permission); }
        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => inner.AuthorizeOrganization(permission);
    }

    private sealed class World : IDisposable
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Me { get; } = Guid.NewGuid();
        public Caller User { get; }
        public Project Project { get; }
        public ProjectFile Image { get; }
        public ProjectFile Video { get; }
        public FakeFileStorage Storage { get; } = new();
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();
        public CountingAccess Access { get; }

        public World()
        {
            User = new Caller(Me);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = Me, OrganizationId = TestTenancy.OrgOf(Me), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Image = File("thumb.jpg", "image/jpeg", Project.Id);
            Video = File("film.mp4", "video/mp4", Project.Id);
            Db.Projects.Add(Project);
            Db.ProjectFiles.AddRange(Image, Video);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            Storage.Objects[Image.StorageKey] = PngHeader(1920, 1080);
            Access = new CountingAccess(TestTenancy.Access(Db, User));
        }

        public ProjectFile File(string name, string mime, Guid projectId) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = name,
            MimeType = mime,
            StorageKey = $"projects/{projectId}/userFiles/{name}",
            StorageFileName = name,
            Category = "userFiles",
            UploadedAt = DateTime.UtcNow
        };

        public AssistantPictureDocumentTools Tools(IImageDocumentCommands? commands = null) => new(
            Db, Tokens, Access, User,
            commands ?? AssistantToolTestFactory.PictureCommands(Db, User, Storage, Moq.Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>()),
            Storage);

        public AssistantToolContext Context() => new(Me, Project.Id, null, null);

        public void Dispose() => Db.Dispose();
    }

    /// <summary>A minimal PNG header: signature + IHDR carrying the size.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        List<byte> b = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        b.AddRange(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(width)));
        b.AddRange(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(height)));
        b.AddRange(new byte[] { 8, 2, 0, 0, 0 });
        return b.ToArray();
    }

    private static async Task<JsonElement> Invoke(AssistantPictureDocumentTools tools, AssistantToolContext context, string name, params (string Key, object? Value)[] args)
    {
        AIFunction tool = tools.GetTools(context).Single(t => t.Name == name);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        object? raw = await tool.InvokeAsync(arguments);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string? ErrorOf(JsonElement r) => r.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    [Fact]
    public void The_surface_is_one_propose_apply_pair_with_static_captions()
    {
        using World w = new();
        w.Tools().GetTools(w.Context()).Select(t => t.Name).Should().BeEquivalentTo("ProposeCreatePictureDocument", "ApplyCreatePictureDocument");
        AssistantPictureDocumentTools.Captions.Keys.Should().BeEquivalentTo("ProposeCreatePictureDocument", "ApplyCreatePictureDocument");
    }

    [Fact]
    public async Task A_proposal_names_the_image_costs_nothing_and_writes_nothing()
    {
        using World w = new();

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("fileId", w.Image.Id), ("name", "Launch cover"));

        ErrorOf(proposal).Should().BeNull();
        proposal.GetProperty("confirmationToken").GetString().Should().NotBeNullOrEmpty();
        proposal.GetProperty("paid").GetBoolean().Should().BeFalse();
        proposal.GetProperty("summary").GetString().Should().Contain("thumb.jpg").And.Contain("Launch cover");
        (await w.Db.EditImageDocuments.CountAsync()).Should().Be(0);
        w.Access.Calls.Should().Be(1, "authority is consulted once per call");
    }

    [Fact]
    public async Task Apply_creates_the_document_through_the_editors_create_action_and_returns_its_id_name_and_size()
    {
        using World w = new();
        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("fileId", w.Image.Id), ("name", "Launch cover"));

        JsonElement result = await Invoke(w.Tools(), w.Context(), "ApplyCreatePictureDocument",
            ("fileId", w.Image.Id), ("name", "Launch cover"), ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));

        ErrorOf(result).Should().BeNull();
        Guid documentId = result.GetProperty("documentId").GetGuid();
        result.GetProperty("name").GetString().Should().Be("Launch cover");
        result.GetProperty("width").GetInt32().Should().Be(1920);
        result.GetProperty("height").GetInt32().Should().Be(1080);

        EditImageDocument doc = await w.Db.EditImageDocuments.Include(d => d.Layers).SingleAsync();
        doc.Id.Should().Be(documentId);
        doc.ProjectId.Should().Be(w.Project.Id);
        doc.SourceProjectFileId.Should().Be(w.Image.Id);
        doc.Layers.Should().ContainSingle().Which.Kind.Should().Be(EditImageLayerKind.Source);
    }

    [Fact]
    public async Task A_video_is_refused_with_a_pointer_to_ExtractFrame_and_nothing_is_written()
    {
        using World w = new();

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("fileId", w.Video.Id));

        ErrorOf(proposal).Should().Contain("ExtractFrame");
        proposal.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        (await w.Db.EditImageDocuments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_thumbnail_step_result_is_resolved_to_the_project_file_it_registered()
    {
        using World w = new();
        Guid executionId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        w.Db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = w.Project.Id, OrganizationId = TestTenancy.OrgOf(w.Me) });
        w.Db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = resultId,
            WorkflowExecutionId = executionId,
            Status = StepStatus.Completed,
            Output = JsonSerializer.Serialize(new { status = "completed", projectFileId = w.Image.Id, thumbnailStorageKey = w.Image.StorageKey })
        });
        await w.Db.SaveChangesAsync();

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("outputId", resultId));

        ErrorOf(proposal).Should().BeNull();
        proposal.GetProperty("summary").GetString().Should().Contain("thumb.jpg");
        proposal.ToString().Should().NotContain("projects/", "no storage key ever leaves the tool");
    }

    [Fact]
    public async Task A_video_render_output_is_refused_with_a_pointer_to_ExtractFrame()
    {
        using World w = new();
        Guid executionId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        w.Db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = w.Project.Id, OrganizationId = TestTenancy.OrgOf(w.Me) });
        w.Db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = resultId,
            WorkflowExecutionId = executionId,
            Status = StepStatus.Completed,
            Output = "{\"status\":\"completed\"}",
            OutputStorageKey = $"projects/{w.Project.Id}/outputFiles/e/film.mp4"
        });
        await w.Db.SaveChangesAsync();

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("outputId", resultId));

        ErrorOf(proposal).Should().Contain("ExtractFrame");
    }

    [Fact]
    public async Task Another_projects_file_is_reported_as_not_found()
    {
        using World w = new();
        ProjectFile foreign = w.File("secret.jpg", "image/jpeg", Guid.NewGuid());
        w.Db.ProjectFiles.Add(foreign);
        await w.Db.SaveChangesAsync();

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("fileId", foreign.Id));

        ErrorOf(proposal).Should().Be("image not found");
    }

    [Fact]
    public async Task Exactly_one_of_outputId_and_fileId_is_required()
    {
        using World w = new();

        ErrorOf(await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument")).Should().Contain("outputId or fileId");
        ErrorOf(await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("outputId", Guid.NewGuid()), ("fileId", w.Image.Id))).Should().Contain("outputId or fileId");
    }

    [Fact]
    public async Task A_token_for_one_name_does_not_apply_another_and_nothing_is_written()
    {
        using World w = new();
        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeCreatePictureDocument", ("fileId", w.Image.Id), ("name", "A"));

        JsonElement result = await Invoke(w.Tools(), w.Context(), "ApplyCreatePictureDocument",
            ("fileId", w.Image.Id), ("name", "B"), ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));

        ErrorOf(result).Should().Contain("stale confirmationToken");
        (await w.Db.EditImageDocuments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_viewer_cannot_propose_and_nothing_is_written()
    {
        using World w = new();
        Guid other = Guid.NewGuid();
        Caller viewer = new(other, OrganizationRole.Member);
        AssistantPictureDocumentTools tools = new(
            w.Db, w.Tokens, TestTenancy.Access(w.Db, viewer), viewer,
            AssistantToolTestFactory.PictureCommands(w.Db, viewer, w.Storage, Moq.Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>()), w.Storage);

        JsonElement proposal = await Invoke(tools, new AssistantToolContext(other, w.Project.Id, null, null), "ProposeCreatePictureDocument", ("fileId", w.Image.Id));

        ErrorOf(proposal).Should().NotBeNull();
        proposal.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        (await w.Db.EditImageDocuments.CountAsync()).Should().Be(0);
    }
}
