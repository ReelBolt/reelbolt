using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Blocks;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// The <see cref="ReelBolt.Shared.Agents.ToolGroup.SandboxBlocks"/> tools: browse the first-party block
/// library and copy a block into the run's workspace.
///
/// <para>
/// <b>What AddBlock can write.</b> Only the files the catalog lists for the block id, each under the fixed
/// prefix <c>src/blocks/</c> (plus <c>src/blocks/blocks.lock.json</c>). The id is looked up in the catalog,
/// the path is never taken from the caller, and every catalog path is re-checked with
/// <see cref="BlockCatalog.CheckPath"/> here as well as at load. It refuses, before writing anything, when
/// any target file already exists with different content, so the agent's own edits to a copied block are
/// never overwritten. A tool exception would end the whole workflow, so every failure is returned as data.
/// </para>
/// </summary>
public sealed class BlockAgentTools
{
    public const string TargetPrefix = "src/blocks/";
    public const string LockPath = "src/blocks/blocks.lock.json";
    private const int MaxListed = 60;

    private readonly IWorkflowExecutionContextAccessor _contextAccessor;
    private readonly ISandboxSessionFactory _sessions;
    private readonly IBlockCatalog _catalog;
    private readonly ILogger<BlockAgentTools> _logger;

    public BlockAgentTools(
        IWorkflowExecutionContextAccessor contextAccessor,
        ISandboxSessionFactory sessions,
        IBlockCatalog catalog,
        ILogger<BlockAgentTools> logger)
    {
        _contextAccessor = contextAccessor;
        _sessions = sessions;
        _catalog = catalog;
        _logger = logger;
    }

    [Description(
        "List ready-made, tested Remotion blocks you can copy into the composition instead of writing the effect from scratch. " +
        "Categories: transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes. " +
        "Call this once, with no arguments, before writing any scene file: it lists every block and is cheap. Pass category or query only to narrow the list. " +
        "Use a block for every transition, title, caption, chart, map, texture, background, device frame or callout the brief needs instead of writing it from scratch. " +
        "Returns one compact line per block (id, category, title, short description). " +
        "Use GetBlock(id) for props and an example, then AddBlock(id) to copy it into src/blocks/.")]
    public string ListBlocks(
        [Description("Optional category filter: transitions, titles, captions, charts, maps, effects, backgrounds, devices or shapes.")] string? category = null,
        [Description("Optional search words matched against id, title, description and tags.")] string? query = null)
    {
        string? cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        if (cat is not null && !BlockCategories.All.Contains(cat))
            return $"Unknown category '{category}'. Categories: {BlockCategories.Joined}.";

        string[] words = (query ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<BlockEntry> hits = _catalog.Blocks
            .Where(b => cat is null || b.Category == cat)
            .Where(b => words.All(w => Matches(b, w)))
            .ToList();

        if (hits.Count == 0)
            return _catalog.Blocks.Count == 0
                ? "No blocks are available in this deployment; build the effect yourself."
                : $"No block matches. Categories: {BlockCategories.Joined}. Try a broader query or no query.";

        StringBuilder sb = new();
        sb.Append(hits.Count).Append(" block(s)").Append(hits.Count > MaxListed ? $" (showing {MaxListed}; narrow with category/query)" : "").Append(":\n");
        foreach (BlockEntry b in hits.Take(MaxListed))
            sb.Append("- ").Append(b.Id).Append(" [").Append(b.Category).Append("] ").Append(b.Title).Append(": ").Append(Shorten(b.Description, 110)).Append('\n');
        return sb.ToString();
    }

    [Description(
        "Full details of one block: props signature, a usage example, the npm packages it needs and the files AddBlock would write " +
        "under src/blocks/. Read-only. Block categories: transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes.")]
    public string GetBlock([Description("Exact block id from ListBlocks, e.g. 'wipe-slide'.")] string id)
    {
        BlockEntry? b = _catalog.Find(id);
        if (b is null)
            return NotFound(id);

        return JsonSerializer.Serialize(new
        {
            id = b.Id,
            version = b.Version,
            title = b.Title,
            category = b.Category,
            description = b.Description,
            tags = b.Tags,
            props = b.Props,
            example = b.Example,
            requires = b.Requires.Select(r => new { name = r.Name, version = r.Version }),
            files = b.Files.Select(f => TargetPrefix + f.Path),
            addWith = $"AddBlock(\"{b.Id}\")"
        });
    }

    [Description(
        "Copy one catalog block into the workspace under src/blocks/ (the block's own folder plus src/blocks/_shared/ helpers) and record it in " +
        "src/blocks/blocks.lock.json. Then import it from your composition, e.g. './blocks/<id>/<File>.tsx'. " +
        "Refuses, writing nothing, if a needed npm package is not installed (the sandbox cannot install packages: choose another block or write that part yourself) or if a target file " +
        "already exists with different content (your edits are never overwritten; the reply names the file). Safe to call again: " +
        "files that are already identical are left untouched. Call it for every brief element a block covers, then adapt props, colours and fonts to the brief and the design system. Block categories: transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes.")]
    public async Task<string> AddBlock([Description("Exact block id from ListBlocks, e.g. 'wipe-slide'.")] string id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await AddBlockCoreAsync(id, cancellationToken);
        }
        // An OperationCanceledException that is not the caller cancelling (an HttpClient timeout) is a sandbox failure, returned as data.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A tool exception terminates the whole workflow; the agent gets the reason instead.
            _logger.LogWarning(ex, "Tool call AddBlock({BlockId}) failed", id);
            return Fail("sandbox_error", $"AddBlock could not complete: {ex.Message}. Files written before the failure (if any) are kept; call it again or write the effect yourself.");
        }
    }

    private async Task<string> AddBlockCoreAsync(string id, CancellationToken ct)
    {
        BlockEntry? block = _catalog.Find(id);
        if (block is null)
            return NotFound(id);

        // Defence in depth: the loader already validated these; a path that fails here is never written.
        foreach (BlockFile f in block.Files)
        {
            if (BlockCatalog.CheckPath(block.Id, f.Path) is { } pathError)
                return Fail("invalid_catalog_path", $"Block '{block.Id}' has an invalid file path '{f.Path}' ({pathError}); it cannot be added.");
        }

        WorkflowExecutionContext ctx = _contextAccessor.Current
            ?? throw new InvalidOperationException("No workflow execution context is available for block tools.");
        ISandboxSession session = _sessions.Create(ctx.ExecutionId);

        // 1. Required packages must already be in the workspace package.json.
        HashSet<string> installed = await ReadInstalledPackagesAsync(session, ct);
        List<BlockRequire> missing = block.Requires.Where(r => !installed.Contains(r.Name)).ToList();
        if (missing.Count > 0)
        {
            string names = string.Join(", ", missing.Select(m => string.IsNullOrEmpty(m.Version) ? m.Name : $"{m.Name}@{m.Version}"));
            return Fail("missing_package",
                $"Block '{block.Id}' needs npm package(s) not in this workspace's package.json: {names}. Nothing was written. " +
                $"Install them with InstallNpmPackages and call AddBlock again, or pick a block that does not need them, or build the effect yourself.");
        }

        // 1b. Parse the lock BEFORE writing anything: a corrupt lock is refused, never rebuilt.
        string? lockRaw = await TryReadAsync(session, LockPath, ct);
        if (!TryParseLock(lockRaw, out List<JsonObject> lockEntries))
            return Fail("lock_corrupt",
                $"{LockPath} is not a valid block lock (expected {{\"blocks\":[{{\"id\":\"...\",...}}]}}). Nothing was written. Fix or delete that file, then call AddBlock again.");
        Dictionary<string, HashSet<string>> recorded = new(StringComparer.Ordinal);
        foreach (JsonObject e in lockEntries)
            if (e["files"] is JsonArray fa)
                foreach (JsonNode? fn in fa)
                    if (fn is JsonObject fo && fo["path"] is JsonValue pv && pv.TryGetValue(out string? fp) && fo["sha256"] is JsonValue hv && hv.TryGetValue(out string? fh))
                    {
                        if (!recorded.TryGetValue(fp, out HashSet<string>? set))
                            recorded[fp] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(fh);
                    }

        // 2. Classify every target before writing anything.
        List<(BlockFile File, string Target)> toWrite = [];
        List<string> unchanged = [];
        List<string> conflicts = [];
        List<string> upgraded = [];
        foreach (BlockFile f in block.Files)
        {
            string target = TargetPrefix + f.Path;
            string? existing = await TryReadAsync(session, target, ct);
            if (existing is null)
                toWrite.Add((f, target));
            else if (HashOf(existing) == f.Sha256)
                unchanged.Add(target);
            else if (recorded.TryGetValue(f.Path, out HashSet<string>? prior) && prior.Contains(HashOf(existing)))
            {
                // Unedited copy of an earlier catalog version (e.g. a _shared helper): safe to bring up to date.
                toWrite.Add((f, target));
                upgraded.Add(target);
            }
            else
                conflicts.Add(target);
        }

        if (conflicts.Count > 0)
        {
            return Fail("existing_file_differs",
                $"Block '{block.Id}' was NOT added and nothing was written: these files already exist with different content (yours are kept): " +
                $"{string.Join(", ", conflicts)}. Edit them in place if you want your version, or delete them and call AddBlock again to restore the catalog version.");
        }

        // 3. Write the missing files.
        List<string> done = [];
        foreach ((BlockFile file, string target) in toWrite)
        {
            string? failure = null;
            try
            {
                SandboxActionResult result = await session.WriteTextAsync(target, file.Content, ct);
                if (!result.Ok)
                    failure = "the sandbox reported a failed write";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "AddBlock({BlockId}) write of {Target} threw", block.Id, target);
                failure = ex.Message;
            }

            if (failure is not null)
                return Fail("write_failed",
                    $"Writing {target} failed ({failure}); the block is only partly added. Written before the failure: " +
                    $"{(done.Count == 0 ? "nothing" : string.Join(", ", done))}. The lock was not updated. Call AddBlock again to finish it.");
            done.Add(target);
        }

        // 4. Upsert the lock entry (a failure here is a warning: the files are in place).
        bool lockChanged = false;
        string? warning = null;
        try
        {
            lockChanged = await UpsertLockAsync(session, block, lockEntries, lockRaw, ct);
            if (!lockChanged && lockRaw is null)
                warning = $"{LockPath} could not be written; the block files are in place.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AddBlock({BlockId}) lock write threw", block.Id);
            warning = $"{LockPath} could not be written ({ex.Message}); the block files are in place.";
        }

        _logger.LogInformation(
            "Tool call AddBlock for execution {ExecutionId}: block={BlockId} written={Written} unchanged={Unchanged}",
            ctx.ExecutionId, block.Id, toWrite.Count, unchanged.Count);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            block = block.Id,
            version = block.Version,
            written = toWrite.Select(w => w.Target),
            unchanged,
            upgraded,
            warning,
            lockUpdated = lockChanged,
            import = $"Import from your composition, e.g. './blocks/{block.Files.First(x => x.Path.StartsWith(block.Id + "/", StringComparison.Ordinal)).Path}'.",
            example = block.Example
        });
    }

    private static async Task<HashSet<string>> ReadInstalledPackagesAsync(ISandboxSession session, CancellationToken ct)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        string? raw = await TryReadAsync(session, "package.json", ct);
        if (raw is null)
            return names;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            foreach (string section in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (doc.RootElement.TryGetProperty(section, out JsonElement deps) && deps.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty p in deps.EnumerateObject())
                        names.Add(p.Name);
            }
        }
        catch (JsonException)
        {
            // An unparsable package.json reads as "nothing installed": AddBlock then refuses with the install hint.
        }

        return names;
    }

    /// <summary>True when <paramref name="raw"/> is absent (empty lock) or an object whose "blocks" is an array of objects with string ids.</summary>
    private static bool TryParseLock(string? raw, out List<JsonObject> entries)
    {
        entries = [];
        if (raw is null)
            return true;
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject root || root["blocks"] is not JsonArray arr)
                return false;
            foreach (JsonNode? n in arr)
            {
                if (n is not JsonObject o || o["id"] is not JsonValue v || !v.TryGetValue(out string? _))
                    return false;
                entries.Add((JsonObject)o.DeepClone());
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> UpsertLockAsync(ISandboxSession session, BlockEntry block, List<JsonObject> existing, string? raw, CancellationToken ct)
    {
        JsonArray entries = [];
        foreach (JsonObject o in existing)
            if (o["id"]!.GetValue<string>() != block.Id)
                entries.Add(o);

        JsonArray files = [];
        foreach (BlockFile f in block.Files)
            files.Add(new JsonObject { ["path"] = f.Path, ["sha256"] = f.Sha256 });
        entries.Add(new JsonObject { ["id"] = block.Id, ["version"] = block.Version, ["files"] = files });

        JsonArray sorted = new(entries.OfType<JsonObject>().OrderBy(o => o["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(o => (JsonNode)o.DeepClone()).ToArray());
        string next = new JsonObject { ["blocks"] = sorted }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (string.Equals(raw, next, StringComparison.Ordinal))
            return false;

        SandboxActionResult result = await session.WriteTextAsync(LockPath, next, ct);
        return result.Ok;
    }

    /// <summary>Text of a workspace file, or null when it does not exist. Existence comes from a directory listing, so a read failure is never mistaken for "absent".</summary>
    private static async Task<string?> TryReadAsync(ISandboxSession session, string path, CancellationToken ct)
    {
        int slash = path.LastIndexOf('/');
        string dir = slash < 0 ? "." : path[..slash];
        string name = path[(slash + 1)..];
        IReadOnlyList<SandboxFileEntry> entries;
        try
        {
            entries = await session.ListFilesAsync(dir, ct);
        }
        catch (SandboxRequestFailedException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!entries.Any(e => !e.IsDir && string.Equals(e.Name, name, StringComparison.Ordinal)))
            return null;

        return (await session.ReadTextAsync(path, ct)).Text;
    }

    private static string HashOf(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static bool Matches(BlockEntry b, string word) =>
        b.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
        || b.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
        || b.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
        || b.Category.Contains(word, StringComparison.OrdinalIgnoreCase)
        || b.Tags.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private string NotFound(string id) =>
        Fail("unknown_block", $"No block with id '{id}'. Call ListBlocks (categories: {BlockCategories.Joined}) for the exact ids.");

    private static string Fail(string code, string message) => JsonSerializer.Serialize(new { ok = false, code, error = message });
}
