using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Voiceover source-speech guard: OverMusic over a source that already speaks.</summary>
public partial class VideoCompileStepExecutorTests
{
    private static VideoAnalysisArtifact GuardArtifact(double speechRatio, bool camera, bool readable = true)
    {
        VideoAnalysisArtifact a = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) }, offeredIds: new[] { "s0", "s1" });
        if (!readable)
            return a;
        return a with
        {
            Shots = a.Shots.Select(s => s with { Audio = new VideoAnalysisShotAudio(-25, -10, speechRatio, "Medium") }).ToList(),
            Sources = [new VideoAnalysisSourceInfo(0, SourceVideoKey, a.Media, ProjectFileId: camera ? Guid.NewGuid() : null)]
        };
    }

    private static async Task<(string Filter, string[] Codes, JsonElement Output)> RunGuard(
        VideoAnalysisArtifact artifact, Func<VideoCompileStepConfig, VideoCompileStepConfig>? cfg = null)
    {
        StepExecutionContext context = CreateVoiceoverContext(
            artifact, BuildDecisionJson(("s0", "s1", "keep")), out Mock<IProjectFileWorkspace> workspace, cfg);
        IReadOnlyList<string> args = null!;
        StepExecutionResult result = await CreateExecutor(workspace, ffmpegArgsCaptured: a => args ??= a).ExecuteAsync(context);
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output!);
        JsonElement root = doc.RootElement.Clone();
        string[] codes = root.TryGetProperty("warnings", out JsonElement w)
            ? w.EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToArray()
            : [];
        return (string.Join("\n", args!.Where(x => !x.Contains("/tmp") && !x.Contains("reelbolt-video"))), codes, root);
    }

    [Fact]
    public async Task Guard_narrated_film_drops_source_sound_exactly_like_MuteSourceAudio_and_warns()
    {
        var baseline = await RunGuard(GuardArtifact(0, camera: false, readable: false), c => c with { MuteSourceAudio = true });
        var guarded = await RunGuard(GuardArtifact(1.0, camera: false));
        guarded.Filter.Should().Be(baseline.Filter);
        guarded.Codes.Should().Contain("voiceover_source_narration_muted");
    }

    [Fact]
    public async Task Guard_speech_on_camera_footage_only_warns_and_leaves_argv_untouched()
    {
        var baseline = await RunGuard(GuardArtifact(0, camera: true, readable: false));
        var guarded = await RunGuard(GuardArtifact(1.0, camera: true));
        guarded.Filter.Should().Be(baseline.Filter);
        guarded.Codes.Should().Contain("voiceover_source_speech_under_lines");
        guarded.Codes.Should().NotContain("voiceover_source_narration_muted");
    }

    [Fact]
    public async Task Guard_source_without_speech_changes_nothing()
    {
        var baseline = await RunGuard(GuardArtifact(0, camera: false, readable: false));
        var guarded = await RunGuard(GuardArtifact(0.0, camera: false));
        guarded.Filter.Should().Be(baseline.Filter);
        guarded.Codes.Should().NotContain(c => c.StartsWith("voiceover_source_"));
        guarded.Output.GetProperty("voiceover").TryGetProperty("sourceSpeechGuard", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Guard_unreadable_speech_facts_change_nothing()
    {
        var baseline = await RunGuard(GuardArtifact(0, camera: false, readable: false));
        var guarded = await RunGuard(GuardArtifact(0, camera: false, readable: false));
        guarded.Filter.Should().Be(baseline.Filter);
        guarded.Codes.Should().NotContain(c => c.StartsWith("voiceover_source_"));
    }

    [Theory]
    [InlineData("duck")]
    [InlineData("replace")]
    [InlineData("mute")]
    public async Task Guard_never_fires_when_the_mode_or_mute_is_explicit(string which)
    {
        Func<VideoCompileStepConfig, VideoCompileStepConfig> cfg = which switch
        {
            "duck" => c => c with { VoiceoverMode = VoiceoverMode.DuckDialogue },
            "replace" => c => c with { VoiceoverMode = VoiceoverMode.ReplaceDialogue },
            _ => c => c with { MuteSourceAudio = true }
        };
        var baseline = await RunGuard(GuardArtifact(0, camera: false, readable: false), cfg);
        var guarded = await RunGuard(GuardArtifact(1.0, camera: false), cfg);
        guarded.Filter.Should().Be(baseline.Filter);
        guarded.Codes.Should().NotContain(c => c.StartsWith("voiceover_source_"));
    }
}
