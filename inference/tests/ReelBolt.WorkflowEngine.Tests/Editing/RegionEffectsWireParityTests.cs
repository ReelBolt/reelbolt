using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>
/// Wire parity: a region-effect entry must carry effect, strength, mask and enabled, exactly as the schema and the
/// web engine (`regionEffectsProblem`) require; the server must not fill a missing member with a default.
/// </summary>
public class RegionEffectsWireParityTests
{
    private static readonly string FixtureDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "web", "lib", "editing", "__fixtures__", "contract"));

    private static bool ServerAccepts(string dropMember)
    {
        JsonNode fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir, "region-effects-accepted.json")))!;
        JsonObject entry = fixture["document"]!["tracks"]![0]!["items"]![0]!["regionEffects"]![0]!.AsObject();
        entry.Remove(dropMember);
        EditTimeline document;
        try
        {
            document = JsonSerializer.Deserialize<EditTimeline>(fixture["document"]!.ToJsonString(), EditTimelineJson.Options)!;
        }
        catch (JsonException)
        {
            return false;
        }

        Dictionary<Guid, IReadOnlySet<string>> tracks = [];
        foreach (JsonNode? t in fixture["segmentationTracks"]!.AsArray())
        {
            tracks[t!["stepResultId"]!.GetValue<Guid>()] = t["trackIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        }

        Dictionary<Guid, Guid> fileOwners = [];
        Dictionary<Guid, Guid> stepOwners = [];
        foreach (AssetRef? reference in TimelineAssetReferences.MediaSources(document))
        {
            if (reference?.ProjectFileId is { } f) fileOwners[f] = document.ProjectId;
            if (reference?.StepResultId is { } s) stepOwners[s] = document.ProjectId;
        }

        foreach (Guid s in TimelineAssetReferences.ProvenanceStepResultIds(document)) stepOwners[s] = document.ProjectId;
        var context = new EditTimelineValidationContext(document.ProjectId, new Dictionary<AssetRef, long>(), fileOwners, stepOwners,
            FramesPerSecond: document.Settings?.Rate is { Den: > 0 } r ? (double)r.Num / r.Den : null, SegmentationTrackIds: tracks);
        return EditTimelineValidator.Validate(document, context).IsValid;
    }

    [Fact]
    public void Baseline_fixture_is_accepted() => ServerAccepts("__none__").Should().BeTrue();

    [Theory]
    [InlineData("effect")]
    [InlineData("strength")]
    [InlineData("enabled")]
    [InlineData("mask")]
    public void A_region_effect_missing_a_member_is_refused(string member) =>
        ServerAccepts(member).Should().BeFalse($"{member} is required on the wire");
}
