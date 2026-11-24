using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A stored timeline comes back from PostgreSQL <c>jsonb</c> with its keys ordered by length, so an
/// item's <c>type</c> discriminator is never first; the browser sends items as <c>{ id, type, … }</c>.
/// Every reader of a document must accept that, or a timeline with one clip stops loading.
/// </summary>
public class EditTimelineJsonTests
{
    private static string WithDiscriminatorLast(string json)
    {
        JsonNode root = JsonNode.Parse(json)!;
        foreach (JsonNode? track in root["tracks"]!.AsArray())
        {
            foreach (JsonNode? item in track!["items"]!.AsArray())
            {
                JsonObject obj = item!.AsObject();
                JsonNode? type = obj["type"];
                obj.Remove("type");
                obj.Add("type", type!.DeepClone());
            }
        }
        return root.ToJsonString();
    }

    private static EditTimeline TimelineWithOneClip()
    {
        var clip = new MediaClip(Guid.NewGuid(), "clip-a.mp4", 0, 150, new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid()));
        return new EditTimeline(
            id: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            name: "One clip",
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks: new List<Track> { new(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, new List<Item> { clip }) },
            markers: new List<Marker>());
    }

    [Fact]
    public void A_document_whose_items_carry_the_discriminator_last_still_reads()
    {
        string json = WithDiscriminatorLast(JsonSerializer.Serialize(TimelineWithOneClip(), EditTimelineJson.Options));
        json.Should().Contain("\"type\":\"mediaClip\"}");

        EditTimeline? read = JsonSerializer.Deserialize<EditTimeline>(json, EditTimelineJson.Options);

        read!.Tracks.Single().Items.Single().Should().BeOfType<MediaClip>().Which.Name.Should().Be("clip-a.mp4");
    }

    [Fact]
    public void The_default_web_options_would_have_refused_it()
    {
        string json = WithDiscriminatorLast(JsonSerializer.Serialize(TimelineWithOneClip(), EditTimelineJson.Options));

        Action read = () => JsonSerializer.Deserialize<EditTimeline>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        read.Should().Throw<NotSupportedException>();
    }
}
