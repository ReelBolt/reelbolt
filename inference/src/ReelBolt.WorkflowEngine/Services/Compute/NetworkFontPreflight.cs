using System.Text;
using System.Text.RegularExpressions;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>One network font load found in a sandbox source file.</summary>
/// <param name="Path">The workspace-relative file, e.g. <c>src/scenes/Title.tsx</c>.</param>
/// <param name="Line">The 1-based line the load is on.</param>
/// <param name="What">A short description of the load, e.g. <c>an import of @remotion/google-fonts/Inter</c>.</param>
public sealed record NetworkFontLoad(string Path, int Line, string What);

/// <summary>
/// Refuses a sandbox project that loads a font from the network, before anything renders it.
///
/// <para>
/// The sandbox has no internet. A composition that imports <c>@remotion/google-fonts</c> (which is
/// installed in the template, and fetches <c>fonts.gstatic.com</c> when the font loads), <c>@import</c>s a
/// remote stylesheet, or points an <c>@font-face</c>, a <c>new FontFace(...)</c> or
/// <c>@remotion/fonts</c>' <c>loadFont({url})</c> at a remote file does not fail at build time: it fails
/// in the middle of the render, after the browser has waited out its font timeout. This check reads
/// the project's <c>src/**</c> sources (ts, tsx, js, jsx, mjs, css) and answers with the first such load
/// it finds, so the tool can refuse in milliseconds with the file, the line and the installed families
/// (<see cref="SandboxFonts"/>) to use instead.
/// </para>
///
/// <para>
/// <b>Only loads are refused.</b> Comments are blanked before matching, and a font host or package
/// name in copy or in a string that loads nothing (no <c>import</c>/<c>require</c>, <c>@import</c>,
/// <c>url(</c>, <c>href=</c>, <c>fetch(</c>, <c>FontFace</c> or <c>loadFont</c> around it) is not a
/// load: a false refusal would block every render of the project.
/// </para>
///
/// <para>
/// <b>Bounded and fail-open.</b> The walk skips <c>node_modules</c> and dot-directories, never reads a
/// file over <see cref="MaxFileBytes"/>, and stops at <see cref="MaxTotalBytes"/>,
/// <see cref="MaxFiles"/> files or <see cref="MaxDirectories"/> directories. A source it cannot list
/// or read is skipped: the check is a fast-fail guard on top of a sandbox that has no network anyway,
/// so when it cannot look, the render goes ahead exactly as it did before. Cancellation propagates.
/// </para>
/// </summary>
public static class NetworkFontPreflight
{
    /// <summary>The tag the refusal message starts with.</summary>
    public const string ErrorCode = "NETWORK_FONT";

    /// <summary>A source file larger than this is not read.</summary>
    internal const int MaxFileBytes = 256 * 1024;

    /// <summary>The walk stops before reading more than this many bytes in total.</summary>
    internal const long MaxTotalBytes = 4L * 1024 * 1024;

    /// <summary>The walk reads at most this many files.</summary>
    internal const int MaxFiles = 400;

    /// <summary>The walk lists at most this many directories.</summary>
    internal const int MaxDirectories = 200;

    private const string Root = "src";

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs",
    };

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>A remote URL: <c>http://</c>, <c>https://</c> or protocol-relative <c>//</c>.</summary>
    private const string Remote = @"(?:https?:)?//";

    private const string FontHosts = @"(?:fonts\.googleapis\.com|fonts\.gstatic\.com|use\.typekit\.net)";

    /// <summary>The loads, in the order they are reported when two start at the same place.</summary>
    private static readonly (Regex Pattern, Func<Match, string> Describe)[] Loads =
    [
        // import ... from "@remotion/google-fonts/X", import "…", import("…"), require("…"), export … from "…".
        (new Regex(@"(?:\bfrom\s*|\bimport\s*\(?\s*|\brequire\s*\(\s*)[""'`](?<pkg>@remotion/google-fonts(?:/[^""'`\s]*)?)[""'`]", Options, MatchTimeout),
            m => "an import of " + m.Groups["pkg"].Value),

        // @import url(https://…) / @import "https://…" — any remote stylesheet.
        (new Regex(@"@import\s+(?:url\(\s*)?[""']?\s*(?<url>" + Remote + @"[^""')\s;]*)", Options, MatchTimeout),
            m => "an @import of " + Bound(m.Groups["url"].Value)),

        // A remote url() inside an @font-face block.
        (new Regex(@"@font-face\s*\{[^}]{0,4000}?url\(\s*[""']?\s*(?<url>" + Remote + @"[^""')\s]*)", Options, MatchTimeout),
            m => "an @font-face src of " + Bound(m.Groups["url"].Value)),

        // A font host inside url(…), href=…, or fetch(…).
        (new Regex(@"(?:url\(\s*[""']?|\bhref\s*=\s*\{?\s*[""'`]|\bfetch\s*\(\s*[""'`])\s*(?<url>" + Remote + FontHosts + @"[^""'`)\s]*)", Options, MatchTimeout),
            m => "a load from " + Bound(m.Groups["url"].Value)),

        // new FontFace(name, 'url(https://…)').
        (new Regex(@"\bnew\s+FontFace\s*\(\s*[^,]{1,200}?,\s*[""'`]\s*url\(\s*[""']?\s*(?<url>" + Remote + @"[^""')\s]*)", Options, MatchTimeout),
            m => "a new FontFace from " + Bound(m.Groups["url"].Value)),

        // @remotion/fonts loadFont({ ..., url: 'https://…' }).
        (new Regex(@"\bloadFont\s*\(\s*\{[^}]{0,1000}?\burl\s*:\s*[""'`]\s*(?<url>" + Remote + @"[^""'`\s]*)", Options, MatchTimeout),
            m => "a loadFont of " + Bound(m.Groups["url"].Value)),

        // Any remote url() to a font file (.woff2/.woff/.ttf/.otf/.eot), wherever it is.
        (new Regex(@"url\(\s*[""']?\s*(?<url>" + Remote + @"[^""')\s]*\.(?:woff2?|ttf|otf|eot)(?:[?#][^""')\s]*)?)[""']?\s*\)", Options, MatchTimeout),
            m => "a font file from " + Bound(m.Groups["url"].Value)),
    ];

    /// <summary>
    /// The refusal for the sandbox project <paramref name="session"/> holds, or null when it loads no
    /// font from the network (or its sources could not be read). The text is the tool's whole
    /// <c>error</c>: <c>NETWORK_FONT: &lt;file&gt;:&lt;line&gt; loads a font from the network ...</c>.
    /// </summary>
    public static async Task<string?> CheckAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        NetworkFontLoad? load = await FindAsync(session, cancellationToken);
        return load is null ? null : RefusalMessage(load);
    }

    /// <summary>
    /// The model-facing refusal for <paramref name="load"/>. <see cref="ErrorCode"/> is a tool-result tag the model reads,
    /// not a step failure code (listed in FailureClassifier.NonCodeLiterals): a refused graphic still fails as GRAPHIC_NOT_RENDERED.
    /// </summary>
    public static string RefusalMessage(NetworkFontLoad load) =>
        $"{ErrorCode}: {load.Path}:{load.Line} loads a font from the network ({load.What}), so the render would fail. " +
        "Remove that load: do not use @remotion/google-fonts, a font URL, a remote @import or @font-face. " +
        "The sandbox has no internet. Use an installed font by CSS font-family name: " + SandboxFonts.FamilyList + ", or call StageFont to stage a Google Fonts family or a project font file.";

    /// <summary>The first network font load in the project's <c>src/**</c>, in path order, or null.</summary>
    public static async Task<NetworkFontLoad?> FindAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        var pending = new Queue<string>();
        pending.Enqueue(Root);
        int directories = 0;
        int files = 0;
        long total = 0;

        while (pending.Count > 0)
        {
            string directory = pending.Dequeue();
            if (++directories > MaxDirectories)
                return null;

            IReadOnlyList<SandboxFileEntry> entries;
            try
            {
                entries = await session.ListFilesAsync(directory, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing src/ holds nothing; an unreachable sandbox is the render's problem to report.
                continue;
            }

            foreach (SandboxFileEntry entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlainName(entry.Name))
                    continue;

                string path = directory + "/" + entry.Name;
                if (entry.IsDir)
                {
                    if (!entry.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && !entry.Name.StartsWith('.'))
                        pending.Enqueue(path);
                    continue;
                }

                if (!IsScanned(entry.Name) || entry.Size > MaxFileBytes)
                    continue;
                if (files >= MaxFiles || total + entry.Size > MaxTotalBytes)
                    return null;

                string text;
                try
                {
                    text = (await session.ReadTextAsync(path, cancellationToken)).Text;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }

                files++;
                total += Math.Max(entry.Size, Encoding.UTF8.GetByteCount(text));
                if (Scan(path, text) is { } load)
                    return load;
            }
        }

        return null;
    }

    /// <summary>The first network font load in one source file, or null. Comments never count.</summary>
    public static NetworkFontLoad? Scan(string path, string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxFileBytes)
            return null;

        bool css = path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
        string code = BlankComments(text, css);

        Match? first = null;
        Func<Match, string>? describe = null;
        foreach ((Regex pattern, Func<Match, string> describeLoad) in Loads)
        {
            Match match;
            try
            {
                match = pattern.Match(code);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success)
                continue;

            // The earliest load in the file wins; on a tie, the more specific (earlier) pattern.
            if (first is null || LoadIndex(match) < LoadIndex(first))
            {
                first = match;
                describe = describeLoad;
            }
        }

        if (first is null || describe is null)
            return null;

        int index = LoadIndex(first);
        int line = 1;
        for (int i = 0; i < index; i++)
        {
            if (code[i] == '\n')
                line++;
        }

        return new NetworkFontLoad(path, line, describe(first));
    }

    /// <summary>Where the URL or package is, so a multi-line @font-face points at its src line.</summary>
    private static int LoadIndex(Match match) =>
        match.Groups["url"].Success ? match.Groups["url"].Index
        : match.Groups["pkg"].Success ? match.Groups["pkg"].Index
        : match.Index;

    private static bool IsScanned(string name) =>
        name.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || ScriptExtensions.Contains(Path.GetExtension(name));

    private static bool IsPlainName(string name) =>
        name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(['/', '\\']) < 0;

    private static string Bound(string url) => url.Length <= 120 ? url : url[..120] + "...";

    /// <summary>
    /// <paramref name="text"/> with every comment replaced by spaces (newlines kept, so line numbers
    /// hold). String and template literals are kept as they are: a load inside a template-literal
    /// <c>&lt;style&gt;</c> is still a load. CSS has only block comments (a <c>//</c> there is a URL).
    /// </summary>
    internal static string BlankComments(string text, bool css)
    {
        var output = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? text.Length : end + 2;
                Blank(output, text, i, stop);
                i = stop;
                continue;
            }

            if (!css && c == '/' && next == '/')
            {
                int end = text.IndexOf('\n', i);
                int stop = end < 0 ? text.Length : end;
                Blank(output, text, i, stop);
                i = stop;
                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                int stop = StringEnd(text, i, c);
                output.Append(text, i, stop - i);
                i = stop;
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    /// <summary>The index just past the string literal opened at <paramref name="start"/>; a quote string ends at a newline.</summary>
    private static int StringEnd(string text, int start, char quote)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == quote)
                return i + 1;
            if (c == '\n' && quote != '`')
                return i;
            i++;
        }

        return text.Length;
    }

    private static void Blank(StringBuilder output, string text, int start, int stop)
    {
        for (int i = start; i < stop; i++)
            output.Append(text[i] == '\n' ? '\n' : ' ');
    }
}
