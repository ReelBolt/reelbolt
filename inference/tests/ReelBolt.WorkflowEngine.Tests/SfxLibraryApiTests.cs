using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

internal static class SfxTestWav
{
    /// <summary>A silent 48 kHz mono 16-bit PCM WAV of the given length, with a canonical 44-byte header.</summary>
    public static byte[] Make(int ms, byte fill = 0)
    {
        int dataBytes = 48000 * 2 * ms / 1000;
        byte[] wav = new byte[44 + dataBytes];
        void Put(int at, string s) { for (int i = 0; i < s.Length; i++) wav[at + i] = (byte)s[i]; }
        Put(0, "RIFF");
        BitConverter.GetBytes(36 + dataBytes).CopyTo(wav, 4);
        Put(8, "WAVEfmt ");
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(48000).CopyTo(wav, 24);
        BitConverter.GetBytes(96000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Put(36, "data");
        BitConverter.GetBytes(dataBytes).CopyTo(wav, 40);
        Array.Fill(wav, fill, 44, dataBytes);
        return wav;
    }
}

internal sealed class FakeSfxRenderer : ISfxRenderer
{
    public int Renders { get; private set; }
    public int Normalizes { get; private set; }
    public bool Available { get; set; } = true;

    public Task<byte[]?> RenderAsync(SfxRecipe recipe, CancellationToken ct)
    {
        Renders++;
        return Task.FromResult<byte[]?>(Available ? SfxTestWav.Make(recipe.DurationMs, (byte)(recipe.Slug.Length % 200)) : null);
    }

    public Task<byte[]?> NormalizeAsync(byte[] source, CancellationToken ct)
    {
        Normalizes++;
        return Task.FromResult<byte[]?>(Available ? SfxTestWav.Make(500, 7) : null);
    }
}

internal sealed class FakeSfxStore : ISfxLibraryStorage
{
    public Dictionary<string, byte[]> Objects { get; } = [];
    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Objects.ContainsKey(key));
    public Task PutAsync(string key, byte[] bytes, string contentType, CancellationToken ct) { Objects[key] = bytes; return Task.CompletedTask; }
    public Task<byte[]?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(key, out byte[]? b) ? b : null);
}

internal sealed class FakeSfxFetcher(Func<Uri, byte[]?> serve) : ISfxPackFetcher
{
    public List<Uri> Requested { get; } = [];
    public Task<byte[]?> FetchAsync(Uri url, CancellationToken ct) { Requested.Add(url); return Task.FromResult(serve(url)); }
}

public class SfxSynthRecipesTests
{
    private static readonly string[] EventTags =
        ["appear", "disappear", "text_reveal", "transition", "hit", "flash", "scene_swap", "stamp", "pop"];

    [Fact]
    public void The_table_holds_a_deterministic_set_of_well_formed_recipes()
    {
        IReadOnlyList<SfxRecipe> all = SfxSynthRecipes.All;

        all.Count.Should().BeInRange(30, 60);
        all.Select(r => r.Slug).Should().OnlyHaveUniqueItems();
        all.Should().OnlyContain(r => System.Text.RegularExpressions.Regex.IsMatch(r.Slug, "^[a-z0-9]+(-[a-z0-9]+)*$"));
        all.Should().OnlyContain(r => r.DurationMs >= 150 && r.DurationMs <= 2500, "every effect lasts 0.15 to 2.5 s");
        all.Should().OnlyContain(r => r.Tags.Count > 0 && r.Tags.All(t => t == t.ToLowerInvariant()));
        all.SelectMany(r => r.Tags).Should().Contain(EventTags, "the event types the sound designer picks by must all be present");
        all.Select(r => r.Category).Distinct().Should().Contain(["pop", "click", "whoosh", "swoosh", "ding", "stamp", "impact", "shutter", "riser", "glitch", "zap", "sparkle", "text-tick", "transition"]);
    }

    [Fact]
    public void The_argv_is_built_by_first_party_code_and_is_the_same_every_time()
    {
        foreach (SfxRecipe r in SfxSynthRecipes.All)
        {
            IReadOnlyList<string> a = SfxSynthRecipes.BuildArgv(r, "/tmp/out.wav");
            IReadOnlyList<string> b = SfxSynthRecipes.BuildArgv(r, "/tmp/out.wav");
            a.Should().Equal(b);
            a.Should().Contain(["-bitexact", "-map_metadata", "pcm_s16le"]);
            a.Should().NotContain(x => x.Contains("http", StringComparison.OrdinalIgnoreCase), "synthesis reads no input from anywhere");
            a.Count(x => x == "-i").Should().Be(0, "every source is a seeded generator inside the graph");
            r.Graph.Should().Contain("[o]");
            if (r.Graph.Contains("anoisesrc", StringComparison.Ordinal))
                r.Graph.Should().Contain("seed=", "noise is seeded so the bytes are reproducible");
        }
    }

    [Fact]
    public void The_pack_normalizer_argv_trims_and_levels_without_a_shell()
    {
        IReadOnlyList<string> argv = SfxPackNormalizer.BuildArgv("/in/x.bin", "/out/y.wav");

        argv.Should().Contain(["-bitexact", "/in/x.bin", "/out/y.wav", "pcm_s16le"]);
        string af = argv[argv.ToList().IndexOf("-af") + 1];
        af.Should().Contain("silenceremove").And.Contain("loudnorm");
    }

    [FfmpegToolsFact]
    public async Task Every_recipe_really_renders_with_the_right_length_a_safe_peak_and_real_sound_and_twice_to_the_same_bytes()
    {
        FfmpegSfxRenderer renderer = new(NullLogger<FfmpegSfxRenderer>.Instance);
        foreach (SfxRecipe r in SfxSynthRecipes.All)
        {
            byte[]? first = await renderer.RenderAsync(r, CancellationToken.None);
            byte[]? second = await renderer.RenderAsync(r, CancellationToken.None);

            first.Should().NotBeNull(because: r.Slug);
            WavFacts.Sha256(first!).Should().Be(WavFacts.Sha256(second!), $"{r.Slug} must render to identical bytes");
            int duration = WavFacts.DurationMs(first!)!.Value;
            duration.Should().BeCloseTo(r.DurationMs, 30, because: r.Slug);

            (double mean, double peak) = await Volume(first!);
            peak.Should().BeLessThanOrEqualTo(-0.5, $"{r.Slug} must not clip");
            peak.Should().BeGreaterThan(-30, $"{r.Slug} must not be near-silent");
            mean.Should().BeGreaterThan(-60, $"{r.Slug} must contain sound");
        }
    }

    private static async Task<(double Mean, double Peak)> Volume(byte[] wav)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sfx-test-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(path, wav);
        try
        {
            ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in new[] { "-hide_banner", "-nostdin", "-i", path, "-af", "volumedetect", "-f", "null", "-" })
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            double Read(string key) => double.Parse(
                System.Text.RegularExpressions.Regex.Match(err, key + @": (-?[0-9.]+) dB").Groups[1].Value, CultureInfo.InvariantCulture);
            return (Read("mean_volume"), Read("max_volume"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SfxManifestTests
{
    private static string Pack(string licence = "CC0 1.0", string url = "https://example.org/a", string sha = null!, string kind = "pack", string slug = "kenney-click") => $$"""
        {"version":1,"items":[{"kind":"{{kind}}","slug":"{{slug}}","title":"Click","category":"click","tags":["click","Appear"],
        "file":"interface/click_001.ogg","source_url":"{{url}}","author":"Kenney","licence":"{{licence}}",
        "licence_url":"https://creativecommons.org/publicdomain/zero/1.0/","licence_proof":"The page states: License: Creative Commons CC0",
        "retrieved_at":"2026-11-10","source_sha256":"{{sha ?? new string('c', 64)}}"}]}
        """;

    [Fact]
    public void A_verified_cc0_pack_item_is_accepted_with_all_its_provenance()
    {
        SfxManifest m = SfxManifestReader.Parse(Pack());

        SfxPackItem item = m.Pack.Single();
        item.Licence.Should().Be("CC0 1.0");
        item.LicenceUrl.Should().StartWith("https://creativecommons.org/");
        item.LicenceProof.Should().NotBeEmpty();
        item.RetrievedAt.Year.Should().Be(2026);
        item.Tags.Should().Equal("click", "appear");
    }

    [Theory]
    [InlineData("CC-BY 4.0")]
    [InlineData("Royalty free")]
    public void Anything_that_is_not_cc0_is_refused(string licence) =>
        FluentThrow(() => SfxManifestReader.Parse(Pack(licence: licence)), "only provably CC0");

    [Fact]
    public void A_pack_item_without_a_valid_hash_or_https_source_is_refused()
    {
        FluentThrow(() => SfxManifestReader.Parse(Pack(sha: "abc")), "SHA-256");
        FluentThrow(() => SfxManifestReader.Parse(Pack(url: "http://example.org/a")), "https");
    }

    [Fact]
    public void An_unknown_kind_a_duplicate_slug_and_a_missing_proof_are_refused()
    {
        FluentThrow(() => SfxManifestReader.Parse(Pack(kind: "stock")), "kind");
        FluentThrow(() => SfxManifestReader.Parse(Pack(slug: SfxSynthRecipes.All[0].Slug)), "Duplicate");
        FluentThrow(() => SfxManifestReader.Parse(Pack().Replace("licence_proof", "note")), "licence_proof");
    }

    [Fact]
    public void A_synth_entry_must_name_a_known_recipe_and_may_switch_it_off()
    {
        string slug = SfxSynthRecipes.All[0].Slug;
        SfxManifest m = SfxManifestReader.Parse($$"""{"items":[{"kind":"synth","recipe":"{{slug}}","enabled":false}]}""");

        m.Synth.Single().Should().Be(new SfxSynthEntry(slug, false));
        FluentThrow(() => SfxManifestReader.Parse("""{"items":[{"kind":"synth","recipe":"nope"}]}"""), "unknown recipe");
    }

    [Fact]
    public void The_shipped_manifest_lists_only_items_with_complete_proof_and_a_matching_committed_file()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Services", "SfxLibrary");
        File.Exists(Path.Combine(dir, SfxLibrarySeeder.ManifestFileName)).Should().BeTrue("the manifest ships next to the assembly");

        SfxManifest m = SfxManifestReader.Parse(File.ReadAllText(Path.Combine(dir, SfxLibrarySeeder.ManifestFileName)));

        m.Pack.Should().NotBeEmpty();
        long total = 0;
        foreach (SfxPackItem p in m.Pack)
        {
            p.Licence.Should().Be("CC0 1.0", p.Slug);
            p.LicenceUrl.Should().Be("https://creativecommons.org/publicdomain/zero/1.0/", p.Slug);
            p.LicenceProof.Should().Contain("CC0", p.Slug);
            p.LicenceProof.Should().Contain("License.txt", p.Slug);
            p.SourceUrl.Should().StartWith("https://kenney.nl/assets/", p.Slug);
            p.Author.Should().NotBeNullOrWhiteSpace(p.Slug);
            p.RetrievedAt.Year.Should().BeGreaterThanOrEqualTo(2026, p.Slug);
            p.OriginalFile.Should().NotBeNullOrWhiteSpace(p.Slug);
            p.Tags.Should().NotBeEmpty(p.Slug);
            p.IsCommitted.Should().BeTrue("the shipped pack is committed, hash-pinned bytes: " + p.Slug);

            string file = Path.Combine(dir, "pack", p.File);
            File.Exists(file).Should().BeTrue(p.Slug);
            byte[] bytes = File.ReadAllBytes(file);
            total += bytes.Length;
            WavFacts.Sha256(bytes).Should().Be(p.Sha256, $"{p.Slug} must match its manifest hash");
            System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("OggS", p.Slug);
            p.MimeType.Should().Be("audio/ogg");
        }

        total.Should().BeLessThan(6 * 1024 * 1024, "committed audio stays small");
        Directory.GetFiles(Path.Combine(dir, "pack")).Length.Should().Be(m.Pack.Count, "no unlisted audio ships");
    }

    [Fact]
    public void A_committed_item_needs_exactly_one_hash_a_duration_and_an_original_file_name()
    {
        string committed = Pack().Replace("\"source_sha256\":\"" + new string('c', 64) + "\"", "\"sha256\":\"" + new string('c', 64) + "\",\"duration_ms\":300,\"mime_type\":\"audio/ogg\",\"original_file\":\"p/a.ogg\"");
        SfxManifestReader.Parse(committed).Pack.Single().IsCommitted.Should().BeTrue();

        FluentThrow(() => SfxManifestReader.Parse(committed.Replace(",\"duration_ms\":300", "")), "duration_ms");
        FluentThrow(() => SfxManifestReader.Parse(committed.Replace(",\"original_file\":\"p/a.ogg\"", "")), "original_file");
        FluentThrow(() => SfxManifestReader.Parse(committed.Replace("\"sha256\"", "\"sha256\":\"" + new string('d', 64) + "\",\"source_sha256\"")), "exactly one");
        FluentThrow(() => SfxManifestReader.Parse(committed.Replace("CC0 1.0", "CC0 1.0").Replace("\"licence_proof\":\"The page states: License: Creative Commons CC0\",", "")), "licence_proof");
    }

    private static void FluentThrow(Action act, string contains) =>
        act.Should().Throw<InvalidDataException>().Where(e => e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));
}

public class SfxLibrarySeederTests
{
    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase($"sfx-{Guid.NewGuid()}").Options);

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static Task<SfxSeedResult> Seed(
        InferenceApiDbContext db, FakeSfxRenderer r, FakeSfxStore s, IConfiguration? cfg = null, FakeSfxFetcher? f = null, string? manifest = null) =>
        SfxLibrarySeeder.SeedAsync(db, cfg ?? Config(), r, s, f ?? new FakeSfxFetcher(_ => null), NullLogger.Instance, manifestJson: manifest ?? """{"items":[]}""");

    [Fact]
    public async Task Seeding_renders_every_recipe_stores_it_under_the_platform_prefix_and_records_provenance()
    {
        using InferenceApiDbContext db = NewDb();
        FakeSfxRenderer renderer = new();
        FakeSfxStore store = new();

        SfxSeedResult result = await Seed(db, renderer, store);

        result.Rendered.Should().Be(SfxSynthRecipes.All.Count);
        List<SfxLibraryItem> rows = await db.SfxLibraryItems.OrderBy(i => i.SortOrder).ToListAsync();
        rows.Should().HaveCount(SfxSynthRecipes.All.Count);
        rows.Should().OnlyContain(i => i.StorageKey.StartsWith(SfxLibraryKeys.Prefix) && store.Objects.ContainsKey(i.StorageKey));
        rows.Should().OnlyContain(i => i.Licence == "ReelBolt-original" && i.RecipeVersion == "v1" && i.IsEnabled && i.Sha256.Length == 64);
        rows.Should().OnlyContain(i => i.SizeBytes == store.Objects[i.StorageKey].LongLength && i.Sha256 == WavFacts.Sha256(store.Objects[i.StorageKey]));
        rows.Select(i => i.DurationMs).Should().Equal(SfxSynthRecipes.All.Select(r => r.DurationMs));
        rows.Should().OnlyContain(i => i.Tags.Count > 0);
    }

    [Fact]
    public async Task Seeding_twice_renders_nothing_the_second_time_and_changes_no_ids()
    {
        using InferenceApiDbContext db = NewDb();
        FakeSfxRenderer renderer = new();
        FakeSfxStore store = new();
        await Seed(db, renderer, store);
        List<Guid> ids = await db.SfxLibraryItems.Select(i => i.Id).OrderBy(i => i).ToListAsync();
        int rendersAfterFirst = renderer.Renders;

        SfxSeedResult second = await Seed(db, renderer, store);

        second.Rendered.Should().Be(0);
        second.Unchanged.Should().Be(SfxSynthRecipes.All.Count);
        renderer.Renders.Should().Be(rendersAfterFirst);
        (await db.SfxLibraryItems.Select(i => i.Id).OrderBy(i => i).ToListAsync()).Should().Equal(ids);
    }

    [Fact]
    public async Task A_missing_object_is_rendered_again_and_a_takedown_is_never_undone()
    {
        using InferenceApiDbContext db = NewDb();
        FakeSfxRenderer renderer = new();
        FakeSfxStore store = new();
        string off = SfxSynthRecipes.All[0].Slug;
        string manifest = $$"""{"items":[{"kind":"synth","recipe":"{{off}}","enabled":false}]}""";
        await Seed(db, renderer, store, manifest: manifest);
        // A takedown made by hand in the database, on a sound the manifest still lists.
        SfxLibraryItem byHand = await db.SfxLibraryItems.FirstAsync(i => i.Slug == SfxSynthRecipes.All[2].Slug);
        byHand.IsEnabled = false;
        await db.SaveChangesAsync();
        store.Objects.Remove(SfxLibraryKeys.KeyFor(SfxSynthRecipes.All[1].Slug));

        SfxSeedResult result = await Seed(db, renderer, store, manifest: manifest);

        result.Rendered.Should().Be(1);
        (await db.SfxLibraryItems.AsNoTracking().FirstAsync(i => i.Slug == off)).IsEnabled.Should().BeFalse("the manifest takes it down on every boot");
        (await db.SfxLibraryItems.AsNoTracking().FirstAsync(i => i.Slug == byHand.Slug)).IsEnabled.Should().BeFalse("the seeder never switches a sound back on: a takedown by hand survives every boot");
    }

    [Fact]
    public async Task A_missing_or_invalid_manifest_switches_nothing_off_and_no_boot_switches_a_sound_back_on()
    {
        using InferenceApiDbContext db = NewDb();
        FakeSfxRenderer renderer = new();
        FakeSfxStore store = new();
        FakeSfxFetcher fetcher = new(_ => PackBytes);
        IConfiguration config = Config(("SfxLibrary:PackBaseUrl", "https://packs.example.org/v1/"));
        string off = SfxSynthRecipes.All[0].Slug;
        string good = PackManifest(WavFacts.Sha256(PackBytes)).Replace("{\"items\":[", $$"""{"items":[{"kind":"synth","recipe":"{{off}}","enabled":false},""");
        async Task<bool> On(string slug) => (await db.SfxLibraryItems.AsNoTracking().SingleAsync(i => i.Slug == slug)).IsEnabled;

        await Seed(db, renderer, store, config, fetcher, good);
        (await On("kenney-click")).Should().BeTrue();
        (await On(off)).Should().BeFalse();
        int total = await db.SfxLibraryItems.CountAsync();

        // One boot with an invalid manifest (one entry is not CC0): no switch moves, in either direction.
        await Seed(db, renderer, store, config, fetcher, good.Replace("CC0 1.0", "CC-BY 4.0"));
        (await On("kenney-click")).Should().BeTrue("an invalid manifest says nothing about which sounds should exist");
        (await On(off)).Should().BeFalse("and it does not undo a takedown either");

        // One boot whose manifest file is not there.
        IConfiguration missing = Config(("SfxLibrary:PackBaseUrl", "https://packs.example.org/v1/"), ("SfxLibrary:ManifestPath", Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json")));
        await SfxLibrarySeeder.SeedAsync(db, missing, renderer, store, fetcher, NullLogger.Instance);
        (await On("kenney-click")).Should().BeTrue();
        (await On(off)).Should().BeFalse();
        (await db.SfxLibraryItems.CountAsync(i => i.IsEnabled)).Should().Be(total - 1);

        // A manifest that was read and does not list the pack sound takes it out (an older release, or a takedown)...
        await Seed(db, renderer, store, config, fetcher, """{"items":[]}""");
        (await On("kenney-click")).Should().BeFalse();
        (await On(off)).Should().BeFalse("a sound that is off is never switched on by a boot");

        // ...and a later boot whose manifest lists it again keeps the row and leaves the switch alone: off is the
        // takedown, and only a person turns a sound back on.
        Guid id = (await db.SfxLibraryItems.AsNoTracking().SingleAsync(i => i.Slug == "kenney-click")).Id;
        SfxSeedResult again = await Seed(db, renderer, store, config, fetcher, good);
        again.Rendered.Should().Be(0);
        (await On("kenney-click")).Should().BeFalse();
        (await On(off)).Should().BeFalse();
        (await db.SfxLibraryItems.AsNoTracking().SingleAsync(i => i.Slug == "kenney-click")).Id.Should().Be(id);
    }

    /// <summary>Fails every save while <see cref="Failing"/> is set, the way a refused row fails one in Postgres.</summary>
    private sealed class FailingSaves : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Failing { get; set; }
        public int Refused { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Failing) return base.SavingChangesAsync(eventData, result, cancellationToken);
            Refused++;
            throw new DbUpdateException("the row was refused");
        }
    }

    [Fact]
    public async Task A_failed_save_leaves_nothing_behind_for_the_next_save_on_the_same_context()
    {
        FailingSaves saves = new() { Failing = true };
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"sfx-{Guid.NewGuid()}").AddInterceptors(saves).Options);
        FakeSfxRenderer renderer = new();
        FakeSfxStore store = new();

        SfxSeedResult failed = await Seed(db, renderer, store);

        failed.Should().Be(SfxSeedResult.Off, "the boot goes on without the library");
        saves.Refused.Should().Be(1, "the pass stops at the first refused row");
        db.ChangeTracker.Entries<SfxLibraryItem>().Should().BeEmpty("the seeders that run next share this context");

        // The next seeder's save on the same context sends none of the library's rows again.
        saves.Failing = false;
        (await db.SaveChangesAsync()).Should().Be(0);
        (await db.SfxLibraryItems.CountAsync()).Should().Be(0);

        // And the next boot seeds the whole library.
        (await Seed(db, renderer, store)).Rendered.Should().Be(SfxSynthRecipes.All.Count);
    }

    /// <summary>A store whose put fails from the given call on.</summary>
    private sealed class FailingPuts(int failFrom) : ISfxLibraryStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public int Puts { get; private set; }
        public bool Broken { get; set; } = true;
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Objects.ContainsKey(key));
        public Task<byte[]?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(key, out byte[]? b) ? b : null);

        public Task PutAsync(string key, byte[] bytes, string contentType, CancellationToken ct)
        {
            Puts++;
            if (Broken && Puts >= failFrom) throw new InvalidOperationException("storage down");
            Objects[key] = bytes;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_storage_failure_part_way_keeps_every_row_already_written_in_step_with_its_object()
    {
        using InferenceApiDbContext db = NewDb();
        FailingPuts store = new(failFrom: 4);

        SfxSeedResult result = await SfxLibrarySeeder.SeedAsync(
            db, Config(), new FakeSfxRenderer(), store, new FakeSfxFetcher(_ => null), NullLogger.Instance, manifestJson: """{"items":[]}""");

        // Each row is saved right after its object: the three written before the failure are whole, the rest are not there.
        result.Should().Be(SfxSeedResult.Off);
        db.ChangeTracker.Entries<SfxLibraryItem>().Should().BeEmpty();
        List<SfxLibraryItem> rows = await db.SfxLibraryItems.AsNoTracking().ToListAsync();
        rows.Select(r => r.Slug).Should().BeEquivalentTo(SfxSynthRecipes.All.Take(3).Select(r => r.Slug));
        rows.Should().OnlyContain(r => r.Sha256 == WavFacts.Sha256(store.Objects[r.StorageKey]));

        // The next boot renders only what is missing.
        store.Broken = false;
        SfxSeedResult next = await SfxLibrarySeeder.SeedAsync(
            db, Config(), new FakeSfxRenderer(), store, new FakeSfxFetcher(_ => null), NullLogger.Instance, manifestJson: """{"items":[]}""");
        (next.Rendered, next.Unchanged).Should().Be((SfxSynthRecipes.All.Count - 3, 3));
    }

    [Fact]
    public async Task No_ffmpeg_leaves_the_library_empty_and_is_never_an_error()
    {
        using InferenceApiDbContext db = NewDb();

        SfxSeedResult result = await Seed(db, new FakeSfxRenderer { Available = false }, new FakeSfxStore());

        result.Rendered.Should().Be(0);
        (await db.SfxLibraryItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_off_switch_seeds_nothing()
    {
        using InferenceApiDbContext db = NewDb();
        FakeSfxRenderer renderer = new();

        await Seed(db, renderer, new FakeSfxStore(), Config(("SfxLibrary:Enabled", "false")));

        renderer.Renders.Should().Be(0);
        (await db.SfxLibraryItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_broken_store_never_fails_the_boot()
    {
        using InferenceApiDbContext db = NewDb();

        SfxSeedResult result = await SfxLibrarySeeder.SeedAsync(
            db, Config(), new FakeSfxRenderer(), new ThrowingStore(), new FakeSfxFetcher(_ => null), NullLogger.Instance, manifestJson: """{"items":[]}""");

        result.Rendered.Should().Be(0);
    }

    private sealed class ThrowingStore : ISfxLibraryStorage
    {
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => throw new InvalidOperationException("storage down");
        public Task PutAsync(string key, byte[] bytes, string contentType, CancellationToken ct) => throw new InvalidOperationException("storage down");
        public Task<byte[]?> GetAsync(string key, CancellationToken ct) => throw new InvalidOperationException("storage down");
    }

    private static readonly byte[] PackBytes = [1, 2, 3, 4, 5];

    private static string PackManifest(string sha) => $$"""
        {"items":[{"kind":"pack","slug":"kenney-click","title":"Click","category":"click","tags":["click"],"file":"ui/click.ogg",
        "source_url":"https://example.org/click","author":"Kenney","licence":"CC0 1.0",
        "licence_url":"https://creativecommons.org/publicdomain/zero/1.0/","licence_proof":"stated CC0","retrieved_at":"2026-11-10",
        "source_sha256":"{{sha}}"}]}
        """;

    [Fact]
    public async Task A_pack_item_is_downloaded_verified_normalised_and_stored_with_its_licence_proof_fields()
    {
        using InferenceApiDbContext db = NewDb();
        FakeSfxFetcher fetcher = new(_ => PackBytes);
        FakeSfxRenderer renderer = new();
        FakeSfxStore store = new();

        SfxSeedResult result = await Seed(
            db, renderer, store, Config(("SfxLibrary:PackBaseUrl", "https://packs.example.org/v1/")), fetcher, PackManifest(WavFacts.Sha256(PackBytes)));

        result.Rendered.Should().Be(SfxSynthRecipes.All.Count + 1);
        fetcher.Requested.Single().ToString().Should().Be("https://packs.example.org/v1/ui/click.ogg");
        SfxLibraryItem row = await db.SfxLibraryItems.SingleAsync(i => i.Slug == "kenney-click");
        row.Licence.Should().Be("CC0 1.0");
        row.LicenceUrl.Should().Contain("creativecommons.org");
        row.Author.Should().Be("Kenney");
        row.SourceUrl.Should().Be("https://example.org/click");
        row.RecipeVersion.Should().StartWith("pack:");
        row.Sha256.Should().Be(WavFacts.Sha256(store.Objects[row.StorageKey]));
        renderer.Normalizes.Should().Be(1);
    }

    [Fact]
    public async Task A_committed_pack_file_is_stored_as_is_when_its_hash_matches_and_refused_when_it_does_not()
    {
        string dir = Directory.CreateTempSubdirectory("sfxpack-").FullName;
        try
        {
            byte[] ogg = [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 1, 2, 3];
            File.WriteAllBytes(Path.Combine(dir, "good.ogg"), ogg);
            File.WriteAllBytes(Path.Combine(dir, "bad.ogg"), ogg);
            string Item(string slug, string file, string sha) => $$"""
                {"kind":"pack","slug":"{{slug}}","title":"T","category":"click","tags":["click"],"file":"{{file}}","original_file":"p/{{file}}","mime_type":"audio/ogg","duration_ms":250,
                "sha256":"{{sha}}","source_url":"https://kenney.nl/assets/x","author":"Kenney","licence":"CC0 1.0",
                "licence_url":"https://creativecommons.org/publicdomain/zero/1.0/","licence_proof":"License.txt: CC0","retrieved_at":"2026-11-10"}
                """;
            string manifest = $$"""{"items":[{{Item("good-one", "good.ogg", WavFacts.Sha256(ogg))}},{{Item("bad-one", "bad.ogg", new string('0', 64))}}]}""";
            using InferenceApiDbContext db = NewDb();
            FakeSfxStore store = new();

            SfxSeedResult result = await Seed(db, new FakeSfxRenderer(), store, Config(("SfxLibrary:PackDirectory", dir)), manifest: manifest);

            result.Failed.Should().Be(1);
            SfxLibraryItem row = await db.SfxLibraryItems.SingleAsync(i => i.Slug == "good-one");
            row.StorageKey.Should().Be("platform/sfx-library/good-one.ogg");
            row.MimeType.Should().Be("audio/ogg");
            row.DurationMs.Should().Be(250);
            store.Objects[row.StorageKey].Should().Equal(ogg);
            (await db.SfxLibraryItems.AnyAsync(i => i.Slug == "bad-one")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_pack_file_whose_hash_differs_from_the_manifest_is_refused()
    {
        using InferenceApiDbContext db = NewDb();

        await Seed(db, new FakeSfxRenderer(), new FakeSfxStore(), Config(("SfxLibrary:PackBaseUrl", "https://packs.example.org")),
            new FakeSfxFetcher(_ => PackBytes), PackManifest(new string('f', 64)));

        (await db.SfxLibraryItems.AnyAsync(i => i.Slug == "kenney-click")).Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_pack_url_pack_items_are_skipped_not_failed()
    {
        using InferenceApiDbContext db = NewDb();
        FakeSfxFetcher fetcher = new(_ => PackBytes);

        SfxSeedResult result = await Seed(db, new FakeSfxRenderer(), new FakeSfxStore(), f: fetcher, manifest: PackManifest(WavFacts.Sha256(PackBytes)));

        result.Skipped.Should().Be(1);
        result.Failed.Should().Be(0);
        fetcher.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_manifest_still_seeds_the_synthesized_items_and_publishes_nothing_unverified()
    {
        using InferenceApiDbContext db = NewDb();

        SfxSeedResult result = await Seed(db, new FakeSfxRenderer(), new FakeSfxStore(), manifest: PackManifest(WavFacts.Sha256(PackBytes)).Replace("CC0 1.0", "CC-BY 4.0"));

        result.Rendered.Should().Be(SfxSynthRecipes.All.Count);
        (await db.SfxLibraryItems.AnyAsync(i => i.Slug == "kenney-click")).Should().BeFalse();
    }

    [Fact]
    public async Task A_synth_item_switched_off_in_the_manifest_is_disabled()
    {
        using InferenceApiDbContext db = NewDb();
        string slug = SfxSynthRecipes.All[2].Slug;

        await Seed(db, new FakeSfxRenderer(), new FakeSfxStore(), manifest: $$"""{"items":[{"kind":"synth","recipe":"{{slug}}","enabled":false}]}""");

        (await db.SfxLibraryItems.SingleAsync(i => i.Slug == slug)).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void The_wav_reader_measures_duration_and_rejects_garbage()
    {
        WavFacts.DurationMs(SfxTestWav.Make(250)).Should().Be(250);
        WavFacts.DurationMs([1, 2, 3]).Should().BeNull();
    }
}

public class SfxLibraryControllerTests
{
    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase($"sfxc-{Guid.NewGuid()}").Options);

    private static SfxLibraryItem Item(string slug, string category, string[] tags, bool enabled = true, int order = 0, string? key = null) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Title = slug,
        Category = category,
        Tags = [.. tags],
        DurationMs = 400,
        SizeBytes = 100,
        Sha256 = new string('a', 64),
        StorageKey = key ?? SfxLibraryKeys.KeyFor(slug),
        MimeType = "audio/wav",
        Licence = "ReelBolt-original",
        IsEnabled = enabled,
        SortOrder = order,
        AddedAt = DateTimeOffset.UtcNow
    };

    private static (SfxLibraryController Controller, FakeSfxStore Store) Make(InferenceApiDbContext db)
    {
        FakeSfxStore store = new();
        SfxLibraryController c = new(db, store) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        return (c, store);
    }

    [Fact]
    public async Task The_list_filters_by_query_category_and_tag_pages_and_hides_disabled_items_and_storage_keys()
    {
        using InferenceApiDbContext db = NewDb();
        db.SfxLibraryItems.AddRange(
            Item("pop-soft", "pop", ["pop", "appear"], order: 1), Item("whoosh-a", "whoosh", ["transition"], order: 2),
            Item("whoosh-b", "whoosh", ["transition", "scene_swap"], order: 3), Item("hidden", "pop", ["pop"], enabled: false, order: 4));
        await db.SaveChangesAsync();
        (SfxLibraryController c, _) = Make(db);

        SfxLibraryListResponse all = (await c.List(null, null, null)).Value!;
        all.Total.Should().Be(3);
        all.Categories.Should().Equal(new SfxLibraryCategoryCount("pop", 1), new SfxLibraryCategoryCount("whoosh", 2));
        (await c.List(null, "whoosh", null)).Value!.Items.Select(i => i.Slug).Should().Equal("whoosh-a", "whoosh-b");
        (await c.List(null, null, "scene_swap")).Value!.Items.Select(i => i.Slug).Should().Equal("whoosh-b");
        (await c.List("soft", null, null)).Value!.Items.Select(i => i.Slug).Should().Equal("pop-soft");
        SfxLibraryListResponse page = (await c.List(null, null, null, page: 2, pageSize: 2)).Value!;
        page.Items.Select(i => i.Slug).Should().Equal("whoosh-b");
        page.Total.Should().Be(3);

        string json = JsonSerializer.Serialize(all).ToLowerInvariant();
        json.Should().NotContain("platform/").And.NotContain("storage").And.NotContain("sha256");
    }

    [Fact]
    public async Task Preview_streams_an_enabled_item_inline_with_range_support_and_safe_headers()
    {
        using InferenceApiDbContext db = NewDb();
        SfxLibraryItem item = Item("pop-soft", "pop", ["pop"]);
        db.SfxLibraryItems.Add(item);
        await db.SaveChangesAsync();
        (SfxLibraryController c, FakeSfxStore store) = Make(db);
        store.Objects[item.StorageKey] = SfxTestWav.Make(300);

        IActionResult result = await c.Preview(item.Id, CancellationToken.None);

        FileContentResult file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("audio/wav");
        file.EnableRangeProcessing.Should().BeTrue();
        file.FileContents.Should().Equal(store.Objects[item.StorageKey]);
        c.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        c.Response.Headers["Content-Disposition"].ToString().Should().StartWith("inline;");
    }

    [Fact]
    public async Task Preview_answers_the_same_empty_404_for_unknown_disabled_missing_bytes_and_a_foreign_key()
    {
        using InferenceApiDbContext db = NewDb();
        SfxLibraryItem disabled = Item("off", "pop", ["pop"], enabled: false);
        SfxLibraryItem noBytes = Item("nobytes", "pop", ["pop"]);
        SfxLibraryItem foreign = Item("foreign", "pop", ["pop"], key: "projects/abc/userFiles/x.wav");
        db.SfxLibraryItems.AddRange(disabled, noBytes, foreign);
        await db.SaveChangesAsync();
        (SfxLibraryController c, FakeSfxStore store) = Make(db);
        store.Objects[foreign.StorageKey] = [1];

        foreach (Guid id in new[] { Guid.NewGuid(), disabled.Id, noBytes.Id, foreign.Id })
            (await c.Preview(id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void The_controller_requires_a_signed_in_user_and_has_no_anonymous_action()
    {
        typeof(SfxLibraryController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        typeof(SfxLibraryController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().OnlyContain(m => m.GetCustomAttribute<AllowAnonymousAttribute>() == null);
    }

    [Fact]
    public void The_library_is_platform_content_with_no_tenant_column_and_the_engine_maps_it_read_only()
    {
        using InferenceApiDbContext db = NewDb();
        var type = db.Model.FindEntityType(typeof(SfxLibraryItem))!;

        type.GetTableName().Should().Be("sfx_library_items");
        type.GetDeclaredQueryFilters().Should().BeEmpty();
        type.FindProperty("OrganizationId").Should().BeNull();
    }
}
