namespace ReelBolt.Shared.Inference;

/// <summary>
/// Client for interacting with an external video generation provider.
/// </summary>
public interface IVideoGenerationClient
{
    /// <summary>
    /// Submits a video generation request and returns a handle to the job.
    /// </summary>
    Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct);

    /// <summary>
    /// Polls the status of an external video generation job.
    /// </summary>
    Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct);

    /// <summary>
    /// No-cost provider test: queries a random, nonexistent task id and never creates a task.
    /// Reports ok only on a response that proves the endpoint is the vendor's query API and the key
    /// was accepted; network errors, timeouts, 5xx, 408/429 and unrecognised bodies are not ok.
    /// Bounds itself with its own timeout; throws only if <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// Downloads the generated video from the given URL.
    /// </summary>
    Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct);

    /// <summary>
    /// Describes the capabilities offered by a specific model.
    /// </summary>
    VideoGenerationCapabilities Describe(string model);
}

/// <summary>
/// Factory for creating <see cref="IVideoGenerationClient"/> instances.
/// </summary>
public interface IVideoGenerationClientFactory
{
    /// <summary>
    /// Gets or creates a client configured with the given provider settings.
    /// </summary>
    IVideoGenerationClient Get(ResolvedVideoGenerationProvider provider);
}
