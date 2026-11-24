using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Shared;

public class StepConfigSchemaExporterTests
{
    [Fact]
    public void GetSchema_VideoAnalyze_HasConfigTrue()
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();

        // Act
        var result = exporter.GetSchema(StepType.VideoAnalyze);

        // Assert
        Assert.True(result.HasConfig);
        Assert.Equal("VideoAnalyzeStepConfig", result.ConfigTypeName);
        Assert.NotNull(result.Schema);
        Assert.True(result.Schema.AsObject().ContainsKey("properties"));
        var properties = result.Schema["properties"]?.AsObject();
        Assert.NotNull(properties);
        // Check for camelCase property name since JsonNamingPolicy.CamelCase is used
        Assert.True(properties.ContainsKey("analyzeVisuals"));
    }

    [Fact]
    public void GetSchema_VideoGenerate_ExportsSpendDurationResolutionRatioAndTakes()
    {
        var exporter = new StepConfigSchemaExporter();

        var result = exporter.GetSchema(StepType.VideoGenerate);

        Assert.True(result.HasConfig);
        Assert.Equal("VideoGenerateStepConfig", result.ConfigTypeName);
        Assert.NotNull(result.Schema);
        var properties = result.Schema["properties"]?.AsObject();
        Assert.NotNull(properties);
        foreach (var name in new[] { "maxSpendCredits", "durationSeconds", "resolution", "ratio", "takes" })
        {
            Assert.True(properties.ContainsKey(name), $"VideoGenerate schema is missing '{name}'");
        }
    }

    [Fact]
    public void GetSchema_ForEach_HasConfigFalse()
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();

        // Act
        var result = exporter.GetSchema(StepType.ForEach);

        // Assert
        Assert.False(result.HasConfig);
        Assert.Null(result.ConfigTypeName);
        Assert.Null(result.Schema);
    }

    [Fact]
    public void GetAllSchemas_ReturnsSchemaForEveryStepType()
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();

        // Act
        var result = exporter.GetAllSchemas();

        // Assert
        var stepTypeValues = Enum.GetValues<StepType>();
        Assert.Equal(stepTypeValues.Length, result.Count);

        foreach (var stepType in stepTypeValues)
        {
            Assert.True(result.ContainsKey(stepType.ToString()));
        }
    }

    [Theory]
    [InlineData(StepType.Agent)]
    [InlineData(StepType.ForEach)]
    [InlineData(StepType.Parallel)]
    public void GetSchema_StepTypesWithoutConfig_DoNotThrow(StepType stepType)
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();

        // Act & Assert - should not throw
        var result = exporter.GetSchema(stepType);
        Assert.False(result.HasConfig);
        Assert.Null(result.Schema);
    }

    [Theory]
    [InlineData(StepType.Extract)]
    [InlineData(StepType.VideoAnalyze)]
    [InlineData(StepType.VideoCompile)]
    [InlineData(StepType.EditRoom)]
    [InlineData(StepType.GraphicsRoom)]
    [InlineData(StepType.ColorGradeRoom)]
    [InlineData(StepType.DailiesRoom)]
    [InlineData(StepType.ReviewLoop)]
    [InlineData(StepType.DesignSystem)]
    public void GetSchema_StepTypesWithConfig_DoNotThrow(StepType stepType)
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();

        // Act & Assert - should not throw
        var result = exporter.GetSchema(stepType);
        Assert.True(result.HasConfig);
        Assert.NotNull(result.Schema);
    }

    // -- QA-2 (T-207), CLOSED on the assistant/MCP path. ------------------------------------------
    //
    // VideoDerushPrePassMode.Fast and RoomDecisionMode.Adaptive behave EXACTLY like Prior and
    // Questions respectively, so an assistant reading the step schema must be told so rather than
    // inferring a capability from the member's name. The caveat lives on the properties as
    // [Description]; StepConfigSchemaExporter now lifts it onto the schema's `description` keyword,
    // because System.Text.Json's JsonSchemaExporter does not do that by itself — it needs
    // JsonSchemaExporterOptions.TransformSchemaNode, which is what the exporter now supplies.
    //
    // T-207's tripwire — GetSchema_DoesNotYetEmitTheCaveat_BecauseTheExporterHasNoDescriptionHook,
    // which asserted the caveat was ABSENT and was written to fail the day the hook landed — has
    // been inverted as its own instructions required. The two attribute pins it sat next to are kept
    // (they pin the caveat TEXT at the source), and the absence assertion is replaced by: the caveat
    // reaching GetSchema for all four step types; proof the hook is scoped to annotated properties
    // only; and proof a step config with no [Description] emits a byte-identical schema.

    [Fact]
    public void DerushPrePass_CarriesACaveat_ThatFastIsDeclaredButNotImplemented()
    {
        string caveat = AssertSingleDescriptionAttribute<VideoAnalyzeStepConfig>("DerushPrePass");

        Assert.Contains("Fast", caveat);
        Assert.Contains("NOT IMPLEMENTED", caveat);
        Assert.Contains("Prior", caveat);
    }

    [Theory]
    [InlineData(typeof(EditRoomStepConfig))]
    [InlineData(typeof(GraphicsRoomStepConfig))]
    [InlineData(typeof(ColorGradeRoomStepConfig))]
    [InlineData(typeof(DailiesRoomStepConfig))]
    public void DecisionScheduling_CarriesACaveat_ThatAdaptiveIsDeclaredButNotImplemented(Type roomConfigType)
    {
        // Asserted per CONCRETE record, not just on IRoomStepConfig: the exporter builds a schema
        // from the concrete type's own properties, so an attribute that lived only on the interface
        // would never be visible to it.
        string caveat = AssertSingleDescriptionAttribute(roomConfigType, "DecisionScheduling");

        Assert.Contains("Adaptive", caveat);
        Assert.Contains("NOT IMPLEMENTED", caveat);
        Assert.Contains("Questions", caveat);
    }

    [Theory]
    [InlineData(StepType.VideoAnalyze, typeof(VideoAnalyzeStepConfig), "DerushPrePass", "derushPrePass", "Fast", "Prior")]
    [InlineData(StepType.EditRoom, typeof(EditRoomStepConfig), "DecisionScheduling", "decisionScheduling", "Adaptive", "Questions")]
    [InlineData(StepType.GraphicsRoom, typeof(GraphicsRoomStepConfig), "DecisionScheduling", "decisionScheduling", "Adaptive", "Questions")]
    [InlineData(StepType.ColorGradeRoom, typeof(ColorGradeRoomStepConfig), "DecisionScheduling", "decisionScheduling", "Adaptive", "Questions")]
    [InlineData(StepType.DailiesRoom, typeof(DailiesRoomStepConfig), "DecisionScheduling", "decisionScheduling", "Adaptive", "Questions")]
    public void GetSchema_EmitsTheCaveat_ForEveryDeclaredButNotImplementedWaveTwoMember(
        StepType stepType,
        Type configType,
        string clrPropertyName,
        string jsonPropertyName,
        string unimplementedMember,
        string implementedTwin)
    {
        // The positive half of what the tripwire used to assert as absent: the caveat the media
        // department wrote as [Description] now reaches the payload ListStepSchemas serves.
        var exporter = new StepConfigSchemaExporter();

        StepConfigSchemaResult result = exporter.GetSchema(stepType);

        Assert.True(result.HasConfig);
        Assert.NotNull(result.Schema);

        string description = AssertDescriptionOfProperty(result.Schema!, jsonPropertyName);
        string caveat = AssertSingleDescriptionAttribute(configType, clrPropertyName);

        // The emitted text IS the attribute's text — the C# doc comment is the single source of
        // truth, so the schema can never drift into saying something the record does not.
        Assert.Equal(caveat, description);
        Assert.Contains(unimplementedMember, description);
        Assert.Contains("NOT IMPLEMENTED", description);
        Assert.Contains(implementedTwin, description);
        Assert.Contains("reserved for a later phase", description);
    }

    [Fact]
    public void GetSchema_EmitsADescription_OnlyForPropertiesCarryingADescriptionAttribute()
    {
        // The scope constraint, stated exhaustively: across EVERY step config, the ONLY schema
        // nodes carrying a `description` keyword are the annotated properties (plus VideoGenerate's generated resolution note). Everything else
        // is untouched by the hook — this is the assertion that the shared surface the workflow
        // builder consumes did not move.
        var exporter = new StepConfigSchemaExporter();
        var describedNodes = new List<string>();

        foreach (StepType stepType in Enum.GetValues<StepType>())
        {
            StepConfigSchemaResult result = exporter.GetSchema(stepType);
            if (result.Schema is null)
            {
                continue;
            }

            CollectDescriptionPaths(result.Schema, $"{stepType}:$", describedNodes);
        }

        // The set is exhaustive, so it is also the record of WHICH properties are self-documenting.
        // The six VideoAnalyze segmentation knobs and VideoCompile's regionEffects arrived with the
        // prompted-region-effects contracts; they are listed here (not filtered out as noise) because
        // this assertion is the negative half of the hook's contract — anything NOT in the list must
        // emit no description — and the positive half per field is
        // WorkflowTemplateRegionEffectTests.The_new_fields_descriptions_reach_the_assistant_schema.
        Assert.Equal(
            new[]
            {
                // AudioCleanup: every property documents itself for ListStepSchemas (the step has no builder UI yet).
                "AudioCleanup:$.properties.allowSourceMediaEgress.description",
                "AudioCleanup:$.properties.maxSourceMinutes.description",
                "AudioCleanup:$.properties.outputKind.description",
                "AudioCleanup:$.properties.providerId.description",
                "AudioCleanup:$.properties.registerProjectFile.description",
                "AudioCleanup:$.properties.source.description",
                "AudioCleanup:$.properties.target.description",
                // T-108: every AudioGenerate property documents itself for ListStepSchemas (the step has no builder UI yet).
                "AudioGenerate:$.properties.compositionPlanJson.description",
                "AudioGenerate:$.properties.durationSec.description",
                "AudioGenerate:$.properties.fileNamePrefix.description",
                "AudioGenerate:$.properties.instrumental.description",
                "AudioGenerate:$.properties.kind.description",
                "AudioGenerate:$.properties.loop.description",
                "AudioGenerate:$.properties.maxSpendCredits.description",
                "AudioGenerate:$.properties.model.description",
                "AudioGenerate:$.properties.prompt.description",
                "AudioGenerate:$.properties.promptInfluence.description",
                "AudioGenerate:$.properties.providerId.description",
                "AudioGenerate:$.properties.registerProjectFile.description",
                "AudioGenerate:$.properties.takes.description",
                "ColorGradeRoom:$.properties.decisionScheduling.description",
                "DailiesRoom:$.properties.decisionScheduling.description",
                "EditRoom:$.properties.decisionScheduling.description",
                "GraphicsRoom:$.properties.decisionScheduling.description",
                // ImageGenerate: every property documents itself for ListStepSchemas.
                "ImageGenerate:$.properties.allowSourceMediaEgress.description",
                "ImageGenerate:$.properties.aspectRatio.description",
                "ImageGenerate:$.properties.fileNamePrefix.description",
                "ImageGenerate:$.properties.height.description",
                "ImageGenerate:$.properties.instruction.description",
                "ImageGenerate:$.properties.itemList.description",
                "ImageGenerate:$.properties.maxSpendCredits.description",
                "ImageGenerate:$.properties.mode.description",
                "ImageGenerate:$.properties.outputHeight.description",
                "ImageGenerate:$.properties.outputWidth.description",
                "ImageGenerate:$.properties.prompt.description",
                "ImageGenerate:$.properties.providerId.description",
                "ImageGenerate:$.properties.registerProjectFile.description",
                "ImageGenerate:$.properties.resolution.description",
                "ImageGenerate:$.properties.source.description",
                "ImageGenerate:$.properties.variations.description",
                "ImageGenerate:$.properties.width.description",
                "VideoAnalyze:$.properties.allowSourceMediaEgress.description",
                "VideoAnalyze:$.properties.derushPrePass.description",
                "VideoAnalyze:$.properties.maxSegmentationObjects.description",
                "VideoAnalyze:$.properties.maxSegmentationSeconds.description",
                "VideoAnalyze:$.properties.segmentationDetectionThreshold.description",
                "VideoAnalyze:$.properties.segmentationProviderId.description",
                "VideoAnalyze:$.properties.segmentationTimeoutSeconds.description",
                "VideoCompile:$.properties.captionSource.description",
                "VideoCompile:$.properties.captionTranslationStepOrder.description",
                "VideoCompile:$.properties.cutOnEvents.description",
                "VideoCompile:$.properties.dynamics.description",
                "VideoCompile:$.properties.dynamicsPlan.description",
                "VideoCompile:$.properties.enableMotion.description",
                "VideoCompile:$.properties.eventMoves.description",
                "VideoCompile:$.properties.flashCues.description",

                "VideoCompile:$.properties.followSubject.description",
                "VideoCompile:$.properties.maxMotionCues.description",
                "VideoCompile:$.properties.minOverlayCapHeightPct.description",
                "VideoCompile:$.properties.motionPlan.description",
                "VideoCompile:$.properties.regionEffects.description",
                "VideoCompile:$.properties.snapCutsToSpeech.description",
                "VideoCompile:$.properties.speedRamps.description",
                // T-105: GENERATED (per-model resolution catalogue), not a [Description] attribute.
                // video-enhance L3a: every VideoEnhance property documents itself for ListStepSchemas (the step has no builder UI yet).
                "VideoEnhance:$.properties.allowSourceMediaEgress.description",
                "VideoEnhance:$.properties.denoiseStrength.description",
                "VideoEnhance:$.properties.engine.description",
                "VideoEnhance:$.properties.frameMultiplier.description",
                "VideoEnhance:$.properties.keepGrain.description",
                "VideoEnhance:$.properties.marginBottom.description",
                "VideoEnhance:$.properties.marginLeft.description",
                "VideoEnhance:$.properties.marginRight.description",
                "VideoEnhance:$.properties.marginTop.description",
                "VideoEnhance:$.properties.maxSourceSeconds.description",
                "VideoEnhance:$.properties.op.description",
                "VideoEnhance:$.properties.prompt.description",
                "VideoEnhance:$.properties.providerId.description",
                "VideoEnhance:$.properties.registerProjectFile.description",
                "VideoEnhance:$.properties.shutterAngle.description",
                "VideoEnhance:$.properties.source.description",
                "VideoEnhance:$.properties.sourceFps.description",
                "VideoEnhance:$.properties.sourceHeight.description",
                "VideoEnhance:$.properties.sourceWidth.description",
                "VideoEnhance:$.properties.targetAspect.description",
                "VideoEnhance:$.properties.targetHeight.description",
                "VideoEnhance:$.properties.upscaleFactor.description",
                "VideoGenerate:$.properties.resolution.description",
            },
            describedNodes.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    // RE-BASELINED 2026-10-03 by the decision-engine-p2 integration pass. The previous values were
    // 332bd7a4… (VideoCompile) and b6d89917… (VideoGenerate), captured at
    // 127633451f794c3a8f304ceeafe8d2be48bf9825. They went stale for a reason that has nothing to do
    // with the description hook this theory exists to guard: the two CONFIG TYPES changed after that
    // commit, by video-generation-complete's generated-clips work —
    //
    //   git diff --stat 127633451f 80b417fe -- VideoCompileStepConfig.cs VideoGenerateStepConfig.cs
    //     → 133 insertions(+), 7 deletions(-)   (GeneratedClips, EnableGeneratedClips, …)
    //
    // — so the pinned value was asserting against a config shape that no longer exists. This was
    // INHERITED trunk red, not caused by the integration: both failures reproduce on pristine
    // 80b417fe, and the recomputed hash there is byte-for-byte the same as here (f280c843… for
    // VideoGenerate). Recorded rather than silently absorbed, and the values below were obtained by
    // recomputing the hash of the live emission, never by copying an observed failure's tail.
    //
    // What this re-baseline does NOT relax: assertion 1 below is the half that actually guards the
    // hook — the emitted schema must still be byte-equal to the PRE-HOOK code path's own output,
    // recomputed live in-test. That assertion passes unchanged, so the description transform is
    // still proven not to touch a config that has no [Description] anywhere. Only the frozen
    // "before vs after" snapshot moved, because the "before" no longer exists.
    //
    // RE-BASELINED 2026-10-04 (5139007e… → bc18d175…): VideoCompileStepConfig gained
    // InsertContentProjectFileId and InsertFit (docs/video-editing.md "Inserting an existing
    // video"). Narrowness was PROVEN, not argued: deleting exactly those two properties from the
    // live emission's "properties" object re-hashes to 5139007e… byte for byte, so nothing else in
    // the schema moved.
    //
    // RE-BASELINED again the same night (bc18d175… → e28ece48…): CensorLabels, CensorStyle,
    // CensorPadding, CensorHoldSec and KeepWholeSources (docs/video-editing.md "Censoring tracked
    // objects"). Proven the same way — deleting exactly those five properties re-hashes to
    // bc18d175… byte for byte.
    //
    // RE-BASELINED 2026-10-04 (e28ece48… → 17671fd6…): the voiceover phases 3–4 members appended
    // after KeepWholeSources — EnableCaptions, CaptionStyle, CaptionFontSizePct, CaptionFontColor,
    // CaptionBoxColor, CaptionHighlightColor, CaptionMaxCharsPerCue, MaxCaptionCues, VoiceoverMode,
    // VoiceoverDuckDb, VoiceoverDuckRampMs, EnablePickups, PickupVoiceoverStepOrder (docs/voiceover.md
    // "Captions" / "Pickups and dubs"). Deliberate, not blind: the web mirror's schema pin test moves
    // in the same commit for the same thirteen properties.
    // RE-BASELINED again on master (ff287fe4… on master, 17671fd6… on this branch → 698cf5b7…):
    // master's InsertReflections (appended after PickupVoiceoverStepOrder on merge) and its
    // MaxInsertExprKeyframes default 96 → 1500 (docs/video-editing.md "Reflections" and "Sub-pixel
    // corners at the source's frame rate") now coexist with the thirteen voiceover members.
    //
    // RE-BASELINED once more on top of 698cf5b7…: seam bridges and pinned shot plans
    // (VideoCompile MaxSeamBridgeMs, VideoGenerate PlanProjectFileId + the appended "ProjectFile"
    // planSource member) and blurred canvas fill (VideoCompile CanvasFill). Proven the same way —
    // deleting maxSeamBridgeMs and canvasFill re-hashes to 698cf5b7…, and deleting
    // planProjectFileId plus the "ProjectFile" enum member re-hashes to e647f343…, byte for byte.
    //
    // RE-BASELINED 2026-10-04 (8fa1f096… → f3df80bd…): the customer-facing output format —
    // VideoCompile OutputFormat, OutputFit and TargetDurationSec (docs/video-editing.md "Output
    // format"), appended after CanvasFill. Proven the same way — deleting exactly outputFormat,
    // outputFit and targetDurationSec from the live emission's "properties" re-hashes to
    // 8fa1f096… byte for byte, so nothing else in the schema moved.
    //
    // RE-BASELINED 2026-10-04 (f3df80bd… → af820d8a…): caption typography for the libass caption
    // renderer — VideoCompile CaptionFont and CaptionPosition (docs/voiceover.md "Captions"),
    // appended after TargetDurationSec. Proven the same way — deleting exactly captionFont and
    // captionPosition from the live emission's "properties" re-hashes to f3df80bd… byte for byte.
    // (CaptionFontSizePct's new "0 and 4 mean automatic" reading is behaviour, not schema: its
    // type and default are unchanged.)
    //
    // RE-BASELINED (af820d8a… → c0d70e21…): VideoCompileStepConfig gained MuteSourceAudio (a
    // music-only program over clips whose own audio is noise). Proven the same way — deleting
    // muteSourceAudio re-hashes to af820d8a… byte for byte.
    //
    // RE-BASELINED (c0d70e21… → 34e53811…): VideoOutputFit gained Subject (reframe each clip's
    // crop onto its tracked screen or located subject, docs/video-editing.md "Reframing to the
    // subject"). Proven the same way — deleting exactly "Subject" from outputFit's enum re-hashes
    // to c0d70e21… byte for byte. VideoAnalyze's LocateSubjects/SubjectLabel/MaxSubjectShots are
    // not pinned here (that config has no pin), so they move nothing in this test.
    //
    // RE-BASELINED 2026-10-05 (34e53811… → 016f3192…): VideoOutputFormat gained Portrait (4:5,
    // 1080x1350 — the quick format switch, docs/editing-styles.md), appended after Landscape. Proven
    // the same way — deleting exactly "Portrait" from outputFormat's enum re-hashes to 34e53811…
    // byte for byte. The run's style and format live on the execution row, not in this config, so
    // they move nothing here.
    //
    // RE-BASELINED (34e53811… → 066744e9…): VideoCompileStepConfig gained KeepOrder (play the
    // editor's spans in the order listed, docs/video-editing.md "Playing spans in the editor's
    // order"), appended after MuteSourceAudio. Proven the same way — deleting exactly keepOrder from
    // the live emission's "properties" re-hashes to 34e53811… byte for byte.
    //
    // RE-BASELINED (066744e9… → aeaa8272…): InsertContents (a list of VideoInsertContentAssignment —
    // different content per source clip / tracked region) and InsertContentDips (Freeze/Cut/Keep the
    // near-black dips of insert content), docs/video-editing.md "Inserting an existing video",
    // appended after KeepOrder. Proven the same way — deleting exactly insertContents and
    // insertContentDips from the live emission's "properties" re-hashes to 066744e9… byte for byte
    // (the new record type is only reachable through insertContents, so it leaves with it).
    //
    // RE-BASELINED (aeaa8272… → 62a52230…): BeatSync (Off/Beat/Bar — snap every cut to the music's
    // beat or bar, docs/video-editing.md "Cutting to the beat"), appended after InsertContentDips.
    // Proven the same way — deleting exactly beatSync from the live emission's "properties"
    // re-hashes to aeaa8272… byte for byte.
    //
    // MERGED (016f3192… + 62a52230… → b05a1cdf…): both of the above on one tree. Proven both ways —
    // deleting exactly "Portrait" from outputFormat's enum re-hashes to 62a52230…, and deleting
    // exactly keepOrder, insertContents, insertContentDips and beatSync re-hashes to 016f3192…,
    // each byte for byte.
    //
    // RE-BASELINED (b05a1cdf… → 023a3d9d…): LoudnessTarget (Off/Social/Web/Broadcast — bring the
    // finished program to a platform loudness, docs/video-editing.md "Program loudness"), appended
    // after BeatSync. Proven the same way — deleting exactly loudnessTarget from the live emission's
    // "properties" re-hashes to b05a1cdf… byte for byte.
    //
    // RE-BASELINED (023a3d9d… → 189cf7f7…): CaptionStyle gained Bold (the Reels/TikTok caption look,
    // docs/voiceover.md "Captions"), appended after Punch. Proven the same way — deleting exactly
    // "Bold" from captionStyle's enum re-hashes to 023a3d9d… byte for byte.
    // RE-BASELINED (189cf7f7… → 872b1e1b…): TransitionPolicy gained Editor (the editor agent names
    // each seam's transition preset and speed — docs/video-editing.md "Editor-chosen transitions"),
    // appended after Expressive. Deleting exactly "Editor" from transitionPolicy's enum re-hashes to
    // 189cf7f7… byte for byte.
    //
    // RE-BASELINED 2026-10-10 by issue #93 phases 4-5 (video-generation-complete, rebased onto master),
    // BOTH pins, each by one named, additive config-surface change in the same commit:
    //   - VideoCompile 872b1e1b… → c9a72431…: TakeSelection (an ExtractInputRef, the dailies room's
    //     answer) appended LAST, after LoudnessTarget — one property, one transitively reachable
    //     type that already existed (ExtractInputRef).
    //   - VideoGenerate 2214f5ed… → 613a8fb8…: LookMatching, CameraContinuity (bool), MaxGpuMinutes
    //     (decimal), ConformFps, ConformHeight (int) appended at the end of the record — five
    //     properties, no new reachable type, no enum member touched.
    // Nothing was removed, retyped, reordered or re-defaulted, and this test's own
    // `Assert.Equal(unhooked, emitted)` half still proves the description hook is inert on both.
    //
    // RE-BASELINED 2026-10-10 by the video-generation credits wave (branch i2v/credits):
    //   - VideoGenerate 613a8fb8… → c58d04a8…: ONE property RENAMED, maxSpendUsd →
    //     maxSpendCredits. The step's spend cap is denominated in platform CREDITS now (1 credit =
    //     $0.006 of vendor cost, PricingDefaults.CreditUsdValue) and a vendor's own USD quote is
    //     converted at the boundary instead of being the cap itself. The delta is the JSON key and
    //     nothing else: the property is still a `decimal` at the same position in the record, and
    //     no other property, no transitively reachable type and no enum member moved. A rename moves
    //     this pin precisely because the schema emits property NAMES, which is why it is argued here
    //     rather than pasted.
    //   - VideoCompile c9a72431… UNCHANGED, which is this re-baseline's control: the wave touched no
    //     VideoCompile surface, and a value that had moved there would be a finding, not a pin.
    // RE-BASELINED (c9a72431… → a9501d3c…): VideoCompileStepConfig gained MixInsertAudio and
    // InsertAudioGainDb (the sound of an inserted asset, docs/video-editing.md "Sound from an
    // inserted asset"), appended LAST after TakeSelection — two properties, neither of them a new
    // transitively reachable type (both are primitives) and no enum member touched. Proven the same
    // way as every entry above: deleting exactly mixInsertAudio and insertAudioGainDb from the LIVE
    // emission's "properties" re-hashes to c9a72431… byte for byte, so nothing else in the schema
    // moved (no property removed, retyped, reordered or re-defaulted).
    // RE-BASELINED (a9501d3c… → 9a1d6d7d…): VideoCompileStepConfig gained DesignSystemColors and
    // DesignSystemFonts (brand-aware overlay colours and caption fonts, docs/design-system.md,
    // P3-07), appended LAST after InsertAudioGainDb — two bool properties, neither a new
    // transitively reachable type and no enum member touched. Proven the same way as every entry
    // above: deleting exactly designSystemColors and designSystemFonts from the LIVE emission's
    // "properties" re-hashes to a9501d3c… byte for byte, so nothing else in the schema moved (no
    // property removed, retyped, reordered or re-defaulted). VideoGenerate's pin is unchanged.
    // VideoCompile LEFT this theory in the prompted-region-effects contracts: it now declares
    // [property: Description] on RegionEffects, so "a config with no [Description] anywhere" is no
    // longer true of it and its LIVE half cannot pass by construction. The property being guarded —
    // that the hook mutates no node but its own — is still asserted by this theory over
    // VideoGenerate, and exhaustively by
    // GetSchema_EmitsADescription_OnlyForPropertiesCarryingADescriptionAttribute above, which lists
    // every described node in the whole surface.
    [Theory]
    [InlineData(StepType.VideoGenerate, typeof(VideoGenerateStepConfig),
        "c58d04a8a00df1a980e918a32a32beb117cd588964189d66032d547093d439e1")]
    public void GetSchema_WithoutAnyDescriptionAttribute_IsByteIdenticalToTheUnhookedExport(
        StepType stepType, Type configType, string pinnedSha256)
    {
        // Two independent proofs that adding the hook changed nothing for a step config that has no
        // [Description] anywhere (VideoCompile is the richest config in the repo, VideoGenerate the
        // most recently extended one):
        //
        //  1. LIVE — the emitted schema is byte-equal to the schema the PRE-HOOK code path produces,
        //     re-computed here: JsonSchemaExporter.GetJsonSchemaAsNode(<the exporter's own serializer
        //     options>, configType) with no JsonSchemaExporterOptions at all. If the transform ever
        //     mutated a node it had no business touching, this diverges.
        //  2. PINNED — the emitted JSON hashes to a fixed literal, a bare comparison against bytes
        //     rather than a value recomputed in-test. That is what makes this half worth having and
        //     also what makes it honest about its limits: it cannot notice a schema change on its
        //     own, but it equally cannot silently re-baseline itself, which is exactly the failure
        //     mode the LIVE half above is blind to.
        //
        //     The literal is no longer the sha256 captured at
        //     127633451f794c3a8f304ceeafe8d2be48bf9825, the commit immediately before the description
        //     hook landed. A historical hash is unrecoverable for these config types: it is taken over
        //     the schema the CURRENT type serializes, and both shapes have moved on since that commit.
        //     There is no pre-hook export of the present type left to hash, so this literal is a
        //     baseline re-taken over the current export; the LIVE half is what keeps proving the hook
        //     itself changed nothing.
        //
        //     RE-BASELINED for VideoCompile and VideoGenerate only, by the wave-A generated-clips
        //     work — VideoCompile gained GeneratedClips/EnableGeneratedClips/MaxGeneratedClips and
        //     VideoGenerate gained the StepRef plan source with Plan/Analysis refs — so the pin was
        //     stale against a config surface that has since changed INTENTIONALLY and under review.
        //     Re-baselining is safe here precisely because it does not weaken the guard: proof 1
        //     above (LIVE, re-computed against the unhooked code path) still independently asserts
        //     that the description hook mutates nothing. Every other step type's pin is untouched.
        //
        //     An earlier branch of issue #93 (wave B) carried a MaxGeneratedSeams member and a
        //     "Generated" member on VideoTransitionPolicy/SeamTreatment, re-baselining this pin three
        //     times across two merges. That design was superseded by master's anchor-based seam
        //     bridge (MaxSeamBridgeMs) and none of it is in this tree, so those literals are history,
        //     not ancestors of the value below.
        //
        //     RE-BASELINED ONCE, deliberately — a pin updated merely to agree with whatever the code
        //     now emits is a rubber stamp, so the re-baseline is argued here rather than assumed.
        //     The hashes above were captured by rebuilding 0e3ef54a878bfba2486c7647a91ba48e23c39a5c
        //     (the commit that added this test) and confirming it still emits the PREVIOUS values —
        //     i.e. the old pins were correct for the tree they were taken over, and only the tree
        //     moved. Each delta traces to one source, and the two sources are NOT the same:
        //       - VideoGenerate e647f343… — loopBackBehavior (Reuse|Regenerate) added to VideoGenerateStepConfig;
        //         that one property and its enum are the only schema delta against f280c843….
        //       - VideoGenerate f280c843… — upstream: a59c0e81 (#189) adds "StepRef" (appended after
        //         "Inline") to the planSource enum, plus the plan / analysis ExtractInputRef
        //         properties. That value is identical on both sides of the origin/master merge and
        //         sits outside this pair's dispute.
        //       - VideoCompile 5139007e… — this branch: EnableVoiceover / VoiceoverStepOrder. The
        //         other side of the merge carries 12420df7…, taken over a config that does not have
        //         them, so that value is the wrong one for a merged tree that does. The
        //         generated-clips fields upstream added are present in BOTH trees and are not what
        //         separates these two hashes.
        //     Diffing the schemas shows those additions and NOTHING else: no property removed,
        //     retyped, reordered or re-defaulted, and no transitively reachable type
        //     (ExtractInputRef, ExtractInputSource, any referenced enum) moved at all. That
        //     narrowness is the whole justification — a future re-baseline with a wider diff is a
        //     finding to report, not a value to paste.
        var exporter = new StepConfigSchemaExporter();

        StepConfigSchemaResult result = exporter.GetSchema(stepType);

        Assert.True(result.HasConfig);
        Assert.NotNull(result.Schema);

        if (stepType == StepType.VideoGenerate)
        {
            // T-105: the exporter now stamps a GENERATED description (the per-model resolution catalogue) onto
            // VideoGenerate's `resolution`, on purpose and not through the [Description] hook this theory guards.
            // Removing exactly that node restores the hook-only emission, so proof 1 still asserts the hook
            // itself mutated nothing. (VideoGenerationResolutionCatalogTests pins the annotation itself.)
            result.Schema!["properties"]!["resolution"]!.AsObject().Remove("description");
        }

        string emitted = SerializeSchema(result.Schema!);
        string unhooked = SerializeSchema(JsonSchemaExporter.GetJsonSchemaAsNode(UnhookedSerializerOptions, configType));

        Assert.Equal(unhooked, emitted);
        if (stepType == StepType.VideoGenerate)
        {
            // T-105 re-baseline, argued: VideoGenerateStepConfig gained firstFrame, lastFrame, firstFrameProjectFileId
            // and lastFrameProjectFileId (Inline first/last frame images). Deleting exactly those four properties from
            // the LIVE emission re-hashes to the previous pin (c58d04a8…) byte for byte, so nothing else in the schema
            // moved. The pinned literal stays the pre-T-105 one rather than being re-taken over the new shape.
            var stripped = System.Text.Json.Nodes.JsonNode.Parse(emitted)!;
            // Avatars phase 2 re-baseline, argued the same way: avatarId, avatarReferenceRoles, avatarAudio and
            // avatarAudioProjectFileId are appended members; stripping them too re-hashes to the same pin. App inputs F1
            // (docs/design/app-inputs.md 27) appends stillProviderId the same way. Several subjects per clip
            // (docs/design/avatar-objects.md) appends subjects, subjectMode, plannedSubjects and plannedSubjectIds the
            // same way: no existing property moved.
            foreach (string added in new[] { "firstFrame", "lastFrame", "firstFrameProjectFileId", "lastFrameProjectFileId",
                "avatarId", "avatarReferenceRoles", "avatarAudio", "avatarAudioProjectFileId", "stillProviderId",
                "subjects", "subjectMode", "plannedSubjects", "plannedSubjectIds" })
                stripped["properties"]!.AsObject().Remove(added);
            Assert.Equal("c58d04a8a00df1a980e918a32a32beb117cd588964189d66032d547093d439e1", Sha256Hex(SerializeSchema(stripped)));
            return;
        }
        if (stepType == StepType.VideoCompile)
        {
            // T-106 re-baseline, argued: VideoCompileStepConfig gained InsertContentRef (appended LAST), and
            // VideoInsertContentAssignment (the items of insertContents) gained the same InsertContentRef. Deleting
            // exactly "insertContentRef" from the LIVE emission's top-level properties AND from the insertContents
            // item properties re-hashes to the previous pin (22e5f1da...) byte for byte, so nothing else moved.
            // The pinned literal stays the pre-T-106 one rather than being re-taken over the new shape.
            var stripped = System.Text.Json.Nodes.JsonNode.Parse(emitted)!;
            stripped["properties"]!.AsObject().Remove("insertContentRef");
            stripped["properties"]!["insertContents"]!["items"]!["properties"]!.AsObject().Remove("insertContentRef");
            // T-109 re-baseline, argued the same way: VideoCompileStepConfig gained MusicGainDb (a nullable number) and
            // GeneratedClipAudio (a nullable GeneratedAudioMode, inlined under that one property), appended LAST. Deleting
            // exactly "musicGainDb" and "generatedClipAudio" from the top-level properties as well re-hashes to the pin.
            stripped["properties"]!.AsObject().Remove("musicGainDb");
            stripped["properties"]!.AsObject().Remove("generatedClipAudio");
            // T-203 re-baseline, argued the same way: VideoCompileStepConfig gained TargetDurationMode (a nullable
            // TargetDurationMode enum, inlined under that one property), appended LAST. Deleting exactly
            // "targetDurationMode" from the top-level properties as well re-hashes to the pin.
            stripped["properties"]!.AsObject().Remove("targetDurationMode");
            Assert.Equal(pinnedSha256, Sha256Hex(SerializeSchema(stripped)));
            return;
        }
        Assert.Equal(pinnedSha256, Sha256Hex(emitted));
    }

    [Fact]
    public void LiftDescriptionAttribute_ContextWithNoProperty_ReturnsTheNodeUntouchedAndNeverThrows()
    {
        // The transform runs for nodes that belong to no property at all — type-level nodes and
        // array-item nodes, which System.Text.Json hands over with a null PropertyInfo (observed
        // live: `$.properties.sources.items`). `default` is the one such context value reachable from
        // outside the assembly (JsonSchemaExporterContext is a sealed struct with no public
        // constructor), so this pins that the guard short-circuits and the node comes back by
        // reference, unchanged: no stray `description`, no throw escaping into GetSchema's catch.
        //
        // What this does NOT cover, stated rather than implied: a context whose PropertyInfo exists
        // but whose AttributeProvider throws. That value cannot be constructed from outside the
        // assembly, so it is not inducible without adding a test-only injection seam — see the
        // commit report. The deployed defence against it is that LiftDescriptionAttribute swallows
        // its own failures, and GetAllSchemas_EveryStepTypeThatHadAConfig_StillHasOne pins the
        // observable consequence for every step type the repo ships.
        JsonNode node = JsonNode.Parse("""{"type":"string"}""")!;

        JsonNode returned = StepConfigSchemaExporter.LiftDescriptionAttribute(default, node);

        Assert.Same(node, returned);
        Assert.Equal("""{"type":"string"}""", returned.ToJsonString());
    }

    [Fact]
    public void GetAllSchemas_EveryStepTypeThatHadAConfig_StillHasOne()
    {
        // The other half of the same discipline, at the boundary that actually matters: enabling the
        // transform must not turn a working schema into a missing one (HasConfig: false), and must
        // not change the result shape or the key order GetAllSchemas returns.
        var exporter = new StepConfigSchemaExporter();

        Dictionary<string, StepConfigSchemaResult> all = exporter.GetAllSchemas();

        Assert.Equal(
            Enum.GetValues<StepType>().Select(stepType => stepType.ToString()).ToArray(),
            all.Keys.ToArray());

        foreach (StepType stepType in new[]
                 {
                     StepType.Extract,
                     StepType.VideoAnalyze,
                     StepType.VideoCompile,
                     StepType.EditRoom,
                     StepType.GraphicsRoom,
                     StepType.ColorGradeRoom,
                     StepType.VideoGenerate,
                 })
        {
            StepConfigSchemaResult result = all[stepType.ToString()];

            Assert.True(result.HasConfig, $"{stepType} lost its config because the description hook landed");
            Assert.NotNull(result.ConfigTypeName);
            Assert.NotNull(result.Schema);
        }
    }

    [Fact]
    public void DerushPrePass_PositionalParameter_KeepsItsTypeAndDefault()
    {
        // [Description] is an attribute on an existing positional parameter, so it must not have
        // renamed, retyped, reordered or re-defaulted anything on VideoAnalyzeStepConfig.
        var parameter = Assert.Single(
            typeof(VideoAnalyzeStepConfig).GetConstructors().Single().GetParameters(),
            p => p.Name == "DerushPrePass");

        Assert.Equal(typeof(VideoDerushPrePassMode), parameter.ParameterType);
        Assert.Equal(VideoDerushPrePassMode.Off, parameter.DefaultValue);
    }

    private static string AssertSingleDescriptionAttribute<T>(string propertyName)
        => AssertSingleDescriptionAttribute(typeof(T), propertyName);

    private static string AssertSingleDescriptionAttribute(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName);
        Assert.True(property != null, $"{type.Name}.{propertyName} was not found");

        DescriptionAttribute description = Assert.Single(
            property!.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true).Cast<DescriptionAttribute>());

        Assert.False(string.IsNullOrWhiteSpace(description.Description));
        return description.Description;
    }

    /// <summary>
    /// A mirror of <c>StepConfigSchemaExporter</c>'s own serializer options, used to re-run the
    /// exporter's PRE-HOOK schema call in-test: <c>JsonSchemaExporter.GetJsonSchemaAsNode(options,
    /// configType)</c> with no <see cref="JsonSchemaExporterOptions"/>. Kept in lockstep with the
    /// production field deliberately — if the exporter's options ever change, this test must be
    /// re-examined rather than silently agreeing with a changed baseline.
    /// </summary>
    private static readonly JsonSerializerOptions UnhookedSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
    };

    private static string SerializeSchema(JsonNode schema)
        => schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string AssertDescriptionOfProperty(JsonNode schema, string propertyName)
    {
        JsonNode? property = schema["properties"]?[propertyName];
        Assert.True(property is not null, $"schema has no property '{propertyName}'");

        JsonNode? description = property!["description"];
        Assert.True(description is not null, $"property '{propertyName}' carries no description");

        return description!.GetValue<string>();
    }

    /// <summary>
    /// Records the JSON path of every <c>description</c> keyword in a schema. A config property that
    /// merely happens to be NAMED "description" is a schema object, not a string, so it is not
    /// collected: only the keyword itself is.
    /// </summary>
    private static void CollectDescriptionPaths(JsonNode node, string path, List<string> found)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in obj)
            {
                if (entry.Value is null)
                {
                    continue;
                }

                string childPath = $"{path}.{entry.Key}";
                if (entry.Key == "description"
                    && entry.Value is JsonValue value
                    && value.TryGetValue<string>(out _))
                {
                    found.Add(childPath);
                }

                CollectDescriptionPaths(entry.Value, childPath, found);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is { } item)
                {
                    CollectDescriptionPaths(item, $"{path}[{i}]", found);
                }
            }
        }
    }
}
