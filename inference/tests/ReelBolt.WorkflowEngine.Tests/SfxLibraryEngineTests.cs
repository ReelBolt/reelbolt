using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>A hand-written library: the enabled entries and the bytes the trusted lookup would read for them.</summary>
internal sealed class FakeSfxLibraryLookup : ISfxLibraryLookup
{
    public List<SfxLibraryEntry> Entries { get; } = [];
    public Dictionary<Guid, byte[]> Bytes { get; } = [];

    public FakeSfxLibraryLookup Add(string slug, string category, string[] tags, bool enabled = true, string? key = null)
    {
        var entry = new SfxLibraryEntry(
            Guid.NewGuid(), slug, slug.Replace('-', ' '), category, tags, 400, 40_000, "audio/wav",
            new string('a', 64), key ?? SfxLibraryKeys.KeyFor(slug));
        if (enabled)
        {
            Entries.Add(entry);
            Bytes[entry.Id] = [0, 1, 2];
        }

        LastAdded = entry;
        return this;
    }

    public SfxLibraryEntry LastAdded { get; private set; } = null!;

    public Task<IReadOnlyList<SfxLibraryEntry>> ListEnabledAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SfxLibraryEntry>>(Entries);

    public Task<SfxLibraryEntry?> FindEnabledAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Entries.FirstOrDefault(e => e.Id == id && SfxLibraryKeys.IsLibraryKey(e.StorageKey)));

    public Task<byte[]?> ReadBytesAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Entries.Any(e => e.Id == id && SfxLibraryKeys.IsLibraryKey(e.StorageKey)) && Bytes.TryGetValue(id, out byte[]? b) ? b : null);

    public IServiceScopeFactory Scopes() =>
        new ServiceCollection().AddSingleton<ISfxLibraryLookup>(this).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
}

public partial class VideoAnalyzeStepExecutorTests
{
    private static async Task<JsonDocument> RunSfxOfferAsync(
        Func<VideoAnalyzeStepConfig, VideoAnalyzeStepConfig> configure, FakeSfxLibraryLookup? library, params string[] projectAudio)
    {
        StepExecutionContext context = CreateContext(
            out Mock<IProjectFileWorkspace> workspace,
            out Mock<IMediaProbe> probe,
            out Mock<ISilenceDetector> silence,
            out Mock<IShotDetector> shotDetector,
            out _, out _,
            configOverride: configure);
        SetupBasicProbeAndDetectors(probe, silence, shotDetector);
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectAudio.Select((n, i) =>
                new ProjectWorkspaceFile(Guid.NewGuid(), ProjectId, n, null, "userFiles", "k" + i, "audio/wav", 100, DateTime.UtcNow, null)).ToList());

        StepExecutionResult result = await CreateExecutor(
            workspace, probe, silence, shotDetector, new Mock<ITranscriptionClientFactory>(), new Mock<IInferenceProviderResolver>(),
            scopeFactory: library?.Scopes()).ExecuteAsync(context);
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        return JsonDocument.Parse(result.Output);
    }

    [Fact]
    public async Task IncludeSfxLibrary_offers_library_sounds_first_with_category_and_tags_then_project_files()
    {
        FakeSfxLibraryLookup library = new FakeSfxLibraryLookup()
            .Add("pop-soft", "pop", ["pop", "appear"])
            .Add("whoosh-short-a", "whoosh", ["whoosh", "transition"]);
        Guid popId = library.Entries[0].Id;

        using JsonDocument doc = await RunSfxOfferAsync(
            c => c with { OfferSfxClips = true, IncludeSfxLibrary = true }, library, "voice-drop.wav");

        JsonElement clips = doc.RootElement.GetProperty("view").GetProperty("sfxClips");
        clips.GetArrayLength().Should().Be(3);
        clips[0].GetProperty("id").GetString().Should().Be("x0");
        clips[0].GetProperty("category").GetString().Should().Be("pop");
        clips[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().BeEquivalentTo("pop", "appear");
        clips[1].GetProperty("id").GetString().Should().Be("x1");
        clips[1].GetProperty("category").GetString().Should().Be("whoosh");
        clips[2].GetProperty("id").GetString().Should().Be("x2");
        clips[2].GetProperty("name").GetString().Should().Be("voice-drop.wav");
        clips[2].TryGetProperty("category", out _).Should().BeFalse("a project file carries no library words");
        doc.RootElement.GetProperty("meta").GetProperty("offeredSfxClipCount").GetInt32().Should().Be(3);
        doc.RootElement.GetRawText().Should().NotContain("platform/", "no storage key ever reaches the view");
        popId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task IncludeSfxLibrary_off_is_byte_identical_even_with_a_library_present()
    {
        FakeSfxLibraryLookup library = new FakeSfxLibraryLookup().Add("pop-soft", "pop", ["pop"]);

        using JsonDocument without = await RunSfxOfferAsync(c => c with { OfferSfxClips = true }, null, "a.wav", "b.wav");
        using JsonDocument with = await RunSfxOfferAsync(c => c with { OfferSfxClips = true }, library, "a.wav", "b.wav");

        with.RootElement.GetProperty("view").GetRawText().Should().Be(without.RootElement.GetProperty("view").GetRawText());
        with.RootElement.GetProperty("meta").GetProperty("offeredSfxClipCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task IncludeSfxLibrary_without_OfferSfxClips_offers_nothing()
    {
        FakeSfxLibraryLookup library = new FakeSfxLibraryLookup().Add("pop-soft", "pop", ["pop"]);

        using JsonDocument doc = await RunSfxOfferAsync(c => c with { IncludeSfxLibrary = true }, library, "a.wav");

        doc.RootElement.GetProperty("view").TryGetProperty("sfxClips", out _).Should().BeFalse();
    }

    [Fact]
    public async Task IncludeSfxLibrary_caps_library_offers_and_leaves_the_project_file_cap_alone()
    {
        FakeSfxLibraryLookup library = new();
        for (int i = 0; i < 75; i++)
            library.Add($"item-{i:D3}", "pop", ["pop"]);

        using JsonDocument doc = await RunSfxOfferAsync(
            c => c with { OfferSfxClips = true, IncludeSfxLibrary = true, MaxSfxClips = 1 }, library, "a.wav", "b.wav");

        // Exactly the library cap plus the one project file its own cap allows; the library comes first.
        doc.RootElement.GetProperty("meta").GetProperty("offeredSfxClipCount").GetInt32().Should().Be(SfxLibraryOffers.MaxLibraryOffers + 1);
        JsonElement clips = doc.RootElement.GetProperty("view").GetProperty("sfxClips");
        clips.GetArrayLength().Should().Be(SfxLibraryOffers.MaxLibraryOffers + 1);
        clips.EnumerateArray().Take(SfxLibraryOffers.MaxLibraryOffers).Select(c => c.TryGetProperty("category", out JsonElement category) && category.GetString() == "pop")
            .Should().AllBeEquivalentTo(true);
        clips[SfxLibraryOffers.MaxLibraryOffers].GetProperty("name").GetString().Should().Be("a.wav");
    }

    [Fact]
    public async Task IncludeSfxLibrary_with_an_unreachable_library_degrades_to_the_project_files()
    {
        using JsonDocument doc = await RunSfxOfferAsync(
            c => c with { OfferSfxClips = true, IncludeSfxLibrary = true }, null, "a.wav");

        doc.RootElement.GetProperty("view").GetProperty("sfxClips").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void The_footage_reader_lists_library_words_for_a_library_clip_only()
    {
        VideoAnalysisArtifact artifact = (VideoAnalysisArtifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VideoAnalysisArtifact));
        artifact = artifact with
        {
            SfxCandidates =
            [
                new("x0", Guid.Empty, "Pop soft", "audio/wav", 2048, Guid.NewGuid(), "pop", ["pop", "appear"]),
                new("x1", Guid.NewGuid(), "mine.wav", "audio/wav", 2048)
            ]
        };

        using JsonDocument doc = JsonDocument.Parse(VideoFootageReader.ListSfxClips(artifact, null));
        JsonElement items = doc.RootElement.GetProperty("sfxClips");
        items[0].GetProperty("category").GetString().Should().Be("pop");
        items[0].GetProperty("tags").GetArrayLength().Should().Be(2);
        items[1].TryGetProperty("category", out _).Should().BeFalse();
        items[1].TryGetProperty("tags", out _).Should().BeFalse();
    }

    [Fact]
    public void A_library_candidate_serializes_its_item_id_and_a_project_candidate_does_not()
    {
        string library = JsonSerializer.Serialize(new VideoAnalysisSfxCandidate("x0", Guid.Empty, "n", "audio/wav", 1, Guid.NewGuid(), "pop", ["pop"]));
        string project = JsonSerializer.Serialize(new VideoAnalysisSfxCandidate("x0", Guid.NewGuid(), "n", "audio/wav", 1));

        library.Should().Contain("LibraryItemId");
        project.Should().NotContain("LibraryItemId").And.NotContain("Category").And.NotContain("Tags");
    }
}

public class SfxLibraryCacheTests
{
    [Fact]
    public void A_large_library_is_sampled_across_categories_in_library_order_up_to_the_cap()
    {
        FakeSfxLibraryLookup lib = new();
        for (int i = 0; i < 80; i++) lib.Add($"pop-{i:D2}", "pop", ["pop"]);
        for (int i = 0; i < 5; i++) lib.Add($"riser-{i}", "riser", ["riser"]);
        for (int i = 0; i < 5; i++) lib.Add($"stamp-{i}", "stamp", ["stamp"]);

        IReadOnlyList<SfxLibraryEntry> picked = SfxLibraryOffers.Pick(lib.Entries);

        picked.Count.Should().Be(SfxLibraryOffers.MaxLibraryOffers);
        picked.Count(e => e.Category == "riser").Should().Be(5);
        picked.Count(e => e.Category == "stamp").Should().Be(5);
        picked.Select(e => lib.Entries.IndexOf(e)).Should().BeInAscendingOrder();
        SfxLibraryOffers.Pick(lib.Entries.Take(10).ToList()).Count.Should().Be(10);
    }

    [Fact]
    public void The_library_digest_changes_with_content_and_ignores_order()
    {
        FakeSfxLibraryLookup a = new FakeSfxLibraryLookup().Add("one", "pop", ["pop"]).Add("two", "click", ["click"]);
        string digest = SfxLibraryDigest.Of(a.Entries);

        SfxLibraryDigest.Of(a.Entries.AsEnumerable().Reverse()).Should().Be(digest);
        SfxLibraryDigest.Of(a.Entries.Take(1)).Should().NotBe(digest, "a taken-down sound changes the key");
        SfxLibraryDigest.Of(a.Entries.Select(e => e with { Sha256 = new string('b', 64) })).Should().NotBe(digest, "changed bytes change the key");
    }

    [Fact]
    public void The_cache_key_gains_the_digest_only_when_it_is_set()
    {
        StepCacheKeyInputs baseline = new()
        {
            ProjectId = Guid.NewGuid(),
            StepType = StepType.VideoAnalyze,
            AgentType = AgentType.Custom,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow,
            MaxIterations = 1,
            ProjectFileFingerprint = "fp"
        };
        StepCacheKeyBuilder builder = new();

        builder.Build(baseline with { SfxLibraryDigest = null }).Should().Be(builder.Build(baseline));
        builder.Build(baseline with { SfxLibraryDigest = "abc" }).Should().NotBe(builder.Build(baseline));
        builder.Build(baseline with { SfxLibraryDigest = "abc" }).Should().NotBe(builder.Build(baseline with { SfxLibraryDigest = "abd" }));
    }

    [Fact]
    public void The_analyze_revision_gains_a_suffix_only_for_a_library_offering_config()
    {
        string plain = DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"offerSfxClips":true}""");
        string library = DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"offerSfxClips":true,"includeSfxLibrary":true}""");
        string flagWithoutOffer = DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"includeSfxLibrary":true}""");

        library.Should().Contain("+sfx-library-1");
        plain.Should().NotContain("sfx-library");
        flagWithoutOffer.Should().NotContain("sfx-library");
    }
}

public partial class VideoCompileStepExecutorTests
{
    private static VideoAnalysisArtifact WithLibraryCandidate(VideoAnalysisArtifact artifact, Guid libraryItemId) =>
        artifact with
        {
            SfxCandidates = [new VideoAnalysisSfxCandidate("x0", Guid.Empty, "Whoosh short", "audio/wav", 4096, libraryItemId, "whoosh", ["transition"])],
            OfferedSfxIds = ["x0"]
        };

    private async Task<(StepExecutionResult Result, JsonElement Edl, Mock<IProjectFileWorkspace> Workspace)> RunLibraryCueAsync(
        FakeSfxLibraryLookup? library, Guid candidateItemId)
    {
        VideoAnalysisArtifact artifact = WithLibraryCandidate(
            BuildArtifact(shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) }, offeredIds: new[] { "s0", "s1" }), candidateItemId);
        StepExecutionContext context = CreateSfxContext(
            artifact, BuildDecisionJson(("s0", "s1", "keep all")), BuildSfxPlanJson(("x0", "s1", "OnCut", "Normal")),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, scopeFactory: library?.Scopes()).ExecuteAsync(context);
        return (result, edl, workspace);
    }

    [Fact]
    public async Task A_library_cue_is_resolved_through_the_trusted_lookup_and_mixed_like_a_project_clip()
    {
        FakeSfxLibraryLookup library = new FakeSfxLibraryLookup().Add("whoosh-short-a", "whoosh", ["transition"]);

        (StepExecutionResult result, JsonElement edl, Mock<IProjectFileWorkspace> workspace) = await RunLibraryCueAsync(library, library.LastAdded.Id);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("applied").GetBoolean().Should().BeTrue();
        sfx.GetProperty("appliedCueCount").GetInt32().Should().Be(1);
        sfx.GetProperty("dropped").GetArrayLength().Should().Be(0);
        sfx.GetProperty("cues")[0].GetProperty("outputStartSec").GetDouble().Should().BeApproximately(4.0, 0.01);
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.Is<string>(k => k.StartsWith(SfxLibraryKeys.Prefix, StringComparison.Ordinal)),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a platform key never goes through the project-scoped download");
    }

    [Fact]
    public async Task A_library_cue_naming_an_unknown_or_disabled_item_is_dropped()
    {
        FakeSfxLibraryLookup library = new();
        library.Add("taken-down", "whoosh", ["transition"], enabled: false);
        Guid disabledId = library.LastAdded.Id;

        foreach (Guid id in new[] { disabledId, Guid.NewGuid() })
        {
            (StepExecutionResult result, JsonElement edl, _) = await RunLibraryCueAsync(library, id);

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            edl.GetProperty("sfx").GetProperty("applied").GetBoolean().Should().BeFalse();
            edl.GetProperty("sfx").GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("sfx_library_item_unavailable");
        }
    }

    [Fact]
    public async Task A_library_cue_is_dropped_when_no_library_is_reachable()
    {
        (StepExecutionResult result, JsonElement edl, _) = await RunLibraryCueAsync(null, Guid.NewGuid());

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("sfx").GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("sfx_library_item_unavailable");
    }

    [Fact]
    public async Task A_library_item_whose_key_is_outside_the_platform_prefix_never_resolves()
    {
        FakeSfxLibraryLookup library = new FakeSfxLibraryLookup().Add("evil", "whoosh", ["transition"], key: "projects/someone-else/userFiles/secret.wav");

        (StepExecutionResult result, JsonElement edl, _) = await RunLibraryCueAsync(library, library.LastAdded.Id);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("sfx").GetProperty("applied").GetBoolean().Should().BeFalse();
    }
}
