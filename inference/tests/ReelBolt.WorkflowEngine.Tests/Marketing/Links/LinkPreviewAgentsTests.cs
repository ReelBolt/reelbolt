#nullable enable
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing.Links;

/// <summary>
/// Which requests a tracked link counts as <c>bot_hits</c> instead of <c>clicks</c>. The user-agent
/// strings below are the shapes the named fetchers send; none is stored anywhere by the product.
/// </summary>
public class LinkPreviewAgentsTests
{
    private const string Chrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    [Fact]
    public void The_list_is_the_documented_one()
    {
        LinkPreviewAgents.KnownAgents.Should().Equal(
            "Slackbot", "Twitterbot", "facebookexternalhit", "LinkedInBot", "Discordbot", "TelegramBot",
            "WhatsApp", "Googlebot", "bingbot");
        LinkPreviewAgents.GenericTokens.Should().Equal("bot", "crawler", "spider", "preview");
        LinkPreviewAgents.SpeculativePurposes.Should().Equal("prefetch", "prerender", "preview");
        LinkPreviewAgents.OtherFetchers.Should().Contain(
            ["Mastodon", "Pleroma", "Akkoma", "Misskey", "Lemmy", "Cardyb", "HeadlessChrome", "Google-InspectionTool", "Microsoft Office"]);
        LinkPreviewAgents.HttpLibraries.Should().Contain(
            ["curl", "Wget", "python-requests", "Go-http-client", "node-fetch", "okhttp", "http.rb"]);
    }

    // The agents a review ran against the first version of this class, which filed every one as a click.
    [Theory]
    [InlineData("curl/8.5.0")]
    [InlineData("Wget/1.21.4")]
    [InlineData("python-requests/2.32.3")]
    [InlineData("Python-urllib/3.12")]
    [InlineData("python-httpx/0.27.0")]
    [InlineData("Go-http-client/2.0")]
    [InlineData("node-fetch/1.0 (+https://github.com/bitinn/node-fetch)")]
    [InlineData("undici")]
    [InlineData("axios/1.7.2")]
    [InlineData("okhttp/4.12.0")]
    [InlineData("Java/17.0.9")]
    [InlineData("Apache-HttpClient/4.5.14 (Java/17.0.9)")]
    [InlineData("http.rb/5.2.0")]
    [InlineData("Dalvik/2.1.0 (Linux; U; Android 14; Pixel 8 Build/UQ1A.240205.002)")]
    [InlineData("MailApp/3.2 CFNetwork/1496.0.7 Darwin/23.5.0")]
    [InlineData("Mastodon/4.3.0 (http.rb/5.2.0; +https://mastodon.social/)")]
    [InlineData("Pleroma 2.6.2; https://example.social <admin@example.social>")]
    [InlineData("Akkoma 3.13.2; https://example.social <admin@example.social>")]
    [InlineData("Misskey/2024.5.0 (https://example.social)")]
    [InlineData("Lemmy/0.19.5; +https://example.social")]
    [InlineData("Mozilla/5.0 (compatible; Bluesky Cardyb/1.1; +mailto:support@bsky.app)")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/126.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (compatible; Google-InspectionTool/1.0)")]
    [InlineData("Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36 (compatible; GoogleOther)")]
    [InlineData("Mozilla/4.0 (compatible; MSIE 7.0; Windows NT 10.0; Win64; x64; Trident/7.0; .NET4.0C; .NET4.0E; Microsoft Outlook 16.0.17328; ms-office; MSOffice 16)")]
    [InlineData("Microsoft Office/16.0 (Windows NT 10.0; Microsoft Outlook 16.0.17328; Pro)")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Chrome-Lighthouse")]
    [InlineData("Iframely/1.3.1 (+https://iframely.com/docs/about)")]
    public void A_library_a_fediverse_server_a_headless_browser_and_office_software_are_bots(string userAgent)
    {
        LinkPreviewAgents.IsBot(userAgent).Should().BeTrue();
        LinkPreviewAgents.IsBot(new LinkRequestSignals(userAgent, "navigate", "document")).Should().BeTrue(
            "a named fetcher is a bot whatever else it sends");
    }

    [Theory]
    [InlineData("navigate", "document", false)] // a person opening the link in a tab
    [InlineData("Navigate", " document ", false)] // header values are compared without case or outer white space
    [InlineData(null, null, false)] // a browser that sends no fetch metadata is judged by its user agent
    [InlineData("", "", false)]
    [InlineData("navigate", null, false)]
    [InlineData("cors", "empty", true)] // a script's fetch()
    [InlineData("no-cors", "image", true)] // an <img> pointed at the link
    [InlineData("no-cors", "empty", true)] // a beacon or a link checker inside a page
    [InlineData("navigate", "iframe", true)] // loaded into a frame, not opened
    [InlineData("navigate", "embed", true)]
    [InlineData("same-origin", "document", true)]
    [InlineData(null, "image", true)]
    public void With_fetch_metadata_only_a_page_navigation_is_a_click(string? mode, string? dest, bool bot)
    {
        LinkPreviewAgents.IsBot(new LinkRequestSignals(Chrome, mode, dest)).Should().Be(bot);
    }

    [Theory]
    [InlineData("prefetch", null, null, null)] // Sec-Purpose: a speculative load
    [InlineData("prefetch;prerender", null, null, null)] // Chrome prerender
    [InlineData("prefetch; anonymous-client-ip", null, null, null)] // a private prefetch proxy
    [InlineData(null, "prefetch", null, null)] // Purpose: the older spelling
    [InlineData(null, "preview", null, null)]
    [InlineData(null, null, "preview", null)] // X-Purpose: Safari's top-sites preview
    [InlineData(null, null, null, "prefetch")] // X-Moz: Firefox link prefetch
    [InlineData("PREFETCH", null, null, null)]
    public void A_prefetch_or_a_preview_is_not_a_click_even_from_a_real_browser(string? secPurpose, string? purpose, string? xPurpose, string? xMoz)
    {
        LinkPreviewAgents.IsBot(new LinkRequestSignals(Chrome, "navigate", "document", secPurpose, purpose, xPurpose, xMoz))
            .Should().BeTrue("nobody has looked at a page that was only loaded ahead of time");
    }

    [Fact]
    public void A_browser_opening_the_link_is_a_click_with_or_without_fetch_metadata()
    {
        LinkPreviewAgents.IsBot(new LinkRequestSignals(Chrome)).Should().BeFalse();
        LinkPreviewAgents.IsBot(new LinkRequestSignals(Chrome, "navigate", "document")).Should().BeFalse();
        LinkPreviewAgents.IsBot(new LinkRequestSignals(Chrome, "navigate", "document", "", "", "", "")).Should().BeFalse(
            "an absent header reads as an empty string");
        LinkPreviewAgents.IsBot(new LinkRequestSignals(null, "navigate", "document")).Should().BeTrue(
            "no user agent is a bot whatever the other headers claim");
    }

    [Theory]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)")]
    [InlineData("Twitterbot/1.0")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [InlineData("LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)")]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)")]
    [InlineData("TelegramBot (like TwitterBot)")]
    [InlineData("WhatsApp/2.23.20.0 A")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    public void Each_named_fetcher_is_a_bot(string userAgent)
    {
        LinkPreviewAgents.IsBot(userAgent).Should().BeTrue();
    }

    [Theory]
    [InlineData("SomeNewBot/3.1")]
    [InlineData("acme-crawler/0.4")]
    [InlineData("Mozilla/5.0 (compatible; YandexSpider/1.0)")]
    [InlineData("Skype for Business URL Preview Service")]
    [InlineData("FACEBOOKEXTERNALHIT/1.1")] // matching ignores case
    public void A_generic_token_marks_a_bot(string userAgent)
    {
        LinkPreviewAgents.IsBot(userAgent).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_user_agent_is_a_bot(string? userAgent)
    {
        LinkPreviewAgents.IsBot(userAgent).Should().BeTrue("a browser always sends a user agent");
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36")]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/22A3354 Instagram 350.0.0.0 (iPhone16,2; iOS 18_0; en_US; en; scale=3.00; 1290x2796)")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/FBIOS;FBAV/480.0.0;FBDV/iPhone16,2]")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [LinkedInApp]/9.30.1")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0.6723.90 Mobile/15E148 Safari/604.1")]
    public void A_browser_is_a_click(string userAgent)
    {
        LinkPreviewAgents.IsBot(userAgent).Should().BeFalse();
    }
}
