using System.Linq;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Agents.Translation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Customer finding 50 (2026-10-04): the 15 s vertical reel drew literal markdown on screen
/// ("Build the edit as a *flowchart.*"). <see cref="OnScreenCopySanitizer"/> strips it
/// deterministically from the Scriptwriter/Director outputs before any downstream step reads them,
/// and the render tool's scene declaration rejects any that still reaches a component.
/// </summary>
public class OnScreenCopySanitizerTests
{
    [Theory]
    [InlineData("Build the edit as a *flowchart.*", "Build the edit as a flowchart.")]
    [InlineData("Watch it land on the *timeline.*", "Watch it land on the timeline.")]
    [InlineData("**Ship faster** with *zero* setup", "Ship faster with zero setup")]
    [InlineData("***Bold italic*** copy", "Bold italic copy")]
    [InlineData("Meet _ReelBolt_ and __friends__", "Meet ReelBolt and friends")]
    [InlineData("Run `npm start` today", "Run npm start today")]
    [InlineData("# Launch day", "Launch day")]
    [InlineData("### Three steps", "Three steps")]
    public void Strips_markdown_emphasis_and_keeps_the_words(string input, string expected)
    {
        OnScreenCopySanitizer.Strip(input).Should().Be(expected);
        OnScreenCopySanitizer.ContainsMarkdown(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("Plain copy, nothing to do.")]
    [InlineData("2 * 3 = 6")]
    [InlineData("a*b*c")]
    [InlineData("*Terms apply")]
    [InlineData("Save 20%*")]
    [InlineData("src/**/*.tsx")]
    [InlineData("snake_case_name")]
    [InlineData("#1 choice for editors")]
    [InlineData("Brand colour #ff0000")]
    [InlineData("* not emphasis *")]
    [InlineData("")]
    public void Leaves_ordinary_copy_untouched(string input)
    {
        OnScreenCopySanitizer.Strip(input).Should().Be(input);
        OnScreenCopySanitizer.ContainsMarkdown(input).Should().BeFalse();
    }

    [Fact]
    public void Scriptwriter_output_loses_markdown_in_copy_fields_but_keeps_ids()
    {
        const string output = """
            {"title":"**ReelBolt**","durationInSeconds":15,"narrative":"Vertical 9:16, 15 s.","scenes":[
              {"sceneId":"scene_1_hook_","startTime":0,"duration":5,"voiceover":"Meet *ReelBolt*.","onScreenText":["Build the edit as a *flowchart.*","Plain"],"visualDescription":"Zoom into the `builder` panel"}]}
            """;

        string cleaned = ScriptwriterAgentImpl.SanitizeOutput(output);
        using JsonDocument doc = JsonDocument.Parse(cleaned);
        JsonElement scene = doc.RootElement.GetProperty("scenes")[0];

        doc.RootElement.GetProperty("title").GetString().Should().Be("ReelBolt");
        scene.GetProperty("onScreenText")[0].GetString().Should().Be("Build the edit as a flowchart.");
        scene.GetProperty("onScreenText")[1].GetString().Should().Be("Plain");
        scene.GetProperty("voiceover").GetString().Should().Be("Meet ReelBolt.");
        scene.GetProperty("visualDescription").GetString().Should().Be("Zoom into the builder panel");
        scene.GetProperty("sceneId").GetString().Should().Be("scene_1_hook_", "ids are not copy and are never rewritten");
        scene.GetProperty("duration").GetInt32().Should().Be(5);
    }

    [Fact]
    public void Field_names_match_case_insensitively_because_models_emit_either_casing()
    {
        string cleaned = ScriptwriterAgentImpl.SanitizeOutput("""{"Scenes":[{"OnScreenText":["*Fast*"]}]}""");
        cleaned.Should().Contain("\"Fast\"").And.NotContain("*");
    }

    [Fact]
    public void A_clean_or_unparsable_output_is_returned_byte_identical()
    {
        const string clean = "{ \"title\" : \"Plain\",\n  \"scenes\": [] }";
        ScriptwriterAgentImpl.SanitizeOutput(clean).Should().BeSameAs(clean);

        const string notJson = "```json\n{\"title\":\"*x*\"\n```";
        ScriptwriterAgentImpl.SanitizeOutput(notJson).Should().BeSameAs(notJson);
    }

    [Fact]
    public void Director_output_loses_markdown_in_shot_descriptions_and_visual_elements()
    {
        const string output = """
            {"shots":[{"shotId":"s_1_","sceneId":"intro","description":"Text overlay: **Ship faster**","visualElements":["*CTA* button"],"camera":{"angle":"close-up","movement":"zoom-in","focus":"builder"}}],"totalDurationInSeconds":15}
            """;

        using JsonDocument doc = JsonDocument.Parse(DirectorAgentImpl.SanitizeOutput(output));
        JsonElement shot = doc.RootElement.GetProperty("shots")[0];
        shot.GetProperty("description").GetString().Should().Be("Text overlay: Ship faster");
        shot.GetProperty("visualElements")[0].GetString().Should().Be("CTA button");
        shot.GetProperty("shotId").GetString().Should().Be("s_1_");
    }

    [Fact]
    public void Non_ascii_copy_survives_a_rewrite_unescaped()
    {
        string cleaned = ScriptwriterAgentImpl.SanitizeOutput("""{"title":"*Café* — prêt"}""");
        cleaned.Should().Be("""{"title":"Café — prêt"}""");
    }

    [Fact]
    public void Render_declaration_with_markdown_on_screen_is_rejected_with_the_clean_text()
    {
        var (manifest, errors) = CompositionSceneManifest.FromDeclaration("FinalVideo", 30,
        [
            new DeclaredScene("Hook", 0, 150, "The builder", ["Build the edit as a *flowchart.*"])
        ]);

        manifest.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("markdown") && e.Contains("\"Build the edit as a flowchart.\""));
    }

    [Fact]
    public void Render_declaration_without_markdown_is_still_accepted()
    {
        var (manifest, errors) = CompositionSceneManifest.FromDeclaration("FinalVideo", 30,
        [
            new DeclaredScene("Hook", 0, 150, "The builder", ["Build the edit as a flowchart."])
        ]);

        errors.Should().BeEmpty();
        manifest.Should().NotBeNull();
    }

    // ---------------------------------------------------------------- prompt contracts

    private static string Prompt(System.Type agentType) =>
        (string)agentType.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void Promo_prompts_read_format_and_length_from_the_brief_and_forbid_markdown_and_invented_people()
    {
        foreach (System.Type agent in new[] { typeof(ScriptwriterAgentImpl), typeof(DirectorAgentImpl), typeof(AuthorAgentImpl), typeof(RemotionComponentTranslatorAgent) })
        {
            string prompt = Prompt(agent);
            prompt.Should().Contain("User Request", agent.Name);
            prompt.Should().Contain("1080x1920", $"{agent.Name} must default a reel to vertical");
            prompt.Should().Contain("\"9:16\"", agent.Name);
            prompt.Should().Contain("markdown", agent.Name);
            prompt.Should().Contain("Welcome back", $"{agent.Name} must replace invented personas with neutral copy");
        }

        foreach (System.Type agent in new[] { typeof(DirectorAgentImpl), typeof(AuthorAgentImpl), typeof(RemotionComponentTranslatorAgent) })
            Prompt(agent).Should().Contain("48 px", $"{agent.Name} must size on-screen text for the target screen");
    }

    [Fact]
    public void Author_prompt_requires_an_audio_note_instead_of_silently_shipping_mute()
    {
        string prompt = Prompt(typeof(AuthorAgentImpl));
        prompt.Should().Contain("metadata.audio");
        prompt.Should().Contain("No audio: this workflow has no narration step");
        prompt.Should().Contain("StageVoiceoverAudio");
    }

    [Fact]
    public void Story_first_prompts_tell_the_writer_to_lead_and_the_editor_to_follow_the_script_order()
    {
        Prompt(typeof(NarrationWriterAgent)).Should().Contain("## Story first");
        string editor = Prompt(typeof(VideoStoryEditorAgent));
        editor.Should().Contain("## When the narration script came first");
        editor.Should().Contain("in the script's order");
    }

    [Fact]
    public void Music_and_sfx_prompts_say_why_there_is_nothing_to_hear()
    {
        Prompt(typeof(MusicSupervisorAgent)).Should().Contain("No music: this project has no audio files to choose");
        Prompt(typeof(SoundDesignerAgent)).Should().Contain("No sound effects: this project has no sound-effect audio");
    }

    [Fact]
    public void Video_review_prompt_reads_dropped_narration_and_the_warnings_list()
    {
        string prompt = Prompt(typeof(ReelBolt.WorkflowEngine.Agents.Quality.VideoReviewAgentImpl));
        prompt.Should().Contain("`voiceover.dropped`");
        prompt.Should().Contain("narration_mostly_dropped");
    }

    [Fact]
    public void Every_copy_field_list_is_lower_camel_case_to_document_the_schema_names()
    {
        ScriptwriterAgentImpl.CopyFields.Concat(DirectorAgentImpl.CopyFields)
            .Should().OnlyContain(f => char.IsLower(f[0]));
    }
}
