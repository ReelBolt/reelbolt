using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs;

/// <summary>Runs the hybrid request shapes against a real Qdrant. Skipped unless REELBOLT_QDRANT_TEST_HOST is set.</summary>
public sealed class QdrantContainerFactAttribute : FactAttribute
{
    public QdrantContainerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REELBOLT_QDRANT_TEST_HOST")))
            Skip = "Set REELBOLT_QDRANT_TEST_HOST (and optionally REELBOLT_QDRANT_TEST_PORT, gRPC, default 6334) to run against a Qdrant container.";
    }
}

public class PlatformDocsQdrantIntegrationTests
{
    [QdrantContainerFact]
    public async Task Hybrid_collection_fuses_dense_and_sparse_and_honours_filters()
    {
        string host = Environment.GetEnvironmentVariable("REELBOLT_QDRANT_TEST_HOST")!;
        int port = int.TryParse(Environment.GetEnvironmentVariable("REELBOLT_QDRANT_TEST_PORT"), out int p) ? p : 6334;
        using var client = new QdrantClient(host, port);
        string name = "platformdocs_it_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await client.CreateCollectionAsync(
                name,
                new VectorParamsMap { Map = { ["dense"] = new VectorParams { Size = 3, Distance = Distance.Cosine } } },
                sparseVectorsConfig: new SparseVectorConfig { Map = { ["bm25"] = new SparseVectorParams { Modifier = Modifier.Idf } } });
            foreach (string field in QdrantPlatformDocsIndex.IndexedFields)
                await client.CreatePayloadIndexAsync(name, field, PayloadSchemaType.Keyword, wait: true);

            (string Id, float[] Dense, string Text, string Kind)[] docs =
            [
                ("a", [1f, 0f, 0f], "render the final video with the compile step", "recipe"),
                ("b", [0f, 1f, 0f], "transcribe speech into subtitles", "concept"),
                ("c", [0f, 0f, 1f], "generate narration with a voice", "recipe")
            ];
            var points = docs.Select(d =>
            {
                var point = new PointStruct
                {
                    Id = QdrantPlatformDocsIndex.PointId(d.Id),
                    Vectors = new System.Collections.Generic.Dictionary<string, Vector>
                    {
                        ["dense"] = d.Dense,
                        ["bm25"] = PlatformDocsMetadata.SparseTermFrequencies(d.Text)
                    }
                };
                point.Payload["chunkId"] = d.Id;
                point.Payload["kind"] = d.Kind;
                point.Payload["content"] = d.Text;
                return point;
            }).ToList();
            await client.UpsertAsync(name, points, wait: true);

            var options = new PlatformDocsHybridOptions();
            // Lexical-only signal: the dense vector points at "b" but the words say "subtitles".
            var fused = PlatformDocsQueryBuilder.Hybrid(name, [0f, 0f, 0.1f], "subtitles transcribe", null, options);
            var dense = PlatformDocsQueryBuilder.DenseCosine(name, [1f, 0f, 0f], null, options);
            var batch = await client.QueryBatchAsync(name, [fused, dense]);

            batch[0].Result.Select(r => r.Payload["chunkId"].StringValue).Should().Contain(["b", "c"]);
            batch[0].Result.First().Payload["chunkId"].StringValue.Should().Be("b", "the sparse side outranks the unrelated dense hit");
            batch[1].Result.First().Score.Should().BeApproximately(1f, 0.001f);

            var must = PlatformDocsQueryBuilder.MustFilter(new PlatformDocsSearchOptions { Kind = "concept" });
            var filtered = await client.QueryBatchAsync(name, [PlatformDocsQueryBuilder.Hybrid(name, [1f, 0f, 0f], "render video", must, options)]);
            filtered[0].Result.Select(r => r.Payload["chunkId"].StringValue).Should().Equal("b");
        }
        finally
        {
            await client.DeleteCollectionAsync(name);
        }
    }
}
