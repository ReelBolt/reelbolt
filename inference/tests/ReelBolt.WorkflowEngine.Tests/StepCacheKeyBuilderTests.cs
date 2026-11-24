using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for <see cref="StepCacheKeyBuilder"/>: determinism, sensitivity to every hashed field,
/// null/empty distinguishability, output shape, and the length-prefixed-framing collision
/// resistance the doc comment on <see cref="StepCacheKeyBuilder.Build"/> claims.
/// </summary>
public class StepCacheKeyBuilderTests
{
    private static StepCacheKeyInputs BaseInputs() => new()
    {
        ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StepType = StepType.Agent,
        AgentType = AgentType.VideoStoryEditor,
        AgentDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        AgentSystemPrompt = "You decide which shots to keep.",
        AgentOutputSchemaName = "VideoEditDecisionOutput",
        AgentInferenceProviderId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        AgentAssignedSkillsJson = "[\"remotion-markup\"]",
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        SelectedPriorStepOrdersJson = "[1,2]",
        InputMappingJson = "{\"a\":1}",
        ExtractConfigJson = "{\"op\":\"Project\"}",
        VideoAnalyzeConfigJson = "{\"source\":\"ProjectFile\"}",
        VideoCompileConfigJson = "{\"mode\":\"Reencode\"}",
        VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\"},\"model\":\"s2.1-pro-free\"}",
        EditRoomConfigJson = "{\"seats\":3}",
        GraphicsRoomConfigJson = "{\"seats\":2}",
        ColorGradeRoomConfigJson = "{\"seats\":2}",
        DailiesRoomConfigJson = "{\"rounds\":2}",
        ConditionExpression = "score >= 9",
        MaxIterations = 3,
        MinScore = 8,
        ResolvedInput = "[\"begin\"]",
        UserRequest = "make it punchy",
        ProjectFileFingerprint = "abc123",
        WorkflowDefinitionId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    };

    private static readonly IStepCacheKeyBuilder Builder = new StepCacheKeyBuilder();

    private static StepCacheKeyInputs AnalyzeInputs(string configJson) =>
        BaseInputs() with { StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = configJson };

    [Fact]
    public void A_Standard_VideoAnalyze_key_is_unchanged_by_the_scene_detection_revision()
    {
        Builder.Build(AnalyzeInputs("""{"sceneDetection":"Standard"}""")).Should().StartWith("2bb65ef94ede74b0e795260bb5a5daf99fa4d7b955681e998c", "pinned: this golden moves only with a deliberate VideoAnalyze revision bump (display-upright-9, hdr-tonemap-2, analysis-copy-3, events-1, outline-1, view-coverage-1, speech-outline-1, edit-moments-1); re-recorded on purpose for tap-seed-1 (earlier 8869f6f0...), for edit-moments-1 (earlier 72f94c6e...), for speech-outline-1 (earlier 9b285f2c...), for outline-1 (earlier 89c09703...) and events-1 (earlier fc66993a...); and for SchemaVersion v12 (earlier 78179f4b...; only the version string moved; v11 was earlier c5142871...)").And.HaveLength(64);
    }

    [Fact]
    public void A_Sensitive_VideoAnalyze_key_carries_the_sensitive_scenes_revision()
    {
        // 3c41bb84... was the key while the +sensitive-scenes-1 revision was never read by the builder.
        Builder.Build(AnalyzeInputs("""{"sceneDetection":"Sensitive"}""")).Should().NotBe("3c41bb84b1e598b151b187658fd1e781aeb77fc3ffbaa9a459c60459d81fbc57");
    }

    [Fact]
    public void Schema_version_is_v9()
    {
        // R10: A10 gained a resolved-provider fingerprint and bumped the schema to v4; D12 changed
        // step-cache ADMISSIBILITY only and did NOT bump it again. v5 adds the upstream digest (what a
        // step reads from earlier steps by step order — docs/design/composition-revisions.md). v6 adds
        // the rendered ambient design/product blocks a role consumes (P2-18). v7 adds what each StepResult
        // source RESOLVED to (T-023, S-17), so a pruned or deleted source misses; v8 is the
        // prompted-region-effects contracts: InferenceProviderCapability.VideoSegmentation joined the
        // fingerprint capability set, so the VALUE of an already-hashed field changed meaning. v9 is the
        // T-024 security remediation: the segmentation mask content key is segmented by ObjectMatchScope,
        // so an analyze cached before the fix could hold the other scope's track set under an unchanged
        // config; the row's meaning moved and it must orphan. v10 is the audio-cleanup vendor layer:
        // InferenceProviderCapability.AudioIsolation joined the fingerprint capability set (one more line in the
        // hashed fingerprint changes EVERY step's key), so every pre-v10 row must orphan. v11 is the video-enhance
        // vendor layer: VideoUpscale and FrameInterpolation joined the same set (two more lines), so every pre-v11
        // row must orphan.
        // Asserted as a literal, not against the constant, so a silent bump — which orphans every
        // existing cache row — has to be a deliberate edit to this test.
        StepCacheKeyInputs.SchemaVersion.Should().Be("v13");

        // v3's own components are still there and still hashed — the bump ADDS a component, it does
        // not replace the field set (R10: "do not reorder existing components").
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions.For(StepType.VideoAnalyze).Should().NotBeEmpty();

        // ...and the new component genuinely participates, which is what makes the bumped version
        // mean anything: with the schema hash alone unchanged, a fingerprint difference must move
        // the key.
        Builder.Build(BaseInputs() with { ResolvedProviderFingerprint = "aaa" })
            .Should().NotBe(Builder.Build(BaseInputs() with { ResolvedProviderFingerprint = "bbb" }));
        Builder.Build(BaseInputs() with { UpstreamDigest = "aaa" })
            .Should().NotBe(Builder.Build(BaseInputs() with { UpstreamDigest = "bbb" }));
        Builder.Build(BaseInputs() with { DesignSystem = "d=aaa|p=-" })
            .Should().NotBe(Builder.Build(BaseInputs() with { DesignSystem = "d=bbb|p=-" }));
    }

    [Fact]
    public void A_default_step_key_is_the_recorded_golden()
    {
        // The prompted-region-effects contracts claim a step that configures NO segmentation target
        // and NO region effects keeps exactly the cache key shape it had, and that the only two things
        // that may move its key are the ones this ticket changed on purpose: the schema version (now v12: VideoOutpaint joined the fingerprint after VideoUpscale and FrameInterpolation did at v11)
        // and the deterministic revision of the two video step types. This literal is that claim,
        // recorded: a silent drift in the hashed field set or its framing has to break this line.
        Builder.Build(BaseInputs()).Should().Be(
            "f5cc2843a6488ddfcca0d3b4c98e90f500a7fc4afddd0e0dcd9e7d35cad7c611");
    }

    [Fact]
    public void The_resolved_provider_fingerprint_is_hashed_including_when_it_is_null()
    {
        // Hashed unconditionally, unlike RunStyle: the v4 bump already orphaned every pre-v4 row,
        // so there is nothing left to preserve by omitting an absent fingerprint — and a step whose
        // resolver is unwired must still get a stable key of its own.
        string withoutFingerprint = Builder.Build(BaseInputs() with { ResolvedProviderFingerprint = null });
        string withFingerprint = Builder.Build(BaseInputs() with { ResolvedProviderFingerprint = "abc" });

        withoutFingerprint.Should().NotBe(withFingerprint);
        Builder.Build(BaseInputs() with { ResolvedProviderFingerprint = null }).Should().Be(withoutFingerprint);
    }

    [Fact]
    public void Build_is_deterministic_for_identical_inputs()
    {
        StepCacheKeyInputs inputs = BaseInputs();

        string key1 = Builder.Build(inputs);
        string key2 = Builder.Build(inputs with { });

        key1.Should().Be(key2);
    }

    [Fact]
    public void Build_returns_64_lowercase_hex_characters()
    {
        string key = Builder.Build(BaseInputs());

        key.Should().HaveLength(64);
        key.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    public static IEnumerable<object[]> FieldMutators()
    {
        yield return new object[] { "ProjectId", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { ProjectId = Guid.NewGuid() }) };
        yield return new object[] { "StepType", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { StepType = StepType.Extract }) };
        yield return new object[] { "AgentType", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { AgentType = AgentType.Colorist }) };
        yield return new object[] { "AgentDefinitionId", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { AgentDefinitionId = Guid.NewGuid() }) };
        yield return new object[] { "AgentSystemPrompt", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { AgentSystemPrompt = "different prompt" }) };
        yield return new object[] { "AgentOutputSchemaName", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { AgentOutputSchemaName = "SomethingElseOutput" }) };
        yield return new object[] { "AgentInferenceProviderId", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { AgentInferenceProviderId = Guid.NewGuid() }) };
        yield return new object[] { "AgentAssignedSkillsJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { AgentAssignedSkillsJson = "[\"different-skill\"]" }) };
        yield return new object[] { "AgentInputContextMode", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { AgentInputContextMode = AgentInputContextMode.FullWorkflow }) };
        yield return new object[] { "SelectedPriorStepOrdersJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { SelectedPriorStepOrdersJson = "[9]" }) };
        yield return new object[] { "InputMappingJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { InputMappingJson = "{\"b\":2}" }) };
        yield return new object[] { "ExtractConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { ExtractConfigJson = "{\"op\":\"Resolve\"}" }) };
        yield return new object[] { "VideoAnalyzeConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { VideoAnalyzeConfigJson = "{\"source\":\"StepOutput\"}" }) };
        yield return new object[] { "VideoCompileConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { VideoCompileConfigJson = "{\"mode\":\"StreamCopy\"}" }) };
        yield return new object[] { "VideoGenerateConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { VideoGenerateConfigJson = "{\"prompt\":\"a red fox at dusk\",\"takes\":1}" }) };
        yield return new object[] { "VoiceoverConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\"},\"model\":\"s1\"}" }) };
        yield return new object[] { "EditRoomConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { EditRoomConfigJson = "{\"seats\":5}" }) };
        yield return new object[] { "GraphicsRoomConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { GraphicsRoomConfigJson = "{\"seats\":5}" }) };
        yield return new object[] { "ColorGradeRoomConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { ColorGradeRoomConfigJson = "{\"seats\":5}" }) };
        yield return new object[] { "DailiesRoomConfigJson", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { DailiesRoomConfigJson = "{\"rounds\":3}" }) };
        yield return new object[] { "ConditionExpression", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { ConditionExpression = "score >= 5" }) };
        yield return new object[] { "MaxIterations", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { MaxIterations = 7 }) };
        yield return new object[] { "MinScore", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { MinScore = 2 }) };
        yield return new object[] { "ResolvedInput", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { ResolvedInput = "[\"different\"]" }) };
        yield return new object[] { "UserRequest", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { UserRequest = "make it slower" }) };
        yield return new object[] { "ProjectFileFingerprint", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { ProjectFileFingerprint = "def456" }) };
        yield return new object[] { "ResolvedProviderFingerprint", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { ResolvedProviderFingerprint = "fedcba" }) };
        yield return new object[] { "DesignSystem", (Func<StepCacheKeyInputs, StepCacheKeyInputs>)(i => i with { DesignSystem = "d=0123456789abcdef|p=fedcba9876543210" }) };
    }

    [Theory]
    [MemberData(nameof(FieldMutators))]
    public void Build_changes_when_a_single_hashed_field_changes(string fieldName, Func<StepCacheKeyInputs, StepCacheKeyInputs> mutate)
    {
        StepCacheKeyInputs original = BaseInputs();
        StepCacheKeyInputs mutated = mutate(original);

        string originalKey = Builder.Build(original);
        string mutatedKey = Builder.Build(mutated);

        mutatedKey.Should().NotBe(originalKey, because: $"changing {fieldName} must change the cache key");
    }

    [Fact]
    public void Two_VideoGenerate_steps_differing_only_in_VideoGenerateConfigJson_get_different_keys()
    {
        StepCacheKeyInputs baseline = BaseInputs() with
        {
            StepType = StepType.VideoGenerate,
            AgentType = AgentType.VideoTransform,
            VideoGenerateConfigJson = "{\"prompt\":\"a red fox at dusk\",\"model\":\"m1\",\"durationSeconds\":6,\"takes\":1}"
        };
        StepCacheKeyInputs otherPrompt = baseline with
        {
            VideoGenerateConfigJson = "{\"prompt\":\"a red fox at dawn\",\"model\":\"m1\",\"durationSeconds\":6,\"takes\":1}"
        };

        Builder.Build(otherPrompt).Should().NotBe(Builder.Build(baseline));
    }

    /// <summary>
    /// Plan §7 item 3, asserted on the REAL config type rather than on a hand-written JSON string:
    /// the derush pre-pass makes a <c>VideoAnalyze</c> step's output depend on
    /// <c>DerushPrePass</c>/<c>DerushAcceptAt</c>/<c>DerushMinMargin</c>, so a cached derush output
    /// must not be reusable under different thresholds. It is covered without a new key field
    /// because every step config blob is already a key component (see
    /// <see cref="StepCacheKeyBuilder"/>); this proves those fields actually land in that blob.
    /// </summary>
    [Fact]
    public void Derush_thresholds_reach_the_cache_key_through_the_real_VideoAnalyze_config()
    {
        // The same options the executor deserializes its config with
        // (VideoAnalyzeStepExecutor.ConfigJsonOptions): web defaults plus string enum values.
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        var baseline = new VideoAnalyzeStepConfig(
            Version: 1, Source: new VideoSourceRef(VideoSourceKind.ProjectFile))
        with
        {
            DerushPrePass = VideoDerushPrePassMode.Prior
        };

        string KeyFor(VideoAnalyzeStepConfig config) => Builder.Build(BaseInputs() with
        {
            StepType = StepType.VideoAnalyze,
            AgentType = AgentType.VideoTransform,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, options)
        });

        string baselineKey = KeyFor(baseline);

        // Each field independently. A key component that omitted any one of them would silently
        // reuse a cached derush view under different thresholds — the exact failure §7 item 3 names.
        KeyFor(baseline with { DerushAcceptAt = 0.5 })
            .Should().NotBe(baselineKey, "DerushAcceptAt must be in the key");
        KeyFor(baseline with { DerushMinMargin = 0.05 })
            .Should().NotBe(baselineKey, "DerushMinMargin must be in the key");
        KeyFor(baseline with { DerushPrePass = VideoDerushPrePassMode.Fast })
            .Should().NotBe(baselineKey, "DerushPrePass must be in the key");
        KeyFor(baseline with { ScreenInstructionLikeSpans = true })
            .Should().NotBe(baselineKey, "ScreenInstructionLikeSpans must be in the key");

        // ...and still deterministic for an unchanged config.
        KeyFor(baseline).Should().Be(baselineKey);
    }

    [Fact]
    public void Two_Voiceover_steps_differing_only_in_VoiceoverConfigJson_get_different_keys()
    {
        // "Two distinct Voiceover steps in one project must not hash identically." A Voiceover
        // step's config carries EVERYTHING that decides its output (source kind + lines, provider,
        // model, default voice, byte budget), so omitting it from the hash let two genuinely
        // different narration steps collide on one cache entry — the second step would replay the
        // first step's lines, and its per-line storage keys, as if they were its own.
        StepCacheKeyInputs baseline = BaseInputs() with
        {
            StepType = StepType.Voiceover,
            AgentType = AgentType.VideoTransform,
            VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"Buy now\"}]},\"model\":\"s2.1-pro-free\",\"defaultVoiceId\":\"v1\"}"
        };
        StepCacheKeyInputs otherLines = baseline with
        {
            VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"Learn more\"}]},\"model\":\"s2.1-pro-free\",\"defaultVoiceId\":\"v1\"}"
        };

        Builder.Build(otherLines).Should().NotBe(Builder.Build(baseline));
    }

    [Fact]
    public void Voiceover_config_with_pronunciation_overrides_gets_a_different_key()
    {
        StepCacheKeyInputs without = BaseInputs() with
        {
            StepType = StepType.Voiceover,
            AgentType = AgentType.VideoTransform,
            VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\"}}"
        };
        StepCacheKeyInputs with_ = without with
        {
            VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\"},\"pronunciationOverrides\":[{\"term\":\"perché\",\"ipa\":\"perˈke\"}]}"
        };

        Builder.Build(with_).Should().NotBe(Builder.Build(without));
    }

    [Fact]
    public void Two_Voiceover_steps_identical_in_every_field_get_the_same_key()
    {
        // The other half of the same requirement: sensitivity to VoiceoverConfigJson must not come
        // at the cost of determinism — two step configs that are byte-identical are the same work
        // and must still share one cache entry (that is the whole reason Voiceover stays cacheable
        // rather than being dropped from StepCachePolicy).
        StepCacheKeyInputs first = BaseInputs() with
        {
            StepType = StepType.Voiceover,
            AgentType = AgentType.VideoTransform,
            VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"Buy now\"}]},\"model\":\"s2.1-pro-free\",\"defaultVoiceId\":\"v1\"}"
        };
        StepCacheKeyInputs second = BaseInputs() with
        {
            StepType = StepType.Voiceover,
            AgentType = AgentType.VideoTransform,
            VoiceoverConfigJson = "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"Buy now\"}]},\"model\":\"s2.1-pro-free\",\"defaultVoiceId\":\"v1\"}"
        };

        Builder.Build(second).Should().Be(Builder.Build(first));
    }

    [Fact]
    public void WorkflowDefinitionId_is_deliberately_excluded_from_the_hash()
    {
        // See StepCacheKeyInputs' own doc comment: WorkflowDefinitionId is metadata-only, carried
        // for WorkflowStepCacheEntry population, NOT part of the content that determines whether
        // two executions would produce the same output — a cache entry must survive an edit to the
        // workflow definition it was produced under.
        StepCacheKeyInputs original = BaseInputs();
        StepCacheKeyInputs mutated = original with { WorkflowDefinitionId = Guid.NewGuid() };

        Builder.Build(mutated).Should().Be(Builder.Build(original));
    }

    [Fact]
    public void Null_and_empty_string_are_distinguishable()
    {
        StepCacheKeyInputs withNull = BaseInputs() with { UserRequest = null };
        StepCacheKeyInputs withEmpty = BaseInputs() with { UserRequest = string.Empty };

        Builder.Build(withNull).Should().NotBe(Builder.Build(withEmpty));
    }

    [Fact]
    public void Two_different_null_optional_fields_still_produce_the_same_key_as_each_other()
    {
        StepCacheKeyInputs a = BaseInputs() with { UserRequest = null, MinScore = null };
        StepCacheKeyInputs b = BaseInputs() with { UserRequest = null, MinScore = null };

        Builder.Build(a).Should().Be(Builder.Build(b));
    }

    [Fact]
    public void Length_prefixed_framing_prevents_concatenation_collisions_across_adjacent_fields()
    {
        // The exact shape the doc comment on StepCacheKeyBuilder.Build calls out: {AgentSystemPrompt="ab", AgentOutputSchemaName="c"}
        // vs {AgentSystemPrompt="a", AgentOutputSchemaName="bc"} would hash identically under naive
        // newline-joined concatenation (both flatten to "...ab" + "c..." vs "...a" + "bc..." = the
        // same bytes once the field NAMES are fixed and only these two adjacent values move mass
        // between them) unless each value's own length is also part of the framing.
        StepCacheKeyInputs a = BaseInputs() with { AgentSystemPrompt = "ab", AgentOutputSchemaName = "c" };
        StepCacheKeyInputs b = BaseInputs() with { AgentSystemPrompt = "a", AgentOutputSchemaName = "bc" };

        Builder.Build(a).Should().NotBe(Builder.Build(b));
    }

    [Fact]
    public void Length_prefixed_framing_prevents_concatenation_collisions_via_embedded_separators()
    {
        // A value containing the field separator itself (a real system prompt or JSON config can
        // easily contain '=' or '\n') must not be able to forge a different field split.
        StepCacheKeyInputs a = BaseInputs() with { AgentSystemPrompt = "x\nAgentOutputSchemaName=5:hijack", AgentOutputSchemaName = "real" };
        StepCacheKeyInputs b = BaseInputs() with { AgentSystemPrompt = "x", AgentOutputSchemaName = "real" };

        Builder.Build(a).Should().NotBe(Builder.Build(b));
    }

    [Fact]
    public void Deterministic_step_types_hash_their_algorithm_revision()
    {
        // A fixed executor must not keep replaying its pre-fix output from cache.
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions.For(StepType.VideoAnalyze).Should().NotBeEmpty();
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions.For(StepType.VideoCompile).Should().NotBeEmpty();
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions.For(StepType.Agent).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"view\":{},\"meta\":{\"objectTracking\":{\"applied\":true,\"degraded\":true}}}", true)]
    [InlineData("{\"view\":{},\"meta\":{\"vision\":{\"degraded\":false}}}", false)]
    [InlineData("{\"view\":{},\"meta\":{\"transcription\":{\"degraded\":true}}}", false)]
    [InlineData("{\"view\":{},\"meta\":{\"events\":{\"count\":3,\"degraded\":true}}}", true)]
    [InlineData("{\"view\":{},\"meta\":{\"events\":{\"count\":3,\"suppressed\":0}}}", false)]
    [InlineData("{\"keep\":[]}", false)]
    [InlineData("not json \"degraded\"", false)]
    public void Degraded_outputs_are_never_cached(string output, bool degraded) =>
        ReelBolt.WorkflowEngine.Execution.Caching.StepResultCache.ReportsDegradation(output).Should().Be(degraded);
}
