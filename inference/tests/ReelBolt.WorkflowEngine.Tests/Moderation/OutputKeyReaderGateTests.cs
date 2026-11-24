using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Outputs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Readers that take a project file's bytes somewhere (to the assistant's model, into a run's prompt, to a link) read a
/// key under the output prefixes only with a current verdict: the assistant's line, outline and edit tools, the brief
/// file, and the output link fetch (which follows the same rule as minting the link).
/// </summary>
public sealed class OutputKeyReaderGateTests
{
    private const string Version = "e1:1000";

    private static PublishModeration Moderation(InferenceApiDbContext db) =>
        Moderation(db, out _);

    private static PublishModeration Moderation(InferenceApiDbContext db, out Mock<IFileStorageService> storage)
    {
        storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1000, "text/plain", "\"e1\""));
        return new PublishModeration(
            new Mock<IContentModerationGate>().Object, storage.Object, new Mock<IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, db,
            Options.Create(new ContentModerationOptions { Enabled = true }), NullLogger<PublishModeration>.Instance);
    }

    private static void Verdict(InferenceApiDbContext db, Project project, string key, string state, string? reason = null)
    {
        db.OutputModerationVerdicts.Add(new OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = key,
            SourceVersion = Version,
            State = state,
            ReasonCode = reason,
            ModelSnapshot = "m"
        });
        db.SaveChanges();
    }

    private static (InferenceApiDbContext Db, Project Project, ProjectFile File, string Key) Seed(Guid owner, string name = "notes.md")
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        string key = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(project.Id, "outputFiles", null, name);
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = name,
            OriginalPath = name,
            Category = "outputFiles",
            StorageFileName = name,
            StorageKey = key,
            MimeType = "text/markdown",
            SizeBytes = 12,
            CsamStatus = CsamScanStatus.Clean
        };
        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return (db, project, file, key);
    }

    // ---- the assistant's line, outline and edit tools ----------------------------------------------

    private static async Task<string> CallAsync(AssistantFileTools tools, AssistantToolContext context, string tool, params (string, object?)[] args)
    {
        AIFunction function = tools.GetTools(context).Single(t => t.Name == tool);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args) arguments[key] = value;
        object? raw = await function.InvokeAsync(arguments);
        return raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw?.ToString() ?? string.Empty;
    }

    private static AssistantFileTools Tools(InferenceApiDbContext db, Guid owner, FakeFileStorage storage, PublishModeration moderation) =>
        new(db, ConfirmationTokenService.CreateEphemeral(), storage, Mock.Of<IPublishEndpoint>(), new RecordingSummarizationQueue(),
            TestTenancy.Access(db, new OutputsUser(owner, TestTenancy.OrgOf(owner))), publishModeration: moderation);

    [Theory]
    [InlineData("ReadProjectFileLines")]
    [InlineData("GetProjectFileOutline")]
    [InlineData("ProposeEditProjectFile")]
    public async Task The_assistants_line_outline_and_edit_tools_refuse_an_output_without_a_verdict(string tool)
    {
        Guid owner = Guid.NewGuid();
        (InferenceApiDbContext db, Project project, ProjectFile file, string key) = Seed(owner);
        FakeFileStorage storage = new();
        storage.Objects[key] = Encoding.UTF8.GetBytes("# Title\nbody\n");
        AssistantFileTools tools = Tools(db, owner, storage, Moderation(db));
        AssistantToolContext context = new(owner, project.Id);

        string json = tool == "ProposeEditProjectFile"
            ? await CallAsync(tools, context, tool, ("fileId", file.Id),
                ("edits", new List<ProjectFileEdit> { new("body", "text", false) }))
            : await CallAsync(tools, context, tool, ("fileId", file.Id));

        json.Should().Contain("FILE_UNAVAILABLE");
        json.Should().NotContain("# Title", "the bytes were not read into the model's context");
    }

    [Fact]
    public async Task The_assistants_line_tool_reads_an_output_that_has_an_allow()
    {
        Guid owner = Guid.NewGuid();
        (InferenceApiDbContext db, Project project, ProjectFile file, string key) = Seed(owner);
        Verdict(db, project, key, "Allow");
        FakeFileStorage storage = new();
        storage.Objects[key] = Encoding.UTF8.GetBytes("# Title\nbody\n");
        AssistantFileTools tools = Tools(db, owner, storage, Moderation(db));

        string json = await CallAsync(tools, new AssistantToolContext(owner, project.Id), "ReadProjectFileLines", ("fileId", file.Id));

        json.Should().Contain("# Title");
    }

    // ---- the brief file ----------------------------------------------------------------------------

    [Fact]
    public async Task A_brief_file_that_is_an_output_without_a_verdict_is_refused_and_not_read()
    {
        Guid owner = Guid.NewGuid();
        (InferenceApiDbContext db, Project project, ProjectFile file, string key) = Seed(owner);
        Mock<IFileStorageService> brief = new();
        BriefFileResolver resolver = new(db, brief.Object, Moderation(db));

        BriefResolution resolution = await resolver.ResolveAsync(project.Id, file.Id, null, CancellationToken.None);

        resolution.Error.Should().Be(BriefFileResolver.NotAvailable);
        brief.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_brief_file_that_is_an_output_with_an_allow_is_read()
    {
        Guid owner = Guid.NewGuid();
        (InferenceApiDbContext db, Project project, ProjectFile file, string key) = Seed(owner);
        Verdict(db, project, key, "Allow");
        Mock<IFileStorageService> brief = new();
        brief.Setup(s => s.DownloadAsync(project.Id, key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("Make it short.")));
        BriefFileResolver resolver = new(db, brief.Object, Moderation(db));

        BriefResolution resolution = await resolver.ResolveAsync(project.Id, file.Id, null, CancellationToken.None);

        resolution.Error.Should().BeNull();
        resolution.Text.Should().Contain("Make it short.");
    }

    // ---- the output link fetch follows the rule the mint uses --------------------------------------

    [Fact]
    public async Task The_link_fetch_refuses_wordless_audio_exactly_as_the_mint_does()
    {
        using OutputsWorld w = new();
        w.Db.OutputModerationVerdicts.Add(new OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = w.OrgId,
            ProjectId = w.Project.Id,
            StorageKey = w.RenderKey,
            SourceVersion = Version,
            State = "Unavailable",
            ReasonCode = OutputModerationReasonCodes.NothingToClassify,
            ModelSnapshot = "m"
        });
        w.Db.SaveChanges();
        PublishModeration moderation = Moderation(w.Db);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        OutputLinkService links = new(tokens, new ConfigurationBuilder().Build(), w.Clock);
        string url = links.Create(new OutputLinkTarget(w.Project.Id, OutputLinkKind.StepResult, w.Render.Id, null), w.UserId, null).Url;
        string token = url[(url.LastIndexOf('/') + 1)..];
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = OutputsWorld.Bucket }).Build();
        OutputLinksController controller = new(links, new OutputLinkResolver(w.Db, w.Storage), w.S3.Object, config, w.Clock, moderation)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        // The mint refuses it (wordless output is not "cleared" for sharing); the fetch now does too.
        PublishModerationOutcome mint = await moderation.EvaluateObjectAsync(
            w.Project.Id, w.RenderKey, "video/mp4",
            new ModerationAttribution(w.OrgId, w.Project.Id, null, null, null, ModerationSurface.Publish, ModerationContentKind.Text),
            CancellationToken.None);
        mint.Allowed.Should().BeFalse();

        (await controller.Get(token, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }
}
