using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SfxLibrary;

/// <summary>What one seeding pass did (logged, and asserted by tests).</summary>
public sealed record SfxSeedResult(int Rendered, int Unchanged, int Failed, int Skipped)
{
    public static SfxSeedResult Off { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Seeds the built-in sound-effects library at API boot, idempotently, like <c>PlanCatalogSeeder</c>.
/// Synthesized items (<see cref="SfxSynthRecipes"/>) are rendered with ffmpeg; pack items from the manifest are downloaded
/// from <c>SfxLibrary:PackBaseUrl</c>, SHA-256-verified against the manifest and normalised. A row is (re)rendered only
/// when it is new, its recipe/source revision changed or its object is missing, and each row is saved right after its
/// object is written. <see cref="SfxLibraryItem.IsEnabled"/> is the takedown switch: the seeder switches a sound off
/// when a manifest that was read no longer lists it (or lists a synthesized one with <c>enabled: false</c>), and it
/// never switches a sound back on, so a sound taken down by hand in the database stays down across boots. A boot whose
/// manifest is missing or invalid changes no switch at all, so a bad manifest never takes the library down. The seeder never fails the boot: with no ffmpeg or no storage it logs and leaves the library as it
/// is, and after a failed save it lets go of its rows so the seeders that share the context are not affected. The API
/// owns this because its image carries ffmpeg and it migrates before the engine starts.
/// </summary>
public static class SfxLibrarySeeder
{
    public const string EnabledKey = "SfxLibrary:Enabled";
    public const string PackBaseUrlKey = "SfxLibrary:PackBaseUrl";
    public const string ManifestPathKey = "SfxLibrary:ManifestPath";
    public const string PackDirectoryKey = "SfxLibrary:PackDirectory";
    public const string ManifestFileName = "sfx-library.manifest.json";

    /// <summary>The directory holding committed pack files, next to the manifest.</summary>
    public static string DefaultPackDirectory => Path.Combine(AppContext.BaseDirectory, "Services", "SfxLibrary", "pack");

    /// <summary>The most a committed pack file may weigh.</summary>
    public const int MaxPackFileBytes = 1024 * 1024;

    public static string DefaultManifestPath => Path.Combine(AppContext.BaseDirectory, "Services", "SfxLibrary", ManifestFileName);

    public static async Task<SfxSeedResult> SeedAsync(
        InferenceApiDbContext db,
        IConfiguration configuration,
        ISfxRenderer renderer,
        ISfxLibraryStorage storage,
        ISfxPackFetcher fetcher,
        ILogger logger,
        TimeProvider? time = null,
        string? manifestJson = null,
        CancellationToken ct = default)
    {
        if (!configuration.GetValue(EnabledKey, true))
        {
            logger.LogInformation("Sound library seeding is off ({Key}=false).", EnabledKey);
            return SfxSeedResult.Off;
        }

        try
        {
            SfxManifest? manifest = LoadManifest(configuration, manifestJson, logger);
            return await RunAsync(
                db, configuration, renderer, storage, fetcher, logger, time ?? TimeProvider.System,
                manifest ?? SfxManifest.Empty, manifestLoaded: manifest is not null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sound library seeding failed; the API continues without it.");
            // The context is shared with the seeders that run next: a row left tracked here would be sent again by
            // their saves and fail them. Only this seeder's rows are let go.
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<SfxLibraryItem> entry in db.ChangeTracker.Entries<SfxLibraryItem>().ToList())
                entry.State = EntityState.Detached;
            return SfxSeedResult.Off;
        }
    }

    /// <summary>The manifest, or null when it is missing or invalid (the synthesized items still seed, and no switch is changed).</summary>
    private static SfxManifest? LoadManifest(IConfiguration configuration, string? manifestJson, ILogger logger)
    {
        if (manifestJson is null)
        {
            string path = configuration[ManifestPathKey] ?? DefaultManifestPath;
            if (!File.Exists(path))
            {
                logger.LogWarning("Sound library manifest not found at {Path}; seeding the synthesized items only and leaving every sound's switch as it is.", path);
                return null;
            }

            manifestJson = File.ReadAllText(path);
        }

        try
        {
            return SfxManifestReader.Parse(manifestJson);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            // An invalid manifest must not publish anything unverified: synthesized items still seed.
            logger.LogError(ex, "Sound library manifest is invalid; seeding the synthesized items only and leaving every sound's switch as it is.");
            return null;
        }
    }

    private sealed record Desired(
        string Slug, string Title, string Category, List<string> Tags, string Licence, string? LicenceUrl, string? Author,
        string? SourceUrl, string Revision, bool Enabled, SfxRecipe? Recipe, SfxPackItem? Pack);

    private static async Task<SfxSeedResult> RunAsync(
        InferenceApiDbContext db, IConfiguration configuration, ISfxRenderer renderer, ISfxLibraryStorage storage,
        ISfxPackFetcher fetcher, ILogger logger, TimeProvider time, SfxManifest manifest, bool manifestLoaded, CancellationToken ct)
    {
        Dictionary<string, bool> synthEnabled = manifest.Synth.ToDictionary(s => s.Recipe, s => s.Enabled, StringComparer.Ordinal);
        List<Desired> desired = [];
        foreach (SfxRecipe r in SfxSynthRecipes.All)
        {
            desired.Add(new Desired(
                r.Slug, r.Title, r.Category, [.. r.Tags], SfxSynthRecipes.Licence, null, "ReelBolt", null,
                SfxSynthRecipes.Version, synthEnabled.GetValueOrDefault(r.Slug, true), r, null));
        }

        foreach (SfxPackItem p in manifest.Pack)
        {
            desired.Add(new Desired(
                p.Slug, p.Title, p.Category, [.. p.Tags], p.Licence, p.LicenceUrl, p.Author, p.SourceUrl,
                "pack:" + (p.Sha256 ?? p.SourceSha256)![..16], true, null, p));
        }

        Dictionary<string, SfxLibraryItem> existing = await db.SfxLibraryItems.ToDictionaryAsync(i => i.Slug, StringComparer.Ordinal, ct);
        string? packBase = configuration[PackBaseUrlKey]?.TrimEnd('/');
        int rendered = 0, unchanged = 0, failed = 0, skipped = 0;
        int order = 0;
        foreach (Desired d in desired)
        {
            order++;
            string ext = d.Pack?.MimeType == "audio/ogg" ? "ogg" : "wav";
            string key = SfxLibraryKeys.KeyFor(d.Slug, ext);
            existing.TryGetValue(d.Slug, out SfxLibraryItem? row);
            bool current = row is not null && row.RecipeVersion == d.Revision && await storage.ExistsAsync(row.StorageKey, ct);
            if (current)
            {
                row!.Title = d.Title;
                row.Category = d.Category;
                row.Tags = d.Tags;
                row.SortOrder = order;
                unchanged++;
                continue;
            }

            byte[]? wav;
            int? durationMs;
            if (d.Recipe is not null)
            {
                wav = await renderer.RenderAsync(d.Recipe, ct);
                durationMs = wav is null ? null : WavFacts.DurationMs(wav);
            }
            else if (d.Pack!.IsCommitted)
            {
                wav = ReadCommitted(d.Pack, configuration, logger);
                durationMs = d.Pack.DurationMs;
            }
            else
            {
                wav = await RenderPackAsync(d.Pack, packBase, renderer, fetcher, logger, ct, () => skipped++);
                durationMs = wav is null ? null : WavFacts.DurationMs(wav);
            }

            if (wav is null)
            {
                if (d.Recipe is not null || d.Pack!.IsCommitted) failed++;
                continue;
            }

            if (durationMs is null or <= 0)
            {
                logger.LogWarning("Sound library: rendered {Slug} is not a readable sound; skipped.", d.Slug);
                failed++;
                continue;
            }

            await storage.PutAsync(key, wav, d.Pack?.MimeType ?? "audio/wav", ct);
            if (row is null)
            {
                row = new SfxLibraryItem { Id = Guid.NewGuid(), Slug = d.Slug, AddedAt = time.GetUtcNow(), IsEnabled = d.Enabled };
                db.SfxLibraryItems.Add(row);
                existing[d.Slug] = row;
            }

            row.Title = d.Title;
            row.Category = d.Category;
            row.Tags = d.Tags;
            row.DurationMs = durationMs.Value;
            row.SizeBytes = wav.LongLength;
            row.Sha256 = WavFacts.Sha256(wav);
            row.StorageKey = key;
            row.MimeType = d.Pack?.MimeType ?? "audio/wav";
            row.Licence = d.Licence;
            row.LicenceUrl = d.LicenceUrl;
            row.Author = d.Author;
            row.SourceUrl = d.SourceUrl;
            row.RecipeVersion = d.Revision;
            row.SortOrder = order;
            // Saved now, not at the end of the pass: the engine refuses bytes whose hash differs from the row, so the
            // time a replaced object and its row disagree is one item, and a later failure does not undo this one.
            await db.SaveChangesAsync(ct);
            rendered++;
        }

        // A manifest that was read takes a sound out when it no longer lists it (or lists a synthesized one with
        // enabled: false). The seeder never switches a sound back on: that switch is the takedown, and a takedown made
        // by hand must survive every boot. A missing or invalid manifest says nothing about which sounds should exist,
        // so that boot changes no switch: it must never take the library down.
        if (manifestLoaded)
        {
            Dictionary<string, bool> enabledBySlug = new(StringComparer.Ordinal);
            foreach (Desired d in desired) enabledBySlug.TryAdd(d.Slug, d.Enabled);
            foreach (SfxLibraryItem row in existing.Values)
            {
                if (row.IsEnabled && !enabledBySlug.GetValueOrDefault(row.Slug, false))
                {
                    // Said out loud: no boot switches it back on, so after a rollback this line is the list to check.
                    logger.LogWarning("Sound library: {Slug} is switched off because this release's manifest does not list it as on. No boot switches it back on.", row.Slug);
                    row.IsEnabled = false;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Sound library: {Rendered} rendered, {Unchanged} unchanged, {Failed} failed, {Skipped} pack items skipped.",
            rendered, unchanged, failed, skipped);
        return new SfxSeedResult(rendered, unchanged, failed, skipped);
    }

    /// <summary>A committed pack file from the pack directory, accepted only when its SHA-256 equals the manifest's.</summary>
    private static byte[]? ReadCommitted(SfxPackItem pack, IConfiguration configuration, ILogger logger)
    {
        string dir = Path.GetFullPath(configuration[PackDirectoryKey] ?? DefaultPackDirectory);
        string path = Path.GetFullPath(Path.Combine(dir, pack.File));
        if (!path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            logger.LogWarning("Sound library: committed pack file for {Slug} is missing; skipped.", pack.Slug);
            return null;
        }

        FileInfo info = new(path);
        if (info.Length > MaxPackFileBytes) return null;
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(WavFacts.Sha256(bytes), pack.Sha256, StringComparison.Ordinal))
        {
            logger.LogError("Sound library: committed pack file for {Slug} does not match its manifest SHA-256; refused.", pack.Slug);
            return null;
        }

        return bytes;
    }

    private static async Task<byte[]?> RenderPackAsync(
        SfxPackItem pack, string? packBase, ISfxRenderer renderer, ISfxPackFetcher fetcher, ILogger logger,
        CancellationToken ct, Action skip)
    {
        if (string.IsNullOrEmpty(packBase) || !Uri.TryCreate($"{packBase}/{pack.File}", UriKind.Absolute, out Uri? url))
        {
            skip();
            return null;
        }

        byte[]? raw = await fetcher.FetchAsync(url, ct);
        if (raw is null)
        {
            logger.LogWarning("Sound library: could not download pack item {Slug}; skipped.", pack.Slug);
            skip();
            return null;
        }

        if (!string.Equals(WavFacts.Sha256(raw), pack.SourceSha256, StringComparison.Ordinal))
        {
            logger.LogError("Sound library: pack item {Slug} does not match its manifest SHA-256; refused.", pack.Slug);
            skip();
            return null;
        }

        return await renderer.NormalizeAsync(raw, ct);
    }
}
