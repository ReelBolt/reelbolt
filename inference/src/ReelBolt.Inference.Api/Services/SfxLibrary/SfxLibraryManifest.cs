using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.SfxLibrary;

/// <summary>
/// One third-party pack sound listed in the manifest, with the proof it may be shipped (docs/sfx-library.md). Two shapes:
/// a COMMITTED file (<c>sha256</c> of the stored bytes, <c>duration_ms</c>, <c>original_file</c>; the normalised bytes ship in
/// the pack directory) or a DOWNLOADED one (<c>source_sha256</c> of the file as served from <c>SfxLibrary:PackBaseUrl</c>).
/// </summary>
public sealed record SfxPackItem(
    string Slug,
    string Title,
    string Category,
    IReadOnlyList<string> Tags,
    string File,
    string SourceUrl,
    string Author,
    string Licence,
    string LicenceUrl,
    string LicenceProof,
    DateTimeOffset RetrievedAt,
    string? SourceSha256,
    string? Sha256 = null,
    int? DurationMs = null,
    string? OriginalFile = null,
    string MimeType = "audio/wav")
{
    /// <summary>True for a file committed next to the assembly in already-normalised form (its <see cref="Sha256"/> is of the stored bytes).</summary>
    public bool IsCommitted => Sha256 is not null;
}

/// <summary>A manifest line for a synthesized item: it names a code recipe and can switch the item off.</summary>
public sealed record SfxSynthEntry(string Recipe, bool Enabled);

/// <summary>The parsed, validated manifest. Synthesized items live in <see cref="SfxSynthRecipes"/>; packs come from here.</summary>
public sealed record SfxManifest(IReadOnlyList<SfxSynthEntry> Synth, IReadOnlyList<SfxPackItem> Pack)
{
    public static SfxManifest Empty { get; } = new([], []);
}

/// <summary>
/// Reads <c>sfx-library.manifest.json</c>. Two item kinds: <c>synth</c> (recipe name, optional <c>enabled</c>) and
/// <c>pack</c> (a recorded sound with source, author, licence name and version, licence URL, quoted proof text,
/// retrieval date and the SHA-256 of the file as downloaded). A pack item is accepted ONLY when it is provably CC0:
/// anything else makes the whole manifest invalid, so an unverified third-party item can never reach the library.
/// </summary>
public static class SfxManifestReader
{
    private static readonly Regex SlugShape = new("^[a-z0-9][a-z0-9-]{0,78}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex RelativeFile = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,200}$", RegexOptions.Compiled);

    /// <summary>Parses and validates; throws <see cref="InvalidDataException"/> naming the first problem.</summary>
    public static SfxManifest Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The sound-library manifest has no items array.");

        List<SfxSynthEntry> synth = [];
        List<SfxPackItem> pack = [];
        HashSet<string> slugs = new(SfxSynthRecipes.All.Select(r => r.Slug), StringComparer.Ordinal);
        HashSet<string> recipes = new(slugs, StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            string kind = Req(item, "kind");
            if (kind == "synth")
            {
                string recipe = Req(item, "recipe");
                if (!recipes.Contains(recipe))
                    throw new InvalidDataException($"Manifest synth item names an unknown recipe '{recipe}'.");
                bool enabled = !item.TryGetProperty("enabled", out JsonElement e) || e.ValueKind != JsonValueKind.False;
                synth.Add(new SfxSynthEntry(recipe, enabled));
            }
            else if (kind == "pack")
            {
                pack.Add(ReadPack(item, slugs));
            }
            else
            {
                throw new InvalidDataException($"Manifest item kind '{kind}' is not 'synth' or 'pack'.");
            }
        }

        return new SfxManifest(synth, pack);
    }

    private static SfxPackItem ReadPack(JsonElement item, HashSet<string> slugs)
    {
        string slug = Req(item, "slug");
        if (!SlugShape.IsMatch(slug))
            throw new InvalidDataException($"Pack item slug '{slug}' is not a lower-case slug.");
        if (!slugs.Add(slug))
            throw new InvalidDataException($"Duplicate sound-library slug '{slug}'.");

        string licence = Req(item, "licence");
        if (!licence.StartsWith("CC0", StringComparison.Ordinal))
            throw new InvalidDataException($"Pack item '{slug}' is not CC0 ('{licence}'); only provably CC0 items are accepted.");

        string licenceUrl = Req(item, "licence_url");
        string sourceUrl = Req(item, "source_url");
        foreach (string url in new[] { licenceUrl, sourceUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || u.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Pack item '{slug}' has a non-https URL.");
        }

        string file = Req(item, "file");
        if (!RelativeFile.IsMatch(file) || file.Contains("..", StringComparison.Ordinal) || file.Contains("//", StringComparison.Ordinal))
            throw new InvalidDataException($"Pack item '{slug}' file '{file}' is not a safe relative path.");

        string? sourceSha = Opt(item, "source_sha256")?.ToLowerInvariant();
        string? storedSha = Opt(item, "sha256")?.ToLowerInvariant();
        if ((sourceSha is null) == (storedSha is null))
            throw new InvalidDataException($"Pack item '{slug}' needs exactly one of sha256 (committed file) and source_sha256 (download).");
        foreach (string? sha in new[] { sourceSha, storedSha })
        {
            if (sha is not null && !Hex64.IsMatch(sha))
                throw new InvalidDataException($"Pack item '{slug}' has a hash that is not a SHA-256 hex digest.");
        }

        int? durationMs = null;
        string mime = "audio/wav";
        string? originalFile = null;
        if (storedSha is not null)
        {
            if (!item.TryGetProperty("duration_ms", out JsonElement d) || !d.TryGetInt32(out int ms) || ms is < 10 or > 10_000)
                throw new InvalidDataException($"Pack item '{slug}' needs duration_ms between 10 and 10000.");
            durationMs = ms;
            mime = Req(item, "mime_type");
            if (mime is not ("audio/ogg" or "audio/wav"))
                throw new InvalidDataException($"Pack item '{slug}' mime_type must be audio/ogg or audio/wav.");
            originalFile = Req(item, "original_file");
        }

        if (!DateTimeOffset.TryParse(Req(item, "retrieved_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset retrieved))
            throw new InvalidDataException($"Pack item '{slug}' retrieved_at is not a date.");

        List<string> tags = [];
        if (item.TryGetProperty("tags", out JsonElement tagArray) && tagArray.ValueKind == JsonValueKind.Array)
            tags.AddRange(tagArray.EnumerateArray().Select(t => t.GetString()?.Trim().ToLowerInvariant() ?? string.Empty).Where(t => t.Length > 0));

        return new SfxPackItem(
            slug, Req(item, "title"), Req(item, "category").ToLowerInvariant(), tags, file,
            sourceUrl, Req(item, "author"), licence, licenceUrl, Req(item, "licence_proof"), retrieved, sourceSha, storedSha, durationMs, originalFile, mime);
    }

    private static string? Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static string Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : throw new InvalidDataException($"Manifest item is missing '{name}'.");
}
