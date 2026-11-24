using System.Globalization;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing.Interchange;

/// <summary>
/// R12.2: the one place that decides what a media file is called inside a hand-off package. The interchange
/// writers (OTIO <c>target_url</c>, EDL <c>* SOURCE FILE</c>, FCPXML <c>media-rep src</c>) and the zip builder
/// both read the SAME assignment, so a reference in the timeline file is always exactly the zip entry
/// <c>media/&lt;safe-name&gt;</c>. A name never reaches a path on disk: it is only ever a zip entry name we
/// generate.
/// </summary>
public static class HandOffNaming
{
    /// <summary>The longest entry name (stem + extension) we write.</summary>
    public const int MaxNameLength = 120;

    /// <summary>The folder inside the package that holds the media.</summary>
    public const string MediaFolder = "media";

    private const int MaxExtensionLength = 16;
    private const string Fallback = "media";

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>The zip entry name of a safe file name: <c>media/&lt;name&gt;</c>.</summary>
    public static string EntryName(string safeName) => MediaFolder + "/" + safeName;

    /// <summary>
    /// A file name that is safe on Windows, macOS and Linux and as a zip entry: NFC; no path separators (only the
    /// part after the last one is kept), no control or invisible formatting characters, no <c>..</c>, no characters
    /// Windows forbids, no reserved device name, no trailing dot or space; at most <see cref="MaxNameLength"/>
    /// characters with the extension kept. A name with nothing usable becomes <c>media</c>.
    /// </summary>
    public static string Sanitize(string? name)
    {
        string text = (name ?? string.Empty).Normalize(NormalizationForm.FormC);
        int cut = text.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) text = text[(cut + 1)..];

        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                continue;
            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*') { sb.Append('_'); continue; }
            sb.Append(c);
        }

        string clean = sb.ToString();
        while (clean.Contains("..", StringComparison.Ordinal)) clean = clean.Replace("..", ".", StringComparison.Ordinal);
        clean = clean.Trim().TrimEnd('.', ' ');

        (string stem, string extension) = Split(clean);
        stem = stem.Trim().TrimEnd('.', ' ');
        if (stem.Length == 0) stem = Fallback;
        if (ReservedWindowsNames.Contains(stem)) stem = "_" + stem;

        return Fit(stem, extension, MaxNameLength);
    }

    /// <summary>
    /// A unique safe name for each source, in the order given. Two sources that sanitise to the same name
    /// (compared without regard to case, as Windows and macOS do) get <c> (2)</c>, <c> (3)</c> before the extension.
    /// The same source asked for twice gets its first name back, so a file is written once.
    /// </summary>
    public static IReadOnlyDictionary<AssetRef, string> Assign(IEnumerable<(AssetRef Reference, string? FileName)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Dictionary<AssetRef, string> assigned = [];
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach ((AssetRef reference, string? fileName) in sources)
        {
            if (assigned.ContainsKey(reference)) continue;
            string safe = Sanitize(fileName);
            if (taken.Contains(safe))
            {
                (string stem, string extension) = Split(safe);
                for (int n = 2; ; n++)
                {
                    string suffix = $" ({n.ToString(CultureInfo.InvariantCulture)})";
                    string candidate = Fit(stem + suffix, extension, MaxNameLength, keepTail: suffix.Length);
                    if (taken.Add(candidate)) { safe = candidate; break; }
                }
            }
            else
            {
                taken.Add(safe);
            }

            assigned[reference] = safe;
        }

        return assigned;
    }

    private static (string Stem, string Extension) Split(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return (name, string.Empty);
        string extension = name[dot..];
        return extension.Length - 1 > MaxExtensionLength ? (name, string.Empty) : (name[..dot], extension);
    }

    /// <summary>Joins stem and extension within <paramref name="max"/> chars, trimming the stem (never the extension or the last <paramref name="keepTail"/> chars of it).</summary>
    private static string Fit(string stem, string extension, int max, int keepTail = 0)
    {
        if (stem.Length + extension.Length <= max) return stem + extension;
        int room = Math.Max(1, max - extension.Length);
        if (keepTail > 0 && keepTail < room)
        {
            string tail = stem[^keepTail..];
            string head = stem[..^keepTail];
            int headRoom = room - tail.Length;
            head = TrimSurrogate(head[..Math.Min(head.Length, headRoom)]).TrimEnd('.', ' ');
            return head + tail + extension;
        }

        return TrimSurrogate(stem[..room]).TrimEnd('.', ' ') + extension;
    }

    private static string TrimSurrogate(string text) =>
        text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
}

/// <summary>What one hand-off source looks like to the interchange writers.</summary>
/// <param name="SafeName">The <see cref="HandOffNaming"/> name; the file is <c>media/{SafeName}</c> in the package.</param>
/// <param name="SizeBytes">The stored size, when known; written to the OTIO media reference's ReelBolt metadata.</param>
/// <param name="DurationSec">The probed duration, when known; written as the OTIO <c>available_range</c>.</param>
public sealed record HandOffMediaEntry(string SafeName, long? SizeBytes = null, double? DurationSec = null);

/// <summary>
/// The "hand-off media naming" writer option: for each asset reference, the package-relative file name the
/// writers put in the timeline file instead of a <c>reelbolt://</c> URL. A reference missing from the map falls
/// back to the reelbolt URL (the media could not be resolved in this project).
/// </summary>
public sealed class HandOffMediaNames(IReadOnlyDictionary<AssetRef, HandOffMediaEntry> entries)
{
    public IReadOnlyDictionary<AssetRef, HandOffMediaEntry> Entries { get; } = entries ?? throw new ArgumentNullException(nameof(entries));

    public HandOffMediaEntry? Find(AssetRef reference) => Entries.TryGetValue(reference, out HandOffMediaEntry? entry) ? entry : null;

    /// <summary>The plain relative path (<c>media/clip a.mp4</c>), as an EDL comment carries it.</summary>
    public string? RelativePath(AssetRef reference) => Find(reference) is { } entry ? HandOffNaming.EntryName(entry.SafeName) : null;

    /// <summary>The relative URL reference (<c>media/clip%20a.mp4</c>), as OTIO <c>target_url</c> and FCPXML <c>src</c> carry it.</summary>
    public string? RelativeUrl(AssetRef reference) =>
        Find(reference) is { } entry ? HandOffNaming.MediaFolder + "/" + Uri.EscapeDataString(entry.SafeName) : null;
}
