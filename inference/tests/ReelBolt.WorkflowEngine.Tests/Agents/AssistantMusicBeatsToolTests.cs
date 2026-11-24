#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Tests.Outputs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// <c>GetMusicBeats</c>: the cached beat grid of a project file, the free analysis started only for an editor,
/// and the same refusals the media-analysis routes give.
/// </summary>
public class AssistantMusicBeatsToolTests
{
    /// <summary>The real access service, except that edit access is refused (a viewer).</summary>
    private sealed class ViewOnlyAccess(IProjectAccessService inner) : IProjectAccessService
    {
        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct) =>
            Task.FromResult(Get(projectId, permission));

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) =>
            permission == ProjectPermission.Edit
                ? new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null)
                : inner.Get(projectId, permission);

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => inner.AuthorizeOrganization(permission);
    }

    private readonly Mock<IPublishEndpoint> _publish = new();

    private AssistantOutputTools Tools(OutputsWorld w, bool viewOnly = false)
    {
        _publish.Setup(p => p.Publish(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        IProjectAccessService access = TestTenancy.Access(w.Db, new OutputsUser(w.UserId, w.OrgId));
        if (viewOnly) access = new ViewOnlyAccess(access);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        return new AssistantOutputTools(
            w.Db, tokens, access,
            new OutputDescriber(w.Db, w.Storage, new OutputLinkService(tokens, new ConfigurationBuilder().Build(), w.Clock), _publish.Object),
            w.Storage, Mock.Of<IStorageObjectCopier>(), _publish.Object, new RecordingSummarizationQueue());
    }

    private static async Task<JsonElement> Invoke(AssistantOutputTools tools, OutputsWorld w, Guid fileId, Guid? projectId = null)
    {
        AIFunction tool = tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)).Single(t => t.Name == "GetMusicBeats");
        AIFunctionArguments args = new() { ["fileId"] = fileId };
        if (projectId is { } p) args["projectId"] = p;
        object? raw = await tool.InvokeAsync(args);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static AnalysisBeatsResult Beats(int bars, int beatsPerBar = 4) => new(
        Bpm: 123.456, BeatSec: 0.486, BeatPhaseSec: 0.1, BarPhaseSec: 0.1, BeatsPerBar: beatsPerBar,
        BeatTimesSec: Enumerable.Range(0, bars * beatsPerBar).Select(i => 0.1 + i * 0.486).ToList(),
        FirstDownbeatIndex: 0, DurationSec: 200.123, AnalysedSec: 180.0, DropSec: 32.456, OutroSec: null,
        Confidence: 0.8765, Steady: true, TempoWord: "upbeat", HalfTempoScore: 0.1, DoubleTempoScore: 0.2, Truncated: false);

    private static void Seed(OutputsWorld w, string state, AnalysisBeatsResult? result = null, string? reason = null, TimeSpan? age = null)
    {
        DateTime when = DateTime.UtcNow - (age ?? TimeSpan.Zero);
        MediaAnalysisRequest row = new()
        {
            Id = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            OrganizationId = w.OrgId,
            MediaRef = $"file:{w.File.Id}",
            Kind = MediaAnalysisKinds.Beats,
            Language = string.Empty,
            State = state,
            Reason = reason,
            CreatedAt = when,
            UpdatedAt = when
        };
        if (result is not null)
        {
            MediaAnalysisResult stored = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = w.OrgId,
                Digest = new string('a', 64),
                Kind = MediaAnalysisKinds.Beats,
                ResultJson = JsonSerializer.Serialize(result, MediaAnalysisJson.Options),
                SummaryJson = "{}"
            };
            w.Db.MediaAnalysisResults.Add(stored);
            row.ResultId = stored.Id;
        }

        w.Db.MediaAnalysisRequests.Add(row);
        w.Db.SaveChanges();
    }

    [Fact]
    public async Task Ready_returns_the_rounded_grid_without_secrets()
    {
        using OutputsWorld w = new();
        Seed(w, MediaAnalysisStates.Ready, Beats(10));

        JsonElement r = await Invoke(Tools(w), w, w.File.Id);

        r.GetProperty("status").GetString().Should().Be("ready");
        r.GetProperty("fileName").GetString().Should().Be("soundtrack.mp3");
        r.GetProperty("bpm").GetDouble().Should().Be(123.5);
        r.GetProperty("confidence").GetDouble().Should().Be(0.88);
        r.GetProperty("beatsPerBar").GetInt32().Should().Be(4);
        r.GetProperty("barCount").GetInt32().Should().Be(10);
        r.GetProperty("barTimesSec").GetArrayLength().Should().Be(10);
        r.GetProperty("barTimesSec")[1].GetDouble().Should().Be(Math.Round(0.1 + 4 * 0.486, 2));
        r.GetProperty("dropSec").GetDouble().Should().Be(32.46);
        r.GetProperty("truncated").GetBoolean().Should().BeFalse();
        string body = r.GetRawText();
        body.Should().NotContain("storage").And.NotContain(OutputsWorld.Bucket).And.NotContain(new string('a', 64)).And.NotContain("projects/");
    }

    [Fact]
    public async Task Ready_caps_bar_times_at_64_and_says_truncated()
    {
        using OutputsWorld w = new();
        Seed(w, MediaAnalysisStates.Ready, Beats(100));

        JsonElement r = await Invoke(Tools(w), w, w.File.Id);

        r.GetProperty("barCount").GetInt32().Should().Be(100);
        r.GetProperty("barTimesSec").GetArrayLength().Should().Be(64);
        r.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(MediaAnalysisStates.Queued)]
    [InlineData(MediaAnalysisStates.Running)]
    public async Task In_flight_reads_working_and_starts_nothing(string state)
    {
        using OutputsWorld w = new();
        Seed(w, state);

        JsonElement r = await Invoke(Tools(w), w, w.File.Id);

        r.GetProperty("status").GetString().Should().Be("working");
        _publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_carries_a_plain_reason()
    {
        using OutputsWorld w = new();
        Seed(w, MediaAnalysisStates.Failed, reason: "The audio could not be decoded.");

        JsonElement r = await Invoke(Tools(w), w, w.File.Id);

        r.GetProperty("status").GetString().Should().Be("failed");
        r.GetProperty("reason").GetString().Should().Be("The audio could not be decoded.");
    }

    [Fact]
    public async Task Not_analysed_with_edit_access_starts_the_analysis_once()
    {
        using OutputsWorld w = new();
        AssistantOutputTools tools = Tools(w);

        JsonElement r = await Invoke(tools, w, w.File.Id);

        r.GetProperty("status").GetString().Should().Be("started");
        r.GetProperty("message").GetString().Should().Contain("again");
        w.Db.MediaAnalysisRequests.Count(x => x.Kind == MediaAnalysisKinds.Beats && x.ProjectId == w.Project.Id).Should().Be(1);
        _publish.Invocations.Should().HaveCount(1);

        JsonElement again = await Invoke(tools, w, w.File.Id);
        again.GetProperty("status").GetString().Should().Be("working", "the queued row is reused");
        _publish.Invocations.Should().HaveCount(1);
    }

    [Fact]
    public async Task Not_analysed_with_view_access_starts_nothing()
    {
        using OutputsWorld w = new();

        JsonElement r = await Invoke(Tools(w, viewOnly: true), w, w.File.Id);

        r.GetProperty("status").GetString().Should().Be("notAnalysed");
        w.Db.MediaAnalysisRequests.Should().BeEmpty();
        _publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_foreign_project_is_refused_as_not_found()
    {
        using OutputsWorld w = new();
        (Project theirs, _) = w.AddForeignProject();

        JsonElement r = await Invoke(Tools(w), w, w.File.Id, theirs.Id);

        r.GetProperty("error").GetString().Should().Be("project not found");
        _publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_file_is_refused()
    {
        using OutputsWorld w = new();

        JsonElement r = await Invoke(Tools(w), w, Guid.NewGuid());

        r.GetProperty("error").GetString().Should().Be("file not found");
    }

    [Fact]
    public async Task A_quarantined_file_is_refused_and_not_started()
    {
        using OutputsWorld w = new();
        w.File.ModerationStatus = FileModerationStatus.Quarantined;
        w.Db.SaveChanges();

        JsonElement r = await Invoke(Tools(w), w, w.File.Id);

        r.GetProperty("code").GetString().Should().Be("CONTENT_BLOCKED");
        w.Db.MediaAnalysisRequests.Should().BeEmpty();
        _publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_not_audio_or_video_is_refused()
    {
        using OutputsWorld w = new();
        w.File.MimeType = "application/pdf";
        w.Db.SaveChanges();

        JsonElement r = await Invoke(Tools(w), w, w.File.Id);

        r.GetProperty("error").GetString().Should().Be("Only video and audio files can be analysed.");
        _publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void The_tool_is_free_read_only_and_in_the_mcp_catalog()
    {
        AssistantPlatformTools.Tools.Should().ContainKey("GetMusicBeats");
        AssistantToolAnnotations.For("GetMusicBeats").ReadOnlyHint.Should().BeTrue();
        AssistantToolAnnotations.For("GetMusicBeats").DestructiveHint.Should().BeFalse();
        AssistantToolAnnotations.For("GetMusicBeats").IdempotentHint.Should().BeTrue();
        AssistantToolAnnotations.For("GetMusicBeats").OpenWorldHint.Should().BeFalse();
    }
}
