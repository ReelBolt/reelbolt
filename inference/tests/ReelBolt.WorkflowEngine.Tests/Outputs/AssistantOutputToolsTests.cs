#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// <c>GetOutput</c> and <c>Propose/ApplySaveOutputAsFile</c>: opaque ids and short-lived links only,
/// project-scoped per call, and a save that copies the render into <c>userFiles</c> under the quota.
/// </summary>
public class AssistantOutputToolsTests
{
    private readonly IConfirmationTokenService _tokens = ConfirmationTokenService.CreateEphemeral();
    private readonly List<object> _published = [];
    private readonly Mock<IPlanGateService> _planGate = new();
    private readonly RecordingSummarizationQueue _summaries = new();
    private readonly ReelBolt.Inference.Api.Services.Csam.CsamScanRequests _csamScans = new();

    private class InMemoryCopier(OutputsWorld w) : IStorageObjectCopier
    {
        public int Copies { get; private set; }

        /// <summary>The key the last copy landed at (QA-P0-26's cross-replica assertion needs it).</summary>
        public string? DestinationKey { get; private set; }

        public Task CopyAsync(Guid projectId, string sourceKey, string destinationKey, CancellationToken ct)
        {
            Copies++;
            DestinationKey = destinationKey;
            w.Storage.Objects[destinationKey] = w.Storage.Objects[sourceKey].ToArray();
            OnCopied(projectId, destinationKey);
            return Task.CompletedTask;
        }

        /// <summary>Where a subclass models what a concurrent apply does while this copy is in flight.</summary>
        protected virtual void OnCopied(Guid projectId, string destinationKey) { }
    }

    /// <summary>QA-P0-26: commits the row another API replica would have written for the same deterministic key
    /// (the process-local storage lock cannot see it), so the post-copy refusal must not delete that copy.</summary>
    private sealed class RivalCommitter(OutputsWorld w) : InMemoryCopier(w)
    {
        protected override void OnCopied(Guid projectId, string destinationKey)
        {
            w.Db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = "rival.mp4",
                OriginalPath = "rival.mp4",
                Category = "userFiles",
                StorageKey = destinationKey,
                StorageBucket = OutputsWorld.Bucket,
                MimeType = "video/mp4",
                SizeBytes = 1000
            });
            w.Db.SaveChanges();
        }
    }

    /// <summary>S-LFR-01: the same rival row as <see cref="RivalCommitter"/>, but the interceptor is armed once it
    /// is committed, so it is the TOOL's own save that fails. That is the path where the copy must survive because
    /// a committed row points at it — the guard QA-P0-26 added to the refusal path, applied to the failure path.</summary>
    private sealed class SaveFailingRivalCommitter(OutputsWorld w, OutputsWorld.FailNextSaveInterceptor interceptor) : InMemoryCopier(w)
    {
        protected override void OnCopied(Guid projectId, string destinationKey)
        {
            w.Db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = "rival.mp4",
                OriginalPath = "rival.mp4",
                Category = "userFiles",
                StorageKey = destinationKey,
                StorageBucket = OutputsWorld.Bucket,
                MimeType = "video/mp4",
                SizeBytes = 1000
            });
            w.Db.SaveChanges();
            interceptor.Armed = true;
        }
    }

    /// <summary>SQCR-S-05: arms the tool's save failure on copy with NO rival row, so the recovery finds
    /// nothing referencing the copy and takes the branch that deletes it.</summary>
    private sealed class SaveFailingCopier(OutputsWorld w, OutputsWorld.FailNextSaveInterceptor interceptor) : InMemoryCopier(w)
    {
        protected override void OnCopied(Guid projectId, string destinationKey) => interceptor.Armed = true;
    }

    /// <summary>SQCR-S-14: arms the save failure only on the FIRST copy, so a second apply of the same
    /// token can succeed (and must drop the mark the failed recovery left).</summary>
    private sealed class SaveFailingOnceCopier(OutputsWorld w, OutputsWorld.FailNextSaveInterceptor interceptor) : InMemoryCopier(w)
    {
        private int _copies;

        protected override void OnCopied(Guid projectId, string destinationKey)
        {
            if (Interlocked.Increment(ref _copies) == 1) interceptor.Armed = true;
        }
    }

    private InMemoryCopier? _copier;
    private OutputLinkService Links(OutputsWorld w) => new(_tokens, new ConfigurationBuilder().Build(), w.Clock);

    private AssistantOutputTools Tools(OutputsWorld w, ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        var publish = new Mock<IPublishEndpoint>();
        publish.Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((m, _) => _published.Add(m))
            .Returns(Task.CompletedTask);
        publish.Setup(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectFileIndexingRequested, CancellationToken>((m, _) => _published.Add(m))
            .Returns(Task.CompletedTask);
        _copier ??= new InMemoryCopier(w);
        var access = TestTenancy.Access(w.Db, new OutputsUser(w.UserId, w.OrgId));
        return new AssistantOutputTools(
            w.Db,
            _tokens,
            access,
            new OutputDescriber(w.Db, w.Storage, Links(w), publish.Object),
            w.Storage,
            _copier,
            publish.Object,
            _summaries,
            _planGate.Object,
            productEvents: productEvents,
            csamScans: _csamScans);
    }

    private static async Task<JsonElement> Invoke(AssistantOutputTools tools, OutputsWorld w, string name, params (string Key, object? Value)[] args)
    {
        AIFunction tool = tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)).Single(t => t.Name == name);
        var arguments = new AIFunctionArguments();
        foreach ((string k, object? v) in args) arguments[k] = v;
        object? raw = await tool.InvokeAsync(arguments);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static void AssertNoStorageSecrets(OutputsWorld w, string body)
    {
        body.Should().NotContain("storageKey");
        body.Should().NotContain("storage_key");
        body.Should().NotContain("StorageKey");
        body.Should().NotContain(OutputsWorld.Bucket);
        body.Should().NotContain("X-Amz-");
        body.Should().NotContain(w.RenderKey);
        body.Should().NotContain("projects/");
        body.Should().NotContain("outputFiles");
    }

    [Fact]
    public void The_tool_set_is_the_four_names_with_captions()
    {
        using OutputsWorld w = new();
        Tools(w).GetTools(new AssistantToolContext(w.UserId)).Select(t => t.Name)
            .Should().BeEquivalentTo("GetOutput", "GetMusicBeats", "ProposeSaveOutputAsFile", "ApplySaveOutputAsFile");
        AssistantOutputTools.Captions.Keys.Should().BeEquivalentTo("GetOutput", "GetMusicBeats", "ProposeSaveOutputAsFile", "ApplySaveOutputAsFile");
        Tools(w).GetTools(new AssistantToolContext(w.UserId)).Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Description));
    }

    // ---------------------------------------------------------------- GetOutput

    [Fact]
    public async Task GetOutput_minting_a_link_records_output_shared_once_per_user()
    {
        using OutputsWorld w = new();
        w.AddRenderDerivatives();
        var log = new ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventLog();
        var emitter = new ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter(
            log, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter>.Instance);

        await Invoke(Tools(w, emitter), w, "GetOutput", ("outputId", w.Render.Id));
        await Invoke(Tools(w, emitter), w, "GetOutput", ("outputId", w.Render.Id));

        log.Rows.Should().ContainSingle().Which.Event.Should().Be("activation.output_shared");
        log.Rows[0].UserId.Should().Be(w.UserId);
    }

    [Fact]
    public async Task GetOutput_describes_a_render_with_a_link_and_its_contact_sheets()
    {
        using OutputsWorld w = new();
        w.AddRenderDerivatives();

        JsonElement r = await Invoke(Tools(w), w, "GetOutput", ("outputId", w.Render.Id));

        r.GetProperty("projectName").GetString().Should().Be("Launch Promo");
        JsonElement o = r.GetProperty("output");
        o.GetProperty("id").GetGuid().Should().Be(w.Render.Id);
        o.GetProperty("kind").GetString().Should().Be("render");
        o.GetProperty("name").GetString().Should().Be("final-cut.mp4");
        o.GetProperty("mimeType").GetString().Should().Be("video/mp4");
        o.GetProperty("sizeBytes").GetInt64().Should().Be(1000);
        o.GetProperty("durationSec").GetDouble().Should().Be(61.2);
        o.GetProperty("width").GetInt32().Should().Be(1920);
        o.GetProperty("height").GetInt32().Should().Be(1080);
        o.GetProperty("executionId").GetGuid().Should().Be(w.Execution.Id);
        o.GetProperty("stepOrder").GetInt32().Should().Be(3);
        o.GetProperty("stepLabel").GetString().Should().Be("Compile");

        JsonElement link = r.GetProperty("link");
        link.GetProperty("url").GetString().Should().StartWith("/api/v1/output-links/");
        link.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(OutputsWorld.Now.AddMinutes(15));

        JsonElement frames = r.GetProperty("frames");
        frames.GetProperty("status").GetString().Should().Be("ready");
        frames.GetProperty("thumbnail").GetProperty("url").GetString().Should().StartWith("/api/v1/output-links/");
        JsonElement[] sheets = frames.GetProperty("contactSheets").EnumerateArray().ToArray();
        sheets.Should().HaveCount(2);
        sheets[1].GetProperty("cols").GetInt32().Should().Be(5);
        sheets[1].GetProperty("rows").GetInt32().Should().Be(4);
        sheets[1].GetProperty("densitySec").GetDouble().Should().Be(2.0);
        sheets[1].GetProperty("firstTimeSec").GetDouble().Should().Be(40.0);
        sheets[1].GetProperty("thumbCount").GetInt32().Should().Be(11);
        _published.Should().BeEmpty("derivatives already exist");
    }

    [Fact]
    public async Task GetOutput_links_resolve_to_the_render_and_the_sheet_they_name()
    {
        using OutputsWorld w = new();
        w.AddRenderDerivatives();
        JsonElement r = await Invoke(Tools(w), w, "GetOutput", ("outputId", w.Render.Id));
        var resolver = new OutputLinkResolver(w.Db, w.Storage);

        async Task<string?> KeyOf(string url)
        {
            Links(w).TryVerify(url[(url.LastIndexOf('/') + 1)..], w.Clock.Now, out OutputLinkClaims claims).Should().BeTrue();
            return (await resolver.ResolveAsync(claims, CancellationToken.None))?.StorageKey;
        }

        (await KeyOf(r.GetProperty("link").GetProperty("url").GetString()!)).Should().Be(w.RenderKey);
        (await KeyOf(r.GetProperty("frames").GetProperty("contactSheets")[1].GetProperty("url").GetString()!))
            .Should().EndWith("/sheet-1.jpg");
    }

    [Fact]
    public async Task GetOutput_never_carries_a_storage_key_bucket_or_presigned_url()
    {
        using OutputsWorld w = new();
        w.AddRenderDerivatives();
        AssistantOutputTools tools = Tools(w);

        AssertNoStorageSecrets(w, (await Invoke(tools, w, "GetOutput", ("outputId", w.Render.Id))).GetRawText());
        AssertNoStorageSecrets(w, (await Invoke(tools, w, "GetOutput", ("outputId", w.File.Id))).GetRawText());
    }

    [Fact]
    public async Task GetOutput_describes_a_project_file()
    {
        using OutputsWorld w = new();
        JsonElement r = await Invoke(Tools(w), w, "GetOutput", ("outputId", w.File.Id));

        JsonElement o = r.GetProperty("output");
        o.GetProperty("kind").GetString().Should().Be("file");
        o.GetProperty("name").GetString().Should().Be("soundtrack.mp3");
        o.GetProperty("mimeType").GetString().Should().Be("audio/mpeg");
        o.GetProperty("sizeBytes").GetInt64().Should().Be(400);
        o.TryGetProperty("executionId", out _).Should().BeFalse();
        r.GetProperty("frames").GetProperty("status").GetString().Should().Be("unavailable");
        _published.Should().BeEmpty("frames are for video only");
    }

    [Fact]
    public async Task GetOutput_on_a_video_without_derivatives_asks_for_them_once_and_says_pending()
    {
        using OutputsWorld w = new();
        JsonElement r = await Invoke(Tools(w), w, "GetOutput", ("outputId", w.Render.Id));

        r.GetProperty("frames").GetProperty("status").GetString().Should().Be("pending");
        r.GetProperty("frames").GetProperty("contactSheets").GetArrayLength().Should().Be(0);
        MediaDerivativesRequested request = _published.Should().ContainSingle().Which.Should().BeOfType<MediaDerivativesRequested>().Subject;
        request.ProjectId.Should().Be(w.Project.Id);
        request.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.StepResult, w.Render.Id));
        request.SourceStorageKey.Should().Be(w.RenderKey);
        request.MimeType.Should().Be("video/mp4");
        request.Kinds.Should().Contain([MediaDerivativeKind.Thumbnail, MediaDerivativeKind.Filmstrip]);
    }

    [Fact]
    public async Task GetOutput_reports_frames_unavailable_when_a_filmstrip_row_exists_but_its_sidecar_is_unreadable()
    {
        using OutputsWorld w = new();
        (MediaDerivative thumb, MediaDerivative strip) = w.AddRenderDerivatives();
        w.Db.MediaDerivatives.Remove(thumb);
        await w.Db.SaveChangesAsync();
        w.Storage.Objects.Remove(strip.IndexStorageKey!);

        JsonElement r = await Invoke(Tools(w), w, "GetOutput", ("outputId", w.Render.Id));

        r.GetProperty("frames").GetProperty("status").GetString().Should().Be("unavailable");
        r.GetProperty("frames").GetProperty("contactSheets").GetArrayLength().Should().Be(0);
        _published.Should().BeEmpty("the derivative rows exist; asking again would not make the sidecar readable");
    }

    [Fact]
    public async Task GetOutput_refuses_another_organizations_output_and_publishes_nothing()
    {
        using OutputsWorld w = new();
        (Project theirs, WorkflowStepResult theirRender) = w.AddForeignProject();
        AssistantOutputTools tools = Tools(w);

        JsonElement inOurs = await Invoke(tools, w, "GetOutput", ("outputId", theirRender.Id));
        inOurs.GetProperty("error").GetString().Should().Be("output not found");

        JsonElement inTheirs = await Invoke(tools, w, "GetOutput", ("outputId", theirRender.Id), ("projectId", theirs.Id));
        inTheirs.GetProperty("error").GetString().Should().Be("project not found");

        inOurs.GetRawText().Should().NotContain("theirs.mp4");
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOutput_on_a_step_result_without_a_render_is_not_found()
    {
        using OutputsWorld w = new();
        w.Render.OutputStorageKey = null;
        await w.Db.SaveChangesAsync();

        (await Invoke(Tools(w), w, "GetOutput", ("outputId", w.Render.Id))).GetProperty("error").GetString().Should().Be("output not found");
    }

    // ---------------------------------------------------------------- SaveOutputAsFile

    [Fact]
    public async Task Saving_a_render_copies_it_into_user_files_as_one_new_row()
    {
        using OutputsWorld w = new();
        AssistantOutputTools tools = Tools(w);
        int rowsBefore = w.Db.ProjectFiles.Count();

        JsonElement proposal = await Invoke(tools, w, "ProposeSaveOutputAsFile",
            ("outputId", w.Render.Id), ("fileName", "teaser"), ("directoryPath", "renders"));
        proposal.GetProperty("summary").GetString().Should().Contain("renders/teaser.mp4");
        proposal.GetProperty("sizeBytes").GetInt64().Should().Be(1000);
        AssertNoStorageSecrets(w, proposal.GetRawText());

        JsonElement applied = await Invoke(tools, w, "ApplySaveOutputAsFile",
            ("outputId", w.Render.Id), ("fileName", "teaser"), ("directoryPath", "renders"),
            ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));

        AssertNoStorageSecrets(w, applied.GetRawText());
        Guid fileId = applied.GetProperty("fileId").GetGuid();
        applied.GetProperty("fileName").GetString().Should().Be("teaser.mp4");
        applied.GetProperty("directoryPath").GetString().Should().Be("renders");
        applied.GetProperty("sizeBytes").GetInt64().Should().Be(1000);

        w.Db.ProjectFiles.Count().Should().Be(rowsBefore + 1);
        ProjectFile row = w.Db.ProjectFiles.Single(f => f.Id == fileId);
        row.ProjectId.Should().Be(w.Project.Id);
        row.Category.Should().Be("userFiles");
        row.StorageKey.Should().StartWith($"projects/{w.Project.Id}/userFiles/renders/");
        row.OriginalPath.Should().Be("renders/teaser.mp4");
        row.MimeType.Should().Be("video/mp4");
        row.SizeBytes.Should().Be(1000);
        row.SummaryStatus.Should().Be(SummaryStatus.Done);
        row.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        row.SourceKind.Should().Be("output_copy", "the engine tells a copy of an output from an upload by this mark");
        row.OutputProducedAt.Should().NotBeNull("the copy records when its output was made");
        w.Storage.Objects[row.StorageKey].Should().Equal(w.Storage.Objects[w.RenderKey]);
        w.Storage.Objects.Should().ContainKey(w.RenderKey, "a copy, not a move: deleting either side never breaks the other");

        // docs/csam.md: the copy starts Pending, so no derivative is requested before its CSAM verdict; the scan is
        // asked for at once and the sweeper requests the set after it.
        _published.OfType<MediaDerivativesRequested>().Should().BeEmpty();
        _csamScans.TryTake(out Guid scanned).Should().BeTrue();
        scanned.Should().Be(row.ProjectId);
    }

    /// <summary>
    /// QA-P1-12: the copy keeps the render's events. Applying a save copies the render's event sidecar to
    /// mirror the copy's own <c>userFiles/</c> key (never moving it), so an analysis of the copy still
    /// offers its <c>e{n}</c> anchors.
    /// </summary>
    [Fact]
    public async Task Saving_a_render_copies_its_event_sidecar_next_to_the_copy()
    {
        using OutputsWorld w = new();
        string sourceSidecar = $"projects/{w.Project.Id}/agentFiles/render-events/{w.Execution.Id}/final-cut.mp4.events.json";
        byte[] manifest = System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"fps\":30,\"events\":[{\"name\":\"stamp\",\"frame\":90,\"label\":\"stamp happens\"}]}");
        w.Storage.Objects[sourceSidecar] = manifest;
        AssistantOutputTools tools = Tools(w);

        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "teaser.mp4")))
            .GetProperty("confirmationToken").GetString()!;
        JsonElement applied = await Invoke(tools, w, "ApplySaveOutputAsFile",
            ("outputId", w.Render.Id), ("fileName", "teaser.mp4"), ("confirmationToken", token));

        string copyKey = w.Db.ProjectFiles.Single(f => f.Id == applied.GetProperty("fileId").GetGuid()).StorageKey;
        string copySidecar = copyKey.Replace("/userFiles/", "/agentFiles/render-events/", StringComparison.Ordinal) + ".events.json";

        copySidecar.Should().NotBe(sourceSidecar);
        w.Storage.Objects.Should().ContainKey(copySidecar, "the copy carries the render's events");
        w.Storage.Objects[copySidecar].Should().Equal(manifest);
        w.Storage.Objects.Should().ContainKey(sourceSidecar, "a copy, not a move");
    }

    [Fact]
    public async Task The_token_is_bound_to_the_project_the_output_and_the_target_path()
    {
        using OutputsWorld w = new();
        AssistantOutputTools tools = Tools(w);
        var second = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = w.Execution.Id,
            WorkflowStepId = w.Step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{w.Project.Id}/outputFiles/{w.Execution.Id}/other.mp4"
        };
        w.Db.WorkflowStepResults.Add(second);
        await w.Db.SaveChangesAsync();
        w.Storage.Objects[second.OutputStorageKey] = OutputsWorld.Bytes(1000);
        int rowsBefore = w.Db.ProjectFiles.Count();

        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "a.mp4")))
            .GetProperty("confirmationToken").GetString()!;

        (await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", second.Id), ("fileName", "a.mp4"), ("confirmationToken", token)))
            .GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        (await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "b.mp4"), ("confirmationToken", token)))
            .GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        (await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "a.mp4"), ("directoryPath", "x"), ("confirmationToken", token)))
            .GetProperty("error").GetString().Should().Contain("stale confirmationToken");

        // The render changed size since the proposal: what the user confirmed is not what would be copied.
        w.Storage.Objects[w.RenderKey] = OutputsWorld.Bytes(2000);
        (await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "a.mp4"), ("confirmationToken", token)))
            .GetProperty("error").GetString().Should().Contain("stale confirmationToken");

        w.Db.ProjectFiles.Count().Should().Be(rowsBefore);
        _copier!.Copies.Should().Be(0);
    }

    [Theory]
    [InlineData(-3600, -3600, false)]  // run and result before the marker
    [InlineData(3600, -3600, false)]   // run restarted after it, the result is the old one
    [InlineData(-3600, 3600, false)]   // run began before it
    [InlineData(3600, 3600, true)]
    public async Task Saving_an_output_that_may_predate_the_projects_marker_is_refused_and_a_later_one_is_saved(
        int runOffset, int resultOffset, bool saved)
    {
        using OutputsWorld w = new();
        DateTime marker = DateTime.UtcNow.AddDays(-1);
        w.Db.Projects.Single(p => p.Id == w.Project.Id).ContentQuarantinedAt = marker;
        w.Db.WorkflowExecutions.Single(e => e.Id == w.Execution.Id).FirstStartedAt = marker.AddSeconds(runOffset);
        w.Db.WorkflowStepResults.Single(r => r.Id == w.Render.Id).ExecutedAt = marker.AddSeconds(resultOffset);
        await w.Db.SaveChangesAsync();
        AssistantOutputTools tools = Tools(w);
        int rowsBefore = w.Db.ProjectFiles.Count();

        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;
        JsonElement result = await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));

        if (saved)
        {
            result.TryGetProperty("fileId", out _).Should().BeTrue(result.GetRawText());
            w.Db.ProjectFiles.Count().Should().Be(rowsBefore + 1);
        }
        else
        {
            result.GetProperty("code").GetString().Should().Be("FILE_UNAVAILABLE");
            result.GetRawText().Should().NotContainEquivalentOf("quarantin");
            w.Db.ProjectFiles.Count().Should().Be(rowsBefore);
            _copier!.Copies.Should().Be(0, "nothing is copied");
        }
    }

    [Fact]
    public async Task A_quota_violation_writes_nothing()
    {
        using OutputsWorld w = new();
        _planGate.Setup(g => g.CheckUploadAsync(w.OrgId, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "Storage quota exceeded", 413, 500, 450));
        AssistantOutputTools tools = Tools(w);
        int rowsBefore = w.Db.ProjectFiles.Count();
        int objectsBefore = w.Storage.Objects.Count;

        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;
        JsonElement refused = await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));

        refused.GetProperty("code").GetString().Should().Be("STORAGE_QUOTA_EXCEEDED");
        refused.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        w.Db.ProjectFiles.Count().Should().Be(rowsBefore);
        w.Storage.Objects.Count.Should().Be(objectsBefore);
        _copier!.Copies.Should().Be(0);
        _published.Should().BeEmpty();
    }

    /// <summary>S-09 / QA-P0-16: a token is single-use; replaying an applied one answers the same file and copies nothing.</summary>
    [Fact]
    public async Task Replaying_an_applied_token_returns_the_same_file_and_copies_once()
    {
        using OutputsWorld w = new();
        AssistantOutputTools tools = Tools(w);
        int rowsBefore = w.Db.ProjectFiles.Count();
        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "a.mp4")))
            .GetProperty("confirmationToken").GetString()!;

        JsonElement first = await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "a.mp4"), ("confirmationToken", token));
        JsonElement again = await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "a.mp4"), ("confirmationToken", token));

        first.TryGetProperty("error", out _).Should().BeFalse(first.GetRawText());
        again.GetProperty("fileId").GetGuid().Should().Be(first.GetProperty("fileId").GetGuid());
        w.Db.ProjectFiles.Count().Should().Be(rowsBefore + 1);
        _copier!.Copies.Should().Be(1);
        _published.OfType<MediaDerivativesRequested>().Should().BeEmpty("the copy's CSAM verdict is pending");
    }

    /// <summary>S-09: the quota is re-checked after the copy (on the copy's real size); a violation then deletes the copy.</summary>
    [Fact]
    public async Task A_quota_violation_found_after_the_copy_deletes_the_copy_and_records_nothing()
    {
        using OutputsWorld w = new();
        _planGate.SetupSequence(g => g.CheckUploadAsync(w.OrgId, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null)
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "Storage quota exceeded", 413, 500, 450));
        AssistantOutputTools tools = Tools(w);
        int rowsBefore = w.Db.ProjectFiles.Count();
        int objectsBefore = w.Storage.Objects.Count;
        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;

        JsonElement refused = await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));

        refused.GetProperty("code").GetString().Should().Be("STORAGE_QUOTA_EXCEEDED");
        _copier!.Copies.Should().Be(1, "the second check runs after the copy");
        w.Db.ProjectFiles.Count().Should().Be(rowsBefore);
        w.Storage.Objects.Count.Should().Be(objectsBefore, "the copy was deleted");
        _published.Should().BeEmpty();
        _planGate.Verify(g => g.CheckUploadAsync(w.OrgId, 1000, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>QA-P0-26: the copy's key is deterministic for the token, so another API replica can commit its row
    /// for the same key while this call's post-copy quota re-check runs (the storage lock is process-local). The
    /// refusal must not delete a copy a saved row points at.</summary>
    [Fact]
    public async Task A_post_copy_quota_refusal_keeps_a_copy_another_replicas_committed_row_points_at()
    {
        using OutputsWorld w = new();
        _planGate.SetupSequence(g => g.CheckUploadAsync(w.OrgId, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null)
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "Storage quota exceeded", 413, 500, 450));
        _copier = new RivalCommitter(w);
        AssistantOutputTools tools = Tools(w);
        int objectsBefore = w.Storage.Objects.Count;
        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;

        JsonElement refused = await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));

        refused.GetProperty("code").GetString().Should().Be("STORAGE_QUOTA_EXCEEDED");
        _copier!.DestinationKey.Should().NotBeNull();
        w.Db.ProjectFiles.Should().ContainSingle(f => f.StorageKey == _copier.DestinationKey,
            "the rival replica's row is the one the copy must survive for");
        w.Storage.Objects.Should().ContainKey(_copier.DestinationKey!,
            "the copy a committed row points at is never deleted by another replica's refusal");
        w.Storage.Objects.Count.Should().Be(objectsBefore + 1);
    }

    /// <summary>S-LFR-01: the same cross-replica hazard as QA-P0-26, on the save-FAILURE path. The save throws,
    /// and a replica that committed its row for the same deterministic key before that failure points at the
    /// copy, so the failure cleanup must not delete it.</summary>
    [Fact]
    public async Task A_save_failure_keeps_a_copy_another_replicas_committed_row_points_at()
    {
        var interceptor = new OutputsWorld.FailNextSaveInterceptor();
        using OutputsWorld w = new(interceptor);
        _copier = new SaveFailingRivalCommitter(w, interceptor);
        AssistantOutputTools tools = Tools(w);
        int objectsBefore = w.Storage.Objects.Count;
        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;

        try
        {
            await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));
        }
        catch (Exception)
        {
            // The save failure is the answer the caller gets; the assertion below is about the copy.
        }

        _copier!.DestinationKey.Should().NotBeNull();
        w.Db.ProjectFiles.Should().ContainSingle(f => f.StorageKey == _copier.DestinationKey,
            "the rival replica's row is the one the copy must survive for");
        w.Storage.Objects.Should().ContainKey(_copier.DestinationKey!,
            "a save failure never deletes a copy a committed row points at");
        w.Storage.Objects.Count.Should().Be(objectsBefore + 1);
    }

    /// <summary>
    /// SQCR-S-05: when the recovery's copy delete fails, the copy must not be left to nothing. A durable
    /// mark goes in before the delete and stays when the delete fails, so the retention sweep removes it.
    /// </summary>
    [Fact]
    public async Task A_failed_recovery_delete_leaves_a_durable_mark_for_the_copy()
    {
        var interceptor = new OutputsWorld.FailNextSaveInterceptor();
        using OutputsWorld w = new(interceptor);
        _copier = new SaveFailingCopier(w, interceptor);
        AssistantOutputTools tools = Tools(w);
        w.Storage.DeleteFailure = new Amazon.S3.AmazonS3Exception("throttled") { StatusCode = System.Net.HttpStatusCode.ServiceUnavailable };
        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;

        try
        {
            await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));
        }
        catch (Exception)
        {
            // The save failure is the answer the caller gets; the assertion below is about the copy.
        }

        _copier!.DestinationKey.Should().NotBeNull();
        w.Storage.Objects.Should().ContainKey(_copier.DestinationKey!, "the failed delete leaves the copy");
        StorageRetentionMark mark = w.Db.StorageRetentionMarks.Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(_copier.DestinationKey!);
        mark.Category.Should().Be("userFiles");
        mark.PurgedAt.Should().BeNull("the sweep retries an unpurged mark");
    }

    /// <summary>
    /// SQCR-S-14: a later successful apply of the same token must not leave the failed recovery's mark
    /// behind — the row now names the copy, and the purge deletes by key, so the mark would remove an
    /// object a live row points at.
    /// </summary>
    [Fact]
    public async Task A_successful_apply_after_a_failed_recovery_drops_the_mark_it_left()
    {
        var interceptor = new OutputsWorld.FailNextSaveInterceptor();
        using OutputsWorld w = new(interceptor);
        _copier = new SaveFailingOnceCopier(w, interceptor);
        AssistantOutputTools tools = Tools(w);
        w.Storage.DeleteFailure = new Amazon.S3.AmazonS3Exception("throttled") { StatusCode = System.Net.HttpStatusCode.ServiceUnavailable };
        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;

        try
        {
            await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));
        }
        catch (Exception)
        {
            // The save failure is the answer the caller gets.
        }

        w.Storage.DeleteFailure = null;
        JsonElement applied = await Invoke(tools, w, "ApplySaveOutputAsFile",
            ("outputId", w.Render.Id), ("confirmationToken", token));

        applied.GetProperty("fileName").GetString().Should().NotBeNullOrWhiteSpace("the save succeeded");
        w.Db.ProjectFiles.Should().ContainSingle(f => f.StorageKey == _copier!.DestinationKey);
        w.Db.StorageRetentionMarks.Should().BeEmpty("the row names the copy, so no mark may name it");
    }

    /// <summary>
    /// SQCR-S-15 / QA-SQC-8 / SQCR-S-16 / SQCR-S-17: a failure dropping a stale mark must not fall into
    /// the row-insert recovery (whose "another saved it" answer would be a false success), must not carry
    /// raw provider text out of the tool, and must leave the objects this call created nameable.
    /// </summary>
    [Fact]
    public async Task A_failure_dropping_a_stale_mark_answers_a_redacted_error_and_names_the_copy()
    {
        var interceptor = new OutputsWorld.FailNextSaveInterceptor();
        using OutputsWorld w = new(interceptor);
        _copier = new SaveFailingCopier(w, interceptor);   // arms on every copy
        AssistantOutputTools tools = Tools(w);
        // The render has an events sidecar, so the save brings one too: both must be named on failure.
        w.Storage.Objects[RenderEventManifest.SidecarKeyFor(w.RenderKey)!] = OutputsWorld.Bytes(20);
        w.Storage.DeleteFailure = new Amazon.S3.AmazonS3Exception("throttled") { StatusCode = System.Net.HttpStatusCode.ServiceUnavailable };
        string token = (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("confirmationToken").GetString()!;

        // Attempt 1: the row insert fails and the recovery's object delete fails too, so the copy and its
        // sidecar are both named.
        try
        {
            await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", w.Render.Id), ("confirmationToken", token));
        }
        catch (Exception)
        {
            // The save failure is the answer the caller gets.
        }

        string copyKey = MarkedCopyKey(w);
        w.Storage.DeleteFailure = null;
        interceptor.SkipSaves = 1;   // attempt 2: the row insert goes through; the mark drop is the save that fails

        JsonElement refused = await Invoke(tools, w, "ApplySaveOutputAsFile",
            ("outputId", w.Render.Id), ("confirmationToken", token));

        refused.GetProperty("code").GetString().Should().Be("SAVE_FAILED");
        refused.GetProperty("error").GetString().Should()
            .Be("The copy could not be saved just now; nothing was written. Try again in a moment.");
        AssertNoStorageSecrets(w, refused.GetRawText());
        // SQCR-S-17: the copy and its sidecar still have no row, so each stays named for the sweep.
        MarkedCopyKey(w).Should().Be(copyKey);
        w.Db.StorageRetentionMarks.Select(m => m.StorageKey).Should()
            .BeEquivalentTo(copyKey, RenderEventManifest.SidecarKeyFor(copyKey)!);
    }

    /// <summary>
    /// The copy key among the marks: the one that is not its own sidecar. (<c>InMemoryCopier.DestinationKey</c>
    /// holds the LAST copy, which is the sidecar, so it cannot be used to name the copy once a sidecar exists.)
    /// </summary>
    private static string MarkedCopyKey(OutputsWorld w)
    {
        List<string> marked = [.. w.Db.StorageRetentionMarks.Select(m => m.StorageKey)];
        marked.Should().HaveCount(2, "the copy and its sidecar are both named");
        string copy = marked.Single(k => !k.EndsWith(".events.json", StringComparison.Ordinal));
        marked.Should().Contain(RenderEventManifest.SidecarKeyFor(copy)!, "the other mark mirrors it");
        return copy;
    }

    [Fact]
    public async Task Only_a_render_can_be_saved_and_never_another_organizations()
    {
        using OutputsWorld w = new();
        (_, WorkflowStepResult theirRender) = w.AddForeignProject();
        AssistantOutputTools tools = Tools(w);

        (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.File.Id))).GetProperty("error").GetString()
            .Should().Be("output not found");
        (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", theirRender.Id))).GetProperty("error").GetString()
            .Should().Be("output not found");
        (await Invoke(tools, w, "ApplySaveOutputAsFile", ("outputId", theirRender.Id), ("confirmationToken", "x"))).GetProperty("error").GetString()
            .Should().Be("output not found");
        _copier!.Copies.Should().Be(0);
    }

    [Fact]
    public async Task The_file_name_keeps_the_renders_extension()
    {
        using OutputsWorld w = new();
        AssistantOutputTools tools = Tools(w);

        (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "teaser.txt")))
            .GetProperty("error").GetString().Should().Contain(".mp4");
        (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id), ("fileName", "../escape.mp4")))
            .GetProperty("error").GetString().Should().Contain("invalid path");
        (await Invoke(tools, w, "ProposeSaveOutputAsFile", ("outputId", w.Render.Id)))
            .GetProperty("fileName").GetString().Should().Be("final-cut.mp4");
    }
}
