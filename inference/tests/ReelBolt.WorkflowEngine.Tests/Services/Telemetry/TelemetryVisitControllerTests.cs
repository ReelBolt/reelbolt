using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

public class TelemetryVisitControllerTests
{
    private const string Vid = "f47ac10b-58cc-4372-a567-0e02b2c3d479";

    private static TelemetryVisitController Controller(RecordingProductEventLog log, bool enabled = true, long? contentLength = null, VisitEventDto? bodyDto = null, byte[]? raw = null)
    {
        var c = new TelemetryVisitController(
            Options.Create(new AlphaTelemetryOptions { VisitIngestEnabled = enabled }), log);
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("app.reelbolt.ai");
        http.Request.ContentLength = contentLength;
        http.Request.Body = new System.IO.MemoryStream(raw ?? System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
            bodyDto ?? Body(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    private static VisitEventDto Body(string ev = "site.visit", string? vid = Vid) =>
        new() { Event = ev, VisitorId = vid, Source = "Google", ReferrerHost = "news.ycombinator.com" };

    [Fact]
    public async Task Off_by_default_answers_404_and_writes_nothing()
    {
        var log = new RecordingProductEventLog();
        (await Controller(log, enabled: false).Visit(CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        log.Rows.Should().BeEmpty();
        new AlphaTelemetryOptions().VisitIngestEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_valid_visit_is_204_with_a_sanitized_label_and_no_user_or_org()
    {
        var log = new RecordingProductEventLog();
        (await Controller(log).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        ProductEvent row = log.Rows.Single();
        row.Event.Should().Be("site.visit");
        row.Source.Should().Be(ProductEventSources.Client);
        row.Detail.Should().Be("google", "the label is the lowercased utm_source");
        row.VisitorId.Should().Be(Guid.Parse(Vid));
        row.UserId.Should().BeNull();
        row.OrganizationId.Should().BeNull();
    }

    [Fact]
    public async Task Invalid_attribution_is_dropped_not_rejected()
    {
        var log = new RecordingProductEventLog();
        VisitEventDto body = Body("signup.started");
        body.Source = "bad value!";
        body.ReferrerHost = "app.reelbolt.ai";
        (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        log.Rows.Single().Detail.Should().Be("direct", "a bad source and a self-referral both drop to direct");
    }

    [Fact]
    public async Task A_referrer_host_becomes_the_label_when_there_is_no_utm()
    {
        var log = new RecordingProductEventLog();
        VisitEventDto body = Body();
        body.Source = null;
        await Controller(log, bodyDto: body).Visit(CancellationToken.None);
        log.Rows.Single().Detail.Should().Be("news.ycombinator.com");
    }

    [Theory]
    [InlineData("alpha.signup")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unknown_event_is_400(string? ev)
    {
        var log = new RecordingProductEventLog();
        (await Controller(log, bodyDto: Body(ev!)).Visit(CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        log.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_bad_visitor_id_is_400(string? vid)
    {
        var log = new RecordingProductEventLog();
        (await Controller(log, bodyDto: Body(vid: vid)).Visit(CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversize_body_is_400()
    {
        var log = new RecordingProductEventLog();
        (await Controller(log, contentLength: 2049).Visit(CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        log.Rows.Should().BeEmpty();
    }

    [Fact]
    public void The_endpoint_is_anonymous()
        => typeof(TelemetryVisitController).GetMethod("Visit")!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Should().NotBeEmpty();

    [Fact]
    public async Task A_malformed_body_is_404_when_disabled_and_400_when_enabled()
    {
        var log = new RecordingProductEventLog();
        byte[] junk = System.Text.Encoding.UTF8.GetBytes("{not json");
        (await Controller(log, enabled: false, raw: junk).Visit(CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await Controller(log, raw: junk).Visit(CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repeat_of_the_same_visitor_and_event_is_204_with_one_row()
    {
        var log = new RecordingProductEventLog();
        for (int i = 0; i < 3; i++)
            (await Controller(log).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        log.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_different_event_or_a_row_older_than_30_minutes_is_recorded()
    {
        var log = new RecordingProductEventLog();
        await Controller(log).Visit(CancellationToken.None);
        await Controller(log, bodyDto: Body("signup.started")).Visit(CancellationToken.None);
        log.Rows.Should().HaveCount(2);

        log.Rows[0].OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-31);
        await Controller(log).Visit(CancellationToken.None);
        log.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_source_equal_to_an_own_host_is_not_a_label()
    {
        var log = new RecordingProductEventLog();
        VisitEventDto body = Body();
        body.Source = "www.reelbolt.ai";
        body.ReferrerHost = null;
        await Controller(log, bodyDto: body).Visit(CancellationToken.None);
        log.Rows.Single().Detail.Should().Be("direct");
    }

    // ---- the five visitor events, their detail and the first touch (docs/design/new-user-funnel.md 4 and 6) ----

    [Fact]
    public void The_visit_endpoint_accepts_exactly_the_five_visitor_events()
        => ProductEventNames.VisitEvents.Should().Equal(
            "site.visit", "site.page_view", "site.cta_click", "signup.started", "signup.submitted");

    [Theory]
    [InlineData("site.visit", null, "google")]
    [InlineData("signup.started", null, "google")]
    [InlineData("site.page_view", "for-creators", "for-creators")]
    [InlineData("site.page_view", "blog:how-we-cut-a-film", "blog:how-we-cut-a-film")]
    [InlineData("site.cta_click", "pricing-creator", "pricing-creator")]
    [InlineData("signup.submitted", null, null)]
    public async Task Each_event_is_recorded_with_the_detail_that_belongs_to_it(string ev, string? sent, string? stored)
    {
        var log = new RecordingProductEventLog();
        VisitEventDto body = Body(ev);
        body.Detail = sent;
        (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        ProductEvent row = log.Rows.Single();
        row.Event.Should().Be(ev);
        row.Detail.Should().Be(stored);
        row.Source.Should().Be(ProductEventSources.Client);
        row.VisitorId.Should().Be(Guid.Parse(Vid));
        row.UserId.Should().BeNull();
        row.OrganizationId.Should().BeNull();
    }

    [Theory]
    [InlineData("site.visit", "google")]
    [InlineData("signup.started", "google")]
    [InlineData("signup.submitted", null)]
    public async Task A_detail_sent_with_an_event_that_has_none_of_its_own_is_ignored(string ev, string? stored)
    {
        var log = new RecordingProductEventLog();
        VisitEventDto body = Body(ev);
        body.Detail = "pricing";
        (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        log.Rows.Single().Detail.Should().Be(stored, "the server decides this event's detail, never the caller");
    }

    public static TheoryData<string?> BadDetails => new()
    {
        null,
        "",
        " ",
        "Home",
        " home",
        "home ",
        "home\n",
        "-home",
        "for creators",
        "blog/slug",
        "caf\u00e9",
        "<script>",
        new string('a', 65),
    };

    [Theory]
    [MemberData(nameof(BadDetails))]
    public async Task A_page_view_or_cta_click_without_a_valid_detail_token_is_400_and_writes_nothing(string? detail)
    {
        foreach (string ev in new[] { "site.page_view", "site.cta_click" })
        {
            var log = new RecordingProductEventLog();
            VisitEventDto body = Body(ev);
            body.Detail = detail;
            IActionResult result = await Controller(log, bodyDto: body).Visit(CancellationToken.None);
            result.Should().BeOfType<BadRequestObjectResult>()
                .Which.Value.Should().BeEquivalentTo(new { error = "invalid_detail" });
            log.Rows.Should().BeEmpty("the row would mean nothing without its page key or CTA location");
            log.FirstTouches.Should().BeEmpty("a refused beacon stores nothing at all");
        }
    }

    [Fact]
    public async Task A_detail_token_may_be_64_characters_and_use_the_whole_alphabet()
    {
        var log = new RecordingProductEventLog();
        string longest = "a" + new string('9', 63);
        foreach (string detail in new[] { longest, "blog:a.b_c-d", "0" })
        {
            VisitEventDto body = Body("site.page_view");
            body.Detail = detail;
            (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        }

        log.Rows.Select(r => r.Detail).Should().Equal(longest, "blog:a.b_c-d", "0");
    }

    [Fact]
    public async Task The_first_event_of_a_visitor_stores_their_sanitized_first_touch()
    {
        var log = new RecordingProductEventLog();
        var body = new VisitEventDto
        {
            Event = "site.page_view",
            VisitorId = Vid,
            Detail = "home",
            Source = " HackerNews ",
            Medium = "Community",
            Campaign = "launch-2026-11",
            Content = "hn-launch",
            Term = "video+editor",
            LandingPath = "/For/Software-Teams?utm_source=hackernews#top",
            ReferrerHost = "News.Ycombinator.com",
        };
        DateTimeOffset before = DateTimeOffset.UtcNow;
        (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();

        VisitorAttribution touch = log.FirstTouches.Values.Single();
        touch.VisitorId.Should().Be(Guid.Parse(Vid));
        touch.SourceLabel.Should().Be("hackernews");
        touch.UtmSource.Should().Be("hackernews");
        touch.UtmMedium.Should().Be("community");
        touch.UtmCampaign.Should().Be("launch-2026-11");
        touch.UtmContent.Should().Be("hn-launch", "on a tracked link utm_content is the link code");
        touch.UtmTerm.Should().Be("video+editor");
        touch.LandingPath.Should().Be("/for/software-teams", "the query and the fragment never reach the table");
        touch.ReferrerHost.Should().Be("news.ycombinator.com");
        touch.FirstTouchAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        touch.CreatedAt.Should().Be(touch.FirstTouchAt);
        log.Rows.Single().Detail.Should().Be("home", "the page key is the event's detail; the label lives on the first touch");
    }

    [Fact]
    public async Task Invalid_first_touch_fields_are_dropped_and_own_hosts_are_never_a_source()
    {
        var log = new RecordingProductEventLog();
        var body = new VisitEventDto
        {
            Event = "site.visit",
            VisitorId = Vid,
            Source = "app.reelbolt.ai",
            Medium = "bad value!",
            Campaign = new string('c', 65),
            Content = "-leading-dash",
            Term = "<script>",
            LandingPath = "//evil.example/path",
            ReferrerHost = "www.reelbolt.ai",
        };
        (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();

        VisitorAttribution touch = log.FirstTouches.Values.Single();
        touch.SourceLabel.Should().Be("direct");
        touch.UtmSource.Should().BeNull("the site's own host is not an acquisition source");
        touch.UtmMedium.Should().BeNull();
        touch.UtmCampaign.Should().BeNull();
        touch.UtmContent.Should().BeNull();
        touch.UtmTerm.Should().BeNull();
        touch.LandingPath.Should().BeNull();
        touch.ReferrerHost.Should().BeNull("a self-referral is not an acquisition source");
        log.Rows.Single().Detail.Should().Be("direct");
    }

    [Theory]
    [InlineData("/pricing\n?utm_source=x")]
    [InlineData("/pricing\n#top")]
    [InlineData("/pricing\r\n?x=1")]
    public async Task A_landing_path_with_a_line_feed_before_the_query_is_dropped_and_the_visit_is_kept(string landingPath)
    {
        var log = new RecordingProductEventLog();
        var body = new VisitEventDto { Event = "site.visit", VisitorId = Vid, Source = "hackernews", LandingPath = landingPath };
        (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();

        log.FirstTouches.Values.Single().LandingPath.Should().BeNull("a control character never reaches a column BI reads");
        log.Rows.Single().Detail.Should().Be("hackernews", "a bad landing path must not lose the visit");
    }

    [Fact]
    public async Task No_first_touch_field_is_ever_wider_than_its_column()
    {
        // The reported case: the widest path the shape allows, a line feed, then a query. It used to pass
        // as 121 characters, which varchar(120) refuses, so the anonymous endpoint answered 500.
        var log = new RecordingProductEventLog();
        var body = new VisitEventDto
        {
            Event = "site.visit",
            VisitorId = Vid,
            Source = new string('s', 64) + "\n",
            Medium = new string('m', 64) + "\nx",
            LandingPath = "/" + new string('a', 119) + "\n?x=1",
            ReferrerHost = new string('h', 100) + "\n.example",
        };
        (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();

        VisitorAttribution touch = log.FirstTouches.Values.Single();
        (touch.LandingPath?.Length ?? 0).Should().BeLessThanOrEqualTo(120);
        (touch.UtmSource?.Length ?? 0).Should().BeLessThanOrEqualTo(64);
        (touch.UtmMedium?.Length ?? 0).Should().BeLessThanOrEqualTo(64);
        (touch.ReferrerHost?.Length ?? 0).Should().BeLessThanOrEqualTo(100);
        touch.SourceLabel.Length.Should().BeLessThanOrEqualTo(64);
        foreach (string? stored in new[] { touch.LandingPath, touch.UtmSource, touch.UtmMedium, touch.ReferrerHost, touch.SourceLabel })
        {
            (stored ?? string.Empty).Any(char.IsControl).Should().BeFalse();
        }

        touch.LandingPath.Should().BeNull();
        touch.UtmMedium.Should().BeNull();
        touch.ReferrerHost.Should().BeNull();
    }

    [Fact]
    public async Task The_host_the_request_arrived_on_is_an_own_host_too()
    {
        var log = new RecordingProductEventLog();
        var c = new TelemetryVisitController(
            Options.Create(new AlphaTelemetryOptions { VisitIngestEnabled = true, OwnHosts = [] }), log);
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("staging.example.test", 8443);
        http.Request.Body = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"event\":\"site.visit\",\"visitorId\":\"" + Vid + "\",\"source\":\"staging.example.test\",\"referrerHost\":\"www.staging.example.test\"}"));
        c.ControllerContext = new ControllerContext { HttpContext = http };

        (await c.Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        log.FirstTouches.Values.Single().SourceLabel.Should().Be("direct");
        log.FirstTouches.Values.Single().ReferrerHost.Should().BeNull();
    }

    [Fact]
    public async Task The_first_touch_wins_and_later_events_carry_their_own_detail()
    {
        var log = new RecordingProductEventLog();
        await Controller(log).Visit(CancellationToken.None);

        VisitEventDto later = Body("site.cta_click");
        later.Source = "reddit";
        later.Content = "other-link";
        later.Detail = "hero";
        await Controller(log, bodyDto: later).Visit(CancellationToken.None);

        log.FirstTouches.Should().HaveCount(1);
        log.FirstTouches.Values.Single().SourceLabel.Should().Be("google", "a later beacon never rewrites the first touch");
        log.Rows.Select(r => (r.Event, r.Detail)).Should().Equal(("site.visit", "google"), ("site.cta_click", "hero"));
    }

    [Fact]
    public async Task A_repeat_is_dropped_per_visitor_event_and_detail()
    {
        var log = new RecordingProductEventLog();

        async Task Send(string ev, string? detail, string vid = Vid)
        {
            VisitEventDto body = Body(ev, vid);
            body.Detail = detail;
            (await Controller(log, bodyDto: body).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>();
        }

        await Send("site.page_view", "home");
        await Send("site.page_view", "home");
        await Send("site.page_view", "pricing");
        await Send("site.cta_click", "pricing");
        await Send("site.cta_click", "pricing");
        await Send("signup.submitted", null);
        await Send("signup.submitted", null);
        await Send("site.page_view", "home", "0b0e7c5e-6d0f-4a0c-9d55-2c9a4b1f0a11");

        log.Rows.Select(r => (r.Event, r.Detail)).Should().Equal(
            ("site.page_view", "home"),
            ("site.page_view", "pricing"),
            ("site.cta_click", "pricing"),
            ("signup.submitted", null),
            ("site.page_view", "home"));

        // Outside the window the same page view counts again.
        log.Rows[0].OccurredAt = DateTimeOffset.UtcNow - TelemetryVisitController.DedupeWindow - TimeSpan.FromMinutes(1);
        await Send("site.page_view", "home");
        log.Rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task The_new_fields_fit_inside_the_unchanged_two_kilobyte_cap()
    {
        TelemetryVisitController.MaxBodyBytes.Should().Be(2048);
        TelemetryVisitController.DedupeWindow.Should().Be(TimeSpan.FromMinutes(30));

        var log = new RecordingProductEventLog();
        string filler = new('a', 64);
        var widest = new VisitEventDto
        {
            Event = "site.page_view",
            VisitorId = Vid,
            Source = filler,
            Medium = filler,
            Campaign = filler,
            Content = filler,
            Term = filler,
            Detail = filler,
            LandingPath = "/" + new string('p', 119),
            ReferrerHost = new string('h', 100),
        };
        (await Controller(log, bodyDto: widest).Visit(CancellationToken.None)).Should().BeOfType<NoContentResult>(
            "a body whose every field is at its widest stored size is far below the cap");
        log.Rows.Should().HaveCount(1);

        byte[] oversize = System.Text.Encoding.UTF8.GetBytes("{\"event\":\"site.visit\",\"visitorId\":\"" + Vid + "\",\"pad\":\"" + new string('x', 2048) + "\"}");
        (await Controller(new RecordingProductEventLog(), raw: oversize).Visit(CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>(
            "the cap is on the bytes read, with or without a Content-Length");
    }
}
