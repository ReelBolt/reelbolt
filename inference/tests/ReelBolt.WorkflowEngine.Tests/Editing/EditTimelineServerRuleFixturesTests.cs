using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>
/// T-012: the validator rules the audit found missing (d1 N1 schemaVersion pin, d1 N4 negative
/// <c>recordIn</c>, d1 N5 picture item on an audio track), beyond what the shared contract corpus
/// (<c>web/lib/editing/__fixtures__/contract/*.json</c>, run by <see cref="EditTimelineContractCorpusTests"/>)
/// already pins: the missing-field default, sequences, every picture-only item type, and the media clip
/// that stays legal on an audio track.
/// </summary>
public class EditTimelineServerRuleFixturesTests
{
    [Fact]
    public void AMissingSchemaVersionReadsAs2AndIsValid()
    {
        string json = File.ReadAllText(Path.Combine(ContractDirectory(), "title-on-video-track.json"));
        using JsonDocument fixture = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> fields = fixture.RootElement.GetProperty("document")
            .EnumerateObject().Where(f => f.Name != "schemaVersion").ToDictionary(f => f.Name, f => f.Value.Clone());

        EditTimeline document = JsonSerializer.Deserialize<EditTimeline>(JsonSerializer.Serialize(fields), EditTimelineJson.Options)!;

        document.SchemaVersion.Should().Be(2);
        EditTimelineValidator.Validate(document, EmptyContext(document.ProjectId)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-2)]
    public void AnySchemaVersionOtherThan2IsRefusedAtItsPath(int schemaVersion)
    {
        EditTimeline document = Blank() with { SchemaVersion = schemaVersion };

        ValidationResult result = EditTimelineValidator.Validate(document, EmptyContext(document.ProjectId));

        result.Violations.Should().ContainSingle(v => v.Path == "schemaVersion");
    }

    [Fact]
    public void ANegativeRecordInInsideASequenceIsRefusedToo()
    {
        EditTimeline blank = Blank();
        EditTimeline document = new(
            blank.Id, blank.ProjectId, blank.Name, blank.Settings, blank.Tracks, blank.Markers,
            sequences:
            [
                new EditSequence(Guid.NewGuid(), "Nested",
                [
                    new Track(Guid.NewGuid(), TrackKind.Video, "V", null, 100, false, true, false, false, null,
                        [new TitleClip(Guid.NewGuid(), "T", -1, 30, "Hi")])
                ])
            ]);

        ValidationResult result = EditTimelineValidator.Validate(document, EmptyContext(document.ProjectId));

        result.Violations.Should().Contain(v => v.Path == "sequences[0].tracks[0].items[0].recordIn");
    }

    [Fact]
    public void AMediaClipOnAnAudioTrackIsStillValid_ItIsTheFilesSound()
    {
        // OTIO import and the seeder put media clips on audio tracks (a music bed, the audio of a
        // camera file); the kind rule is about picture-only items, not about media clips.
        Guid fileId = Guid.NewGuid();
        EditTimeline blank = Blank();
        AssetRef asset = new(AssetRefKind.ProjectFile, ProjectFileId: fileId);
        EditTimeline document = blank with
        {
            Tracks =
            [
                blank.Tracks[0],
                blank.Tracks[1] with { Items = [new MediaClip(Guid.NewGuid(), "Bed", 0, 90, asset)] }
            ]
        };
        EditTimelineValidationContext context = new(
            document.ProjectId,
            new Dictionary<AssetRef, long>(),
            new Dictionary<Guid, Guid> { [fileId] = document.ProjectId },
            new Dictionary<Guid, Guid>());

        EditTimelineValidator.Validate(document, context).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("titleClip")]
    [InlineData("caption")]
    [InlineData("insertClip")]
    [InlineData("adjustmentClip")]
    public void EveryPictureOnlyItemTypeIsRefusedOnAnAudioTrack(string type)
    {
        Item item = type switch
        {
            "titleClip" => new TitleClip(Guid.NewGuid(), "Title", 0, 30, "Hi"),
            "caption" => new Caption(Guid.NewGuid(), "Cap", 0, 30, "Hi"),
            "insertClip" => new InsertClip(Guid.NewGuid(), "Insert", 0, 30),
            _ => new AdjustmentClip(Guid.NewGuid(), "Adjust", 0, 30)
        };
        EditTimeline blank = Blank();
        EditTimeline document = blank with { Tracks = [blank.Tracks[0], blank.Tracks[1] with { Items = [item] }] };

        ValidationResult result = EditTimelineValidator.Validate(document, EmptyContext(document.ProjectId));

        result.Violations.Should().Contain(v => v.Path == "tracks[1].items[0]", Describe(result));
    }

    private static EditTimeline Blank()
    {
        Guid projectId = Guid.NewGuid();
        return new EditTimeline(
            id: Guid.NewGuid(),
            projectId: projectId,
            name: "Rules",
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks:
            [
                new Track(EditTimelineSeeder.TrackId("V1"), TrackKind.Video, "Video", null, 100, false, true, false, false, null, Array.Empty<Item>()),
                new Track(EditTimelineSeeder.TrackId("A1"), TrackKind.Audio, "Audio", null, 100, false, true, false, false, null, Array.Empty<Item>())
            ],
            markers: Array.Empty<Marker>());
    }

    private static EditTimelineValidationContext EmptyContext(Guid projectId) =>
        new(projectId, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid>(), new Dictionary<Guid, Guid>());

    private static string Describe(ValidationResult result) =>
        "violations: " + string.Join("; ", result.Violations.Select(v => $"{v.Path}: {v.Message}"));

    private static string ContractDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "lib", "editing", "__fixtures__", "contract")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        dir.Should().NotBeNull("the test must locate web/lib/editing/__fixtures__/contract from the repo root");
        return Path.Combine(dir!, "web", "lib", "editing", "__fixtures__", "contract");
    }
}
