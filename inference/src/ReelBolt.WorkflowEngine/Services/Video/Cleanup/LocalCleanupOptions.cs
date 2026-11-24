namespace ReelBolt.WorkflowEngine.Services.Video.Cleanup;

/// <summary>Bound from <c>Cleanup:Local</c>. Operator configuration only; never from a request.</summary>
public sealed class LocalCleanupOptions
{
    public const string SectionName = "Cleanup:Local";

    /// <summary>The vendored model's path in the image (see the Dockerfile and inference/assets/rnnoise).</summary>
    public const string DefaultRnnoiseModelPath = "/app/assets/rnnoise/sh.rnnn";

    /// <summary>Pinned SHA-256 of the vendored sh.rnnn.</summary>
    public const string RnnoiseModelSha256 = "70bb6685eb0c2a1d18e2918dca3fbfbd39317010b1802eb1b6ea73a92f3fdec0";

    public string RnnoiseModelPath { get; set; } = DefaultRnnoiseModelPath;
}
