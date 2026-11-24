using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs.RetrievalEval;

/// <summary>
/// A minimal OpenAI-compatible <c>POST {endpoint}/v1/embeddings</c> client for the local text-embeddings-inference
/// service. Local only: the eval makes no provider call that costs money.
/// </summary>
public sealed class HttpEmbeddingGenerator(string endpoint, string model, string? cacheDir = null, Action<string>? log = null) : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Batch = 8;
    private int _served;
    private int _fetched;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _url = endpoint.TrimEnd('/') + "/v1/embeddings";

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        string[] texts = [.. values];
        float[]?[] found = new float[texts.Length][];
        if (cacheDir is not null && texts.Length > 1) // batches only: a single text is a query, whose embedding latency must stay real
        {
            Directory.CreateDirectory(cacheDir);
            for (int i = 0; i < texts.Length; i++)
            {
                string file = CachePath(texts[i]);
                if (File.Exists(file)) found[i] = JsonSerializer.Deserialize<float[]>(File.ReadAllText(file));
            }
        }

        int[] missing = [.. Enumerable.Range(0, texts.Length).Where(i => found[i] is null)];
        foreach (int[] group in missing.Chunk(Batch))
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                _url, new { model, input = group.Select(i => texts[i]).ToArray() }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            int n = 0;
            foreach (JsonElement item in doc.RootElement.GetProperty("data").EnumerateArray().OrderBy(e => e.GetProperty("index").GetInt32()))
            {
                float[] vector = [.. item.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle())];
                found[group[n]] = vector;
                if (cacheDir is not null && texts.Length > 1) File.WriteAllText(CachePath(texts[group[n]]), JsonSerializer.Serialize(vector));
                n++;
            }

            _fetched += group.Length;
            log?.Invoke($"embedded {_fetched} new texts so far");
        }

        _served += texts.Length;
        List<Embedding<float>> all = [.. found.Select(v => new Embedding<float>(v!) { ModelId = model })];

        return new GeneratedEmbeddings<Embedding<float>>(all);
    }

    private string CachePath(string text) => Path.Combine(
        cacheDir!, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(model + "\n" + text))) + ".json");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
