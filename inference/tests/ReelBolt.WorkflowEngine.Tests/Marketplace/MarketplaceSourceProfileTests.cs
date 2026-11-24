using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplaceSourceProfileTests
{
    private static (JsonObject Config, WalkResult Walk) Walk(System.Type type, string profilesJson)
    {
        WalkResult walk = new();
        JsonNode? cleaned = ConfigWalker.Walk(JsonNode.Parse($"{{\"sourceInputProfiles\":{profilesJson}}}"), type, 1, walk);
        return ((JsonObject)cleaned!, walk);
    }

    [Theory]
    [InlineData(typeof(VideoCompileStepConfig))]
    [InlineData(typeof(VideoAnalyzeStepConfig))]
    public void Known_profiles_and_auto_are_kept(System.Type type)
    {
        string known = CameraProfiles.Ids[0];
        (JsonObject config, WalkResult walk) = Walk(type, $"{{\"0\":\"{known}\",\"3\":\"auto\"}}");

        JsonObject profiles = config["sourceInputProfiles"]!.AsObject();
        profiles["0"]!.GetValue<string>().Should().Be(known);
        profiles["3"]!.GetValue<string>().Should().Be("auto");
        walk.Removed.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_values_and_odd_keys_are_dropped_and_recorded()
    {
        string known = CameraProfiles.Ids[0];
        (JsonObject config, WalkResult walk) = Walk(typeof(VideoCompileStepConfig),
            $"{{\"0\":\"{known}\",\"1\":\"not-a-camera\",\"-2\":\"auto\",\"x\":\"auto\",\"5000\":\"auto\",\"2\":\"11111111-2222-3333-4444-555555555555\",\"4\":7}}");

        config["sourceInputProfiles"]!.AsObject().Select(kv => kv.Key).Should().Equal("0");
        walk.Removed.Count(r => r.Field.EndsWith("sourceInputProfiles", System.StringComparison.Ordinal)).Should().Be(6);
    }
}
