#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.Shared.Data.Models.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing.Links;

/// <summary>
/// <c>GET /api/v1/l/{code}</c> (docs/design/new-user-funnel.md section 6) against the real
/// <see cref="TrackedLinkService"/> on a relational test database: what an active, archived, unknown and
/// malformed code answer, which counter a hit lands in, and that nothing but the counter is written.
/// </summary>
public sealed class TrackedLinkRedirectControllerTests : IDisposable
{
    private const string Browser =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private const string LongPath =
        "/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=software";

    private readonly MarketingHarness _h = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly RecordingLogger<TrackedLinkRedirectController> _log = new();

    public void Dispose()
    {
        foreach (IDisposable d in _disposables) d.Dispose();
        _h.Dispose();
    }

    private InferenceApiDbContext Db()
    {
        InferenceApiDbContext db = _h.NewDb();
        _disposables.Add(db);
        return db;
    }

    private Guid AddLink(Action<MarketingLink>? change = null)
    {
        using InferenceApiDbContext db = _h.NewDb();
        MarketingLink link = new()
        {
            Id = Guid.NewGuid(),
            Code = "hn-launch",
            Channel = "hackernews",
            Medium = "community",
            Campaign = "launch-2026-11",
            Placement = "Show HN, first comment",
            DestinationPath = "/for/software-teams",
            Persona = "software",
            CreatedAt = _h.Now,
        };
        change?.Invoke(link);
        db.MarketingLinks.Add(link);
        db.SaveChanges();
        return link.Id;
    }

    private List<MarketingLinkDailyStat> Stats()
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingLinkDailyStats.AsNoTracking().OrderBy(s => s.Day).ToList();
    }

    private TrackedLinkRedirectController Controller(
        bool enabled = true, string? userAgent = Browser, string method = "GET", ITrackedLinkDirectory? directory = null,
        params (string Name, string Value)[] headers)
    {
        TrackedLinkRedirectController c = new(
            Options.Create(new MarketingLinksOptions { Enabled = enabled }),
            directory ?? new TrackedLinkService(Db(), _h.Clock),
            _log,
            _h.Clock);
        DefaultHttpContext http = new();
        http.Request.Method = method;
        http.Request.Host = new HostString("reelbolt.ai");
        http.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        if (userAgent is not null) http.Request.Headers.UserAgent = userAgent;
        foreach ((string name, string value) in headers) http.Request.Headers[name] = value;
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    private static void ShouldRedirectTo(TrackedLinkRedirectController c, IActionResult result, string location)
    {
        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status302Found);
        c.Response.Headers.Location.ToString().Should().Be(location);
        c.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task An_active_link_counts_one_click_and_redirects_to_its_landing_page_with_its_utm_parameters()
    {
        Guid id = AddLink();
        TrackedLinkRedirectController c = Controller();

        IActionResult result = await c.Follow("hn-launch", CancellationToken.None);

        ShouldRedirectTo(c, result, LongPath);
        MarketingLinkDailyStat row = Stats().Single();
        row.LinkId.Should().Be(id);
        row.Day.Should().Be(DateOnly.FromDateTime(_h.Now), "the counter is per UTC day");
        row.Clicks.Should().Be(1);
        row.BotHits.Should().Be(0);
    }

    [Fact]
    public async Task The_location_is_relative_so_the_redirect_can_only_stay_on_the_host_it_came_to()
    {
        AddLink();
        TrackedLinkRedirectController c = Controller();
        await c.Follow("hn-launch", CancellationToken.None);

        string location = c.Response.Headers.Location.ToString();
        location.Should().StartWith("/").And.NotStartWith("//");
        location.Should().NotContain("://").And.NotContain("reelbolt.ai");
    }

    [Fact]
    public async Task A_link_without_a_persona_redirects_without_a_for_parameter()
    {
        AddLink(l => { l.Persona = null; l.DestinationPath = "/"; });
        TrackedLinkRedirectController c = Controller();

        ShouldRedirectTo(
            c,
            await c.Follow("hn-launch", CancellationToken.None),
            "/?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch");
    }

    [Fact]
    public async Task Repeated_clicks_add_to_the_same_day_and_a_new_day_gets_its_own_row()
    {
        Guid id = AddLink();
        await Controller().Follow("hn-launch", CancellationToken.None);
        await Controller().Follow("hn-launch", CancellationToken.None);
        await Controller(userAgent: "Twitterbot/1.0").Follow("hn-launch", CancellationToken.None);
        DateOnly first = DateOnly.FromDateTime(_h.Now);

        _h.Clock.Advance(TimeSpan.FromDays(1));
        await Controller().Follow("hn-launch", CancellationToken.None);

        Stats().Select(s => (s.LinkId, s.Day, s.Clicks, s.BotHits)).Should().Equal(
            (id, first, 2, 1),
            (id, first.AddDays(1), 1, 0));
    }

    [Theory]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)")]
    [InlineData("facebookexternalhit/1.1")]
    [InlineData("WhatsApp/2.23.20.0 A")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("acme-crawler/0.4")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_link_preview_or_crawler_is_redirected_too_but_counted_as_a_bot_hit(string? userAgent)
    {
        AddLink();
        TrackedLinkRedirectController c = Controller(userAgent: userAgent);

        ShouldRedirectTo(c, await c.Follow("hn-launch", CancellationToken.None), LongPath);
        MarketingLinkDailyStat row = Stats().Single();
        row.Clicks.Should().Be(0, "a preview fetch is not a person's click");
        row.BotHits.Should().Be(1);
    }

    [Theory]
    [InlineData("curl/8.5.0")]
    [InlineData("python-requests/2.32.3")]
    [InlineData("Go-http-client/2.0")]
    [InlineData("Mastodon/4.3.0 (http.rb/5.2.0; +https://mastodon.social/)")]
    [InlineData("Mozilla/5.0 (compatible; Bluesky Cardyb/1.1; +mailto:support@bsky.app)")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/126.0.0.0 Safari/537.36")]
    [InlineData("Microsoft Office/16.0 (Windows NT 10.0; Microsoft Outlook 16.0.17328; Pro)")]
    public async Task A_library_a_fediverse_server_and_mail_software_are_bot_hits_not_clicks(string userAgent)
    {
        AddLink();
        TrackedLinkRedirectController c = Controller(userAgent: userAgent);

        ShouldRedirectTo(c, await c.Follow("hn-launch", CancellationToken.None), LongPath);
        Stats().Single().Should().Match<MarketingLinkDailyStat>(s => s.Clicks == 0 && s.BotHits == 1);
    }

    [Fact]
    public async Task A_browser_navigation_is_a_click_and_the_same_browser_fetching_or_prefetching_is_a_bot_hit()
    {
        Guid id = AddLink();

        // A person opening the link in a tab: fetch metadata says page navigation.
        await Controller(headers: [("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "document"), ("Sec-Fetch-User", "?1")])
            .Follow("hn-launch", CancellationToken.None);
        // The same browser, but a script's fetch(), an image tag, a prerender and a Firefox prefetch.
        await Controller(headers: [("Sec-Fetch-Mode", "cors"), ("Sec-Fetch-Dest", "empty")]).Follow("hn-launch", CancellationToken.None);
        await Controller(headers: [("Sec-Fetch-Mode", "no-cors"), ("Sec-Fetch-Dest", "image")]).Follow("hn-launch", CancellationToken.None);
        await Controller(headers: [("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "document"), ("Sec-Purpose", "prefetch;prerender")])
            .Follow("hn-launch", CancellationToken.None);
        TrackedLinkRedirectController prefetch = Controller(headers: [("X-Moz", "prefetch")]);
        ShouldRedirectTo(prefetch, await prefetch.Follow("hn-launch", CancellationToken.None), LongPath);

        MarketingLinkDailyStat row = Stats().Single();
        (row.LinkId, row.Clicks, row.BotHits).Should().Be((id, 1, 4));
    }

    [Fact]
    public async Task A_head_request_is_answered_like_a_get_and_counted_as_a_bot_hit()
    {
        AddLink();
        TrackedLinkRedirectController c = Controller(method: "HEAD");

        ShouldRedirectTo(c, await c.Follow("hn-launch", CancellationToken.None), LongPath);
        Stats().Single().Should().Match<MarketingLinkDailyStat>(s => s.Clicks == 0 && s.BotHits == 1);
    }

    [Fact]
    public void The_action_answers_get_and_head_only()
    {
        MethodInfo follow = typeof(TrackedLinkRedirectController).GetMethod(nameof(TrackedLinkRedirectController.Follow))!;
        follow.GetCustomAttribute<AcceptVerbsAttribute>()!.HttpMethods.Should().BeEquivalentTo("GET", "HEAD");
        follow.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be(
            "{**code}", "everything after /l/ is the code, so a mangled address is a malformed code and never a routing 404");
        follow.GetParameters().Single(p => p.Name == "code").ParameterType.Should().Be(typeof(string));
        new System.Reflection.NullabilityInfoContext().Create(follow.GetParameters().Single(p => p.Name == "code")).WriteState
            .Should().Be(System.Reflection.NullabilityState.Nullable, "a required route value would answer 400 for an empty code");
        typeof(TrackedLinkRedirectController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/v1/l");
    }

    [Fact]
    public async Task An_archived_link_redirects_to_the_home_page_and_counts_nothing()
    {
        AddLink(l => l.ArchivedAt = _h.Now);
        TrackedLinkRedirectController c = Controller();

        ShouldRedirectTo(c, await c.Follow("hn-launch", CancellationToken.None), "/");
        Stats().Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_code_redirects_to_the_home_page_and_is_never_a_404()
    {
        AddLink();
        TrackedLinkRedirectController c = Controller();

        ShouldRedirectTo(c, await c.Follow("no-such-link", CancellationToken.None), "/");
        Stats().Should().BeEmpty();
    }

    [Theory]
    [InlineData("HN-LAUNCH")]
    [InlineData("HN-Launch")]
    [InlineData("hN-lAuNcH")]
    public async Task A_code_typed_in_another_letter_case_is_the_same_link_on_every_edge(string typed)
    {
        // nginx forwards the path as typed and the Pages Function lowers it first: both must reach the link.
        Guid id = AddLink();
        TrackedLinkRedirectController c = Controller();

        ShouldRedirectTo(c, await c.Follow(typed, CancellationToken.None), LongPath);
        MarketingLinkDailyStat row = Stats().Single();
        (row.LinkId, row.Clicks, row.BotHits).Should().Be((id, 1, 0));
        c.Response.Headers.Location.ToString().Should().Contain("utm_content=hn-launch", "the campaign value is the stored code, never the typed one");
    }

    [Fact]
    public async Task The_code_reaches_the_directory_already_lowered()
    {
        AddLink();
        ThrowingDirectory directory = new() { Inner = new TrackedLinkService(Db(), _h.Clock) };

        await Controller(directory: directory).Follow("HN-Launch", CancellationToken.None);

        directory.FoundCodes.Should().Equal("hn-launch");
    }

    [Theory]
    [InlineData("HN-LAUNCH")]
    [InlineData("Hn-Launch")]
    public async Task The_directory_itself_finds_a_link_whatever_the_letter_case(string typed)
    {
        Guid id = AddLink();
        TrackedLinkService service = new(Db(), _h.Clock);

        (await service.FindActiveAsync(typed, CancellationToken.None))!.Id.Should().Be(id);
        (await service.FindActiveAsync("hn-launch", CancellationToken.None))!.Id.Should().Be(id);
        (await service.FindActiveAsync("HN_LAUNCH", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_archived_link_stays_archived_in_any_letter_case()
    {
        AddLink(l => l.ArchivedAt = _h.Now);
        TrackedLinkRedirectController c = Controller();

        ShouldRedirectTo(c, await c.Follow("HN-LAUNCH", CancellationToken.None), "/");
        Stats().Should().BeEmpty();
    }

    [Theory]
    [InlineData("A")] // still too short after lowering
    [InlineData("-HN")]
    [InlineData("HN_LAUNCH")]
    [InlineData("HN LAUNCH")]
    [InlineData("a")]
    [InlineData("-hn")]
    [InlineData("hn_launch")]
    [InlineData("hn launch")]
    [InlineData("hn-launch%0d%0aset-cookie:x")]
    [InlineData("../../etc/passwd")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 41 characters
    [InlineData("hn-launch/extra")] // more path segments after the code
    [InlineData("")]
    [InlineData(null)] // nothing after /l/
    public async Task A_malformed_code_redirects_to_the_home_page_without_touching_the_database(string? code)
    {
        AddLink();
        ThrowingDirectory directory = new() { FailFind = true, FailRecord = true };
        TrackedLinkRedirectController c = Controller(directory: directory);

        ShouldRedirectTo(c, await c.Follow(code, CancellationToken.None), "/");
        directory.FindCalls.Should().Be(0, "a code that cannot name a link is not looked up");
        Stats().Should().BeEmpty();
    }

    [Fact]
    public async Task While_the_switch_is_off_every_code_is_a_bare_404_and_nothing_is_counted()
    {
        AddLink();
        TrackedLinkRedirectController c = Controller(enabled: false);

        (await c.Follow("hn-launch", CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await Controller(enabled: false).Follow("no-such-link", CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        c.Response.Headers.Location.Count.Should().Be(0);
        Stats().Should().BeEmpty();
    }

    [Fact]
    public void The_switch_is_on_by_default()
    {
        new MarketingLinksOptions().Enabled.Should().BeTrue();
        MarketingLinksOptions.SectionName.Should().Be("Marketing:Links");
    }

    [Fact]
    public async Task A_counter_that_fails_never_breaks_the_redirect()
    {
        Guid id = AddLink();
        ThrowingDirectory directory = new() { Inner = new TrackedLinkService(Db(), _h.Clock), FailRecord = true };
        TrackedLinkRedirectController c = Controller(directory: directory);

        ShouldRedirectTo(c, await c.Follow("hn-launch", CancellationToken.None), LongPath);
        directory.RecordCalls.Should().Be(1);
        Stats().Should().BeEmpty();
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_link_that_cannot_be_read_falls_back_to_the_home_page()
    {
        AddLink();
        ThrowingDirectory directory = new() { FailFind = true };
        TrackedLinkRedirectController c = Controller(directory: directory);

        ShouldRedirectTo(c, await c.Follow("hn-launch", CancellationToken.None), "/?utm_content=hn-launch");
        directory.RecordCalls.Should().Be(0);
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_swallowed()
    {
        AddLink();
        ThrowingDirectory directory = new() { CancelFind = true };

        Func<Task> act = () => Controller(directory: directory).Follow("hn-launch", CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")]
    [InlineData("/l/hn-launch")]
    [InlineData("/L/hn-launch")] // the edge matches /l/ in any letter case: this is the link itself
    [InlineData("/api/v1/l/hn-launch")] // and this is this very route
    public async Task A_stored_destination_that_is_not_an_own_origin_path_is_never_followed(string destination)
    {
        // The admin API never writes such a row; this is the redirect's own check on what it reads.
        MarketingLink stored = new()
        {
            Id = Guid.NewGuid(),
            Code = "hn-launch",
            Channel = "hackernews",
            Medium = "community",
            Campaign = "launch-2026-11",
            Placement = "p",
            DestinationPath = destination,
        };
        ThrowingDirectory directory = new() { Stored = stored };
        TrackedLinkRedirectController c = Controller(directory: directory);

        ShouldRedirectTo(c, await c.Follow("hn-launch", CancellationToken.None), "/");
        directory.RecordCalls.Should().Be(0, "a link that is not followed is not counted");
    }

    [Fact]
    public async Task Nothing_about_the_caller_is_stored_or_logged()
    {
        AddLink();
        await Controller().Follow("hn-launch", CancellationToken.None);
        await Controller(directory: new ThrowingDirectory { Inner = new TrackedLinkService(Db(), _h.Clock), FailRecord = true })
            .Follow("hn-launch", CancellationToken.None);

        // The counter row is a link id, a day and two numbers: there is no column to hold a caller.
        using InferenceApiDbContext db = _h.NewDb();
        db.Model.FindEntityType(typeof(MarketingLinkDailyStat))!.GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo("LinkId", "Day", "Clicks", "BotHits");
        db.ProductEvents.Count().Should().Be(0);
        db.VisitorAttributions.Count().Should().Be(0);
        foreach ((LogLevel _, EventId _, string message) in _log.Entries)
        {
            message.Should().NotContain("Mozilla").And.NotContain("203.0.113.7");
        }
    }

    [Fact]
    public void The_endpoint_is_anonymous()
    {
        typeof(TrackedLinkRedirectController).GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
        typeof(TrackedLinkRedirectController).GetCustomAttribute<AuthorizeAttribute>().Should().BeNull();
    }

    /// <summary>A directory that fails on demand, or answers a fixed link; otherwise it passes through to <see cref="Inner"/>.</summary>
    private sealed class ThrowingDirectory : ITrackedLinkDirectory
    {
        public ITrackedLinkDirectory? Inner { get; init; }
        public MarketingLink? Stored { get; init; }
        public bool FailFind { get; init; }
        public bool CancelFind { get; init; }
        public bool FailRecord { get; init; }
        public int FindCalls { get; private set; }
        public int RecordCalls { get; private set; }

        public List<string> FoundCodes { get; } = [];

        public Task<MarketingLink?> FindActiveAsync(string code, CancellationToken ct)
        {
            FindCalls++;
            FoundCodes.Add(code);
            if (CancelFind) throw new OperationCanceledException();
            if (FailFind) throw new InvalidOperationException("the database is not reachable");
            if (Stored is not null) return Task.FromResult<MarketingLink?>(Stored);
            return Inner!.FindActiveAsync(code, ct);
        }

        public Task RecordHitAsync(Guid linkId, DateOnly day, bool bot, CancellationToken ct)
        {
            RecordCalls++;
            if (FailRecord) throw new InvalidOperationException("relation \"marketing_link_daily_stats\" does not exist");
            return Inner!.RecordHitAsync(linkId, day, bot, ct);
        }
    }
}
