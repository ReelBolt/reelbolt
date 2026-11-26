using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Marketplace;

public enum PreviewModerationOutcome
{
    Allow,
    Review,
    Block,

    /// <summary>Moderation is off, has no provider, or could not judge the file: a human decides, never auto-listed.</summary>
    Unavailable
}

public sealed record PreviewModerationResult(PreviewModerationOutcome Outcome, string? Reason);

public interface IMarketplacePreviewModerator
{
    Task<PreviewModerationResult> CheckAsync(string previewKey, Guid organizationId, CancellationToken ct);
}

/// <summary>
/// The content check of the cut preview, on its own bytes: one downscaled frame per interval (the sampler's limits) and the speech
/// transcript go through <see cref="IContentModerationGate"/>. The audio itself is never sent to the model, only frames and text.
/// Any trouble (disabled, no provider, a file that cannot be sampled or transcribed) is <see cref="PreviewModerationOutcome.Unavailable"/>.
/// </summary>
public sealed class MarketplacePreviewModerator(
    IMarketplacePreviewObjects objects,
    IModerationMediaSampler sampler,
    IContentModerationGate gate,
    IInferenceProviderResolver providers,
    ITranscriptionClientFactory transcription,
    IOptions<ContentModerationOptions> options,
    ILogger<MarketplacePreviewModerator> logger,
    ITenantContext? tenant = null) : IMarketplacePreviewModerator
{
    public async Task<PreviewModerationResult> CheckAsync(string previewKey, Guid organizationId, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return new(PreviewModerationOutcome.Unavailable, "moderation_disabled");

        try
        {
            ModerationMediaSample sample;
            await using (Stream media = await objects.OpenReadAsync(previewKey, ct))
                sample = await sampler.SampleAsync(media, "video/mp4", ct);
            if (sample.Failure is not null)
                return new(PreviewModerationOutcome.Unavailable, "not_sampled");

            string? transcript = null;
            if (sample.Audio is { Length: > 0 })
            {
                ResolvedTranscriptionProvider? provider = await providers.ResolvePlatformTranscriptionAsync(ct);
                if (provider is null)
                {
                    if (!options.Value.AllowFramesOnlyWithoutTranscription)
                        return new(PreviewModerationOutcome.Unavailable, "no_transcription");
                }
                else
                {
                    transcript = await TranscribeAsync(provider, sample.Audio, organizationId, ct);
                    if (transcript is null)
                        return new(PreviewModerationOutcome.Unavailable, "transcription_failed");
                }
            }

            transcript = ModerationMediaSample.WithSubtitles(transcript, sample.SubtitleText);
            List<ModerationImageInput> images = [.. sample.Images.Select(b => new ModerationImageInput("image/jpeg", b))];
            if (images.Count == 0 && string.IsNullOrWhiteSpace(transcript))
                return new(PreviewModerationOutcome.Unavailable, "nothing_to_check");

            ModerationDecision decision;
            using (tenant.System())
            {
                decision = await gate.ModerateAsync(
                    new ModerationInput(transcript, images, ModerationContentKind.Video),
                    new ModerationAttribution(organizationId, null, null, null, null, ModerationSurface.Publish, ModerationContentKind.Video),
                    ct);
            }

            return decision.Action switch
            {
                ModerationAction.Block => new(PreviewModerationOutcome.Block, decision.ReasonCode ?? "blocked"),
                ModerationAction.Unavailable => new(PreviewModerationOutcome.Unavailable, "unavailable"),
                _ when !decision.Available => new(PreviewModerationOutcome.Unavailable, "unavailable"),
                ModerationAction.Review => new(PreviewModerationOutcome.Review, decision.ReasonCode),
                ModerationAction.Allow => new(PreviewModerationOutcome.Allow, null),
                _ => new(PreviewModerationOutcome.Unavailable, "unavailable")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The content check of a marketplace preview failed; treating it as unavailable");
            return new(PreviewModerationOutcome.Unavailable, "error");
        }
    }

    private async Task<string?> TranscribeAsync(ResolvedTranscriptionProvider provider, byte[] audio, Guid organizationId, CancellationToken ct)
    {
        // The publisher's organization, under Moderation (uncharged); an outer scope wins.
        using IDisposable usageScope = UsageScope.BeginIfNone(
            organizationId == Guid.Empty ? null : organizationId, null, null, UsageSource.Moderation);

        try
        {
            ITranscriptionClient client = transcription.Get(provider);
            TranscriptResult result = await client.TranscribeAsync(new MemoryStream(audio, writable: false), "audio.wav", language: null, wordTimestamps: false, ct);
            return result.Text ?? string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transcription failed while checking a marketplace preview");
            return null;
        }
    }
}
