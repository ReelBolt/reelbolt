using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// The allowlist of settable settings (docs/design/app-inputs.md 7): it is exactly the reviewed initial list, every entry
/// is a real member of the exported config schema, and nothing on the denylist (consent, egress, providers, spend caps,
/// ids, step references, timeouts, cache, mode, version, template keys) can ever be bound.
/// </summary>
public class AppBindableAllowlistTests
{
    private static readonly Regex Denied = new(
        "(egress|consent|providerid$|id$|fileid$|steporder|^timeout|cachemode|^version$|placeholders|apiKey|^source$|^sources$)",
        RegexOptions.IgnoreCase);

    [Fact]
    public void The_allowlist_is_exactly_the_reviewed_initial_list()
    {
        Dictionary<string, string[]> expected = new()
        {
            ["VideoGenerate"] = ["inlinePrompt", "resolution", "durationSeconds", "ratio", "takes", "maxClips", "maxSpendCredits", "@model", "@stillModel"],
            ["AudioGenerate"] = ["prompt", "durationSec", "takes", "maxSpendCredits", "@model"],
            ["Voiceover"] = ["@model", "@spokenText"],
            ["ThumbnailGenerate"] = ["atTimeSec", "overlayText"],
            ["VideoCompile"] = ["programFadeOutMs", "programAudioFadeOutMs", "transitionPolicy", "enableCaptions", "captionStyle", "musicGainDb",
                "regionEffects[].target", "regionEffects[].effect", "captionSource", "enableGraphics", "enableMusic", "enableColorGrade", "enableSfx",
                "enableGeneratedClips", "enableVoiceover", "enablePickups", "enableMotion", "cutOnEvents", "beatSync", "muteSourceAudio"],
            ["VideoAnalyze"] = ["transcription", "sceneDetection", "trackObjects[].label", "emitOverlayPlacements", "offerMusicTracks", "offerSfxClips", "maxDescribedEvents", "sfxOnFootage", "@visionModel"],
            ["ExtractFrame"] = ["at", "atSec"],
            ["AudioCleanup"] = ["target"],
            ["ImageGenerate"] = ["prompt", "instruction", "itemList", "resolution", "outputWidth", "outputHeight"],
        };

        Dictionary<string, string[]> actual = Enum.GetValues<StepType>()
            .Select(t => (t, AppBindableCatalog.For(t)))
            .Where(p => p.Item2.Count > 0)
            .ToDictionary(p => p.t.ToString(), p => p.Item2.Select(f => f.Path).ToArray());

        actual.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach ((string type, string[] paths) in expected)
            actual[type].Should().BeEquivalentTo(paths, $"the {type} allowlist is reviewed (docs/design/app-inputs.md 7.2)");
    }

    [Fact]
    public void No_bindable_setting_matches_the_denylist()
    {
        List<string> leaks = [];
        foreach (AppBindableField field in AppBindableCatalog.All().Where(f => !f.IsPseudo))
        {
            foreach (string segment in field.Path.Replace("[]", string.Empty).Split('.'))
            {
                if (Denied.IsMatch(segment))
                    leaks.Add($"{field.StepType}.{field.Path}");
            }
        }

        leaks.Should().BeEmpty("consent, egress, providers, ids, step references, timeouts and cache settings are never bindable");
        AppBindableCatalog.Find(StepType.VideoCompile, "mode").Should().BeNull("stream copy vs re-encode stays the author's");
        AppBindableCatalog.Find(StepType.VideoCompile, "targetDurationSec").Should().BeNull("the brief's lengthSec governs it");
        AppBindableCatalog.Find(StepType.VideoGenerate, "maxSpendCredits")!.SpendCap.Should().BeTrue("a person sets the step cap; the validator keeps it visible");
        AppBindableCatalog.Find(StepType.VideoGenerate, "allowSourceMediaEgress").Should().BeNull();
        AppBindableCatalog.Find(StepType.VideoGenerate, "avatarId").Should().BeNull("the brief's avatar governs it");
        AppBindableCatalog.Find(StepType.AudioGenerate, "kind").Should().BeNull();
    }

    [Fact]
    public void ImageGenerate_binds_its_text_and_item_list_but_never_source_egress_provider_or_spend()
    {
        foreach (string path in new[] { "prompt", "instruction", "itemList" })
        {
            AppBindableField field = AppBindableCatalog.Find(StepType.ImageGenerate, path)!;
            field.Mode.Should().Be(AppBindingMode.Placeholder, path);
            field.Types.Should().OnlyContain(t => AppInputTypes.IsText(t));
        }

        AppBindableCatalog.Find(StepType.ImageGenerate, "resolution")!.Types.Should().Equal(AppInputType.Choice);
        AppBindableCatalog.Find(StepType.ImageGenerate, "resolution")!.Choices.Should().Equal("1K", "2K", "4K");
        foreach (string path in new[] { "outputWidth", "outputHeight" })
        {
            AppBindableField size = AppBindableCatalog.Find(StepType.ImageGenerate, path)!;
            size.Mode.Should().Be(AppBindingMode.Value, path);
            size.Types.Should().Equal(AppInputType.Number);
            size.Min.Should().Be(64);
            size.Max.Should().Be(8192);
        }

        AppBindableCatalog.Find(StepType.ImageGenerate, "prompt")!.MaxLength.Should().Be(ImageGenerateStepConfig.MaxPromptLength);
        foreach (string path in new[] { "source", "allowSourceMediaEgress", "providerId", "maxSpendCredits", "variations", "mode" })
            AppBindableCatalog.Find(StepType.ImageGenerate, path).Should().BeNull(path);
    }

    [Fact]
    public void A_hosted_cleanup_target_cannot_be_chosen_from_the_app()
    {
        AppBindableField target = AppBindableCatalog.Find(StepType.AudioCleanup, "target")!;
        target.Choices.Should().BeEquivalentTo(["VoiceBasic", "Hum", "Wind", "Hiss", "Echo"]);
        target.Choices.Should().NotContain(["IsolateVoice", "RemoveMusic"], "hosted targets need consent given in the app only");
    }

    [Fact]
    public void Every_bindable_setting_is_a_member_of_the_exported_schema()
    {
        StepConfigSchemaExporter exporter = new();
        List<string> missing = [];
        foreach (AppBindableField field in AppBindableCatalog.All().Where(f => !f.IsPseudo))
        {
            JsonNode? schema = exporter.GetSchema(field.StepType).Schema;
            string json = schema?.ToJsonString() ?? string.Empty;
            foreach (string segment in field.Path.Replace("[]", string.Empty).Split('.'))
            {
                if (!json.Contains($"\"{segment}\"", StringComparison.Ordinal))
                    missing.Add($"{field.StepType}.{field.Path} ({segment})");
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Text_settings_take_placeholders_and_everything_else_is_a_whole_value()
    {
        foreach (AppBindableField field in AppBindableCatalog.All())
        {
            if (field.Path == AppBindableCatalog.SpokenTextKey)
            {
                // F1: a Voiceover's own words are a whole value written as Inline lines, from a text input.
                field.Mode.Should().Be(AppBindingMode.Value);
                field.Types.Should().OnlyContain(t => AppInputTypes.IsText(t));
                continue;
            }

            if (field.Types.All(AppInputTypes.IsText))
                field.Mode.Should().Be(AppBindingMode.Placeholder, field.Path);
            else
            {
                field.Mode.Should().Be(AppBindingMode.Value, field.Path);
                // F1: a nested whole value names a member of an item of a top-level list (regionEffects[].effect).
                if (field.Path.Contains('.'))
                    field.Path.Should().MatchRegex(@"^[a-zA-Z]+\[\]\.[a-zA-Z]+$");
                field.Types.Should().NotContain(t => AppInputTypes.IsText(t), field.Path);
            }
        }
    }

    [Fact]
    public void Each_setting_states_the_json_kind_it_holds()
    {
        AppBindableCatalog.Find(StepType.VideoCompile, "enableCaptions")!.ValueKind.Should().Be(AppBindableValueKind.Boolean);
        AppBindableCatalog.Find(StepType.VideoCompile, "programFadeOutMs")!.ValueKind.Should().Be(AppBindableValueKind.Number);
        AppBindableCatalog.Find(StepType.VideoCompile, "captionSource")!.ValueKind.Should().Be(AppBindableValueKind.Text);
        AppBindableCatalog.Find(StepType.VideoCompile, "regionEffects[].effect")!.Choices.Should().Contain(["Blur", "Pixelate"]);
        AppBindableCatalog.Find(StepType.VideoGenerate, AppBindableCatalog.StillModelKey)!.Capabilities.Should().Equal("ImageEdit");
    }

    [Fact]
    public void The_model_key_exists_only_on_the_steps_that_can_choose_a_model()
    {
        AppBindableCatalog.All().Where(f => f.Path == AppBindableCatalog.ModelKey).Select(f => f.StepType)
            .Should().BeEquivalentTo([StepType.VideoGenerate, StepType.AudioGenerate, StepType.Voiceover]);
        AppBindableCatalog.Find(StepType.Agent, AppBindableCatalog.ModelKey).Should().BeNull("an agent's model is chosen through the input's agent member");
    }

    [Fact]
    public void Step_cards_carry_bindable_fields_only_when_asked()
    {
        StepCatalog.Get(StepType.VideoGenerate).BindableFields.Should().BeNull("GetStepCatalog starts showing them in lane L8");
        StepCard card = StepCatalog.Get(StepType.VideoGenerate, includeBindableFields: true);
        card.BindableFields!.Select(f => f.Path).Should().Contain(["inlinePrompt", "@model"]);
        card.BindableFields!.Single(f => f.Path == "inlinePrompt").Mode.Should().Be("placeholder");
        card.BindableFields!.Single(f => f.Path == "ratio").Types.Should().Equal("shape", "choice");
        StepCatalog.GetAll(includeBindableFields: true).Single(c => c.StepType == "Agent").BindableFields.Should().BeEmpty();
    }
}
