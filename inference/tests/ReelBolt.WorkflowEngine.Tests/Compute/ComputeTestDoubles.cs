using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The doubles the D6 compute tests share. Nothing here talks to a real gateway, a real database or a
/// real clock: the whole point of the selector and the client is that they are decidable from three
/// inputs — the mode, the organization's entitlements and whether a runner is online.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    /// <summary>Every request the client made, in order, for assertions about what was (or was not) sent.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Every request body the client sent, read before the response is produced.</summary>
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return await handler(request).ConfigureAwait(false);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> over one handler, with a base address the client treats as the gateway.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler, string baseAddress = "http://gateway.test") : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri(baseAddress) };
}

/// <summary>An <see cref="IComputeEntitlements"/> that answers from a constant, and counts how many times it was asked.</summary>
internal sealed class FixedComputeEntitlements(ComputeEntitlements value) : IComputeEntitlements
{
    public int Calls { get; private set; }

    public ValueTask<ComputeEntitlements> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return ValueTask.FromResult(value);
    }
}

/// <summary>An <see cref="IComputeEntitlements"/> that fails the test if the selector asks it anything — the Mode=Local contract.</summary>
internal sealed class ForbiddenComputeEntitlements : IComputeEntitlements
{
    public ValueTask<ComputeEntitlements> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Compute:Mode=Local must not read compute entitlements");
}

/// <summary>A clock the test moves by hand, so the 5-second runner-presence cache is exercised without sleeping.</summary>
internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Builders for the D6 objects, so each test states only what it is about.</summary>
internal static class ComputeTestFactory
{
    public static ComputeOptions Options(
        ComputeMode mode = ComputeMode.Fabric,
        int runnerQueryCacheSeconds = 5,
        int sessionWaitSeconds = 900,
        string? token = "test-internal-token",
        int jobWaitSeconds = 30)
        => new()
        {
            Mode = mode,
            RunnerQueryCacheSeconds = runnerQueryCacheSeconds,
            SessionWaitSeconds = sessionWaitSeconds,
            InternalToken = token ?? string.Empty,
            JobWaitSeconds = jobWaitSeconds
        };

    public static RunnerGatewayClient Gateway(
        HttpMessageHandler handler,
        ComputeOptions? options = null,
        IExecutionSessionGate? gate = null)
        => new(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            gate ?? new ExecutionSessionGate(Microsoft.Extensions.Options.Options.Create(Options())),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RunnerGatewayClient>.Instance);

    public static ComputeTargetSelector Selector(
        IComputeEntitlements entitlements,
        RunnerGatewayClient gateway,
        ComputeOptions options,
        TimeProvider? time = null)
        => new(
            Microsoft.Extensions.Options.Options.Create(options),
            entitlements,
            gateway,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComputeTargetSelector>.Instance,
            time ?? TimeProvider.System);

    /// <summary>A JSON body the gateway would answer with, for the job routes.</summary>
    public static string JobBody(
        Guid jobId,
        Guid? sessionId = null,
        string status = "Queued",
        string jobType = "video.compile",
        string target = "CloudPool",
        int attempt = 1,
        int maxAttempts = 2,
        long leaseEpoch = 1,
        bool created = true,
        bool sessionLost = false,
        bool timedOut = false)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            jobId,
            sessionId,
            status,
            jobType,
            target,
            attempt,
            maxAttempts,
            leaseEpoch,
            created,
            sessionLost,
            timedOut
        });

    /// <summary>A <c>GET /internal/v1/runners</c> body. A pool worker has a null orgId, exactly as the gateway sends it.</summary>
    public static string RunnersBody(params (string Kind, Guid? OrgId, string[] JobTypes)[] runners)
    {
        var payload = runners.Select(r => new
        {
            runnerId = Guid.NewGuid(),
            kind = r.Kind,
            orgId = r.OrgId,
            name = "runner",
            freeSlots = 1,
            running = 0,
            draining = false,
            connectedAt = DateTimeOffset.UnixEpoch,
            jobTypes = r.JobTypes,
            container = "docker",
            agentVersion = "1.0.0"
        });
        return System.Text.Json.JsonSerializer.Serialize(new { online = runners.Length, runners = payload });
    }

    /// <summary>A runners body that also carries the gateway's <c>pool</c> block (<c>wakerHealthy</c>).</summary>
    public static string RunnersBodyWithPool(bool wakerHealthy, params (string Kind, Guid? OrgId, string[] JobTypes)[] runners)
    {
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(RunnersBody(runners))!;
        node["pool"] = new System.Text.Json.Nodes.JsonObject
        {
            ["wakerHealthy"] = wakerHealthy,
            ["wakerAgeSeconds"] = 3,
            ["reason"] = "ok",
            ["capped"] = false
        };
        return node.ToJsonString();
    }

    /// <summary>The three v1 job types, for a runner that advertises everything.</summary>
    public static string[] AllJobTypes => ["remotion.workspace-session", "video.compile", "video.analyze.extract"];

    public static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
