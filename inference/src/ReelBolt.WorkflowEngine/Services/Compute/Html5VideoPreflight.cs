using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>One JSX use of the HTML5 video element component imported from <c>remotion</c>.</summary>
/// <param name="SourceFile">The workspace-relative source file, e.g. <c>src/Reel.tsx</c>.</param>
/// <param name="Line">The 1-based line of the JSX element.</param>
/// <param name="Component">The name as imported from <c>remotion</c>: <c>Video</c> or <c>Html5Video</c>.</param>
public sealed record Html5VideoUse(string SourceFile, int Line, string Component);

/// <summary>
/// Refuses a full render of a composition that plays a clip through <c>remotion</c>'s HTML5 video element
/// (<c>&lt;Video&gt;</c>, renamed <c>&lt;Html5Video&gt;</c>; both exported by the pinned 4.0.545) instead of
/// <c>&lt;OffthreadVideo&gt;</c>.
///
/// <para>
/// <b>Why.</b> During a render the HTML5 element is seeked in each browser tab and the frame is captured
/// once the seek reports done; when the browser has not painted the new picture yet, the capture holds
/// the picture that tab drew last, which is the frame <c>--concurrency</c> frames earlier (two frames at
/// the sandbox's default of 2). Production reel 2026-11-05 (sc6, a 30 fps H.264 clip with B-frames, full
/// screen with its own audio): 56 of 145 unambiguous frames showed source frame i-2 instead of i, never
/// any other offset, so the clip visibly stepped back every few frames. The same clip through
/// <c>&lt;OffthreadVideo&gt;</c> (muted or not, any Sequence offset, fractional startFrom/from, premountFor)
/// rendered frame-exact; through <c>&lt;Video&gt;</c> it showed the same i-2 frames. <c>&lt;OffthreadVideo&gt;</c>
/// extracts the exact frame with the renderer's own decoder and carries the clip's audio unless
/// <c>muted</c>, so nothing is lost by switching.
/// </para>
///
/// <para>
/// <b>Best effort, fail-open.</b> Reads <c>src/**</c> (ts, tsx, js, jsx, mjs) within the bounds of
/// <see cref="NetworkFontPreflight"/>, blanks comments, and reports a JSX element whose name was imported
/// from <c>'remotion'</c> as <c>Video</c> or <c>Html5Video</c> (aliases included). Past a bound or on a
/// failed read the answer is null and the render goes ahead as before. Cancellation propagates.
/// </para>
/// </summary>
public static class Html5VideoPreflight
{
    /// <summary>The tag the refusal message starts with (a tool-result tag, not a step failure code).</summary>
    public const string ErrorCode = "HTML5_VIDEO_IN_RENDER";

    /// <summary>The refusal names at most this many uses.</summary>
    internal const int MaxReported = 20;

    private const string SourceRoot = "src";

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs",
    };

    private static readonly HashSet<string> Html5Components = new(StringComparer.Ordinal) { "Video", "Html5Video" };

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex RemotionNamedImport = new(
        @"\bimport\s+(?:type\s+)?(?:[A-Za-z_$][\w$]*\s*,\s*)?\{(?<names>[^}]{0,4000})\}\s*from\s*(?<q>['""])remotion\k<q>",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex ImportSpecifier = new(
        @"^\s*(?:type\s+)?(?<name>[A-Za-z_$][\w$]*)(?:\s+as\s+(?<alias>[A-Za-z_$][\w$]*))?\s*$",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// The model-facing refusal for <paramref name="uses"/>: names each use and says exactly what to write
    /// instead. Nothing is rendered.
    /// </summary>
    public static string RefusalMessage(IReadOnlyList<Html5VideoUse> uses)
    {
        IEnumerable<string> listed = uses.Take(MaxReported).Select(u => $"<{u.Component}> in {u.SourceFile}:{u.Line}");
        string more = uses.Count > MaxReported ? $" and {uses.Count - MaxReported} more" : "";
        return $"{ErrorCode}: the composition plays video through remotion's HTML5 <Video>/<Html5Video>, which shows " +
               "stale frames in a render (the clip steps back a frame or two every few frames), so nothing was rendered: " +
               string.Join("; ", listed) + more + ". " +
               "Replace each one with <OffthreadVideo> imported from 'remotion', keeping src, style and the timing props " +
               "(trimBefore/trimAfter or startFrom/endAt, playbackRate, volume). <OffthreadVideo> plays the clip's own " +
               "audio unless you pass muted, so a clip that must be heard needs no separate <Audio>. It has no loop prop: " +
               "for a loop wrap it in <Loop durationInFrames={...}>. Then call RenderVideoAndUploadToStorage again.";
    }

    /// <summary>
    /// Every use of the HTML5 video component in <c>src/**</c> (empty when there is none), or null when the
    /// scan could not be completed.
    /// </summary>
    public static async Task<IReadOnlyList<Html5VideoUse>?> FindAsync(ISandboxSession session, CancellationToken cancellationToken)
    {
        var uses = new List<Html5VideoUse>();
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
                return uses;
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
                uses.AddRange(Scan(path, text));
            }
        }

        return uses;
    }

    /// <summary>The JSX uses of the HTML5 video component in one source file. Comments never count.</summary>
    public static IReadOnlyList<Html5VideoUse> Scan(string sourceFile, string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("remotion", StringComparison.Ordinal) || !text.Contains("Video", StringComparison.Ordinal))
            return [];

        string code = NetworkFontPreflight.BlankComments(text, css: false);
        try
        {
            // Local name -> the name remotion exports it as.
            var locals = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match import in RemotionNamedImport.Matches(code))
            {
                foreach (string specifier in import.Groups["names"].Value.Split(','))
                {
                    Match s = ImportSpecifier.Match(specifier);
                    if (!s.Success || !Html5Components.Contains(s.Groups["name"].Value))
                        continue;
                    string local = s.Groups["alias"].Success ? s.Groups["alias"].Value : s.Groups["name"].Value;
                    locals[local] = s.Groups["name"].Value;
                }
            }

            if (locals.Count == 0)
                return [];

            var found = new List<Html5VideoUse>();
            foreach ((string local, string exported) in locals)
            {
                var element = new Regex(@"<\s*" + Regex.Escape(local) + @"(?=[\s/>])", RegexOptions.CultureInvariant, MatchTimeout);
                foreach (Match use in element.Matches(code))
                    found.Add(new Html5VideoUse(sourceFile, LineOf(code, use.Index), exported));
            }

            return [.. found.OrderBy(u => u.Line)];
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    private static int LineOf(string code, int index)
    {
        int line = 1;
        for (int i = 0; i < index; i++)
        {
            if (code[i] == '\n')
                line++;
        }

        return line;
    }
}
