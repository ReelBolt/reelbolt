using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>One <c>staticFile('...')</c> literal whose target is not under <c>public/</c>.</summary>
/// <param name="StaticPath">The path as written, relative to <c>public/</c>, e.g. <c>ad-01.mp4</c>.</param>
/// <param name="SourceFile">The workspace-relative source file it is written in, e.g. <c>src/Scene.tsx</c>.</param>
/// <param name="Line">The 1-based line of the reference.</param>
public sealed record MissingStaticFile(string StaticPath, string SourceFile, int Line);

/// <summary>
/// Refuses a still, a layout check or a render of a composition whose code points <c>staticFile()</c>
/// at a file that was never staged into <c>public/</c>.
///
/// <para>
/// Production run 36df4287: the Author referenced <c>staticFile('ad-01.mp4')</c> after staging failed
/// on that made-up name, and every <c>remotion still</c> then died minutes later inside the renderer
/// ("Could not extract frame from compositor Error: Request closed", offthread-video-server) with
/// nothing that named the missing file. This reads the project's <c>src/**</c> sources (ts, tsx, js,
/// jsx, mjs), collects every <c>staticFile('...')</c> / <c>staticFile("...")</c> literal outside a
/// comment, and checks each target exists under <c>public/</c>, so the tool can answer in
/// milliseconds with the exact missing paths.
/// </para>
///
/// <para>
/// <b>Best effort, fail-open.</b> Only plain string literals are checked (a template literal or a
/// computed path is skipped). The walk has the same bounds as <see cref="NetworkFontPreflight"/>; past
/// a bound, or when any listing or read fails for a reason other than "that folder does not exist",
/// the answer is null and the caller proceeds exactly as before. Cancellation propagates.
/// </para>
/// </summary>
public static class StaticFilePreflight
{
    /// <summary>The tag the refusal message starts with (a tool-result tag, not a step failure code).</summary>
    public const string ErrorCode = "MISSING_STATIC_FILE";

    /// <summary>The refusal names at most this many missing paths.</summary>
    internal const int MaxReported = 20;

    private const string SourceRoot = "src";
    private const string PublicRoot = "public";

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs",
    };

    private static readonly Regex StaticFileCall = new(
        @"\bstaticFile\s*\(\s*(?<q>['""])(?<path>[^'""\r\n]{1,300})\k<q>\s*\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The model-facing refusal for <paramref name="missing"/>: names each path (and where it is used),
    /// and says how to stage it. Nothing is rendered.
    /// </summary>
    public static string RefusalMessage(IReadOnlyList<MissingStaticFile> missing)
    {
        IEnumerable<string> listed = missing.Take(MaxReported)
            .Select(m => $"public/{m.StaticPath} (staticFile in {m.SourceFile}:{m.Line})");
        string more = missing.Count > MaxReported ? $" and {missing.Count - MaxReported} more" : "";
        return $"{ErrorCode}: the composition uses files that are not in the sandbox, so nothing was rendered: " +
               string.Join("; ", listed) + more + ". " +
               "Stage each one with StageProjectFile, passing the exact id or file name ListProjectFiles shows " +
               "(and sandboxPath \"public/<that path>\" when the name differs), or point staticFile() at a file " +
               "that is already staged. Do not guess file names: only files ListProjectFiles lists exist.";
    }

    /// <summary>
    /// Every <c>staticFile</c> literal in <c>src/**</c> whose target is missing from <c>public/</c>
    /// (empty when all are present or there are none), or null when the scan could not be completed.
    /// </summary>
    public static async Task<IReadOnlyList<MissingStaticFile>?> FindMissingAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        List<MissingStaticFile>? references = await CollectReferencesAsync(session, cancellationToken);
        if (references is null)
            return null;
        if (references.Count == 0)
            return [];

        var listings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var missing = new List<MissingStaticFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MissingStaticFile reference in references)
        {
            if (!seen.Add(reference.StaticPath))
                continue;

            int slash = reference.StaticPath.LastIndexOf('/');
            string directory = slash < 0 ? PublicRoot : PublicRoot + "/" + reference.StaticPath[..slash];
            string name = slash < 0 ? reference.StaticPath : reference.StaticPath[(slash + 1)..];

            if (!listings.TryGetValue(directory, out HashSet<string>? names))
            {
                try
                {
                    IReadOnlyList<SandboxFileEntry> entries = await session.ListFilesAsync(directory, cancellationToken);
                    names = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                }
                catch (SandboxRequestFailedException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    names = [];
                }

                listings[directory] = names;
            }

            if (!names.Contains(name))
                missing.Add(reference);
        }

        return missing;
    }

    /// <summary>Every checkable <c>staticFile</c> literal in <c>src/**</c>, in path order; null past a bound or on a failed read.</summary>
    private static async Task<List<MissingStaticFile>?> CollectReferencesAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        var references = new List<MissingStaticFile>();
        var pending = new Queue<string>();
        pending.Enqueue(SourceRoot);
        int directories = 0;
        int files = 0;
        long total = 0;

        while (pending.Count > 0)
        {
            string directory = pending.Dequeue();
            if (++directories > NetworkFontPreflight.MaxDirectories)
                return null;

            IReadOnlyList<SandboxFileEntry> entries;
            try
            {
                entries = await session.ListFilesAsync(directory, cancellationToken);
            }
            catch (SandboxRequestFailedException ex) when (ex.StatusCode == HttpStatusCode.NotFound && directory == SourceRoot)
            {
                return references;
            }

            foreach (SandboxFileEntry entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name.Length == 0 || entry.Name is "." or ".." || entry.Name.IndexOfAny(['/', '\\']) >= 0)
                    continue;

                string path = directory + "/" + entry.Name;
                if (entry.IsDir)
                {
                    if (!entry.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && !entry.Name.StartsWith('.'))
                        pending.Enqueue(path);
                    continue;
                }

                if (!ScriptExtensions.Contains(Path.GetExtension(entry.Name)))
                    continue;
                if (entry.Size > NetworkFontPreflight.MaxFileBytes)
                    continue;
                if (files >= NetworkFontPreflight.MaxFiles || total + entry.Size > NetworkFontPreflight.MaxTotalBytes)
                    return null;

                string text = (await session.ReadTextAsync(path, cancellationToken)).Text;
                files++;
                total += Math.Max(entry.Size, Encoding.UTF8.GetByteCount(text));
                references.AddRange(Scan(path, text));
            }
        }

        return references;
    }

    /// <summary>The checkable <c>staticFile</c> literals of one source file. Comments never count.</summary>
    public static IReadOnlyList<MissingStaticFile> Scan(string sourceFile, string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("staticFile", StringComparison.Ordinal))
            return [];

        string code = NetworkFontPreflight.BlankComments(text, css: false);
        var found = new List<MissingStaticFile>();
        MatchCollection matches;
        try
        {
            matches = StaticFileCall.Matches(code);
            _ = matches.Count;
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }

        foreach (Match match in matches)
        {
            string? staticPath = Checkable(match.Groups["path"].Value);
            if (staticPath is null)
                continue;

            int line = 1;
            for (int i = 0; i < match.Index; i++)
            {
                if (code[i] == '\n')
                    line++;
            }

            found.Add(new MissingStaticFile(staticPath, sourceFile, line));
        }

        return found;
    }

    /// <summary>The path relative to <c>public/</c>, or null for anything this check cannot judge.</summary>
    private static string? Checkable(string raw)
    {
        string path = raw.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        path = path.TrimStart('/');

        if (path.Length == 0
            || path.Contains("${", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal)
            || path.IndexOfAny(['?', '#', '%']) >= 0
            || path.EndsWith('/'))
        {
            return null;
        }

        string[] segments = path.Split('/');
        return segments.Any(s => s.Length == 0 || s is "." or "..") ? null : path;
    }
}
