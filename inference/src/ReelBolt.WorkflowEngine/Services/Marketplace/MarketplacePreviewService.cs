using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Marketplace;

/// <summary>
/// Cuts one marketplace preview: downloads the (server-resolved) source, refuses what is too long, too big or not a playable
/// video, trims the first 30 s at most 720p (H.264 + AAC, faststart), takes one poster, hashes the result and uploads both under
/// the platform prefix. Never throws: every outcome is a <see cref="MarketplacePreviewFinished"/>.
/// </summary>
public sealed class MarketplacePreviewService(
    IMarketplacePreviewStorage storage,
    IVideoToolRunner runner,
    IOptions<VideoEditingOptions> video,
    IOptions<MarketplacePreviewOptions> options,
    ILogger<MarketplacePreviewService> logger)
{
    public const string ScratchSubdirectory = "marketplace-preview";

    public async Task<MarketplacePreviewFinished> RunAsync(MarketplacePreviewRequested message, CancellationToken ct, Func<bool>? isSuperseded = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!MarketplacePreviewKeys.IsValidNonce(message.RunNonce))
            return Fail(message, MarketplacePreviewFailureCodes.SourceInvalid);
        MarketplacePreviewOptions o = options.Value;
        if (!o.Enabled)
            return Fail(message, MarketplacePreviewFailureCodes.Disabled);

        string? directory = null;
        try
        {
            string root = Path.GetFullPath(video.Value.ScratchPath);
            directory = Path.GetFullPath(Path.Combine(root, ScratchSubdirectory, $"{message.VersionId:N}-{Guid.NewGuid():N}"));
            VideoScratchSpace.ValidateScratchScope(root, directory);
            Directory.CreateDirectory(directory);

            string source = Path.Combine(directory, "source.bin");
            string preview = Path.Combine(directory, MarketplacePreviewKeys.PreviewFileName);
            string poster = Path.Combine(directory, MarketplacePreviewKeys.PosterFileName);

            await storage.DownloadSourceAsync(message.SourceStorageKey, source, o.MaxSourceBytes, ct).ConfigureAwait(false);

            string? refusal = await CheckSourceAsync(source, o, ct).ConfigureAwait(false);
            if (refusal is not null)
                return Fail(message, refusal);

            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(30, o.EncodeTimeoutSeconds));
            VideoToolResult encoded = await runner.RunFfmpegAsync(
                MarketplacePreviewArgvBuilder.BuildPreview(source, preview, o.PreviewSeconds), timeout, ct, VideoToolPriority.Low).ConfigureAwait(false);
            if (!encoded.Succeeded || !File.Exists(preview) || new FileInfo(preview).Length == 0)
            {
                logger.LogWarning("Marketplace preview encode failed for version {VersionId} (exit {ExitCode}, timedOut {TimedOut})",
                    message.VersionId, encoded.ExitCode, encoded.TimedOut);
                return Fail(message, MarketplacePreviewFailureCodes.EncodeFailed);
            }

            MediaProbeResult? cut = await ProbeAsync(preview, ct).ConfigureAwait(false);
            if (cut is null || cut.DurationSec <= 0)
                return Fail(message, MarketplacePreviewFailureCodes.EncodeFailed);

            VideoToolResult shot = await runner.RunFfmpegAsync(
                MarketplacePreviewArgvBuilder.BuildPoster(preview, poster, MarketplacePreviewArgvBuilder.PosterAt(cut.DurationSec)),
                timeout, ct, VideoToolPriority.Low).ConfigureAwait(false);
            if (!shot.Succeeded || !File.Exists(poster) || new FileInfo(poster).Length == 0)
                return Fail(message, MarketplacePreviewFailureCodes.EncodeFailed);

            string sha;
            await using (FileStream stream = File.OpenRead(preview))
                sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            long size = new FileInfo(preview).Length;

            string previewKey = MarketplacePreviewKeys.StagingPreview(message.RecipeId, message.VersionId, message.RunNonce);
            string posterKey = MarketplacePreviewKeys.StagingPoster(message.RecipeId, message.VersionId, message.RunNonce);
            if (isSuperseded?.Invoke() == true)
                return Fail(message, MarketplacePreviewFailureCodes.Superseded);
            try
            {
                await storage.UploadAsync(previewKey, preview, "video/mp4", ct).ConfigureAwait(false);
                await storage.UploadAsync(posterKey, poster, "image/jpeg", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Marketplace preview upload failed for version {VersionId}", message.VersionId);
                return Fail(message, MarketplacePreviewFailureCodes.UploadFailed);
            }

            return new MarketplacePreviewFinished(
                message.VersionId, message.RecipeId, true, null, previewKey, posterKey, sha,
                Math.Round(cut.DurationSec, 3), size, message.RunNonce);
        }
        catch (MarketplaceSourceException ex)
        {
            return Fail(message, ex.Code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Marketplace preview for version {VersionId} failed", message.VersionId);
            return Fail(message, MarketplacePreviewFailureCodes.Internal);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>Null when the source is a playable video within the limits, else a failure code.</summary>
    private async Task<string?> CheckSourceAsync(string path, MarketplacePreviewOptions o, CancellationToken ct)
    {
        VideoToolResult result = await runner.RunFfprobeAsync(
            MediaDerivativeInputFormats.BuildSourceProbeArgs(path), TimeSpan.FromSeconds(60), ct, VideoToolPriority.Low).ConfigureAwait(false);
        if (!result.Succeeded || !FfprobeOutputParser.HasVideoStream(result.StdOut))
            return MarketplacePreviewFailureCodes.NotVideo;

        MediaProbeResult probe = FfprobeOutputParser.Parse(result.StdOut);
        if (probe.Width <= 0 || probe.Height <= 0 || probe.DurationSec <= 0)
            return MarketplacePreviewFailureCodes.NotVideo;
        if (probe.DurationSec > o.MaxSourceSeconds)
            return MarketplacePreviewFailureCodes.SourceTooLong;
        return null;
    }

    private async Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken ct)
    {
        VideoToolResult result = await runner.RunFfprobeAsync(
            MediaDerivativeInputFormats.BuildSourceProbeArgs(path), TimeSpan.FromSeconds(60), ct, VideoToolPriority.Low).ConfigureAwait(false);
        return result.Succeeded && FfprobeOutputParser.HasVideoStream(result.StdOut) ? FfprobeOutputParser.Parse(result.StdOut) : null;
    }

    private static MarketplacePreviewFinished Fail(MarketplacePreviewRequested message, string code) =>
        new(message.VersionId, message.RecipeId, false, code, RunNonce: message.RunNonce);

    private static void TryDelete(string? directory)
    {
        if (directory is null)
            return;
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
