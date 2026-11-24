#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// The bearer middleware and the concurrency gate, through the real pipeline. The middleware is the only thing
/// between an unauthenticated caller and the zip/PDF parsers, so a regression that widens the /health exemption
/// or drops the gate must fail here.
/// </summary>
[Collection(ProcessWideAllocationCollection.Name)]
public class WorkerHostTests
{
    private const string MinimalPdf = "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n";
    private readonly ITestOutputHelper _output;

    public WorkerHostTests(ITestOutputHelper output) => _output = output;

    private static HttpRequestMessage Request(HttpMethod method, string path, string? authorization = null, byte[]? body = null)
    {
        HttpRequestMessage request = new(method, path);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (body is not null)
            request.Content = new ByteArrayContent(body);
        return request;
    }

    // ------------------------------------------------------------------ bearer middleware

    [Theory]
    [InlineData("POST", "/v1/extract-zip")]
    [InlineData("POST", "/v1/pdf-text")]
    [InlineData("GET", "/v1/extract-zip")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/unknown")]
    [InlineData("GET", "/healthz")]
    [InlineData("GET", "/health/")]
    [InlineData("GET", "/health.json")]
    [InlineData("GET", "/health/extra")]
    public async Task No_token_is_a_bare_401_on_every_path_except_health(string method, string path)
    {
        await using WorkerServer server = await WorkerServer.StartAsync();

        using HttpResponseMessage response = await server.Client.SendAsync(Request(new HttpMethod(method), path, body: method == "POST" ? new byte[] { 1 } : null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer ")]
    [InlineData("Bearer")]
    [InlineData("Basic test-worker-token")]
    [InlineData("test-worker-token")]
    [InlineData("Bearer test-worker-token-and-more")]
    [InlineData("Bearer test-worker-toke")]
    public async Task Wrong_or_malformed_token_is_401_on_both_work_routes(string authorization)
    {
        await using WorkerServer server = await WorkerServer.StartAsync();

        foreach (string path in new[] { "/v1/extract-zip", "/v1/pdf-text" })
        {
            using HttpResponseMessage response = await server.Client.SendAsync(
                Request(HttpMethod.Post, path, authorization, new byte[] { 1, 2, 3 }));
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        }
    }

    [Fact]
    public async Task Health_is_public_and_the_only_public_route()
    {
        await using WorkerServer server = await WorkerServer.StartAsync();

        using HttpResponseMessage anonymous = await server.Client.SendAsync(Request(HttpMethod.Get, "/health"));
        using HttpResponseMessage wrong = await server.Client.SendAsync(Request(HttpMethod.Get, "/health", "Bearer nope"));

        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        wrong.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Health_route_matching_is_case_insensitive_like_the_route_itself()
    {
        // PathString equality is case-insensitive, so the exemption covers exactly the route that exists.
        await using WorkerServer server = await WorkerServer.StartAsync();

        using HttpResponseMessage response = await server.Client.SendAsync(Request(HttpMethod.Get, "/Health"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Valid_token_reaches_the_handlers()
    {
        await using WorkerServer server = await WorkerServer.StartAsync();
        string auth = $"Bearer {WorkerServer.Token}";

        using HttpResponseMessage notPdf = await server.Client.SendAsync(
            Request(HttpMethod.Post, "/v1/pdf-text", auth, Encoding.UTF8.GetBytes("not a pdf")));
        using HttpResponseMessage zip = await server.Client.SendAsync(
            Request(HttpMethod.Post, "/v1/extract-zip", auth, ZipFixtures.Build(("a.css", Encoding.UTF8.GetBytes("a{color:red}")))));

        notPdf.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        zip.StatusCode.Should().Be(HttpStatusCode.OK);
        (await zip.Content.ReadAsStringAsync()).Should().Contain("a.css");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Process_refuses_to_start_with_a_blank_token(string token)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "ReelBolt.ImportWorker.dll");
        File.Exists(dll).Should().BeTrue("the worker is a project reference of the test project");

        ProcessStartInfo start = new("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["IMPORT_WORKER_TOKEN"] = token;

        using Process process = Process.Start(start)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("a worker with a blank token must exit, not serve");
        }

        process.ExitCode.Should().NotBe(0);
        (await stderr + await stdout).Should().Contain("IMPORT_WORKER_TOKEN is required");
    }

    // ------------------------------------------------------------------ concurrency gate

    private sealed class BlockingRunner : IPdfTextRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public bool ThrowOnRun { get; set; }

        public int Entered => Volatile.Read(ref _entered);

        public void Release() => _release.TrySetResult();

        public async Task<PdfTextRunResult> RunAsync(string path, WorkerOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref _entered);
            if (ThrowOnRun)
                throw new InvalidOperationException("runner exploded");

            await _release.Task.WaitAsync(ct);
            return new PdfTextRunResult(true, "ok", false, null);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Requests_beyond_the_concurrency_limit_get_503_with_retry_after_and_slots_are_reused()
    {
        BlockingRunner runner = new();
        await using WorkerServer server = await WorkerServer.StartAsync(new WorkerOptions(WorkerServer.Token, MaxConcurrency: 2), runner);
        string auth = $"Bearer {WorkerServer.Token}";
        byte[] pdf = Encoding.ASCII.GetBytes(MinimalPdf);

        Task<HttpResponseMessage> first = server.Client.SendAsync(Request(HttpMethod.Post, "/v1/pdf-text", auth, pdf));
        Task<HttpResponseMessage> second = server.Client.SendAsync(Request(HttpMethod.Post, "/v1/pdf-text", auth, pdf));
        await WaitUntil(() => runner.Entered == 2, "two pdf requests inside the runner");

        using HttpResponseMessage thirdPdf = await server.Client.SendAsync(Request(HttpMethod.Post, "/v1/pdf-text", auth, pdf));
        using HttpResponseMessage thirdZip = await server.Client.SendAsync(
            Request(HttpMethod.Post, "/v1/extract-zip", auth, ZipFixtures.Build(("a.css", new byte[] { 0x61 }))));

        thirdPdf.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        thirdPdf.Headers.RetryAfter.Should().NotBeNull();
        thirdZip.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "zip and pdf share one gate");
        runner.Entered.Should().Be(2, "the saturated requests never reached the parser");

        runner.Release();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
        (await second).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage afterwards = await server.Client.SendAsync(Request(HttpMethod.Post, "/v1/pdf-text", auth, pdf));
        afterwards.StatusCode.Should().Be(HttpStatusCode.OK, "released slots are reusable");
    }

    [Fact]
    public async Task A_handler_that_throws_still_releases_its_slot()
    {
        BlockingRunner runner = new() { ThrowOnRun = true };
        await using WorkerServer server = await WorkerServer.StartAsync(new WorkerOptions(WorkerServer.Token, MaxConcurrency: 1), runner);
        string auth = $"Bearer {WorkerServer.Token}";
        byte[] pdf = Encoding.ASCII.GetBytes(MinimalPdf);

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await server.Client.SendAsync(Request(HttpMethod.Post, "/v1/pdf-text", auth, pdf));
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, $"attempt {i}: a leaked slot would answer 503");
        }
    }

    /// <summary>
    /// SEC-1 PoC: three parallel posts of the 2000-entry forged-ratio nested-archive bomb (a 15 MiB upload that
    /// would inflate 16 GiB). Process-wide allocation and wall time must stay bounded.
    /// </summary>
    [Fact]
    public async Task Three_parallel_bomb_posts_cost_bounded_allocation_and_time()
    {
        byte[] blob = RawZip.Deflate(RawZip.ZerosWithPrefix(8 * 1024 * 1024, 0x50, 0x4B, 0x03, 0x04));
        List<RawZip.Entry> entries = new();
        for (int i = 0; i < 2000; i++)
            entries.Add(new RawZip.Entry($"n{i:D4}.bin", blob, 8 * 1024 * 1024, ClaimedCompressedSize: 100_000));
        byte[] bomb = RawZip.Build(entries);

        await using WorkerServer server = await WorkerServer.StartAsync();
        string auth = $"Bearer {WorkerServer.Token}";
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));

        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch sw = Stopwatch.StartNew();

        Task<HttpResponseMessage>[] posts = new Task<HttpResponseMessage>[3];
        for (int i = 0; i < posts.Length; i++)
            posts[i] = server.Client.SendAsync(Request(HttpMethod.Post, "/v1/extract-zip", auth, bomb), timeout.Token);
        HttpResponseMessage[] responses = await Task.WhenAll(posts);

        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        _output.WriteLine($"3 parallel bomb posts: allocated={allocated / (1024 * 1024)} MiB elapsed={sw.Elapsed.TotalSeconds:F2}s "
            + $"statuses={string.Join(",", Array.ConvertAll(responses, r => (int)r.StatusCode))}");

        foreach (HttpResponseMessage response in responses)
        {
            ((int)response.StatusCode).Should().BeOneOf(200, 503);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                string body = await response.Content.ReadAsStringAsync();
                body.Should().Contain("\"aborted\":true");
            }

            response.Dispose();
        }

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        allocated.Should().BeLessThan(1500L * 1024 * 1024, "work is bounded by the 64 MiB total budget per request");
    }
}
