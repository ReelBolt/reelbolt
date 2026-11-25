using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>security-headers T-003: the anonymous CSP/COEP violation sink, its sanitizer and its limits.</summary>
public class CspReportTests
{
    private sealed class RecordingStore : ICspReportStore
    {
        public List<CspViolationKey> Keys { get; } = [];

        public Task RecordAsync(CspViolationKey key, DateOnly day, int maxKeysPerDay, CancellationToken ct)
        {
            Keys.Add(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => 1000;
    }

    private const string Chrome = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static (TelemetryCspReportController Controller, RecordingStore Store) Build(
        byte[] body, string contentType = "application/csp-report", bool enabled = true, int burst = 60, FakeTime? time = null,
        string userAgent = Chrome, long? contentLength = null)
    {
        AlphaTelemetryOptions options = new() { CspReportEnabled = enabled, CspReportBurst = burst, CspReportRatePerSecond = 1 };
        RecordingStore store = new();
        FakeTime clock = time ?? new FakeTime();
        TelemetryCspReportController controller = new(Options.Create(options), new CspReportRateLimiter(Options.Create(options), clock), store, clock);
        DefaultHttpContext http = new();
        http.Request.ContentType = contentType;
        http.Request.ContentLength = contentLength;
        http.Request.Headers.UserAgent = userAgent;
        http.Request.Body = new System.IO.MemoryStream(body);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, store);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private const string Legacy = """
        {"csp-report":{"document-uri":"https://reelbolt.ai/pricing?utm=x#frag","referrer":"https://secret.example/","violated-directive":"img-src 'self'",
        "effective-directive":"img-src","original-policy":"default-src 'self'","disposition":"report","blocked-uri":"https://cdn.example.com:8443/a/b.png?token=abc",
        "status-code":200,"source-file":"https://reelbolt.ai/app.js","line-number":3,"script-sample":"secret"}}
        """;

    private static async Task AssertNoContent(Task<IActionResult> call)
    {
        IActionResult result = await call;
        result.Should().BeOfType<NoContentResult>("always 204 with an empty body");
    }

    // ---- sanitizer --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://reelbolt.ai//app/review/g/abc123", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/review%2Fg/abc123", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/review/g/abc123", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/review/g", "(sensitive)")]
    [InlineData("https://reelbolt.ai/api/v1/review-links/9f1c/decisions", "(sensitive)")]
    [InlineData("https://reelbolt.ai/api/v1/output-links/tok", "(sensitive)")]
    [InlineData("https://reelbolt.ai/oauth/authorize", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/reset-password", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/verify-email/xyz", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/accept-invite", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/email-change", "(sensitive)")]
    [InlineData("https://reelbolt.ai/app/editor/3fa85f64-5717-4562-b3fc-2c963f66afa6/review", "/app/editor/:id/review")]
    [InlineData("https://reelbolt.ai/app/projects/3fa85f6457174562b3fc2c963f66afa6", "/app/projects/:id")]
    [InlineData("https://reelbolt.ai/app/projects/a1b2c3d4e5f6", "/app/projects/:id")]
    [InlineData("https://reelbolt.ai/app/projects/12345/files", "/app/projects/:id/files")]
    [InlineData("https://reelbolt.ai/x/Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5", "/x/:id")]
    [InlineData("https://reelbolt.ai/pricing?a=1#b", "/pricing")]
    [InlineData("https://reelbolt.ai/?utm_source=x", "/")]
    [InlineData("https://reelbolt.ai", "/")]
    [InlineData("https://reelbolt.ai/blog/how-to-edit-video-for-youtube-in-2026", "/blog/how-to-edit-video-for-youtube-in-2026")]
    [InlineData("not a url", "other")]
    [InlineData(null, "other")]
    public void Document_paths_lose_tokens_and_ids(string? input, string expected) =>
        CspReportSanitizer.DocumentPath(input).Should().Be(expected);

    [Theory]
    [InlineData("https://cdn.example.com/a/b.png?token=abc#x", "https://cdn.example.com")]
    [InlineData("https://cdn.example.com:8443/x", "https://cdn.example.com:8443")]
    [InlineData("https://user:pw@cdn.example.com/x", "https://cdn.example.com")]
    [InlineData("wss://ws.example.com/socket?key=1", "wss://ws.example.com")]
    [InlineData("inline", "inline")]
    [InlineData("eval", "eval")]
    [InlineData("data", "data")]
    [InlineData("data:image/png;base64,AAAA", "data")]
    [InlineData("blob:https://reelbolt.ai/1234-abcd", "blob")]
    [InlineData("self", "self")]
    [InlineData("chrome-extension://abcdef/x.js", "other")]
    [InlineData("", "other")]
    [InlineData("https://bad host/x", "other")]
    public void Blocked_uris_reduce_to_an_origin_or_a_keyword(string input, string expected) =>
        CspReportSanitizer.BlockedOrigin(input).Should().Be(expected);

    [Fact]
    public void Blocked_origin_never_exceeds_its_column()
    {
        string huge = "https://" + new string('a', 150) + ".example.com/x";
        CspReportSanitizer.BlockedOrigin(huge).Should().Be("other");
    }

    [Theory]
    [InlineData("img-src 'self' https://x", "img-src")]
    [InlineData("SCRIPT-SRC-ELEM", "script-src-elem")]
    [InlineData("made-up-src", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void Directives_come_from_a_closed_vocabulary(string? input, string expected) =>
        CspReportSanitizer.Directive(input).Should().Be(expected);

    [Theory]
    [InlineData(Chrome, "chrome")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0", "edge")]
    [InlineData("Mozilla/5.0 (X11; Linux) Gecko/20100101 Firefox/131.0", "firefox")]
    [InlineData("Mozilla/5.0 (Macintosh) AppleWebKit/605.1.15 Version/17.0 Safari/605.1.15", "safari")]
    [InlineData("curl/8.0 secret-agent-name", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void User_agent_is_reduced_to_a_family(string? ua, string expected) =>
        CspReportSanitizer.UaFamily(ua).Should().Be(expected);

    // ---- endpoint ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Legacy_report_uri_body_is_stored_as_allowlisted_fields_only()
    {
        (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(Legacy));
        await AssertNoContent(c.Report(CancellationToken.None));
        store.Keys.Should().ContainSingle().Which.Should().Be(
            new CspViolationKey("csp", "report", "img-src", "https://cdn.example.com:8443", "/pricing", "chrome"));
    }

    [Fact]
    public async Task Reporting_api_array_stores_one_row_per_report_and_skips_other_types()
    {
        string body = """
            [{"type":"csp-violation","url":"https://reelbolt.ai/x","body":{"documentURL":"https://reelbolt.ai/app/login?next=/x","effectiveDirective":"script-src-elem","blockedURL":"inline","disposition":"enforce"}},
             {"type":"coep","body":{"type":"corp","blockedURL":"https://img.example.com/a.png?x=1","disposition":"report","documentURL":"https://reelbolt.ai/app/editor/12345"}},
             {"type":"deprecation","body":{"id":"x"}}]
            """;
        (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(body), "application/reports+json; charset=utf-8");
        await AssertNoContent(c.Report(CancellationToken.None));
        store.Keys.Should().Equal(
            new CspViolationKey("csp", "enforce", "script-src-elem", "inline", "/app/login", "chrome"),
            new CspViolationKey("coep", "report", "coep", "https://img.example.com", "/app/editor/:id", "chrome"));
    }

    [Fact]
    public async Task Plain_json_content_type_accepts_both_shapes()
    {
        (TelemetryCspReportController c1, RecordingStore s1) = Build(Utf8(Legacy), "application/json");
        await AssertNoContent(c1.Report(CancellationToken.None));
        s1.Keys.Should().HaveCount(1);

        string single = """{"type":"csp-violation","body":{"effectiveDirective":"img-src","blockedURL":"data","documentURL":"https://reelbolt.ai/","disposition":"report"}}""";
        (TelemetryCspReportController c2, RecordingStore s2) = Build(Utf8(single), "application/json");
        await AssertNoContent(c2.Report(CancellationToken.None));
        s2.Keys.Single().BlockedOrigin.Should().Be("data");
    }

    [Fact]
    public async Task Other_content_type_is_dropped_with_204()
    {
        (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(Legacy), "text/plain");
        await AssertNoContent(c.Report(CancellationToken.None));
        store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_json_is_204_and_stores_nothing()
    {
        foreach (string bad in new[] { "{not json", "", "[]", "null", "42", "{\"csp-report\":5}", new string('[', 500) })
        {
            (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(bad));
            await AssertNoContent(c.Report(CancellationToken.None));
            store.Keys.Should().BeEmpty(bad.Length > 20 ? "deep nesting" : bad);
        }
    }

    [Fact]
    public async Task A_body_over_the_cap_is_204_and_stores_nothing()
    {
        string padded = Legacy.Replace("\"status-code\":200", "\"status-code\":200,\"pad\":\"" + new string('x', 17000) + "\"");
        (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(padded));
        await AssertNoContent(c.Report(CancellationToken.None));
        store.Keys.Should().BeEmpty();

        // Content-Length alone is enough to refuse it unread.
        (TelemetryCspReportController c2, RecordingStore s2) = Build(Utf8(Legacy), contentLength: 100000);
        await AssertNoContent(c2.Report(CancellationToken.None));
        s2.Keys.Should().BeEmpty();

        (TelemetryCspReportController c3, RecordingStore s3) = Build(Utf8(Legacy));
        await AssertNoContent(c3.Report(CancellationToken.None));
        s3.Keys.Should().HaveCount(1, "a normal report is under the cap");
    }

    [Fact]
    public async Task The_kill_switch_answers_204_and_stores_nothing()
    {
        (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(Legacy), enabled: false);
        await AssertNoContent(c.Report(CancellationToken.None));
        store.Keys.Should().BeEmpty();
        new AlphaTelemetryOptions().CspReportEnabled.Should().BeTrue("on by default; independent of VisitIngestEnabled");
        new AlphaTelemetryOptions().VisitIngestEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_store_failure_is_still_204()
    {
        AlphaTelemetryOptions options = new();
        FakeTime clock = new();
        TelemetryCspReportController c = new(Options.Create(options), new CspReportRateLimiter(Options.Create(options), clock), new ThrowingStore(), clock);
        DefaultHttpContext http = new();
        http.Request.ContentType = "application/csp-report";
        http.Request.Body = new System.IO.MemoryStream(Utf8(Legacy));
        c.ControllerContext = new ControllerContext { HttpContext = http };
        await AssertNoContent(c.Report(CancellationToken.None));
    }

    private sealed class ThrowingStore : ICspReportStore
    {
        public Task RecordAsync(CspViolationKey key, DateOnly day, int maxKeysPerDay, CancellationToken ct) =>
            throw new InvalidOperationException("db down");
    }

    [Fact]
    public async Task Excess_reports_are_dropped_by_the_per_process_bucket_and_refill_with_time()
    {
        FakeTime clock = new();
        (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(Legacy), burst: 2, time: clock);
        await AssertNoContent(c.Report(CancellationToken.None));
        store.Keys.Should().HaveCount(1);

        // Same limiter, more requests: the bucket (burst 2, 1 token/s) empties after two.
        AlphaTelemetryOptions options = new() { CspReportBurst = 2, CspReportRatePerSecond = 1 };
        CspReportRateLimiter limiter = new(Options.Create(options), clock);
        limiter.TryAcquire().Should().BeTrue();
        limiter.TryAcquire().Should().BeTrue();
        limiter.TryAcquire().Should().BeFalse("burst spent");
        clock.Ticks += 1000; // one second at 1000 ticks/s
        limiter.TryAcquire().Should().BeTrue("one token refilled");
        limiter.TryAcquire().Should().BeFalse();

        // The endpoint answers 204 and stores nothing when the bucket is empty.
        TelemetryCspReportController dry = new(Options.Create(options), limiter, store, clock);
        DefaultHttpContext http = new();
        http.Request.ContentType = "application/csp-report";
        http.Request.Body = new System.IO.MemoryStream(Utf8(Legacy));
        dry.ControllerContext = new ControllerContext { HttpContext = http };
        int before = store.Keys.Count;
        await AssertNoContent(dry.Report(CancellationToken.None));
        store.Keys.Count.Should().Be(before);
    }

    [Fact]
    public void The_entity_has_no_column_for_any_forbidden_field()
    {
        string[] columns = typeof(CspViolationCount).GetProperties().Select(p => p.Name).ToArray();
        columns.Should().BeEquivalentTo(
            "Day", "ReportType", "Disposition", "EffectiveDirective", "BlockedOrigin", "DocumentPath", "UaFamily", "Count");
        string[] forbidden = ["Url", "Query", "Referrer", "Source", "Sample", "Line", "Column", "Policy", "Status", "Ip", "User", "Cookie", "Agent"];
        foreach (string f in forbidden)
            columns.Should().NotContain(c => c.Contains(f, StringComparison.OrdinalIgnoreCase) && c != "UaFamily", f);

        typeof(CspViolationKey).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "ReportType", "Disposition", "EffectiveDirective", "BlockedOrigin", "DocumentPath", "UaFamily");
    }

    [Fact]
    public async Task Forbidden_input_fields_never_reach_the_key()
    {
        (TelemetryCspReportController c, RecordingStore store) = Build(Utf8(Legacy));
        await AssertNoContent(c.Report(CancellationToken.None));
        string stored = string.Join('|', store.Keys.Select(k => k.ToString()));
        foreach (string leak in new[] { "token=abc", "secret", "utm=x", "frag", "app.js", "default-src", "8443/a", "b.png" })
            stored.Should().NotContain(leak);
    }
}
