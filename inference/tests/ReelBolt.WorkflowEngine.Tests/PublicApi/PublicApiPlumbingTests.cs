using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

public class PublicApiPlumbingTests
{
    private sealed class FakeStore(Func<(int, int)>? onIncrement = null) : IApiKeyRateCounterStore
    {
        private int _total;
        private int _writes;
        public Task<(int Total, int Writes)> IncrementAsync(Guid apiKeyId, DateTime windowStart, bool isWrite, CancellationToken ct)
        {
            if (onIncrement is not null) return Task.FromResult(onIncrement());
            _total++;
            if (isWrite) _writes++;
            return Task.FromResult((_total, _writes));
        }

        public Task<int> DeleteOlderThanAsync(DateTime windowStart, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class ThrowingStore : IApiKeyRateCounterStore
    {
        public Task<(int Total, int Writes)> IncrementAsync(Guid apiKeyId, DateTime windowStart, bool isWrite, CancellationToken ct) =>
            throw new InvalidOperationException("db down");

        public Task<int> DeleteOlderThanAsync(DateTime windowStart, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class CaptureAudit : IApiKeyAuditWriter
    {
        public System.Collections.Generic.List<ApiKeyAuditEntry> Entries { get; } = [];
        public void TryWrite(ApiKeyAuditEntry entry) => Entries.Add(entry);
    }

    private static IOptionsMonitor<PublicApiOptions> Opts(bool enabled = true, int rpm = 300, int wpm = 60) =>
        new TestMonitor(new PublicApiOptions { Enabled = enabled, RateLimit = { RequestsPerMinute = rpm, WritesPerMinute = wpm } });

    private sealed class TestMonitor(PublicApiOptions value) : IOptionsMonitor<PublicApiOptions>
    {
        public PublicApiOptions CurrentValue => value;
        public PublicApiOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PublicApiOptions, string?> listener) => null;
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 30, TimeSpan.Zero);

    [Fact]
    public async Task RateLimiter_counts_reads_and_writes_and_refuses_over_the_write_cap()
    {
        ApiKeyRateLimiter limiter = new(new FakeStore(), Opts(rpm: 5, wpm: 2), NullLogger<ApiKeyRateLimiter>.Instance);
        Guid key = Guid.NewGuid();

        (await limiter.CheckAsync(key, false, Now, default)).Allowed.Should().BeTrue();
        (await limiter.CheckAsync(key, true, Now, default)).Allowed.Should().BeTrue();
        (await limiter.CheckAsync(key, true, Now, default)).Allowed.Should().BeTrue();
        ApiKeyRateLimitDecision refused = await limiter.CheckAsync(key, true, Now, default);
        refused.Allowed.Should().BeFalse();
        refused.ViolatedLimit.Should().Be("writes");
        refused.ResetsAt.Should().Be(new DateTimeOffset(2026, 1, 1, 10, 1, 0, TimeSpan.Zero));
        refused.Remaining.Should().Be(1);

        (await limiter.CheckAsync(key, false, Now, default)).Allowed.Should().BeTrue("total is 5, the cap");
        (await limiter.CheckAsync(key, false, Now, default)).Allowed.Should().BeFalse("total reached 6");
    }

    [Fact]
    public async Task RateLimiter_fails_open_when_the_database_throws()
    {
        ApiKeyRateLimiter limiter = new(new ThrowingStore(), Opts(), NullLogger<ApiKeyRateLimiter>.Instance);
        ApiKeyRateLimitDecision d = await limiter.CheckAsync(Guid.NewGuid(), true, Now, default);
        d.Allowed.Should().BeTrue();
        d.Degraded.Should().BeTrue();
    }

    [Fact]
    public async Task RateCounterStore_upserts_on_a_relational_provider()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE api_key_rate_counters (api_key_id TEXT NOT NULL, window_start TEXT NOT NULL, total INTEGER NOT NULL, writes INTEGER NOT NULL, PRIMARY KEY (api_key_id, window_start))");
        ApiKeyRateCounterStore store = new(db);
        Guid key = Guid.NewGuid();
        DateTime w = ApiKeyRateLimiter.WindowStart(Now);

        (await store.IncrementAsync(key, w, false, default)).Should().Be((1, 0));
        (await store.IncrementAsync(key, w, true, default)).Should().Be((2, 1));
        (await store.IncrementAsync(Guid.NewGuid(), w, true, default)).Should().Be((1, 1));
    }

    private static (PublicApiGuardFilter Filter, CaptureAudit Audit) NewGuard(bool enabled, IApiKeyRateCounterStore? store = null, int rpm = 300)
    {
        CaptureAudit audit = new();
        PublicApiGuardFilter filter = new(Opts(enabled, rpm), new FakeKeyContext(), new ApiKeyRateLimiter(store ?? new FakeStore(), Opts(enabled, rpm), NullLogger<ApiKeyRateLimiter>.Instance), audit);
        return (filter, audit);
    }

    private static (ResourceExecutingContext Ctx, DefaultHttpContext Http, Func<bool> Reached) Ctx(bool apiKey, string method = "GET", System.Collections.Generic.IList<object>? metadata = null, string? scope = null)
    {
        DefaultHttpContext http = new();
        http.Request.Method = method;
        http.Response.Body = new MemoryStream();
        http.User = apiKey
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()), new Claim(AuthClaims.Scope, scope ?? "runs.read")], "ApiKey"))
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"));
        ResourceExecutingContext ctx = new(new ActionContext(http, new RouteData(), new ActionDescriptor { EndpointMetadata = metadata ?? [new AnyApiKeyScopeAttribute()] }), [], []);
        return (ctx, http, () => false);
    }

    private static async Task<bool> Run(PublicApiGuardFilter f, ResourceExecutingContext ctx)
    {
        bool reached = false;
        await f.OnResourceExecutionAsync(ctx, () =>
        {
            reached = true;
            return Task.FromResult(new ResourceExecutedContext(ctx, []));
        });
        return reached;
    }

    private static string Body(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        return new StreamReader(http.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task Guard_flag_off_is_an_empty_404()
    {
        (PublicApiGuardFilter f, _) = NewGuard(false);
        (ResourceExecutingContext ctx, DefaultHttpContext http, _) = Ctx(true);
        (await Run(f, ctx)).Should().BeFalse();
        http.Response.StatusCode.Should().Be(404);
        Body(http).Should().BeEmpty();
        http.Response.Headers.ContainsKey("X-Request-Id").Should().BeTrue();
    }

    [Fact]
    public async Task Guard_session_principal_gets_api_key_required()
    {
        (PublicApiGuardFilter f, _) = NewGuard(true);
        (ResourceExecutingContext ctx, DefaultHttpContext http, _) = Ctx(false);
        (await Run(f, ctx)).Should().BeFalse();
        http.Response.StatusCode.Should().Be(401);
        JsonNode.Parse(Body(http))!["error"]!["code"]!.GetValue<string>().Should().Be("api_key_required");
    }

    [Fact]
    public async Task Guard_key_passes_and_is_audited_with_headers()
    {
        (PublicApiGuardFilter f, CaptureAudit audit) = NewGuard(true);
        (ResourceExecutingContext ctx, DefaultHttpContext http, _) = Ctx(true);
        http.Request.Path = "/api/public/v1/projects/abc/runs";
        http.Request.QueryString = new QueryString("?secret=1");
        (await Run(f, ctx)).Should().BeTrue();
        http.Response.Headers["RateLimit-Limit"].ToString().Should().Be("300");
        http.Response.Headers.ContainsKey("RateLimit-Remaining").Should().BeTrue();
        http.Response.Headers.ContainsKey("RateLimit-Reset").Should().BeTrue();
        audit.Entries.Should().ContainSingle();
        audit.Entries[0].Route.Should().NotContain("secret");
        audit.Entries[0].Method.Should().Be("GET");
    }

    [Fact]
    public async Task Guard_429_carries_retry_after_and_resets_at_and_is_audited()
    {
        (PublicApiGuardFilter f, CaptureAudit audit) = NewGuard(true, rpm: 1);
        (ResourceExecutingContext c1, _, _) = Ctx(true);
        await Run(f, c1);
        (ResourceExecutingContext c2, DefaultHttpContext http, _) = Ctx(true);
        (await Run(f, c2)).Should().BeFalse();
        http.Response.StatusCode.Should().Be(429);
        http.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
        JsonNode err = JsonNode.Parse(Body(http))!["error"]!;
        err["code"]!.GetValue<string>().Should().Be("rate_limited");
        err["resetsAt"].Should().NotBeNull();
        audit.Entries.Last().StatusCode.Should().Be(429);
    }

    [Fact]
    public void Envelope_adapter_keeps_code_message_and_extras()
    {
        ObjectResult r = PublicApiResults.From(new ObjectResult(new { code = "plan_limit", message = "Upgrade", feature = "x" }) { StatusCode = 402 });
        r.StatusCode.Should().Be(402);
        JsonNode err = ((JsonObject)r.Value!)["error"]!;
        err["code"]!.GetValue<string>().Should().Be("plan_limit");
        err["message"]!.GetValue<string>().Should().Be("Upgrade");
        err["feature"]!.GetValue<string>().Should().Be("x");

        ObjectResult plain = PublicApiResults.From(new BadRequestObjectResult(new { message = "bad" }));
        plain.StatusCode.Should().Be(400);
        ((JsonObject)plain.Value!)["error"]!["message"]!.GetValue<string>().Should().Be("bad");
        ((JsonObject)plain.Value!)["error"]!["code"].Should().NotBeNull();
    }

    [Fact]
    public async Task Audit_writer_counts_dropped_rows_when_the_channel_is_full()
    {
        ApiKeyAuditWriter writer = new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiKeyAuditWriter>.Instance, 2);
        ApiKeyAuditEntry e = new(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, "GET", "api/public/v1/me", 200, 1, "r");
        for (int i = 0; i < 5; i++) writer.TryWrite(e);
        writer.DroppedCount.Should().Be(3);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Idempotency_replay_mismatch_in_progress_isolation_and_abandon()
    {
        // A fresh context per request, like the scoped one in production (a duplicate insert surfaces as a
        // store conflict, not as an identity-map clash inside one context).
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using (InferenceApiDbContext setup = SqliteDb(connection))
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE TABLE api_idempotency_records (api_key_id TEXT NOT NULL, key TEXT NOT NULL, request_hash TEXT NOT NULL, status TEXT NOT NULL, response_status INTEGER NULL, response_body TEXT NULL, created_at TEXT NOT NULL, PRIMARY KEY (api_key_id, key))");
        IIdempotencyStore store = new PerCallStore(connection);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        (await store.BeginAsync(a, "k1", "h1", now, default)).Status.Should().Be(IdempotencyBeginStatus.Proceed);
        (await store.BeginAsync(a, "k1", "h1", now, default)).Status.Should().Be(IdempotencyBeginStatus.InProgress);
        await store.CompleteAsync(a, "k1", 201, Encoding.UTF8.GetBytes("{\"x\":1}"), now, default);

        IdempotencyBegin replay = await store.BeginAsync(a, "k1", "h1", now, default);
        replay.Status.Should().Be(IdempotencyBeginStatus.Replay);
        replay.ResponseStatus.Should().Be(201);
        replay.ResponseBody.Should().Be("{\"x\":1}");
        (await store.BeginAsync(a, "k1", "other", now, default)).Status.Should().Be(IdempotencyBeginStatus.Reused);
        (await store.BeginAsync(b, "k1", "h1", now, default)).Status.Should().Be(IdempotencyBeginStatus.Proceed, "keys are per API key");

        await store.AbandonAsync(b, "k1", default);
        (await store.BeginAsync(b, "k1", "h1", now, default)).Status.Should().Be(IdempotencyBeginStatus.Proceed);

        (await store.BeginAsync(a, "big", "h", now, default)).Status.Should().Be(IdempotencyBeginStatus.Proceed);
        await store.CompleteAsync(a, "big", 200, new byte[IdempotencyStore.MaxResponseBodyBytes + 1], now, default);
        (await store.BeginAsync(a, "big", "h", now, default)).Status.Should().Be(IdempotencyBeginStatus.ResponseUnavailable); // over the bound: Done with no body

        // A write that was committed but whose action threw: Done with no body, never re-run (S-M1).
        (await store.BeginAsync(a, "thrown", "h", now, default)).Status.Should().Be(IdempotencyBeginStatus.Proceed);
        await store.CompleteWithoutBodyAsync(a, "thrown", 500, now, default);
        IdempotencyBegin unavailable = await store.BeginAsync(a, "thrown", "h", now, default);
        unavailable.Status.Should().Be(IdempotencyBeginStatus.ResponseUnavailable);
    }

    private static InferenceApiDbContext SqliteDb(System.Data.Common.DbConnection c) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(c).Options);

    private sealed class PerCallStore(System.Data.Common.DbConnection db) : IIdempotencyStore
    {
        private IdempotencyStore New() => new(SqliteDb(db), NullLogger<IdempotencyStore>.Instance);
        public Task<IdempotencyBegin> BeginAsync(Guid k, string key, string h, DateTime n, CancellationToken ct) => New().BeginAsync(k, key, h, n, ct);
        public Task CompleteAsync(Guid k, string key, int s, ReadOnlyMemory<byte> b, DateTime n, CancellationToken ct) => New().CompleteAsync(k, key, s, b, n, ct);
        public Task AbandonAsync(Guid k, string key, CancellationToken ct) => New().AbandonAsync(k, key, ct);
        public Task CompleteWithoutBodyAsync(Guid k, string key, int s, DateTime n, CancellationToken ct) => New().CompleteWithoutBodyAsync(k, key, s, n, ct);
    }

    [Fact]
    public async Task Me_returns_the_key_identity_not_the_creator()
    {
        InferenceApiDbContext db = NewDb();
        (ApplicationUser user, Organization org) = await SeedCreatorAsync(db);
        ApiKey key = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Name = "CI key",
            KeyId = "abcd1234",
            Scopes = ["runs.read"],
            CreatedByUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();

        MeController c = new(db, new MeKeyContext(key.Id, org.Id), Microsoft.Extensions.Options.Options.Create(new PublicApiOptions()));
        OkObjectResult ok = (OkObjectResult)await c.Get(default);
        PublicApiMeView view = (PublicApiMeView)ok.Value!;
        view.ActingAs.Type.Should().Be("apiKey");
        view.ActingAs.Name.Should().Be("CI key");
        view.Organization.Id.Should().Be(org.Id);
        view.Limits.RequestsPerMinute.Should().Be(300);
        view.Budget.Should().BeNull();
        view.Key.Scopes.Should().Equal("runs.read");
    }

    private sealed class MeKeyContext(Guid key, Guid org) : IApiKeyContext
    {
        public bool IsApiKey => true;
        public Guid? KeyId => key;
        public Guid? OrganizationId => org;
        public System.Collections.Generic.IReadOnlyList<Guid>? AllowedProjectIds => null;
        public System.Collections.Generic.IReadOnlyCollection<string> Scopes => [];
    }

    private static async Task<string> RenderDocAsync()
    {
        ServiceCollection s = new();
        s.AddLogging();
        FakeEnv env = new();
        s.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(env);
        s.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(env);
        s.AddControllers().AddApplicationPart(typeof(MeController).Assembly);
        s.AddEndpointsApiExplorer();
        // The running app's own SwaggerGen options (the internal document, the global Bearer scheme, the public
        // document), so this renders what GET /api/public/v1/openapi.json serves, not a trimmed copy of it.
        s.AddSwaggerGen(ReelBolt.Inference.Api.InferenceApiServiceCollectionExtensions.ConfigureSwagger);
        using ServiceProvider sp = s.BuildServiceProvider();
        return await PublicApiOpenApi.RenderAsync(sp.GetRequiredService<ISwaggerProvider>());
    }

    private sealed class FakeEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment, Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string ApplicationName { get; set; } = "ReelBolt.Inference.Api";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task OpenApi_contains_only_public_operations()
    {
        JsonNode doc = JsonNode.Parse(await RenderDocAsync())!;
        doc["info"]!["title"]!.GetValue<string>().Should().Be("ReelBolt Public API");
        doc["info"]!["version"]!.GetValue<string>().Should().Be("1.0.0");
        string[] paths = ((JsonObject)doc["paths"]!).Select(p => p.Key).ToArray();
        paths.Should().Contain("/api/public/v1/me");
        paths.Should().OnlyContain(p => p.StartsWith("/api/public/v1/"));
        doc["paths"]!["/api/public/v1/me"]!["get"]!["x-required-scopes"].Should().NotBeNull();
        doc["components"]!["securitySchemes"]!["ApiKey"].Should().NotBeNull();
        doc["components"]!["schemas"]!["WebhookEventPayload"].Should().NotBeNull("the webhook payload is published as a component");
        doc["components"]!["schemas"]!["WebhookEventData"].Should().NotBeNull();
    }

    [Fact]
    public async Task OpenApi_every_public_operation_requires_the_ApiKey_scheme_and_documents_its_statuses()
    {
        JsonNode doc = JsonNode.Parse(await RenderDocAsync())!;
        var operations = doc["paths"]!.AsObject()
            .Where(path => !path.Key.EndsWith("openapi.json", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => path.Value!.AsObject().Select(op => (Path: path.Key, Method: op.Key, Op: op.Value!)))
            .ToList();
        operations.Should().NotBeEmpty();
        foreach ((string path, string method, JsonNode op) in operations)
        {
            JsonArray? security = op["security"] as JsonArray;
            security.Should().NotBeNull($"{method} {path} carries a security requirement");
            security!.Single()!.AsObject().ContainsKey("ApiKey").Should().BeTrue($"{method} {path} requires the ApiKey bearer scheme");
            op["responses"]!.AsObject().Count.Should().BeGreaterThanOrEqualTo(3, $"{method} {path} documents its success and the shared 401 and 429");
        }

        string[] start = doc["paths"]!["/api/public/v1/projects/{projectId}/workflows/{workflowId}/runs"]!["post"]!["responses"]!
            .AsObject().Select(r => r.Key).ToArray();
        start.Should().Contain(["201", "202", "402"]);
    }

    [Fact]
    public async Task OpenApi_matches_the_committed_document()
    {
        string json = JsonSerializer.Serialize(JsonNode.Parse(await RenderDocAsync()), new JsonSerializerOptions { WriteIndented = true }) + "\n";
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "docs")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        string path = Path.Combine(dir!, "docs", "public-api", "openapi-v1.json");
        if (Environment.GetEnvironmentVariable("UPDATE_PUBLIC_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
        }

        File.Exists(path).Should().BeTrue("regenerate with UPDATE_PUBLIC_OPENAPI=1");
        (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Should().Be(json,
            "docs/public-api/openapi-v1.json drifted; regenerate with UPDATE_PUBLIC_OPENAPI=1");
    }
}
