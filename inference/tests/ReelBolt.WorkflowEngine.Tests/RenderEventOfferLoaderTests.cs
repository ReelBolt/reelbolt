using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-110: which render event sidecars an analysis offers — every analysed source that is a render, and the
/// insert content a downstream SFX+inserts compile consuming THIS analysis pins (by reference or project file).
/// </summary>
public class RenderEventOfferLoaderTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly string RenderA = $"projects/{ProjectId}/outputFiles/22222222-2222-2222-2222-222222222222/film.mp4";
    private static readonly string RenderB = $"projects/{ProjectId}/outputFiles/44444444-4444-4444-4444-444444444444/screen.mp4";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Sidecar(params (string Name, int Frame)[] events) =>
        (RenderEventManifest.Validate(events.Select(e => new RenderEvent(e.Name, e.Frame, e.Name + " happens")).ToList()).Manifest! with { Fps = 30 }).ToJson();

    private sealed class Harness
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _staged = new(StringComparer.Ordinal);

        public Harness()
        {
            Workspace.Setup(w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((key, path, _) =>
                {
                    if (!_store.TryGetValue(key, out string? text))
                        throw new InvalidOperationException("no such object");
                    _staged[path] = text;
                    return Task.CompletedTask;
                });
            Workspace.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<string, int, CancellationToken>((path, _, _) => Task.FromResult(Encoding.UTF8.GetBytes(_staged[path])));
            Files.Setup(f => f.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ProjectFiles);
        }

        public Mock<IMediaWorkspace> Workspace { get; } = new();
        public Mock<IProjectFileWorkspace> Files { get; } = new();
        public List<ProjectWorkspaceFile> ProjectFiles { get; } = [];
        public List<WorkflowStep> Steps { get; } = [new WorkflowStep { StepOrder = 1, StepType = StepType.VideoAnalyze }];
        public List<StepOutputHistoryEntry> History { get; } = [];

        public void Put(string renderKey, string sidecarJson) => PutSidecar(RenderEventManifest.SidecarKeyFor(renderKey)!, sidecarJson);

        /// <summary>Puts a sidecar at an explicit key, so a test does not depend on the key mapping it exercises.</summary>
        public void PutSidecar(string sidecarKey, string sidecarJson) => _store[sidecarKey] = sidecarJson;

        public void AddCompile(VideoCompileStepConfig config, int order = 5) =>
            Steps.Add(new WorkflowStep { StepOrder = order, StepType = StepType.VideoCompile, VideoCompileConfigJson = JsonSerializer.Serialize(config, Options) });

        public Task<List<RenderEventAnchors.EventSource>> LoadAsync(params RenderEventOfferLoader.SourceWindow[] sources) =>
            RenderEventOfferLoader.LoadAsync(
                sources, Steps, analyzeStepOrder: 1, ProjectId, History, Files.Object, scopes: null, Workspace.Object, CancellationToken.None);
    }

    private static VideoCompileStepConfig Compile(int analysisStepOrder = 1, bool sfx = true, bool inserts = true) =>
        new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: analysisStepOrder)
        {
            EnableSfx = sfx,
            EnableInserts = inserts
        };

    [Fact]
    public async Task A_source_that_is_a_render_with_a_sidecar_offers_its_events_on_the_trimmed_window()
    {
        Harness h = new();
        h.Put(RenderA, Sidecar(("stamp", 90)));

        List<RenderEventAnchors.EventSource> sources = await h.LoadAsync(new RenderEventOfferLoader.SourceWindow(0, RenderA, 2.0, 5.0));

        RenderEventAnchors.EventSource source = sources.Should().ContainSingle().Subject;
        source.SourceIndex.Should().Be(0);
        source.TrimInSec.Should().Be(2.0);
        source.DurationSec.Should().Be(5.0);
        source.Manifest.Events.Should().ContainSingle().Which.Name.Should().Be("stamp");
    }

    [Fact]
    public async Task A_source_without_a_sidecar_offers_nothing()
    {
        Harness h = new();

        (await h.LoadAsync(new RenderEventOfferLoader.SourceWindow(0, RenderA, null, 5.0))).Should().BeEmpty();
        (await h.LoadAsync(new RenderEventOfferLoader.SourceWindow(0, $"projects/{ProjectId}/uploads/take.mp4", null, 5.0))).Should().BeEmpty();
    }

    [Fact]
    public async Task Insert_content_pinned_by_step_reference_offers_its_events()
    {
        Harness h = new();
        h.Put(RenderB, Sidecar(("row", 30)));
        h.History.Add(new StepOutputHistoryEntry(3, "Author", "{}", OutputStorageKey: RenderB));
        h.AddCompile(Compile() with { InsertContentRef = new InsertContentRef(InsertContentRef.StepKind, StepOrder: 3) });

        RenderEventAnchors.EventSource source = (await h.LoadAsync()).Should().ContainSingle().Subject;

        source.InsertContentStorageKey.Should().Be(RenderB);
        source.SourceIndex.Should().BeNull();
    }

    [Fact]
    public async Task Insert_content_pinned_as_a_project_file_kept_under_outputFiles_offers_its_events()
    {
        Harness h = new();
        h.Put(RenderB, Sidecar(("row", 30)));
        h.ProjectFiles.Add(new ProjectWorkspaceFile(FileId, ProjectId, "screen.mp4", null, "outputFiles", RenderB, "video/mp4", 10, DateTime.UtcNow, null));
        h.AddCompile(Compile() with { InsertContents = [new VideoInsertContentAssignment(FileId, RegionId: "r0")] });

        (await h.LoadAsync()).Should().ContainSingle().Which.InsertContentStorageKey.Should().Be(RenderB);
    }

    [Theory]
    [InlineData(2, true, true)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    public async Task Only_a_compile_of_this_analysis_with_sfx_and_inserts_on_contributes(int analysisStepOrder, bool sfx, bool inserts)
    {
        Harness h = new();
        h.Put(RenderB, Sidecar(("row", 30)));
        h.History.Add(new StepOutputHistoryEntry(3, "Author", "{}", OutputStorageKey: RenderB));
        h.AddCompile(Compile(analysisStepOrder, sfx, inserts) with { InsertContentRef = new InsertContentRef(InsertContentRef.StepKind, StepOrder: 3) });

        (await h.LoadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_content_pinned_twice_is_offered_once_and_a_step_that_has_not_run_offers_nothing()
    {
        Harness h = new();
        h.Put(RenderB, Sidecar(("row", 30)));
        h.History.Add(new StepOutputHistoryEntry(3, "Author", "{}", OutputStorageKey: RenderB));
        h.AddCompile(Compile() with
        {
            InsertContentRef = new InsertContentRef(InsertContentRef.StepKind, StepOrder: 3),
            InsertContents =
            [
                new VideoInsertContentAssignment(Guid.Empty, RegionId: "r1", InsertContentRef: new InsertContentRef(InsertContentRef.StepKind, StepOrder: 3)),
                new VideoInsertContentAssignment(Guid.Empty, RegionId: "r2", InsertContentRef: new InsertContentRef(InsertContentRef.StepKind, StepOrder: 7))
            ]
        });

        (await h.LoadAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task An_unreadable_sidecar_or_compile_config_offers_nothing_and_never_throws()
    {
        Harness h = new();
        h.Put(RenderA, "{ not json");
        h.Steps.Add(new WorkflowStep { StepOrder = 6, StepType = StepType.VideoCompile, VideoCompileConfigJson = "{ nope" });

        (await h.LoadAsync(new RenderEventOfferLoader.SourceWindow(0, RenderA, null, 5.0))).Should().BeEmpty();
    }

    /// <summary>
    /// QA-P1-12: a render saved as a project file keeps its <c>e{n}</c> events. <c>ApplySaveOutputAsFile</c>
    /// copies the render's sidecar to mirror the copy's own <c>userFiles/</c> key, so an analysis that pins
    /// the copy still offers the render's events — while an unrelated <c>userFiles</c> entry, which has no
    /// sidecar, offers none.
    /// </summary>
    [Fact]
    public async Task A_render_copied_to_a_project_file_offers_its_events_and_an_unrelated_user_file_offers_none()
    {
        Harness h = new();
        Guid copiedFile = Guid.Parse("55555555-5555-5555-5555-555555555555");
        string copyKey = $"projects/{ProjectId}/userFiles/{copiedFile}.mp4";
        Guid unrelatedFile = Guid.Parse("66666666-6666-6666-6666-666666666666");
        string unrelatedKey = $"projects/{ProjectId}/userFiles/{unrelatedFile}.mp4";

        // The sidecar the save copies next to the copy, mirroring the copy's own key.
        h.PutSidecar($"projects/{ProjectId}/agentFiles/render-events/{copiedFile}.mp4.events.json", Sidecar(("stamp", 90)));
        h.ProjectFiles.Add(new ProjectWorkspaceFile(copiedFile, ProjectId, "film.mp4", null, "userFiles", copyKey, "video/mp4", 10, DateTime.UtcNow, null));
        h.ProjectFiles.Add(new ProjectWorkspaceFile(unrelatedFile, ProjectId, "take.mp4", null, "userFiles", unrelatedKey, "video/mp4", 10, DateTime.UtcNow, null));
        h.AddCompile(Compile() with
        {
            InsertContents =
            [
                new VideoInsertContentAssignment(copiedFile, RegionId: "r0"),
                new VideoInsertContentAssignment(unrelatedFile, RegionId: "r1")
            ]
        });

        RenderEventAnchors.EventSource source = (await h.LoadAsync()).Should().ContainSingle().Subject;

        source.InsertContentStorageKey.Should().Be(copyKey);
        source.Manifest.Events.Should().ContainSingle().Which.Name.Should().Be("stamp");
    }
}
