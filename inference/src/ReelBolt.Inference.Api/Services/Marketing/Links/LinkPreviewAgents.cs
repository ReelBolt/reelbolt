namespace ReelBolt.Inference.Api.Services.Marketing.Links;

/// <summary>
/// What a request to a tracked link says about how it was made, as far as the counter needs to know:
/// the user agent and the browser's own "what kind of request is this" headers. Read once to choose the
/// counter column and dropped; none of it is stored or logged.
/// </summary>
/// <param name="UserAgent"><c>User-Agent</c>.</param>
/// <param name="SecFetchMode"><c>Sec-Fetch-Mode</c>: <c>navigate</c> when a browser opens the address as a page.</param>
/// <param name="SecFetchDest"><c>Sec-Fetch-Dest</c>: <c>document</c> for a page in a tab.</param>
/// <param name="SecPurpose"><c>Sec-Purpose</c>: <c>prefetch</c> (with or without <c>prerender</c>) for a speculative load.</param>
/// <param name="Purpose"><c>Purpose</c>, the older spelling of the same (<c>prefetch</c>, <c>preview</c>).</param>
/// <param name="XPurpose"><c>X-Purpose</c>, Safari's spelling (<c>preview</c>).</param>
/// <param name="XMoz"><c>X-Moz</c>, Firefox's spelling (<c>prefetch</c>).</param>
public readonly record struct LinkRequestSignals(
    string? UserAgent,
    string? SecFetchMode = null,
    string? SecFetchDest = null,
    string? SecPurpose = null,
    string? Purpose = null,
    string? XPurpose = null,
    string? XMoz = null);

/// <summary>
/// Tells a person opening a tracked link in a browser from everything else that fetches it, so a link's
/// <c>bot_hits</c> can be counted apart from its <c>clicks</c> (docs/design/new-user-funnel.md section 6).
/// A link pasted into a chat or a post is fetched by the platform before any person clicks it, once per
/// server on a federated network, and every link in a newsletter is opened by mail software; counting
/// those fetches as clicks would overstate exactly the channels that are compared with each other.
/// <para>
/// A hit is a <b>click</b> only when nothing says otherwise. It is a <b>bot hit</b> when:
/// </para>
/// <list type="bullet">
/// <item><description>there is no user agent (a browser always sends one);</description></item>
/// <item><description>the user agent carries one of the names below (case-insensitive substring): a
/// link-preview fetcher, a crawler, a scripted or headless browser, or an HTTP library or command-line
/// tool;</description></item>
/// <item><description>the browser says the load is speculative (<c>Sec-Purpose</c>, <c>Purpose</c>,
/// <c>X-Purpose</c>, <c>X-Moz</c>): a prefetch or a preview nobody has looked at;</description></item>
/// <item><description>the browser sends fetch metadata and it is not a page being opened:
/// <c>Sec-Fetch-Mode</c> other than <c>navigate</c>, or <c>Sec-Fetch-Dest</c> other than
/// <c>document</c> (a script's <c>fetch</c>, an image tag, a frame).</description></item>
/// </list>
/// <para>
/// A browser that sends no fetch metadata (older Safari, some in-app browsers) is judged by its user agent
/// alone, as is a request that reached this API through an edge that does not forward those headers.
/// </para>
/// <para>
/// <b>What this cannot see.</b> Software that presents itself as an ordinary browser opening a page is
/// counted as a click: some mail security scanners do exactly that. <c>clicks</c> is therefore "opened
/// like a person would", an upper bound on people, never a count of people. The rule errs toward
/// <c>bot_hits</c> wherever it can tell.
/// </para>
/// </summary>
public static class LinkPreviewAgents
{
    /// <summary>
    /// The named link-preview fetchers and search crawlers: Slack, X, Facebook (also Instagram and
    /// Threads), LinkedIn, Discord, Telegram, WhatsApp, Google and Bing.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownAgents =
    [
        "Slackbot",
        "Twitterbot",
        "facebookexternalhit",
        "LinkedInBot",
        "Discordbot",
        "TelegramBot",
        "WhatsApp",
        "Googlebot",
        "bingbot",
    ];

    /// <summary>
    /// Fetchers whose user agent carries none of the generic tokens. The fediverse servers (one post is
    /// fetched once by every server it federates to) and Bluesky's link cards; Google's and Yahoo's
    /// tools that are not named "bot"; embed services; mail and office software that opens a link before
    /// the person's browser does; scripted and headless browsers; page-speed and uptime checkers.
    /// </summary>
    public static readonly IReadOnlyList<string> OtherFetchers =
    [
        "Mastodon",
        "Pleroma",
        "Akkoma",
        "Misskey",
        "Lemmy",
        "Friendica",
        "Cardyb",
        "Google-InspectionTool",
        "GoogleOther",
        "Google-Read-Aloud",
        "Google-Safety",
        "FeedFetcher-Google",
        "Mediapartners-Google",
        "APIs-Google",
        "Google Favicon",
        "Slurp",
        "ia_archiver",
        "Iframely",
        "Embedly",
        "Microsoft Office",
        "ms-office",
        "HeadlessChrome",
        "PhantomJS",
        "Puppeteer",
        "Playwright",
        "Selenium",
        "Chrome-Lighthouse",
        "PageSpeed",
        "GTmetrix",
        "Pingdom",
        "StatusCake",
        "Site24x7",
    ];

    /// <summary>
    /// HTTP libraries and command-line tools. No browser sends any of these; a script, a server or an
    /// app's own network code does.
    /// </summary>
    public static readonly IReadOnlyList<string> HttpLibraries =
    [
        "curl",
        "Wget",
        "HTTPie",
        "python-requests",
        "python-urllib",
        "python-httpx",
        "aiohttp",
        "Go-http-client",
        "node-fetch",
        "node.js",
        "undici",
        "axios",
        "got (",
        "okhttp",
        "http.rb",
        "Faraday",
        "Ruby",
        "Java/",
        "Apache-HttpClient",
        "libwww-perl",
        "Guzzle",
        "Dalvik",
        "CFNetwork",
        "Deno/",
    ];

    /// <summary>Generic tokens that mark an automated fetcher whatever its name.</summary>
    public static readonly IReadOnlyList<string> GenericTokens = ["bot", "crawler", "spider", "preview"];

    /// <summary>The words that mark a speculative load in <c>Sec-Purpose</c>, <c>Purpose</c>, <c>X-Purpose</c> or <c>X-Moz</c>.</summary>
    public static readonly IReadOnlyList<string> SpeculativePurposes = ["prefetch", "prerender", "preview"];

    /// <summary>
    /// Whether <paramref name="userAgent"/> alone marks an automated fetcher. Null, empty and
    /// white-space-only count as one: a browser always sends a user agent.
    /// </summary>
    public static bool IsBot(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return true;
        return ContainsAny(userAgent, KnownAgents)
            || ContainsAny(userAgent, OtherFetchers)
            || ContainsAny(userAgent, HttpLibraries)
            || ContainsAny(userAgent, GenericTokens);
    }

    /// <summary>
    /// Whether the request is anything but a person opening the link as a page: the user-agent rule of
    /// <see cref="IsBot(string?)"/>, a speculative load, or fetch metadata that is not a page navigation.
    /// </summary>
    public static bool IsBot(LinkRequestSignals request)
    {
        if (IsBot(request.UserAgent)) return true;
        if (IsSpeculative(request.SecPurpose) || IsSpeculative(request.Purpose)
            || IsSpeculative(request.XPurpose) || IsSpeculative(request.XMoz))
        {
            return true;
        }

        // Fetch metadata is judged only when the browser sent it. A present header with another value is
        // a script, an image tag or a frame, never a person opening the link in a tab.
        if (IsPresentAndNot(request.SecFetchMode, "navigate")) return true;
        return IsPresentAndNot(request.SecFetchDest, "document");
    }

    private static bool IsSpeculative(string? header) =>
        !string.IsNullOrWhiteSpace(header) && ContainsAny(header, SpeculativePurposes);

    private static bool IsPresentAndNot(string? header, string expected) =>
        !string.IsNullOrWhiteSpace(header) && !header.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, IReadOnlyList<string> tokens)
    {
        foreach (string token in tokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
