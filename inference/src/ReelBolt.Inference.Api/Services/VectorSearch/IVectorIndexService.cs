namespace ReelBolt.Inference.Api.Services.VectorSearch;

public interface IVectorIndexService
{
    Task UpsertFileChunksAsync(
        Guid projectId,
        string? embeddingModel,
        Guid fileId,
        string filePath,
        string fileName,
        IReadOnlyList<VectorizedFileChunk> chunks,
        CancellationToken ct);

    /// <summary>Removes the file's chunks from EVERY collection of the project (any model/dimension, plus the legacy one).</summary>
    Task DeleteFileChunksAsync(Guid projectId, Guid fileId, CancellationToken ct);

    /// <summary>
    /// Deletes every collection belonging to the project (any model/dimension, plus the legacy one).
    /// Used by the C9 account purge: a collection is the project's embedded file text, so it goes with
    /// the project rather than file by file. Returns the number of collections deleted.
    /// </summary>
    Task<int> DeleteProjectCollectionsAsync(Guid projectId, CancellationToken ct);

    Task<IReadOnlyList<VectorSearchChunkResult>> SearchAsync(
        Guid projectId,
        string? embeddingModel,
        ReadOnlyMemory<float> queryVector,
        int limit,
        CancellationToken ct);
}
