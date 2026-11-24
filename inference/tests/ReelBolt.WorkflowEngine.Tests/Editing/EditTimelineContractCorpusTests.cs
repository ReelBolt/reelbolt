using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>
/// The shared golden contract corpus, C# side.
///
/// <c>web/lib/editing/__fixtures__/contract/*.json</c> is ONE fixture set run by two runners: the
/// vitest <c>contract.test.ts</c> against the TS engine's <c>assertInvariants</c>, and this one
/// against what the server's PATCH enforces — the body binding through
/// <see cref="EditTimelineJson.Options"/> (a <see cref="JsonException"/> is the 400 the client
/// gets) followed by <see cref="EditTimelineValidator.Validate"/>. The files are read from the
/// source tree (located by walking up from the test output directory), never copied, so the two
/// runners cannot drift onto different copies.
///
/// Each fixture states the contract's verdict. When a <c>knownDrift</c> marker names the
/// <c>cs</c> side, the runner asserts the drift STILL exists, so the change fixing it has to
/// delete the marker.
///
/// The validation context owns every file and step result the document references for the
/// document's own project (what <c>BuildValidationContextAsync</c> resolves for an honest
/// same-project document), with the asset durations the fixture's optional <c>sourceDurations</c> names (none when absent, which the validator reads as unchecked). Cross-tenant
/// ownership is covered by <c>EditTimelineValidatorTests</c>, not here.
/// </summary>
public class EditTimelineContractCorpusTests
{
    private const string FixtureRelativeDirectory = "web/lib/editing/__fixtures__/contract";

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(FixtureDirectory(), "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Fact]
    public void Corpus_is_present_and_well_formed()
    {
        string[] files = Directory.GetFiles(FixtureDirectory(), "*.json");
        files.Length.Should().BeGreaterThanOrEqualTo(11);

        foreach (string file in files)
        {
            JsonObject fixture = Load(Path.GetFileNameWithoutExtension(file));
            fixture["name"]!.GetValue<string>().Should().Be(Path.GetFileNameWithoutExtension(file));
            fixture["description"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            fixture["expected"]!["valid"]!.GetValueKind().Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
            fixture["document"].Should().BeOfType<JsonObject>();
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Server_verdict_matches_the_contract(string name)
    {
        JsonObject fixture = Load(name);
        bool expectedValid = fixture["expected"]!["valid"]!.GetValue<bool>();
        string? expectedPath = fixture["expected"]!["errorPath"]?.GetValue<string>();
        string? fixedBy = CsDriftFixedBy(fixture);

        (bool valid, IReadOnlyList<string> paths, string detail) = ServerVerdict(fixture["document"]!.ToJsonString(), SourceDurations(fixture), SegmentationTracks(fixture));

        if (fixedBy is not null)
        {
            valid.Should().NotBe(
                expectedValid,
                $"knownDrift says the server still disagrees with the contract (fixed by {fixedBy}); it now agrees, so delete the cs knownDrift marker. Server said: {detail}");
            return;
        }

        valid.Should().Be(expectedValid, $"the contract verdict for '{name}' is valid={expectedValid}; server said: {detail}");
        if (!valid && expectedPath is not null && paths.Count > 0)
        {
            // A binding failure names the object it could not bind; the contract may name the member inside it.
            paths.Any(p => p == expectedPath || (detail.StartsWith("binding failed", StringComparison.Ordinal) && expectedPath!.StartsWith(p + ".", StringComparison.Ordinal)))
                .Should().BeTrue($"the server's violations were: {detail}");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Unchecked_clips_match_the_contract(string name)
    {
        JsonObject fixture = Load(name);
        if (fixture["unchecked"] is not JsonArray expected) return;

        EditTimeline document = JsonSerializer.Deserialize<EditTimeline>(fixture["document"]!.ToJsonString(), EditTimelineJson.Options)!;
        EditTimelineValidator.UncheckedMediaClips(document, SourceDurations(fixture))
            .Should().Equal(expected.Select(n => n!.GetValue<string>()));
    }

    /// <summary>
    /// R01.6 (additive fixture field): <c>sourceDurations</c> — the known length in frames of the sources the
    /// document names, <c>[{projectFileId|stepResultId, frames}]</c>. Absent means no duration is known.
    /// </summary>
    private static Dictionary<AssetRef, long> SourceDurations(JsonObject fixture)
    {
        Dictionary<AssetRef, long> durations = [];
        if (fixture["sourceDurations"] is not JsonArray entries) return durations;

        foreach (JsonNode? entry in entries)
        {
            long frames = entry!["frames"]!.GetValue<long>();
            if (entry["projectFileId"] is { } file)
                durations[new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: file.GetValue<Guid>())] = frames;
            else if (entry["stepResultId"] is { } result)
                durations[new AssetRef(AssetRefKind.StepResult, StepResultId: result.GetValue<Guid>())] = frames;
        }

        return durations;
    }

    /// <summary>
    /// Additive fixture field <c>segmentationTracks</c>: <c>[{stepResultId, projectId?, trackIds}]</c>. Only entries of the
    /// document's own project (no <c>projectId</c>) reach the validator, as the real context carries same-project
    /// VideoAnalyze results only; a foreign entry is therefore absent, exactly like a missing one.
    /// </summary>
    private static Dictionary<Guid, IReadOnlySet<string>> SegmentationTracks(JsonObject fixture)
    {
        Dictionary<Guid, IReadOnlySet<string>> tracks = [];
        if (fixture["segmentationTracks"] is not JsonArray entries) return tracks;

        foreach (JsonNode? entry in entries)
        {
            if (entry!["projectId"] is not null) continue;
            tracks[entry["stepResultId"]!.GetValue<Guid>()] = entry["trackIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        }

        return tracks;
    }

    private static (bool Valid, IReadOnlyList<string> Paths, string Detail) ServerVerdict(string documentJson, IReadOnlyDictionary<AssetRef, long> sourceDurations, IReadOnlyDictionary<Guid, IReadOnlySet<string>>? segmentationTracks = null)
    {
        EditTimeline? document;
        try
        {
            document = JsonSerializer.Deserialize<EditTimeline>(documentJson, EditTimelineJson.Options);
        }
        catch (JsonException ex)
        {
            string? path = ex.Path is { Length: > 2 } p && p.StartsWith("$.", StringComparison.Ordinal) ? p[2..] : null;
            return (false, path is null ? [] : [path], $"binding failed at {ex.Path}: {ex.Message}");
        }

        if (document is null)
        {
            return (false, [], "the document bound to null");
        }

        Dictionary<Guid, Guid> fileOwners = [];
        Dictionary<Guid, Guid> stepResultOwners = [];
        foreach (AssetRef? reference in TimelineAssetReferences.MediaSources(document).Concat(TimelineAssetReferences.LutFiles(document)))
        {
            if (reference?.ProjectFileId is { } fileId)
            {
                fileOwners[fileId] = document.ProjectId;
            }

            if (reference?.StepResultId is { } stepResultId)
            {
                stepResultOwners[stepResultId] = document.ProjectId;
            }
        }

        foreach (Guid stepResultId in TimelineAssetReferences.ProvenanceStepResultIds(document))
        {
            stepResultOwners[stepResultId] = document.ProjectId;
        }

        var context = new EditTimelineValidationContext(document.ProjectId, sourceDurations, fileOwners, stepResultOwners, FramesPerSecond: document.Settings?.Rate is { Den: > 0 } r ? (double)r.Num / r.Den : null, SegmentationTrackIds: segmentationTracks);
        ValidationResult result = EditTimelineValidator.Validate(document, context);
        return (
            result.IsValid,
            result.Violations.Select(v => v.Path).ToList(),
            result.IsValid ? "valid" : string.Join("; ", result.Violations.Select(v => $"{v.Path}: {v.Message}")));
    }

    private static string? CsDriftFixedBy(JsonObject fixture)
    {
        // Read in place, never re-parented: a marker is one object or an array of them.
        IEnumerable<JsonNode?> drifts = fixture["knownDrift"] switch
        {
            JsonArray array => array.ToList(),
            JsonObject single => new List<JsonNode?> { single },
            _ => new List<JsonNode?>(),
        };

        return drifts
            .Where(d => d?["side"]?.GetValue<string>() == "cs")
            .Select(d => d!["fixedBy"]?.GetValue<string>() ?? "an unnamed ticket")
            .FirstOrDefault();
    }

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory(), name + ".json")))!.AsObject();

    private static string FixtureDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, FixtureRelativeDirectory);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"could not find {FixtureRelativeDirectory} above {AppContext.BaseDirectory}; the corpus is read from the source tree");
    }
}
