using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-206: the PinInsert step's config shape and its save-time rules.</summary>
public sealed class PinInsertStepConfigTests
{
    private static PinInsertStepConfig Parse(string json) =>
        JsonSerializer.Deserialize<PinInsertStepConfig>(json, PinInsertStepConfig.JsonOptions)!;

    [Fact]
    public void The_documented_example_parses_and_requires_applied_by_default()
    {
        PinInsertStepConfig config = Parse("""
            {"analysis":{"from":"Step","stepOrder":1},
             "pins":[{"regionId":"r0","content":{"from":"Step","stepOrder":3}},
                     {"content":{"from":"ProjectFile","projectFileId":"8f6a7d1e-1c2b-4d5e-9f00-1234567890ab"}}]}
            """);

        config.Validate().Should().BeEmpty();
        config.RequireApplied.Should().BeTrue();
        config.Analysis.From.Should().Be(ExtractInputSource.Step);
        config.Pins.Should().HaveCount(2);
        config.Pins[1].RegionId.Should().BeNull("an empty region means every offered screen");
    }

    [Theory]
    [InlineData("""{"analysis":{"from":"Step"},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""", "positive stepOrder")]
    [InlineData("""{"analysis":{"from":"Accumulated"},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""", "from=Step")]
    [InlineData("""{"analysis":{"from":"Previous"},"pins":[]}""", "at least one")]
    [InlineData("""{"analysis":{"from":"Previous"},"pins":[{"regionId":"screen","content":{"from":"Step","stepOrder":2}}]}""", "not an insert region id")]
    [InlineData("""{"analysis":{"from":"Previous"},"pins":[{"regionId":"r0","content":{"from":"Step","stepOrder":2}},{"regionId":"r0","content":{"from":"Step","stepOrder":3}}]}""", "pinned twice")]
    [InlineData("""{"analysis":{"from":"Previous"},"pins":[{"content":{"from":"Step","stepOrder":2}},{"content":{"from":"Step","stepOrder":3}}]}""", "At most one pin")]
    [InlineData("""{"analysis":{"from":"Previous"},"pins":[{"regionId":"r1"}]}""", "content is required")]
    [InlineData("""{"analysis":{"from":"Previous"},"pins":[{"regionId":"r1","content":{"from":"Step","stepOrder":2,"stepResultId":"8f6a7d1e-1c2b-4d5e-9f00-1234567890ab"}}]}""", "nothing else")]
    public void Invalid_configs_say_why(string json, string expected) =>
        Parse(json).Validate().Should().Contain(e => e.Contains(expected));

    [Fact]
    public void Too_many_pins_are_refused()
    {
        string pins = string.Join(",", Enumerable.Range(0, PinInsertStepConfig.MaxPins + 1)
            .Select(i => "{\"regionId\":\"r" + i + "\",\"content\":{\"from\":\"Step\",\"stepOrder\":2}}"));

        Parse("{\"analysis\":{\"from\":\"Previous\"},\"pins\":[" + pins + "]}").Validate()
            .Should().Contain(e => e.Contains($"at most {PinInsertStepConfig.MaxPins}"));
    }
}
