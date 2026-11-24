using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class EnumShapeTests
{
    [Fact]
    public void StepType_ImageGenerate_is_last_member()
    {
        // Renamed again when ImageGenerate (image-generate-step initiative: pictures from a prompt or an edit instruction) was appended after VideoEnhance.
        // Renamed again when AudioCleanup (audio cleanup initiative: local filters and hosted voice isolation) was appended after PinInsert.
        // Renamed again when PinInsert (launch-film P2, T-206) was appended after AudioGenerate.
        // Renamed again when AudioGenerate (launch-film P1, T-108) was appended after ExtractFrame.
        // Renamed again when ExtractFrame (launch-film P1) was appended after DesignSystem.
        // Renamed again when DesignSystem (product design system, P2) was appended after TimelineRender.
        // Renamed again when TimelineRender (editor v2, P4: renders an edit_timelines document) was appended after DailiesRoom.
        // Renamed again when DailiesRoom (issue #93 phase 4, the dailies room) was appended after DesignGraphic.
        // Renamed again when DesignGraphic (designed graphics for the picture editor) was appended after Revise.
        // Renamed again when Revise (composition revisions) was appended after ThumbnailGenerate.
        // Renamed from StepType_Voiceover_is_last_member when ThumbnailGenerate was appended: the
        // property under test was always "the last member is the newest one", and Voiceover stopped
        // being that. The enum is persisted by NAME (both DbContexts convert it to a string), so
        // appending a member is safe for stored rows; what this guards is that an INSERTION — which
        // would silently renumber every member after it — is caught.
        StepType.ImageGenerate.Should().Be(Enum.GetValues<StepType>().Last());
        Enum.GetNames<StepType>().Should().ContainInOrder("ColorGradeRoom", "VideoGenerate", "Voiceover", "ThumbnailGenerate", "Revise", "DesignGraphic", "DailiesRoom", "TimelineRender", "DesignSystem", "ExtractFrame", "AudioGenerate", "PinInsert", "AudioCleanup", "VideoEnhance", "ImageGenerate");
    }

    [Fact]
    public void InferenceProviderKind_LocalRunner_is_last_member()
    {
        // Renamed from ..._ElevenLabs_is_last_member when the local-model gateway appended LocalRunner. Before that, renamed from ..._ComfyUI_is_last_member when the audio arm appended ElevenLabs (music and
        // sound-effect generation, Product decision 2). Renamed again when issue #93 phases 3 and 5 appended Higgsfield and then ComfyUI after FalAi —
        // after ..._FishAudio_is_last_member (the image arm appended BlackForestLabs) and
        // ..._BlackForestLabs_is_last_member (the subject-detection arm appended FalAi) — for the same
        // reason StepType's guard was renamed: the property under test is "the last member is the newest
        // one", and whatever was last stops being that. The enum is persisted by NAME, so appending is
        // safe for stored rows; what this catches is an INSERTION, which would silently renumber every
        // member after it.
        InferenceProviderKind.LocalRunner.Should().Be(Enum.GetValues<InferenceProviderKind>().Last());
        Enum.GetNames<InferenceProviderKind>().Should().ContainInOrder(
            "MiniMax", "OpenJev", "FishAudio", "BlackForestLabs", "FalAi", "Higgsfield", "ComfyUI", "ElevenLabs", "LocalRunner");
    }

    [Fact]
    public void InferenceProviderCapability_LipSync_is_last_member()
    {
        // Renamed from ..._Compute_is_last_member when the audio arm appended MusicGeneration and then
        // SoundEffectGeneration after Compute.
        // Renamed from ..._Embedding_is_last_member when ImageGeneration and ImageEdit were appended,
        // again when the subject-detection arm appended Segmentation and Depth, again when the
        // picture editor's tools got one capability each (appended after Depth, in job order), and
        // again when the model catalog appended Compute, MusicGeneration and SoundEffectGeneration
        // (docs/design/model-catalog.md §2.3), and again when the prompted-region-effects initiative
        // appended text-prompted VIDEO segmentation — its own capability, never a mode of the
        // still-image Segmentation arm. Renamed once more when the content-moderation initiative
        // appended ContentModeration, served by OpenAICompatible only. Renamed again when the
        // workflow-apps knowledge lane appended Rerank (OpenAICompatible only), then WebSearch, then the
        // audio-cleanup initiative appended AudioIsolation (FalAi only), then the video-enhance initiative appended
        // VideoUpscale and FrameInterpolation (FalAi only), then VideoOutpaint for the Extend op (FalAi only).
        // then the avatar-lipsync-capability initiative appended LipSync (FalAi only, talking-head models).
        // LipSync is last because it appended last.
        InferenceProviderCapability.LipSync.Should().Be(Enum.GetValues<InferenceProviderCapability>().Last());
        Enum.GetNames<InferenceProviderCapability>().Should().ContainInOrder(
            "SpeechSynthesis", "Embedding", "ImageGeneration", "ImageEdit", "Segmentation", "Depth",
            "ImageErase", "ImageFill", "ImageExpand", "ImageBackgroundReplace", "ImageRelight", "ImageUpscale",
            "Compute", "MusicGeneration", "SoundEffectGeneration", "VideoSegmentation", "ContentModeration", "Rerank", "WebSearch", "AudioIsolation",
            "VideoUpscale", "FrameInterpolation", "VideoOutpaint", "LipSync");
    }

    [Fact]
    public void UsageSource_is_append_only_Editor_then_Mcp_FileSummary_TimelineAssist_Moderation_PostText_after_Unattributed()
    {
        UsageSource.PostText.Should().Be(Enum.GetValues<UsageSource>().Last());
        Enum.GetNames<UsageSource>().Should().Equal(
            "Workflow", "Assistant", "Indexing", "DocsIndex", "Storage", "Unattributed", "Editor",
            "Mcp", "FileSummary", "TimelineAssist", "Moderation", "PostText");
    }

    [Fact]
    public void UsageKind_is_append_only_in_spec_order()
    {
        Enum.GetNames<UsageKind>().Should().Equal(
            "LlmInputTokens", "LlmOutputTokens", "LlmCachedInputTokens", "EmbeddingTokens", "AsrAudioSeconds",
            "TtsInputBytes", "VideoGenSeconds", "RenderOutputSeconds", "EncodeOutputSeconds", "AnalyzeMediaSeconds",
            "StorageGbDay",
            // Appended with the image arm: one unit per image RETURNED, split between generation and
            // editing because the vendors price them differently (BFL lists FLUX.2 [pro] at $0.03
            // text-to-image against $0.045 for editing). Append-only, like every member above.
            "ImageGenImages", "ImageEditImages",
            // Appended with the subject-detection arm: one unit per image RETURNED by a segmentation
            // or depth call, which is also the unit fal bills in (per successful output; queue time
            // and server errors are not charged).
            "SegmentationImages", "DepthImages",
            // Appended with the audio arm: billed minutes of generated music (fal's ElevenLabs Music
            // rounds UP to the next minute) and seconds of generated sound effect; then the
            // prompted-region-effects initiative: one unit per FRAME of video input sent to a
            // text-prompted video segmenter (fal: $0.01 per 16 frames); then content moderation:
            // one unit per CALL, priced at the vendor's real $0 figure; then rerank: one
            // unit per SEARCH (a query plus up to 20 candidate texts); then audio cleanup: billed MINUTES of
            // source audio sent to a hosted voice isolator (seconds measured, rounded up to minutes);
            // then video enhancement: billed OUTPUT seconds of an upscaled clip and GPU COMPUTE seconds of an interpolation.
            "MusicGenMinutes", "SoundEffectGenSeconds", "VideoSegmentationFrames", "ModerationCalls", "RerankSearches", "WebSearches", "AudioCleanupMinutes",
            "VideoEnhanceSeconds", "VideoEnhanceComputeSeconds");
    }

    [Fact]
    public void EditImageLayerKind_is_append_only_in_spec_order()
    {
        // Persisted by NAME (the DbContext converts it to a string). The thumbnail editor appended
        // Generated, Text and Image after the original Source and Cutout; an INSERTION or a rename
        // would silently change what stored rows mean.
        Enum.GetNames<EditImageLayerKind>().Should().Equal("Source", "Cutout", "Generated", "Text", "Image");
        EditImageLayerKind.Image.Should().Be(Enum.GetValues<EditImageLayerKind>().Last());
    }

    [Fact]
    public void ImageOperation_is_append_only_in_spec_order()
    {
        // Persisted by name inside a layer's provenance JSON.
        Enum.GetNames<ReelBolt.Shared.Inference.ImageOperation>().Should().Equal(
            "Expand", "ReplaceBackground", "Relight", "Upscale");
    }

    [Fact]
    public void FailureClass_is_append_only_in_spec_order()
    {
        Enum.GetNames<FailureClass>().Should().Equal(
            "None", "UserInput", "UserCancelled", "ByoProviderError", "PlatformProviderError", "PlatformInternal",
            "InsufficientCredits", "EntitlementDenied",
            // D12 appended the runner class last; the members above keep their spec order and their
            // names, because a persisted column reads them back by name.
            "Runner");
    }
}
