using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Csam;

namespace ReelBolt.Inference.Api.Services.Marketplace;

public enum PreviewCsamResult
{
    /// <summary>The control is switched off: nothing was scanned (recorded, never silent).</summary>
    NotConfigured,
    Clean,
    Match,

    /// <summary>The control is on but did not answer: the preview is refused (fail closed).</summary>
    Unavailable
}

/// <summary>Scans the NEW preview and poster bytes with the same provider as ingest.</summary>
public interface IMarketplacePreviewCsamGate
{
    Task<PreviewCsamResult> ScanAsync(string previewKey, string posterKey, CancellationToken ct);
}

/// <summary>
/// Ingest semantics with one deliberate difference. Ingest fails OPEN (an outage never blocks an upload); a marketplace preview is
/// about to become public, so when <c>Csam:Arachnid:Enabled</c> is on and the provider is unconfigured or does not answer, the
/// preview is refused. Off means off: <see cref="PreviewCsamResult.NotConfigured"/>. The poster is hashed locally (PDQ, 32 bytes leave
/// the host) and falls back to the media endpoint; the video goes to the media endpoint.
/// </summary>
public sealed class MarketplacePreviewCsamGate(
    IMarketplacePreviewObjects objects,
    IArachnidShieldClient client,
    IPdqHasher hasher,
    IOptions<CsamOptions> options,
    ILogger<MarketplacePreviewCsamGate> logger) : IMarketplacePreviewCsamGate
{
    public async Task<PreviewCsamResult> ScanAsync(string previewKey, string posterKey, CancellationToken ct)
    {
        ArachnidOptions o = options.Value.Arachnid;
        if (!o.Enabled)
            return PreviewCsamResult.NotConfigured;

        if (string.IsNullOrWhiteSpace(o.BaseUrl) || string.IsNullOrWhiteSpace(o.Username) || string.IsNullOrWhiteSpace(o.Password))
        {
            logger.LogWarning("The CSAM control is enabled but not configured; a marketplace preview is refused");
            return PreviewCsamResult.Unavailable;
        }

        try
        {
            ArachnidScanResult poster = await ScanPosterAsync(posterKey, o, ct);
            if (IsMatch(poster.Classification))
                return PreviewCsamResult.Match;

            await using Stream video = await objects.OpenReadAsync(previewKey, ct);
            ArachnidScanResult preview = await client.ScanMediaAsync(video, "video/mp4", ct);
            return IsMatch(preview.Classification) ? PreviewCsamResult.Match : PreviewCsamResult.Clean;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The CSAM scan of a marketplace preview did not complete; the preview is refused");
            return PreviewCsamResult.Unavailable;
        }
    }

    private async Task<ArachnidScanResult> ScanPosterAsync(string posterKey, ArachnidOptions o, CancellationToken ct)
    {
        using MemoryStream bytes = new();
        await using (Stream stream = await objects.OpenReadAsync(posterKey, ct))
            await stream.CopyToAsync(bytes, ct);
        bytes.Position = 0;
        if (bytes.Length <= Math.Max(0, o.MaxInlineHashBytes) && hasher.TryHash(bytes, out string hash, out _))
            return await client.ScanPdqAsync(hash, ct);

        bytes.Position = 0;
        return await client.ScanMediaAsync(bytes, "image/jpeg", ct);
    }

    internal static bool IsMatch(string? classification) =>
        string.Equals(classification, "csam", StringComparison.OrdinalIgnoreCase)
        || string.Equals(classification, "harmful-abusive-material", StringComparison.OrdinalIgnoreCase);
}
