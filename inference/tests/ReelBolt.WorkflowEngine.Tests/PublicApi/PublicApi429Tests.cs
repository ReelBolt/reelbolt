using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.PublicApi;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>T-029: every public 429 carries Retry-After, RateLimit-Reset and error.resetsAt, in agreement.</summary>
public sealed class PublicApi429Tests
{
    private sealed class SourceWithRetryAfter(object value, int seconds) : ObjectResult(value), IRetryAfterResult
    {
        public int RetryAfterSeconds => seconds;
    }

    internal static void AssertConsistent(HttpResponse response, ObjectResult result, int? expectedSeconds = null)
    {
        result.StatusCode.Should().Be(429);
        JsonObject error = ((JsonObject)result.Value!)["error"]!.AsObject();
        string retryAfter = response.Headers.RetryAfter.ToString();
        retryAfter.Should().NotBeNullOrEmpty();
        response.Headers["RateLimit-Reset"].ToString().Should().Be(retryAfter);
        error["retryAfterSeconds"]!.GetValue<int>().ToString().Should().Be(retryAfter);
        DateTimeOffset.Parse(error["resetsAt"]!.GetValue<string>()).Offset.Should().Be(TimeSpan.Zero);
        if (expectedSeconds is int s) retryAfter.Should().Be(s.ToString());
    }

    [Fact]
    public void A_plain_429_gets_the_default_horizon_on_all_three()
    {
        DefaultHttpContext http = new();
        ObjectResult shaped = PublicApiEnvelope.RateLimited(http.Response,
            PublicApiEnvelope.Result(429, "run_queue_full", "Too many queued."));
        AssertConsistent(http.Response, shaped, PublicApiEnvelope.DefaultRetryAfterSeconds);
    }

    [Fact]
    public void The_helper_overwrites_a_per_minute_window_RateLimit_Reset_set_by_the_guard()
    {
        DefaultHttpContext http = new();
        http.Response.Headers["RateLimit-Reset"] = "55";
        ObjectResult shaped = PublicApiEnvelope.RateLimited(http.Response,
            PublicApiEnvelope.Result(429, "x", "y", new { retryAfterSeconds = 7 }));
        AssertConsistent(http.Response, shaped, 7);
    }

    [Fact]
    public void A_source_Retry_After_survives_PublicApiResults_From()
    {
        DefaultHttpContext http = new();
        ObjectResult source = new SourceWithRetryAfter(new { code = "TOO_DEMANDING_LIMIT", message = "m" }, 42) { StatusCode = 429 };
        ObjectResult shaped = PublicApiEnvelope.RateLimited(http.Response, PublicApiResults.From(source));
        AssertConsistent(http.Response, shaped, 42);
        ((JsonObject)shaped.Value!)["error"]!["code"]!.GetValue<string>().Should().Be("TOO_DEMANDING_LIMIT");
    }

    [Fact]
    public void A_source_without_Retry_After_gets_the_default()
    {
        DefaultHttpContext http = new();
        ObjectResult source = new ObjectResult(new { code = "PLAN_CONCURRENCY", message = "m" }) { StatusCode = 429 };
        ObjectResult shaped = PublicApiEnvelope.RateLimited(http.Response, PublicApiResults.From(source));
        AssertConsistent(http.Response, shaped, PublicApiEnvelope.DefaultRetryAfterSeconds);
    }

    [Fact]
    public void A_future_resetsAt_from_the_source_drives_the_horizon()
    {
        DefaultHttpContext http = new();
        ObjectResult shaped = PublicApiEnvelope.RateLimited(http.Response,
            PublicApiEnvelope.RateLimitedResult(DateTimeOffset.UtcNow.AddSeconds(90), "webhook_test_rate_limited", "slow down"));
        AssertConsistent(http.Response, shaped);
        int seconds = int.Parse(http.Response.Headers.RetryAfter.ToString());
        seconds.Should().BeInRange(88, 91);
    }

    [Fact]
    public void No_public_controller_returns_a_raw_429()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inference", "src"))) dir = Path.GetDirectoryName(dir)!;
        dir.Should().NotBeNull("the repo root must be reachable from the test output");
        string controllers = Path.Combine(dir!, "inference", "src", "ReelBolt.Inference.Api", "Controllers", "PublicApi");

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(controllers, "*.cs").Where(f => Path.GetFileName(f) != "PublicApiControllerBase.cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool is429 = line.Contains("Status429TooManyRequests") || line.Contains("StatusCode(429") || line.Contains("StatusCode = 429");
                if (!is429) continue;
                bool allowed = line.Contains("ProducesResponseType")
                    || line.Contains("PublicError(")
                    // `{ StatusCode = 429 }` closing an ObjectResult that is the argument of PublicRefusal(...)
                    || (line.Contains("StatusCode = StatusCodes.Status429TooManyRequests") && IsInsideRefusal(lines, i));
                if (!allowed) offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        offenders.Should().BeEmpty("every public 429 must go through PublicError/PublicRefusal (the one helper)");
    }

    private static bool IsInsideRefusal(string[] lines, int index)
    {
        for (int i = index; i >= Math.Max(0, index - 15); i--)
            if (lines[i].Contains("PublicRefusal(")) return true;
        return false;
    }
}
