using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// Places the mandatory campaign footer (and its unsubscribe link) with an HTML5 tree builder (AngleSharp, default
/// configuration: no scripting, no CSS engine, no loader, so nothing is fetched or run). The admin's HTML is parsed, the
/// footer is appended as the LAST child of the real <c>body</c>, and the document is serialized with its doctype. An
/// unclosed div, template, select, details, plaintext, script, comment and so on can no longer contain the footer,
/// because it is attached to the <c>body</c> node and not written after the text.
/// <para>
/// The result is verified failing closed: the output is parsed again, the footer is found by its id, its parent must be
/// <c>body</c>, and the unsubscribe placeholder and the required identity strings must be in it. Otherwise
/// <see cref="CampaignRefusal"/> with a stable code: <c>html_too_deep</c> (nesting beyond <see cref="MaxDepth"/>, or a parse that overruns its time budget),
/// <c>html_no_body</c> (a frameset document has no body) or <c>footer_placement_failed</c>.
/// </para>
/// <para>
/// The size limit is checked by the caller before this runs. Depth is measured by an iterative scan of the text
/// before the parser runs (AngleSharp caps its own tree at 512 and parses deep input in quadratic time), never by a
/// recursive walk, and the serializer only runs once the depth is within the limit.
/// </para>
/// <para>
/// The serializer normalizes: attribute values are double-quoted, tag and attribute names are lower case, an implied
/// <c>html</c>/<c>head</c>/<c>body</c> and table <c>tbody</c> appear, void elements are written <c>&lt;br&gt;</c>,
/// CRLF in text is kept as parsed (HTML input preprocessing turns it into LF), and a downlevel-revealed
/// <c>&lt;![if ...]&gt;</c> becomes the comment <c>&lt;!--[if ...]--&gt;</c>. Placeholders such as
/// <c>{{ params.x }}</c> and <c>{{ unsubscribe }}</c> are plain text/attribute characters and are not encoded.
/// </para>
/// <para>
/// Out of scope by decision: an admin who hides the footer on purpose (CSS, <c>body</c> attributes), and mail-client
/// rendering. The mandatory human test send is the check for those. A <c>style</c> element in the head stays admin
/// content.
/// </para>
/// </summary>
public static class CampaignFooterPlacer
{
    /// <summary>Most open elements the admin's HTML may nest; real mail stays well under 100.</summary>
    public const int MaxDepth = 400;

    private const int ParserDepthCap = 500;
    public const string FooterId = "rb-campaign-footer";

    /// <summary>Refuses (throws) when the HTML is too deep or has no body. Used when validating a draft.</summary>
    public static void Check(string html)
    {
        using IHtmlDocument doc = Parse(html);
        if (doc.Body is not IHtmlBodyElement) throw new CampaignRefusal("html_no_body");
    }

    /// <summary>The document with <paramref name="footerHtml"/> as the last child of body. <paramref name="required"/> must all appear in the footer text.</summary>
    public static string Place(string html, string footerHtml, IReadOnlyList<string> required)
    {
        using IHtmlDocument doc = Parse(html);
        IHtmlBodyElement body = doc.Body as IHtmlBodyElement ?? throw new CampaignRefusal("html_no_body");
        IElement wrapper = doc.CreateElement("div");
        wrapper.SetAttribute("id", FooterId);
        wrapper.InnerHtml = footerHtml;
        body.AppendChild(wrapper);
        string output = doc.ToHtml();

        using IHtmlDocument check = new HtmlParser().ParseDocument(output);
        IElement? footer = check.GetElementById(FooterId);
        // the id must be unique: an id repeated in the admin's HTML would let the check look at the wrong node
        if (footer is null || check.All.Count(e => e.Id == FooterId) != 1 || footer.ParentElement is not IHtmlBodyElement || footer.ParentElement != check.Body)
            throw new CampaignRefusal("footer_placement_failed");
        bool unsub = footer.QuerySelectorAll("a").Any(a => a.GetAttribute("href") == "{{ unsubscribe }}");
        string text = footer.TextContent;
        if (!unsub || required.Any(r => !text.Contains(r, StringComparison.Ordinal)))
            throw new CampaignRefusal("footer_placement_failed");
        return output;
    }

    private static readonly TimeSpan ParseBudget = TimeSpan.FromSeconds(10);

    // Elements the tree builder closes implicitly; counting them would refuse sloppy but flat mail such as a
    // thousand unclosed paragraphs.
    private static readonly HashSet<string> ImpliedEnd = new(StringComparer.Ordinal)
    {
        "p", "li", "dt", "dd", "option", "optgroup", "tr", "td", "th", "thead", "tbody", "tfoot", "colgroup", "col",
        "caption", "rb", "rt", "rp", "rtc", "html", "head", "body",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
        "basefont", "bgsound", "frame", "keygen",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes", "noscript", "plaintext",
    };

    // AngleSharp 1.9.0 silently caps the tree at depth 512 (so measuring the parsed tree cannot see deeper input) and
    // its parse time is quadratic in nesting (200k nested divs take minutes). So depth is measured on the text before
    // the parser runs, and the parse itself has a time budget as a backstop.
    private static IHtmlDocument Parse(string html)
    {
        if (ScanDepth(html) > MaxDepth) throw new CampaignRefusal("html_too_deep");
        using CancellationTokenSource cts = new(ParseBudget);
        IHtmlDocument doc;
        try
        {
            doc = new HtmlParser().ParseDocumentAsync(html, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new CampaignRefusal("html_too_deep");
        }

        if (Depth(doc) >= ParserDepthCap)
        {
            doc.Dispose();
            throw new CampaignRefusal("html_too_deep");
        }

        return doc;
    }

    /// <summary>
    /// Iterative, linear scan of the start and end tags: the deepest nesting of open elements, skipping comments,
    /// quoted attribute values and raw-text content. A close tag pops back to the matching open element and is ignored
    /// when none is open. Stops as soon as the limit is passed.
    /// </summary>
    internal static int ScanDepth(string html)
    {
        List<string> open = [];
        int max = 0;
        int i = 0;
        int n = html.Length;
        while (i < n)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= n) break;
            char c = html[lt + 1];
            if (c == '!' || c == '?')
            {
                if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
                {
                    int end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                }
                else
                {
                    int end = html.IndexOf('>', lt + 2);
                    i = end < 0 ? n : end + 1;
                }

                continue;
            }

            bool closing = c == '/';
            int nameStart = lt + (closing ? 2 : 1);
            int p = nameStart;
            while (p < n && (char.IsAsciiLetterOrDigit(html[p]) || html[p] is ':' or '-' or '_')) p++;
            if (p == nameStart || !char.IsAsciiLetter(html[nameStart]))
            {
                i = lt + 1;
                continue;
            }

            string name = html.Substring(nameStart, p - nameStart).ToLowerInvariant();
            char quote = '\0';
            int q = p;
            for (; q < n; q++)
            {
                char ch = html[q];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (ch == '>')
                {
                    break;
                }
            }

            i = q < n ? q + 1 : n;
            bool selfClosing = q > 0 && q < n && html[q - 1] == '/';
            if (closing)
            {
                int idx = open.LastIndexOf(name);
                if (idx >= 0) open.RemoveRange(idx, open.Count - idx);
                continue;
            }

            if (RawText.Contains(name) && !selfClosing)
            {
                int close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? n : close;
                continue;
            }

            if (Void.Contains(name) || ImpliedEnd.Contains(name)) continue;
            open.Add(name);
            if (open.Count > max) max = open.Count;
            if (max > MaxDepth) return max;
        }

        return max;
    }

    private static int Depth(INode root)
    {
        int max = 0;
        Stack<(INode Node, int Depth)> stack = new();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (INode node, int d) = stack.Pop();
            if (d > max) max = d;
            if (d >= ParserDepthCap) return d;
            foreach (INode child in node.ChildNodes) stack.Push((child, d + 1));
        }

        return max;
    }
}
