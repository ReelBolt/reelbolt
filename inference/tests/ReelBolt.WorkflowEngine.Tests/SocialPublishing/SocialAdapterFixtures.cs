#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>One request as the platform saw it, captured while the body was still readable.</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body, long BodyBytes, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A hand-written platform: every request is answered by the next responder in order (or one keyed by "METHOD host/path"),
/// and recorded. No network, no recorded real traffic, no credential.
/// </summary>
internal sealed class FixtureHandler : HttpMessageHandler
{
    private readonly Queue<Func<SeenRequest, HttpResponseMessage>> _queue = new();

    public List<SeenRequest> Seen { get; } = [];

    public FixtureHandler Enqueue(Func<SeenRequest, HttpResponseMessage> responder)
    {
        _queue.Enqueue(responder);
        return this;
    }

    public FixtureHandler Enqueue(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null) =>
        Enqueue(_ =>
        {
            HttpResponseMessage response = new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return response;
        });

    /// <summary>The next request fails at the transport level, as a dropped connection does.</summary>
    public FixtureHandler EnqueueNetworkFailure() => Enqueue(_ => throw new HttpRequestException("connection reset"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = string.Empty;
        long bytes = 0;
        if (request.Content is not null)
        {
            byte[] raw = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            bytes = raw.Length;
            body = raw.Length <= 65536 ? Encoding.UTF8.GetString(raw) : string.Empty;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        if (request.Content is not null) foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        SeenRequest seen = new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body, bytes, headers);
        lock (Seen) Seen.Add(seen);
        if (_queue.Count == 0) throw new InvalidOperationException($"No fixture response left for {request.Method} {request.RequestUri}");
        return _queue.Dequeue()(seen);
    }
}
