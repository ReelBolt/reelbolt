using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// W0.4 (editor-v2 remediation): media is probed when its derivatives are made, a source with no
/// audio stream never fails a peaks decode, and the JSON documents the editor fetches carry no
/// byte-order mark. Every test here runs the real ffmpeg/ffprobe on a generated clip.
/// </summary>
public class MediaIngestProbeFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public MediaIngestProbeFfmpegTests(ITestOutputHelper output) => _output = output;

    [FfmpegToolsFact]
    public async Task A_derivative_run_stores_the_sources_probe_on_every_row_it_writes()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "clip.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, "\"etag-probe\"", [MediaDerivativeKind.Proxy, MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();
            rows.Should().HaveCount(2);

            foreach (MediaDerivative row in rows)
            {
                MediaSourceProbe? probe = MediaSourceProbe.TryParse(row.SourceProbeJson);
                probe.Should().NotBeNull($"the {row.Kind} row must carry the source's probe");
                probe!.DurationSec.Should().BeApproximately(2.0, 0.1);
                probe.FpsNum.Should().Be(25);
                probe.FpsDen.Should().Be(1);
                probe.Width.Should().Be(160);
                probe.Height.Should().Be(120);
                probe.VideoCodec.Should().Be("h264");
                probe.AudioCodec.Should().Be("aac");
                probe.AudioChannels.Should().Be(2);
                probe.HasAudio.Should().BeTrue();
            }

            _output.WriteLine("MEASURED stored probe: {0}", rows[0].SourceProbeJson);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_probe_is_stored_with_the_first_row_written_which_is_not_the_proxy()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "first.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, "\"etag-first\"", [MediaDerivativeKind.Proxy, MediaDerivativeKind.WaveformPeaks, MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();
            MediaDerivative first = rows.OrderBy(r => r.CreatedAt).First();
            first.Kind.Should().BeOneOf(MediaDerivativeKind.WaveformPeaks, MediaDerivativeKind.KeyframeIndex);
            MediaSourceProbe.TryParse(first.SourceProbeJson).Should().NotBeNull("a big clip's length must not wait for the proxy");
            rows.Should().Contain(r => r.Kind == MediaDerivativeKind.Proxy);
            rows.OrderBy(r => r.CreatedAt).ToList().FindIndex(r => r.Kind == MediaDerivativeKind.Proxy)
                .Should().BeGreaterThan(0, "the proxy row is written after the seconds-long kinds");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task An_output_hold_that_starts_while_the_run_waits_for_its_slot_skips_the_run_and_frees_the_slot()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "held.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            Guid fileId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";
            await using (WorkflowEngineDbContext db = fixture.Db.CreateDbContext())
            {
                db.Projects.Add(new Project { Id = projectId, Name = "p", OwnerId = Guid.NewGuid(), CsamOutputHoldAt = DateTime.UtcNow });
                db.ProjectFiles.Add(new ProjectFile
                {
                    Id = fileId,
                    ProjectId = projectId,
                    StorageKey = storageKey,
                    OriginalFileName = "held.mp4",
                    OutputProducedAt = DateTime.UtcNow.AddHours(-1),
                    UploadedAt = DateTime.UtcNow.AddHours(-1)
                });
                await db.SaveChangesAsync();
            }

            var kinds = new[] { MediaDerivativeKind.KeyframeIndex };
            await fixture.Dispatcher.RunAsync(fixture.Request(projectId, storageKey, "\"etag-held\"", kinds, fileId), CancellationToken.None);

            fixture.ReadRows().Should().BeEmpty("a held source is never downloaded or processed");
            fixture.Runner.FfmpegArgvs.Should().BeEmpty();
            fixture.Sink.Stages.Last().Should().Be(MediaDerivativeProgressStage.Failed);

            await using (WorkflowEngineDbContext db = fixture.Db.CreateDbContext())
            {
                db.Projects.Single(p => p.Id == projectId).CsamOutputHoldAt = null;
                await db.SaveChangesAsync();
            }

            await fixture.Dispatcher.RunAsync(fixture.Request(projectId, storageKey, "\"etag-held\"", kinds, fileId), CancellationToken.None);
            fixture.ReadRows().Should().ContainSingle("the slot was released, so the next run is not stuck behind the skipped one");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_silent_video_gets_no_peaks_and_the_run_does_not_fail()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "silent.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: false);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, "\"etag-silent\"", [MediaDerivativeKind.WaveformPeaks, MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();
            rows.Should().ContainSingle().Which.Kind.Should().Be(MediaDerivativeKind.KeyframeIndex);
            MediaSourceProbe.TryParse(rows[0].SourceProbeJson)!.HasAudio.Should().BeFalse();

            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed,
                "a source with no audio stream has no peaks to make; that is not a failure (RD-10)");
            fixture.Sink.Stages.Last().Should().Be(MediaDerivativeProgressStage.Done);
            fixture.Runner.FfmpegArgvs.Should().NotContain(args => args.Contains("s16le"),
                "the peaks decode must never be attempted on a source with no audio");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_repeat_request_that_is_all_cache_hits_ends_done_and_backfills_a_missing_probe()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "clip.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";
            const string etag = "\"etag-repeat\"";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, etag, [MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            // A row written before probes were stored: wipe the probe the first run wrote.
            using (WorkflowEngineDbContext db = fixture.Db.CreateDbContext())
            {
                foreach (MediaDerivative row in db.MediaDerivatives) row.SourceProbeJson = null;
                db.SaveChanges();
            }

            int stagesBefore = fixture.Sink.Stages.Count;

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, etag, [MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            fixture.Sink.Stages.Skip(stagesBefore).Last().Should().Be(MediaDerivativeProgressStage.Done,
                "everything already being made is the done state, not a skip (RD-14)");

            MediaDerivative only = fixture.ReadRows().Should().ContainSingle().Subject;
            MediaSourceProbe.TryParse(only.SourceProbeJson).Should().NotBeNull(
                "a cache-hit run still probes the source and fills the probe of a row that lacks one");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_json_documents_a_derivative_run_writes_carry_no_byte_order_mark()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "clip.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: true);

            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
            FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
            FfprobeMediaProbe probe = MediaDerivativeFfmpegFixture.BuildProbe(runner, options);
            MediaDerivativeService service = MediaDerivativeFfmpegFixture.BuildService(runner, probe, options);
            Guid projectId = Guid.NewGuid();
            string sourceKey = $"projects/{projectId:D}/userFiles/clip.mp4";

            MediaDerivativeSpec[] specs =
            [
                MediaDerivativeSpec.ForKeyframeIndex(),
                MediaDerivativeSpec.ForWaveformPeaks(0),
                MediaDerivativeSpec.ForFilmstrip(0)
            ];

            foreach (MediaDerivativeSpec spec in specs)
            {
                MediaDerivativeResult result = await service.ProduceAsync(
                    projectId, sourceKey, "etag-bom", spec, source, progress: null, CancellationToken.None);

                foreach (MediaDerivativeArtifact json in result.Artifacts.Where(a => a.ContentType == "application/json"))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(json.LocalPath);
                    bytes.Should().NotBeEmpty();
                    bytes[0].Should().Be((byte)'{', $"{spec.Slug} artifact {json.FileName} must start with the JSON itself, not a BOM");
                }
            }
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }
}
