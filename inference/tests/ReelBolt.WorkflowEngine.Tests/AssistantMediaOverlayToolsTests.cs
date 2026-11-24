using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// ProposeSetMediaOverlay / ApplySetMediaOverlay append one picture or video to a VideoCompile step's
/// <c>mediaOverlays</c> through the normal project-scoped save validation, with a token that goes stale when the
/// step is edited, and never start a run. Real diff service, real token service, real save validators.
/// </summary>
public class AssistantMediaOverlayToolsTests
{
    private sealed class Caller(Guid userId, bool emailVerified = true) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified { get; } = emailVerified;
    }

    private sealed class Rig : IDisposable
    {
        private readonly SqliteConnection _connection;

        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Project Other { get; }
        public Guid AgentId { get; } = Guid.NewGuid();
        public WorkflowDefinition Workflow { get; }
        public WorkflowStep Compile { get; }
        public ProjectFile Photo { get; }
        public ProjectFile Clip { get; }
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();

        /// <summary>
        /// The apply path replaces steps through <see cref="WorkflowStepReplacer"/>, whose archive runs real
        /// <c>ExecuteDelete</c>/<c>ExecuteUpdate</c> SQL that the in-memory provider refuses, so the rig runs on Sqlite.
        /// </summary>
        public Rig()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);
            Db.Database.EnsureCreated();
            using (WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options))
                engine.GetService<IRelationalDatabaseCreator>().CreateTables();

            Db.ApplicationUsers.Add(new ApplicationUser { Id = User, Email = $"{User:N}@t.local", DisplayName = "owner", MustChangePassword = false });
            Db.Organizations.Add(new Organization { Id = TestTenancy.OrgOf(User), Name = "org", Slug = "org-" + User.ToString("N"), Kind = OrganizationKind.Personal });
            Db.SaveChanges();

            Project = NewProject("Launch");
            Other = NewProject("Elsewhere");
            Db.AgentDefinitions.Add(new AgentDefinition { Id = AgentId, Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });

            Workflow = new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Product film",
                ProjectId = Project.Id,
                Kind = WorkflowDefinitionKind.Standard,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            Db.WorkflowDefinitions.Add(Workflow);
            Db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Workflow.Id,
                AgentDefinitionId = AgentId,
                StepOrder = 1,
                Label = "Analyze",
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = """{"version":1}""",
            });
            Compile = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Workflow.Id,
                AgentDefinitionId = AgentId,
                StepOrder = 2,
                Label = "Render",
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = """{"version":1,"mode":"Reencode","keepWholeSources":true}""",
            };
            Db.WorkflowSteps.Add(Compile);

            Photo = AddFile(Project, "testimonial.png", "image/png");
            Clip = AddFile(Project, "demo.mp4", "video/mp4");
            Db.SaveChanges();
        }

        private Project NewProject(string name)
        {
            Project project = new() { Id = Guid.NewGuid(), Name = name, OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(project);
            return project;
        }

        public ProjectFile AddFile(Project project, string name, string mime, CsamScanStatus status = CsamScanStatus.Clean)
        {
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = name, MimeType = mime, CsamStatus = status, StorageKey = $"projects/{project.Id}/userFiles/{name}" };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public AssistantMediaOverlayTools Tools(bool emailVerified = true)
        {
            Caller caller = new(User, emailVerified);
            return new AssistantMediaOverlayTools(Db, TestTenancy.Access(Db, caller), new WorkflowEditDiffService(Db), Tokens, caller);
        }

        public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, AssistantMediaOverlayTools? tools = null)
        {
            tools ??= Tools();
            AIFunction fn = tools.GetTools(new AssistantToolContext(User, Project.Id, ThreadId: Thread)).Single(t => t.Name == tool);
            object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public Dictionary<string, object?> Args(Guid? media = null, Guid? workflow = null, int stepOrder = 2, string? token = null,
            string? form = null, string? anchor = null, string? size = null, double? startSec = null, double? endSec = null,
            int? opacity = null, string? cardAudio = null, string? audio = null, string? audioLevel = null)
        {
            Dictionary<string, object?> args = new()
            {
                ["mediaFileId"] = media ?? Photo.Id,
                ["workflowId"] = workflow ?? Workflow.Id,
                ["stepOrder"] = stepOrder,
            };
            if (token is not null) args["confirmationToken"] = token;
            if (form is not null) args["form"] = form;
            if (anchor is not null) args["anchor"] = anchor;
            if (size is not null) args["size"] = size;
            if (startSec is not null) args["startSec"] = startSec;
            if (endSec is not null) args["endSec"] = endSec;
            if (opacity is not null) args["opacity"] = opacity;
            if (cardAudio is not null) args["cardAudio"] = cardAudio;
            if (audio is not null) args["audio"] = audio;
            if (audioLevel is not null) args["audioLevel"] = audioLevel;
            return args;
        }

        public Guid Thread { get; } = Guid.NewGuid();

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static VideoCompileStepConfig ConfigOf(Rig rig)
    {
        rig.Db.ChangeTracker.Clear();
        string json = rig.Db.WorkflowSteps.AsNoTracking().Single(s => s.StepType == StepType.VideoCompile).VideoCompileConfigJson!;
        return JsonSerializer.Deserialize<VideoCompileStepConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        })!;
    }

    [Fact]
    public async Task Propose_returns_a_token_and_a_plain_summary_and_writes_nothing()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(form: "FullScreen", startSec: 12, endSec: 18, opacity: 80, cardAudio: "Mute"));

        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("summary").GetString().Should().Contain("testimonial.png").And.Contain("full screen").And.Contain("0:12").And.Contain("0:18").And.Contain("80%").And.Contain("mutes the film");
        result.GetProperty("workflowName").GetString().Should().Be("Product film");
        result.GetProperty("stepOrder").GetInt32().Should().Be(2);
        rig.Db.WorkflowExecutions.Should().BeEmpty();
        ConfigOf(rig).MediaOverlays.Should().BeNull("a proposal writes nothing");
    }

    [Fact]
    public async Task Apply_appends_the_overlay_to_the_compile_step_and_starts_no_run()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(
            form: "Inset", anchor: "TopLeft", size: "Large", startSec: 3, endSec: 9, opacity: 60,
            audio: "Mixed", audioLevel: "Prominent"))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await rig.CallAsync("ApplySetMediaOverlay", rig.Args(
            token: token, form: "Inset", anchor: "TopLeft", size: "Large", startSec: 3, endSec: 9, opacity: 60,
            audio: "Mixed", audioLevel: "Prominent"));

        result.GetProperty("hint").GetString().Should().Contain("ExecuteWorkflow");
        VideoCompileStepConfig config = ConfigOf(rig);
        config.KeepWholeSources.Should().BeTrue("the rest of the compile settings stay as they were");
        VideoMediaOverlay overlay = config.MediaOverlays.Should().ContainSingle().Subject;
        overlay.Content.Should().Be(new InsertContentRef(InsertContentRef.ProjectFileKind, ProjectFileId: rig.Photo.Id));
        overlay.Form.Should().Be(MediaOverlayForm.PictureInPicture);
        overlay.Anchor.Should().Be(MediaOverlayAnchor.TopLeft);
        overlay.Size.Should().Be(MediaOverlaySize.Large);
        overlay.OpacityPercent.Should().Be(60);
        overlay.StartSec.Should().Be(3);
        overlay.EndSec.Should().Be(9);
        overlay.Audio.Should().Be(MediaOverlayAudio.Muted, "audio applies to a video only; an image asking for Mixed is rendered muted");
        overlay.Volume.Should().Be(MediaOverlayVolume.Prominent);
        rig.Db.WorkflowExecutions.Should().BeEmpty("ExecuteWorkflow stays the run gate");
    }

    [Fact]
    public async Task Apply_refuses_every_token_after_the_step_was_edited()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeSetMediaOverlay", rig.Args())).GetProperty("confirmationToken").GetString()!;

        // The user edits the compile step between the proposal and the apply.
        WorkflowStep step = rig.Db.WorkflowSteps.Single(s => s.StepType == StepType.VideoCompile);
        step.VideoCompileConfigJson = step.VideoCompileConfigJson!.Replace("\"keepWholeSources\":true", "\"keepWholeSources\":false", StringComparison.Ordinal);
        rig.Db.SaveChanges();

        JsonElement result = await rig.CallAsync("ApplySetMediaOverlay", rig.Args(token: token));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        ConfigOf(rig).MediaOverlays.Should().BeNull("a stale token writes nothing");
    }

    [Fact]
    public async Task Apply_refuses_a_token_minted_for_other_choices()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(opacity: 80))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await rig.CallAsync("ApplySetMediaOverlay", rig.Args(token: token, opacity: 20));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        ConfigOf(rig).MediaOverlays.Should().BeNull();
    }

    [Fact]
    public async Task Apply_refuses_an_unverified_email()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeSetMediaOverlay", rig.Args())).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await rig.CallAsync("ApplySetMediaOverlay", rig.Args(token: token), rig.Tools(emailVerified: false));

        result.GetProperty("code").GetString().Should().Be(EmailVerificationGate.ErrorCode);
        ConfigOf(rig).MediaOverlays.Should().BeNull();
    }

    [Fact]
    public async Task Media_from_another_project_a_quarantined_file_or_the_wrong_kind_is_refused()
    {
        using Rig rig = new();
        ProjectFile foreign = rig.AddFile(rig.Other, "foreign.png", "image/png");
        ProjectFile quarantined = rig.AddFile(rig.Project, "bad.png", "image/png", CsamScanStatus.Quarantined);
        ProjectFile moderated = rig.AddFile(rig.Project, "moderated.png", "image/png");
        moderated.ModerationStatus = FileModerationStatus.Quarantined;
        rig.Db.SaveChanges();
        ProjectFile audio = rig.AddFile(rig.Project, "song.mp3", "audio/mpeg");

        foreach ((ProjectFile file, string expected) in new[] { (foreign, "not found"), (quarantined, "not found"), (moderated, "not found"), (audio, "picture or a video") })
        {
            JsonElement result = await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(media: file.Id));
            result.GetProperty("error").GetString().Should().Contain(expected);
            result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_workflow_of_another_project_and_a_step_that_is_not_compile_are_refused()
    {
        using Rig rig = new();
        WorkflowDefinition foreign = new() { Id = Guid.NewGuid(), Name = "Foreign", ProjectId = rig.Other.Id, Kind = WorkflowDefinitionKind.Standard };
        rig.Db.WorkflowDefinitions.Add(foreign);
        rig.Db.SaveChanges();

        JsonElement workflowRefusal = await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(workflow: foreign.Id));
        JsonElement stepRefusal = await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(stepOrder: 1));

        workflowRefusal.GetProperty("error").GetString().Should().Contain("workflow not found");
        stepRefusal.GetProperty("error").GetString().Should().Contain("not a VideoCompile step");
    }

    [Theory]
    [InlineData("form", "Circle")]
    [InlineData("anchor", "Middle")]
    [InlineData("size", "Huge")]
    [InlineData("cardAudio", "Loud")]
    [InlineData("audio", "On")]
    [InlineData("audioLevel", "Deafening")]
    public async Task An_invalid_choice_is_refused_with_the_offered_words(string name, string value)
    {
        using Rig rig = new();
        Dictionary<string, object?> args = rig.Args();
        args[name] = value;

        JsonElement result = await rig.CallAsync("ProposeSetMediaOverlay", args);

        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task An_opacity_outside_the_range_is_refused(int opacity)
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(opacity: opacity));

        result.GetProperty("error").GetString().Should().Contain("opacity");
    }

    [Fact]
    public async Task A_span_that_ends_before_it_starts_is_refused()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeSetMediaOverlay", rig.Args(startSec: 18, endSec: 12));

        result.GetProperty("error").GetString().Should().Contain("endSec must be after startSec");
    }
}
