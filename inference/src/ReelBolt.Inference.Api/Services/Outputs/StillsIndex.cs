using System.Text.Json;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>
/// Reads a <see cref="MediaDerivativeKind.Stills"/> row: its JSON index (<c>{count, stills:[{atSec,
/// index, storageLeaf}]}</c>, bare leaves beside the row's own object) and the times it was requested
/// at (its params JSON's <c>timesMs</c>; absent = the evenly spaced set).
/// </summary>
internal static class StillsIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record Sidecar(int Count = 0, IReadOnlyList<Entry>? Stills = null);

    internal sealed record Entry(double AtSec = 0, int Index = 0, string? StorageLeaf = null);

    /// <summary>A still of a row, its object key resolved and re-checked against the project prefix (server-side only).</summary>
    internal sealed record Still(int Position, double AtSec, string StorageKey);

    /// <summary>The requested times of a stills row, in ms; empty for the even set; null when the params are unreadable.</summary>
    internal static int[]? RequestedTimesMs(MediaDerivative row)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.ParamsJson) ? "{}" : row.ParamsJson);
            if (!document.RootElement.TryGetProperty("timesMs", out JsonElement times)) return [];
            return times.ValueKind == JsonValueKind.Array ? [.. times.EnumerateArray().Select(t => t.GetInt32())] : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The row's stills in index order, or empty when its index is missing, foreign or unreadable. Never throws except on cancellation.</summary>
    internal static async Task<IReadOnlyList<Still>> ReadAsync(IFileStorageService storage, MediaDerivative row, CancellationToken ct)
    {
        string projectPrefix = $"projects/{row.ProjectId}/";
        if (row.Kind != MediaDerivativeKind.Stills ||
            string.IsNullOrWhiteSpace(row.StorageKey) || !row.StorageKey.StartsWith(projectPrefix, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(row.IndexStorageKey) || !row.IndexStorageKey.StartsWith(projectPrefix, StringComparison.Ordinal))
            return [];

        Sidecar? sidecar;
        try
        {
            await using Stream stream = await storage.DownloadAsync(row.ProjectId, row.IndexStorageKey, ct);
            sidecar = await JsonSerializer.DeserializeAsync<Sidecar>(stream, JsonOptions, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }

        List<Still> stills = [];
        foreach (Entry entry in sidecar?.Stills ?? [])
        {
            if (!FilmstripSheets.TryDeriveSheetKey(row.StorageKey, entry.StorageLeaf, projectPrefix, out string key)) continue;
            stills.Add(new Still(stills.Count, entry.AtSec, key));
        }

        return stills;
    }
}
