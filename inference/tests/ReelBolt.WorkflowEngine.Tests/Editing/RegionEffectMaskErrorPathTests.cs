using System;
using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>
/// A binding failure inside a region effect's mask names the region effect that holds the mask,
/// not the document root; a converter cannot name the member any finer.
/// </summary>
public class RegionEffectMaskErrorPathTests
{
    private static readonly string FixtureDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "web", "lib", "editing", "__fixtures__", "contract"));

    [Theory]
    [InlineData("region-effects-mask-missing-invert-refused.json")]
    [InlineData("region-effects-mask-missing-feather-refused.json")]
    [InlineData("region-effects-missing-strength-refused.json")]
    public void Binding_failure_names_the_region_effect_path(string file)
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, file)));
        string document = fixture.RootElement.GetProperty("document").GetRawText();
        JsonException ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EditTimeline>(document, EditTimelineJson.Options));
        ex.Path.Should().StartWith("$.tracks[0].items[0].regionEffects[0]", $"message: {ex.Message}");
    }
}
