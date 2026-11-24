using System;
using System.Collections.Generic;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Briefs;

/// <summary>The shared brief rules (REST, assistant tool, MCP), without a database.</summary>
public class RunBriefValidatorTests
{
    private static (RunBrief? Brief, string? Error) Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return RunBriefValidator.ParseJson(doc.RootElement.Clone());
    }

    [Fact]
    public void Absent_or_null_is_no_brief()
    {
        RunBriefValidator.ParseJson(null).Should().Be((null, null));
        Parse("null").Should().Be((null, null));
    }

    [Fact]
    public void A_full_brief_parses_in_camelCase_with_enum_names()
    {
        (RunBrief? brief, string? error) = Parse(
            """{"language":"it","sourceLanguage":"en-US","lengthSec":45,"format":"Vertical","resolution":"1080p","stylePreset":"fast-social-reel","tone":"Calm","designSystem":"none","voice":{"mode":"narrated"},"sourceFiles":[{"role":"video","fileIds":["8c1d1c0e-0000-4000-8000-000000000001"]}],"notes":"hi"}""");

        error.Should().BeNull();
        brief!.Tone.Should().Be(StyleTone.Calm);
        brief.Voice!.Mode.Should().Be(RunBriefVoiceMode.Narrated);
        brief.SourceFiles![0].Role.Should().Be(RunBriefFileRole.Video);
        RunBriefValidator.Validate(brief).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"lenghtSec":30}""")]
    [InlineData("""{"voice":{"mode":"narrated","accent":"x"}}""")]
    [InlineData("""{"sourceFiles":[{"role":"video","fileIds":[],"extra":1}]}""")]
    [InlineData("""{"tone":"Grumpy"}""")]
    [InlineData("""{"lengthSec":"long"}""")]
    [InlineData("""{"voice":{"mode":"shouting"}}""")]
    public void Unknown_fields_and_values_are_a_plain_error_not_an_exception(string json)
    {
        (RunBrief? brief, string? error) = Parse(json);

        brief.Should().BeNull();
        error.Should().Contain("not recognised");
    }

    [Fact]
    public void A_brief_that_is_not_an_object_is_refused()
    {
        Parse("[1]").Error.Should().Contain("must be an object");
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData("IT", "it")]
    [InlineData("es-mx", "es-MX")]
    [InlineData("zh-hant-tw", "zh-Hant-TW")]
    [InlineData("es-419", "es-419")]
    [InlineData("tlh", "tlh")]
    [InlineData("de-de-1996", "de-DE-1996")]
    [InlineData("zh-cmn-hans-cn", "zh-cmn-Hans-CN")]
    [InlineData("en-us-u-ca-gregory", "en-US-u-ca-gregory")]
    [InlineData("en-x-Private", "en-x-private")]
    [InlineData("sl-rozaj-biske", "sl-rozaj-biske")]
    public void Language_tags_of_the_right_shape_are_accepted_and_canonicalised(string input, string expected)
    {
        RunBriefValidator.TryNormalizeLanguage(input, out string normalized).Should().BeTrue();
        normalized.Should().Be(expected);
        RunBriefValidator.Validate(new RunBrief(Language: input, SourceLanguage: input)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("i")]
    [InlineData("italian language")]
    [InlineData("it_IT")]
    [InlineData("it-")]
    [InlineData("12")]
    [InlineData("it-IT-ex")]
    [InlineData("it-IT-x")]
    [InlineData("abcdefghijklmnopqrstuvwxyz-0123456789-abcdefgh")]
    public void Language_tags_of_the_wrong_shape_are_refused_in_plain_words(string input)
    {
        RunBriefValidator.Validate(new RunBrief(Language: input)).Should().Contain("not a language tag");
        RunBriefValidator.Validate(new RunBrief(SourceLanguage: input)).Should().Contain("sourceLanguage");
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(600, true)]
    [InlineData(4, false)]
    [InlineData(601, false)]
    [InlineData(0, false)]
    [InlineData(-30, false)]
    public void Length_is_5_to_600_seconds(int length, bool valid)
    {
        string? error = RunBriefValidator.Validate(new RunBrief(LengthSec: length));

        (error is null).Should().Be(valid);
        if (!valid) error.Should().Contain("between 5 and 600");
    }

    [Theory]
    [InlineData("project", true)]
    [InlineData("none", true)]
    [InlineData("None", true)]
    [InlineData("0b9a5d8e-0000-4000-8000-000000000001", false)]
    [InlineData("brand", false)]
    public void Design_system_is_project_or_none_in_v1(string value, bool valid)
    {
        string? error = RunBriefValidator.Validate(new RunBrief(DesignSystem: value));

        (error is null).Should().Be(valid);
    }

    [Fact]
    public void Voice_rules()
    {
        Guid id = Guid.NewGuid();
        RunBriefValidator.Validate(new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.None))).Should().BeNull();
        RunBriefValidator.Validate(new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, ProjectVoiceId: id))).Should().BeNull();
        RunBriefValidator.Validate(new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, id, Guid.NewGuid()))).Should().Contain("one voice");
        RunBriefValidator.Validate(new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.None, LibraryVoiceId: id))).Should().Contain("narrated");
        RunBriefValidator.Validate(new RunBrief(Voice: new RunBriefVoice(null, LibraryVoiceId: id))).Should().Contain("narrated");
        RunBriefValidator.Validate(new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, Guid.Empty))).Should().Contain("real ids");
    }

    [Fact]
    public void Source_file_rules()
    {
        Guid a = Guid.NewGuid();
        RunBriefSourceFiles Video(params Guid[] ids) => new(RunBriefFileRole.Video, ids);

        RunBriefValidator.Validate(new RunBrief(SourceFiles: [Video(a), new(RunBriefFileRole.Music, [a])])).Should().BeNull();
        RunBriefValidator.Validate(new RunBrief(SourceFiles: [Video(a), Video(Guid.NewGuid())])).Should().Contain("more than once");
        RunBriefValidator.Validate(new RunBrief(SourceFiles: [new(null, [a])])).Should().Contain("needs a role");
        RunBriefValidator.Validate(new RunBrief(SourceFiles: [new(RunBriefFileRole.Code, [])])).Should().Contain("at least one file id");
        RunBriefValidator.Validate(new RunBrief(SourceFiles: [Video(Guid.Empty)])).Should().Contain("real ids");

        var tooMany = new List<Guid>();
        for (int i = 0; i <= RunBriefValidator.MaxFilesPerRole; i++) tooMany.Add(Guid.NewGuid());
        RunBriefValidator.Validate(new RunBrief(SourceFiles: [Video([.. tooMany])])).Should().Contain("per role");
    }

    [Fact]
    public void A_legacy_member_and_the_same_brief_field_is_a_conflict()
    {
        Guid id = Guid.NewGuid();
        RunBriefValidator.FindLegacyConflict(new RunBrief(StyleId: id), new LegacyRunInputs(StyleId: id)).Should().Contain("styleId");
        RunBriefValidator.FindLegacyConflict(new RunBrief(StylePreset: "a"), new LegacyRunInputs(StylePresetKey: "b")).Should().Contain("stylePreset");
        RunBriefValidator.FindLegacyConflict(new RunBrief(Style: new StyleConfig(Tone: StyleTone.Calm)), new LegacyRunInputs(Style: new StyleConfig())).Should().Contain("'style'");
        RunBriefValidator.FindLegacyConflict(new RunBrief(Format: "Vertical"), new LegacyRunInputs(OutputFormat: "Square")).Should().Contain("format");
        RunBriefValidator.FindLegacyConflict(new RunBrief(Resolution: "720p"), new LegacyRunInputs(OutputResolution: "1080p")).Should().Contain("resolution");
        RunBriefValidator.FindLegacyConflict(new RunBrief(Notes: "x"), new LegacyRunInputs(UserRequest: "y")).Should().Contain("notes");

        // Different fields, or one side blank, are no conflict.
        RunBriefValidator.FindLegacyConflict(new RunBrief(Format: "Vertical"), new LegacyRunInputs(OutputResolution: "720p", UserRequest: "y")).Should().BeNull();
        RunBriefValidator.FindLegacyConflict(new RunBrief(Format: "Vertical"), new LegacyRunInputs(OutputFormat: " ")).Should().BeNull();
    }

    [Fact]
    public void Tone_folds_into_a_one_axis_Custom_style_or_overrides_the_presets_axis()
    {
        RunStyleSnapshot? custom = RunBriefSplitter.FoldTone(null, StyleTone.Calm);
        custom!.Name.Should().Be("Custom");
        custom.Config.Should().Be(new StyleConfig(Tone: StyleTone.Calm));

        var preset = new RunStyleSnapshot("Fast", "fast-social-reel", null, new StyleConfig(Pacing: StylePacing.Fast, Tone: StyleTone.Energetic));
        RunStyleSnapshot? folded = RunBriefSplitter.FoldTone(preset, StyleTone.Serious);
        folded!.Config.Tone.Should().Be(StyleTone.Serious);
        folded.Config.Pacing.Should().Be(StylePacing.Fast);
        folded.PresetKey.Should().Be("fast-social-reel");

        RunBriefSplitter.FoldTone(preset, null).Should().BeSameAs(preset);
    }

    [Fact]
    public void Only_the_stored_fields_are_stored_normalised_and_an_empty_result_is_no_column()
    {
        RunBriefSplitter.ToStored(new RunBrief(Format: "Vertical", Tone: StyleTone.Calm, DesignSystem: "project", Notes: "n")).Should().BeNull();

        StoredRunBrief stored = RunBriefSplitter.ToStored(new RunBrief(
            Language: "ES-mx", SourceLanguage: "en", LengthSec: 30, DesignSystem: "None",
            Voice: new RunBriefVoice(RunBriefVoiceMode.None)))!;

        stored.Language.Should().Be("es-MX");
        stored.SourceLanguage.Should().Be("en");
        stored.DesignSystem.Should().Be("none");
        stored.Voice!.Mode.Should().Be(RunBriefVoiceMode.None);
        RunBriefJson.Serialize(stored).Should().Be(
            """{"version":1,"language":"es-MX","sourceLanguage":"en","lengthSec":30,"designSystem":"none","voice":{"mode":"None"}}""");
        RunBriefJson.TryParse(RunBriefJson.Serialize(stored)).Should().BeEquivalentTo(stored);
        RunBriefJson.TryParse("{not json").Should().BeNull();
    }

    [Fact]
    public void The_composed_view_reads_style_format_and_resolution_back_from_their_columns()
    {
        string style = StyleJson.Serialize(new RunStyleSnapshot("Fast", "fast-social-reel", null, new StyleConfig(Tone: StyleTone.Upbeat)));
        string stored = RunBriefJson.Serialize(new StoredRunBrief(Language: "it", LengthSec: 40));

        RunBrief view = RunBriefView.Compose(stored, style, RunOutputFormat.Vertical, 1080)!;

        view.Language.Should().Be("it");
        view.LengthSec.Should().Be(40);
        view.Format.Should().Be("Vertical");
        view.Resolution.Should().Be("1080p");
        view.StylePreset.Should().Be("fast-social-reel");
        view.Style.Should().BeNull();
        view.Tone.Should().Be(StyleTone.Upbeat);

        RunBriefView.Compose(null, null, null, null).Should().BeNull();
    }

    [Fact]
    public void The_view_echoes_the_overlay_card_audio_choice()
    {
        RunBrief view = RunBriefView.Compose("""{"version":1,"overlayCardAudio":"mute"}""", null, null, null)!;

        view.OverlayCardAudio.Should().Be("mute");
    }

    [Fact]
    public void The_model_binder_shape_also_refuses_unknown_fields()
    {
        // ASP.NET binds the request with these web defaults; the type's attribute makes an unknown member an error there too.
        Action act = () => JsonSerializer.Deserialize<RunBrief>("""{"langauge":"it"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void The_insert_content_role_takes_exactly_one_file()
    {
        (RunBrief? one, string? e1) = Parse("""{"sourceFiles":[{"role":"insertContent","fileIds":["8c1d1c0e-0000-4000-8000-000000000001"]}]}""");
        e1.Should().BeNull();
        one!.SourceFiles![0].Role.Should().Be(RunBriefFileRole.InsertContent);
        RunBriefValidator.Validate(one).Should().BeNull();

        (RunBrief? two, _) = Parse("""{"sourceFiles":[{"role":"insertContent","fileIds":["8c1d1c0e-0000-4000-8000-000000000001","8c1d1c0e-0000-4000-8000-000000000002"]}]}""");
        RunBriefValidator.Validate(two!).Should().Contain("exactly one file");
    }

    [Fact]
    public void The_overlay_role_takes_at_most_four_files()
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        (RunBrief? four, string? error) = Parse(
            """{"sourceFiles":[{"role":"overlay","fileIds":["8c1d1c0e-0000-4000-8000-000000000001","8c1d1c0e-0000-4000-8000-000000000002","8c1d1c0e-0000-4000-8000-000000000003","8c1d1c0e-0000-4000-8000-000000000004"]}]}""");
        error.Should().BeNull();
        four!.SourceFiles![0].Role.Should().Be(RunBriefFileRole.Overlay);
        RunBriefValidator.Validate(four).Should().BeNull();

        RunBrief five = new(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Overlay, [.. ids, Guid.NewGuid()])]);
        RunBriefValidator.Validate(five).Should().Contain("at most 4");
    }

    [Theory]
    [InlineData("agent", true)]
    [InlineData("keep", true)]
    [InlineData("mute", true)]
    [InlineData("KEEP", true)]
    [InlineData("loud", false)]
    [InlineData("", true)]
    public void Overlay_card_audio_is_agent_keep_or_mute(string value, bool valid)
    {
        string? error = RunBriefValidator.Validate(new RunBrief(OverlayCardAudio: value));

        (error is null).Should().Be(valid);
        if (!valid) error.Should().Contain("overlayCardAudio");
    }

    [Fact]
    public void Overlay_card_audio_round_trips_through_the_stored_brief()
    {
        RunBriefSplitter.ToStored(new RunBrief(OverlayCardAudio: "MUTE"))!.OverlayCardAudio.Should().Be("mute");
        StoredRunBrief stored = RunBriefSplitter.ToStored(new RunBrief(OverlayCardAudio: "keep"))!;
        RunBriefJson.TryParse(RunBriefJson.Serialize(stored))!.OverlayCardAudio.Should().Be("keep");
    }
}
