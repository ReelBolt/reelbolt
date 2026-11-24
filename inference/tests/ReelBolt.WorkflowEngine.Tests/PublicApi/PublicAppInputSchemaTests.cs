using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

public class PublicAppInputSchemaTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Build(params AppManifestInput[] inputs) =>
        PublicAppInputSchema.Build(new AppManifest(Inputs: inputs));

    private static AppManifestInput In(string id, string type, bool? required = null, string? label = null, string? help = null) =>
        new("input", Id: id, Type: type, Required: required, Label: label, Help: help);

    [Fact]
    public void Empty_inputs_give_an_empty_closed_object()
    {
        JsonElement s = Build();
        s.GetProperty("$schema").GetString().Should().Be(PublicAppInputSchema.Dialect);
        s.GetProperty("type").GetString().Should().Be("object");
        s.GetProperty("properties").EnumerateObject().Should().BeEmpty();
        s.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        s.TryGetProperty("required", out _).Should().BeFalse();
    }

    [Fact]
    public void Text_inputs_carry_title_description_default_and_the_type_cap()
    {
        JsonElement s = Build(
            In("topic", "shortText", true, "Topic", "What it is about") with { Default = J("\"cats\"") },
            In("script", "longText") with { MaxLength = 500 });
        JsonElement topic = s.GetProperty("properties").GetProperty("topic");
        topic.GetProperty("type").GetString().Should().Be("string");
        topic.GetProperty("maxLength").GetInt32().Should().Be(AppInputTypes.MaxShortText);
        topic.GetProperty("title").GetString().Should().Be("Topic");
        topic.GetProperty("description").GetString().Should().Be("What it is about");
        topic.GetProperty("default").GetString().Should().Be("cats");
        s.GetProperty("properties").GetProperty("script").GetProperty("maxLength").GetInt32().Should().Be(500);
        s.TryGetProperty("required", out _).Should().BeFalse("topic has a default, so it is never missing");
    }

    [Fact]
    public void Number_and_duration_map_to_number_and_integer_with_limits()
    {
        JsonElement s = Build(
            In("level", "number") with { Min = 0, Max = 10, Step = 2 },
            In("secs", "duration") with { Max = 60 });
        JsonElement level = s.GetProperty("properties").GetProperty("level");
        level.GetProperty("type").GetString().Should().Be("number");
        level.GetProperty("minimum").GetDouble().Should().Be(0);
        level.GetProperty("maximum").GetDouble().Should().Be(10);
        level.GetProperty("multipleOf").GetDouble().Should().Be(2);
        JsonElement secs = s.GetProperty("properties").GetProperty("secs");
        secs.GetProperty("type").GetString().Should().Be("integer");
        secs.GetProperty("minimum").GetDouble().Should().Be(1);
        secs.GetProperty("maximum").GetDouble().Should().Be(60);
    }

    [Fact]
    public void Choice_toggle_shape_and_model_map_to_enum_boolean_enum_and_uuid()
    {
        JsonElement s = Build(
            In("tone", "choice") with { Options = [new AppInputOption(J("\"warm\""), "Warm"), new AppInputOption(J("\"cool\""))] },
            In("loud", "toggle"),
            In("shape", "shape"),
            In("model", "model"));
        JsonElement p = s.GetProperty("properties");
        p.GetProperty("tone").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("warm", "cool");
        p.GetProperty("loud").GetProperty("type").GetString().Should().Be("boolean");
        p.GetProperty("shape").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal(AppInputTypes.Shapes);
        p.GetProperty("model").GetProperty("format").GetString().Should().Be("uuid");
    }

    [Fact]
    public void Non_input_kinds_are_left_out()
    {
        JsonElement s = PublicAppInputSchema.Build(new AppManifest(Inputs: [new AppManifestInput("brief", Field: "topic"), In("a", "toggle")]));
        s.GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Equal("a");
    }

    private static IEnumerable<string> Required(JsonElement s) =>
        s.TryGetProperty("required", out JsonElement r) ? r.EnumerateArray().Select(e => e.GetString()!) : [];

    [Fact]
    public void Required_lists_only_inputs_the_checker_would_report_missing()
    {
        AppManifest m = new(Inputs:
        [
            In("plain", "shortText", true),
            In("withDefault", "shortText", true) with { Default = J("\"x\"") },
            In("conditional", "shortText", true) with { ShowWhen = new AppInputShowWhen("plain", J("\"a\"")) },
            In("flag", "toggle", true),
            In("egress", "toggle", true) with { Permission = "mediaEgress" }
        ]);
        Required(PublicAppInputSchema.Build(m)).Should().Equal("plain");
        AppInputValueChecker.Check(m, [], new Dictionary<string, JsonElement>()).Problems.Select(p => p.InputId).Should().Equal("plain");
    }

    [Fact]
    public void A_permission_never_gets_a_default()
    {
        JsonElement s = Build(In("egress", "toggle") with { Permission = "mediaEgress", Default = J("true") });
        s.GetProperty("properties").GetProperty("egress").TryGetProperty("default", out _).Should().BeFalse();
        AppInputValueChecker.Check(new AppManifest(Inputs: [In("egress", "toggle") with { Permission = "mediaEgress", Default = J("true") }]), [],
            new Dictionary<string, JsonElement>()).Values.Should().NotContainKey("egress");
    }

    [Fact]
    public void An_unknown_type_is_omitted_and_a_model_lists_no_allowed_ids()
    {
        JsonElement s = Build(In("weird", "hologram", true), In("m", "model") with { Allowed = [System.Guid.NewGuid()] });
        s.GetProperty("properties").TryGetProperty("weird", out _).Should().BeFalse();
        Required(s).Should().BeEmpty();
        s.GetProperty("properties").GetProperty("m").TryGetProperty("enum", out _).Should().BeFalse();
    }

    [Fact]
    public void Max_length_never_goes_negative_and_multiple_of_needs_a_whole_step_dividing_the_minimum()
    {
        JsonElement s = Build(
            In("t", "shortText") with { MaxLength = -5 },
            In("a", "number") with { Min = 0.1, Step = 0.1 },
            In("b", "number") with { Step = 2 },
            In("c", "number") with { Min = 1, Step = 2 });
        JsonElement p = s.GetProperty("properties");
        p.GetProperty("t").GetProperty("maxLength").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        p.GetProperty("a").TryGetProperty("multipleOf", out _).Should().BeFalse();
        p.GetProperty("b").TryGetProperty("multipleOf", out _).Should().BeFalse();
        p.GetProperty("c").TryGetProperty("multipleOf", out _).Should().BeFalse();
    }
}
