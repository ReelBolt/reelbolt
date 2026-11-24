using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.PlatformDocs;

/// <summary>Which half of the documentation a chunk comes from.</summary>
public enum PlatformDocsAudience
{
    /// <summary><c>docs/user-guide/**</c> — plain-language, user-facing.</summary>
    User,

    /// <summary>
    /// The top-level developer docs in <c>docs/*.md</c>. Internal background for the assistant only: it may
    /// read these to understand the product, but they carry no URL and their technical content is never
    /// repeated to a customer.
    /// </summary>
    Developer
}

/// <summary>One retrievable section of one documentation page.</summary>
/// <param name="Id">Stable id: the doc path plus the section ordinal, so re-indexing is deterministic.</param>
/// <param name="DocPath">Path relative to the docs root, forward slashes (e.g. <c>user-guide/recipes/tracked-screen-insert.md</c>).</param>
/// <param name="Title">The page title (front matter <c>title</c>, else the first <c># </c> heading, else the file name).</param>
/// <param name="Section">The <c>##</c> heading the chunk sits under, or empty for the page's lead.</param>
/// <param name="Url">
/// Where the page lives on the public docs site, relative to the site root (<c>/docs/...</c>). Empty for
/// every <see cref="PlatformDocsAudience.Developer"/> chunk: the developer reference is no longer
/// published, so there is no page to link to and nothing the assistant could ever say a customer may open.
/// </param>
/// <param name="Text">The section text, front matter removed.</param>
public sealed record PlatformDocsChunk(
    string Id,
    string DocPath,
    PlatformDocsAudience Audience,
    string Title,
    string Section,
    string Url,
    string Text)
{
    /// <summary>
    /// What gets embedded: the page title and section heading prefixed to the body, so a short
    /// section ("## Prerequisites") still lands near questions about its page's subject.
    /// </summary>
    public string EmbeddingText => string.IsNullOrEmpty(Section)
        ? $"{Title}\n\n{Text}"
        : $"{Title} — {Section}\n\n{Text}";

    /// <summary>Retrieval metadata (kind, step/agent types, neighbours, ...); see <see cref="PlatformDocsChunkMeta"/>.</summary>
    public PlatformDocsChunkMeta Meta { get; init; } = PlatformDocsChunkMeta.Empty;
}

/// <summary>
/// Loads the documentation tree and splits it into heading-scoped chunks. Pure and deterministic:
/// the same files always yield the same chunks and the same <see cref="CorpusHash"/>, which is what
/// lets the indexer decide "already indexed" by collection name alone.
/// </summary>
public sealed class PlatformDocsCorpus
{
    /// <summary>
    /// Bumped whenever chunking changes shape, so a new build re-indexes even if no Markdown changed.
    /// "2": developer chunks no longer carry a docs-site URL (the developer site is not public).
    /// "3": chunks carry retrieval metadata (kind, step/agent types, neighbours, ...) and the collection is hybrid.
    /// </summary>
    internal const string ChunkerVersion = "3";

    /// <summary>
    /// Top-level folders/files under the docs root that are internal working material rather than
    /// documentation — the same set the docs site excludes (docs-site/docusaurus.config.js) — plus
    /// <c>model-catalog-prices.md</c>, the generated vendor price list, because what the platform pays
    /// each vendor is internal and must never be quoted by the assistant.
    /// </summary>
    internal static readonly string[] ExcludedPrefixes =
    [
        "marketing/", "design/", "screenshots/", "archive/", "README.md", "model-catalog-prices.md"
    ];

    private static readonly Regex FrontMatterTitle = new(@"^title:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public IReadOnlyList<PlatformDocsChunk> Chunks { get; }

    /// <summary>12 hex chars of SHA-256 over every chunk (and the chunker version).</summary>
    public string CorpusHash { get; }

    public PlatformDocsCorpus(IReadOnlyList<PlatformDocsChunk> chunks)
    {
        Chunks = chunks;
        CorpusHash = ComputeHash(chunks);
    }

    public static PlatformDocsCorpus Empty { get; } = new([]);

    /// <summary>Loads every <c>.md</c> under <paramref name="root"/>; a missing root yields an empty corpus.</summary>
    public static PlatformDocsCorpus Load(string root, int maxChunkChars, bool includeGeneratedFacts = false)
    {
        if (!Directory.Exists(root))
            return Empty;

        var chunks = new List<PlatformDocsChunk>();
        IEnumerable<string> files = Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(IsIncluded)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (string relative in files)
        {
            string markdown = File.ReadAllText(System.IO.Path.Combine(root, relative));
            chunks.AddRange(ChunkDocument(relative, markdown, maxChunkChars));
        }

        if (includeGeneratedFacts)
        {
            // A bad card must never take the docs search down: the facts are an addition to the corpus.
            try
            {
                chunks.AddRange(PlatformDocsGeneratedFacts.Build());
            }
            catch (Exception)
            {
                // Indexing goes on with the documents alone; the drift-guard tests catch a bad card at build time.
            }
        }

        return new PlatformDocsCorpus(chunks);
    }

    /// <summary>
    /// User-guide pages at any depth, the assistant knowledge base (<c>assistant-kb/**</c>, developer audience =
    /// INTERNAL BACKGROUND, never quoted to customers), plus developer docs at the TOP level only — other
    /// subfolders are drafts, reviews and research, not reference material (<c>design/</c> stays excluded).
    /// </summary>
    internal static bool IsIncluded(string relativePath)
    {
        if (ExcludedPrefixes.Any(p => relativePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (relativePath.StartsWith("user-guide/", StringComparison.Ordinal)
            || relativePath.StartsWith("assistant-kb/", StringComparison.Ordinal))
            return true;
        return !relativePath.Contains('/');
    }

    /// <summary>Splits one Markdown file into chunks at <c>##</c> headings (outside code fences).</summary>
    internal static IEnumerable<PlatformDocsChunk> ChunkDocument(string relativePath, string markdown, int maxChunkChars)
    {
        markdown = markdown.Replace("\r\n", "\n");
        string? frontMatterTitle = null;
        string docVersion = PlatformDocsMetadata.DocVersionOf(markdown);
        var declared = new Dictionary<string, List<string>>();

        if (markdown.StartsWith("---\n", StringComparison.Ordinal))
        {
            int end = markdown.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                string frontMatter = markdown[4..end];
                declared = PlatformDocsMetadata.ParseKb(frontMatter);
                Match m = FrontMatterTitle.Match(frontMatter);
                if (m.Success)
                    frontMatterTitle = m.Groups[1].Value.Trim().Trim('"', '\'');
                int bodyStart = markdown.IndexOf('\n', end + 4);
                markdown = bodyStart < 0 ? string.Empty : markdown[(bodyStart + 1)..];
            }
        }

        PlatformDocsAudience audience = relativePath.StartsWith("user-guide/", StringComparison.Ordinal)
            ? PlatformDocsAudience.User
            : PlatformDocsAudience.Developer;

        // Split into (heading, body) sections, never splitting inside a fenced code block.
        var sections = new List<(string Heading, StringBuilder Body)> { ("", new StringBuilder()) };
        string? h1 = null;
        bool inFence = false;
        foreach (string line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && line.StartsWith("# ", StringComparison.Ordinal) && h1 is null)
            {
                h1 = line[2..].Trim();
                continue;
            }

            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                sections.Add((line[3..].Trim(), new StringBuilder()));
                continue;
            }

            sections[^1].Body.Append(line).Append('\n');
        }

        string title = frontMatterTitle ?? h1 ?? System.IO.Path.GetFileNameWithoutExtension(relativePath);
        string url = BuildUrl(relativePath);
        int ordinal = 0;
        var chunks = new List<PlatformDocsChunk>();

        foreach ((string heading, StringBuilder body) in sections)
        {
            string text = body.ToString().Trim();
            if (text.Length == 0)
                continue;

            foreach (string piece in SplitLong(text, Math.Max(400, maxChunkChars)))
            {
                chunks.Add(new PlatformDocsChunk(
                    $"{relativePath}#{ordinal++}",
                    relativePath,
                    audience,
                    title,
                    heading,
                    url.Length > 0 && heading.Length > 0 ? url + "#" + Anchor(heading) : url,
                    piece)
                {
                    Meta = PlatformDocsMetadata.Build(relativePath, title, heading, piece, declared, docVersion)
                });
            }
        }

        // Adjacency inside one document, for neighbour expansion of a split section.
        for (int i = 0; i < chunks.Count; i++)
        {
            yield return chunks[i] with
            {
                Meta = chunks[i].Meta with
                {
                    PrevId = i > 0 ? chunks[i - 1].Id : null,
                    NextId = i < chunks.Count - 1 ? chunks[i + 1].Id : null
                }
            };
        }
    }

    /// <summary>
    /// The page's public docs-site URL. Mirrors docs-site/docusaurus.config.js: the user guide is served at
    /// <c>/docs/</c> and an <c>index.md</c> is its folder. Developer pages have NO public URL — that site is
    /// no longer published — so they return an empty string and can never be linked.
    /// </summary>
    internal static string BuildUrl(string relativePath)
    {
        if (!relativePath.StartsWith("user-guide/", StringComparison.Ordinal))
            return string.Empty;

        string slug = relativePath["user-guide/".Length..];
        slug = slug[..^".md".Length];
        if (slug == "index")
            slug = "";
        else if (slug.EndsWith("/index", StringComparison.Ordinal))
            slug = slug[..^"/index".Length];
        return $"/docs/{slug}";
    }

    /// <summary>The heading anchor Docusaurus generates (GitHub-style slug).</summary>
    internal static string Anchor(string heading)
    {
        var sb = new StringBuilder();
        foreach (char c in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SplitLong(string text, int max)
    {
        if (text.Length <= max)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (string paragraph in text.Split("\n\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > max)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            // A single paragraph longer than the budget (a big table or code block) is hard-cut.
            string remaining = paragraph;
            while (remaining.Length > max)
            {
                yield return remaining[..max];
                remaining = remaining[max..];
            }

            current.Append(remaining).Append("\n\n");
        }

        if (current.ToString().Trim().Length > 0)
            yield return current.ToString().Trim();
    }

    private static string ComputeHash(IReadOnlyList<PlatformDocsChunk> chunks)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(ChunkerVersion + "\n"));
        foreach (PlatformDocsChunk chunk in chunks)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(chunk.Id + "\n" + chunk.Url + "\n" + chunk.EmbeddingText + "\n" + chunk.Meta.Canonical() + "\n\0"));
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }
}
