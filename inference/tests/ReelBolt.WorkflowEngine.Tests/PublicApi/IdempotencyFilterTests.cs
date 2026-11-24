using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.PublicApi;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// The <c>[Idempotent]</c> filter end to end against the real store (SQLite): transient answers are not stored
/// (Q-1), a throw after the write leaves the key Done with no body (S-M1), and the 422 / 409 / replay answers (Q-8).
/// </summary>
public sealed class IdempotencyFilterTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Guid _apiKey = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using InferenceApiDbContext setup = Db();
        await setup.Database.ExecuteSqlRawAsync(
            "CREATE TABLE api_idempotency_records (api_key_id TEXT NOT NULL, key TEXT NOT NULL, request_hash TEXT NOT NULL, status TEXT NOT NULL, response_status INTEGER NULL, response_body TEXT NULL, created_at TEXT NOT NULL, PRIMARY KEY (api_key_id, key))");
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private InferenceApiDbContext Db() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite((DbConnection)_connection).Options);

    private sealed class PerCallStore(Func<InferenceApiDbContext> db) : IIdempotencyStore
    {
        private IdempotencyStore New() => new(db(), NullLogger<IdempotencyStore>.Instance);
        public Task<IdempotencyBegin> BeginAsync(Guid k, string key, string h, DateTime n, CancellationToken ct) => New().BeginAsync(k, key, h, n, ct);
        public Task CompleteAsync(Guid k, string key, int s, ReadOnlyMemory<byte> b, DateTime n, CancellationToken ct) => New().CompleteAsync(k, key, s, b, n, ct);
        public Task AbandonAsync(Guid k, string key, CancellationToken ct) => New().AbandonAsync(k, key, ct);
        public Task CompleteWithoutBodyAsync(Guid k, string key, int s, DateTime n, CancellationToken ct) => New().CompleteWithoutBodyAsync(k, key, s, n, ct);
    }

    private sealed record Answer(int Status, string Body, bool Replayed, bool Threw);

    /// <summary>One request through the filter; <paramref name="action"/> stands in for the controller action.</summary>
    private async Task<Answer> SendAsync(string key, string body, Func<HttpContext, Task<int>> action)
    {
        ServiceCollection services = new();
        services.AddSingleton<IApiKeyContext>(new FakeKeyContext(_apiKey));
        services.AddSingleton<IIdempotencyStore>(new PerCallStore(Db));
        DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = "POST";
        http.Request.Headers[IdempotentAttribute.HeaderName] = key;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        MemoryStream response = new();
        http.Response.Body = response;

        ActionContext actionContext = new(http, new RouteData(), new ActionDescriptor());
        ResourceExecutingContext executing = new(actionContext, [], []);
        bool threw = false;
        try
        {
            await new IdempotentAttribute().OnResourceExecutionAsync(executing, async () =>
            {
                int status = await action(http);
                http.Response.StatusCode = status;
                await http.Response.WriteAsync("{\"ok\":true}");
                return new ResourceExecutedContext(actionContext, []);
            });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        return new Answer(http.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()),
            http.Response.Headers.ContainsKey(IdempotentAttribute.ReplayedHeaderName), threw);
    }

    private static string Code(Answer a) => JsonNode.Parse(a.Body)!["error"]!["code"]!.GetValue<string>();

    [Fact]
    public async Task A_429_is_not_stored_so_the_retry_with_the_same_key_runs_the_action_again()
    {
        int runs = 0;
        Answer first = await SendAsync("k-429", "{}", _ => { runs++; return Task.FromResult(429); });
        first.Status.Should().Be(429);

        Answer retry = await SendAsync("k-429", "{}", _ => { runs++; return Task.FromResult(201); });

        runs.Should().Be(2, "a stored 429 would block the retry for 24 hours");
        retry.Status.Should().Be(201);
        retry.Replayed.Should().BeFalse();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(409)]
    public async Task Other_transient_answers_are_not_stored_either(int status)
    {
        int runs = 0;
        await SendAsync("k-t", "{}", _ => { runs++; return Task.FromResult(status); });
        await SendAsync("k-t", "{}", _ => { runs++; return Task.FromResult(200); });
        runs.Should().Be(2);
    }

    [Fact]
    public async Task A_throw_after_the_write_was_committed_replays_as_409_and_never_runs_the_action_again()
    {
        int runs = 0;
        Answer first = await SendAsync("k-throw", "{}", http =>
        {
            runs++;
            PublicIdempotency.MarkCommitted(http); // the run row is saved; the response is lost
            throw new InvalidOperationException("boom after save");
        });
        first.Threw.Should().BeTrue();

        Answer replay = await SendAsync("k-throw", "{}", _ => { runs++; return Task.FromResult(201); });

        runs.Should().Be(1, "a second start would be a second run");
        replay.Status.Should().Be(409);
        Code(replay).Should().Be("idempotency_response_unavailable");
    }

    [Fact]
    public async Task A_throw_before_any_write_abandons_the_key_so_the_retry_runs()
    {
        int runs = 0;
        Answer first = await SendAsync("k-early", "{}", _ => { runs++; throw new InvalidOperationException("before save"); });
        first.Threw.Should().BeTrue();

        Answer retry = await SendAsync("k-early", "{}", _ => { runs++; return Task.FromResult(201); });

        runs.Should().Be(2);
        retry.Status.Should().Be(201);
    }

    [Fact]
    public async Task The_same_key_with_another_body_is_422_idempotency_key_reused()
    {
        await SendAsync("k-reuse", "{\"a\":1}", _ => Task.FromResult(201));

        Answer other = await SendAsync("k-reuse", "{\"a\":2}", _ => Task.FromResult(201));

        other.Status.Should().Be(422);
        Code(other).Should().Be("idempotency_key_reused");
    }

    [Fact]
    public async Task A_request_arriving_while_the_first_is_in_flight_is_409_idempotency_in_progress()
    {
        TaskCompletionSource release = new();
        TaskCompletionSource entered = new();
        Task<Answer> first = SendAsync("k-live", "{}", async _ => { entered.SetResult(); await release.Task; return 201; });
        await entered.Task;

        Answer second = await SendAsync("k-live", "{}", _ => Task.FromResult(201));

        second.Status.Should().Be(409);
        Code(second).Should().Be("idempotency_in_progress");
        release.SetResult();
        (await first).Status.Should().Be(201);
    }

    [Fact]
    public async Task A_replay_answers_the_stored_response_with_the_replayed_header_and_does_not_run_the_action()
    {
        int runs = 0;
        Answer first = await SendAsync("k-replay", "{}", _ => { runs++; return Task.FromResult(201); });
        first.Replayed.Should().BeFalse();

        Answer replay = await SendAsync("k-replay", "{}", _ => { runs++; return Task.FromResult(500); });

        runs.Should().Be(1);
        replay.Status.Should().Be(201);
        replay.Replayed.Should().BeTrue();
        replay.Body.Should().Be("{\"ok\":true}");
    }
}
