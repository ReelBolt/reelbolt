using System.Text.Json;
using ReelBolt.Inference.Api.Services.Storage;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>
/// Reads a filmstrip density's index sidecar and derives its sheets' object keys, by the same rules
/// <c>MediaController</c> applies (a density is one row; sheet N is the row's directory plus the
/// sidecar's bare <c>storageLeaf</c>, re-checked against the project prefix).
/// </summary>
internal static class FilmstripSheets
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record Sidecar(
        double DensitySec = 0,
        int ThumbWidth = 0,
        int ThumbHeight = 0,
        int Cols = 0,
        int Rows = 0,
        int SheetCount = 0,
        IReadOnlyList<Sheet>? Sheets = null);

    internal sealed record Sheet(string? StorageLeaf = null, int FirstThumbIndex = 0, int ThumbCount = 0, double FirstTimeSec = 0);

    /// <summary>The parsed sidecar, or null when it is missing, outside the project, or unreadable. Never throws except on cancellation.</summary>
    internal static async Task<Sidecar?> TryReadAsync(IFileStorageService storage, Guid projectId, string? indexKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(indexKey) || !indexKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            return null;

        try
        {
            await using Stream stream = await storage.DownloadAsync(projectId, indexKey, ct);
            return await JsonSerializer.DeserializeAsync<Sidecar>(stream, JsonOptions, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static bool TryDeriveSheetKey(string rowStorageKey, string? storageLeaf, string expectedPrefix, out string sheetKey)
    {
        sheetKey = string.Empty;
        if (string.IsNullOrWhiteSpace(storageLeaf) ||
            storageLeaf.Contains('/') ||
            storageLeaf.Contains('\\') ||
            storageLeaf.Contains("..", StringComparison.Ordinal))
            return false;

        string? directory = Path.GetDirectoryName(rowStorageKey)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(directory)) return false;

        sheetKey = $"{directory}/{storageLeaf}";
        return sheetKey.StartsWith(expectedPrefix, StringComparison.Ordinal);
    }
}
