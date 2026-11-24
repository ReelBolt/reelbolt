using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>
/// Golden step-cache keys recorded on the code BEFORE the output-resolution choice existed: a run
/// without a resolution must keep exactly these keys (no cache invalidation for existing runs).
/// The key also carries the VideoCompile AlgorithmRevision (DeterministicStepRevisions), so these
/// are re-recorded whenever that revision or StepCacheKeyInputs.SchemaVersion is bumped (last: the
/// run-brief-* revision tags of VideoCompile and VideoAnalyze, schema still v9; before that the
/// T-024 remediation took the schema to v9 and VideoAnalyze to +mask-scope-key-5; before that the
/// mask-gap-check-2 bump on VideoAnalyze and VideoCompile, so 2026-10-19.whole-shots-target-length-27
/// +prompted-region-effects-1+reframe-knots-28+mask-gap-check-2 and
/// 2026-10-17.insert-smoothing-content-sub-shots-6+prompted-region-effects-1+mask-gap-check-2; before
/// that, schema v8 with the prompted-region-effects VideoSegmentation fingerprint line, then the merged
/// VideoCompile revision 2026-10-19.whole-shots-target-length-27+prompted-region-effects-1
/// +reframe-knots-28, then the P1 and P2 compile revisions, merged P0+P1+P2; the new null config
/// members alone leave the keys unchanged).
/// </summary>
public class RunStyleCacheKeyGoldenTests
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static List<WorkflowStep> Steps() =>
    [
        new WorkflowStep { StepOrder = 1, StepType = StepType.VideoAnalyze },
        new WorkflowStep
        {
            StepOrder = 2,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(
                new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1),
                ConfigJson)
        }
    ];

    private static string Key(WorkflowExecution execution) =>
        new StepCacheKeyBuilder().Build(new StepCacheKeyInputs
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.VideoCompile,
            AgentType = AgentType.VideoStoryEditor,
            AgentDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            VideoCompileConfigJson = "{\"version\":1}",
            MaxIterations = 0,
            ResolvedInput = "in",
            ProjectFileFingerprint = "fp",
            ResolvedProviderFingerprint = "prov",
            UpstreamDigest = "up",
            RunStyle = RunStyleOverrides.CacheKeyComponent(execution, Steps())
        });

    private static WorkflowExecution Styled() => new()
    {
        StyleConfigJson = StyleJson.Serialize(new RunStyleSnapshot("s", null, null, new StyleConfig(Pacing: StylePacing.Fast)))
    };

    [Fact]
    public void A_run_with_no_style_format_or_resolution_keeps_its_recorded_key()
    {
        // Re-recorded on purpose in lane 3e: DeterministicStepRevisions.For(VideoCompile) gained +voiceover-speech-guard-1 (SchemaVersion unchanged; the key framing did not change).
        // Re-recorded on purpose again for +stream-copy-reencode-1: the VideoCompile revision moved, SchemaVersion v9 and the key framing did not.
        // And again for +vfr-sync-1.
        // And again for +follow-subject-1+reframe-visible-anchor-1 (VideoCompile revision tag only; these inputs carry no step config).
        // And again when StepCacheKeyInputs.SchemaVersion went v9 -> v10 (the audio-cleanup provider-fingerprint line): the schema version string is part of the hashed key. Recorded with both changes after the merge.
        // And again for +canvas-overlays-1 (VideoCompile revision tag only; docs/design/graphics-see-and-size.md). Prefix as the failure printed it.
        // And again for +region-mask-crop-geometry-1 (VideoCompile revision tag only).
        // And again when it went v10 -> v11 (the video-enhance VideoUpscale and FrameInterpolation provider-fingerprint lines): the version string moved and the resolved-provider fingerprint gained the new capability lines. Recorded after merging master.
        // And again for SchemaVersion v12 (the VideoOutpaint fingerprint line: the version string moved and the resolved-provider fingerprint gained one more capability line).
        Key(new WorkflowExecution()).Should().StartWith("bf411b6a136f4b53503b8c37eecf9e2fcf987de46bf1b2010c").And.HaveLength(64);
    }

    [Fact]
    public void A_styled_run_without_a_resolution_keeps_its_recorded_component_and_key()
    {
        RunStyleOverrides.CacheKeyComponent(Styled(), Steps()).Should().Be("style=17:{\"pacing\":\"Fast\"}|format=-|target=-");
        // Re-recorded on purpose in lane 3e: DeterministicStepRevisions.For(VideoCompile) gained +voiceover-speech-guard-1 (SchemaVersion unchanged; the key framing did not change).
        // Re-recorded on purpose again for +stream-copy-reencode-1: the VideoCompile revision moved, SchemaVersion v9 and the key framing did not.
        // And again for +vfr-sync-1.
        // And again for +follow-subject-1+reframe-visible-anchor-1 (VideoCompile revision tag only; these inputs carry no step config).
        // And again for SchemaVersion v10 (same reason as above: only the version string moved).
        // And again for +canvas-overlays-1 (VideoCompile revision tag only).
        // And again for +region-mask-crop-geometry-1 (VideoCompile revision tag only).
        // And again for SchemaVersion v11 (same reason).
        // And again for SchemaVersion v12 (the VideoOutpaint fingerprint line; only the version string moved).
        Key(Styled()).Should().StartWith("a944c8ee0c1732b6572d07f460de1879cce68399561ab6e226").And.HaveLength(64);
    }

}
