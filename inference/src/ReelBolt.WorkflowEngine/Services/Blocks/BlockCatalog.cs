using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Services.Blocks;

/// <summary>A package a block imports and the version range it was written against.</summary>
public sealed record BlockRequire(string Name, string Version);

/// <summary>One file of a block: its catalog path (<c>&lt;id&gt;/&lt;file&gt;</c> or <c>_shared/&lt;file&gt;</c>), verified content and hash.</summary>
public sealed record BlockFile(string Path, string Sha256, string Content);

/// <summary>A block that passed every load-time check.</summary>
public sealed record BlockEntry(
    string Id,
    string Version,
    string Title,
    string Category,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<BlockRequire> Requires,
    IReadOnlyList<BlockFile> Files,
    string Props,
    string Example);

/// <summary>A catalog block that was dropped, and why. Surfaced through the log only; never fatal.</summary>
public sealed record BlockLoadFailure(string Id, string Reason);

/// <summary>
/// The first-party block library (<c>inference/blocks/</c>, shipped to <c>Blocks:CorpusPath</c>), loaded
/// once and hash-verified. A block that fails any check is dropped with a log line; the engine and the
/// other blocks are unaffected.
/// </summary>
public interface IBlockCatalog
{
    /// <summary>Every block that loaded and verified, ordered by category then id.</summary>
    IReadOnlyList<BlockEntry> Blocks { get; }

    /// <summary>The block with this exact id, or null.</summary>
    BlockEntry? Find(string? id);

    /// <summary>Blocks dropped at load time.</summary>
    IReadOnlyList<BlockLoadFailure> Failures { get; }
}

public static class BlockCategories
{
    /// <summary>Closed category set. Keep in sync with <c>CATEGORIES</c> in inference/blocks/scripts/lib.mjs.</summary>
    public static readonly IReadOnlyList<string> All =
        ["transitions", "titles", "captions", "charts", "maps", "effects", "backgrounds", "devices", "shapes"];

    public static string Joined => string.Join(", ", All);
}

public sealed partial class BlockCatalog : IBlockCatalog
{
    public const string DefaultCorpusPath = "/app/blocks";
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxFilesPerBlock = 24;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.Ordinal) { ".tsx", ".ts", ".json", ".css", ".svg" };

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^(?!\\.+$)[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SegmentRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex ShaRegex();

    private readonly string _corpusRoot;
    private readonly ILogger<BlockCatalog> _logger;
    private readonly Lazy<(IReadOnlyList<BlockEntry> Blocks, IReadOnlyList<BlockLoadFailure> Failures)> _loaded;

    public BlockCatalog(IConfiguration configuration, ILogger<BlockCatalog> logger)
    {
        _corpusRoot = configuration["Blocks:CorpusPath"] ?? DefaultCorpusPath;
        _logger = logger;
        _loaded = new Lazy<(IReadOnlyList<BlockEntry>, IReadOnlyList<BlockLoadFailure>)>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<BlockEntry> Blocks => _loaded.Value.Blocks;

    public IReadOnlyList<BlockLoadFailure> Failures => _loaded.Value.Failures;

    public BlockEntry? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Blocks.FirstOrDefault(b => string.Equals(b.Id, id.Trim(), StringComparison.Ordinal));

    private (IReadOnlyList<BlockEntry>, IReadOnlyList<BlockLoadFailure>) Load()
    {
        List<BlockLoadFailure> failures = [];
        try
        {
            return LoadCore(failures);
        }
        catch (Exception ex)
        {
            // A missing or unreadable corpus must never take the engine down: no blocks, one log line.
            _logger.LogWarning(ex, "Block catalog could not be loaded from {CorpusRoot}; no blocks are available", _corpusRoot);
            return ([], failures);
        }
    }

    private (IReadOnlyList<BlockEntry>, IReadOnlyList<BlockLoadFailure>) LoadCore(List<BlockLoadFailure> failures)
    {
        string root = Path.GetFullPath(_corpusRoot);
        string catalogPath = Path.Combine(root, "catalog.json");
        if (!File.Exists(catalogPath))
        {
            _logger.LogWarning("Block catalog not found at {CatalogPath}; no blocks are available", catalogPath);
            return ([], failures);
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("schemaVersion", out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1
            || !doc.RootElement.TryGetProperty("blocks", out JsonElement blocks)
            || blocks.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Block catalog {CatalogPath} has an unsupported shape (expected schemaVersion 1 and a blocks array)", catalogPath);
            return ([], failures);
        }

        string srcRoot = Path.Combine(root, "src");
        List<BlockEntry> parsed = [];
        foreach (JsonElement element in blocks.EnumerateArray())
        {
            string id = element.ValueKind == JsonValueKind.Object ? Str(element, "id") : "";
            try
            {
                string? error = TryLoadBlock(element, srcRoot, out BlockEntry? entry);
                if (error is not null || entry is null)
                {
                    Drop(failures, id, error ?? "invalid entry");
                    continue;
                }

                parsed.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or DecoderFallbackException)
            {
                Drop(failures, id, ex.Message);
            }
        }

        // A duplicated id is ambiguous (which file set would AddBlock copy?), so every claimant is dropped.
        HashSet<string> duplicated = parsed.GroupBy(b => b.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string dup in duplicated)
            Drop(failures, dup, "duplicate block id in catalog; all entries with this id dropped");

        List<BlockEntry> kept = parsed.Where(b => !duplicated.Contains(b.Id))
            .OrderBy(b => BlockCategories.All.ToList().IndexOf(b.Category))
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
        _logger.LogInformation("Block catalog loaded: {Loaded} block(s), {Dropped} dropped", kept.Count, failures.Count);
        return (kept, failures);
    }

    private void Drop(List<BlockLoadFailure> failures, string id, string reason)
    {
        failures.Add(new BlockLoadFailure(id, reason));
        _logger.LogWarning("Block '{BlockId}' dropped from the catalog: {Reason}", id, reason);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Returns an error message, or null with <paramref name="entry"/> set.</summary>
    private static string? TryLoadBlock(JsonElement e, string srcRoot, out BlockEntry? entry)
    {
        entry = null;
        if (e.ValueKind != JsonValueKind.Object)
            return "entry is not an object";

        string id = Str(e, "id");
        if (!IdRegex().IsMatch(id) || id.Length > 64)
            return "invalid id";

        string version = Str(e, "version");
        string title = Str(e, "title");
        string category = Str(e, "category");
        string description = Str(e, "description");
        if (version.Length == 0 || title.Length == 0 || description.Length == 0)
            return "version, title and description are required";
        if (!BlockCategories.All.Contains(category))
            return $"category '{category}' is not one of: {BlockCategories.Joined}";

        List<string> tags = e.TryGetProperty("tags", out JsonElement t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

        List<BlockRequire> requires = [];
        if (e.TryGetProperty("requires", out JsonElement r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement q in r.EnumerateArray())
            {
                string name = q.ValueKind == JsonValueKind.Object ? Str(q, "name") : "";
                if (name.Length == 0)
                    return "a requires entry has no name";
                requires.Add(new BlockRequire(name, Str(q, "version")));
            }
        }

        if (!e.TryGetProperty("files", out JsonElement f) || f.ValueKind != JsonValueKind.Array)
            return "files is missing";

        List<BlockFile> files = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement item in f.EnumerateArray())
        {
            string path = item.ValueKind == JsonValueKind.Object ? Str(item, "path") : "";
            string sha = Str(item, "sha256");
            string? pathError = CheckPath(id, path);
            if (pathError is not null)
                return $"file '{path}': {pathError}";
            if (!ShaRegex().IsMatch(sha))
                return $"file '{path}': sha256 is not 64 lowercase hex characters";
            if (!seen.Add(path))
                return $"file '{path}' is listed twice";

            string? readError = ReadVerified(srcRoot, path, sha, out string content);
            if (readError is not null)
                return $"file '{path}': {readError}";
            files.Add(new BlockFile(path, sha, content));
        }

        if (files.Count == 0 || files.Count > MaxFilesPerBlock)
            return $"a block needs between 1 and {MaxFilesPerBlock} files";
        if (!files.Any(x => x.Path.StartsWith(id + "/", StringComparison.Ordinal)))
            return "a block must own at least one file under its own id";

        entry = new BlockEntry(id, version, title, category, description, tags, requires, files, Str(e, "props"), Str(e, "example"));
        return null;
    }

    /// <summary>
    /// Lexical path guard for a catalog file path: relative, forward slashes only, exactly
    /// <c>&lt;id&gt;/&lt;file&gt;</c> or <c>_shared/&lt;file&gt;</c> (extra nesting allowed below that), no
    /// <c>..</c> or dot-only segments, no absolute path, an allowed extension. Also used by AddBlock as a
    /// second line of defence before anything is written.
    /// </summary>
    public static string? CheckPath(string id, string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 200)
            return "path is empty or too long";
        if (path.Contains('\\') || path.StartsWith('/') || path.Contains('\0') || Path.IsPathRooted(path))
            return "path must be relative with forward slashes";
        string[] parts = path.Split('/');
        if (parts.Length < 2)
            return "path must be <id>/<file>";
        if (!string.Equals(parts[0], id, StringComparison.Ordinal) && parts[0] != "_shared")
            return $"path must start with '{id}/' or '_shared/'";
        if (!parts.Skip(1).All(s => SegmentRegex().IsMatch(s)))
            return "path has an invalid segment";
        if (!AllowedExtensions.Contains(Path.GetExtension(path)))
            return "extension not allowed";
        return null;
    }

    private static string? ReadVerified(string srcRoot, string relativePath, string expectedSha, out string content)
    {
        content = "";
        string fullRoot = Path.GetFullPath(srcRoot);
        string rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
            return "resolves outside the corpus root";

        // No symlink anywhere between the corpus src root and the file: a link could point the loader at
        // content the catalog never vouched for. The hash would catch a changed target, but not a link to
        // a file that happens to match, and "no links in the corpus" is the simpler invariant to hold.
        string current = fullRoot;
        foreach (string segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
                return "file not found";
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return "path goes through a symbolic link";
        }

        FileInfo file = new(full);
        if (file.Length > MaxFileBytes)
            return "file is larger than the per-file cap";

        byte[] bytes = File.ReadAllBytes(full);
        string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
            return "sha256 mismatch (file differs from the catalog)";

        content = new UTF8Encoding(false, true).GetString(bytes);
        return null;
    }
}
