namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One sound effect of the built-in library (docs/sfx-library.md). PLATFORM content: no organization,
/// no tenant filter, the same rows for every project. The Inference API owns the table and seeds it at
/// boot; the engine maps it read-only to offer the items to a sound designer and to resolve a cue the
/// compile mixes. The object-store key is server-side only: it is never returned by any route and never
/// taken from a plan, an agent or a request.
/// </summary>
public class SfxLibraryItem
{
    public Guid Id { get; set; }

    /// <summary>Stable, unique, lower-case slug (<c>whoosh-short-a</c>); the manifest's identity for the item.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>One category word (<c>whoosh</c>, <c>pop</c>, <c>stamp</c> ...).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Lower-case words, including the event types the item suits (<c>appear</c>, <c>hit</c>, <c>transition</c> ...). Stored as a jsonb array.</summary>
    public List<string> Tags { get; set; } = [];

    public int DurationMs { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the stored bytes.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Always under <see cref="SfxLibraryKeys.Prefix"/>.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string MimeType { get; set; } = "audio/wav";

    /// <summary>Licence name and version (<c>ReelBolt-original</c>, <c>CC0 1.0</c>).</summary>
    public string Licence { get; set; } = string.Empty;

    public string? LicenceUrl { get; set; }

    public string? Author { get; set; }

    public string? SourceUrl { get; set; }

    /// <summary>The synthesis recipe revision of a synthesized item, or <c>pack:</c> plus the source hash prefix of a pack item; a change re-renders it.</summary>
    public string? RecipeVersion { get; set; }

    /// <summary>
    /// False takes the item out of every list, offer and mix: the takedown switch. The API's seeder switches a sound off
    /// when a manifest that was read does not list it as on, and never switches one back on; a boot with no readable
    /// manifest changes nothing. A takedown made by hand in the database therefore survives every boot.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>The one place the platform sound-library storage prefix is defined.</summary>
public static class SfxLibraryKeys
{
    public const string Prefix = "platform/sfx-library/";

    public static string KeyFor(string slug, string extension = "wav") => $"{Prefix}{slug}.{extension}";

    /// <summary>True for a key the library may ever own: under the prefix, no traversal.</summary>
    public static bool IsLibraryKey(string? key) =>
        !string.IsNullOrEmpty(key)
        && key.StartsWith(Prefix, StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal)
        && !key.Contains('\\', StringComparison.Ordinal);
}
