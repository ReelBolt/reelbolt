using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video.Quality;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public sealed class RenderLoudnessChecksTests
{
    [Fact]
    public void Silence_args_are_a_separate_audio_only_null_pass()
    {
        IReadOnlyList<string> args = AudioMixBuilder.BuildSilenceArgs("/tmp/mix.wav");
        args.Should().Contain("silencedetect=noise=-50dB:d=2").And.Contain("-vn").And.Contain("null");
    }

    [Fact]
    public void Measure_args_stay_unchanged()
    {
        AudioMixBuilder.BuildMeasureArgs("/m.wav", new ReelBolt.Shared.Workflows.TimelineRenderLoudness())
            .Should().Contain("loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json");
    }

    [Fact]
    public void Silences_parse_and_an_open_one_runs_to_the_end()
    {
        const string err = "[silencedetect @ 0x1] silence_start: 1.5\n[silencedetect @ 0x1] silence_end: 5.25 | silence_duration: 3.75\n[silencedetect @ 0x1] silence_start: 8";
        AudioMixBuilder.ParseSilences(err, 10).Should().Equal([(1.5, 5.25), (8.0, 10.0)]);
        AudioMixBuilder.ParseSilences("", 10).Should().BeEmpty();
    }

    [Fact]
    public void Silences_are_bounded()
    {
        string err = string.Concat(Enumerable.Range(0, 200).Select(i => $"silence_start: {i * 10}\nsilence_end: {i * 10 + 3}\n"));
        AudioMixBuilder.ParseSilences(err, 5000).Count.Should().Be(AudioMixBuilder.MaxSilences);
    }

    [Fact]
    public void Executor_node_lists_findings_additively_and_empty_when_clean()
    {
        TimelineRenderStepExecutor.LoudnessChecksNode([]).Count.Should().Be(0);
        var node = TimelineRenderStepExecutor.LoudnessChecksNode(
            [new QualityFinding(QualityKinds.Clipping, QualitySeverity.High, null, null, 0.4, 0, "peaks")]);
        node.ToJsonString().Should().Be("""[{"kind":"clipping","severity":"high","measured":0.4,"limit":0,"detail":"peaks"}]""");
    }

    [Fact]
    public void TimelineRender_has_a_revision_tag()
    {
        DeterministicStepRevisions.For(StepType.TimelineRender).Should().Be("2026-11-10.loudness-checks-1");
    }

    [Fact]
    public void Controller_reads_checks_defensively()
    {
        const string json = """
        {"loudness":{"integratedLufs":-20,"truePeakDbtp":0.5,"loudnessRange":3,"normalised":false,"checks":[
          {"kind":"long_silence","severity":"medium","startSec":3,"endSec":9,"measured":6,"limit":3,"detail":"x"},
          {"kind":"bogus","severity":"high","detail":"dropped"},
          {"kind":"clipping","severity":"urgent","detail":"dropped"},
          "junk"]}}
        """;
        EditTimelineRenderLoudnessResponse r = EditTimelinesController.ReadRenderLoudness(json)!;
        r.Checks.Should().ContainSingle().Which.Should().Be(new EditTimelineRenderQualityCheck("long_silence", "medium", 3, 9, 6, 3, "x"));
        JsonSerializer.Serialize(r.Checks[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Be("""{"kind":"long_silence","severity":"medium","startSec":3,"endSec":9,"measured":6,"limit":3,"detail":"x"}""");
    }

    [Fact]
    public void Old_stored_outputs_without_checks_read_as_empty()
    {
        EditTimelinesController.ReadRenderLoudness("""{"loudness":{"integratedLufs":-16,"truePeakDbtp":-2,"loudnessRange":4,"normalised":true}}""")!
            .Checks.Should().BeEmpty();
    }
}
