using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class EnumsTests
{
    [Fact]
    public void InferenceProviderCapability_Decision_exists_and_roundtrips()
    {
        // Arrange
        var decision = InferenceProviderCapability.Decision;
        var decisionString = decision.ToString();

        // Act
        var parsed = Enum.Parse<InferenceProviderCapability>(decisionString);

        // Assert
        parsed.Should().Be(InferenceProviderCapability.Decision);
    }

    [Fact]
    public void InferenceProviderKind_TypeSafe_exists_and_roundtrips()
    {
        // Arrange
        var typeSafe = InferenceProviderKind.TypeSafe;
        var typeSafeString = typeSafe.ToString();

        // Act
        var parsed = Enum.Parse<InferenceProviderKind>(typeSafeString);

        // Assert
        parsed.Should().Be(InferenceProviderKind.TypeSafe);
    }

    [Fact]
    public void StepType_master_and_branch_members_are_appended_in_order()
    {
        // Arrange & Act
        var names = Enum.GetNames(typeof(StepType));

        // Assert
        // Master appended VideoGenerate, this branch appended Voiceover; each keeps the position its
        // own side gave it, so the merged tail is ColorGradeRoom -> VideoGenerate -> Voiceover.
        names.Should().ContainInOrder("ColorGradeRoom", "VideoGenerate", "Voiceover");
    }

    [Fact]
    public void StepType_VideoGenerate_is_appended_in_order()
    {
        // Restored from master's StepType_VideoGenerate_is_last_member. The literal "is last" form
        // cannot hold once this branch appends Voiceover, so master's member is pinned by position
        // in the merged tail instead; EnumShapeTests keeps Voiceover last.
        var names = Enum.GetNames(typeof(StepType));

        names.Should().ContainInOrder("ColorGradeRoom", "VideoGenerate", "Voiceover");
    }

    [Fact]
    public void InferenceProviderKind_MiniMax_follows_TypeSafe()
    {
        // This assertion follows the TAIL, so it moves every time a kind is appended — it is how an
        // append is distinguished from a reorder, not how persisted rows are protected. The NAME
        // assertions below are what actually protect those.
        //
        // Master appended MiniMax, this branch appended FishAudio after it. Master's OpenJev sits
        // between them in the merged tail, so this asserts relative order only, never adjacency.
        var names = Enum.GetNames(typeof(InferenceProviderKind));

        names.Should().ContainInOrder("TypeSafe", "MiniMax", "FishAudio");
    }

    [Fact]
    public void InferenceProviderKind_LocalRunner_is_last_member()
    {
        // Was InferenceProviderKind_ElevenLabs_is_last_member until the local-model gateway appended LocalRunner.
        // Was InferenceProviderKind_ComfyUI_is_last_member until the audio arm appended ElevenLabs.
        // Was InferenceProviderKind_Higgsfield_is_last_member. Issue #93 phases 3 and 5 append
        // Higgsfield and then ComfyUI AFTER master's tail (..., FishAudio, BlackForestLabs, FalAi), so
        // the tail is ..., FalAi, Higgsfield, ComfyUI: Higgsfield is SECOND-to-last, FalAi
        // THIRD-to-last and BlackForestLabs FOURTH-to-last. Pinning all four is what proves the tail
        // is a chain of appends rather than a reorder — the enum is persisted by NAME, so an
        // insertion would break nothing, but a reorder of the persisted names would still be a drift
        // worth catching.
        // Arrange & Act
        var names = Enum.GetNames(typeof(InferenceProviderKind));

        // Assert
        names.Last().Should().Be("LocalRunner");
        names[names.Length - 2].Should().Be("ElevenLabs");
        names[names.Length - 3].Should().Be("ComfyUI");
        names[names.Length - 4].Should().Be("Higgsfield");
        names[names.Length - 5].Should().Be("FalAi");
        names[names.Length - 6].Should().Be("BlackForestLabs");
        names.Should().ContainInOrder("OpenJev", "FishAudio", "BlackForestLabs", "FalAi", "Higgsfield", "ComfyUI", "ElevenLabs", "LocalRunner");
    }

    [Fact]
    public void InferenceProviderKind_ElevenLabs_keeps_its_persisted_name()
    {
        // The controller allowlist and inference_providers.kind both carry the NAME.
        Enum.GetNames<InferenceProviderKind>().Should().Contain("ElevenLabs");
        InferenceProviderKind.ElevenLabs.ToString().Should().Be("ElevenLabs");
        Enum.Parse<InferenceProviderKind>("ElevenLabs").Should().Be(InferenceProviderKind.ElevenLabs);
    }

    [Fact]
    public void InferenceProviderKind_ComfyUI_keeps_its_persisted_name()
    {
        // JsonStringEnumConverter persists member NAMES; the admin API's "kind" string must match.
        Enum.GetNames<InferenceProviderKind>().Should().Contain("ComfyUI");
        InferenceProviderKind.ComfyUI.ToString().Should().Be("ComfyUI");
        Enum.Parse<InferenceProviderKind>("ComfyUI").Should().Be(InferenceProviderKind.ComfyUI);
    }

    [Fact]
    public void InferenceProviderKind_MiniMax_keeps_its_persisted_name()
    {
        // The persisted kind NAME is load-bearing, not cosmetic: InferenceProvidersController
        // hard-codes the string "MiniMax" in its VideoGeneration allowlist and
        // inference_providers.kind persists the name, so a rename would silently orphan every
        // existing VideoGeneration row behind an "Invalid kind" 400 rather than failing loudly.
        // The old ordering test used to pin this by accident; #188's OpenJev work replaced it, and
        // the name was left pinned by nothing until this test.
        Enum.GetNames<InferenceProviderKind>().Should().Contain("MiniMax");
        InferenceProviderKind.MiniMax.ToString().Should().Be("MiniMax");
        Enum.Parse<InferenceProviderKind>("MiniMax").Should().Be(InferenceProviderKind.MiniMax);
    }

    [Fact]
    public void InferenceProviderKind_Higgsfield_keeps_its_persisted_name()
    {
        // Same contract as MiniMax above, for the newly appended kind: the controller's allowlist
        // matches the name "Higgsfield" and the column persists the name.
        Enum.GetNames<InferenceProviderKind>().Should().Contain("Higgsfield");
        InferenceProviderKind.Higgsfield.ToString().Should().Be("Higgsfield");
        Enum.Parse<InferenceProviderKind>("Higgsfield").Should().Be(InferenceProviderKind.Higgsfield);
    }

    [Fact]
    public void InferenceProviderKind_existing_members_keep_their_names_and_order()
    {
        // The enum is persisted as a string, so a rename or a reorder does not fail a migration —
        // it silently rebinds existing inference_providers rows to a different kind (or leaves
        // them holding a name no longer defined). This is the guard for that, and appending a new
        // member at the tail is the only edit it permits.
        var names = Enum.GetNames(typeof(InferenceProviderKind));

        names.Take(6).Should().Equal(
            "AzureOpenAI",
            "OpenAICompatible",
            "Anthropic",
            "Gemini",
            "DeepSeek",
            "TypeSafe");
    }

    [Fact]
    public void InferenceProviderKind_OpenJev_exists_and_roundtrips()
    {
        // Arrange
        var openJev = InferenceProviderKind.OpenJev;
        var openJevString = openJev.ToString();

        // Act
        var parsed = Enum.Parse<InferenceProviderKind>(openJevString);

        // Assert
        openJevString.Should().Be("OpenJev");
        parsed.Should().Be(InferenceProviderKind.OpenJev);
    }

    [Fact]
    public void InferenceProviderKind_OpenJev_is_appended_in_order()
    {
        // Restored from master's InferenceProviderKind_OpenJev_is_last_member. The literal "is last"
        // form cannot hold once this branch appends FishAudio, so master's member is pinned by
        // presence and position in the merged tail instead. EnumShapeTests no longer keeps FishAudio
        // last either: the wave-B merge appends Higgsfield after it, so that file pins HIGGSFIELD as
        // the tail and FishAudio as the member immediately in front of it.
        // Existence is not uniquely pinned here: the round-trip test above asserts it, and deleting
        // the member fails the build, since the suite's other tests name it at compile time. What
        // is unique here is position: the sibling MiniMax_follows_TypeSafe test deliberately omits
        // OpenJev, so only this assertion places it between MiniMax and FishAudio.
        var names = Enum.GetNames(typeof(InferenceProviderKind));

        names.Should().ContainInOrder("TypeSafe", "MiniMax", "OpenJev", "FishAudio");
    }

    [Fact]
    public void InferenceProviderCapability_VideoGeneration_follows_Decision()
    {
        // Arrange & Act
        var names = Enum.GetNames(typeof(InferenceProviderCapability));

        // Assert
        // Master appended VideoGeneration, this branch appended SpeechSynthesis after it.
        names.Should().ContainInOrder("Decision", "VideoGeneration", "SpeechSynthesis");
    }

    [Fact]
    public void InferenceProviderCapability_VideoGeneration_is_appended_in_order()
    {
        // Restored from master's InferenceProviderCapability_VideoGeneration_is_last_member. The
        // literal "is last" form cannot hold once this branch appends SpeechSynthesis, so master's
        // member is pinned by position in the merged tail instead; EnumShapeTests keeps
        // SpeechSynthesis last.
        var names = Enum.GetNames(typeof(InferenceProviderCapability));

        names.Should().ContainInOrder("Decision", "VideoGeneration", "SpeechSynthesis");
    }

    [Fact]
    public void AgentType_dailies_members_are_appended_last()
    {
        // Was AgentType_GraphicDesigner_is_last_member. Issue #93 phase 4 appends the dailies room's
        // two agent types after master's tail: ..., ShotDirector, NarrationWriter, PickupPlanner,
        // NarrationTranslator, CompositionReviser, GraphicDesigner, TakeSelector, DailiesDirector, and
        // the picture-overlay initiative then appended OverlayPlanner after them. It
        // stays a POSITION pin rather than being dropped: every AgentType must still be appended LAST
        // in this shared enum, never inserted, because several live initiatives edit it and a
        // reordering would renumber the persisted AgentType column on every seeded row.
        var names = Enum.GetNames(typeof(AgentType));

        // Assert
        names.Should().ContainInOrder("ShotDirector", "NarrationWriter", "PickupPlanner", "NarrationTranslator", "CompositionReviser", "GraphicDesigner", "TakeSelector", "DailiesDirector", "OverlayPlanner", "MotionDirector", "DynamicsPlanner");
        names.Last().Should().Be("DynamicsPlanner");
        names[names.Length - 2].Should().Be("MotionDirector");
        names[names.Length - 3].Should().Be("OverlayPlanner");
        names[names.Length - 4].Should().Be("DailiesDirector");
        names[names.Length - 5].Should().Be("TakeSelector");
        names[names.Length - 6].Should().Be("GraphicDesigner");
    }

    [Fact]
    public void StepType_DailiesRoom_is_appended_after_DesignGraphic()
    {
        var names = Enum.GetNames(typeof(StepType));

        // TimelineRender (editor v2) is appended after DailiesRoom, and DesignSystem (product design
        // system, P2) after TimelineRender, ExtractFrame after DesignSystem, AudioGenerate after ExtractFrame, PinInsert after AudioGenerate AudioCleanup after PinInsert, VideoEnhance after AudioCleanup and ImageGenerate after VideoEnhance;
        // append-only, never reordered.
        names.Should().ContainInOrder("ColorGradeRoom", "VideoGenerate", "Voiceover", "ThumbnailGenerate", "Revise", "DesignGraphic", "DailiesRoom", "TimelineRender", "DesignSystem", "ExtractFrame", "AudioGenerate", "PinInsert", "AudioCleanup", "VideoEnhance", "ImageGenerate");
        names.Last().Should().Be("ImageGenerate");
    }
}
