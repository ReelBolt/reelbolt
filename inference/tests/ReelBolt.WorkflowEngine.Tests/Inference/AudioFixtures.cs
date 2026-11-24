#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The recorded audio-vendor mocks (Fixtures/Audio, copied from research/audio-vendors.md section C) and the
/// stub transport that replays them. No live call, no real key: every client under test gets the key <c>test</c>.
/// </summary>
internal static class AudioFixtures
{
    public const string Key = "test";

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Inference", "Fixtures", "Audio");

    public static string Text(string name) => File.ReadAllText(Path.Combine(Dir, name));

    public static byte[] Silence => File.ReadAllBytes(Path.Combine(Dir, "silence.mp3"));

    public static JsonNode Node(string name) => JsonNode.Parse(Text(name))!;

    public static bool SameJson(string actual, string fixture) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual), Node(fixture));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Audio(byte[] bytes, string contentType = "audio/mpeg", params (string Name, string Value)[] headers)
    {
        ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        foreach ((string name, string value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

/// <summary>Records every request (with its body read eagerly) and answers from a function.</summary>
internal sealed class AudioStubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    public IEnumerable<string> PostBodies =>
        Requests.Select((r, i) => (r, i)).Where(t => t.r.Method == HttpMethod.Post).Select(t => Bodies[t.i]);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
        return answer(request);
    }
}
