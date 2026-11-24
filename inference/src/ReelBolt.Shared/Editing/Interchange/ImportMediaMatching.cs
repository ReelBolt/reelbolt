using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing.Interchange;

/// <summary>What an import knows about one media reference when it asks which project file it means (R12.3).</summary>
/// <param name="Url">The reference as written (<c>target_url</c>, or the reference name): a URL, a path or a bare file name.</param>
/// <param name="ClipName">The clip's own name, for messages.</param>
/// <param name="AssetUrl">The <c>reelbolt://</c> URL a ReelBolt hand-off keeps in the reference's metadata, when present.</param>
/// <param name="AvailableDurationSec">The reference's <c>available_range</c> duration, when the file carries one (an EDL never does).</param>
/// <param name="SizeBytes">The size a ReelBolt hand-off recorded, when present.</param>
/// <param name="FrameSeconds">One frame at the clip's rate: the tolerance a duration must agree within.</param>
public sealed record MediaMatchQuery(
    string? Url,
    string ClipName,
    string? AssetUrl = null,
    double? AvailableDurationSec = null,
    long? SizeBytes = null,
    double FrameSeconds = 1.0 / 30.0);

/// <summary>One project file an ambiguous name could mean. <see cref="FileId"/> is project-scoped, never a storage key.</summary>
public sealed record MediaCandidate(Guid FileId, string Name, double? DurationSec, long SizeBytes);

/// <summary>The answer for one reference: a project asset and how it was found, or the reason it stays offline.</summary>
/// <param name="How"><c>id</c>, <c>name</c> or <c>nameAndDuration</c> when matched.</param>
/// <param name="Reason"><c>notFound</c>, <c>ambiguous</c> or <c>durationMismatch</c> when not.</param>
/// <param name="Name">The matched file's name (for the report).</param>
public sealed record MediaMatchResult(AssetRef? Asset, string? How, string? Reason, string? Name = null, IReadOnlyList<MediaCandidate>? Candidates = null)
{
    public static MediaMatchResult Matched(AssetRef asset, string how, string? name) => new(asset, how, null, name);

    public static MediaMatchResult Offline(string reason, IReadOnlyList<MediaCandidate>? candidates = null) => new(null, null, reason, null, candidates);

    /// <summary>The plain sentence for an offline marker's note and the report.</summary>
    public static string MessageFor(string? reason) => reason switch
    {
        "ambiguous" => "Several files in this project have this name and nothing tells them apart.",
        "durationMismatch" => "A file with this name is here, but its length is different.",
        _ => "This file is not in this project."
    };
}

/// <summary>One reference that matched: <c>how</c> is <c>id</c>, <c>name</c> or <c>nameAndDuration</c>.</summary>
public sealed record MatchedMedia(string Name, Guid? FileId, Guid? StepResultId, string How);

/// <summary>A name that several project files share and nothing tells apart: the import left it offline.</summary>
public sealed record AmbiguousMedia(string Name, IReadOnlyList<MediaCandidate> Candidates);

/// <summary>
/// R12.3: safe import matching. A <c>reelbolt://</c> id first; else the file name (case-insensitive, NFC, directory
/// ignored), and when the file states a duration it must agree with the project file's within one frame at the clip's
/// rate. Several files with one name: the one that agrees on duration (then size) wins if it is unique; otherwise the
/// reference is NOT guessed, it stays offline as <c>ambiguous</c> with the candidates listed.
/// </summary>
public sealed class ImportMediaMatcher
{
    private readonly Dictionary<string, List<MediaCandidate>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, MediaCandidate> _byId = [];
    private readonly Func<AssetRef, bool> _owns;
    private readonly Func<string, AssetRef?> _storageResolver;

    /// <param name="files">This project's files (and only this project's).</param>
    /// <param name="owns">Whether an asset reference belongs to this project (file or step result).</param>
    /// <param name="storageResolver">The seeder's resolver (storage key / step-result output key / exact name): the last fallback.</param>
    public ImportMediaMatcher(IEnumerable<MediaCandidate> files, Func<AssetRef, bool> owns, Func<string, AssetRef?>? storageResolver = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(owns);
        _owns = owns;
        _storageResolver = storageResolver ?? (_ => null);
        foreach (MediaCandidate file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Name)) continue;
            _byId[file.FileId] = file;
            string key = NameKey(file.Name);
            if (!_byName.TryGetValue(key, out List<MediaCandidate>? list)) _byName[key] = list = [];
            list.Add(file);
        }
    }

    public MediaMatchResult Resolve(MediaMatchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        foreach (string? candidateUrl in new[] { query.AssetUrl, query.Url })
        {
            if (TimelineInterchange.TryParseAssetUrl(candidateUrl) is { } direct)
            {
                if (!_owns(direct)) continue;
                string? matchedName = direct.ProjectFileId is { } id && _byId.TryGetValue(id, out MediaCandidate? file) ? file.Name : query.ClipName;
                return MediaMatchResult.Matched(direct, "id", matchedName);
            }
        }

        if (string.IsNullOrWhiteSpace(query.Url) || query.Url.StartsWith(TimelineInterchange.ReelBoltUrlScheme, StringComparison.OrdinalIgnoreCase))
            return MediaMatchResult.Offline("notFound");

        string leaf = LeafName(query.Url);
        if (!_byName.TryGetValue(NameKey(leaf), out List<MediaCandidate>? sameName) || sameName.Count == 0)
        {
            AssetRef? legacy = _storageResolver(leaf) ?? _storageResolver(query.Url);
            return legacy is not null && _owns(legacy)
                ? MediaMatchResult.Matched(legacy, "name", leaf)
                : MediaMatchResult.Offline("notFound");
        }

        List<MediaCandidate> pool = sameName;
        string how = "name";
        if (query.AvailableDurationSec is { } stated && stated > 0)
        {
            double tolerance = Math.Max(query.FrameSeconds, 1e-6) + 1e-6;
            List<MediaCandidate> agree = sameName.Where(c => c.DurationSec is { } d && Math.Abs(d - stated) <= tolerance).ToList();
            List<MediaCandidate> unknown = sameName.Where(c => c.DurationSec is null).ToList();
            if (agree.Count > 0) { pool = agree; how = "nameAndDuration"; }
            else if (unknown.Count > 0) { pool = unknown; } // never probed: the name is all there is to go on
            else return MediaMatchResult.Offline("durationMismatch");
        }

        if (pool.Count > 1 && query.SizeBytes is { } size)
        {
            List<MediaCandidate> bySize = pool.Where(c => c.SizeBytes == size).ToList();
            if (bySize.Count > 0) pool = bySize;
        }

        if (pool.Count != 1) return MediaMatchResult.Offline("ambiguous", pool);
        return MediaMatchResult.Matched(new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: pool[0].FileId), how, pool[0].Name);
    }

    /// <summary>
    /// The file name at the end of a URL or path: percent-decoded, both slash kinds understood, query and fragment dropped.
    /// </summary>
    public static string LeafName(string reference)
    {
        string text = reference;
        int cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0 && text.Contains("://", StringComparison.Ordinal)) text = text[..cut];
        text = text.TrimEnd('/', '\\');
        int slash = text.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) text = text[(slash + 1)..];
        try { text = Uri.UnescapeDataString(text); }
        catch (UriFormatException) { /* keep the raw text */ }
        return text;
    }

    private static string NameKey(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();
}
