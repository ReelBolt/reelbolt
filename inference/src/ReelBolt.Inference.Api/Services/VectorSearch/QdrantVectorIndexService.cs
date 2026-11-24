using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace ReelBolt.Inference.Api.Services.VectorSearch;

public sealed class QdrantVectorIndexService : IVectorIndexService
{
    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorIndexService> _logger;

    public QdrantVectorIndexService(IOptions<VectorSearchOptions> options, ILogger<QdrantVectorIndexService> logger)
    {
        _client = QdrantClientFactory.Create(options.Value);
        _logger = logger;
    }

    public async Task UpsertFileChunksAsync(
        Guid projectId,
        string? embeddingModel,
        Guid fileId,
        string filePath,
        string fileName,
        IReadOnlyList<VectorizedFileChunk> chunks,
        CancellationToken ct)
    {
        if (chunks.Count == 0)
            return;

        int vectorSize = chunks[0].Vector.Length;
        string collectionName = EmbeddingSpace.CollectionName(projectId, embeddingModel, vectorSize);

        await EnsureCollectionForUpsertAsync(collectionName, vectorSize, ct);

        List<PointStruct> points = new(chunks.Count);
        foreach (VectorizedFileChunk chunk in chunks)
        {
            PointStruct point = new()
            {
                Id = Guid.NewGuid(),
                Vectors = chunk.Vector.ToArray()
            };

            point.Payload["projectId"] = projectId.ToString();
            point.Payload["fileId"] = fileId.ToString();
            point.Payload["filePath"] = filePath;
            point.Payload["fileName"] = fileName;
            point.Payload["chunkIndex"] = chunk.ChunkIndex;
            point.Payload["totalChunks"] = chunk.TotalChunks;
            point.Payload["language"] = chunk.Language;
            point.Payload["content"] = chunk.Content;

            points.Add(point);
        }

        try
        {
            await _client.UpsertAsync(collectionName, points, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new IndexNotReadyException($"Vector index is not ready for project '{projectId}'.", ex);
        }
    }

    public async Task DeleteFileChunksAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<string> collections = await _client.ListCollectionsAsync(ct);
            Filter filter = MatchKeyword("fileId", fileId.ToString());
            foreach (string collection in collections.Where(c => EmbeddingSpace.BelongsToProject(projectId, c)))
            {
                await _client.DeleteAsync(collection, filter, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            throw new IndexNotReadyException($"Vector index is not ready for project '{projectId}'.", ex);
        }
    }

    public async Task<int> DeleteProjectCollectionsAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<string> collections = await _client.ListCollectionsAsync(ct);
            string[] mine = collections.Where(c => EmbeddingSpace.BelongsToProject(projectId, c)).ToArray();
            foreach (string collection in mine)
                await _client.DeleteCollectionAsync(collection, cancellationToken: ct);
            return mine.Length;
        }
        catch (Exception ex)
        {
            throw new IndexNotReadyException($"Vector index is not ready for project '{projectId}'.", ex);
        }
    }

    public async Task<IReadOnlyList<VectorSearchChunkResult>> SearchAsync(
        Guid projectId,
        string? embeddingModel,
        ReadOnlyMemory<float> queryVector,
        int limit,
        CancellationToken ct)
    {
        string collectionName = EmbeddingSpace.CollectionName(projectId, embeddingModel, queryVector.Length);
        await EnsureCollectionExistsAsync(collectionName, projectId, ct);

        try
        {
            IReadOnlyList<ScoredPoint> results = await _client.SearchAsync(
                collectionName,
                queryVector,
                limit: (ulong)Math.Max(1, limit),
                cancellationToken: ct);

            return results.Select(MapResult).ToList();
        }
        catch (Exception ex)
        {
            throw new IndexNotReadyException($"Vector index is not ready for project '{projectId}'.", ex);
        }
    }

    private async Task EnsureCollectionForUpsertAsync(string collectionName, int vectorSize, CancellationToken ct)
    {
        try
        {
            bool exists = await _client.CollectionExistsAsync(collectionName, ct);
            if (exists)
                return;

            await _client.CreateCollectionAsync(collectionName, new VectorParams
            {
                Size = (ulong)vectorSize,
                Distance = Distance.Cosine
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new IndexNotReadyException($"Vector index is not ready for collection '{collectionName}'.", ex);
        }
    }

    private async Task EnsureCollectionExistsAsync(string collectionName, Guid projectId, CancellationToken ct)
    {
        try
        {
            bool exists = await _client.CollectionExistsAsync(collectionName, ct);
            if (!exists)
                throw new IndexNotReadyException($"Vector index is not ready for project '{projectId}'.");
        }
        catch (IndexNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IndexNotReadyException($"Vector index is not ready for project '{projectId}'.", ex);
        }
    }

    private static VectorSearchChunkResult MapResult(ScoredPoint point)
    {
        Guid fileId = TryGetGuid(point.Payload, "fileId");
        string filePath = TryGetString(point.Payload, "filePath");
        string fileName = TryGetString(point.Payload, "fileName");
        int chunkIndex = TryGetInt(point.Payload, "chunkIndex");
        int totalChunks = TryGetInt(point.Payload, "totalChunks");
        string language = TryGetString(point.Payload, "language");
        string content = TryGetString(point.Payload, "content");

        return new VectorSearchChunkResult(
            fileId,
            filePath,
            fileName,
            chunkIndex,
            totalChunks,
            language,
            content,
            point.Score);
    }

    private static string TryGetString(IDictionary<string, Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out Value? value) || value is null)
            return string.Empty;

        if (value.HasStringValue)
            return value.StringValue;

        if (value.HasIntegerValue)
            return value.IntegerValue.ToString();

        if (value.HasDoubleValue)
            return value.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (value.HasBoolValue)
            return value.BoolValue.ToString();

        return string.Empty;
    }

    private static int TryGetInt(IDictionary<string, Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out Value? value) || value is null)
            return 0;

        if (value.HasIntegerValue)
            return (int)value.IntegerValue;

        if (value.HasStringValue && int.TryParse(value.StringValue, out int parsed))
            return parsed;

        return 0;
    }

    private static Guid TryGetGuid(IDictionary<string, Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out Value? value) || value is null)
            return Guid.Empty;

        if (!value.HasStringValue)
            return Guid.Empty;

        return Guid.TryParse(value.StringValue, out Guid parsed) ? parsed : Guid.Empty;
    }
}
