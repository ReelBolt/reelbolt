using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

public class ChangeRequestValidatorTests
{
    private const string Output = """{"view":{"lines":[{"id":"vo0","status":"ok","durationSec":2.5,"text":"Hello"},{"id":"vo1","status":"failed"},{"nope":1}]}}""";

    private static ChangeRequestPayload P(string json) => ChangeRequestPayload.TryParse(json)!;

    private static string[] Paths(string json, string? output = Output) =>
        ChangeRequestValidator.Validate(StepType.Voiceover, P(json), output).Select(v => v.Path).ToArray();

    [Fact]
    public void ListItems_reads_voiceover_lines_leniently()
    {
        var items = StepChangeCapabilities.ListItems(StepType.Voiceover, Output);
        items.Select(i => i.Id).Should().Equal("vo0", "vo1");
        items[0].Should().Be(new StepItem("vo0", "ok", 2.5, "Hello"));
        items[1].Text.Should().BeNull();
        foreach (string? garbage in new string?[] { null, "", "not json", "[]", "{\"view\":3}", "{\"view\":{\"lines\":{}}}" })
            StepChangeCapabilities.ListItems(StepType.Voiceover, garbage).Should().BeEmpty();
        StepChangeCapabilities.ListItems(StepType.Agent, Output).Should().BeEmpty();
    }

    [Fact]
    public void A_valid_request_has_no_violations()
    {
        Paths("""{"itemIds":["vo0"],"overrides":{"vo0":{"text":"Hi","speed":1.2,"voiceId":"v1","pronunciation":[{"term":"Kubernetes","spelling":"Kubernètes"}]},"*":{"speed":1}}}""")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"overrides":{"vo0":{"colour":"red"}}}""", "overrides.vo0.colour")]
    [InlineData("""{"overrides":{"vo0":{"speed":"fast"}}}""", "overrides.vo0.speed")]
    [InlineData("""{"overrides":{"vo0":{"speed":2.5}}}""", "overrides.vo0.speed")]
    [InlineData("""{"overrides":{"vo0":{"speed":0.4}}}""", "overrides.vo0.speed")]
    [InlineData("""{"overrides":{"vo9":{"speed":1}}}""", "overrides.vo9")]
    [InlineData("""{"itemIds":["vo9"]}""", "itemIds[0]")]
    [InlineData("""{"overrides":{"vo0":{"text":5}}}""", "overrides.vo0.text")]
    [InlineData("""{"overrides":{"vo0":{"pronunciation":[{"term":"ab","spelling":"<script>"}]}}}""", "overrides.vo0.pronunciation[0].spelling")]
    [InlineData("""{"instruction":"a\u0007b"}""", "instruction")]
    [InlineData("""{}""", "")]
    public void Violations_are_named(string json, string path) => Paths(json).Should().Contain(path);

    [Fact]
    public void Limits_are_enforced()
    {
        Paths(JsonSerializer.Serialize(new { instruction = new string('x', 2001) })).Should().Contain("instruction");
        Paths(JsonSerializer.Serialize(new { itemIds = Enumerable.Range(0, 65).Select(i => $"vo{i}") })).Should().Contain("itemIds");
        var pron = Enumerable.Range(0, 17).Select(_ => new { term = "ab", spelling = "aa" });
        Paths(JsonSerializer.Serialize(new { overrides = new { vo0 = new { pronunciation = pron } } })).Should().Contain("overrides.vo0.pronunciation");
    }

    [Fact]
    public void Unsupported_types_are_refused_with_their_reason()
    {
        var v = ChangeRequestValidator.Validate(StepType.EditRoom, P("""{"instruction":"x"}"""), null);
        v.Should().ContainSingle().Which.Message.Should().Be("not built yet");
    }

    [Fact]
    public void Canonical_form_ignores_ordering()
    {
        P("""{"instruction":" x ","itemIds":["b","a","a"],"overrides":{"b":{"y":1,"x":{"q":1,"p":2}},"a":{"z":"1"}}}""")
            .CanonicalEquals(P("""{"overrides":{"a":{"z":"1"},"b":{"x":{"p":2,"q":1},"y":1}},"itemIds":["a","b"],"instruction":"x"}""")).Should().BeTrue();
        P("""{"instruction":"x"}""").CanonicalEquals(P("""{"instruction":"y"}""")).Should().BeFalse();
        ChangeRequestPayload.TryParse("garbage").Should().BeNull();
    }

    [Fact]
    public void An_instruction_alone_changes_nothing_so_it_is_refused()
    {
        Paths("""{"instruction":"make it calmer","itemIds":["vo0"]}""").Should().Contain("overrides");
    }

    [Fact]
    public void A_hostile_request_gets_a_bounded_answer()
    {
        var overrides = Enumerable.Range(0, 64).ToDictionary(i => $"x{i}", _ => new { bad1 = 1, bad2 = 2, bad3 = 3 });
        Paths(JsonSerializer.Serialize(new { overrides })).Length.Should().BeLessThanOrEqualTo(50);
    }

    [Fact]
    public void A_script_instruction_without_a_named_scene_is_refused_even_next_to_overrides()
    {
        const string script = """{"scenes":[{"sceneId":"sc1","voiceover":"One."}]}""";
        var noItems = ChangeRequestValidator.Validate(StepType.Agent, AgentType.ScriptwriterAgent, P("""{"instruction":"calmer","overrides":{"sc1":{"voiceover":"Two."}}}"""), script);
        noItems.Should().ContainSingle(v => v.Path == "itemIds" && v.Message.Contains("name the scene"));
        ChangeRequestValidator.Validate(StepType.Agent, AgentType.ScriptwriterAgent, P("""{"instruction":"calmer","itemIds":["sc1"]}"""), script).Should().BeEmpty();
    }
}
