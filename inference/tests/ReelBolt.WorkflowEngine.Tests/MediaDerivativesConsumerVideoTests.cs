#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for <see cref="MediaDerivativesConsumer"/> and the parts of
/// <see cref="MediaDerivativeDispatcher"/> that do not need real media: the database confirmation that
/// makes a stale message write nothing, the immediate handoff that keeps a delivery from being held
/// open for an encode, the closed MIME/kind plan, the row identity fields, and the empty-ETag
/// fallback.
/// </summary>
/// <remarks>
/// The production side (real ffmpeg, real artifacts, the invocation-count cache hit and the etag
/// collision) lives in <c>MediaDerivativeCacheHitFfmpegTests</c>, which shares this file's harness.
/// </remarks>
public class MediaDerivativesConsumerVideoTests
{
    private readonly ITestOutputHelper _output;

    public MediaDerivativesConsumerVideoTests(ITestOutputHelper output) => _output = output;

    // =========================================================================================
    // Registration
    // =========================================================================================

    [Fact]
    public void The_writer_resolves_from_the_real_service_registrations_with_scope_validation_on()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine-media-derivatives",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));

        MediaDerivativeDispatcher fromRoot = root.Services.GetRequiredService<MediaDerivativeDispatcher>();
        MediaDerivativeDispatcher fromSecondCall = root.Services.GetRequiredService<MediaDerivativeDispatcher>();

        fromRoot.Should().NotBeNull();
        fromRoot.Should().BeSameAs(fromSecondCall,
            "the dispatcher's in-flight map and derivative slot cap are only meaningful if exactly one instance exists");

        root.Services.GetRequiredService<IMediaDerivativeProgressSink>()
            .Should().BeOfType<LoggingMediaDerivativeProgressSink>();
        root.Services.GetRequiredService<MediaDerivativeService>().Should().NotBeNull();
        root.Services.GetRequiredService<IProjectFileWorkspace>().Should().NotBeNull();
    }

    [Fact]
    public void The_consumer_consumes_the_derivatives_event()
    {
        typeof(MediaDerivativesConsumer).Should().BeAssignableTo<IConsumer<MediaDerivativesRequested>>();

        Type[] parameterTypes = typeof(MediaDerivativesConsumer).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType).ToArray();

        parameterTypes.Should().Equal(
            typeof(IDbContextFactory<WorkflowEngineDbContext>),
            typeof(MediaDerivativeDispatcher),
            typeof(IMediaDerivativeProgressSink),
            typeof(Microsoft.Extensions.Logging.ILogger<MediaDerivativesConsumer>));
    }

    // =========================================================================================
    // The stale-message rule
    // =========================================================================================

    [Fact]
    public async Task A_stale_project_file_message_writes_no_row_and_never_downloads()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        SeedProjectFile(fixture, fileId, projectId, "projects/" + projectId.ToString("D") + "/userFiles/current.mp4");

        MediaDerivativesConsumer consumer = NewConsumer(fixture);

        // The message was published when the object had a DIFFERENT key: it is stale.
        await consumer.Consume(NewContext(fixture.Request(
            projectId,
            "projects/" + projectId.ToString("D") + "/userFiles/replaced.mp4",
            "\"etag-1\"",
            [MediaDerivativeKind.Proxy],
            sourceProjectFileId: fileId)));

        fixture.ReadRows().Should().BeEmpty(
            "a stale message must never produce a row claiming an etag the object no longer has");
        fixture.Workspace.DownloadCount.Should().Be(0, "a stale message must not even start the work");

        fixture.Sink.Stages.Should().Contain(MediaDerivativeProgressStage.Skipped);
        fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed,
            "a stale message is a skip, not a failure");
    }

    [Fact]
    public async Task A_message_for_another_projects_file_writes_no_row()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid owningProject = Guid.NewGuid();
        Guid otherProject = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        string storageKey = "projects/" + owningProject.ToString("D") + "/userFiles/a.mp4";

        SeedProjectFile(fixture, fileId, owningProject, storageKey);

        MediaDerivativesConsumer consumer = NewConsumer(fixture);

        // Same storage key, but the message claims a project that does not own the row.
        await consumer.Consume(NewContext(fixture.Request(
            otherProject, storageKey, "\"etag-1\"", [MediaDerivativeKind.Proxy], sourceProjectFileId: fileId)));

        fixture.ReadRows().Should().BeEmpty();
        fixture.Workspace.DownloadCount.Should().Be(0);
    }

    [Fact]
    public async Task A_stale_step_result_message_is_confirmed_against_the_step_results_output_storage_key()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();
        Guid stepResultId = Guid.NewGuid();
        string currentKey = "projects/" + projectId.ToString("D") + "/outputFiles/exec/render.mp4";

        SeedStepResult(fixture, stepResultId, projectId, currentKey);

        MediaDerivativesConsumer consumer = NewConsumer(fixture);

        MediaDerivativesRequested stale = new(
            projectId,
            new MediaSourceRef(MediaSourceKind.StepResult, stepResultId),
            SourceProjectFileId: null,
            SourceStorageKey: "projects/" + projectId.ToString("D") + "/outputFiles/exec/older-render.mp4",
            SourceEtag: "\"etag-1\"",
            MimeType: "video/mp4",
            Kinds: [MediaDerivativeKind.KeyframeIndex],
            RequestedBy: "render-output",
            RequestedAt: DateTime.UtcNow);

        await consumer.Consume(NewContext(stale));

        fixture.ReadRows().Should().BeEmpty();
        fixture.Workspace.DownloadCount.Should().Be(0);

        // ...and the same step result, with the key it actually has, is accepted.
        MediaDerivativesRequested current = stale with { SourceStorageKey = currentKey };

        fixture.Workspace.DownloadGate = new TaskCompletionSource().Task;
        await consumer.Consume(NewContext(current));

        await WaitForAsync(() => fixture.Workspace.DownloadCount == 1);
        fixture.Workspace.DownloadCount.Should().Be(1, "a current step-result message must be handed off and start work");
    }

    [Fact]
    public async Task A_message_with_no_storage_key_is_dropped_without_touching_the_database()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();

        await NewConsumer(fixture).Consume(NewContext(fixture.Request(
            projectId, string.Empty, "\"etag\"", [MediaDerivativeKind.Proxy])));

        fixture.ReadRows().Should().BeEmpty();
        fixture.Workspace.DownloadCount.Should().Be(0);
    }

    // =========================================================================================
    // The handoff
    // =========================================================================================

    [Fact]
    public async Task The_delivery_is_acknowledged_while_the_encode_is_still_running()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        string storageKey = "projects/" + projectId.ToString("D") + "/userFiles/a.mp4";

        SeedProjectFile(fixture, fileId, projectId, storageKey);

        // A download that never completes stands in for an encode that takes minutes. If the consumer
        // awaited the work, this call would never return.
        var never = new TaskCompletionSource();
        fixture.Workspace.DownloadGate = never.Task;

        MediaDerivativesConsumer consumer = NewConsumer(fixture);

        Stopwatch stopwatch = Stopwatch.StartNew();
        await consumer
            .Consume(NewContext(fixture.Request(
                projectId, storageKey, "\"etag-1\"", [MediaDerivativeKind.Proxy], sourceProjectFileId: fileId)))
            .WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the delivery must be acknowledged in milliseconds, not held open for the encode");

        await WaitForAsync(() => fixture.Workspace.DownloadCount == 1);
        never.Task.IsCompleted.Should().BeFalse("the background work is still running, which is the point");

        _output.WriteLine(
            "MEASURED handoff: Consume returned in {0:F0} ms while the work was still blocked in the download",
            stopwatch.Elapsed.TotalMilliseconds);

        never.SetResult();
    }

    [Fact]
    public async Task The_dispatcher_never_throws_when_the_source_cannot_be_downloaded()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();
        string storageKey = "projects/" + projectId.ToString("D") + "/userFiles/a.mp4";

        fixture.Workspace.DownloadFailure = new IOException("the object store refused the read");

        Func<Task> run = () => fixture.Dispatcher.RunAsync(
            fixture.Request(projectId, storageKey, "\"etag-1\"", [MediaDerivativeKind.Proxy]),
            CancellationToken.None);

        await run.Should().NotThrowAsync("a derivative failure must never fail a workflow");

        fixture.ReadRows().Should().BeEmpty("the row stays absent so a later request retries");
        fixture.Sink.Stages.Should().Contain(MediaDerivativeProgressStage.Failed);
    }

    [Fact]
    public async Task The_consumer_never_throws_when_the_dispatcher_or_the_database_fails()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        string storageKey = "projects/" + projectId.ToString("D") + "/userFiles/a.mp4";

        SeedProjectFile(fixture, fileId, projectId, storageKey);
        fixture.Workspace.DownloadFailure = new InvalidOperationException("boom");

        Func<Task> consume = () => NewConsumer(fixture).Consume(
            NewContext(fixture.Request(
                projectId, storageKey, "\"etag-1\"", [MediaDerivativeKind.Proxy], sourceProjectFileId: fileId)));

        await consume.Should().NotThrowAsync();

        // Give the fire-and-forget run a moment to finish failing, then assert nothing escaped.
        await WaitForAsync(() => fixture.Sink.Stages.Contains(MediaDerivativeProgressStage.Failed));
        fixture.ReadRows().Should().BeEmpty();
    }

    // =========================================================================================
    // The wire event (T-308)
    // =========================================================================================

    /// <summary>
    /// The swap this ticket is: the seam a derivative run reports through now publishes the shared
    /// <see cref="MediaDerivativeProgress"/> event — the one thing that lets the media pool render
    /// "Generating proxy 42 %".
    /// </summary>
    [Fact]
    public async Task A_transition_is_published_as_a_MediaDerivativeProgress_event()
    {
        var bus = new Mock<IBus>();
        MediaDerivativeProgress? published = null;

        bus.Setup(b => b.Publish(It.IsAny<MediaDerivativeProgress>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativeProgress, CancellationToken>((message, _) => published = message)
            .Returns(Task.CompletedTask);

        var sink = new LoggingMediaDerivativeProgressSink(
            NullLogger<LoggingMediaDerivativeProgressSink>.Instance, bus.Object);

        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        sink.Report(new MediaDerivativeProgressUpdate(
            projectId,
            fileId,
            "projects/" + projectId.ToString("D") + "/userFiles/a.mp4",
            "cache-key-1",
            MediaDerivativeProgressStage.Proxy,
            PercentComplete: 42,
            Detail: "encoding"));

        await WaitForAsync(() => published is not null);

        published!.ProjectId.Should().Be(projectId);
        published.SourceProjectFileId.Should().Be(fileId);
        published.SourceStorageKey.Should().EndWith("/userFiles/a.mp4");
        published.CacheKey.Should().Be("cache-key-1");
        published.Stage.Should().Be("Proxy",
            "the stage travels as a NAME from the closed vocabulary, so the consumer never references the engine's enum");
        published.PercentComplete.Should().Be(42);
        published.Detail.Should().Be("encoding");
        published.OccurredAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        // The refusal this whole seam exists for, asserted rather than assumed: a derivative is not a
        // workflow step, and WorkflowStepProgress routed by invented ids would render a phantom step
        // card in the workflow monitor.
        bus.Verify(
            b => b.Publish(It.IsAny<WorkflowStepProgress>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a derivative has no execution id, step id or step-result id and must not publish a step event");

        _output.WriteLine(
            "MEASURED published event: project={0}, file={1}, stage={2}, percent={3}, cacheKey={4}",
            published.ProjectId, published.SourceProjectFileId, published.Stage,
            published.PercentComplete, published.CacheKey);
    }

    /// <summary>
    /// A run-level transition carries a null cache key (a MIME-type skip, a stale message) and a null
    /// source file id (an unregistered render) — and both must survive the mapping as null rather
    /// than as an empty string, which would claim an identity that does not exist.
    /// </summary>
    [Fact]
    public async Task A_run_level_transition_keeps_its_unknowns_null()
    {
        var bus = new Mock<IBus>();
        MediaDerivativeProgress? published = null;

        bus.Setup(b => b.Publish(It.IsAny<MediaDerivativeProgress>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativeProgress, CancellationToken>((message, _) => published = message)
            .Returns(Task.CompletedTask);

        var sink = new LoggingMediaDerivativeProgressSink(
            NullLogger<LoggingMediaDerivativeProgressSink>.Instance, bus.Object);

        sink.Report(new MediaDerivativeProgressUpdate(
            Guid.NewGuid(),
            SourceProjectFileId: null,
            "projects/p/outputFiles/render.mp4",
            CacheKey: null,
            MediaDerivativeProgressStage.Skipped,
            Detail: "No derivatives are defined for MIME type 'image/png'"));

        await WaitForAsync(() => published is not null);

        published!.SourceProjectFileId.Should().BeNull();
        published.CacheKey.Should().BeNull();
        published.PercentComplete.Should().BeNull();
    }

    /// <summary>
    /// Publication must never cost the work it describes: the sink is called from the derivative's
    /// own commit path, so a broker that throws — synchronously or asynchronously — must be swallowed
    /// at Debug level, and <see cref="IMediaDerivativeProgressSink.Report"/> must return normally.
    /// </summary>
    [Fact]
    public async Task A_broker_that_throws_can_never_reach_the_derivative()
    {
        var bus = new Mock<IBus>();
        bus.Setup(b => b.Publish(It.IsAny<MediaDerivativeProgress>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the bus is not started"));

        var sink = new LoggingMediaDerivativeProgressSink(
            NullLogger<LoggingMediaDerivativeProgressSink>.Instance, bus.Object);

        var update = new MediaDerivativeProgressUpdate(
            Guid.NewGuid(), Guid.NewGuid(), "projects/p/userFiles/a.mp4", "cache-key-1",
            MediaDerivativeProgressStage.Done, PercentComplete: 100);

        Action report = () => sink.Report(update);
        report.Should().NotThrow();

        // The fire-and-forget task must observe its own failure rather than surface it later.
        Func<Task> publish = () => sink.PublishSafelyAsync(update);
        await publish.Should().NotThrowAsync();
    }

    /// <summary>
    /// A harness that builds the engine's registrations without a broker must still resolve this
    /// sink: the absence of a bus costs the GUI its progress frames and nothing else.
    /// </summary>
    [Fact]
    public async Task A_sink_without_a_bus_reports_without_publishing()
    {
        var sink = new LoggingMediaDerivativeProgressSink(
            NullLogger<LoggingMediaDerivativeProgressSink>.Instance);

        var update = new MediaDerivativeProgressUpdate(
            Guid.NewGuid(), Guid.NewGuid(), "projects/p/userFiles/a.mp4", null,
            MediaDerivativeProgressStage.Queued);

        Action report = () => sink.Report(update);
        report.Should().NotThrow();

        Func<Task> publish = () => sink.PublishSafelyAsync(update);
        await publish.Should().NotThrowAsync();
    }

    // =========================================================================================
    // The closed plan
    // =========================================================================================

    [Fact]
    public async Task An_image_source_is_skipped_without_a_single_tool_invocation()
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();
        string storageKey = "projects/" + projectId.ToString("D") + "/userFiles/a.png";

        await fixture.Dispatcher.RunAsync(
            fixture.Request(projectId, storageKey, "\"etag-1\"", [], mimeType: "image/png"),
            CancellationToken.None);

        fixture.ReadRows().Should().BeEmpty();
        fixture.Workspace.DownloadCount.Should().Be(0);
        fixture.Runner.FfmpegCount.Should().Be(0);
        fixture.Runner.FfprobeCount.Should().Be(0);
        fixture.Sink.Stages.Should().Contain(MediaDerivativeProgressStage.Skipped);
        fixture.Sink.Updates.Last().Detail.Should().Contain("No derivatives are defined");
    }

    [Fact]
    public void A_video_source_gets_the_full_closed_set_and_nothing_else()
    {
        IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan =
            MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", kinds: null);

        plan.Should().HaveCount(8);
        plan.Count(p => p.Slug == MediaDerivativeKindSlug.Proxy).Should().Be(1);
        plan.Where(p => p.Slug == MediaDerivativeKindSlug.Filmstrip).Select(p => p.Variant).Should().Equal(0, 1, 2);
        plan.Where(p => p.Slug == MediaDerivativeKindSlug.WaveformPeaks).Select(p => p.Variant).Should().Equal(0, 1, 2);
        plan.Count(p => p.Slug == MediaDerivativeKindSlug.KeyframeIndex).Should().Be(1);
    }

    [Fact]
    public void An_audio_source_gets_peaks_only()
    {
        IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan =
            MediaDerivativeDispatcher.ResolveSpecPlan("audio/wav", kinds: null);

        plan.Should().HaveCount(3);
        plan.Should().OnlyContain(p => p.Slug == MediaDerivativeKindSlug.WaveformPeaks);
    }

    [Fact]
    public void The_messages_kinds_can_only_narrow_the_mime_types_set()
    {
        // Narrowing within the video set.
        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", [MediaDerivativeKind.Filmstrip])
            .Should().Equal((MediaDerivativeKindSlug.Filmstrip, 0), (MediaDerivativeKindSlug.Filmstrip, 1), (MediaDerivativeKindSlug.Filmstrip, 2));

        // Widening is refused: an audio source cannot be asked for a proxy.
        MediaDerivativeDispatcher.ResolveSpecPlan("audio/wav", [MediaDerivativeKind.Proxy]).Should().BeEmpty();

        // A kind that maps to no slug, and an image source, both yield nothing rather than a guess.
        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", [MediaDerivativeKind.Thumbnail]).Should().BeEmpty();
        MediaDerivativeDispatcher.ResolveSpecPlan("image/jpeg", [MediaDerivativeKind.Proxy]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("video/mp4", MediaDerivativeSourceClass.Video)]
    [InlineData("VIDEO/MP4", MediaDerivativeSourceClass.Video)]
    [InlineData("audio/mpeg", MediaDerivativeSourceClass.Audio)]
    [InlineData("image/png", MediaDerivativeSourceClass.Image)]
    [InlineData("application/octet-stream", MediaDerivativeSourceClass.Unknown)]
    [InlineData("", MediaDerivativeSourceClass.Unknown)]
    [InlineData(null, MediaDerivativeSourceClass.Unknown)]
    public void Mime_classification_is_prefix_based_and_never_guesses(string? mimeType, MediaDerivativeSourceClass expected)
    {
        MediaDerivativeDispatcher.ClassifyMimeType(mimeType).Should().Be(expected);
    }

    // =========================================================================================
    // Row identity
    // =========================================================================================

    [Fact]
    public void BuildRow_sets_the_project_file_pointer_for_a_project_file_source()
    {
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForKeyframeIndex();
        MediaDerivativeResult result = Result(spec, "keyframes-abc.json", "application/json");

        MediaDerivativesRequested message = new(
            projectId,
            new MediaSourceRef(MediaSourceKind.ProjectFile, fileId),
            fileId,
            "projects/p/userFiles/a.mp4",
            "\"etag\"",
            "video/mp4",
            [MediaDerivativeKind.KeyframeIndex],
            "upload",
            DateTime.UtcNow);

        MediaDerivative row = MediaDerivativeDispatcher.BuildRow(
            message, spec, result, "\"etag\"", "projects/p/agentFiles/key.json", "application/json", 42, "reelforge-test");

        row.ProjectId.Should().Be(projectId);
        row.SourceKind.Should().Be(MediaSourceKind.ProjectFile);
        row.SourceProjectFileId.Should().Be(fileId);
        row.SourceStepResultId.Should().BeNull();
        row.Kind.Should().Be(MediaDerivativeKind.KeyframeIndex);
        row.ParamsJson.Should().Be(spec.ParamsJson);
        row.ParamsHash.Should().Be(MediaDerivativeCacheKey.ComputeParamsHash(spec));
        row.BuilderVersion.Should().Be(spec.BuilderVersion);
        row.CacheKey.Should().Be(result.CacheKey);
        row.StorageKey.Should().Be("projects/p/agentFiles/key.json");
        row.MimeType.Should().Be("application/json");
        row.SizeBytes.Should().Be(42);
        row.StorageBucket.Should().Be("reelforge-test");
    }

    [Fact]
    public void BuildRow_sets_the_step_result_pointer_and_leaves_the_file_pointer_null_for_an_unregistered_render()
    {
        Guid projectId = Guid.NewGuid();
        Guid stepResultId = Guid.NewGuid();
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForKeyframeIndex();
        MediaDerivativeResult result = Result(spec, "keyframes-abc.json", "application/json");

        MediaDerivativesRequested message = new(
            projectId,
            new MediaSourceRef(MediaSourceKind.StepResult, stepResultId),
            SourceProjectFileId: null,
            SourceStorageKey: "projects/p/outputFiles/exec/render.mp4",
            SourceEtag: "\"etag\"",
            MimeType: "video/mp4",
            Kinds: [MediaDerivativeKind.KeyframeIndex],
            RequestedBy: "render-output",
            RequestedAt: DateTime.UtcNow);

        MediaDerivative row = MediaDerivativeDispatcher.BuildRow(
            message, spec, result, "\"etag\"", "projects/p/agentFiles/key.json", "application/json", 7, "bucket");

        row.SourceKind.Should().Be(MediaSourceKind.StepResult);
        row.SourceProjectFileId.Should().BeNull("an unregistered render has no ProjectFile row to point at");
        row.SourceStepResultId.Should().Be(stepResultId);
    }

    [Fact]
    public void SelectPrimaryArtifact_prefers_the_sprite_sheet_for_a_filmstrip_and_the_variants_own_lod_for_peaks()
    {
        MediaDerivativeSpec filmstrip = MediaDerivativeSpec.ForFilmstrip(0);
        var filmstripResult = new MediaDerivativeResult(
            MediaDerivativeKindSlug.Filmstrip,
            0,
            filmstrip.ParamsJson,
            "key",
            "projects/p/agentFiles/media-derivatives/h",
            160,
            120,
            3,
            10,
            [
                new MediaDerivativeArtifact("/tmp/sheet.jpg", "filmstrip-h-sheet-001.jpg", "image/jpeg"),
                new MediaDerivativeArtifact("/tmp/index.json", "filmstrip-h-index.json", "application/json")
            ],
            "{}");

        MediaDerivativeDispatcher.SelectPrimaryArtifact(filmstripResult).ContentType.Should().Be("image/jpeg");

        // Peaks: the sidecar is byte-identical for all three levels of detail, so a row must address
        // its OWN LOD file or three rows would name one shared object.
        MediaDerivativeSpec peaks = MediaDerivativeSpec.ForWaveformPeaks(1);
        var peaksArtifacts = new List<MediaDerivativeArtifact>
        {
            new("/tmp/lod20.raw", "peaks-h-lod20ms.raw", "application/octet-stream"),
            new("/tmp/lod100.raw", "peaks-h-lod100ms.raw", "application/octet-stream"),
            new("/tmp/lod1000.raw", "peaks-h-lod1000ms.raw", "application/octet-stream"),
            new("/tmp/peaks.json", "peaks-h-index.json", "application/json")
        };

        var peaksResult = new MediaDerivativeResult(
            MediaDerivativeKindSlug.WaveformPeaks,
            1,
            peaks.ParamsJson,
            "key",
            "projects/p/agentFiles/media-derivatives/h",
            0,
            0,
            3,
            10,
            peaksArtifacts,
            "{}");

        MediaDerivativeDispatcher.SelectPrimaryArtifact(peaksResult).FileName.Should().Be(
            "peaks-h-lod100ms.raw",
            "a peaks row must name its own level of detail, not the sidecar every variant shares");

        // And the other two variants address their own levels, not the same one.
        MediaDerivativeResult variant0 = peaksResult with { Variant = 0 };
        MediaDerivativeResult variant2 = peaksResult with { Variant = 2 };

        MediaDerivativeDispatcher.SelectPrimaryArtifact(variant0).FileName.Should().Be("peaks-h-lod20ms.raw");
        MediaDerivativeDispatcher.SelectPrimaryArtifact(variant2).FileName.Should().Be("peaks-h-lod1000ms.raw");

        new[]
        {
            MediaDerivativeDispatcher.SelectPrimaryArtifact(peaksResult).FileName,
            MediaDerivativeDispatcher.SelectPrimaryArtifact(variant0).FileName,
            MediaDerivativeDispatcher.SelectPrimaryArtifact(variant2).FileName
        }.Distinct(StringComparer.Ordinal).Should().HaveCount(3);
    }

    // =========================================================================================
    // The empty-ETag fallback, and the path-safety helpers
    // =========================================================================================

    [Fact]
    public void ResolveEffectiveEtag_passes_a_real_etag_through_and_hashes_the_object_when_it_is_absent()
    {
        string path = Path.Combine(Path.GetTempPath(), "reelforge-etag-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("some bytes"));

            MediaDerivativeDispatcher.ResolveEffectiveEtag("\"abc\"", path).Should().Be("\"abc\"");
            MediaDerivativeDispatcher.ResolveEffectiveEtag(string.Empty, path).Should().StartWith("sha256:");
            MediaDerivativeDispatcher.ResolveEffectiveEtag("   ", path).Should().StartWith("sha256:");

            // Stable for the same bytes, and distinct for different ones — which is what makes it a
            // usable content identity rather than a random value.
            string first = MediaDerivativeDispatcher.ResolveEffectiveEtag(string.Empty, path);
            MediaDerivativeDispatcher.ResolveEffectiveEtag(string.Empty, path).Should().Be(first);

            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("other bytes"));
            MediaDerivativeDispatcher.ResolveEffectiveEtag(string.Empty, path).Should().NotBe(first);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData("projects/p/userFiles/a.mp4", ".mp4")]
    [InlineData("projects/p/userFiles/a.MOV", ".MOV")]
    [InlineData("projects/p/userFiles/noext", "")]
    [InlineData("projects/p/userFiles/a.wav", ".wav")]
    [InlineData("projects/p/userFiles/C0001.MXF", ".MXF")]
    [InlineData("projects/p/userFiles/a.mxf", ".mxf")]
    [InlineData("projects/p/userFiles/00001.MTS", ".MTS")]
    [InlineData("projects/p/userFiles/a.m2ts", ".m2ts")]
    [InlineData("projects/p/userFiles/a.tar.gz", ".bin")]
    [InlineData("projects/p/userFiles/a.m3u8", ".bin")]
    [InlineData("projects/p/userFiles/a.M3U", ".bin")]
    [InlineData("projects/p/userFiles/a.ffconcat", ".bin")]
    [InlineData("projects/p/userFiles/a.txt", ".bin")]
    [InlineData("projects/p/userFiles/a.mpd", ".bin")]
    [InlineData("projects/p/userFiles/a.sdp", ".bin")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SafeExtension_keeps_only_a_media_extension_and_neutralises_the_rest(string? storageKey, string expected)
    {
        MediaDerivativeDispatcher.SafeExtension(storageKey).Should().Be(expected);
    }

    // =========================================================================================
    // Scratch bound: a run takes its slot before it downloads the source (security MI-1)
    // =========================================================================================

    [Fact]
    public async Task Concurrent_runs_never_download_more_sources_at_once_than_the_derivative_slot_cap()
    {
        string root = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(
            root,
            MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(root, "scratch"), derivativeMaxConcurrent: 1));

        // Not real media: once a download is released its probe fails fast, which ends that run.
        string junk = Path.Combine(root, "junk.mp4");
        File.WriteAllBytes(junk, Encoding.UTF8.GetBytes("not a video"));
        fixture.ServeAsSourceObject(junk);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Workspace.DownloadGate = gate.Task;

        Guid projectId = Guid.NewGuid();
        Task first = fixture.Dispatcher.RunAsync(
            fixture.Request(projectId, $"projects/{projectId}/userFiles/a.mp4", "\"a\"", []), CancellationToken.None);
        Task second = fixture.Dispatcher.RunAsync(
            fixture.Request(projectId, $"projects/{projectId}/userFiles/b.mp4", "\"b\"", []), CancellationToken.None);

        await WaitForAsync(() => fixture.Workspace.DownloadCount >= 1);
        await Task.Delay(300);

        fixture.Workspace.DownloadCount.Should().Be(
            1, "with one derivative slot, a second upload must queue before it pulls its whole source to scratch");

        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        fixture.Workspace.DownloadCount.Should().Be(2, "the queued run still downloads once the first releases the slot");
    }

    // =========================================================================================

    private static MediaDerivativeDispatchFixture NewFixture()
    {
        string root = MediaDerivativeFfmpegFixture.NewDirectory();
        return MediaDerivativeDispatchFixture.Create(root);
    }

    private static MediaDerivativesConsumer NewConsumer(MediaDerivativeDispatchFixture fixture) =>
        new(fixture.Db, fixture.Dispatcher, fixture.Sink, NullLogger<MediaDerivativesConsumer>.Instance);

    private static ConsumeContext<MediaDerivativesRequested> NewContext(MediaDerivativesRequested message)
    {
        var context = new Mock<ConsumeContext<MediaDerivativesRequested>>();
        context.SetupGet(c => c.Message).Returns(message);
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return context.Object;
    }

    private static void SeedProjectFile(
        MediaDerivativeDispatchFixture fixture, Guid fileId, Guid projectId, string storageKey)
    {
        using WorkflowEngineDbContext db = fixture.Db.CreateDbContext();
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = fileId,
            ProjectId = projectId,
            StorageKey = storageKey,
            OriginalFileName = "a.mp4",
            MimeType = "video/mp4",
            CsamStatus = CsamScanStatus.Clean
        });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(CsamScanStatus.Pending)]
    [InlineData(CsamScanStatus.Scanning)]
    public async Task A_request_for_a_file_with_no_csam_verdict_yet_writes_no_row_and_never_downloads(CsamScanStatus status)
    {
        using MediaDerivativeDispatchFixture fixture = NewFixture();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        string key = "projects/" + projectId.ToString("D") + "/userFiles/a.mp4";
        SeedProjectFile(fixture, fileId, projectId, key);
        using (WorkflowEngineDbContext db = fixture.Db.CreateDbContext())
        {
            db.ProjectFiles.Single(f => f.Id == fileId).CsamStatus = status;
            db.SaveChanges();
        }

        await NewConsumer(fixture).Consume(NewContext(fixture.Request(
            projectId, key, "\"etag-1\"", [MediaDerivativeKind.Proxy], sourceProjectFileId: fileId)));

        fixture.ReadRows().Should().BeEmpty();
        fixture.Workspace.DownloadCount.Should().Be(0, "no derivative is made before the CSAM verdict");
    }

    private static void SeedStepResult(
        MediaDerivativeDispatchFixture fixture, Guid stepResultId, Guid projectId, string outputStorageKey)
    {
        using WorkflowEngineDbContext db = fixture.Db.CreateDbContext();

        Guid executionId = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            WorkflowDefinitionId = Guid.NewGuid()
        });

        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = stepResultId,
            WorkflowExecutionId = executionId,
            WorkflowStepId = Guid.NewGuid(),
            OutputStorageKey = outputStorageKey
        });

        db.SaveChanges();
    }

    private static MediaDerivativeResult Result(MediaDerivativeSpec spec, string fileName, string contentType) =>
        new(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            MediaDerivativeCacheKey.ComputeCacheKey("projects/p/userFiles/a.mp4", "\"etag\"", spec),
            "projects/p/agentFiles/media-derivatives/hash",
            0,
            0,
            1.0,
            11,
            [new MediaDerivativeArtifact("/tmp/" + fileName, fileName, contentType)],
            SidecarJson: null);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeout.Token);
        }
    }
}
